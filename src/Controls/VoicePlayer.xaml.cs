using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.Controls;

/// <summary>Voice note bubble content: play/pause, waveform with progress, duration, speed.</summary>
public sealed partial class VoicePlayer : UserControl
{
    private const int BarCount = 48;
    private const double MaxBarHeight = 28, MinBarHeight = 3;

    private readonly List<Rectangle> _bars = new();
    private int _playedBars = -1;

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(Message), typeof(VoicePlayer),
        new PropertyMetadata(null, (d, e) => ((VoicePlayer)d).OnMessageChanged(e.OldValue as Message)));

    public Message? Message { get => (Message?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    public VoicePlayer()
    {
        InitializeComponent();
        Loaded += (_, _) => { AudioPlayback.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => AudioPlayback.Changed -= Refresh;
        ActualThemeChanged += (_, _) => { _playedBars = -1; Refresh(); };
    }

    private void OnMessageChanged(Message? old)
    {
        if (old is not null) old.PropertyChanged -= OnMessagePropertyChanged;
        if (Message is null) return;
        Message.PropertyChanged += OnMessagePropertyChanged;
        TimeText.Text = Message.Time;
        Ticks.Delivery = Message.Delivery;
        Ticks.Visibility = Message.IsOutgoing ? Visibility.Visible : Visibility.Collapsed;
        BuildBars(Message);
        Refresh();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>WhatsApp sends 64 samples (0-100); resample to the bar count. Plain audio has none.</summary>
    private void BuildBars(Message message)
    {
        Bars.Children.Clear();
        _bars.Clear();
        _playedBars = -1;
        var samples = message.Waveform;
        var seed = message.Id.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        var fallback = new Random(seed);
        for (var i = 0; i < BarCount; i++)
        {
            double level = samples.Length > 0
                ? samples[i * samples.Length / BarCount] / 100.0
                : 0.25 + 0.5 * fallback.NextDouble();
            var bar = new Rectangle
            {
                Width = 3,
                Height = Math.Max(MinBarHeight, level * MaxBarHeight),
                RadiusX = 1.5,
                RadiusY = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _bars.Add(bar);
            Bars.Children.Add(bar);
        }
    }

    private void Refresh()
    {
        if (Message is not { } m) return;
        var isCurrent = AudioPlayback.Current == m;
        var playing = isCurrent && AudioPlayback.IsPlaying;

        LoadingRing.IsActive = m.IsMediaLoading;
        PlayButton.IsEnabled = m.HasMediaFile;
        PlayIcon.Glyph = m.MediaFailed ? Glyphs.Warning : playing ? Glyphs.Pause : Glyphs.Play;
        PlayIcon.Opacity = m.IsMediaLoading ? 0 : 1;
        ToolTipService.SetToolTip(PlayButton, m.MediaFailed ? "Couldn't download this voice message" : null);

        var duration = isCurrent ? AudioPlayback.Duration : TimeSpan.FromSeconds(m.Seconds);
        var position = isCurrent ? AudioPlayback.Position : TimeSpan.Zero;
        DurationText.Text = Format.Duration(isCurrent && position > TimeSpan.Zero ? position : duration);

        RateButton.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
        RateButton.Content = $"{AudioPlayback.Rate:0.#}×";

        var played = duration > TimeSpan.Zero ? (int)Math.Round(BarCount * position / duration) : 0;
        if (played == _playedBars) return;
        _playedBars = played;
        for (var i = 0; i < _bars.Count; i++)
            _bars[i].Fill = i < played ? PlayedSwatch.Fill : UnplayedSwatch.Fill;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Message is { } m) AudioPlayback.Toggle(m);
    }

    private void Bars_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Message is not { HasMediaFile: true } m || Bars.ActualWidth <= 0) return;
        AudioPlayback.Seek(m, e.GetPosition(Bars).X / Bars.ActualWidth);
    }

    private void Rate_Click(object sender, RoutedEventArgs e) => AudioPlayback.CycleRate();
}
