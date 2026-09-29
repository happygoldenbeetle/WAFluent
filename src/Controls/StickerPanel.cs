using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative.Controls;

/// <summary>
/// The sticker button's panel, like WhatsApp's: your favourite and recent stickers, or your
/// recent GIFs, in grids, a GIF | Stickers switch at the bottom (emoji have their own
/// keyboard). Favourites are the ones starred on your phone (synced); recents are every
/// sticker and GIF sent or received in your chats, newest first. Click one to send it.
/// With a GIPHY key (Settings â€º GIF search) the GIF side also searches, trending first.
/// </summary>
public sealed partial class StickerPanel : Grid
{
    private const double Cell = 92;

    /// <summary>A sticker or GIF was picked (sent by the window, then the panel closes).</summary>
    public event Action<StickerDto>? Picked;

    /// <summary>A GIF from search was picked (the window downloads and sends it).</summary>
    public event Action<Giphy.Gif>? GifPicked;

    /// <summary>The GIPHY key; empty (a build without one): no search.</summary>
    public string GiphyKey { get; set; } = "";

    /// <summary>Asks for a sticker's file (it arrives through <see cref="MediaArrived"/>).</summary>
    public event Action<StickerDto>? DownloadWanted;

    private readonly StackPanel _sections = new() { Padding = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(40, 60, 40, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button _gifTab, _stickerTab;
    private readonly Dictionary<(string, string), Grid> _cells = new();
    private IReadOnlyList<StickerDto> _favorites = [], _stickers = [], _gifs = [];
    private bool _showGifs;

    private readonly TextBox _search = new()
    {
        PlaceholderText = "Search GIPHY",
        Margin = new Thickness(12, 12, 12, 2),
        Visibility = Visibility.Collapsed,
    };
    private readonly ScrollViewer _scroller = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _searching;
    private string? _loadedQuery, _loadedKey;
    private IReadOnlyList<Giphy.Gif>? _results;
    private string? _searchError;

    public StickerPanel()
    {
        Width = 420;
        Height = 440;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _empty.Foreground = Themed.Brush("TextFillColorSecondaryBrush");
        Children.Add(_search);
        _scroller.Content = new Grid { Children = { _sections, _empty } };
        SetRow(_scroller, 1);
        Children.Add(_scroller);
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (_showGifs) Show(gifs: true); };

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
    public void SetItems(IReadOnlyList<StickerDto> favorites, IReadOnlyList<StickerDto> stickers, IReadOnlyList<StickerDto> gifs)
    {
        _favorites = favorites;
        _stickers = stickers;
        _gifs = gifs;
        Show(_showGifs);
    }

    /// <summary>Switches to the GIF or sticker side.</summary>
    public void ShowTab(bool gifs) => Show(gifs);

    /// <summary>A sticker's file finished downloading.</summary>
    public void MediaArrived(string chatId, string messageId, string path)
    {
        if (!_cells.Remove((chatId, messageId), out var cell)) return;
        cell.Children.Clear();
        cell.Children.Add(new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 180 }, Stretch = Stretch.Uniform });
    }

    /// <summary>A sticker's file couldn't be fetched (expired on the server).</summary>
    public void MediaFailed(string chatId, string messageId)
    {
        if (!_cells.Remove((chatId, messageId), out var cell)) return;
        cell.Children.Clear();
        var glyph = StickerGlyph(22);
        glyph.Opacity = 0.35;
        cell.Children.Add(glyph);
        ToolTipService.SetToolTip(cell, "Couldn't load this sticker");
    }

