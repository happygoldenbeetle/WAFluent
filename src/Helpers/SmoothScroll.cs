using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Mouse-wheel scrolling that doesn't lose notches. A ScrollViewer on its own works out each
/// notch from where the view is at that instant: turn the wheel again while it's still gliding
/// from the last notch and it only adds to the half-way point, so quick turns come out short
/// (it feels like scrolls being skipped). Here every notch is added to where the view is
/// headed, and the view glides there. Touchpads and touch aren't touched: they pan directly.
/// </summary>
public static class SmoothScroll
{
    /// <summary>How far one notch of the wheel moves the view.</summary>
    private const double NotchPixels = 110;

    /// <summary>Notches closer together than this belong to one turn of the wheel.</summary>
    private static readonly TimeSpan Turn = TimeSpan.FromMilliseconds(350);

    private sealed class State
    {
        public double Target;
        public DateTime At;
    }

    /// <summary>A ScrollViewer, or something with one inside it (a ListView): once it has loaded.</summary>
    public static void Attach(FrameworkElement host)
    {
        var hooked = false;
        SizeChangedEventHandler? later = null;
        void Hook()
        {
            if (hooked || Find(host) is not { Content: UIElement content } view) return;
            hooked = true;
            if (later is not null) host.SizeChanged -= later;
            var state = new State();
            // On the content, so it's seen before the ScrollViewer itself (which stands down once it's handled).
            content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => OnWheel(view, state, e)), false);
        }
        // A list that starts hidden has nothing inside it yet: tried again when it's first laid out.
        later = (_, _) => Hook();
        host.SizeChanged += later;
        if (host.IsLoaded) Hook();
        else
        {
            RoutedEventHandler? once = null;
            once = (_, _) =>
            {
                host.Loaded -= once;
                Hook();
            };
            host.Loaded += once;
        }
    }

    private static ScrollViewer? Find(DependencyObject root)
    {
        if (root is ScrollViewer view) return view;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static void OnWheel(ScrollViewer view, State state, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || view.ScrollableHeight <= 0) return;
        var wheel = e.GetCurrentPoint(view).Properties;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (wheel.IsHorizontalMouseWheel || ctrl || wheel.MouseWheelDelta == 0) return;

        var now = DateTime.UtcNow;
        // Still gliding from the last notch: on from where that was headed. Otherwise from where the view is.
        var from = now - state.At < Turn ? state.Target : view.VerticalOffset;
        var target = Math.Clamp(from - wheel.MouseWheelDelta / 120.0 * NotchPixels, 0, view.ScrollableHeight);
        (state.Target, state.At) = (target, now);
        e.Handled = true;
        view.ChangeView(null, target, null, disableAnimation: false);
    }
}
