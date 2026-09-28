using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;
using WhatsAppNative.ViewModels;

namespace WhatsAppNative;

public sealed partial class MainWindow : Window
{
    private readonly CoreClient? _core;
    private CallWindow? _call;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        // Live WhatsApp unless `--sample` asks for placeholder data. A missing core is an error,
        // never a silent switch to sample data (that looked like a lost login).
        var sample = Environment.GetCommandLineArgs().Contains("--sample");
        if (!sample) _core = new CoreClient(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        ViewModel = new MainViewModel(_core);

        InitializeComponent();
        SetupSwipe();
        SetupChatListPane();
        BuildQuickReactionSlots();
        DeveloperModeSwitch.IsOn = _ui.DeveloperMode;
        ApplyDeveloperMode();

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ApplyThemeArgument();
        WindowHelper.ApplyCaptionColors(this, Root.ActualTheme);
        Root.ActualThemeChanged += (_, _) => WindowHelper.ApplyCaptionColors(this, Root.ActualTheme);

        WindowHelper.SizeAndCenter(this, 1100, 720);
        WindowHelper.SetMinimumSize(this, 760, 500);

        // x:Bind fills the list on Loading, so the initial selection has to wait until then.
        ChatList.Loaded += (_, _) => ChatList.SelectedItem = ViewModel.SelectedChat;
        Messages.Loaded += (_, _) => ScrollToBottom();
        ViewModel.ConversationChanged += ScrollToBottom;
        Closed += (_, _) =>
        {
            _call?.Close();
            _core?.Dispose();
        };

        if (_core is not null)
        {
            if (!CoreClient.IsAvailable)
                ViewModel.ReportError($"The WhatsApp connection ({CoreClient.ExecutablePath}) is missing. Rebuild WAFluent. Your login and chats are safe.");
            else
                try { _core.Start(); }
                catch (Exception e) { ViewModel.ReportError($"Couldn't start the WhatsApp connection: {e.Message}"); }
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Log out?",
            Content = "WAFluent will be unlinked from your phone and the chats saved on this PC will be removed.",
            PrimaryButtonText = "Log out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ViewModel.Logout();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>`--theme light` / `--theme dark` forces a theme (handy for checking both).</summary>
    private void ApplyThemeArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--theme");
        if (i < 0 || i + 1 >= args.Length) return;
        Root.RequestedTheme = args[i + 1].Equals("light", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Light : ElementTheme.Dark;
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
        var isSettings = section == "Settings";
        SectionPlaceholder.Visibility = isChats || isSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        LogoutButton.Visibility = ViewModel.IsLive ? Visibility.Visible : Visibility.Collapsed;
        (SectionPlaceholderIcon.Glyph, SectionPlaceholderText.Text) = section switch
        {
            "Calls" => (Glyphs.Phone, "No recent calls"),
            "Status" => (Glyphs.Status, "No status updates"),
            "Starred" => (Glyphs.Star, "No starred messages"),
            "Archived" => (Glyphs.Archive, "No archived chats"),
            "Settings" => (Glyphs.Settings, "Settings are coming soon"),
            "Profile" => (Glyphs.Contact, "Your profile appears here once linked"),
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

    // ───────────── Calls ─────────────

    private void VoiceCall_Click(object sender, RoutedEventArgs e) => StartCall(video: false);

    private void VideoCall_Click(object sender, RoutedEventArgs e) => StartCall(video: true);

    private void StartCall(bool video)
    {
        if (ViewModel.SelectedChat is not { } chat) return;
        if (_call is not null)
        {
            _call.Activate();   // one call at a time
            return;
        }
        _call = new CallWindow(chat, video, Root.RequestedTheme, this);
        _call.Closed += (_, _) => _call = null;
        _call.Activate();
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
        // Esc drops the quote; Enter sends; Shift+Enter inserts a new line.
        if (e.Key == VirtualKey.Escape && ViewModel.IsReplying)
        {
            ViewModel.CancelReply();
            e.Handled = true;
            return;
        }
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
        OpenEmojiPanel();
    }

    // ───────────── Helpers ─────────────

    private DateTime _ignoreScrollUntil;

    /// <summary>Near the top of the conversation: fetch older messages.</summary>
    private void MessagesScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (DateTime.Now < _ignoreScrollUntil) return;   // our own jump to the bottom
        if (MessagesScroller.ScrollableHeight > 0 && MessagesScroller.VerticalOffset < 400)
            ViewModel.LoadOlder(ViewModel.SelectedChat);
    }

    private void ScrollToBottom()
    {
        _ignoreScrollUntil = DateTime.Now.AddMilliseconds(800);
        // Wait for the repeater to measure the new items, then jump to the end.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            MessagesScroller.UpdateLayout();
            MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null, disableAnimation: true);
        });
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);
}
