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
        Helpers.Ui.IMessage = !_ui.ClassicBubbles;   // before any bubble is drawn
#if DEBUG
        // WAFLUENT_BUBBLES=classic|imessage previews a style without changing your settings.
        var style = Environment.GetEnvironmentVariable("WAFLUENT_BUBBLES");
        if (style is "classic" or "imessage") Helpers.Ui.IMessage = style == "imessage";
#endif

        InitializeComponent();
        SetupSwipe();
        EmojiData.Warm();
        SetupChatListPane();
        BuildQuickReactionSlots();
        SetupChatMenus();
        SetupInfoPanel();
        SetupTheme();
        SetupTray();
        SetupBubbleMotion();
        SetupStickers();
        DeveloperModeSwitch.IsOn = _ui.DeveloperMode;
        SystemAccentSwitch.IsOn = _ui.UseSystemAccent;
        Helpers.AppColors.Changed += RefreshTheme;
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
        ViewModel.MessageArrived += m =>
        {
            // Follow new messages only when you're already at the bottom, not while reading older ones.
            if (m.IsOutgoing || MessagesScroller.ScrollableHeight - MessagesScroller.VerticalOffset < 160) ScrollToBottom();
            else if (!m.IsOutgoing) { _missedWhileUp++; UpdateJumpDown(); }   // count it on the jump button
        };
        Controls.VoicePlayer.PictureFor = ViewModel.VoicePicture;
        ViewModel.ChatOpened += chat => ChatList.SelectedItem = chat;
        // Open a chat and start typing: the composer takes focus (after the chat has drawn).
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) _missedWhileUp = 0;
            if (e.PropertyName == nameof(ViewModel.SelectedChat) && ViewModel.SelectedChat is not null)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ComposerBox.Focus(FocusState.Programmatic));
        };
        // Online while the window is in front, like WhatsApp Desktop.
        Activated += (_, e) => ViewModel.SetPresence(e.WindowActivationState != WindowActivationState.Deactivated);
#if DEBUG
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "attach")
            Messages.Loaded += async (_, _) => { await Task.Delay(3000); Attach_Click(AttachButton, new RoutedEventArgs()); };
        // WAFLUENT_SELFTEST=poll-dialog | contact-dialog | photo-dialog: opens that attach dialog.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is "poll-dialog" or "contact-dialog" or "photo-dialog")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                switch (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST"))
                {
                    case "poll-dialog": OpenPoll(); break;
                    case "contact-dialog": OpenContacts(); break;
                    default:
                        var files = new List<Windows.Storage.StorageFile>();
                        foreach (var path in Directory.GetFiles(@"C:\Windows\Web\Screen", "*.jpg").Take(2))
                            files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                        files.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(@"C:\Windows\win.ini"));
                        await OpenComposerAsync(files, documents: false);
                        break;
                }
            };
        // WAFLUENT_SELFTEST=uploading: "sends" a photo and a document and holds them uploading.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "uploading")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                var files = new List<Windows.Storage.StorageFile>
                {
                    await Windows.Storage.StorageFile.GetFileFromPathAsync(Directory.GetFiles(@"C:\Windows\Web\Screen", "*.jpg")[0]),
                    await Windows.Storage.StorageFile.GetFileFromPathAsync(@"C:\Windows\win.ini"),
                };
                await OpenComposerAsync(files, documents: false);
                SendComposer();
                foreach (var m in ViewModel.SelectedChat!.Messages.TakeLast(2)) { m.IsUploading = true; m.Delivery = Delivery.Pending; }
            };
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "menu")
            Messages.Loaded += async (_, _) => { await Task.Delay(3000); SelfTestMenu(); };
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "links")
            Messages.Loaded += async (_, _) => { await Task.Delay(4000); SelfTestLinks(); };
        // WAFLUENT_SELFTEST=poll: votes for the second option of the first poll after 6 s.
        // WAFLUENT_SELFTEST=typing: the open chat "types" after 3 s, then a reply arrives at 6 s.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "typing")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                if (ViewModel.SelectedChat is not { } chat) return;
                chat.TypingText = "typing…";
                await Task.Delay(3000);
                ViewModel.SimulateIncoming(chat, "On my way! 🚗");
            };
        // WAFLUENT_SELFTEST=stickers: opens the sticker panel with placeholder items after 3 s.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "stickers")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                Stickers_Click(StickerButton, new RoutedEventArgs());
                var pictures = Directory.GetFiles(@"C:\Windows\Web\Screen", "*.jpg").Take(6).ToList();
                var items = pictures.Select((p, i) => new StickerDto("sample", $"s{i}", 512, 512, p, null)).ToList();
                _stickerPanel?.SetItems(items[..2], items[2..], []);
            };
        // WAFLUENT_SELFTEST=gifs: the GIF side, searching with WAFLUENT_GIPHY or the built-in key.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "gifs")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                Stickers_Click(StickerButton, new RoutedEventArgs());
                var pictures = Directory.GetFiles(@"C:\Windows\Web\Screen", "*.jpg").Take(3).ToList();
                var items = pictures.Select((p, i) => new StickerDto("sample", $"g{i}", 512, 512, null, null)).ToList();
                _stickerPanel!.GiphyKey = Environment.GetEnvironmentVariable("WAFLUENT_GIPHY") ?? Services.Giphy.BuiltInKey;
                _stickerPanel.SetItems([], [], items);
                _stickerPanel.ShowTab(gifs: true);
            };
        var pollTestDone = false;
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "poll")
            Messages.Loaded += async (_, _) =>
            {
                if (pollTestDone) return;   // Loaded can fire more than once
                pollTestDone = true;
                await Task.Delay(6000);
                var log = Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt");
                if (ViewModel.SelectedChat?.Messages.FirstOrDefault(m => m.Kind == MessageKind.Poll) is { } poll)
                {
                    ViewModel.VotePoll(poll.PollOptions[1]);
                    var after = ViewModel.SelectedChat.Messages.First(m => m.Id == poll.Id);
                    var index = ViewModel.SelectedChat.Messages.IndexOf(after);
                    var before = Messages.TryGetElement(index) as FrameworkElement;
                    var line = $"voted; replaced: {!ReferenceEquals(after, poll)}; selected now: {string.Join(",", after.PollOptions.Where(o => o.Selected).Select(o => o.Name))}; " +
                               $"element right after: {before?.GetHashCode()} context is new: {ReferenceEquals(before?.DataContext, after)} is old: {ReferenceEquals(before?.DataContext, poll)}";
                    await Task.Delay(500);
                    var later = Messages.TryGetElement(index) as FrameworkElement;
                    line += $"; 500 ms later: {later?.GetHashCode()} context is new: {ReferenceEquals(later?.DataContext, after)}, tag is new: {ReferenceEquals(later?.Tag, after)}";
                    File.WriteAllText(log, $"{DateTime.Now:HH:mm:ss.fff} " + line);
                }
                else File.WriteAllText(log, $"no poll; chat {ViewModel.SelectedChat?.Name}");
            };
