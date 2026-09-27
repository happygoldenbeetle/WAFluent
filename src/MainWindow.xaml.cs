using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WhatsAppNative.Models;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        SizeAndCenter(1100, 720);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var scale = Scale();
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(500 * scale);
        }

        // x:Bind fills the list on Loading, so the initial selection has to wait until then.
        ChatList.Loaded += (_, _) => ChatList.SelectedItem = ViewModel.SelectedChat;
        Messages.Loaded += (_, _) => ScrollToBottom();
    }

    // ───────────── Sidebar ─────────────

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
        ChatList.SelectedItem = ViewModel.SelectedChat;
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Filtering can clear the ListView selection; keep the open conversation in that case.
        if (ChatList.SelectedItem is not Chat chat || chat == ViewModel.SelectedChat) return;
        ViewModel.SelectedChat = chat;
        ScrollToBottom();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tab = sender.SelectedItem?.Tag as string;
        var isChats = tab == "Chats";
        ChatList.Visibility = isChats ? Visibility.Visible : Visibility.Collapsed;
        TabPlaceholder.Visibility = isChats ? Visibility.Collapsed : Visibility.Visible;
        (TabPlaceholderIcon.Glyph, TabPlaceholderText.Text) = tab switch
        {
            "Statuses" => ("", "No status updates"),
            "Calls" => ("", "No recent calls"),
            _ => ("", ""),
        };
    }

    // ───────────── Composer ─────────────

    private void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a new line.
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shift.HasFlag(CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        SendCurrent();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        if (!ViewModel.Send(ComposerBox.Text)) return;
        ComposerBox.Text = "";
        ComposerBox.Focus(FocusState.Programmatic);
        ScrollToBottom();
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        // Open the Windows emoji panel (Win + .) for the message box.
        ComposerBox.Focus(FocusState.Programmatic);
        const byte VK_LWIN = 0x5B, VK_OEM_PERIOD = 0xBE;
        const uint KEYUP = 0x2;
        keybd_event(VK_LWIN, 0, 0, 0);
        keybd_event(VK_OEM_PERIOD, 0, 0, 0);
        keybd_event(VK_OEM_PERIOD, 0, KEYUP, 0);
        keybd_event(VK_LWIN, 0, KEYUP, 0);
    }

    // ───────────── Helpers ─────────────

    private void ScrollToBottom()
    {
        // Wait for the repeater to measure the new items, then jump to the end.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            MessagesScroller.UpdateLayout();
            MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private double Scale() => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    private void SizeAndCenter(int width, int height)
    {
        var scale = Scale();
        var size = new SizeInt32((int)(width * scale), (int)(height * scale));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        size.Width = Math.Min(size.Width, work.Width);
        size.Height = Math.Min(size.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2,
            size.Width, size.Height));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);
}
