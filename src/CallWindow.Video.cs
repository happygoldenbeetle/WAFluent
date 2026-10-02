using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Video in a call. Your camera (the camera button; Services/CallCamera.cs) shows in the corner
/// and goes to the other side; their picture (Services/CallVideoPlayer.cs) fills the card.
/// A video call starts with both; in a voice call the camera button asks the other side to
/// switch, and when they ask, a bar offers Switch. Turning your camera off keeps their picture.
/// </summary>
public sealed partial class CallWindow
{
    private readonly CallVideoPlayer _remote = new();
    private readonly SoftwareBitmapSource _previewSource = new();
    private CallCamera? _camera;
    private SoftwareBitmap? _previewNext;
    /// <summary>The call began as a video call.</summary>
    private bool _videoCall;
    /// <summary>The camera button is on / the camera is really running / what WhatsApp was last told.</summary>
    private bool _cameraWanted, _cameraRunning, _cameraTold;
    private bool _peerVideo, _settingCamera, _previewBusy, _grown, _videoStopped;
    private int _rotation;

    private void SetupVideo(bool video, bool startCamera)
    {
        _videoCall = _cameraTold = video;
        RemoteVideo.SetMediaPlayer(_remote.Player);
        PreviewImage.Source = _previewSource;
        VideoName.Text = CallerName.Text;
        VideoRequestText.Text = $"{(Controls.Redact.Enabled ? "They" : CallerName.Text)} asked to switch to a video call";
        _vm.CallVideo += OnVideo;
        _vm.CallVideoState += OnVideoState;
        CameraToggle.IsEnabled = video;
        // Your own video call shows you at once; one that's ringing waits for Accept.
        if (video && startCamera) _ = SetCameraAsync(true);
    }

    /// <summary>The call connected: the camera button works now, and WhatsApp hears where the camera stands.</summary>
    private void VideoConnected()
    {
        CameraToggle.IsEnabled = true;
        _camera?.RequestKeyFrame();
        SyncCamera();
    }

    private void StopVideo()
    {
        if (_videoStopped) return;
        _videoStopped = true;
        _vm.CallVideo -= OnVideo;
        _vm.CallVideoState -= OnVideoState;
        _cameraWanted = _cameraRunning = false;
        if (_camera is { } camera)
        {
            _camera = null;
            _ = camera.DisposeAsync();
        }
        PreviewFrame.Visibility = RemoteVideoHost.Visibility = VideoRequest.Visibility = Visibility.Collapsed;
        CallerPanel.Visibility = Visibility.Visible;
        RemoteVideo.SetMediaPlayer(null);
        _remote.Dispose();
    }

    // ───── Your camera ─────

