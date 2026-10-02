using System.Runtime.InteropServices;
using Concentus;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace WhatsAppNative.Services;

/// <summary>The sound the call window makes by itself while a call isn't connected.</summary>
public enum CallTone
{
    None,
    /// <summary>Your call is ringing on the other side.</summary>
    Ringback,
}

/// <summary>
/// A call's sound. WhatsApp's calls (the core) work in 60 ms frames of 16 kHz mono 16-bit
/// samples: the microphone is turned into those and handed to <see cref="Frame"/>, and the
/// other side's are played through <see cref="Play"/>. One AudioGraph in Windows' Communications
/// category does both, so Windows treats it as a call (its echo cancelling and the default
/// communications devices apply). What's heard waits in a short queue (120 ms) so frames that
/// arrive unevenly still play smoothly.
/// </summary>
public sealed class CallAudio : IDisposable
{
    public const int Rate = 16000;
    public const int FrameSamples = 960;

    private static readonly Guid MemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    private AudioGraph? _graph;
    private AudioFrameInputNode? _speaker;
    private AudioDeviceInputNode? _microphone;
    private AudioFileInputNode? _fileMicrophone;
    private AudioFrameOutputNode? _capture;
    private int _graphRate = 48000;

    // What's heard: 16 kHz samples waiting to be played, and where between two of them playback is.
    private readonly Lock _gate = new();
    private readonly Queue<float> _heard = new();
    private bool _primed;
    private double _position;
    private float _previous, _current;
    private IOpusDecoder? _opus;
    private long _toneAt;

    // What's said: the frame being filled, and the samples being averaged down to 16 kHz.
    private readonly float[] _frame = new float[FrameSamples];
    private int _filled, _count, _phase;
    private double _sum;
    private bool _captureFailed, _renderFailed;

    /// <summary>A frame from the microphone: 960 samples, 16-bit little-endian. Raised on the audio thread.</summary>
    public event Action<byte[]>? Frame;

    /// <summary>Muted: silence is sent (the call keeps its rhythm).</summary>
    public bool Muted { get; set; }

    public CallTone Tone { get; set; }

    /// <summary>How loud you and the other side are right now, 0-1 (the call window's wave).</summary>
    public float MicLevel { get; private set; }
    public float PeerLevel { get; private set; }

    /// <summary>Frames sent and samples played so far (the self-test reads these).</summary>
    internal int FramesCaptured { get; private set; }
    internal long SamplesPlayed { get; private set; }

    /// <summary>The self-test: nothing is heard, and a WAV stands in for the microphone.</summary>
    internal static bool Silent { get; set; }
    internal static string? TestInput { get; set; }

    public bool IsOpen => _graph is not null;
    public bool HasMicrophone => _capture is not null;

