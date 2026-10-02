using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WhatsAppNative.Controls;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

/// <summary>
/// A WhatsApp voice call. Yours: "Calling…", "Ringing…" (with a ringback tone) once it rings
/// there, then the timer when they pick up. Theirs: Decline / Accept (Windows' incoming-call
/// notification rings alongside, Services/Notifications.cs). Connected, the microphone goes to
/// the call and their voice to the speakers (Services/CallAudio.cs), the bars follow the two
/// voices, and Mute, the microphone picker and hang up work. With the sample data (no phone
/// linked) it only pretends: it "connects" after a moment and the bars move by themselves.
/// </summary>
public sealed partial class CallWindow : Window
{
    private enum Phase { Ringing, Calling, Connecting, Connected, Ended }

    private const int BarCount = 21;
    private const int DotBars = 3;        // flat dots at each end of the wave
    private const double MinBar = 3, MaxBar = 28;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _heights = new double[BarCount];
    private readonly Random _random = new();
    private readonly DispatcherQueueTimer _ringTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _waveTimer;
    private readonly MainViewModel _vm;
    private readonly UiSettings _ui;
    private readonly CallAudio _audio = new();
    private readonly bool _live, _outgoing;
    private Phase _phase;
    private DateTime _connectedAt;
    private double _loudness;

    /// <summary>WhatsApp's id for this call (empty until yours has been placed).</summary>
    public string CallId { get; private set; } = "";

    internal CallAudio Audio => _audio;

    /// <param name="incoming">Someone calling you; null when you're the one calling.</param>
    public CallWindow(Chat chat, ElementTheme theme, Window owner, MainViewModel vm, UiSettings ui, CallDto? incoming)
    {
        InitializeComponent();
        if (theme != ElementTheme.Default) Root.RequestedTheme = theme;
        _vm = vm;
        _ui = ui;
        _live = vm.IsLive;
        _outgoing = incoming is null;

        Title = Redact.Enabled ? "WhatsApp call" : $"{chat.Name} – WhatsApp call";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CallTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowHelper.ApplyCaptionColors(this, Root.ActualTheme);
        Root.ActualThemeChanged += (_, _) => WindowHelper.ApplyCaptionColors(this, Root.ActualTheme);
        PlaceNextTo(owner, 480, 540);
        WindowHelper.SetMinimumSize(this, 400, 460);

        CallerAvatar.DisplayName = chat.Name;
        CallerAvatar.Source = chat.AvatarPath;
        CallerName.Text = chat.Name;
        BuildWave();

        _ringTimer = DispatcherQueue.CreateTimer();
        _ringTimer.Interval = TimeSpan.FromSeconds(2.5);
        _ringTimer.IsRepeating = false;
        _ringTimer.Tick += (_, _) => Connected();

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();

        _waveTimer = DispatcherQueue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(80);
        _waveTimer.Tick += (_, _) => AnimateWave();

        _audio.Frame += OnFrame;
        _vm.CallAudio += OnAudio;
        Closed += OnClosed;
        Root.Loaded += (_, _) => FocusRest.Focus(FocusState.Programmatic);

        if (incoming is not null)
        {
            _phase = Phase.Ringing;
            CallId = incoming.CallId;
            CallStatus.Text = incoming.Video ? "Incoming video call\nAccept answers with voice" : "Incoming voice call";
            RingButtons.Visibility = Visibility.Visible;
            CallButtons.Visibility = Visibility.Collapsed;
        }
        else
        {
            _phase = Phase.Calling;
            CallStatus.Text = "Calling…";
            if (_live)
            {
                _vm.StartCall(chat, video: false);
                _ = OpenSoundAsync();
            }
            else
            {
                _ringTimer.Start();
            }
        }
    }

    // ───── What WhatsApp says about the call ─────

    public void Apply(CallDto call)
    {
        if (_phase == Phase.Ended || call.Outgoing != _outgoing) return;
        if (CallId.Length > 0 && call.CallId.Length > 0 && call.CallId != CallId) return;   // another call
        if (call.CallId.Length > 0) CallId = call.CallId;
        switch (call.State)
        {
            case "calling":
                _phase = Phase.Calling;
                CallStatus.Text = "Ringing…";
                _audio.Tone = CallTone.Ringback;
                break;
            case "connecting":
                Connecting();
                break;
            case "connected":
                Connected();
                break;
            case "ended":
                Finish(call.Reason switch
                {
                    "declined" => "Call declined",
                    "noAnswer" => "No answer",
                    "missed" => "Missed call",
                    "elsewhere" => "Answered on another device",
                    "failed" => call.Detail ?? "The call couldn't connect.",
                    _ => "Call ended",
                }, slow: call.Reason == "failed");
                break;
        }
    }

