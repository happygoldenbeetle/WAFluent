using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Theme brushes for things built in code. <c>Application.Current.Resources[key]</c> answers
/// for the Windows theme; with Settings → Theme set to Light or Dark the window can differ,
/// so this looks the key up in the theme dictionary the window is actually using.
/// </summary>
public static class Themed
{
    /// <summary>The window's actual theme (kept up to date by MainWindow).</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Default;

    public static Brush Brush(string key)
    {
        var light = Theme == ElementTheme.Light
                    || (Theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);
        var names = light ? new[] { "Light" } : new[] { "Dark", "Default" };
        foreach (var name in names)
            if (Find(Application.Current.Resources, name, key) is Brush brush)
                return brush;
        return (Brush)Application.Current.Resources[key];
    }

    private static object? Find(ResourceDictionary dictionary, string theme, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary t && t.TryGetValue(key, out var value))
            return value;
        foreach (var merged in dictionary.MergedDictionaries)
            if (Find(merged, theme, key) is { } found)
                return found;
        return null;
    }
}
