using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace WhatsAppNative.Services;

/// <summary>
/// The other side's picture in a video call: the H.264 access units WhatsApp delivers go to a
/// <see cref="MediaPlayer"/> as a live stream, so Windows decodes and draws them (the call
/// window shows the player). It starts at a whole picture, plays each one as it arrives, and
/// if it falls behind it throws the backlog away and starts again at the next whole picture.
/// </summary>
public sealed class CallVideoPlayer : IDisposable
{
    private const int MostWaiting = 45;   // three seconds: past that it's not a live picture any more

    private readonly Lock _gate = new();
    private readonly Queue<(byte[] Unit, bool Key, TimeSpan At)> _waiting = new();
    private readonly Stopwatch _clock = new();
    private readonly MediaStreamSource _source;
    private MediaStreamSourceSampleRequest? _request;
    private MediaStreamSourceSampleRequestDeferral? _deferral;
    private bool _started, _disposed;

    public MediaPlayer Player { get; }

    /// <summary>Pictures handed to the player so far (the self-test reads it).</summary>
    internal int Played { get; private set; }

    public CallVideoPlayer()
    {
        // The size is only a starting point: the stream says its own, and it may change mid-call.
        var video = VideoEncodingProperties.CreateH264();
        video.Width = 640;
        video.Height = 480;
        _source = new MediaStreamSource(new VideoStreamDescriptor(video))
        {
            BufferTime = TimeSpan.Zero,
            CanSeek = false,
            IsLive = true,
        };
        _source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        _source.SampleRequested += OnSampleRequested;
        Player = new MediaPlayer { RealTimePlayback = true, AutoPlay = true, IsMuted = true };
        Player.CommandManager.IsEnabled = false;   // not a media session: no media keys, no overlay
        Player.Source = MediaSource.CreateFromMediaStreamSource(_source);
    }

    /// <summary>One access unit from the other side; <paramref name="key"/>: a decoder can start here. Any thread.</summary>
    public void Push(byte[] unit, bool key)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_started)
            {
                if (!key) return;
                _started = true;
                _clock.Restart();
            }
            var at = _clock.Elapsed;
            if (_deferral is { } deferral && _request is { } request)
            {
                request.Sample = Sample(unit, key, at);
                _deferral = null;
                _request = null;
                deferral.Complete();
                return;
            }
            if (_waiting.Count >= MostWaiting)
            {
                // The player isn't keeping up: skip to the next whole picture.
                _waiting.Clear();
                if (!key) { _started = false; return; }
            }
            _waiting.Enqueue((unit, key, at));
        }
    }

    /// <summary>The picture stopped (they paused their camera): the next one must be a whole picture.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _waiting.Clear();
            _started = false;
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_waiting.TryDequeue(out var next))
            {
                e.Request.Sample = Sample(next.Unit, next.Key, next.At);
                return;
            }
            // Nothing yet: answered by the next Push.
            _request = e.Request;
            _deferral = e.Request.GetDeferral();
        }
    }

    private MediaStreamSample Sample(byte[] unit, bool key, TimeSpan at)
    {
        var sample = MediaStreamSample.CreateFromBuffer(unit.AsBuffer(), at);
        sample.KeyFrame = key;
        sample.Duration = TimeSpan.FromMilliseconds(1000.0 / CallCamera.FramesPerSecond);
        Played++;
        return sample;
    }

    public void Dispose()
    {
        MediaStreamSourceSampleRequestDeferral? deferral;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _waiting.Clear();
            deferral = _deferral;
            _deferral = null;
            _request = null;
        }
        try { deferral?.Complete(); }
        catch (Exception) { }
        _source.SampleRequested -= OnSampleRequested;
        try
        {
            Player.Pause();
            Player.Source = null;
        }
        catch (Exception) { }
        Player.Dispose();
    }
}
