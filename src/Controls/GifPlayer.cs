using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace WhatsAppNative.Controls;

/// <summary>
/// A GIF in a bubble (WhatsApp sends GIFs as short MP4s): plays muted and looping while any
/// part of it is on screen, and lets go of the player as soon as it scrolls out of view,
/// the chat changes or the bubble is recycled, so only visible GIFs decode.
/// </summary>
public sealed partial class GifPlayer : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(GifPlayer), new PropertyMetadata(null, (d, _) => ((GifPlayer)d).Restart()));

    /// <summary>The downloaded MP4; null until it arrives.</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private MediaPlayer? _player;
    private MediaPlayerElement? _view;
    private bool _inView;

    public GifPlayer()
    {
        IsHitTestVisible = false;   // taps go to the bubble (opens the viewer)
        EffectiveViewportChanged += (_, e) =>
        {
            var viewport = e.EffectiveViewport;
            var visible = viewport.Width > 0 && viewport.Height > 0
                          && viewport.Right > 0 && viewport.Bottom > 0
                          && viewport.Left < ActualWidth && viewport.Top < ActualHeight;
            if (visible == _inView) return;
            _inView = visible;
            Update();
        };
        Unloaded += (_, _) => { _inView = false; Stop(); };
    }

    private void Restart()
    {
        Stop();
        Update();
    }

    private void Update()
    {
        if (!_inView || Source is not { Length: > 0 } path || !File.Exists(path))
        {
            Stop();
            return;
        }
        if (_player is not null) return;
        _player = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(path)),
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = true,
        };
        _view = new MediaPlayerElement { Stretch = Stretch.UniformToFill, AreTransportControlsEnabled = false };
        _view.SetMediaPlayer(_player);
        Children.Add(_view);
    }

    private void Stop()
    {
        if (_player is null) return;
        _player.Pause();
        _view?.SetMediaPlayer(null);
        _player.Dispose();
        _player = null;
        Children.Clear();
        _view = null;
    }
}
