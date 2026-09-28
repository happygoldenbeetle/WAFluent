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
/// Circular avatar, optionally inside a green "unseen status" ring. Without a photo, people
/// get WhatsApp's default picture (a head-and-shoulders figure on a tinted circle, hue picked
/// from the name) and groups get their initials.
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



    private readonly Ellipse _ring = new() { Stroke = Helpers.AppColors.StatusRing, StrokeThickness = 2 };
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

    /// <summary>Groups show initials instead of the person figure when they have no photo.</summary>
    public static readonly DependencyProperty IsGroupProperty = DependencyProperty.Register(
        nameof(IsGroup), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Update()));

    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Update()));

    /// <summary>Profile picture file; the initials show until it loads (or if it can't).</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).UpdatePhoto()));

    public string DisplayName { get => (string)GetValue(DisplayNameProperty); set => SetValue(DisplayNameProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool ShowRing { get => (bool)GetValue(ShowRingProperty); set => SetValue(ShowRingProperty, value); }
    public bool IsGroup { get => (bool)GetValue(IsGroupProperty); set => SetValue(IsGroupProperty, value); }
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    private readonly Ellipse _photo = new() { Visibility = Visibility.Collapsed };

    // WhatsApp's default picture, measured from the app: in a 100-unit circle the head is a
    // circle (r 7.9) centred 8.5 above the middle, the shoulders a dome 30.5 wide from 52.8 to 65.8.
    private const string FigureData =
        "M50,33.6 A7.9,7.9 0 1 1 49.99,33.6 Z " +
        "M34.75,62 A15.25,9.2 0 0 1 65.25,62 L65.25,63.3 Q65.25,65.8 62.75,65.8 L37.25,65.8 Q34.75,65.8 34.75,63.3 Z";

    private readonly Microsoft.UI.Xaml.Shapes.Path _figure = new();
    private readonly Viewbox _figureBox;

    public Avatar()
    {
        _figure.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), FigureData);
        _figureBox = new Viewbox { Child = new Grid { Width = 100, Height = 100, Children = { _figure } } };

        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(_circle);
        root.Children.Add(_initials);
        root.Children.Add(_figureBox);
        root.Children.Add(_photo);
        Content = root;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    private double Inner => ShowRing ? Size - 8 : Size;   // with a ring, the picture shrinks to leave a gap

    private void Update()
    {
        Width = Height = Size;
        _ring.Width = _ring.Height = Size;
        _ring.Visibility = ShowRing ? Visibility.Visible : Visibility.Collapsed;
        _circle.Width = _circle.Height = _photo.Width = _photo.Height = _figureBox.Width = _figureBox.Height = Inner;
        var name = DisplayName ?? "";
        if (IsGroup)
        {
            _initials.FontSize = Math.Round(Inner * 0.36);
            _initials.Text = Initials(name);
            _initials.Visibility = Visibility.Visible;
            _figureBox.Visibility = Visibility.Collapsed;
            _circle.Fill = new SolidColorBrush(Palette[StableHash(name) % Palette.Length]);
            _circle.Stroke = null;
        }
        else
        {
            var (back, figure) = PersonColors(Hues[StableHash(name) % Hues.Length], ActualTheme == ElementTheme.Light);
            _initials.Visibility = Visibility.Collapsed;
            _figureBox.Visibility = Visibility.Visible;
            _circle.Fill = new SolidColorBrush(back);
            _circle.Stroke = new SolidColorBrush(Color.FromArgb(56, figure.R, figure.G, figure.B));   // the faint rim
            _circle.StrokeThickness = 1;
            _figure.Fill = new SolidColorBrush(figure);
        }
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

    // Hues for the default picture; 252 is WhatsApp's own violet (#23244A circle, #A791FF figure).
    private static readonly double[] Hues = [252, 212, 184, 148, 96, 44, 22, 350, 318, 282];

    /// <summary>Dark: a deep, muted circle and a bright figure. Light: a pale circle and a mid-tone figure.</summary>
    private static (Color Back, Color Figure) PersonColors(double hue, bool light) => light
        ? (Hsl(hue, 0.55, 0.90), Hsl(hue, 0.50, 0.58))
        : (Hsl(hue - 14, 0.345, 0.21), Hsl(hue, 1.00, 0.78));   // WhatsApp's circle leans a little bluer than the figure

    private static Color Hsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(255, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
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
