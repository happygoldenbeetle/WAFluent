using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace WhatsAppNative.Controls;

/// <summary>
/// A loading placeholder's shine: a soft band of light sweeping across whatever is under it
/// (like the map's while it draws). Runs while <see cref="IsActive"/>, and only while loaded.
/// </summary>
public sealed partial class Shimmer : Grid
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Shimmer), new PropertyMetadata(false, (d, _) => ((Shimmer)d).Update()));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _band;
    private readonly TranslateTransform _move = new();
    private readonly Storyboard _sweep = new() { RepeatBehavior = RepeatBehavior.Forever };
    private readonly DoubleAnimation _animation = new() { Duration = TimeSpan.FromMilliseconds(1300) };

    public Shimmer()
    {
        IsHitTestVisible = false;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _band = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _move,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x22, 255, 255, 255), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 1 },
                },
            },
        };
        Children.Add(_band);
        Storyboard.SetTarget(_animation, _move);
        Storyboard.SetTargetProperty(_animation, "X");
        _sweep.Children.Add(_animation);
        SizeChanged += (_, e) =>
        {
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Update();
        };
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => _sweep.Stop();
    }

    private void Update()
    {
        _sweep.Stop();
        _band.Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (!IsActive || !IsLoaded || ActualWidth <= 0) return;
        _animation.From = -_band.Width;
        _animation.To = ActualWidth + _band.Width;
        _sweep.Begin();
    }
}
