using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Recording voice notes, like WhatsApp Desktop: the mic turns the composer into a recording
/// bar (a red dot, the time, delete and send). Send (or Enter) encodes it as OGG Opus and
/// sends it as a voice note; delete (or Esc) throws it away, as does switching chats.
/// Recordings stop at 15 minutes.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(15);
    private VoiceRecorder? _recorder;
    private DispatcherTimer? _recordingTimer;
    private bool _recordingWired;
    private Chat? _recordingChat;
    private DateTime _recordingPresenceAt;

    private async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedChat is null || _recorder is not null) return;
        WireRecording();
        var recorder = new VoiceRecorder();
        try
        {
            await recorder.StartAsync(_ui.MicrophoneId);
        }
        catch (UnauthorizedAccessException)
        {
            ShowToast(false, "Microphone access is off: Settings › Privacy › Microphone › Let desktop apps access your microphone.");
            return;
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Write("starting a voice recording failed", ex);
            ShowToast(false, "No microphone was found.");
            return;
        }
        _recorder = recorder;
        _recordingChat = ViewModel.SelectedChat;
        SendRecordingPresence(true);
        AudioPlayback.Stop();   // don't record a playing voice note
        RecordingTime.Text = "0:00";
        RecordingWave.Clear();
        RecordingPauseIcon.Glyph = "\uE769";
        RecordingBar.Visibility = Visibility.Visible;
        _recordingTimer!.Start();
        RecordingSend.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The chevron beside the mic: a list of microphones, opening upwards, the one in use
    /// ticked. "Default" follows whatever Windows uses. The choice is kept.
    /// </summary>
    private async void MicDevice_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<(string Id, string Name)> devices;
        try { devices = await VoiceRecorder.MicrophonesAsync(); }
        catch (Exception ex)
        {
            Helpers.AppLog.Write("listing microphones failed", ex);
            devices = [];
        }
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
        var known = devices.Any(d => d.Id == _ui.MicrophoneId);
        void Add(string id, string name)
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "microphone", IsChecked = id == (known ? _ui.MicrophoneId : "") };
            item.Click += (_, _) =>
            {
                _ui.MicrophoneId = id;
                _ui.Save();
            };
            menu.Items.Add(item);
        }
        Add("", "Default microphone");
        if (devices.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (id, name) in devices) Add(id, name);
        if (devices.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "No microphones found", IsEnabled = false });
        menu.ShowAt(MicDeviceButton);
    }

    private void WireRecording()
    {
        if (_recordingWired) return;
        _recordingWired = true;
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _recordingTimer.Tick += (_, _) =>
        {
            if (_recorder is not { } r) return;
            var t = r.Elapsed;
            RecordingTime.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            RecordingDot.Opacity = r.IsPaused || DateTime.Now.Millisecond < 500 ? 1 : 0.35;   // blinks while recording
            if (!r.IsPaused) RecordingWave.Push(r.Level);
            if (!r.IsPaused && DateTime.Now - _recordingPresenceAt > TimeSpan.FromSeconds(10)) SendRecordingPresence(true);
            if (t >= MaxRecording) _ = FinishRecordingAsync(send: true);
        };
        RecordingBar.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { e.Handled = true; _ = FinishRecordingAsync(send: false); }
            else if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = FinishRecordingAsync(send: true); }
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat) && _recorder is not null) _ = FinishRecordingAsync(send: false);
        };
    }

    private void RecordingPause_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is not { } r) return;
        r.TogglePause();
        SendRecordingPresence(!r.IsPaused);
        RecordingPauseIcon.Glyph = r.IsPaused ? "\uE720" : "\uE769";   // mic to carry on, pause to stop for a moment
        ToolTipService.SetToolTip(RecordingPause, r.IsPaused ? "Resume" : "Pause");
    }

    /// <summary>"recording audio…" in the chat you're recording for (even after you've switched away).</summary>
    private void SendRecordingPresence(bool recording)
    {
        if (_recordingChat is not { } chat) return;
        _recordingPresenceAt = recording ? DateTime.Now : default;
        ViewModel.SetRecording(chat, recording);
    }

    private void RecordingSend_Click(object sender, RoutedEventArgs e) => _ = FinishRecordingAsync(send: true);

    private void RecordingDelete_Click(object sender, RoutedEventArgs e) => _ = FinishRecordingAsync(send: false);

    private async Task FinishRecordingAsync(bool send)
    {
        if (_recorder is not { } recorder) return;
        _recorder = null;
        SendRecordingPresence(false);
        _recordingChat = null;
        _recordingTimer?.Stop();
        RecordingBar.Visibility = Visibility.Collapsed;
        ComposerBox.Focus(FocusState.Programmatic);
        if (!send)
        {
            await recorder.CancelAsync();
            return;
        }
        try
        {
            if (await recorder.FinishAsync() is not var (path, seconds, waveform))
            {
                ShowToast(false, "Too short to send.");
                return;
            }
            ViewModel.SendVoice(path, seconds, waveform);
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            Helpers.AppLog.Write("encoding a voice note failed", ex);
            ShowToast(false, "The voice message couldn't be saved.");
        }
    }
}
