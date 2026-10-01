using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Helpers;

/// <summary>
/// The pointer becomes a hand over anything that can be clicked (Settings › Hand pointer),
/// like a web page. WinUI only exposes an element's cursor to the element itself, so one
/// watcher per root (the window's content, and each menu, flyout and dialog as it opens)
/// looks at what's under the pointer and sets that root's cursor: buttons, list rows, menu
/// items, tabs, switches, sliders, anything draggable, and elements marked
/// <c>helpers:HandCursor.On="True"</c> (grids with a Tapped handler). Text boxes keep the
/// text cursor; disabled buttons the arrow.
/// </summary>
public static class HandCursor
{
    /// <summary>Settings › Hand pointer.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Marks an element that handles clicks itself (a Tapped handler) as clickable.</summary>
    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(HandCursor), new PropertyMetadata(false));

    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);
    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);

    // UIElement.ProtectedCursor is protected: only reachable this way from outside the element.
    private static readonly PropertyInfo? Cursor =
        typeof(UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly InputCursor Hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>Roots being watched, and whether each currently shows the hand.</summary>
    private static readonly ConditionalWeakTable<UIElement, StrongBox<bool>> Roots = new();

    /// <summary>Watches the window's content, and (every 300 ms) whatever menus, flyouts and dialogs are open over it.</summary>
    public static void Watch(FrameworkElement root)
    {
        Hook(root);
        var popups = root.DispatcherQueue.CreateTimer();
        popups.Interval = TimeSpan.FromMilliseconds(300);
        popups.Tick += (_, _) =>
        {
            if (!Enabled || root.XamlRoot is null) return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
                if (popup.Child is { } child)
                    Hook(child);
        };
        popups.Start();
    }

    private static void Hook(UIElement root)
    {
        if (Cursor is null || Roots.TryGetValue(root, out _)) return;
        Roots.Add(root, new StrongBox<bool>(false));
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Update(root, e.OriginalSource as DependencyObject)), handledEventsToo: true);
        root.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => Update(root, null)), handledEventsToo: true);
    }

    private static void Update(UIElement root, DependencyObject? under)
    {
        if (!Roots.TryGetValue(root, out var shown)) return;
        var hand = Enabled && Clickable(under, root);
        if (hand == shown.Value) return;
        shown.Value = hand;
        try
        {
            Cursor!.SetValue(root, hand ? Hand : null);
        }
        catch (Exception)
        {
            // An element that won't take a cursor: the arrow stays.
        }
    }

    /// <summary>Self-test: the cursor the root shows with the pointer over <paramref name="under"/>.</summary>
    internal static string Probe(UIElement root, DependencyObject under)
    {
        Update(root, under);
        return (Cursor?.GetValue(root) as InputSystemCursor)?.CursorShape.ToString() ?? "Arrow (default)";
    }

    private static bool Clickable(DependencyObject? element, UIElement root)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case TextBox or RichEditBox or PasswordBox:
                    return false;   // typing: the text cursor
                case Control { IsEnabled: false }:
                    return false;
                case ButtonBase or SelectorItem or ItemContainer or NavigationViewItem or ToggleSwitch or ComboBox or Slider
                    or MenuFlyoutItem or MenuFlyoutSubItem or CalendarViewDayItem or Controls.ClickArea:
                    return true;
                case UIElement { CanDrag: true }:
                    return true;
                case FrameworkElement marked when GetOn(marked):
                    return true;
            }
            if (ReferenceEquals(d, root)) break;
        }
        return false;
    }
}
