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
///
/// It's on for every ScrollViewer in the app: Styles/Theme.xaml sets <see cref="IsEnabledProperty"/>
/// in the style all of them take, the ones inside lists, menus and pickers included.
/// </summary>
public static class SmoothScroll
{
    /// <summary>How far one notch of the wheel moves the view.</summary>
    private const double NotchPixels = 120;

    /// <summary>
    /// How long the glide coasts: each of these, it covers about two thirds of what's left, so it
    /// keeps drifting for around half a second after the wheel stops, like a web page's smooth scrolling.
    /// </summary>
    private const double EaseSeconds = 0.15;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll),
        new PropertyMetadata(false, (d, e) => { if (d is ScrollViewer view && e.NewValue is true) Attach(view); }));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>The views that have it (one handler each, however often they're attached).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, object> Hooked = new();

    /// <summary>For the self-test: turns of a real wheel seen, and times a glide followed the list shifting under it (and by how much in all).</summary>
    internal static int Wheels, Shifts;
    internal static double Shifted;

    /// <summary>How many views have it right now (the self-test).</summary>
    internal static int Count => Hooked.Count();

    private sealed class State(ScrollViewer view)
    {
        /// <summary>How much further to go (down is positive).</summary>
        private double _left;

        /// <summary>Still gliding.</summary>
        public bool Gliding => _running;

        /// <summary>
        /// Where the glide has the view, kept here rather than read back: a move takes a frame or
        /// two to show in the view's own offset, and stepping on from a stale reading loses
        /// distance and jitters backwards.
        /// </summary>
        private double _at;
        /// <summary>The view's offset as read last frame, the last two steps made, and how far the view could scroll then.</summary>
        private double _read, _step, _before, _height;
        private bool _running;
        private long _then;

        public void Add(double pixels)
        {
            // The other way: what was left of the old direction is dropped, so it turns at once.
            if (Math.Sign(pixels) != Math.Sign(_left)) _left = 0;
            _left += pixels;
            if (_running) return;
            _running = true;
            _then = System.Diagnostics.Stopwatch.GetTimestamp();
            (_at, _read, _step, _before, _height) = (view.VerticalOffset, view.VerticalOffset, 0, 0, view.ScrollableHeight);
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
            // The list grew or shrank (rows built as they come into view are rarely the height that was
            // guessed for them). When that happens above what's on screen the view renumbers itself:
            // every offset moves along by that much while the picture stays put. The glide's own
            // position has to move along with it, or its next step would aim at the old numbering
            // and the picture would hop. Which of the two happened is told by which fits what the
            // offset did: moved by the change in height (plus the glide's step), or just by the step.
            var actual = view.VerticalOffset;
            var moved = actual - _read;
            var grew = view.ScrollableHeight - _height;
            // (A step shows in the offset a frame or two after it's made: none, one or both of the last two may be in `moved`.)
            double Off(double by) => new[] { 0, _step, _before, _step + _before }.Min(steps => Math.Abs(moved - by - steps));
            if (Math.Abs(grew) > 0.5 && Off(grew) + 1 < Off(0))
            {
                _at += grew;
                Shifts++;
                Shifted += Math.Abs(grew);
            }
            (_read, _height) = (actual, view.ScrollableHeight);

            var step = Math.Abs(_left) < 0.5 ? _left : _left * (1 - Math.Exp(-seconds / EaseSeconds));
            var to = Math.Clamp(_at + step, 0, view.ScrollableHeight);
            _left -= step;
            (_before, _step, _at) = (_step, to - _at, to);
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
            if (!Hooked.TryAdd(view, content)) return;   // it has it already
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
        // Already at the end it's being turned towards: left for whatever this view sits inside to scroll.
        var up = wheel.MouseWheelDelta > 0;
        if (!state.Gliding && (up ? view.VerticalOffset <= 0.5 : view.VerticalOffset >= view.ScrollableHeight - 0.5)) return;
        e.Handled = true;
        Wheels++;
        state.Add(-wheel.MouseWheelDelta / 120.0 * NotchPixels);
    }
}
