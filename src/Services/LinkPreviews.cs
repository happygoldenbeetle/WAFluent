using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;

namespace WhatsAppNative.Services;

/// <summary>
/// Link cards for links you send, like WhatsApp and Discord make them: the page's title,
/// description and picture from its Open Graph / Twitter tags (else its &lt;title&gt;), fetched
/// once per link and kept for the session. The picture becomes a small JPEG, as WhatsApp sends.
/// </summary>
public static partial class LinkPreviews
{
    /// <summary>A card: the link, what the page says about itself, and its picture (a local JPEG).</summary>
    public sealed record Card(string Url, string Title, string Description, string? Thumb, string Site);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, Task<Card?>> Cache = [];
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "WAFluent", "links");

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]*[^\s<>"".,;:!?)\]']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"(\w[\w:-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>The first link in a message's text ("www." gets https://).</summary>
    public static string? FirstLink(string text)
    {
        var m = LinkPattern().Match(text);
        if (!m.Success) return null;
        return m.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + m.Value : m.Value;
    }

    /// <summary>The card for a link (null when the page has nothing to show or can't be reached).</summary>
    public static Task<Card?> GetAsync(string url)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(url, out var task)) Cache[url] = task = FetchAsync(url);
            return task;
        }
    }

    private static async Task<Card?> FetchAsync(string url)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var final = response.RequestMessage?.RequestUri ?? new Uri(url);
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                // A link straight to a picture: the picture is the card.
                var bytes = await response.Content.ReadAsByteArrayAsync(cancel.Token);
                return new Card(url, "", "", await ThumbAsync(bytes), final.Host);
            }
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            // The head is enough: read at most 512 KB.
            await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
            var buffer = new byte[512 * 1024];
            int read = 0, n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancel.Token)) > 0) read += n;
            var html = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match tag in MetaTag().Matches(html))
            {
                string? key = null, value = null;
                foreach (Match a in Attribute().Matches(tag.Value))
                {
                    var name = a.Groups[1].Value.ToLowerInvariant();
                    var v = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                    if (name is "property" or "name") key = v;
                    else if (name == "content") value = v;
                }
                if (key is not null && value is not null) meta.TryAdd(key, WebUtility.HtmlDecode(value).Trim());
            }
            string Pick(params string[] keys) => keys.Select(k => meta.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

            var title = Pick("og:title", "twitter:title");
            if (title.Length == 0 && TitleTag().Match(html) is { Success: true } t) title = WebUtility.HtmlDecode(t.Groups[1].Value).Trim();
            var description = Pick("og:description", "twitter:description", "description");
            var site = Pick("og:site_name");
            if (site.Length == 0) site = final.Host.StartsWith("www.") ? final.Host[4..] : final.Host;
            var image = Pick("og:image:secure_url", "og:image", "twitter:image", "twitter:image:src");
            if (title.Length == 0 && image.Length == 0) return null;

            string? thumb = null;
            if (image.Length > 0 && Uri.TryCreate(final, image, out var imageUri))
            {
                try { thumb = await ThumbAsync(await Http.GetByteArrayAsync(imageUri)); }
                catch (Exception e) { Helpers.AppLog.Write($"link picture {imageUri} failed", e); }   // a card without a picture
            }
            return new Card(url, Shorten(title, 120), Shorten(description, 200), thumb, site);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>The page's picture as a JPEG of at most 300 px (WhatsApp's link thumbnails are small).</summary>
    private static async Task<string?> ThumbAsync(byte[] image)
    {
        using var input = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var scale = Math.Min(1, 300.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                                                     ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".jpg");
        using (var output = File.Create(path).AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
        }
        return path;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        // Many sites only give their card to browsers and link-preview bots.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WAFluent/1.0; +https://github.com/happygoldenbeetle/WAFluent) facebookexternalhit/1.1");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return http;
    }
}
