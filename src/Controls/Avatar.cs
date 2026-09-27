using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace WhatsAppNative.Controls;

/// <summary>
/// Circular avatar: initials on a colour picked from the name, optionally inside a green
/// "unseen status" ring. A photo can be layered on later.
/// </summary>
public sealed partial class Avatar : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0xC2, 0x39, 0xB3), Color.FromArgb(255, 0x00, 0x78, 0xD4),
        Color.FromArgb(255, 0x03, 0x83, 0x87), Color.FromArgb(255, 0xCA, 0x50, 0x10),
        Color.FromArgb(255, 0x87, 0x64, 0xB8), Color.FromArgb(255, 0x49, 0x82, 0x05),
        Color.FromArgb(255, 0xE3, 0x00, 0x8C), Color.FromArgb(255, 0x4F, 0x6B, 0xED),
        Color.FromArgb(255, 0x98, 0x6F, 0x0B), Color.FromArgb(255, 0x00, 0x7C, 0x9A),
    ];

    private static readonly SolidColorBrush RingBrush = new(Color.FromArgb(255, 0x1D, 0xAA, 0x61));

    private readonly Ellipse _ring = new() { Stroke = RingBrush, StrokeThickness = 2 };
    private readonly Ellipse _circle = new();
    private readonly TextBlock _initials = new()
    {
        Foreground = new SolidColorBrush(Colors.White),
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(Avatar), new PropertyMetadata("", (d, _) => ((Avatar)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(48.0, (d, _) => ((Avatar)d).Update()));

    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Update()));

    /// <summary>Profile picture file; the initials show until it loads (or if it can't).</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).UpdatePhoto()));

    public string DisplayName { get => (string)GetValue(DisplayNameProperty); set => SetValue(DisplayNameProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool ShowRing { get => (bool)GetValue(ShowRingProperty); set => SetValue(ShowRingProperty, value); }
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    private readonly Ellipse _photo = new() { Visibility = Visibility.Collapsed };

    public Avatar()
    {
        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(_circle);
        root.Children.Add(_initials);
        root.Children.Add(_photo);
        Content = root;
        IsTabStop = false;
        Update();
    }

    private double Inner => ShowRing ? Size - 8 : Size;   // with a ring, the picture shrinks to leave a gap

    private void Update()
    {
        Width = Height = Size;
        _ring.Width = _ring.Height = Size;
        _ring.Visibility = ShowRing ? Visibility.Visible : Visibility.Collapsed;
        _circle.Width = _circle.Height = _photo.Width = _photo.Height = Inner;
        _initials.FontSize = Math.Round(Inner * 0.36);
        _initials.Text = Initials(DisplayName ?? "");
        _circle.Fill = new SolidColorBrush(Palette[StableHash(DisplayName ?? "") % Palette.Length]);
        UpdatePhoto();
    }

    private void UpdatePhoto()
    {
        if (string.IsNullOrEmpty(Source) || !File.Exists(Source))
        {
            _photo.Visibility = Visibility.Collapsed;
            _photo.Fill = null;
            return;
        }
        // Decode near display size (x2.5 covers 250% scaling) instead of the full 640 px.
        var image = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(Inner * 2.5), UriSource = new Uri(Source) };
        image.ImageFailed += (_, _) => _photo.Visibility = Visibility.Collapsed;
        _photo.Fill = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
        _photo.Visibility = Visibility.Visible;
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    // string.GetHashCode is randomised per process; colours must stay the same between runs.
    private static int StableHash(string s)
    {
        int h = 17;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return (h & 0x7FFFFFFF);
    }
}
