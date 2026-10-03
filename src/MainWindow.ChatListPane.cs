using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The chat list pane can be resized by dragging its right edge, and dragged (or
/// double-clicked) shut; the width and collapsed state are remembered in ui.json.
/// Clicking a rail item or dragging the edge back out reopens it.
/// </summary>
public sealed partial class MainWindow
{
    private const double ChatListMin = 260, ChatListMaxCap = 640;
    private const double CollapseBelow = 170;       // let go narrower than this and the pane closes
    private const double ConversationMin = 380;     // what the conversation keeps at least

    private readonly UiSettings _ui = UiSettings.Load();
    private double _dragStartWidth;
    private bool _dragCollapsed;

    private void SetupChatListPane()
    {
        ApplyChatListWidth(_ui.ChatListCollapsed ? 0 : Clamp(_ui.ChatListWidth));

        ChatListSplitter.DragStarted += () =>
        {
            StopWidthAnimation();
            _dragStartWidth = ChatListColumn.ActualWidth;
            _dragCollapsed = _dragStartWidth < 1;
            _dragWanted = null;
            CompositionTarget.Rendering += DragFrame;
        };
        // The mouse reports far more often than the screen draws, and every width lays the whole
        // conversation out again: the newest position is taken once a frame (DragFrame).
        ChatListSplitter.DragDelta += dx => _dragWanted = _dragStartWidth + dx;
        ChatListSplitter.DragCompleted += () =>
        {
            CompositionTarget.Rendering -= DragFrame;
            DragFrame(null, null!);
            _ui.ChatListCollapsed = _dragCollapsed;
            if (!_dragCollapsed) _ui.ChatListWidth = _widthAnimation is null ? _chatListWidth : _slideTo;
            _ui.Save();
        };
        ChatListSplitter.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            SetChatListCollapsed(!_ui.ChatListCollapsed);
        };

        // A window made narrower squeezes the list rather than the conversation.
        ContentGrid.SizeChanged += (_, _) =>
        {
            if (!_ui.ChatListCollapsed && _widthAnimation is null) ApplyChatListWidth(Clamp(_ui.ChatListWidth));
        };
    }

    /// <summary>Opens or closes the pane with a short slide.</summary>
    private void SetChatListCollapsed(bool collapsed)
    {
        if (_ui.ChatListCollapsed == collapsed) return;
        _ui.ChatListCollapsed = collapsed;
        _ui.Save();
        AnimateChatListWidth(collapsed ? 0 : Clamp(_ui.ChatListWidth));
    }

    private double Clamp(double width)
    {
        var available = ContentGrid.ActualWidth;
        if (available <= 0) return Math.Clamp(width, ChatListMin, ChatListMaxCap);   // before the first layout
        var max = Math.Min(ChatListMaxCap, Math.Max(ChatListMin, available - ConversationMin));
        return Math.Clamp(width, ChatListMin, max);
    }

    private double? _dragWanted;      // where the drag has got to, until the next frame takes it
    private double _chatListWidth;    // the width last set (ActualWidth is a layout behind)

    /// <summary>
    /// One step of a drag: the pane follows the pointer down to its minimum, on whole pixels;
    /// past the collapse point it slides shut, and slides back out when dragged back.
    /// </summary>
    private void DragFrame(object? sender, object e)
    {
        if (_dragWanted is not { } wanted) return;
        _dragWanted = null;
        var collapsed = wanted < CollapseBelow;
        var width = collapsed ? 0 : Math.Round(Clamp(wanted));
        if (collapsed != _dragCollapsed)
        {
            _dragCollapsed = collapsed;
            AnimateChatListWidth(width);
        }
        else if (_widthAnimation is not null) _slideTo = width;   // still sliding: towards where the pointer is now
        else if (width != _chatListWidth) ApplyChatListWidth(width);
    }

    private void ApplyChatListWidth(double width)
    {
        _chatListWidth = width;
        ChatListColumn.Width = new GridLength(width);
        ChatListPane.Visibility = width < 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───── Slide (double-click / reopen) ─────

    private EventHandler<object>? _widthAnimation;
    private double _slideTo;   // where the slide ends (a drag can move it while it runs)

    private void AnimateChatListWidth(double to)
    {
        StopWidthAnimation();
        var from = ChatListColumn.ActualWidth;
        if (Math.Abs(from - to) < 1) { ApplyChatListWidth(to); return; }
        if (to > 0) ChatListPane.Visibility = Visibility.Visible;

        _slideTo = to;
        var start = DateTime.UtcNow;
        const double duration = 220;
        _widthAnimation = (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / duration);
            var eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            ChatListColumn.Width = new GridLength(Math.Round(from + (_slideTo - from) * eased));
            if (t >= 1)
            {
                StopWidthAnimation();
                ApplyChatListWidth(_slideTo);
            }
        };
        CompositionTarget.Rendering += _widthAnimation;
    }

    private void StopWidthAnimation()
    {
        if (_widthAnimation is null) return;
        CompositionTarget.Rendering -= _widthAnimation;
        _widthAnimation = null;
    }

    /// <summary>Picking anything on the rail brings a closed list back.</summary>
    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) => SetChatListCollapsed(false);
}
