using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Controls;

/// <summary>Something clickable that isn't a button (the chat header's photo and name): the hand cursor.</summary>
public sealed partial class ClickArea : Grid
{
    public ClickArea()
    {
        Background = new SolidColorBrush(Colors.Transparent);   // hit-testable across its whole area
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