    private void Connecting()
    {
        if (_phase is Phase.Connected or Phase.Ended) return;
        _phase = Phase.Connecting;
        Notifications.ClearCall(CallId);
        RingButtons.Visibility = Visibility.Collapsed;
        CallButtons.Visibility = Visibility.Visible;
        CallStatus.Text = "Connecting…";
    }

    private void Connected()
    {
        if (_phase is Phase.Connected or Phase.Ended) return;
        Connecting();
        _phase = Phase.Connected;
        _audio.Tone = CallTone.None;
        _connectedAt = DateTime.Now;
        UpdateClock();
        _clockTimer.Start();
        _waveTimer.Start();
        if (!_live) return;
        if (MuteToggle.IsChecked == true) _vm.MuteCall(true);
        _ = OpenMicrophoneAsync();
    }

    /// <summary>The call is over: say why, then close.</summary>
    private void Finish(string text, bool slow = false)
    {
        if (_phase == Phase.Ended) return;
        _phase = Phase.Ended;
        StopTimers();
        _audio.Dispose();
        Notifications.ClearCall(CallId);
        CallStatus.Text = text;
        CallNote.Visibility = Visibility.Collapsed;
        RingButtons.IsHitTestVisible = CallButtons.IsHitTestVisible = false;
        RingButtons.Opacity = CallButtons.Opacity = 0.5;
        foreach (var bar in _bars) bar.Height = MinBar;
        var close = DispatcherQueue.CreateTimer();
        close.Interval = TimeSpan.FromMilliseconds(slow ? 3500 : 1400);
        close.IsRepeating = false;
        close.Tick += (_, _) => Close();
        close.Start();
    }

    // ───── Sound ─────

    private async Task OpenSoundAsync()
    {
        try
        {
            await _audio.OpenAsync();
            if (_phase == Phase.Ended) _audio.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("opening call audio failed", ex);
            Note("The speakers couldn't be opened: you won't hear the call.");
        }
    }

    private async Task OpenMicrophoneAsync()
    {
        try
        {
            if (!_audio.IsOpen) await _audio.OpenAsync();
            if (_phase != Phase.Connected)
            {
                if (_phase == Phase.Ended) _audio.Dispose();
                return;
            }
            await _audio.OpenMicrophoneAsync(_ui.MicrophoneId);
            Note(null);
        }
        catch (UnauthorizedAccessException)
        {
            Note("Microphone access is off in Windows' privacy settings: they can't hear you.");
        }
        catch (Exception ex)
        {
            AppLog.Write("opening the microphone for a call failed", ex);
            Note("The microphone couldn't be opened: they can't hear you.");
        }
    }

