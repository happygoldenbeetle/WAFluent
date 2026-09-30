using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>
/// Spotify's own player for a Spotify link in a message (the embed websites use: play,
/// previews, the track list for albums and playlists), in Edge WebView2. It starts only when
/// the bubble is on screen and is let go when it's recycled.
/// </summary>
public sealed partial class SpotifyEmbed : Grid
{
    public const double Height2 = 352;

    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(SpotifyEmbed), new PropertyMetadata(null, (d, _) => ((SpotifyEmbed)d).Update()));

    /// <summary>The embed address (https://open.spotify.com/embed/…); null: nothing.</summary>
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    private WebView2? _view;
    private readonly Shimmer _shimmer = new() { IsActive = true };

    public SpotifyEmbed()
    {
        Height = Height2;
        Background = Helpers.Themed.Brush("FileCardBrush");
        Children.Add(_shimmer);
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Release();
    }

    private void Update()
    {
        if (Url is not { Length: > 0 } url || !IsLoaded)
        {
            Release();
            return;
        }
        if (_view is not null)
        {
            if (_view.Source?.AbsoluteUri != url) _view.Source = new Uri(url);
            return;
        }
        _shimmer.IsActive = true;
        _shimmer.Visibility = Visibility.Visible;
        _view = new WebView2
        {
            DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
            Opacity = 0,   // until the player has drawn (no white flash)
        };
        _view.NavigationCompleted += (_, _) =>
        {
            if (_view is null) return;
            _view.Opacity = 1;
            _shimmer.IsActive = false;
            _shimmer.Visibility = Visibility.Collapsed;
        };
        Children.Insert(0, _view);
        _view.Source = new Uri(url);
    }

    private void Release()
    {
        if (_view is null) return;
        Children.Remove(_view);
        _view.Close();
        _view = null;
        _shimmer.IsActive = true;
        _shimmer.Visibility = Visibility.Visible;
    }
}
