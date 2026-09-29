using System.Net;
using System.Net.Http;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace WhatsAppNative.Services;

/// <summary>
/// GIF search for the sticker panel, through GIPHY (Tenor's public API closed in June 2026).
/// The app's key is built in from src\giphy.key (not in git); ui.json's GiphyKey overrides
/// it. A picked GIF is fetched as MP4, the way WhatsApp sends GIFs, with a small JPEG
/// preview for the chat bubble. Results are cached briefly: every copy of the app shares
/// the key's request allowance.
/// </summary>
public static class Giphy
{
    /// <summary>A search result: an animated preview for the grid, an MP4 to send.</summary>
    public sealed record Gif(string Id, string Title, string Preview, string Mp4, string Still, int Width, int Height);

    /// <summary>The key was refused (wrong, or revoked).</summary>
    public sealed class KeyRefusedException() : Exception("GIF search isn't available right now.");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    /// <summary>The key built into this copy of the app (empty when built without src\giphy.key).</summary>
    public static readonly string BuiltInKey = ReadBuiltInKey();

    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<Gif> Gifs)> Cache = [];
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    private static string ReadBuiltInKey()
    {
        using var stream = typeof(Giphy).Assembly.GetManifestResourceStream("giphy.key");
        return stream is null ? "" : new StreamReader(stream).ReadToEnd().Trim();
    }

    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent", "gifs");

    /// <summary>Trending GIFs (empty query) or the ones matching <paramref name="query"/>.</summary>
    public static async Task<IReadOnlyList<Gif>> SearchAsync(string key, string query, CancellationToken cancel)
    {
        var cacheKey = query.Trim().ToLowerInvariant();
        if (Cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Gifs;
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
        Cache[cacheKey] = (DateTime.UtcNow, gifs);
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