    private void Note(string? text)
    {
        CallNote.Text = text ?? "";
        CallNote.Visibility = text is null || _phase == Phase.Ended ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The microphone, 60 ms at a time (the audio thread).</summary>
    private void OnFrame(byte[] pcm)
    {
        if (_phase == Phase.Connected) _vm.SendCallAudio(pcm);
    }

    /// <summary>Their voice (the core's reader thread).</summary>
    private void OnAudio(byte[] data, bool opus)
    {
        if (_phase == Phase.Connected) _audio.Play(data, opus);
    }

    // ───── Buttons ─────

    /// <summary>Answers (the button, or Accept on Windows' notification).</summary>
    public void Accept()
    {
        if (_phase != Phase.Ringing) return;
        Connecting();
        if (_live)
        {
            _vm.AcceptCall(CallId);
            _ = OpenSoundAsync();
        }
        else
        {
            Connected();
        }
    }

    public void Decline()
    {
        if (_phase != Phase.Ringing) return;
        _vm.RejectCall(CallId);
        Finish("Call declined");
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Accept();

    private void Decline_Click(object sender, RoutedEventArgs e) => Decline();

    private void Mute_Changed(object sender, RoutedEventArgs e)
    {
        var muted = MuteToggle.IsChecked == true;
        MuteIcon.Glyph = muted ? Glyphs.MicOff : Glyphs.Mic;
        _audio.Muted = muted;
        if (_phase == Phase.Connected) _vm.MuteCall(muted);
    }

    /// <summary>Which microphone the call uses (the same choice as voice notes).</summary>
    private async void More_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<(string Id, string Name)> microphones = [];
        try { microphones = await VoiceRecorder.MicrophonesAsync(); }
        catch (Exception) { }
        var chosen = microphones.Any(m => m.Id == _ui.MicrophoneId) ? _ui.MicrophoneId : "";
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Microphone", IsEnabled = false });
        foreach (var (id, name) in microphones.Prepend((Id: "", Name: "Windows' default")))
        {
            var item = new RadioMenuFlyoutItem { Text = name, GroupName = "microphone", IsChecked = id == chosen };
            item.Click += async (_, _) =>
            {
                _ui.MicrophoneId = id;
                _ui.Save();
                if (_live && _phase == Phase.Connected) await OpenMicrophoneAsync();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt((FrameworkElement)sender);
    }

    private void EndCall_Click(object sender, RoutedEventArgs e)
    {
        if (_phase == Phase.Ended) return;
        _vm.EndCall();
        Finish("Call ended");
    }

    /// <summary>Closing the window hangs up (or declines a call that's still ringing).</summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        if (_phase == Phase.Ringing) _vm.RejectCall(CallId);
        else if (_phase != Phase.Ended) _vm.EndCall();
        _phase = Phase.Ended;
        StopTimers();
        _vm.CallAudio -= OnAudio;
        _audio.Frame -= OnFrame;
        _audio.Dispose();
        Notifications.ClearCall(CallId);
    }

    // ───── The clock and the wave ─────

    private void BuildWave()
    {
        var green = new SolidColorBrush(Color.FromArgb(255, 0x25, 0xD3, 0x66));
        for (var i = 0; i < BarCount; i++)
        {
            _heights[i] = MinBar;
            _bars[i] = new Rectangle
            {
                Width = 3, Height = MinBar, RadiusX = 1.5, RadiusY = 1.5,
                Fill = green, VerticalAlignment = VerticalAlignment.Center,
            };
            Wave.Children.Add(_bars[i]);
        }
    }

    private void UpdateClock()
    {
        var elapsed = DateTime.Now - _connectedAt;
        CallStatus.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    /// <summary>
    /// The bars follow whoever is speaking (the louder of the two voices), shaped by a
    /// centre-heavy envelope. With the sample data: a slowly wandering, speech-like loudness.
    /// </summary>
    private void AnimateWave()
    {
        if (_live)
        {
            _loudness = Math.Clamp(Math.Max(_audio.PeerLevel, _audio.MicLevel * 0.8), 0, 1);
        }
        else
        {
            _loudness = Math.Clamp(_loudness + (_random.NextDouble() - 0.45) * 0.35, 0, 1);
            if (_random.NextDouble() < 0.04) _loudness = 0;   // brief pauses between words
        }

        const int active = BarCount - 2 * DotBars;
        for (var i = 0; i < BarCount; i++)
        {
            double target = MinBar;
            var j = i - DotBars;
            if (j is >= 0 and < active)
            {
                var envelope = Math.Sin(Math.PI * (j + 0.5) / active);
                target = MinBar + (MaxBar - MinBar) * _loudness * envelope * (0.45 + 0.55 * _random.NextDouble());
            }
            _heights[i] += (target - _heights[i]) * 0.55;
            _bars[i].Height = _heights[i];
        }
    }

    private void StopTimers()
    {
        _ringTimer.Stop();
        _clockTimer.Stop();
        _waveTimer.Stop();
    }

    /// <summary>Opens over the right side of the main window, like the original app.</summary>
    private void PlaceNextTo(Window owner, int width, int height)
    {
        var scale = WindowHelper.Scale(this);
        var work = WindowHelper.WorkArea(owner);
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        var o = owner.AppWindow;
        var x = o.Position.X + o.Size.Width - w - (int)(24 * scale);
        var y = o.Position.Y + (int)(90 * scale);
        x = Math.Clamp(x, work.X, work.X + work.Width - w);
        y = Math.Clamp(y, work.Y, work.Y + work.Height - h);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }
}
