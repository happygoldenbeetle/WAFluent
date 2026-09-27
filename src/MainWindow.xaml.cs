using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
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

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ApplyThemeArgument();
        UpdateCaptionButtons();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();

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

    /// <summary>`--theme light` / `--theme dark` forces a theme (handy for checking both).</summary>
    private void ApplyThemeArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--theme");
        if (i < 0 || i + 1 >= args.Length) return;
        Root.RequestedTheme = args[i + 1].Equals("light", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Light : ElementTheme.Dark;
    }

    /// <summary>Caption buttons don't follow an app-forced theme on their own.</summary>
    private void UpdateCaptionButtons()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    // ───────────── Rail + chat list ─────────────

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ListTitle is null) return;   // initial IsSelected fires during InitializeComponent
        var section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Chats";
        var isChats = section == "Chats";

        ListTitle.Text = section switch
        {
            "Starred" => "Starred messages",
            "Archived" => "Archived",
            _ => section,
        };
        ListActions.Visibility = isChats ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = isChats ? Visibility.Visible : Visibility.Collapsed;
        ChatList.Visibility = isChats ? Visibility.Visible : Visibility.Collapsed;
        SectionPlaceholder.Visibility = isChats ? Visibility.Collapsed : Visibility.Visible;
        (SectionPlaceholderIcon.Glyph, SectionPlaceholderText.Text) = section switch
        {
            "Calls" => ("", "No recent calls"),
            "Status" => ("", "No status updates"),
            "Starred" => ("", "No starred messages"),
            "Archived" => ("", "No archived chats"),
            "Settings" => ("", "Settings are coming soon"),
            "Profile" => ("", "Your profile appears here once linked"),
            _ => ("", ""),
        };
    }

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

    // ───────────── Composer ─────────────

    private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = ComposerBox.Text.Trim().Length > 0;
        SendButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        MicButton.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

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
