using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhatsAppNative.Controls;

/// <summary>
/// Message bubble chrome. Colours come from the Incoming/Outgoing visual states in
/// Styles/Theme.xaml so they follow light/dark theme changes. With iMessage bubbles
/// (<see cref="Helpers.Ui.IMessage"/>) they're blue/grey and fully round, and the last bubble
/// of a run gets iMessage's curled tail.
/// </summary>
public sealed partial class Bubble : ContentControl
{
    public static readonly DependencyProperty TailProperty = DependencyProperty.Register(
        nameof(Tail), typeof(bool), typeof(Bubble), new PropertyMetadata(true, (d, _) => ((Bubble)d).UpdateState()));

    /// <summary>Last of a run: draws the tail (iMessage look).</summary>
    public bool Tail { get => (bool)GetValue(TailProperty); set => SetValue(TailProperty, value); }

    public static readonly DependencyProperty IsOutgoingProperty = DependencyProperty.Register(
        nameof(IsOutgoing), typeof(bool), typeof(Bubble),
        new PropertyMetadata(false, (d, _) => ((Bubble)d).UpdateState()));

    public bool IsOutgoing { get => (bool)GetValue(IsOutgoingProperty); set => SetValue(IsOutgoingProperty, value); }

    public Bubble()
    {
        ActualThemeChanged += (_, _) => UpdateState();   // the tail follows the new bubble colour
    }

    /// <summary>Re-reads the look (after Settings › iMessage-style bubbles changed).</summary>
    public void Refresh() => UpdateState();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateState();
    }

    private void UpdateState()
    {
        HorizontalAlignment = IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VisualStateManager.GoToState(this, IsOutgoing ? "Outgoing" : "Incoming", false);
        var imessage = Helpers.Ui.IMessage;
        VisualStateManager.GoToState(this, !imessage ? "Classic" : IsOutgoing ? "IMessageOutgoing" : "IMessageIncoming", false);
        if (GetTemplateChild("TailShape") is Microsoft.UI.Xaml.Shapes.Path tail)
        {
            // Same colour as the bubble (set here: a binding misses the state's change when a
            // recycled bubble switches sides).
            if (GetTemplateChild("Chrome") is Border chrome) tail.Fill = chrome.Background;
            tail.Visibility = imessage && Tail ? Visibility.Visible : Visibility.Collapsed;
            // Drawn for the left (incoming); mirrored for yours on the right.
            tail.HorizontalAlignment = IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            // Anything past the bubble's edge is clipped, so the bubble keeps 7 px of room on its
            // tail side (every bubble, so a run stays lined up) and the tail curls into it.
            tail.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = IsOutgoing ? -1 : 1, CenterX = 11 };
            if (GetTemplateChild("Chrome") is Border body)
                body.Margin = !imessage ? default : IsOutgoing ? new Thickness(0, 0, 7, 0) : new Thickness(7, 0, 0, 0);
        }
    }
}
