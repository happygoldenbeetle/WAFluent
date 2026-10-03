using Microsoft.UI.Dispatching;
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
///
/// A player is slow to make and to let go of (tens of milliseconds each), so neither is done
/// where it would stall a scroll: it's made on another thread, and only once the GIF has
/// stayed on screen for a moment (one that's only passing through never gets one), and it's
/// let go of on another thread too. The bubble's still preview shows until it's ready.
/// </summary>
public sealed partial class GifPlayer : Grid
{
    /// <summary>How long a GIF has to stay on screen before it starts.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(280);

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
    private DispatcherQueueTimer? _settle;
    /// <summary>Goes up with every start and stop: a player that finishes being made for an older one is thrown away.</summary>
    private int _wanted;

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
        if (!_inView || Source is not { Length: > 0 })
        {
            Stop();
            return;
        }
        if (_player is not null || _settle is { IsRunning: true }) return;
        _settle ??= DispatcherQueue.CreateTimer();
        _settle.Interval = Settle;
        _settle.IsRepeating = false;
        _settle.Tick -= Settled;
        _settle.Tick += Settled;
        _settle.Start();
    }

    /// <summary>Still on screen after a moment: the player is made (off this thread) and shown.</summary>
    private async void Settled(DispatcherQueueTimer sender, object args)
    {
        if (!_inView || _player is not null || Source is not { Length: > 0 } path) return;
        var wanted = ++_wanted;
        MediaPlayer? player;
        try
        {
            player = await Task.Run(() => File.Exists(path)
                ? new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), IsLoopingEnabled = true, IsMuted = true, AutoPlay = true }
                : null);
        }
        catch (Exception)
        {
            return;   // not a file the player takes: the preview stays
        }
        if (player is null) return;
        if (wanted != _wanted || !_inView || Source != path)
        {
            LetGo(player);   // it left (or changed) while this was being made
            return;
        }
        _player = player;
        _view = new MediaPlayerElement { Stretch = Stretch.UniformToFill, AreTransportControlsEnabled = false };
        _view.SetMediaPlayer(_player);
        Children.Add(_view);
    }

    private void Stop()
    {
        _wanted++;
        _settle?.Stop();
        if (_player is null) return;
        _view?.SetMediaPlayer(null);
        Children.Clear();
        _view = null;
        LetGo(_player);
        _player = null;
    }

    private static void LetGo(MediaPlayer player) => Task.Run(() =>
    {
        try
        {
            player.Pause();
            player.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }
    });
}
