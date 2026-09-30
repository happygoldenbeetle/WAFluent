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
    /// <summary>
    /// A GIF as WhatsApp sends one: a short silent H.264 MP4 (the chat loops it). Frames are
    /// put together the way a GIF viewer does (offsets, transparency, clearing), on white.
    /// Returns the MP4, its size and length.
    /// </summary>
    public static async Task<(string Path, int Width, int Height, int Seconds)> GifAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, input);
        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
        try
        {
            var screen = await decoder.BitmapContainerProperties.GetPropertiesAsync(["/logscrdesc/Width", "/logscrdesc/Height"]);
            if (screen.TryGetValue("/logscrdesc/Width", out var w) && screen.TryGetValue("/logscrdesc/Height", out var h))
                (width, height) = (Convert.ToInt32(w.Value), Convert.ToInt32(h.Value));
        }
        catch (Exception) { /* no screen size: the first frame's */ }
        // H.264 wants even sizes; the extra row/column stays white.
        int outWidth = width + width % 2, outHeight = height + height % 2;

        var canvas = new byte[width * height * 4];
        var frames = new List<(byte[] Pixels, TimeSpan Duration)>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            int left = 0, top = 0, delay = 10, disposal = 0;
            try
            {
                var props = await frame.BitmapProperties.GetPropertiesAsync(["/imgdesc/Left", "/imgdesc/Top", "/grctlext/Delay", "/grctlext/Disposal"]);
                if (props.TryGetValue("/imgdesc/Left", out var l)) left = Convert.ToInt32(l.Value);
                if (props.TryGetValue("/imgdesc/Top", out var t)) top = Convert.ToInt32(t.Value);
                if (props.TryGetValue("/grctlext/Delay", out var d)) delay = Convert.ToInt32(d.Value);
                if (props.TryGetValue("/grctlext/Disposal", out var x)) disposal = Convert.ToInt32(x.Value);
            }
            catch (Exception) { }
            if (delay < 2) delay = 10;   // browsers show "0" and "1" as 100 ms too

            var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                                                     ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            int fw = (int)frame.PixelWidth, fh = (int)frame.PixelHeight;
            var before = disposal == 3 ? (byte[])canvas.Clone() : null;
            for (var y = 0; y < fh; y++)
            {
                var cy = top + y;
                if (cy < 0 || cy >= height) continue;
                for (var x = 0; x < fw; x++)
                {
                    var cx = left + x;
                    if (cx < 0 || cx >= width) continue;
                    var src = (y * fw + x) * 4;
                    if (pixels[src + 3] < 128) continue;   // GIF transparency is on/off
                    var dst = (cy * width + cx) * 4;
                    canvas[dst] = pixels[src];
                    canvas[dst + 1] = pixels[src + 1];
                    canvas[dst + 2] = pixels[src + 2];
                    canvas[dst + 3] = 255;
                }
            }

            // The shown frame: the canvas on white, padded to even size, bottom row first
            // (uncompressed RGB frames are read bottom-up by the encoder).
            var shown = new byte[outWidth * outHeight * 4];
            Array.Fill(shown, (byte)255);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var src = (y * width + x) * 4;
                    if (canvas[src + 3] == 0) continue;
                    var dst = ((outHeight - 1 - y) * outWidth + x) * 4;
                    shown[dst] = canvas[src];
                    shown[dst + 1] = canvas[src + 1];
                    shown[dst + 2] = canvas[src + 2];
                }
            frames.Add((shown, TimeSpan.FromMilliseconds(delay * 10)));

            if (disposal == 2)   // clear this frame's area
            {
                int x0 = Math.Max(0, left), x1 = Math.Min(width, left + fw);
                if (x1 > x0)
                    for (var y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
                        Array.Clear(canvas, (y * width + x0) * 4, (x1 - x0) * 4);
            }
            else if (before is not null)
            {
                canvas = before;
            }
        }
        if (frames.Count == 0) throw new InvalidOperationException("no frames");
        // Very short GIFs play at least a second, repeated, so every player shows them.
        var total = frames.Aggregate(TimeSpan.Zero, (a, f) => a + f.Duration);
        var loops = Math.Max(1, (int)Math.Ceiling(1.0 / Math.Max(0.05, total.TotalSeconds)));
        var sequence = Enumerable.Repeat(frames, loops).SelectMany(f => f).ToList();

        var descriptor = new Windows.Media.Core.VideoStreamDescriptor(
            VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)outWidth, (uint)outHeight));
        var source = new Windows.Media.Core.MediaStreamSource(descriptor) { BufferTime = TimeSpan.Zero };
        var index = 0;
        var at = TimeSpan.Zero;
        source.SampleRequested += (_, args) =>
        {
            if (index >= sequence.Count)
            {
                args.Request.Sample = null;   // end of stream
                return;
            }
            var (pixels, duration) = sequence[index++];
            var sample = Windows.Media.Core.MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), at);
            sample.Duration = duration;
            sample.KeyFrame = index == 1;
            at += duration;
            args.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        profile.Audio = null;
        profile.Video!.Width = (uint)outWidth;
        profile.Video.Height = (uint)outHeight;
        profile.Video.Bitrate = 1_200_000;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;

        Directory.CreateDirectory(Folder);
        var output = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".mp4");
        using (var stream = File.Create(output).AsRandomAccessStream())
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prepared.CanTranscode) throw new InvalidOperationException($"can't encode the GIF: {prepared.FailureReason}");
            await prepared.TranscodeAsync();
        }
        return (output, outWidth, outHeight, Math.Max(1, (int)Math.Round(at.TotalSeconds)));
    }
}