    private void Show(bool gifs)
    {
        var scrollTop = gifs != _showGifs || _search.Text.Trim() != _loadedQuery;
        _showGifs = gifs;
        Highlight(_gifTab, gifs);
        Highlight(_stickerTab, !gifs);
        _sections.Children.Clear();
        _cells.Clear();
        var searchable = GiphyKey.Length > 0;
        _search.Visibility = gifs && searchable ? Visibility.Visible : Visibility.Collapsed;
        var query = _search.Text.Trim();
        var empty = false;
        if (gifs)
        {
            if (!searchable || query.Length == 0) Section("Recent GIFs", _gifs, gif: true);
            if (searchable)
            {
                if (query != _loadedQuery || GiphyKey != _loadedKey) _ = SearchAsync(query);
                Results(query.Length == 0 ? "Trending" : $"GIFs for \u201c{query}\u201d");
            }
            else
            {
                empty = _gifs.Count == 0;

            }
        }
        else
        {
            Section("Favourites", _favorites, gif: false);
            Section("Recent stickers", _stickers, gif: false);
            empty = _favorites.Count + _stickers.Count == 0;
        }
        _empty.Text = gifs
            ? "GIFs you send or receive show up here."
            : "Stickers you star on your phone, send or receive show up here.";
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (scrollTop) _scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Starts loading trending GIFs before the GIF side is opened.</summary>
    public void Prefetch()
    {
        if (GiphyKey.Length > 0 && _search.Text.Trim().Length == 0 && (_loadedQuery != "" || _loadedKey != GiphyKey)) _ = SearchAsync("");
    }

    // â”€â”€â”€â”€â”€ GIF search â”€â”€â”€â”€â”€

    private async Task SearchAsync(string query)
    {
        _searching?.Cancel();
        var cancel = (_searching = new CancellationTokenSource()).Token;
        _loadedQuery = query;
        _loadedKey = GiphyKey;
        _results = null;
        _searchError = null;
        try
        {
            var results = await Giphy.SearchAsync(GiphyKey, query, cancel);
            if (cancel.IsCancellationRequested) return;
            _results = results;
        }
        catch (Giphy.KeyRefusedException e)
        {
            _searchError = e.Message;
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            _searchError = "GIF search isn't reachable right now.";
        }
        catch (Exception)
        {
            return;   // replaced by a newer search
        }
        if (_showGifs) Show(gifs: true);
    }

    /// <summary>Search results (or trending) with GIPHY's attribution.</summary>
    private void Results(string title)
    {
        var header = new Grid { Margin = new Thickness(14, 10, 14, 6) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 110, 0) });
        header.Children.Add(new TextBlock
        {
            Text = "Powered by GIPHY",
            FontSize = 11,
            Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _sections.Children.Add(header);
        if (_searchError is { } error)
        {
            Hint(error);
            return;
        }
        if (_results is null)
        {
            _sections.Children.Add(new ProgressRing { Width = 26, Height = 26, IsActive = true, Margin = new Thickness(0, 30, 0, 0) });
            return;
        }
        if (_results.Count == 0)
        {
            Hint("No GIFs found.");
            return;
        }
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = Cell, ItemHeight = Cell, Margin = new Thickness(8, 0, 8, 4) };
        foreach (var gif in _results) grid.Children.Add(ResultButton(gif));
        _sections.Children.Add(grid);
    }

    private Button ResultButton(Giphy.Gif gif)
    {
        var button = new Button
        {
            Content = new Border
            {
                Width = Cell - 12,
                Height = Cell - 12,
                CornerRadius = new CornerRadius(8),
                Background = Themed.Brush("FileCardBrush"),
                Child = new Image { Source = new BitmapImage(new Uri(gif.Preview)) { DecodePixelWidth = 180 }, Stretch = Stretch.UniformToFill },
            },
            Padding = new Thickness(0),
            Margin = new Thickness(3),
            Width = Cell - 6,
            Height = Cell - 6,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(gif.Title) ? "Send GIF" : gif.Title);
        button.Click += (_, _) => GifPicked?.Invoke(gif);
        return button;
    }

    private void Hint(string text) => _sections.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
        Margin = new Thickness(14, 8, 14, 8),
    });

    /// <summary>A titled grid; nothing when there are no items.</summary>
    private void Section(string title, IReadOnlyList<StickerDto> items, bool gif)
    {
        if (items.Count == 0) return;
        _sections.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(14, 10, 14, 6),
        });
        var grid = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = Cell,
            ItemHeight = Cell,
            Margin = new Thickness(8, 0, 8, 4),
        };
        foreach (var item in items) grid.Children.Add(ItemButton(item, gif));
        _sections.Children.Add(grid);
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

    /// <summary>A sticker (SF-style, Helpers/Sf.cs).</summary>
    public static UIElement StickerGlyph(double size = 17) => new FontIcon { FontFamily = Ui.SymbolFont, Glyph = Sf.Sticker, FontSize = size };
}
