using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace WhatsAppNative.Controls;

/// <summary>Three dots bobbing one after another, like WhatsApp's "typing" bubble. Animates only while shown.</summary>
public sealed partial class TypingDots : StackPanel
{
    private readonly Storyboard _bob = new() { RepeatBehavior = RepeatBehavior.Forever };

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(TypingDots),
        new PropertyMetadata(null, (d, e) => { foreach (var dot in ((TypingDots)d).Children.OfType<Ellipse>()) dot.Fill = (Brush)e.NewValue; }));

    public Brush? DotBrush { get => (Brush?)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }

    public TypingDots()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        for (var i = 0; i < 3; i++)
        {
            var move = new TranslateTransform();
            var dot = new Ellipse { Width = 7, Height = 7, RenderTransform = move, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(dot);

            // Up and back, then rest; each dot 150 ms after the one before.
            var hop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(i * 150), Duration = TimeSpan.FromMilliseconds(1200) };
            hop.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            hop.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(250), Value = -4, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
            hop.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(500), Value = 0, EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } });
            hop.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = 0 });
            Storyboard.SetTarget(hop, move);
            Storyboard.SetTargetProperty(hop, "Y");
            _bob.Children.Add(hop);

            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(i * 150), Duration = TimeSpan.FromMilliseconds(1200) };
            fade.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.45 });
            fade.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(250), Value = 1 });
            fade.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(500), Value = 0.45 });
            fade.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1200), Value = 0.45 });
            Storyboard.SetTarget(fade, dot);
            Storyboard.SetTargetProperty(fade, "Opacity");
            _bob.Children.Add(fade);
        }
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => _bob.Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
    }

    private void Sync()
    {
        if (Visibility == Visibility.Visible && IsLoaded) _bob.Begin(); else _bob.Stop();
    }
}
