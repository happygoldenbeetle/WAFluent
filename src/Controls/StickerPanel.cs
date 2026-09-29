using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative.Controls;

/// <summary>
/// The sticker button's panel, like WhatsApp's: your recent stickers or GIFs in a grid, a
/// GIF | Stickers switch at the bottom (emoji have their own keyboard). Recents are every
/// sticker and GIF sent or received in your chats, newest first; click one to send it.
/// </summary>
public sealed partial class StickerPanel : Grid
{
    private const double Cell = 92;

    /// <summary>A sticker or GIF was picked (sent by the window, then the panel closes).</summary>
    public event Action<StickerDto>? Picked;

    /// <summary>Asks for a sticker's file (it arrives through <see cref="MediaArrived"/>).</summary>
    public event Action<StickerDto>? DownloadWanted;

    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(14, 12, 14, 6) };
    private readonly VariableSizedWrapGrid _grid = new()
    {
        Orientation = Orientation.Horizontal,
        ItemWidth = Cell,
        ItemHeight = Cell,
        Margin = new Thickness(8, 0, 8, 8),
    };
    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(40, 60, 40, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button _gifTab, _stickerTab;
    private readonly Dictionary<(string, string), Grid> _cells = new();
    private IReadOnlyList<StickerDto> _stickers = [], _gifs = [];
    private bool _showGifs;

    public StickerPanel()
    {
        Width = 420;
        Height = 440;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _empty.Foreground = Themed.Brush("TextFillColorSecondaryBrush");
        Children.Add(_title);
        var scroller = new ScrollViewer { Content = new Grid { Children = { _grid, _empty } } };
        SetRow(scroller, 1);
        Children.Add(scroller);

        // GIF | Stickers, a quiet pill at the bottom.
        _gifTab = Tab("GIF", null);
        _stickerTab = Tab(null, StickerGlyph());
        _gifTab.Click += (_, _) => Show(gifs: true);
        _stickerTab.Click += (_, _) => Show(gifs: false);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = Themed.Brush("CardStrokeColorDefaultBrush"),
            Background = Themed.Brush("CardBackgroundFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 10),
            Padding = new Thickness(3),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _gifTab, _stickerTab } },
        };
        SetRow(pill, 2);
        Children.Add(pill);
    }

    /// <summary>New contents from the core; shows the stickers first.</summary>
    public void SetItems(IReadOnlyList<StickerDto> stickers, IReadOnlyList<StickerDto> gifs)
    {
        _stickers = stickers;
        _gifs = gifs;
        Show(_showGifs);
    }

    /// <summary>A sticker's file finished downloading.</summary>
    public void MediaArrived(string chatId, string messageId, string path)
    {
        if (!_cells.TryGetValue((chatId, messageId), out var cell)) return;
        cell.Children.Clear();
        cell.Children.Add(new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 180 }, Stretch = Stretch.Uniform });
    }

    private void Show(bool gifs)
    {
        _showGifs = gifs;
        Highlight(_gifTab, gifs);
        Highlight(_stickerTab, !gifs);
        _title.Text = gifs ? "Recent GIFs" : "Recent stickers";
        _grid.Children.Clear();
        _cells.Clear();
        var items = gifs ? _gifs : _stickers;
        _empty.Text = gifs ? "GIFs you send or receive show up here." : "Stickers you send or receive show up here.";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items) _grid.Children.Add(ItemButton(item, gifs));
    }

    private Button ItemButton(StickerDto item, bool gif)
    {
        var cell = new Grid { Width = Cell - 12, Height = Cell - 12 };
        if (gif)
        {
            if (Ui.Thumb(item.Thumb) is { } thumb)
                cell.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush { ImageSource = thumb, Stretch = Stretch.UniformToFill } });
            else
                cell.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Themed.Brush("FileCardBrush") });
            cell.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)),
                Padding = new Thickness(5, 1, 5, 2),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "GIF", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            });
        }
        else if (item.Path is { } path)
        {
            cell.Children.Add(new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 180 }, Stretch = Stretch.Uniform });
        }
        else
        {
            cell.Children.Add(new ProgressRing { Width = 22, Height = 22, IsActive = true });
            _cells[(item.ChatId, item.MessageId)] = cell;
            DownloadWanted?.Invoke(item);
        }

        var button = new Button
        {
            Content = cell,
            Padding = new Thickness(0),
            Margin = new Thickness(3),
            Width = Cell - 6,
            Height = Cell - 6,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(button, gif ? "Send GIF" : "Send sticker");
        button.Click += (_, _) => Picked?.Invoke(item);
        return button;
    }

    private static Button Tab(string? text, UIElement? icon) => new()
    {
        Content = icon ?? new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
        Width = 84,
        Height = 30,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(15),
        BorderThickness = new Thickness(0),
    };

    private static void Highlight(Button tab, bool on) =>
        tab.Background = on ? Themed.Brush("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>A sticker: a rounded square with its corner peeled.</summary>
    public static UIElement StickerGlyph(double size = 16) => new Microsoft.UI.Xaml.Shapes.Path
    {
        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry),
            "M4.5,0.7 H12.5 A3.8,3.8 0 0 1 16.3,4.5 V10.2 L10.2,16.3 H4.5 A3.8,3.8 0 0 1 0.7,12.5 V4.5 A3.8,3.8 0 0 1 4.5,0.7 Z M10.2,16.3 V13 A2.8,2.8 0 0 1 13,10.2 H16.3"),
        Stroke = Themed.Brush("TextFillColorPrimaryBrush"),
        StrokeThickness = 1.3,
        StrokeLineJoin = PenLineJoin.Round,
        Width = size + 1,
        Height = size + 1,
        Stretch = Stretch.Uniform,
    };
}
