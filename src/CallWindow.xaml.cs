using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// In-call window (UI only for now): rings for a moment, then "connects" and shows
/// a running timer and an animated voice-level wave.
/// </summary>
public sealed partial class CallWindow : Window
{
    private const int BarCount = 21;
    private const int DotBars = 3;        // flat dots at each end of the wave
    private const double MinBar = 3, MaxBar = 28;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _heights = new double[BarCount];
    private readonly Random _random = new();
    private readonly DispatcherQueueTimer _ringTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _waveTimer;
    private DateTime _connectedAt;
    private double _loudness;

    public CallWindow(Chat chat, bool video, ElementTheme theme, Window owner)
    {
        InitializeComponent();
        if (theme != ElementTheme.Default) Root.RequestedTheme = theme;

        Title = $"{chat.Name} – WhatsApp call";
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
        CameraToggle.IsChecked = video;
        CallStatus.Text = video ? "Video calling…" : "Calling…";
        BuildWave();

        _ringTimer = DispatcherQueue.CreateTimer();
        _ringTimer.Interval = TimeSpan.FromSeconds(2.5);
        _ringTimer.IsRepeating = false;
        _ringTimer.Tick += (_, _) => Connect();

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();

        _waveTimer = DispatcherQueue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(80);
        _waveTimer.Tick += (_, _) => AnimateWave();

        _ringTimer.Start();
        Closed += (_, _) => StopTimers();
    }

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

    private void Connect()
    {
        _connectedAt = DateTime.Now;
        UpdateClock();
        _clockTimer.Start();
        _waveTimer.Start();
    }

    private void UpdateClock()
    {
        var elapsed = DateTime.Now - _connectedAt;
        CallStatus.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    /// <summary>Speech-like levels: a slowly wandering loudness shaped by a centre-heavy envelope.</summary>
    private void AnimateWave()
    {
        _loudness = Math.Clamp(_loudness + (_random.NextDouble() - 0.45) * 0.35, 0, 1);
        if (_random.NextDouble() < 0.04) _loudness = 0;   // brief pauses between words

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

    private void Mute_Changed(object sender, RoutedEventArgs e) =>
        MuteIcon.Glyph = MuteToggle.IsChecked == true ? Glyphs.MicOff : Glyphs.Mic;

    private void EndCall_Click(object sender, RoutedEventArgs e)
    {
        StopTimers();
        CallStatus.Text = "Call ended";
        foreach (var bar in _bars) bar.Height = MinBar;
        var close = DispatcherQueue.CreateTimer();
        close.Interval = TimeSpan.FromMilliseconds(700);
        close.IsRepeating = false;
        close.Tick += (_, _) => Close();
        close.Start();
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
