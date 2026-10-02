using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WhatsAppNative.Helpers;

namespace WhatsAppNative.Services;

/// <summary>
/// Your camera in a video call: its pictures, scaled to 360 lines and 15 a second, go through
/// <see cref="H264Encoder"/> and come out of <see cref="Unit"/> as H.264 access units for
/// WhatsApp, and out of <see cref="Preview"/> as bitmaps for the corner of the call window.
/// Camera access for desktop apps must be on in Windows' privacy settings.
/// </summary>
public sealed class CallCamera : IAsyncDisposable
{
    public const int FramesPerSecond = 15;
    private const int Lines = 360;
    private const int Bitrate = 600_000;

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private H264Encoder? _encoder;
    private CancellationTokenSource? _test;
    private byte[] _picture = [];
    private long _last = -1000;
    private bool _failed, _off = true, _keyFrame = true;

    /// <summary>One encoded picture (an Annex-B access unit). Raised on the camera's thread.</summary>
    public event Action<byte[]>? Unit;

    /// <summary>The picture as a bitmap to show (BGRA); whoever takes it disposes it. The camera's thread.</summary>
    public event Action<SoftwareBitmap>? Preview;

    /// <summary>The self-test: made-up moving pictures instead of the camera (which stays off).</summary>
    internal static Func<int, int, int, byte[]>? TestPictures { get; set; }

    public bool IsOn => _reader is not null || _test is not null;

    /// <summary>The cameras Windows has: (device id, name).</summary>
    public static async Task<IReadOnlyList<(string Id, string Name)>> CamerasAsync()
    {
        var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
        return found.Where(d => d.IsEnabled).Select(d => (d.Id, d.Name)).ToList();
    }

    /// <summary>
    /// Turns the camera on (<paramref name="deviceId"/>: null or empty for Windows' default).
    /// Throws <see cref="UnauthorizedAccessException"/> when camera access is off.
    /// </summary>
    public async Task StartAsync(string? deviceId)
    {
        if (IsOn) return;
        _keyFrame = true;
        _off = false;
        if (TestPictures is { } pictures)
        {
            var cancel = _test = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                for (var n = 0; !cancel.IsCancellationRequested; n++)
                {
                    Encode(pictures(640, Lines, n), 640, Lines, null);
                    await Task.Delay(1000 / FramesPerSecond);
                }
            });
            return;
        }

        var capture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        };
        if (!string.IsNullOrEmpty(deviceId)) settings.VideoDeviceId = deviceId;
        try
        {
            try
            {
                await capture.InitializeAsync(settings);
            }
            catch (Exception) when (!string.IsNullOrEmpty(deviceId))
            {
                // The chosen camera is gone: the default one.
                capture.Dispose();
                capture = new MediaCapture();
                settings.VideoDeviceId = "";
                await capture.InitializeAsync(settings);
            }
            var sources = capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
            var source = sources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                         ?? sources.FirstOrDefault()
                         ?? throw new InvalidOperationException("no camera was found");
            // The smallest format that's at least 640 wide and 15 a second: less to scale down.
            var format = source.SupportedFormats
                .Where(f => f.FrameRate.Denominator > 0 && f.FrameRate.Numerator / (double)f.FrameRate.Denominator >= FramesPerSecond - 0.5)
                .OrderBy(f => f.VideoFormat.Width >= 640 ? 0 : 1)
                .ThenBy(f => Math.Abs((int)f.VideoFormat.Width - 640))
                .ThenBy(f => Math.Abs(f.FrameRate.Numerator / (double)f.FrameRate.Denominator - 30))
                .FirstOrDefault();
            if (format is not null)
            {
                try { await source.SetFormatAsync(format); }
                catch (Exception) { }   // whatever it's set to works too
            }
            var (cameraWidth, cameraHeight) = (source.CurrentFormat.VideoFormat.Width, source.CurrentFormat.VideoFormat.Height);
            if (cameraWidth == 0 || cameraHeight == 0) (cameraWidth, cameraHeight) = (640, 360);
            var height = (int)Math.Min(Lines, cameraHeight) & ~1;
            var width = (int)Math.Round(height * cameraWidth / (double)cameraHeight / 2) * 2;
            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Nv12, new BitmapSize { Width = (uint)width, Height = (uint)height });
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrame;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.Dispose();
                throw new InvalidOperationException($"camera: {status}");
            }
            _capture = capture;
            _reader = reader;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>The next picture is a whole one (the call just connected, or the other side asked).</summary>
    public void RequestKeyFrame()
    {
        _keyFrame = true;
        _encoder?.RequestKeyFrame();
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs e)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap) return;
            // The camera gives 30 a second; the call takes 15.
            var now = _clock.ElapsedMilliseconds;
            if (now - _last < 1000 / FramesPerSecond - 8) return;
            _last = now;

            var (width, height) = (bitmap.PixelWidth & ~1, bitmap.PixelHeight & ~1);
            if (_picture.Length != width * height * 3 / 2) _picture = new byte[width * height * 3 / 2];
            using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                // The planes come with their own row lengths: copy them tight, Y then UV.
                var data = MemoryBuffers.Bytes(reference);
                var (y, uv) = (buffer.GetPlaneDescription(0), buffer.GetPlaneDescription(1));
                for (var row = 0; row < height; row++)
                    data.Slice(y.StartIndex + row * y.Stride, width).CopyTo(_picture.AsSpan(row * width));
                for (var row = 0; row < height / 2; row++)
                    data.Slice(uv.StartIndex + row * uv.Stride, width).CopyTo(_picture.AsSpan(width * height + row * width));
            }
            Encode(_picture, width, height, bitmap);
        }
        catch (Exception ex)
        {
            if (!_failed) AppLog.Write("a camera picture couldn't be used", ex);
            _failed = true;
        }
    }

    private void Encode(byte[] nv12, int width, int height, SoftwareBitmap? bitmap)
    {
        byte[]? unit;
        lock (_gate)
        {
            if (_off) return;
            if (_encoder is null || _encoder.Width != width || _encoder.Height != height)
            {
                _encoder?.Dispose();
                _encoder = new H264Encoder(width, height, FramesPerSecond, Bitrate);
            }
            if (_keyFrame)
            {
                _keyFrame = false;
                _encoder.RequestKeyFrame();
            }
            unit = _encoder.Encode(nv12);
        }
        if (unit is not null) Unit?.Invoke(unit);
        if (Preview is not { } preview) return;
        if (bitmap is not null)
        {
            preview(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
        }
        else
        {
            using var made = SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(nv12),
                                                                 BitmapPixelFormat.Nv12, width, height);
            preview(SoftwareBitmap.Convert(made, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _off = true;
        _test?.Cancel();
        _test = null;
        var (reader, capture) = (_reader, _capture);
        _reader = null;
        _capture = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrame;
            try { await reader.StopAsync(); }
            catch (Exception) { }
            reader.Dispose();
        }
        capture?.Dispose();
        lock (_gate)
        {
            _encoder?.Dispose();
            _encoder = null;
        }
    }
}
