using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WhatsAppNative.Helpers;

/// <summary>
/// WhatsApp green, or the Windows accent colour (Settings → Use Windows accent colour) for
/// the controls: buttons, toggles, the rail's selection bar, text boxes. It swaps the app's
/// SystemAccentColor* overrides (App.xaml). Pings and chat content (badges, unread times,
/// sender names, your bubbles) use ChatAccentBrush and stay WhatsApp green either way.
/// Follows Windows when its accent changes.
/// </summary>
public static class AppColors
{
    private static readonly (string Key, UIColorType Type)[] Accents =
    [
        ("SystemAccentColor", UIColorType.Accent),
        ("SystemAccentColorLight1", UIColorType.AccentLight1),
        ("SystemAccentColorLight2", UIColorType.AccentLight2),
        ("SystemAccentColorLight3", UIColorType.AccentLight3),
        ("SystemAccentColorDark1", UIColorType.AccentDark1),
        ("SystemAccentColorDark2", UIColorType.AccentDark2),
        ("SystemAccentColorDark3", UIColorType.AccentDark3),
    ];

    private static readonly UISettings System = new();
    private static Dictionary<string, Color>? _whatsApp;

    public static bool UseSystem { get; private set; }

    /// <summary>Green ring around avatars with an unseen status (WhatsApp's, whatever the accent).</summary>
    public static SolidColorBrush StatusRing { get; } = new(Color.FromArgb(255, 0x1D, 0xAA, 0x61));

    /// <summary>Raised after a switch, once the resources are updated (the window refreshes its theme).</summary>
    public static event Action? Changed;

    public static void Apply(bool useSystem)
    {
        var resources = Application.Current.Resources;
        _whatsApp ??= Accents.ToDictionary(a => a.Key, a => (Color)resources[a.Key]);
        UseSystem = useSystem;
        foreach (var (key, type) in Accents)
            resources[key] = useSystem ? System.GetColorValue(type) : _whatsApp[key];
        Changed?.Invoke();
    }

    /// <summary>Re-applies when Windows' accent colour changes (while in system mode).</summary>
    public static void FollowWindows(Microsoft.UI.Dispatching.DispatcherQueue ui) =>
        System.ColorValuesChanged += (_, _) => ui.TryEnqueue(() => { if (UseSystem) Apply(true); });
}
