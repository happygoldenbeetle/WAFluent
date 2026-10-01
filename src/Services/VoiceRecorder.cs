using System.Runtime.InteropServices.WindowsRuntime;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;

namespace WhatsAppNative.Services;

/// <summary>
/// Voice notes: records the microphone through an AudioGraph (the samples arrive as they're
/// spoken, so the recording bar can draw a live waveform), can pause and resume, then encodes
/// the way WhatsApp sends voice notes — Opus in an OGG file (Concentus) — with the 64-bar
/// waveform the bubbles draw. Microphone access for desktop apps must be on in Windows'
/// privacy settings.
/// </summary>
public sealed class VoiceRecorder : IAsyncDisposable
{
    private const int Rate = 48000;
    private AudioGraph? _graph;
    private AudioFrameOutputNode? _output;
    private readonly List<float> _samples = [];
    private readonly Lock _gate = new();
    private int _graphRate = Rate, _channels = 1;
    private float _level;

    public bool IsRecording => _graph is not null;
    public bool IsPaused { get; private set; }

    /// <summary>What's been recorded so far.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_samples.Count / (double)Rate); }
    }

    /// <summary>The loudness right now, 0-1 (for the live waveform).</summary>
    public float Level => IsPaused ? 0 : _level;

    /// <summary>Starts recording. Throws <see cref="UnauthorizedAccessException"/> when microphone access is off.</summary>
    public async Task StartAsync()
    {
        if (_graph is not null) return;
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException($"audio: {created.Status}");
        var graph = created.Graph;
        if (TestInput is { } file)
        {
            // Self-test: a WAV through the same graph instead of the microphone.
            var source = await graph.CreateFileInputNodeAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));
            if (source.Status != AudioFileNodeCreationStatus.Success) throw new InvalidOperationException($"file: {source.Status}");
            Begin(graph, source.FileInputNode);
            return;
        }
        var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
        if (input.Status == AudioDeviceNodeCreationStatus.AccessDenied)
        {
            graph.Dispose();
            throw new UnauthorizedAccessException("microphone access is off");
        }
        if (input.Status != AudioDeviceNodeCreationStatus.Success)
        {
            graph.Dispose();
            throw new InvalidOperationException($"microphone: {input.Status}");
        }
        Begin(graph, input.DeviceInputNode);
    }

    /// <summary>The self-test's stand-in for the microphone.</summary>
    internal static string? TestInput { get; set; }

    private void Begin(AudioGraph graph, IAudioInputNode input)
    {
        _graphRate = (int)graph.EncodingProperties.SampleRate;
        _channels = (int)Math.Max(1, graph.EncodingProperties.ChannelCount);
        _output = graph.CreateFrameOutputNode();
        input.AddOutgoingConnection(_output);
        graph.QuantumStarted += (_, _) =>
        {
            try
            {
                Collect();
            }
            catch (Exception ex)
            {
                if (!_collectFailed) Helpers.AppLog.Write("reading the microphone failed", ex);
                _collectFailed = true;
            }
        };
        _graph = graph;
        IsPaused = false;
        graph.Start();
    }

    private bool _collectFailed;

    /// <summary>Pauses or resumes (what's recorded so far is kept).</summary>
    public void TogglePause()
    {
        if (_graph is not { } graph) return;
        IsPaused = !IsPaused;
        if (IsPaused) graph.Stop();
        else graph.Start();
        _level = 0;
    }

    /// <summary>Each audio quantum: its samples, made mono at 48 kHz, and the level.</summary>
    private void Collect()
    {
        if (_output is not { } output) return;
        using var frame = output.GetFrame();
        using var buffer = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Read);
        // The copy holds the samples but says it's empty: its length has to be set.
        var copy = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer);
        copy.Length = buffer.Length;
        var bytes = copy.ToArray();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
        var frames = floats.Length / _channels;
        if (frames == 0) return;

        var mono = new float[frames];
        double sum = 0;
        for (var i = 0; i < frames; i++)
        {
            float v = 0;
            for (var c = 0; c < _channels; c++) v += floats[i * _channels + c];
            v /= _channels;
            mono[i] = v;
            sum += v * v;
        }
        _level = (float)Math.Min(1, Math.Sqrt(sum / frames) * 4);

        lock (_gate)
        {
            if (_graphRate == Rate)
            {
                _samples.AddRange(mono);
                return;
            }
            // Another rate (44.1 kHz microphones): linear resampling to 48 kHz.
            var step = _graphRate / (double)Rate;
            for (double t = 0; t < frames - 1; t += step)
            {
                var k = (int)t;
                var f = (float)(t - k);
                _samples.Add(mono[k] * (1 - f) + mono[k + 1] * f);
            }
        }
    }

    /// <summary>Stops and throws the recording away.</summary>
    public Task CancelAsync()
    {
        Stop();
        lock (_gate) _samples.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Stops and encodes: the OGG Opus file, its length in seconds and its waveform (null when too short).</summary>
    public async Task<(string Path, int Seconds, byte[] Waveform)?> FinishAsync()
    {
        Stop();
        short[] pcm;
        lock (_gate)
        {
            pcm = new short[_samples.Count];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(_samples[i] * 32767f, short.MinValue, short.MaxValue);
            _samples.Clear();
        }
        return await Task.Run(() => Encode(pcm));
    }

    private void Stop()
    {
        if (_graph is not { } graph) return;
        _graph = null;
        try { graph.Stop(); }
        catch (Exception) { }
        graph.Dispose();
        _output = null;
    }

    private static readonly string DecodedFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent", "media", "decoded");

    private static string DecodedPath(string ogg) =>
        Path.Combine(DecodedFolder, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(ogg.ToLowerInvariant())))[..16] + ".wav");

    /// <summary>A WAV decoded from this OGG earlier, if there is one.</summary>
    internal static string? DecodedCopy(string ogg) => File.Exists(DecodedPath(ogg)) ? DecodedPath(ogg) : null;

    /// <summary>
    /// Decodes an OGG Opus voice note to a 48 kHz mono WAV with Concentus, for when Windows'
    /// own decoder won't play it. Kept, so it's decoded once. Null when it can't be read.
    /// </summary>
    internal static string? DecodeToWav(string ogg)
    {
        try
        {
            var samples = new List<short>();
            using (var input = File.OpenRead(ogg))
            {
                var reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(Rate, 1), input);
                while (reader.HasNextPacket)
                    if (reader.DecodeNextPacket() is { } packet) samples.AddRange(packet);
            }
            if (samples.Count == 0) return null;
            var path = DecodedPath(ogg);
            Directory.CreateDirectory(DecodedFolder);
            using var output = new BinaryWriter(File.Create(path));
            var data = samples.Count * 2;
            output.Write("RIFF"u8);
            output.Write(36 + data);
            output.Write("WAVEfmt "u8);
            output.Write(16);
            output.Write((short)1);        // PCM
            output.Write((short)1);        // mono
            output.Write(Rate);
            output.Write(Rate * 2);        // bytes per second
            output.Write((short)2);        // block align
            output.Write((short)16);       // bits
            output.Write("data"u8);
            output.Write(data);
            foreach (var sample in samples) output.Write(sample);
            return path;
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Write($"decoding a voice note failed ({ogg})", ex);
            return null;
        }
    }

    /// <summary>Encodes a 48 kHz mono 16-bit WAV (the self-test uses this without a microphone).</summary>
    internal static (string Path, int Seconds, byte[] Waveform)? EncodeWav(string wav) => Encode(ReadPcm(wav));

    private static (string Path, int Seconds, byte[] Waveform)? Encode(short[] samples)
    {
        if (samples.Length < Rate / 2) return null;   // under half a second: nothing to send

        var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "WAFluent", "media", "sent", Guid.NewGuid().ToString("N") + ".ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var file = File.Create(output))
        {
            var encoder = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000;
            var ogg = new OpusOggWriteStream(encoder, file, new OpusTags(), Rate);
            ogg.WriteSamples(samples, 0, samples.Length);
            ogg.Finish();
        }
        var seconds = Math.Max(1, (int)Math.Round(samples.Length / (double)Rate));
        return (output, seconds, Waveform(samples));
    }

    /// <summary>The 16-bit PCM samples of a WAV file's data chunk.</summary>
    private static short[] ReadPcm(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        var at = 12;   // after "RIFF....WAVE"
        while (at + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data")
            {
                size = Math.Min(size, bytes.Length - at - 8);
                var samples = new short[size / 2];
                Buffer.BlockCopy(bytes, at + 8, samples, 0, samples.Length * 2);
                return samples;
            }
            at += 8 + size + (size & 1);
        }
        return [];
    }

    /// <summary>64 levels, 0-100, like WhatsApp's: the loudness of each slice, scaled to the loudest.</summary>
    private static byte[] Waveform(short[] samples)
    {
        const int bars = 64;
        var levels = new double[bars];
        var per = Math.Max(1, samples.Length / bars);
        for (var b = 0; b < bars; b++)
        {
            double sum = 0;
            var start = b * per;
            var end = Math.Min(samples.Length, start + per);
            for (var i = start; i < end; i++) sum += (double)samples[i] * samples[i];
            levels[b] = end > start ? Math.Sqrt(sum / (end - start)) : 0;
        }
        var peak = Math.Max(1, levels.Max());
        return levels.Select(l => (byte)Math.Clamp(Math.Round(100 * Math.Pow(l / peak, 0.7)), 0, 100)).ToArray();
    }

    public async ValueTask DisposeAsync() => await CancelAsync();
}
