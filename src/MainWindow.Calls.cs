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
/// time: a second one keeps ringing on your phone. Voice and video with one person; group calls say
/// they aren't available yet.
/// </summary>
public sealed partial class MainWindow
{
    private CallWindow? _call;

    private void SetupCalls()
    {
        ViewModel.CallChanged += OnCall;
        // Missed calls count from the first time this runs, not from the whole history.
        if (_ui.CallsSeenAt == 0)
        {
            _ui.CallsSeenAt = DateTimeOffset.Now.ToUnixTimeSeconds();
            _ui.Save();
        }
        ViewModel.UseCallsSeen(_ui.CallsSeenAt);
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
        if (ViewModel.SelectedChat is { } chat) StartCallWith(chat, video);
    }

    /// <summary>Calls <paramref name="chat"/> (the open chat's buttons, or someone picked on the Calls page).</summary>
    private void StartCallWith(Chat chat, bool video)
    {
        if (_call is not null)
        {
            _call.Activate();   // one call at a time
            return;
        }
        if (ViewModel.IsLive)
        {
            var problem = chat.IsGroup ? "Group calls aren't available yet."
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
        OpenCall(chat, null, video);
    }

    private void OpenCall(Chat chat, CallDto? incoming, bool video = false)
    {
        _call = new CallWindow(chat, Root.RequestedTheme, this, ViewModel, _ui, incoming, video);
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
    /// to %TEMP%\wafluent-selftest.txt. call-h264: made-up pictures through the encoder.
    /// call-video: a video call on the sample data with made-up pictures for the camera (the real
    /// one stays off), encoded, decoded and shown, with the player's state written to that file.
    /// </summary>
    private void SetupCallSelfTests()
    {
#if DEBUG
        var test = Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST");
        if (test is null || !(test.StartsWith("call-") || test.StartsWith("calls"))) return;
        if (test == "call-link-live")
        {
            // On the linked account (no --sample): both kinds of link, their shape to the file. Nobody is rung.
            var got = new List<string>();
            ViewModel.CallLinkReceived += (url, video) =>
            {
                var cut = url.LastIndexOf('/') + 1;
                got.Add($"video={video} {url[..cut]}<{url.Length - cut} characters>");
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), got);
            };
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 40 && ViewModel.State != ConnectionState.Connected; i++) await Task.Delay(1000);
                await Task.Delay(4000);
                DispatcherQueue.TryEnqueue(() => ViewModel.CreateCallLink(true));
                await Task.Delay(5000);
                DispatcherQueue.TryEnqueue(() => ViewModel.CreateCallLink(false));
            });
            return;
        }
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
                case "calls" or "calls-info" or "calls-number" or "calls-new" or "calls-addfav" or "calls-fav" or "calls-search":
                    // The Calls page on the sample data, and its pages.
                    if (test is "calls-fav" or "calls-info")
                        foreach (var person in ViewModel.ForwardTargets().Where(c => !c.IsGroup).Take(2)) person.IsFavourite = true;   // not saved
                    Nav.SelectedItem = Nav.MenuItems[1];
                    await Task.Delay(600);
                    switch (test)
                    {
                        case "calls-info": CallsList.SelectedIndex = 1; break;
                        case "calls-number": ShowCallsPage(CallsPage.Number); CallNumberBox.Text = "+92 300 1234567"; break;
                        case "calls-new": ShowCallsPage(CallsPage.NewCall); break;
                        case "calls-addfav": OpenAddFavourites(fromFavourites: false); await Task.Delay(300); CallsPickList.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(0, 2)); break;
                        case "calls-fav": FavouritesEdit.IsChecked = true; ShowCallsPage(CallsPage.Favourites); break;
                        case "calls-search": CallsSearch.Text = "a"; break;
                    }
                    break;
                case "calls-badge":
                    // The rail's missed-call count, as if the Calls page had never been looked at (nothing is saved).
                    ViewModel.UseCallsSeen(1);
                    ViewModel.LoadCalls();
                    break;
                case "call-video":
                    CallCamera.TestPictures = TestPicture;
                    OpenCall(chat, null, video: true);
                    await Task.Delay(7500);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), _call?.VideoReport() ?? "no call window");
                    break;
                case "call-h264":
                    var report = new List<string>();
                    try
                    {
                        using var encoder = new H264Encoder(640, 360, 15, 500_000);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        for (var n = 0; n < 45; n++)
                        {
                            if (n == 20) encoder.RequestKeyFrame();
                            var unit = encoder.Encode(TestPicture(640, 360, n));
                            report.Add($"{n}: {(unit is null ? "nothing" : $"{unit.Length} bytes, NALs {string.Join(",", NalTypes(unit))}")}");
                        }
                        report.Add($"45 pictures in {watch.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        report.Add($"failed: {ex}");
                    }
                    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), report);
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

#if DEBUG
    /// <summary>A made-up NV12 picture that moves with <paramref name="n"/>: stripes drifting across colour bands.</summary>
    internal static byte[] TestPicture(int width, int height, int n)
    {
        var picture = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                picture[y * width + x] = (byte)(((x + n * 6) / 40 + y / 40) % 2 == 0 ? 60 + y * 150 / height : 200);
        var chroma = width * height;
        for (var y = 0; y < height / 2; y++)
            for (var x = 0; x < width / 2; x++)
            {
                picture[chroma + y * width + x * 2] = (byte)(90 + x * 80 / (width / 2));       // U
                picture[chroma + y * width + x * 2 + 1] = (byte)(200 - y * 120 / (height / 2)); // V
            }
        return picture;
    }

    /// <summary>The NAL unit types in an Annex-B access unit (7 SPS, 8 PPS, 5 a whole picture, 1 a partial one).</summary>
    internal static List<int> NalTypes(byte[] unit)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < unit.Length; i++)
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1) types.Add(unit[i + 3] & 0x1F);
        return types;
    }
#endif
}
