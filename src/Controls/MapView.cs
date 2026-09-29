using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using WhatsAppNative.Helpers;

namespace WhatsAppNative.Controls;

/// <summary>
/// A sharp street map around a shared location. The sender's phone only attaches a tiny
/// snapshot, so this stitches OpenStreetMap tiles (zoom 17, twice the pixels of the box) into
/// one picture, cached per place in %LOCALAPPDATA%\WAFluent\maps (tiles in \tiles, as OSM's
/// tile policy asks). The snapshot shows until the map is ready, or for good when offline.
/// </summary>
public sealed partial class MapView : Grid
{
    private const int Zoom = 17, Tile = 256, Width2x = 600, Height2x = 300;
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent");
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Downloads = new(2);   // gentle on the tile servers

    public static readonly DependencyProperty LatitudeProperty = DependencyProperty.Register(
        nameof(Latitude), typeof(double), typeof(MapView), new PropertyMetadata(0.0, (d, _) => ((MapView)d).Schedule()));
    public static readonly DependencyProperty LongitudeProperty = DependencyProperty.Register(
        nameof(Longitude), typeof(double), typeof(MapView), new PropertyMetadata(0.0, (d, _) => ((MapView)d).Schedule()));
    public static readonly DependencyProperty ThumbProperty = DependencyProperty.Register(
        nameof(Thumb), typeof(string), typeof(MapView), new PropertyMetadata(null, (d, _) => ((MapView)d).Schedule()));

    public double Latitude { get => (double)GetValue(LatitudeProperty); set => SetValue(LatitudeProperty, value); }
    public double Longitude { get => (double)GetValue(LongitudeProperty); set => SetValue(LongitudeProperty, value); }
    public string? Thumb { get => (string?)GetValue(ThumbProperty); set => SetValue(ThumbProperty, value); }

    private readonly ImageBrush _brush = new() { Stretch = Stretch.UniformToFill };
    private readonly Grid _pin;
    private int _version;

    public MapView()
    {
        Background = _brush;
        // WhatsApp's red pin, its needle tip on the place (the snapshot has its own pin).
        _pin = new Grid { Width = 24, Height = 38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                          Margin = new Thickness(0, 0, 0, 38), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _pin.Children.Add(Shape("M11,17 H13 V37 Q12,38.5 11,37 Z", 0xFF5F6368));
        _pin.Children.Add(Shape("M12,1 A9,9 0 1 1 11.99,1 Z", 0xFFE53935));
        _pin.Children.Add(Shape("M8.5,6.5 A3.2,3.2 0 1 1 8.49,6.5 Z", 0x66FFFFFF));
        Children.Add(_pin);
    }

    private static Microsoft.UI.Xaml.Shapes.Path Shape(string data, uint argb) => new()
    {
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
    };

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // OSM's tile policy: say who's asking.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WAFluent/1.0 (+https://github.com/happygoldenbeetle/WAFluent)");
        return http;
    }

    private bool _scheduled;

    /// <summary>Latitude, longitude and preview arrive one by one: draw once, after all three.</summary>
    private void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _scheduled = false;
            Refresh();
        });
    }

    private async void Refresh()
    {
        var version = ++_version;
        _brush.ImageSource = Ui.Thumb(Thumb);
        _pin.Visibility = Visibility.Collapsed;
        var (lat, lng) = (Latitude, Longitude);
        if (lat == 0 && lng == 0) return;
        try
        {
            var path = await MapFor(lat, lng);
            if (version != _version || path is null) return;   // recycled for another message meanwhile
            _brush.ImageSource = Ui.Image(path);
            _pin.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // Offline or blocked: the snapshot stays.
        }
    }

    /// <summary>The stitched map for a place (from the cache when it's been drawn before).</summary>
    private static async Task<string?> MapFor(double lat, double lng)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000}_{lng:0.00000}_{Zoom}.png");
        var file = Path.Combine(Root, "maps", name);
        if (File.Exists(file)) return file;

        // World pixel position of the place at this zoom (Web Mercator).
        var scale = Tile * Math.Pow(2, Zoom);
        var px = (lng + 180) / 360 * scale;
        var sin = Math.Sin(lat * Math.PI / 180);
        var py = (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * scale;
        var left = (int)Math.Round(px - Width2x / 2.0);
        var top = (int)Math.Round(py - Height2x / 2.0);

        var canvas = new byte[Width2x * Height2x * 4];
        for (var ty = top / Tile; ty * Tile < top + Height2x; ty++)
            for (var tx = left / Tile; tx * Tile < left + Width2x; tx++)
            {
                var tile = await TileAsync(tx, ty);
                if (tile is null) return null;
                Blit(tile, tx * Tile - left, ty * Tile - top, canvas);
            }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var stream = File.Create(file).AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, Width2x, Height2x, 96, 96, canvas);
            await encoder.FlushAsync();
        }
        return file;
    }

    /// <summary>One 256 px tile as BGRA pixels, downloaded once.</summary>
    private static async Task<byte[]?> TileAsync(int x, int y)
    {
        var file = Path.Combine(Root, "tiles", Zoom.ToString(), x.ToString(), $"{y}.png");
        if (!File.Exists(file))
        {
            await Downloads.WaitAsync();
            try
            {
                if (!File.Exists(file))
                {
                    var bytes = await Http.GetByteArrayAsync($"https://tile.openstreetmap.org/{Zoom}/{x}/{y}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, bytes);
                }
            }
            finally
            {
                Downloads.Release();
            }
        }
        using var stream = File.OpenRead(file).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                                                   ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return decoder.PixelWidth == Tile && decoder.PixelHeight == Tile ? data.DetachPixelData() : null;
    }

    private static void Blit(byte[] tile, int dx, int dy, byte[] canvas)
    {
        for (var y = 0; y < Tile; y++)
        {
            var cy = dy + y;
            if (cy < 0 || cy >= Height2x) continue;
            var x0 = Math.Max(0, -dx);
            var x1 = Math.Min(Tile, Width2x - dx);
            if (x1 <= x0) continue;
            Buffer.BlockCopy(tile, (y * Tile + x0) * 4, canvas, (cy * Width2x + dx + x0) * 4, (x1 - x0) * 4);
        }
    }
}
