using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>
/// Repeats a wallpaper tile over <see cref="Panel.Background"/> (XAML image brushes can't tile).
/// Every tile shares the one decoded bitmap, so any window size costs the same memory.
/// </summary>
public sealed partial class TiledBackground : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(TiledBackground), new PropertyMetadata(null, Changed));

    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(TiledBackground), new PropertyMetadata(400.0, Changed));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(TiledBackground), new PropertyMetadata(720.0, Changed));

    public static readonly DependencyProperty TileOpacityProperty = DependencyProperty.Register(
        nameof(TileOpacity), typeof(double), typeof(TiledBackground), new PropertyMetadata(1.0, Changed));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public double TileOpacity { get => (double)GetValue(TileOpacityProperty); set => SetValue(TileOpacityProperty, value); }

    private readonly Canvas _tiles = new();

    public TiledBackground()
    {
        IsHitTestVisible = false;
        Children.Add(_tiles);
        SizeChanged += (_, _) => Layout(rebuild: false);
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TiledBackground)d).Layout(rebuild: true);

    private void Layout(bool rebuild)
    {
        _tiles.Opacity = TileOpacity;
        if (Source is null || TileWidth < 1 || TileHeight < 1)
        {
            _tiles.Children.Clear();
            return;
        }
        if (rebuild) _tiles.Children.Clear();

        var columns = (int)Math.Ceiling(ActualWidth / TileWidth);
        var rows = (int)Math.Ceiling(ActualHeight / TileHeight);
        var needed = Math.Max(0, columns * rows);
        while (_tiles.Children.Count > needed) _tiles.Children.RemoveAt(_tiles.Children.Count - 1);

        for (var i = 0; i < needed; i++)
        {
            if (i >= _tiles.Children.Count)
            {
                // A hair of overlap so rounding never opens a seam between tiles.
                _tiles.Children.Add(new Image { Source = Source, Stretch = Stretch.Fill, Width = TileWidth + 0.5, Height = TileHeight + 0.5 });
            }
            var tile = _tiles.Children[i];
            Canvas.SetLeft(tile, i % columns * TileWidth);
            Canvas.SetTop(tile, i / columns * TileHeight);
        }
    }
}
