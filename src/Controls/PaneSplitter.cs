using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace WhatsAppNative.Controls;

/// <summary>
/// An invisible grab strip on a pane edge: the resize cursor on hover, a thin accent line
/// while hovered or dragged, and drag/double-click events. The owner decides what the
/// drag means (see MainWindow.ChatListPane.cs).
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private readonly Rectangle _line;
    private bool _dragging;
    private double _startX;

    /// <summary>Pointer went down on the strip.</summary>
    public event Action? DragStarted;

    /// <summary>Horizontal distance moved since the drag started.</summary>
    public event Action<double>? DragDelta;

    public event Action? DragCompleted;

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Colors.Transparent);   // hit-testable across its whole width
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _line = new Rectangle
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        _line.Fill = Helpers.Themed.Brush("AccentFillColorDefaultBrush");
        Children.Add(_line);
        AutomationProperties.SetName(this, "Resize chat list");

        PointerEntered += (_, _) =>
        {
            _line.Fill = Helpers.Themed.Brush("AccentFillColorDefaultBrush");   // current theme and accent
            _line.Opacity = 1;
        };
        PointerExited += (_, _) => { if (!_dragging) _line.Opacity = 0; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e);
        PointerCaptureLost += (_, e) => End(e);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging) return;
        _startX = point.Position.X;
        _line.Opacity = 1;
        e.Handled = true;
        DragStarted?.Invoke();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        DragDelta?.Invoke(e.GetCurrentPoint(null).Position.X - _startX);
        e.Handled = true;
    }

    private void End(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        _line.Opacity = 0;
        DragCompleted?.Invoke();
    }
}
