using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Back and forward, like a browser: the mouse's side buttons (or Alt+← / Alt+→). Back first
/// closes whatever is on top — a video, the photo viewer, an album, the send preview, a card,
/// the side panel (a page inside it goes back to Contact info) — and otherwise returns to the
/// chat you had open before; forward goes the other way again.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Stack<Chat> _backChats = new();
    private readonly Stack<Chat> _forwardChats = new();
    private Chat? _navCurrent;
    private bool _navigating;
    /// <summary>When a side button was last pressed: a quick second press isn't a double-click (react).</summary>
    private DateTime _sideButtonAt;

    private void SetupNavigation()
    {
        _navCurrent = ViewModel.SelectedChat;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModel.SelectedChat)) return;
            var next = ViewModel.SelectedChat;
            // The chat list's highlight follows the open chat however it was opened (back,
            // forward, Starred, the mini player…). After it has opened: opening re-sorts the
            // list (its unread count clears), and the highlight mustn't land mid-shuffle.
            DispatcherQueue.TryEnqueue(SyncListSelection);
            if (!_navigating && _navCurrent is not null && next != _navCurrent)
            {
                _backChats.Push(_navCurrent);
                _forwardChats.Clear();   // a new path: nothing ahead any more
            }
            _navCurrent = next;
        };
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Navigation_PointerPressed), handledEventsToo: true);

        var back = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        back.Invoked += (_, e) => e.Handled = GoBack();
        var forward = new KeyboardAccelerator { Key = VirtualKey.Right, Modifiers = VirtualKeyModifiers.Menu };
        forward.Invoked += (_, e) => e.Handled = GoForward();
        Root.KeyboardAccelerators.Add(back);
        Root.KeyboardAccelerators.Add(forward);
    }

    /// <summary>Set while the list's highlight is moved here, so it doesn't count as a click on that chat.</summary>
    private bool _syncingList;

    private void SyncListSelection()
    {
        var open = ViewModel.SelectedChat;
        var listed = open is not null && ViewModel.Chats.Contains(open) ? open : null;
        if (ChatList.SelectedItem == listed) return;
        _syncingList = true;
        try { ChatList.SelectedItem = listed; }
        finally { _syncingList = false; }
    }

    private void Navigation_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var buttons = e.GetCurrentPoint(Root).Properties;
        if (buttons.IsXButton1Pressed || buttons.IsXButton2Pressed) _sideButtonAt = DateTime.Now;
        if (buttons.IsXButton1Pressed)
        {
            GoBack();
            e.Handled = true;
        }
        else if (buttons.IsXButton2Pressed)
        {
            GoForward();
            e.Handled = true;
        }
    }

    private bool GoBack()
    {
        if (CloseTopLayer()) return true;
        while (_backChats.TryPop(out var chat))
        {
            if (!ViewModel.Exists(chat) || chat == _navCurrent) continue;
            if (_navCurrent is { } current) _forwardChats.Push(current);
            Navigate(chat);
            return true;
        }
        return false;
    }

    private bool GoForward()
    {
        while (_forwardChats.TryPop(out var chat))
        {
            if (!ViewModel.Exists(chat) || chat == _navCurrent) continue;
            if (_navCurrent is { } current) _backChats.Push(current);
            Navigate(chat);
            return true;
        }
        return false;
    }

    private void Navigate(Chat chat)
    {
        _navigating = true;
        try { ViewModel.SelectedChat = chat; }
        finally { _navigating = false; }
        ScrollToStart();   // as when it's clicked in the list
    }

    /// <summary>Closes the topmost thing over the conversation; false when there's nothing to close.</summary>
    private bool CloseTopLayer()
    {
        if (StatusViewer.Visibility == Visibility.Visible) CloseStatusViewer();
        else if (VideoViewer.Visibility == Visibility.Visible) CloseVideo();
        else if (Lightbox.Visibility == Visibility.Visible) CloseViewer();
        else if (AlbumView.Visibility == Visibility.Visible) CloseAlbum();
        else if (MediaComposer.Visibility == Visibility.Visible) _ = DiscardComposerAsync();
        else if (AttachSheet.Visibility == Visibility.Visible) CloseSheet();
        else if (InfoPanel.Visibility == Visibility.Visible) InfoClose_Click(this, new RoutedEventArgs());
        else return false;
        return true;
    }
}
