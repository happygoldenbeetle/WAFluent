using Microsoft.UI.Xaml;
using WhatsAppNative.Controls;
using WhatsAppNative.Models;
using WhatsAppNative.Services;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

/// <summary>
/// Calls: the phone button in a chat's header (and Voice in contact info) calls that person,
/// and someone calling you opens the call window ringing, with Windows' incoming-call
/// notification (Accept / Decline there work too, the app in the tray included). One call at a
/// time: a second one keeps ringing on your phone. Voice only for now; group and video calls say so.
/// </summary>
public sealed partial class MainWindow
{
    private CallWindow? _call;

    private void SetupCalls()
    {
        ViewModel.CallChanged += OnCall;
        Notifications.CallAnswered += (callId, accept) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_call is not { } call || call.CallId != callId) return;
            if (accept)
            {
                call.Accept();
                call.Activate();
            }
            else
            {
                call.Decline();
            }
        });
        Closed += (_, _) => _call?.Close();
        SetupCallSelfTests();
    }

    private void StartCall(bool video)
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        if (_call is not null)
        {
            _call.Activate();   // one call at a time
            return;
        }
        if (ViewModel.IsLive)
        {
            var problem = chat.IsGroup ? "Group calls aren't available yet."
                : video ? "Video calls aren't available yet. Voice calls are."
                : ViewModel.IsSelf(chat) ? "You can't call yourself."
                : chat.IsBlocked ? "Unblock this contact to call them."
                : ViewModel.State != ConnectionState.Connected ? "You're not connected to WhatsApp right now."
                : null;
            if (problem is not null)
            {
                ShowToast(false, problem);
                return;
            }
        }
        OpenCall(chat, null);
    }

    private void OpenCall(Chat chat, CallDto? incoming)
    {
        _call = new CallWindow(chat, Root.RequestedTheme, this, ViewModel, _ui, incoming);
        var window = _call;
        window.Closed += (_, _) => { if (_call == window) _call = null; };
        window.Activate();
    }

    private void OnCall(CallDto call)
    {
        if (call.State == "ringing")
        {
            if (_call is not null || ViewModel.ChatById(call.ChatId) is not { } chat) return;
            OpenCall(chat, call);
            Notifications.ShowCall(call.CallId, Redact.Enabled ? "WhatsApp" : chat.Name, call.Video, Redact.Enabled ? null : chat.AvatarPath);
            return;
        }
        if (call.State == "ended") Notifications.ClearCall(call.CallId);
        _call?.Apply(call);
    }

    /// <summary>
    /// WAFLUENT_SELFTEST=call-out | call-in: the call window as it looks calling and being called
    /// (sample data: nothing is placed, nothing rings). call-audio: WAFLUENT_TEST_WAV through the
    /// call's microphone path and back out through its player, silently, with the counts written
    /// to %TEMP%\wafluent-selftest.txt.
    /// </summary>
    private void SetupCallSelfTests()
    {
#if DEBUG
        var test = Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST");
        if (test is not ("call-out" or "call-in" or "call-audio")) return;
        Messages.Loaded += async (_, _) =>
        {
            await Task.Delay(2000);
            if (ViewModel.SelectedChat is not { } chat) return;
            switch (test)
            {
                case "call-out":
                    OpenCall(chat, null);
                    break;
                case "call-in":
                    OpenCall(chat, new CallDto("selftest", chat.Id, "ringing", Video: false, Outgoing: false));
                    break;
                case "call-audio":
                    var lines = new List<string>();
                    try
                    {
                        CallAudio.Silent = true;
                        CallAudio.TestInput = Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV");
                        using var audio = new CallAudio();
                        double loudest = 0;
                        var sizes = new HashSet<int>();
                        audio.Frame += pcm =>
                        {
                            sizes.Add(pcm.Length);
                            loudest = Math.Max(loudest, audio.MicLevel);
                            audio.Play(pcm, opus: false);   // straight back out
                        };
                        await audio.OpenAsync();
                        audio.Tone = CallTone.Ringback;
                        await Task.Delay(700);
                        audio.Tone = CallTone.None;
                        await audio.OpenMicrophoneAsync(null);
                        await Task.Delay(3000);
                        lines.Add($"frames={audio.FramesCaptured} sizes={string.Join(",", sizes)} loudest={loudest:0.00}");
                        lines.Add($"played={audio.SamplesPlayed} peer={audio.PeerLevel:0.00}");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"failed: {ex}");
                    }
                    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
                    break;
            }
        };
#endif
    }
}
