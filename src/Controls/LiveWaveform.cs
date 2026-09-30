using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>
/// The recording bar's waveform, like WhatsApp's: a row of rounded bars moving left as you
/// speak, the newest on the right; quiet moments are small dots.
/// </summary>
public sealed partial class LiveWaveform : StackPanel
{
    private const int Bars = 48;
    private const double Tall = 26;
    private readonly Queue<double> _levels = new();

    public LiveWaveform()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        Height = Tall;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < Bars; i++)
        {
            _levels.Enqueue(0);
            Children.Add(new Border
            {
                Width = 3,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Helpers.Themed.Brush("TextFillColorSecondaryBrush"),
            });
        }
    }

    /// <summary>Starts again, all quiet.</summary>
    public void Clear()
    {
        _levels.Clear();
        for (var i = 0; i < Bars; i++) _levels.Enqueue(0);
        Draw();
    }

    /// <summary>A new loudness (0-1) comes in on the right.</summary>
    public void Push(double level)
    {
        _levels.Dequeue();
        _levels.Enqueue(Math.Clamp(level, 0, 1));
        Draw();
    }

    private void Draw()
    {
        var i = 0;
        foreach (var level in _levels)
        {
            var bar = (Border)Children[i++];
            bar.Height = Math.Max(3, Math.Round(Tall * Math.Sqrt(level)));
        }
    }
}
