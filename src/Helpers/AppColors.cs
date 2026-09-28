using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WhatsAppNative.Helpers;

/// <summary>
/// WhatsApp green, or the Windows accent colour (Settings → Use Windows accent colour).
/// Swaps the app's SystemAccentColor* overrides (App.xaml: buttons, badges, selection,
/// links) and retints what is green on purpose: your bubbles, "You" in quotes, the
/// status ring. Follows Windows when its accent changes.
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
    private static readonly Color WhatsAppGreen = Color.FromArgb(255, 0x00, 0xA8, 0x84);

    public static bool UseSystem { get; private set; }

    /// <summary>The accent in use (for code-drawn things like the select-mode tint).</summary>
    public static Color Accent => UseSystem ? System.GetColorValue(UIColorType.Accent) : WhatsAppGreen;

    /// <summary>Green ring around avatars with an unseen status.</summary>
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

        // Colours that are green by design, not through the accent resources.
        var accent = Accent;
        StatusRing.Color = useSystem ? accent : Color.FromArgb(255, 0x1D, 0xAA, 0x61);
        Tint("Light", "OutgoingBubbleBrush", useSystem ? Blend(accent, Color.FromArgb(255, 255, 255, 255), 0.20) : Hex(0xD9FDD3));
        Tint("Dark", "OutgoingBubbleBrush", useSystem ? Blend(accent, Color.FromArgb(255, 0x1C, 0x1C, 0x1C), 0.38) : Hex(0x144D37));
        Tint("Light", "QuoteSelfBrush", useSystem ? System.GetColorValue(UIColorType.AccentDark1) : Hex(0x008069));
        Tint("Dark", "QuoteSelfBrush", useSystem ? System.GetColorValue(UIColorType.AccentLight1) : Hex(0x06CF9C));
        Changed?.Invoke();
    }

    /// <summary>Re-applies when Windows' accent colour changes (while in system mode).</summary>
    public static void FollowWindows(Microsoft.UI.Dispatching.DispatcherQueue ui) =>
        System.ColorValuesChanged += (_, _) => ui.TryEnqueue(() => { if (UseSystem) Apply(true); });

    /// <summary>Changes a brush in Theme.xaml in place, so everything using it repaints.</summary>
    private static void Tint(string theme, string key, Color color)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary themed
                && themed.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                brush.Color = color;
    }

    private static Color Blend(Color top, Color bottom, double amount) => Color.FromArgb(255,
        (byte)Math.Round(top.R * amount + bottom.R * (1 - amount)),
        (byte)Math.Round(top.G * amount + bottom.G * (1 - amount)),
        (byte)Math.Round(top.B * amount + bottom.B * (1 - amount)));

    private static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
