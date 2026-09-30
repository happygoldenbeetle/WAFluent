using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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

    private async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedChat is null || _recorder is not null) return;
        WireRecording();
        var recorder = new VoiceRecorder();
        try
        {
            await recorder.StartAsync();
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
        AudioPlayback.Stop();   // don't record a playing voice note
        RecordingTime.Text = "0:00";
        RecordingBar.Visibility = Visibility.Visible;
        _recordingTimer!.Start();
        RecordingSend.Focus(FocusState.Programmatic);
    }

    private void WireRecording()
    {
        if (_recordingWired) return;
        _recordingWired = true;
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordingTimer.Tick += (_, _) =>
        {
            if (_recorder is not { } r) return;
            var t = r.Elapsed;
            RecordingTime.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            RecordingDot.Opacity = t.Milliseconds < 500 ? 1 : 0.35;   // blinks, like WhatsApp's
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

    private void RecordingSend_Click(object sender, RoutedEventArgs e) => _ = FinishRecordingAsync(send: true);

    private void RecordingDelete_Click(object sender, RoutedEventArgs e) => _ = FinishRecordingAsync(send: false);

    private async Task FinishRecordingAsync(bool send)
    {
        if (_recorder is not { } recorder) return;
        _recorder = null;
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
