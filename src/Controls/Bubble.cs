using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>
/// Message bubble chrome. Colours come from the Incoming/Outgoing visual states in
/// Styles/Theme.xaml so they follow light/dark theme changes. iMessage style (default):
/// fully round, the last bubble of a run with a curled tail off its bottom corner. Classic:
/// WhatsApp's corners, the first bubble of a run with a little point off its top corner.
/// </summary>
public sealed partial class Bubble : ContentControl
{
    private const double TailWidth = 8, RoundTailWidth = 7;

    // Classic: WhatsApp's point off the top corner. iMessage style: the curl off the bottom corner.
    private const string ClassicTail = "M8,0 L1.6,0 C0.3,0 -0.1,1.3 0.7,2.3 L8,13 Z";
    private const string RoundTail = "M7,0 C7,9 6,14.5 0.6,19 Q0,19.6 1.2,19.8 C6.5,20.3 10.5,18.8 13,16.8 L22,20 L22,0 Z";

    /// <summary>A geometry belongs to one path, so each bubble parses its own (once per style).</summary>
    private static Geometry Parse(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    private bool? _tailRound;

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

        var round = Helpers.Ui.IMessage;
        var room = round ? RoundTailWidth : TailWidth;

        // Room for the tail on its side (anything past the bubble's edge would be clipped);
        // every bubble keeps it so a run lines up.
        chrome.Margin = IsOutgoing ? new Thickness(0, 0, room, 0) : new Thickness(room, 0, 0, 0);
        chrome.BorderThickness = round ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        chrome.CornerRadius = round ? new CornerRadius(Helpers.Ui.BubbleRadius)
                            : !Tail ? new CornerRadius(8)
                            : IsOutgoing ? new CornerRadius(8, 0, 8, 8) : new CornerRadius(0, 8, 8, 8);   // the tail's corner is square

        if (_tailRound != round)
        {
            tail.Data = Parse(round ? RoundTail : ClassicTail);
            _tailRound = round;
        }
        tail.Width = round ? 22 : TailWidth;
        tail.Height = round ? 20 : 13;
        tail.VerticalAlignment = round ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        tail.Visibility = Tail ? Visibility.Visible : Visibility.Collapsed;
        tail.Fill = chrome.Background;   // set here: a binding misses the state's change on recycled bubbles
        tail.HorizontalAlignment = IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        tail.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = IsOutgoing ? -1 : 1, CenterX = tail.Width / 2 };
    }

    /// <summary>Re-reads the look.</summary>
    public void Refresh() => UpdateState();
}
