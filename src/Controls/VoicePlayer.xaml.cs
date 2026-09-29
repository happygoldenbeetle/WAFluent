using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.Controls;

/// <summary>
/// Voice note bubble content: filled play/pause, waveform with the playhead dot, duration, and
/// on the right the sender's picture with a mic badge (headphones for audio files) that turns
/// into the speed pill while the note is playing.
/// </summary>
public sealed partial class VoicePlayer : UserControl
{
    private const int BarCount = 40;   // fills the space between the play button and the picture

    /// <summary>Picture and name for a message's sender (set by the window: you, the contact, a group member).</summary>
    public static Func<Message, (string? Path, string Name)>? PictureFor { get; set; }
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
        var (path, name) = PictureFor?.Invoke(Message) ?? (null, "");
        Picture.DisplayName = name;
        Picture.Source = path;
        Headphones.Visibility = Message.IsVoiceNote ? Visibility.Collapsed : Visibility.Visible;
        MicBadge.Visibility = Message.IsVoiceNote ? Visibility.Visible : Visibility.Collapsed;
        BuildBars(Message);
        Refresh();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.Message.Delivery) && Message is { } m) Ticks.Delivery = m.Delivery;
        Refresh();
    }

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
        PlayIcon.Visibility = !m.MediaFailed && !m.IsMediaLoading && !playing ? Visibility.Visible : Visibility.Collapsed;
        PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        WarningIcon.Visibility = m.MediaFailed ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, m.MediaFailed ? "Couldn't download this voice message" : null);

        var duration = isCurrent ? AudioPlayback.Duration : TimeSpan.FromSeconds(m.Seconds);
        var position = isCurrent ? AudioPlayback.Position : TimeSpan.Zero;
        DurationText.Text = Format.Duration(isCurrent && position > TimeSpan.Zero ? position : duration);

        // The note in the player shows its speed where the picture was.
        RateButton.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
        PictureArea.Visibility = isCurrent ? Visibility.Collapsed : Visibility.Visible;
        RateText.Text = $"{AudioPlayback.Rate:0.#}×";

        // Playhead dot rides along the waveform (resting at the start when idle).
        var fraction = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        Canvas.SetLeft(Knob, Math.Max(0, fraction * Bars.ActualWidth - Knob.Width / 2));

        var played = (int)Math.Round(BarCount * fraction);
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
