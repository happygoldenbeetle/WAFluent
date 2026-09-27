using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WhatsAppNative.Controls;

/// <summary>
/// Message bubble chrome. Colours come from the Incoming/Outgoing visual states in
/// Styles/Theme.xaml so they follow light/dark theme changes.
/// </summary>
public sealed partial class Bubble : ContentControl
{
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
    }
}
