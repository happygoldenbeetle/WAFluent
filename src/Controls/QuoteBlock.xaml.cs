using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhatsAppNative.Controls;

/// <summary>A quoted message: who wrote it and a line or two of what it said.</summary>
public sealed partial class QuoteBlock : UserControl
{
    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(
        nameof(Author), typeof(string), typeof(QuoteBlock), new PropertyMetadata("", (d, _) => ((QuoteBlock)d).Update()));

    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
        nameof(Preview), typeof(string), typeof(QuoteBlock), new PropertyMetadata("", (d, _) => ((QuoteBlock)d).Update()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(QuoteBlock), new PropertyMetadata("", (d, _) => ((QuoteBlock)d).Update()));

    public static readonly DependencyProperty FromMeProperty = DependencyProperty.Register(
        nameof(FromMe), typeof(bool), typeof(QuoteBlock), new PropertyMetadata(false, (d, _) => ((QuoteBlock)d).Update()));

    public static readonly DependencyProperty ThumbProperty = DependencyProperty.Register(
        nameof(Thumb), typeof(string), typeof(QuoteBlock), new PropertyMetadata(null, (d, _) => ((QuoteBlock)d).Update()));

    /// <summary>The quoted media's picture: a file path, or a base64 JPEG preview.</summary>
    public string? Thumb { get => (string?)GetValue(ThumbProperty); set => SetValue(ThumbProperty, value); }

    public string Author { get => (string)GetValue(AuthorProperty); set => SetValue(AuthorProperty, value); }
    public string Preview { get => (string)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool FromMe { get => (bool)GetValue(FromMeProperty); set => SetValue(FromMeProperty, value); }

    public QuoteBlock()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
    }

    private void Update()
    {
        NameText.Text = Author;
        PreviewText.Text = Preview;
        KindIcon.Glyph = Glyph;
        KindIcon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        VisualStateManager.GoToState(this, FromMe ? "Self" : "Other", false);
        Microsoft.UI.Xaml.Media.ImageSource? picture = Thumb switch
        {
            { Length: > 0 } p when p.Length < 300 && File.Exists(p) => new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(p)) { DecodePixelWidth = 112 },
            { Length: > 0 } t => Helpers.Ui.Thumb(t),
            _ => null,
        };
        ThumbImage.ImageSource = picture;
        ThumbBox.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
