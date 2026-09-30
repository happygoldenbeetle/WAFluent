using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative.Controls;

/// <summary>
/// Photos and videos sent together, in one bubble like WhatsApp: 2 stacked, 3 as one large
/// and two small, 4 as a 2×2 grid, more as a 2×2 grid with "+N" on the last tile. Each tile
/// shows the item's picture (its preview until it's downloaded) and follows its download; a
/// click opens that item (<see cref="Open"/>), a right-click its menu (<see cref="Menu"/>).
/// </summary>
public sealed partial class AlbumGrid : Grid
{
    public const double Width2 = 330, Gap = 3;

    public static readonly DependencyProperty AlbumProperty = DependencyProperty.Register(
        nameof(Album), typeof(Message), typeof(AlbumGrid), new PropertyMetadata(null, (d, _) => ((AlbumGrid)d).Rebuild()));

    public Message? Album
    {
        get => (Message?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    /// <summary>Opens an item (the window's photo or video viewer), from its tile.</summary>
    public static Action<Message, FrameworkElement>? Open;

    /// <summary>Shows every item of an album in a grid (the "+N" tile).</summary>
    public static Action<Message>? Expand;

    /// <summary>An item's context menu, at its tile.</summary>
    public static Action<Message, FrameworkElement, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs>? Menu;

    private readonly List<(Message Item, Grid Tile)> _tiles = [];
    private Message? _watched;

    public AlbumGrid()
    {
        Width = Width2;
        RowSpacing = Gap;
        ColumnSpacing = Gap;
        Unloaded += (_, _) => Unwatch();
        Loaded += (_, _) => { if (_watched is null) Rebuild(); };   // recycled back into view
    }

    private void Rebuild()
    {
        Unwatch();
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        _tiles.Clear();
        if (Album is not { AlbumItems: { Count: > 0 } items } album) return;
        _watched = album;
        album.PropertyChanged += Album_PropertyChanged;

        // Tiles along the bubble's edge follow its curve (its radius minus the 3 px around the
        // grid); inside corners stay small. A sender's name above means the top isn't the edge.
        _outer = Math.Max(4, (Ui.IMessage ? Ui.BubbleRadius : 8) - 3);
        _topIsEdge = !album.ShowSender;
        _rows = items.Count == 1 ? 1 : 2;
        _columns = items.Count == 2 ? 1 : 2;
        double half = (Width2 - Gap) / 2;
        var shown = items.Count > 4 ? items.Take(4).ToList() : items.ToList();
        switch (shown.Count)
        {
            case 2:   // stacked, full width
                ColumnDefinitions.Add(new ColumnDefinition());
                AddRow(210);
                AddRow(210);
                Place(shown[0], 0, 0);
                Place(shown[1], 1, 0);
                break;
            case 3:   // one large on top, two below
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition());
                AddRow(220);
                AddRow(half);
                Place(shown[0], 0, 0, columnSpan: 2);
                Place(shown[1], 1, 0);
                Place(shown[2], 1, 1);
                break;
            default:  // 2×2, "+N" on the last when there are more
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition());
                AddRow(half);
                AddRow(half);
                for (var i = 0; i < 4; i++) Place(shown[i], i / 2, i % 2, more: i == 3 ? items.Count - 4 : 0);
                break;
        }
    }

    private double _outer = 9;
    private bool _topIsEdge = true;
    private int _rows = 2, _columns = 2;

    private CornerRadius Corners(int row, int column, int columnSpan)
    {
        const double inner = 3;
        bool top = row == 0 && _topIsEdge, bottom = row == _rows - 1;
        bool left = column == 0, right = column + columnSpan >= _columns;
        return new CornerRadius(top && left ? _outer : inner, top && right ? _outer : inner,
                                bottom && right ? _outer : inner, bottom && left ? _outer : inner);
    }

    private void AddRow(double height) => RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });

    private void Place(Message item, int row, int column, int columnSpan = 1, int more = 0)
    {
        var tile = new Grid { CornerRadius = Corners(row, column, columnSpan), Background = Themed.Brush("FileCardBrush"), Tag = item };
        SetRow(tile, row);
        SetColumn(tile, column);
        SetColumnSpan(tile, columnSpan);
        Fill(tile, item, more);
        tile.Tapped += (_, e) =>
        {
            e.Handled = true;
            // The "+N" tile opens the whole album; the others open their photo or video.
            if (more > 0 && Album is { } album) Expand?.Invoke(album);
            else Open?.Invoke(item, tile);
        };
        tile.ContextRequested += (_, e) => Menu?.Invoke(item, tile, e);
        item.PropertyChanged += Item_PropertyChanged;
        _tiles.Add((item, tile));
        Children.Add(tile);
    }

    /// <summary>The picture (preview until downloaded), a play button on videos, "+N" on the last.</summary>
    private static void Fill(Grid tile, Message item, int more)
    {
        tile.Children.Clear();
        ImageSource? picture = item.MediaPath is { } path && item.Kind == MessageKind.Image && File.Exists(path)
            ? new BitmapImage(new Uri(path)) { DecodePixelWidth = 400 }
            : Ui.Thumb(item.Thumb);
        if (picture is not null)
            tile.Children.Add(new Border { Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill } });
        if (item.IsMediaLoading && item.Kind == MessageKind.Image)
            tile.Children.Add(new ProgressRing { Width = 26, Height = 26, IsActive = true });
        if (item.Kind == MessageKind.Video)
            tile.Children.Add(new Grid
            {
                Width = 44,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)) },
                    new FontIcon { Glyph = "", FontSize = 16, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(3, 0, 0, 0) },
                },
            });
        if (more > 0)
        {
            tile.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0, 0, 0)) });
            tile.Children.Add(new TextBlock
            {
                Text = $"+{more}",
                FontSize = 30,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Message.MediaPath) or nameof(Message.IsMediaLoading) or nameof(Message.MediaFailed))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var at = _tiles.FindIndex(t => t.Item == sender);
            if (at < 0 || Album?.AlbumItems is not { } items) return;
            Fill(_tiles[at].Tile, _tiles[at].Item, at == 3 && items.Count > 4 ? items.Count - 4 : 0);
        });
    }

    private void Album_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Message.AlbumItems)) DispatcherQueue.TryEnqueue(Rebuild);
    }

    private void Unwatch()
    {
        foreach (var (item, _) in _tiles) item.PropertyChanged -= Item_PropertyChanged;
        if (_watched is not null) _watched.PropertyChanged -= Album_PropertyChanged;
        _watched = null;
    }
}
