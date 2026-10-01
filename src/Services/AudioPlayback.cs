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

    /// <summary>Playing, or about to (still opening): pressing again pauses rather than plays twice.</summary>
    public static bool IsPlaying => _player?.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Opening or MediaPlaybackState.Buffering
                                    && !_paused;

    private static bool _paused;
    private static int _opened;

    public static TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    public static TimeSpan Duration =>
        _player?.PlaybackSession.NaturalDuration is { } d && d > TimeSpan.Zero ? d
        : TimeSpan.FromSeconds(Current?.Seconds ?? 0);

    public static double Rate { get; private set; } = 1;

    /// <summary>Self-tests play notes silently.</summary>
    internal static bool Muted { get; set; }

    public static void Toggle(Message message)
    {
        if (message.MediaPath is null) return;
        if (Current == message && _player is not null)
        {
            var playing = IsPlaying;
            if (playing) _player.Pause(); else _player.Play();
            _paused = playing;
            Raise();
            return;
        }
        Open(message, play: true);
    }

    /// <summary>Opens a note paused (the self-test's mini player, which mustn't make a sound).</summary>
    internal static void Load(Message message)
    {
        if (message.MediaPath is not null) Open(message, play: false);
    }

    /// <summary>
    /// Plays <paramref name="message"/>'s file, or <paramref name="source"/> instead: its own
    /// decoding (a WAV) when Windows' decoder gave up on the OGG. Notes made here (Concentus)
    /// open in Windows but fail to decode, so they go that way; so would any received one.
    /// </summary>
    private static void Open(Message message, bool play, string? source = null)
    {
        Stop();
        var ui = DispatcherQueue.GetForCurrentThread();
        source ??= VoiceRecorder.DecodedCopy(message.MediaPath!) ?? message.MediaPath!;
        var opened = ++_opened;
        var failed = false;   // Windows reports a decoding failure several times; once is enough
        _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(source)) };
        _player.PlaybackSession.PlaybackRate = Rate;
        _player.IsMuted = Muted;
        _player.MediaEnded += (_, _) => ui.TryEnqueue(Stop);
        _player.MediaFailed += (_, e) =>
        {
            ui.TryEnqueue(async () =>
            {
                if (failed || opened != _opened) return;   // handled, or already gone on to something else
                failed = true;
                var ogg = message.MediaPath!;
                if (source == ogg && ogg.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    Stop();
                    Current = message;   // keeps the bubble "playing" while it decodes
                    Raise();
                    var wav = await Task.Run(() => VoiceRecorder.DecodeToWav(ogg));
                    if (opened != _opened || Current != message) return;   // stopped, or another note started
                    if (wav is not null)
                    {
                        Open(message, play, wav);
                        return;
                    }
                }
                Helpers.AppLog.Write($"playing a voice note failed: {e.Error} 0x{e.ExtendedErrorCode?.HResult:X8} ({source})");
                Stop();
            });
        };
        _player.MediaOpened += (sender, _) => ui.TryEnqueue(() =>
        {
            if (_pendingSeek is not { } fraction || sender != _player) return;
            _pendingSeek = null;
            _player.PlaybackSession.Position = TimeSpan.FromTicks((long)(Duration.Ticks * fraction));
            Raise();
        });
        Current = message;
        _paused = !play;
        if (play) _player.Play();

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
