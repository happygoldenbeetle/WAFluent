using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WhatsAppNative.Controls;

/// <summary>
/// Vertical list layout for the conversation that remembers every message's measured height.
///
/// StackLayout guesses the rows it hasn't realized from the average height of those on
/// screen. Chat rows vary wildly (a one-liner, a 360 px photo), so that guess changes as you
/// scroll, the list's total height changes under the ScrollViewer, and near the bottom the two
/// fight: the view flickers and won't settle at the end. Here a row measured once keeps its
/// height, so the total only changes when a never-seen row is measured for the first time.
/// </summary>
public sealed partial class ChatLayout : VirtualizingLayout
{
    private const double FallbackHeight = 64;

    /// <summary>Gap between rows.</summary>
    public double Spacing { get; set; } = 6;

    private readonly List<double> _heights = new();   // NaN: never measured
    private List<(UIElement Element, double Top, double Height)> _arranged = new();
    private double _measuredSum;
    private int _measuredCount;

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        _heights.Clear();
        _arranged.Clear();
        _measuredSum = 0;
        _measuredCount = 0;
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewStartingIndex >= 0 && args.NewStartingIndex <= _heights.Count:
                _heights.InsertRange(args.NewStartingIndex, Enumerable.Repeat(double.NaN, args.NewItems?.Count ?? 1));
                break;
            case NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0:
                var removed = Math.Min(args.OldItems?.Count ?? 1, _heights.Count - args.OldStartingIndex);
                for (var i = 0; i < removed; i++) Forget(_heights[args.OldStartingIndex + i]);
                if (removed > 0) _heights.RemoveRange(args.OldStartingIndex, removed);
                break;
            case NotifyCollectionChangedAction.Replace:
                break;   // same row, new content: the old height is a good guess until it's re-measured
            case NotifyCollectionChangedAction.Move when args.OldStartingIndex >= 0 && args.OldStartingIndex < _heights.Count:
                var h = _heights[args.OldStartingIndex];
                _heights.RemoveAt(args.OldStartingIndex);
                _heights.Insert(Math.Min(args.NewStartingIndex, _heights.Count), h);
                break;
            default:   // Reset (another chat, or the list reloaded)
                _heights.Clear();
                _measuredSum = 0;
                _measuredCount = 0;
                break;
        }
        InvalidateMeasure();
    }

    /// <summary>Set by the self-test: every row that took time to get or measure (kind, ms, ms).</summary>
    internal static List<(string Kind, double Get, double Measure)>? Profile;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var passBegan = System.Diagnostics.Stopwatch.GetTimestamp();
        var count = context.ItemCount;
        // Events and the source can disagree after a burst of changes; the source wins.
        if (_heights.Count > count) _heights.RemoveRange(count, _heights.Count - count);
        while (_heights.Count < count) _heights.Add(double.NaN);

        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var estimate = _measuredCount > 0 ? _measuredSum / _measuredCount : FallbackHeight;
        var window = context.RealizationRect;
        var anchor = context.RecommendedAnchorIndex;   // a row asked for by GetOrCreateElement (jump to message)

        var arranged = new List<(UIElement, double, double)>();
        var y = 0.0;
        for (var i = 0; i < count; i++)
        {
            var known = _heights[i];
            var height = double.IsNaN(known) ? estimate : known;
            if ((y + height >= window.Y && y <= window.Y + window.Height) || i == anchor)
            {
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                var element = context.GetOrCreateElementAt(i);
                var made = System.Diagnostics.Stopwatch.GetTimestamp();
                // A message replaced in place (a vote, an edit, a finished download) still has
                // the old message's element: the layout, not the repeater, has to swap it.
                // (Message rows carry their message in Tag; the repeater leaves DataContext unset.)
                if (element is FrameworkElement { Tag: Models.Message shown } && !ReferenceEquals(shown, context.GetItemAt(i)))
                {
                    context.RecycleElement(element);
                    element = context.GetOrCreateElementAt(i);
                }
                element.Measure(new Size(width, double.PositiveInfinity));
                // What building a row costs, by kind (the self-test reads it): getting the element, and measuring it.
                if (Profile is { } profile)
                {
                    var (get, measure) = (System.Diagnostics.Stopwatch.GetElapsedTime(began, made).TotalMilliseconds, System.Diagnostics.Stopwatch.GetElapsedTime(made).TotalMilliseconds);
                    if (get + measure > 0.5) profile.Add(((context.GetItemAt(i) as Models.Message)?.Kind.ToString() ?? "?", get, measure));
                }
                height = element.DesiredSize.Height;
                Remember(i, height);
                arranged.Add((element, y, height));
            }
            y += height + (i < count - 1 ? Gap(context.GetItemAt(i), context.GetItemAt(i + 1)) : 0);
        }

        // Rows that scrolled out of the window (or whose message was removed) go back to the pool.
        var keep = new HashSet<UIElement>(arranged.Select(a => a.Item1));
        foreach (var (element, _, _) in _arranged)
        {
            if (keep.Contains(element)) continue;
            try
            {
                context.RecycleElement(element);
            }
            catch (Exception)
            {
                // Already released by the repeater.
            }
        }
        _arranged = arranged;

        // A message replaced in place (a vote, a download finishing) gets a fresh element
        // here; if the list's size didn't change, nothing else would arrange it and it
        // would stay unplaced (the old look) until the next scroll.
        if (arranged.Count > 0 && VisualTreeHelper.GetParent(arranged[0].Item1) is ItemsRepeater repeater)
            repeater.InvalidateArrange();

        Profile?.Add(("#measure pass", System.Diagnostics.Stopwatch.GetElapsedTime(passBegan).TotalMilliseconds, arranged.Count));
        return new Size(width, Math.Max(0, y));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (element, top, height) in _arranged)
            element.Arrange(new Rect(0, top, finalSize.Width, height));
        Profile?.Add(("#arrange pass", System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds, _arranged.Count));
        return finalSize;
    }

    /// <summary>
    /// Bubbles in a run sit 2 px apart. Classic: the next one continues the run when it has no
    /// tail (tails start runs); iMessage style: this one does (tails end runs).
    /// </summary>
    private double Gap(object current, object next)
    {
        if (current is not Models.Message { Kind: not Models.MessageKind.DateDivider } a
            || next is not Models.Message { Kind: not Models.MessageKind.DateDivider } b) return Spacing;
        return (Helpers.Ui.IMessage ? !a.HasTail : !b.HasTail) ? 2 : Spacing;
    }

    private void Remember(int index, double height)
    {
        Forget(_heights[index]);
        _heights[index] = height;
        _measuredSum += height;
        _measuredCount++;
    }

    private void Forget(double height)
    {
        if (double.IsNaN(height)) return;
        _measuredSum -= height;
        _measuredCount--;
    }

}
