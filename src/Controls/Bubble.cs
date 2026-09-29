using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhatsAppNative.Controls;

/// <summary>
/// Message bubble chrome. Colours come from the Incoming/Outgoing visual states in
/// Styles/Theme.xaml so they follow light/dark theme changes. The first bubble of a run
/// gets WhatsApp's tail: a little point off its top corner, which then isn't rounded.
/// Every bubble keeps the tail's room on its side, so a run lines up.
/// </summary>
public sealed partial class Bubble : ContentControl
{
    private const double TailWidth = 8;

    public static readonly DependencyProperty TailProperty = DependencyProperty.Register(
        nameof(Tail), typeof(bool), typeof(Bubble), new PropertyMetadata(false, (d, _) => ((Bubble)d).UpdateState()));

    /// <summary>First of a run: draws the tail.</summary>
    public bool Tail { get => (bool)GetValue(TailProperty); set => SetValue(TailProperty, value); }

    public Bubble()
    {
        ActualThemeChanged += (_, _) => UpdateState();   // the tail follows the bubble's colour
    }

    public static readonly DependencyProperty IsOutgoingProperty = DependencyProperty.Register(
        nameof(IsOutgoing), typeof(bool), typeof(Bubble),
        new PropertyMetadata(false, (d, _) => ((Bubble)d).UpdateState()));

    public bool IsOutgoing { get => (bool)GetValue(IsOutgoingProperty); set => SetValue(IsOutgoingProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateState();
    }

    private void UpdateState()
    {
        HorizontalAlignment = IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VisualStateManager.GoToState(this, IsOutgoing ? "Outgoing" : "Incoming", false);
        if (GetTemplateChild("Chrome") is not Border chrome || GetTemplateChild("TailShape") is not Microsoft.UI.Xaml.Shapes.Path tail) return;

        // Room for the tail on its side (anything past the bubble's edge would be clipped).
        chrome.Margin = IsOutgoing ? new Thickness(0, 0, TailWidth, 0) : new Thickness(TailWidth, 0, 0, 0);
        // The corner the tail comes out of is square.
        chrome.CornerRadius = !Tail ? new CornerRadius(8) : IsOutgoing ? new CornerRadius(8, 0, 8, 8) : new CornerRadius(0, 8, 8, 8);

        tail.Visibility = Tail ? Visibility.Visible : Visibility.Collapsed;
        tail.Fill = chrome.Background;   // set here: a binding misses the state's change on recycled bubbles
        tail.HorizontalAlignment = IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        tail.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = IsOutgoing ? -1 : 1, CenterX = TailWidth / 2 };
    }

    /// <summary>Re-reads the look.</summary>
    public void Refresh() => UpdateState();
}
