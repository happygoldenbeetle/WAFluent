using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Smooth mouse-wheel scrolling. A ScrollViewer on its own moves a fixed step per notch of the
/// wheel, each worked out from where the view is at that instant: quick turns come out short
/// and the motion is step by step. Here a notch only adds to the distance still to travel, and
/// the view is moved a little every frame, fast at first and easing out as that distance runs
/// down, so turning the wheel is one continuous glide and no notch is lost.
///
/// The distance is kept as "this much further", never as a place in the list: a list that
/// builds its rows as they come into view (the conversation) changes height while it scrolls,
/// and a remembered place would be wrong by the time it's reached (the view would hop back).
/// Touchpads and touch aren't touched: they pan directly.
/// </summary>
public static class SmoothScroll
{
    /// <summary>How far one notch of the wheel moves the view.</summary>
    private const double NotchPixels = 110;

    /// <summary>How quickly the glide closes in: each of these, it covers about two thirds of what's left.</summary>
    private const double EaseSeconds = 0.075;

    private sealed class State(ScrollViewer view)
    {
        /// <summary>How much further to go (down is positive).</summary>
        private double _left;
        /// <summary>The offset read last frame, and the one asked for then: a change takes a frame to show.</summary>
        private double _read = double.NaN, _asked;
        private bool _running;
        private long _then;

        public void Add(double pixels)
        {
            // The other way: what was left of the old direction is dropped, so it turns at once.
            if (Math.Sign(pixels) != Math.Sign(_left)) _left = 0;
            _left += pixels;
            if (_running) return;
            (_running, _then, _read) = (true, System.Diagnostics.Stopwatch.GetTimestamp(), double.NaN);
            CompositionTarget.Rendering += Frame;
        }

        private void Stop()
        {
            if (!_running) return;
            (_running, _left) = (false, 0);
            CompositionTarget.Rendering -= Frame;
        }

        private void Frame(object? sender, object e)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var seconds = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_then, now).TotalSeconds, 0.001, 0.034);   // a slow frame (rows being built) doesn't become a lurch
            _then = now;
            if (!view.IsLoaded || view.ScrollableHeight <= 0)
            {
                Stop();
                return;
            }
            // Where the view is: what it reports, unless last frame's move hasn't shown up there yet.
            var actual = view.VerticalOffset;
            var from = !double.IsNaN(_read) && Math.Abs(actual - _read) < 0.01 ? _asked : actual;
            var step = _left * (1 - Math.Exp(-seconds / EaseSeconds));
            if (Math.Abs(_left) < 0.5) step = _left;   // the last bit, in one go
            var to = Math.Clamp(from + step, 0, view.ScrollableHeight);
            _left -= step;
            (_read, _asked) = (actual, to);
            view.ChangeView(null, to, null, disableAnimation: true);
            // Arrived, or against the top or the bottom: nothing more to do.
            if (Math.Abs(_left) < 0.01 || (to <= 0 && _left < 0) || (to >= view.ScrollableHeight && _left > 0)) Stop();
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, State> States = new();

    /// <summary>What a turn of the wheel does, without a wheel (the self-test): this much further, smoothly.</summary>
    internal static void Nudge(ScrollViewer view, double pixels) => States.GetValue(view, v => new State(v)).Add(pixels);

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
            var state = States.GetValue(view, v => new State(v));
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
        e.Handled = true;
        state.Add(-wheel.MouseWheelDelta / 120.0 * NotchPixels);
    }
}
