using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Round-cropped copy of a picture, for places that can't clip (the NavigationView's
/// ImageIcon on the rail).
/// </summary>
public static class CircleImage
{
    public static async Task<ImageSource?> CreateAsync(string path, int size = 72)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)size,
                ScaledHeight = (uint)size,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();

            // Anti-aliased circular mask; premultiplied, so every channel is scaled.
            var r = size / 2.0;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5 - r;
                var dy = y + 0.5 - r;
                var alpha = Math.Clamp(r - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                if (alpha >= 1) continue;
                var i = (y * size + x) * 4;
                for (var c = 0; c < 4; c++) pixels[i + c] = (byte)(pixels[i + c] * alpha);
            }

            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, size, size, BitmapAlphaMode.Premultiplied);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return source;
        }
        catch (Exception)
        {
            return null;   // missing or unreadable file: keep the placeholder
        }
    }
}
