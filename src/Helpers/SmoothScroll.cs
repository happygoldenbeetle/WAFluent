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

    /// <summary>For the self-test: turns of a real wheel seen, and times a glide took hold of a row (and nothing: kept for the test's line).</summary>
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

        private bool _running;
        private long _then;

        // In a list that builds its rows as they come into view (an ItemsRepeater: the conversation),
        // offsets aren't a fixed ruler: rows turn out taller or shorter than guessed and every offset
        // after them moves. So the glide doesn't keep a position there. It holds on to one row that's
        // on screen and says where on screen that row should be; each frame the view is put wherever
        // makes that true. However the list renumbers itself, the row is still the row.
        private ItemsRepeater? _rows;
        private bool _looked;
        private FrameworkElement? _held;
        private object? _heldItem;
        /// <summary>Where the held row's top should be, measured from the top of the view.</summary>
        private double _heldAt;

        // Everywhere else (lists whose rows are all alike, plain scrolling panels) offsets are a fixed
        // ruler, and the glide keeps its own place on it: a move takes a frame or two to show in the
        // view's own offset, and stepping on from a stale reading loses distance.
        private double _at;

        public void Add(double pixels)
        {
            // The other way: what was left of the old direction is dropped, so it turns at once.
            if (Math.Sign(pixels) != Math.Sign(_left)) _left = 0;
            _left += pixels;
            if (_running) return;
            _running = true;
            _then = System.Diagnostics.Stopwatch.GetTimestamp();
            _at = view.VerticalOffset;
            _held = null;
            if (!_looked)
            {
                _looked = true;
                _rows = view.Content is DependencyObject content ? FindRows(content) : null;
            }
            CompositionTarget.Rendering += Frame;
        }

        private static ItemsRepeater? FindRows(DependencyObject root)
        {
            if (root is ItemsRepeater rows) return rows;
            if (root is ScrollViewer) return null;   // another view's rows aren't this one's
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindRows(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }

        private void Stop()
        {
            if (!_running) return;
            (_running, _left, _held) = (false, 0, null);
            CompositionTarget.Rendering -= Frame;
        }

        private double Top(UIElement row) => row.TransformToVisual(view).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

        /// <summary>The row nearest the middle of the view (rows put aside by the repeater sit far off screen).</summary>
        private FrameworkElement? Middle()
        {
            if (_rows is null) return null;
            FrameworkElement? best = null;
            var (middle, nearest) = (view.ViewportHeight / 2, double.MaxValue);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(_rows); i++)
            {
                if (VisualTreeHelper.GetChild(_rows, i) is not FrameworkElement { ActualHeight: > 0 } row) continue;
                var away = Math.Abs(Top(row) + row.ActualHeight / 2 - middle);
                if (away < nearest) (best, nearest) = (row, away);
            }
            return nearest < view.ViewportHeight * 2 ? best : null;
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
            var step = Math.Abs(_left) < 0.5 ? _left : _left * (1 - Math.Exp(-seconds / EaseSeconds));
            _left -= step;

            double to;
            if (_rows is not null)
            {
                // The held row was given to another message, or left the view's neighbourhood: another is taken,
                // where it is now (what the old one was still owed is carried over when it's still there to ask).
                var owed = 0.0;
                var gone = _held is null || !ReferenceEquals(_held.Tag ?? _held.DataContext, _heldItem) || _held.ActualHeight <= 0 || _held.Parent is null;
                if (!gone)
                {
                    var top = Top(_held!);
                    if (top < -view.ViewportHeight || top > 2 * view.ViewportHeight)
                    {
                        (gone, owed) = (true, top - _heldAt);
                    }
                }
                if (gone)
                {
                    _held = Middle();
                    if (_held is null)
                    {
                        Stop();
                        return;
                    }
                    _heldItem = _held.Tag ?? _held.DataContext;
                    _heldAt = Top(_held) - owed;
                    Shifts++;
                }
                // Scrolling down moves the row up the screen. The view goes wherever puts the row there: its
                // offset and the row's place are read together, so a move that hasn't shown yet is in both
                // and cancels out.
                _heldAt -= step;
                var actual = view.VerticalOffset;
                to = Math.Clamp(actual + Top(_held!) - _heldAt, 0, view.ScrollableHeight);
                // Against the top or the bottom: the row can't go where it was wanted, so it's wanted where it'll be.
                _heldAt = Top(_held!) - (to - actual);
            }
            else
            {
                to = Math.Clamp(_at + step, 0, view.ScrollableHeight);
                _at = to;
            }
            view.ChangeView(null, to, null, disableAnimation: true);
            // Arrived, or against the top or the bottom: nothing more to do.
            if (Math.Abs(_left) < 0.01 || (to <= 0 && _left < 0) || (to >= view.ScrollableHeight && _left > 0)) Stop();
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, State> States = new();

    /// <summary>Whether a view is gliding right now (the self-test).</summary>
    internal static bool IsGliding(ScrollViewer view) => States.TryGetValue(view, out var state) && state.Gliding;

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
