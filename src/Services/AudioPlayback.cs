using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using WhatsAppNative.Models;

namespace WhatsAppNative.Services;

/// <summary>
/// Plays one voice note at a time (starting another stops the first), through Windows'
/// own media stack — its Opus decoder handles WhatsApp's .ogg voice notes.
/// </summary>
public static class AudioPlayback
{
    private static MediaPlayer? _player;
    private static DispatcherQueueTimer? _timer;
    private static double? _pendingSeek;

    /// <summary>Raised on the UI thread whenever the current note, state or position changes.</summary>
    public static event Action? Changed;

    public static Message? Current { get; private set; }

    public static bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public static TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    public static TimeSpan Duration =>
        _player?.PlaybackSession.NaturalDuration is { } d && d > TimeSpan.Zero ? d
        : TimeSpan.FromSeconds(Current?.Seconds ?? 0);

    public static double Rate { get; private set; } = 1;

    public static void Toggle(Message message)
    {
        if (message.MediaPath is null) return;
        if (Current == message && _player is not null)
        {
            if (IsPlaying) _player.Pause(); else _player.Play();
            Raise();
            return;
        }

        Stop();
        var ui = DispatcherQueue.GetForCurrentThread();
        _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(message.MediaPath)) };
        _player.PlaybackSession.PlaybackRate = Rate;
        _player.MediaEnded += (_, _) => ui.TryEnqueue(Stop);
        _player.MediaFailed += (_, _) => ui.TryEnqueue(Stop);
        _player.MediaOpened += (sender, _) => ui.TryEnqueue(() =>
        {
            if (_pendingSeek is not { } fraction || sender != _player) return;
            _pendingSeek = null;
            _player.PlaybackSession.Position = TimeSpan.FromTicks((long)(Duration.Ticks * fraction));
            Raise();
        });
        Current = message;
        _player.Play();

        _timer ??= CreateTimer(ui);
        _timer.Start();
        Raise();
    }

    /// <summary>Jumps to a point (0..1) in the note, starting it if needed.</summary>
    public static void Seek(Message message, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (Current != message) Toggle(message);
        if (_player is null) return;
        if (_player.PlaybackSession.NaturalDuration <= TimeSpan.Zero)
            _pendingSeek = fraction;   // still opening: jump there once it has
        else
            _player.PlaybackSession.Position = TimeSpan.FromTicks((long)(Duration.Ticks * fraction));
        Raise();
    }

    /// <summary>1x → 1.5x → 2x → 1x, like WhatsApp.</summary>
    public static void CycleRate() => SetRate(Rate switch { 1 => 1.5, 1.5 => 2, _ => 1 });

    public static void SetRate(double rate)
    {
        Rate = rate;
        if (_player is not null) _player.PlaybackSession.PlaybackRate = Rate;
        Raise();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        Current = null;
        Raise();
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue ui)
    {
        var timer = ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => Raise();
        return timer;
    }

    private static void Raise() => Changed?.Invoke();
}
