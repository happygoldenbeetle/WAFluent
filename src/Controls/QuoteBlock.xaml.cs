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
    }
}