    /// <summary>Opens the speakers (ringing tones and the other side's voice). The microphone opens separately.</summary>
    public async Task OpenAsync()
    {
        if (_graph is not null) return;
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Communications));
        if (created.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException($"audio: {created.Status}");
        var graph = created.Graph;
        _graphRate = (int)graph.EncodingProperties.SampleRate;
        var output = await graph.CreateDeviceOutputNodeAsync();
        if (output.Status != AudioDeviceNodeCreationStatus.Success)
        {
            graph.Dispose();
            throw new InvalidOperationException($"speakers: {output.Status}");
        }
        _speaker = graph.CreateFrameInputNode(Mono());
        if (Silent) _speaker.OutgoingGain = 0;
        _speaker.AddOutgoingConnection(output.DeviceOutputNode);
        _speaker.QuantumStarted += (node, e) =>
        {
            if (e.RequiredSamples <= 0) return;
            try
            {
                Feed(node, e.RequiredSamples);
            }
            catch (Exception ex)
            {
                if (!_renderFailed) Helpers.AppLog.Write("playing call audio failed", ex);
                _renderFailed = true;
            }
        };
        graph.QuantumStarted += (_, _) =>
        {
            try
            {
                Collect();
            }
            catch (Exception ex)
            {
                if (!_captureFailed) Helpers.AppLog.Write("reading the microphone in a call failed", ex);
                _captureFailed = true;
            }
        };
        _graph = graph;
        graph.Start();
    }

    /// <summary>
    /// Opens the microphone (<paramref name="deviceId"/>: null or empty for Windows' default; one
    /// that's gone falls back to it), replacing the one in use. Throws
    /// <see cref="UnauthorizedAccessException"/> when microphone access is off.
    /// </summary>
    public async Task OpenMicrophoneAsync(string? deviceId)
    {
        if (_graph is not { } graph) return;
        CloseMicrophone();
        IAudioInputNode input;
        if (TestInput is { } file)
        {
            var source = await graph.CreateFileInputNodeAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
            if (source.Status != AudioFileNodeCreationStatus.Success) throw new InvalidOperationException($"file: {source.Status}");
            input = _fileMicrophone = source.FileInputNode;
        }
        else
        {
            Windows.Devices.Enumeration.DeviceInformation? device = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                try { device = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(deviceId); }
                catch (Exception) { }   // gone: the default one
            }
            var opened = device is { IsEnabled: true }
                ? await graph.CreateDeviceInputNodeAsync(MediaCategory.Communications, graph.EncodingProperties, device)
                : await graph.CreateDeviceInputNodeAsync(MediaCategory.Communications);
            if (opened.Status == AudioDeviceNodeCreationStatus.AccessDenied) throw new UnauthorizedAccessException("microphone access is off");
            if (opened.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException($"microphone: {opened.Status}");
            input = _microphone = opened.DeviceInputNode;
        }
        if (_graph != graph)
        {
            // Closed while the microphone was opening.
            (input as IDisposable)?.Dispose();
            _microphone = null;
            _fileMicrophone = null;
            return;
        }
        var capture = graph.CreateFrameOutputNode(Mono());
        input.AddOutgoingConnection(capture);
        lock (_frame)
        {
            _filled = _count = _phase = 0;
            _sum = 0;
        }
        _capture = capture;
    }

    private void CloseMicrophone()
    {
        var capture = _capture;
        _capture = null;
        _microphone?.Dispose();
        _fileMicrophone?.Dispose();
        capture?.Dispose();
        _microphone = null;
        _fileMicrophone = null;
        MicLevel = 0;
    }

    /// <summary>One channel of 32-bit float samples at the graph's rate: what both frame nodes carry.</summary>
    private AudioEncodingProperties Mono()
    {
        var mono = AudioEncodingProperties.CreatePcm((uint)_graphRate, 1, 32);
        mono.Subtype = MediaEncodingSubtypes.Float;
        return mono;
    }

    // ───── The other side ─────

    /// <summary>
    /// The other side's voice: a frame of 16-bit samples, or (<paramref name="opus"/>) one Opus
    /// packet to decode first. Any thread.
    /// </summary>
    public void Play(byte[] data, bool opus)
    {
        ReadOnlySpan<short> samples;
        if (opus)
        {
            var pcm = new short[FrameSamples * 2];   // Opus packets hold up to 120 ms
            int decoded;
            try
            {
                _opus ??= OpusCodecFactory.CreateDecoder(Rate, 1);
                decoded = _opus.Decode(data, pcm, pcm.Length, false);
            }
            catch (Exception)
            {
                return;   // a damaged packet: a gap, not the end of the call
            }
            samples = pcm.AsSpan(0, Math.Max(0, decoded));
        }
        else
        {
            samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, data.Length & ~1));
        }
        if (samples.Length == 0) return;

        double sum = 0;
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                var v = sample / 32768f;
                sum += v * v;
                _heard.Enqueue(v);
            }
            // Far behind (frames came in a burst): skip ahead rather than talk half a second late.
            if (_heard.Count > Rate / 2)
                while (_heard.Count > FrameSamples * 2) _heard.Dequeue();
        }
        PeerLevel = (float)Math.Min(1, Math.Sqrt(sum / samples.Length) * 4);
    }

    /// <summary>The graph wants <paramref name="samples"/> more to play: the queue, resampled, plus any tone.</summary>
    private unsafe void Feed(AudioFrameInputNode node, int samples)
    {
        using var frame = new AudioFrame((uint)(samples * sizeof(float)));
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            var unknown = WinRT.MarshalInterface<Windows.Foundation.IMemoryBufferReference>.FromManaged(reference);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in MemoryBufferByteAccess, out var access));
                try
                {
                    byte* data;
                    uint capacity;
                    // IMemoryBufferByteAccess::GetBuffer, the first method after IUnknown's three.
                    var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
                    Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
                    Render(new Span<float>(data, Math.Min(samples, (int)(capacity / sizeof(float)))));
                }
                finally
                {
                    Marshal.Release(access);
                }
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        node.AddFrame(frame);
    }

    private void Render(Span<float> output)
    {
        var step = (double)Rate / _graphRate;
        var played = 0;
        lock (_gate)
        {
            if (!_primed && _heard.Count >= FrameSamples * 2) _primed = true;
            for (var i = 0; i < output.Length; i++)
            {
                float v = 0;
                if (_primed)
                {
                    _position += step;
                    while (_position >= 1)
                    {
                        _position -= 1;
                        _previous = _current;
                        if (_heard.Count > 0)
                        {
                            _current = _heard.Dequeue();
                            played++;
                        }
                        else
                        {
                            // Ran dry: silence until a couple of frames are waiting again.
                            _current = 0;
                            _primed = false;
                        }
                    }
                    v = _previous + (_current - _previous) * (float)_position;
                }
                output[i] = v;
            }
        }
        SamplesPlayed += played;
        if (played == 0) PeerLevel *= 0.8f;
        if (Tone == CallTone.None)
        {
            _toneAt = 0;
            return;
        }
        for (var i = 0; i < output.Length; i++) output[i] += Ringback(_toneAt++ / (double)_graphRate);
    }

    /// <summary>Two soft beeps, then a pause: "it's ringing there".</summary>
    private static float Ringback(double t)
    {
        const double beep = 0.4, gap = 0.2, cycle = 3.0, fade = 0.03;
        var at = t % cycle;
        double into;
        if (at < beep) into = at;
        else if (at >= beep + gap && at < beep + gap + beep) into = at - beep - gap;
        else return 0;
        var envelope = Math.Min(1, Math.Min(into, beep - into) / fade);
        return (float)(Math.Sin(2 * Math.PI * 440 * t) * 0.07 * envelope);
    }

    // ───── You ─────

    /// <summary>Each audio quantum: the microphone's samples, averaged down to 16 kHz, cut into 60 ms frames.</summary>
    private void Collect()
    {
        if (_capture is not { } capture) return;
        using var frame = capture.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        // The copy holds the samples but says it's empty: its length has to be set.
        var copy = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer);
        copy.Length = buffer.Length;
        var bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(copy);
        var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, bytes.Length & ~3));
        lock (_frame)
        {
            foreach (var v in samples)
            {
                _sum += v;
                _count++;
                _phase += Rate;
                if (_phase < _graphRate) continue;
                _phase -= _graphRate;
                _frame[_filled++] = (float)(_sum / _count);
                _sum = 0;
                _count = 0;
                if (_filled == FrameSamples) Emit();
            }
        }
    }

    private void Emit()
    {
        _filled = 0;
        var bytes = new byte[FrameSamples * 2];
        double sum = 0;
        if (!Muted)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
            for (var i = 0; i < FrameSamples; i++)
            {
                var v = _frame[i];
                sum += v * v;
                pcm[i] = (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);
            }
        }
        MicLevel = (float)Math.Min(1, Math.Sqrt(sum / FrameSamples) * 4);
        FramesCaptured++;
        Frame?.Invoke(bytes);
    }

    public void Dispose()
    {
        if (_graph is not { } graph) return;
        _graph = null;
        try { graph.Stop(); }
        catch (Exception) { }
        _capture = null;
        _speaker = null;
        _microphone = null;
        _fileMicrophone = null;
        graph.Dispose();
        MicLevel = PeerLevel = 0;
    }
}
