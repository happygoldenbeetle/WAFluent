using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Photos and videos are made smaller before sending, as WhatsApp does: standard quality
/// (the default) or HD. Media no bigger than standard goes as it is, and can't be sent "HD"
/// (there's nothing more to keep), like on the phone. Results are kept under
/// %LOCALAPPDATA%\WAFluent\media\sent so the sent bubble keeps showing them.
/// </summary>
public static class MediaCompression
{
    /// <summary>Longest side of a photo: standard / HD.</summary>
    public const int PhotoSd = 1600, PhotoHd = 4096;
    /// <summary>Shortest side of a video: standard (480p) / HD (720p).</summary>
    public const int VideoSd = 480, VideoHd = 720;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent", "media", "sent");

    /// <summary>Whether HD would keep more than standard (the photo or video is bigger than SD).</summary>
    public static bool SupportsHd(string kind, int width, int height) => kind switch
    {
        "image" => Math.Max(width, height) > PhotoSd,
        "video" => Math.Min(width, height) > VideoSd,
        _ => false,
    };

    /// <summary>A JPEG no bigger than the quality's limit (quality 80 SD, 92 HD); transparency on white.</summary>
    public static async Task<(string Path, int Width, int Height)> PhotoAsync(string path, bool hd)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        int width = (int)decoder.OrientedPixelWidth, height = (int)decoder.OrientedPixelHeight;
        var limit = hd ? PhotoHd : PhotoSd;
        var scale = Math.Min(1.0, (double)limit / Math.Max(width, height));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        // The transform scales before orienting: give it the stored (unrotated) size.
        var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
        transform.ScaledWidth = (uint)Math.Max(1, Math.Round((rotated ? height : width) * scale));
        transform.ScaledHeight = (uint)Math.Max(1, Math.Round((rotated ? width : height) * scale));
        // The bitmap reports its real size after scaling and turning (EXIF orientation).
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                                                                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var outWidth = (uint)bitmap.PixelWidth;
        var outHeight = (uint)bitmap.PixelHeight;
        var pixels = new byte[outWidth * outHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        for (var i = 0; i < pixels.Length; i += 4)   // premultiplied over white
        {
            var alpha = pixels[i + 3];
            if (alpha == 255) continue;
            var white = (byte)(255 - alpha);
            pixels[i] += white;
            pixels[i + 1] += white;
            pixels[i + 2] += white;
            pixels[i + 3] = 255;
        }

        Directory.CreateDirectory(Folder);
        var output = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".jpg");
        using (var stream = File.Create(output).AsRandomAccessStream())
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(hd ? 0.92f : 0.8f, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, outWidth, outHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        return (output, (int)outWidth, (int)outHeight);
    }

    /// <summary>
    /// An H.264/AAC MP4 at 480p (SD, ~1.5 Mbit/s) or 720p (HD, ~3.5 Mbit/s), aspect kept.
    /// Videos already that small go as they are.
    /// </summary>
    public static async Task<(string Path, int Width, int Height)> VideoAsync(string path, int width, int height, bool hd, CancellationToken cancel)
    {
        var target = hd ? VideoHd : VideoSd;
        var shortSide = Math.Min(width, height);
        if (shortSide <= target && Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return (path, width, height);

        var scale = shortSide > target ? (double)target / shortSide : 1.0;
        var outWidth = (uint)(Math.Round(width * scale / 2) * 2);   // encoders want even sizes
        var outHeight = (uint)(Math.Round(height * scale / 2) * 2);
        var profile = MediaEncodingProfile.CreateMp4(hd ? VideoEncodingQuality.HD720p : VideoEncodingQuality.Wvga);
        profile.Video!.Width = outWidth;
        profile.Video.Height = outHeight;
        profile.Video.Bitrate = hd ? 3_500_000u : 1_500_000u;
        profile.Audio!.Bitrate = 128_000;

        Directory.CreateDirectory(Folder);
        var source = await StorageFile.GetFileFromPathAsync(path);
        var folder = await StorageFolder.GetFolderFromPathAsync(Folder);
        var destination = await folder.CreateFileAsync(Guid.NewGuid().ToString("N") + ".mp4");
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
        if (!prepared.CanTranscode)
        {
            await destination.DeleteAsync();
            return (path, width, height);
        }
        try
        {
            await prepared.TranscodeAsync().AsTask(cancel);
        }
        catch
        {
            await destination.DeleteAsync();
            throw;
        }
        return (destination.Path, (int)outWidth, (int)outHeight);
    }
}
