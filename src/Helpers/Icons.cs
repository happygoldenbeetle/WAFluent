using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Helpers;

/// <summary>Icons the icon font doesn't have, as paths (Material "notifications" / "notifications_off", filled).</summary>
public static class Icons
{
    public const string BellData =
        "M12,22 C13.1,22 14,21.1 14,20 H10 C10,21.1 10.89,22 12,22 Z M18,16 V11 C18,7.93 16.36,5.36 13.5,4.68 V4 " +
        "C13.5,3.17 12.83,2.5 12,2.5 C11.17,2.5 10.5,3.17 10.5,4 V4.68 C7.63,5.36 6,7.92 6,11 V16 L4,18 V19 H20 V18 Z";

    public const string BellOffData = "M20,18.69 L7.84,6.14 L5.27,3.49 L4,4.76 L6.8,7.56 L6.8,7.57 C6.28,8.56 6,9.73 6,10.99 V15.99 L4,17.99 V18.99 H17.73 L19.73,20.99 L21,19.72 Z M12,22 C13.11,22 14,21.11 14,20 H10 C10,21.11 10.89,22 12,22 Z M18,14.68 V11 C18,7.92 16.36,5.36 13.5,4.68 V4 C13.5,3.17 12.83,2.5 12,2.5 C11.17,2.5 10.5,3.17 10.5,4 V4.68 C9.5,4.92 8.62,5.4 7.92,6.07 Z";

    /// <summary>⊖ — Clear chat.</summary>
    public const string MinusCircleData =
        "F0 M12,2 A10,10 0 1 0 12.01,2 Z M12,3.8 A8.2,8.2 0 1 1 11.99,3.8 Z M7.5,11.1 H16.5 V12.9 H7.5 Z";

    public static IconElement MinusCircle(double size = 18) => new FontIcon { FontFamily = Ui.SymbolFont, Glyph = Sf.MinusCircle, FontSize = size };

    public static PathIcon Bell(double size = 16) => Make(BellData, size);
    public static PathIcon BellOff(double size = 16) => Make(BellOffData, size);

    /// <summary>The paths are drawn on a 24-unit grid; menu icons are 16.</summary>
    private static PathIcon Make(string data, double size = 16)
    {
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
        geometry.Transform = new ScaleTransform { ScaleX = size / 24, ScaleY = size / 24 };
        return new PathIcon { Data = geometry };
    }
}
