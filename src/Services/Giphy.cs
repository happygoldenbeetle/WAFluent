using System.Net;
using System.Net.Http;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace WhatsAppNative.Services;

/// <summary>
/// GIF search for the sticker panel, through GIPHY (Tenor's public API closed in June 2026).
/// Needs the user's own free key (Settings › GIF search). A picked GIF is fetched as MP4,
/// the way WhatsApp sends GIFs, with a small JPEG preview for the chat bubble.
/// </summary>
public static class Giphy
{
    /// <summary>A search result: an animated preview for the grid, an MP4 to send.</summary>
    public sealed record Gif(string Id, string Title, string Preview, string Mp4, string Still, int Width, int Height);

    /// <summary>The key was refused (wrong, or revoked).</summary>
    public sealed class KeyRefusedException() : Exception("GIPHY didn't accept the key.");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent", "gifs");

    /// <summary>Trending GIFs (empty query) or the ones matching <paramref name="query"/>.</summary>
    public static async Task<IReadOnlyList<Gif>> SearchAsync(string key, string query, CancellationToken cancel)
    {
        var url = string.IsNullOrWhiteSpace(query)
            ? $"https://api.giphy.com/v1/gifs/trending?api_key={Uri.EscapeDataString(key)}&limit=30&rating=pg-13"
            : $"https://api.giphy.com/v1/gifs/search?api_key={Uri.EscapeDataString(key)}&q={Uri.EscapeDataString(query.Trim())}&limit=30&rating=pg-13";
        using var response = await Http.GetAsync(url, cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new KeyRefusedException();
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
        var gifs = new List<Gif>();
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!item.TryGetProperty("images", out var images)) continue;
            var preview = Str(images, "fixed_width_small", "url") ?? Str(images, "fixed_width", "url");
            var mp4 = Str(images, "original", "mp4") ?? Str(images, "fixed_width", "mp4");
            var still = Str(images, "fixed_width_still", "url") ?? Str(images, "original_still", "url");
            if (preview is null || mp4 is null || still is null) continue;
            gifs.Add(new Gif(
                item.GetProperty("id").GetString() ?? "",
                item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                preview, mp4, still,
                Int(images, "original", "width"), Int(images, "original", "height")));
        }
        return gifs;
    }

    /// <summary>Downloads the MP4 and makes the JPEG preview (both kept for resending).</summary>
    public static async Task<(string Mp4, string? Thumb)> DownloadAsync(Gif gif)
    {
        Directory.CreateDirectory(Root);
        var safe = string.Concat(gif.Id.Where(char.IsLetterOrDigit));
        var mp4 = Path.Combine(Root, safe + ".mp4");
        if (!File.Exists(mp4))
        {
            var bytes = await Http.GetByteArrayAsync(gif.Mp4);
            await File.WriteAllBytesAsync(mp4, bytes);
        }
        var thumb = Path.Combine(Root, safe + ".jpg");
        if (!File.Exists(thumb))
        {
            try
            {
                var still = await Http.GetByteArrayAsync(gif.Still);
                await Jpeg(still, thumb);
            }
            catch (Exception)
            {
                return (mp4, null);   // the bubble fills in once the phone downloads it
            }
        }
        return (mp4, thumb);
    }

    /// <summary>The first frame of a still (GIF) as a JPEG of at most 100 px, like WhatsApp's previews.</summary>
    private static async Task Jpeg(byte[] image, string file)
    {
        using var input = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var scale = Math.Min(1, 100.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                                                     ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        using var output = File.Create(file).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
    }

    private static string? Str(JsonElement images, string rendition, string field) =>
        images.TryGetProperty(rendition, out var r) && r.TryGetProperty(field, out var v) && v.GetString() is { Length: > 0 } s ? s : null;

    private static int Int(JsonElement images, string rendition, string field) =>
        images.TryGetProperty(rendition, out var r) && r.TryGetProperty(field, out var v) && int.TryParse(v.GetString(), out var n) ? n : 0;
}
