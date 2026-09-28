using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>
/// Wraps something personal (a profile photo, a name or number). In developer mode
/// (Settings) a frosted veil covers it: an untinted in-app acrylic, which blurs
/// whatever is underneath, for screenshots and screen sharing.
/// </summary>
public sealed partial class Redact : Grid
{
    /// <summary>Developer mode: every Redact in the app is veiled.</summary>
    public static bool Enabled { get; private set; }

    private static event Action? Changed;

    public static void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Changed?.Invoke();
    }

    public static readonly DependencyProperty VeilRadiusProperty = DependencyProperty.Register(
        nameof(VeilRadius), typeof(CornerRadius), typeof(Redact),
        new PropertyMetadata(new CornerRadius(4), (d, e) => ((Redact)d)._veil.CornerRadius = (CornerRadius)e.NewValue));

    /// <summary>Round the veil to the content (half the size for avatars).</summary>
    public CornerRadius VeilRadius { get => (CornerRadius)GetValue(VeilRadiusProperty); set => SetValue(VeilRadiusProperty, value); }

    private readonly Border _veil = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(4) };

    public Redact()
    {
        Loaded += (_, _) =>
        {
            if (!Children.Contains(_veil)) Children.Add(_veil);   // after the content, so it's on top
            Changed += Update;
            Update();
        };
        Unloaded += (_, _) => Changed -= Update;
    }

    private void Update()
    {
        if (Enabled && _veil.Background is null)
        {
            _veil.Background = new AcrylicBrush
            {
                TintColor = Colors.Gray,
                TintOpacity = 0,
                TintLuminosityOpacity = 0.05,
                FallbackColor = Colors.Gray,   // transparency effects off: a plain grey block still hides it
            };
        }
        _veil.Visibility = Enabled ? Visibility.Visible : Visibility.Collapsed;
    }
}