#endif
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
            DefaultButton = ContentDialogButton.None,
        };
        DangerButtons(dialog);
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
        _themeFromArgs = true;   // wins over Settings → Theme for this run
    }

    // ───────────── Rail + chat list ─────────────

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ListTitle is null) return;   // initial IsSelected fires during InitializeComponent
        var section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Chats";
        var isList = section is "Chats" or "Archived";   // both are the chat list, filtered
        var isStarred = section == "Starred";
        var isSettings = section == "Settings";

        ListTitle.Text = section switch
        {
            "Starred" => "Starred messages",
            "Archived" => "Archived",
            _ => section,
        };
        ViewModel.ShowArchived = section == "Archived";
        if (isStarred) ViewModel.LoadStarred();

        ListActions.Visibility = section == "Chats" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        FilterChips.Visibility = section == "Chats" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Archived" && ViewModel.Filter != ChatFilter.All) SetFilter(ChatFilter.All);
        ChatList.Visibility = isList ? Visibility.Visible : Visibility.Collapsed;
        StarredList.Visibility = isStarred ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        LogoutButton.Visibility = ViewModel.IsLive ? Visibility.Visible : Visibility.Collapsed;
        UpdateSectionPlaceholder(section);
    }

    /// <summary>The "nothing here" message: other sections, an empty Archived or Starred list.</summary>
    private void UpdateSectionPlaceholder(string section)
    {
        var empty = section switch
        {
            "Chats" or "Settings" => false,
            "Archived" => ViewModel.ArchivedCount == 0,
            "Starred" => !ViewModel.HasStarred,
            _ => true,
        };
        SectionPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        (SectionPlaceholderIcon.Glyph, SectionPlaceholderText.Text) = section switch
        {
            "Calls" => (Glyphs.Phone, "No recent calls"),
            "Status" => (Glyphs.Status, "No status updates"),
            "Starred" => (Glyphs.Star, "No starred messages"),
            "Archived" => (Glyphs.Archive, "No archived chats"),
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
        UpdateShortcodes();
        var hasText = ComposerBox.Text.Trim().Length > 0;
        ViewModel.ComposerEdited(hasText);
        SendButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        MicButton.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The :shortcode: list gets ↑ ↓ Enter Tab Esc first.
        if (ShortcodeKey(e.Key))
        {
            e.Handled = true;
            return;
        }
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
        ViewModel.StopTyping();
        ComposerBox.Text = "";
        ComposerBox.Focus(FocusState.Programmatic);
        ScrollToBottom();
    }

    // ───────────── Helpers ─────────────

    private DateTime _ignoreScrollUntil;

    /// <summary>Near the top of the conversation: fetch older messages.</summary>
    private void MessagesScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateJumpDown();
        if (DateTime.Now < _ignoreScrollUntil) return;   // our own jump to the bottom
        if (MessagesScroller.ScrollableHeight > 0 && MessagesScroller.VerticalOffset < 400)
            ViewModel.LoadOlder(ViewModel.SelectedChat);
    }

    private void ScrollToBottom()
    {
        _ignoreScrollUntil = DateTime.Now.AddMilliseconds(800);
        // Wait for the repeater to measure the new items, then jump to the end. Rows measured on
        // the way down can make the list taller than guessed, so check again until it's settled.
        void Jump(int triesLeft)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                MessagesScroller.UpdateLayout();
                MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null, disableAnimation: true);
                MessagesScroller.UpdateLayout();
                if (triesLeft > 0 && MessagesScroller.ScrollableHeight - MessagesScroller.VerticalOffset > 1) Jump(triesLeft - 1);
            });
        }
        Jump(3);
    }
}