    private void Camera_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingCamera) _ = SetCameraAsync(CameraToggle.IsChecked == true);
    }

    private async Task SetCameraAsync(bool on)
    {
        _settingCamera = true;
        CameraToggle.IsChecked = on;
        _settingCamera = false;
        _cameraWanted = on;
        if (on)
        {
            if (_cameraRunning) return;
            GrowForVideo();
            var camera = _camera ??= NewCamera();
            try
            {
                await camera.StartAsync(_ui.CameraId);
            }
            catch (Exception ex)
            {
                if (ex is not UnauthorizedAccessException) AppLog.Write("opening the camera for a call failed", ex);
                VideoNotice(ex is UnauthorizedAccessException
                    ? "Camera access is off in Windows' privacy settings."
                    : "The camera couldn't be opened. Another app may be using it.");
                await CameraOffAsync();
                return;
            }
            if (!_cameraWanted || _phase == Phase.Ended)
            {
                await CameraOffAsync();   // turned off again while it was opening
                return;
            }
            _cameraRunning = true;
            PreviewFrame.Visibility = Visibility.Visible;
            VideoNotice(null);
            camera.RequestKeyFrame();
        }
        else
        {
            await CameraOffAsync();
        }
        SyncCamera();
    }

    private async Task CameraOffAsync()
    {
        _settingCamera = true;
        CameraToggle.IsChecked = false;
        _settingCamera = false;
        _cameraWanted = _cameraRunning = false;
        PreviewFrame.Visibility = Visibility.Collapsed;
        if (_camera is { } camera)
        {
            _camera = null;
            await camera.DisposeAsync();
        }
        SyncCamera();
    }

    private CallCamera NewCamera()
    {
        var camera = new CallCamera();
        camera.Unit += OnCameraUnit;
        camera.Preview += OnPreview;
        return camera;
    }

    /// <summary>Tells WhatsApp when your camera's state isn't what it was last told (connected calls only).</summary>
    private void SyncCamera()
    {
        if (!_live || _phase != Phase.Connected || _cameraTold == _cameraRunning) return;
        _cameraTold = _cameraRunning;
        _vm.SetCallVideo(_cameraRunning);
    }

    /// <summary>One encoded picture from your camera (its thread).</summary>
    private void OnCameraUnit(byte[] unit)
    {
        if (_phase != Phase.Connected) return;
        if (_live) _vm.SendCallVideo(unit);
        else OnVideo(unit, IsKey(unit), 0);   // the sample data: you're shown back to yourself
    }

    private static bool IsKey(byte[] unit)
    {
        for (var i = 0; i + 3 < unit.Length; i++)
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1 && (unit[i + 3] & 0x1F) is 5 or 7) return true;
        return false;
    }

    /// <summary>The newest camera picture for the corner (the camera's thread); older ones not yet shown are skipped.</summary>
    private void OnPreview(SoftwareBitmap bitmap)
    {
        Interlocked.Exchange(ref _previewNext, bitmap)?.Dispose();
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_previewBusy) return;
            _previewBusy = true;
            while (Interlocked.Exchange(ref _previewNext, null) is { } next)
            {
                try
                {
                    if (!_videoStopped) await _previewSource.SetBitmapAsync(next);
                }
                catch (Exception) { }
                finally { next.Dispose(); }
            }
            _previewBusy = false;
        });
    }

    // ───── Their picture ─────

    /// <summary>One access unit of their picture (the core's reader thread).</summary>
    private void OnVideo(byte[] unit, bool key, int rotation)
    {
        if (_phase == Phase.Ended) return;
        _remote.Push(unit, key);
        if ((key && !_peerVideo) || rotation != _rotation)
            DispatcherQueue.TryEnqueue(() =>
            {
                _rotation = rotation;
                ShowRemote(true);
            });
    }

    private void ShowRemote(bool on)
    {
        if (_videoStopped) return;
        _peerVideo = on;
        RemoteVideoHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CallerPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (!on)
        {
            _remote.Reset();
            return;
        }
        GrowForVideo();
        // Phones send the picture as the sensor sees it and say how far it's turned.
        try { _remote.Player.PlaybackSession.PlaybackRotation = (MediaRotation)(_rotation & 3); }
        catch (Exception) { }
    }

    private void OnVideoState(string state)
    {
        if (_phase == Phase.Ended) return;
        switch (state)
        {
            case "keyframe":
                _camera?.RequestKeyFrame();
                break;
            case "request":
                VideoRequest.Visibility = Visibility.Visible;
                break;
            case "on":
                VideoRequest.Visibility = Visibility.Collapsed;
                _camera?.RequestKeyFrame();
                break;
            case "off":
                ShowRemote(false);
                break;
            case "declined":
            case "failed":
            case "ended":
                // The call is voice again: WhatsApp already knows your camera is off.
                VideoRequest.Visibility = Visibility.Collapsed;
                _cameraTold = false;
                ShowRemote(false);
                _ = CameraOffAsync();
                if (state != "ended") VideoNotice(state == "declined" ? "They didn't switch to video." : "Video couldn't be started.");
                break;
        }
    }

    /// <summary>Switch: your camera comes on and the call becomes a video call.</summary>
    private void VideoSwitch_Click(object sender, RoutedEventArgs e)
    {
        VideoRequest.Visibility = Visibility.Collapsed;
        // Said yes even if the camera then can't be opened: their picture still shows.
        if (_live && _phase == Phase.Connected)
        {
            _cameraTold = true;
            _vm.SetCallVideo(true);
        }
        _ = SetCameraAsync(true);
    }

    /// <summary>Not now: nothing is answered, and their request lapses.</summary>
    private void VideoDismiss_Click(object sender, RoutedEventArgs e) => VideoRequest.Visibility = Visibility.Collapsed;

    /// <summary>A line about video: under the status, or over their picture when that's showing.</summary>
    private void VideoNotice(string? text)
    {
        Note(text);
        VideoNote.Text = text ?? "";
        VideoNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Video needs more room than the voice window: grows once, towards the left (it sits at the right).</summary>
    private void GrowForVideo()
    {
        if (_grown) return;
        _grown = true;
        var scale = WindowHelper.Scale(this);
        var size = AppWindow.Size;
        var (width, height) = (Math.Max(size.Width, (int)(760 * scale)), Math.Max(size.Height, (int)(600 * scale)));
        if (width == size.Width && height == size.Height) return;
        var work = WindowHelper.WorkArea(this);
        var x = Math.Clamp(AppWindow.Position.X - (width - size.Width), work.X, Math.Max(work.X, work.X + work.Width - width));
        var y = Math.Clamp(AppWindow.Position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>The cameras, under the microphones in the "…" menu.</summary>
    private async Task AddCamerasAsync(MenuFlyout menu)
    {
        IReadOnlyList<(string Id, string Name)> cameras = [];
        try { cameras = await CallCamera.CamerasAsync(); }
        catch (Exception) { }
        if (cameras.Count == 0) return;
        var chosen = cameras.Any(c => c.Id == _ui.CameraId) ? _ui.CameraId : "";
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Camera", IsEnabled = false });
        foreach (var (id, name) in cameras.Prepend((Id: "", Name: "Windows' default")))
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "camera", IsChecked = id == chosen };
            item.Click += async (_, _) =>
            {
                _ui.CameraId = id;
                _ui.Save();
                if (!_cameraRunning) return;
                // The other camera, without telling WhatsApp it went off in between.
                _cameraRunning = false;
                if (_camera is { } camera)
                {
                    _camera = null;
                    await camera.DisposeAsync();
                }
                await SetCameraAsync(true);
            };
            menu.Items.Add(item);
        }
    }

    private void LogVideo()
    {
        if (_peerVideo && !_videoStopped) AppLog.Write($"call video: their picture: {_remote.Report()}");
    }

    /// <summary>What the player has done so far (the self-test).</summary>
    internal string VideoReport()
    {
        var session = _remote.Player.PlaybackSession;
        return $"given={_remote.Played} size={session.NaturalVideoWidth}x{session.NaturalVideoHeight} state={session.PlaybackState} " +
               $"position={session.Position.TotalSeconds:0.0}s showing={_peerVideo} preview={PreviewFrame.Visibility} | {_remote.Report()}";
    }
}
