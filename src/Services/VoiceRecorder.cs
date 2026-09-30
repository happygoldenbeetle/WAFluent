using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace WhatsAppNative.Services;

/// <summary>
/// Voice notes: records the microphone (48 kHz mono PCM, to a temporary WAV), then encodes it
/// the way WhatsApp sends voice notes — Opus in an OGG file (Concentus) — and works out the
/// 64-bar waveform the bubbles draw. Microphone access for desktop apps must be on in
/// Windows' privacy settings.
/// </summary>
public sealed class VoiceRecorder : IAsyncDisposable
{
    private const int Rate = 48000;
    private MediaCapture? _capture;
    private StorageFile? _wav;
    private DateTime _started;

    public bool IsRecording => _capture is not null;
    public TimeSpan Elapsed => IsRecording ? DateTime.Now - _started : TimeSpan.Zero;

    /// <summary>Starts recording. Throws <see cref="UnauthorizedAccessException"/> when microphone access is off.</summary>
    public async Task StartAsync()
    {
        if (_capture is not null) return;
        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Audio,
            MediaCategory = MediaCategory.Speech,
        });
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        _wav = await folder.CreateFileAsync($"wafluent-voice-{Guid.NewGuid():N}.wav", CreationCollisionOption.ReplaceExisting);
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(Rate, 1, 16);
        await capture.StartRecordToStorageFileAsync(profile, _wav);
        _capture = capture;
        _started = DateTime.Now;
    }

    /// <summary>Stops and throws the recording away.</summary>
    public async Task CancelAsync()
    {
        await StopCaptureAsync();
        TryDelete(_wav?.Path);
        _wav = null;
    }

    /// <summary>Stops and encodes: the OGG Opus file, its length in seconds and its waveform (null when too short).</summary>
    public async Task<(string Path, int Seconds, byte[] Waveform)?> FinishAsync()
    {
        await StopCaptureAsync();
        var wav = _wav?.Path;
        _wav = null;
        if (wav is null || !File.Exists(wav)) return null;
        try
        {
            return await Task.Run(() => Encode(wav));
        }
        finally
        {
            TryDelete(wav);
        }
    }

    private async Task StopCaptureAsync()
    {
        if (_capture is not { } capture) return;
        _capture = null;
        try { await capture.StopRecordAsync(); }
        catch (Exception) { /* already stopped */ }
        capture.Dispose();
    }

    /// <summary>Encodes a 48 kHz mono 16-bit WAV (the self-test uses this without a microphone).</summary>
    internal static (string Path, int Seconds, byte[] Waveform)? EncodeWav(string wav) => Encode(wav);

    private static (string Path, int Seconds, byte[] Waveform)? Encode(string wav)
    {
        var samples = ReadPcm(wav);
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

    private static void TryDelete(string? path)
    {
        try { if (path is not null) File.Delete(path); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync() => await CancelAsync();
}
