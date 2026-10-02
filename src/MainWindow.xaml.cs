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

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        // Live WhatsApp unless `--sample` asks for placeholder data. A missing core is an error,
        // never a silent switch to sample data (that looked like a lost login).
        var sample = Environment.GetCommandLineArgs().Contains("--sample");
        if (!sample) _core = new CoreClient(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        ViewModel = new MainViewModel(_core);
        Helpers.Ui.IMessage = true;   // the round bubbles (classic ones were a setting once)
        Helpers.Format.Use24Hour = _ui.Use24Hour;   // before any bubble is drawn
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

        WindowHelper.SetMinimumSize(this, 760, 500);
        RestoreWindowPlacement();

        // x:Bind fills the list on Loading, so the initial selection has to wait until then.
        ChatList.Loaded += (_, _) => ChatList.SelectedItem = ViewModel.SelectedChat;
        Messages.Loaded += (_, _) => ScrollToStart();
        ViewModel.ConversationChanged += ScrollToStart;
        ViewModel.MessageArrived += m =>
        {
            // Follow new messages only when you're already at the bottom, not while reading older ones.
            if (m.IsOutgoing || MessagesScroller.ScrollableHeight - MessagesScroller.VerticalOffset < 160) ScrollToBottom();
            else if (!m.IsOutgoing) { _missedWhileUp++; UpdateJumpDown(); }   // count it on the jump button
        };
        Controls.VoicePlayer.PictureFor = ViewModel.VoicePicture;
        Controls.LinkText.MentionClicked = OpenMention;
        // Album tiles open their item like a single photo or video, and have its menu.
        Controls.AlbumGrid.Open = (item, tile) =>
        {
            if (ViewModel.IsSelecting) return;
            if (item.Kind == MessageKind.Video) WhenDownloaded(item, _ => OpenVideo(item, tile));
            else if (item.MediaPath is { } path && File.Exists(path)) OpenViewer(item, tile);
        };
        Controls.AlbumGrid.Menu = (item, tile, e) => Message_ContextRequested(tile, e);
        Controls.AlbumGrid.DragOut = Media_DragStarting;
        Controls.AlbumGrid.Expand = OpenAlbum;
        ViewModel.ChatOpened += chat => ChatList.SelectedItem = chat;
        // Open a chat and start typing: the composer takes focus (after the chat has drawn).
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) _missedWhileUp = 0;
            if (e.PropertyName == nameof(ViewModel.SelectedChat) && ViewModel.SelectedChat is not null)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ComposerBox.Focus(FocusState.Programmatic));
        };
        // Online while the window is in front, like WhatsApp Desktop.
        Activated += (_, e) =>
        {
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            ViewModel.SetPresence(_windowActive);
            if (_windowActive && ViewModel.SelectedChat is { } open) Notifications.Clear(open.Id);
        };
        SetupNotifications();
        SetupCalls();
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
        // WAFLUENT_SELFTEST=compress: SD and HD of WAFLUENT_TEST_PHOTO / WAFLUENT_TEST_VIDEO, to %TEMP%\wafluent-selftest.txt.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "compress")
            Messages.Loaded += async (_, _) =>
            {
                var lines = new List<string>();
                foreach (var hd in new[] { false, true })
                {
                    try
                    {
                        var photo = Environment.GetEnvironmentVariable("WAFLUENT_TEST_PHOTO")!;
                        var (p, w, h) = await MediaCompression.PhotoAsync(photo, hd);
                        lines.Add($"photo {(hd ? "HD" : "SD")}: {w}x{h} {new FileInfo(p).Length / 1024} KB (from {new FileInfo(photo).Length / 1024} KB)");
                        if (Environment.GetEnvironmentVariable("WAFLUENT_TEST_GIF") is { Length: > 0 } gif)
                        {
                            lines.Add($"sniffed: {MediaCompression.Sniff(gif)}");
                            if (await MediaCompression.GifAsync(gif) is var (gp, gw, gh, gs))
                                lines.Add($"gif: {gw}x{gh} {gs}s {new FileInfo(gp).Length / 1024} KB -> {gp}");
                            else
                                lines.Add("gif: one frame (sent as a photo)");
                        }
                        var video = Environment.GetEnvironmentVariable("WAFLUENT_TEST_VIDEO")!;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (vp, vw, vh) = await MediaCompression.VideoAsync(video, 1920, 1080, hd, CancellationToken.None);
                        lines.Add($"video {(hd ? "HD" : "SD")}: {vw}x{vh} {new FileInfo(vp).Length / 1024} KB (from {new FileInfo(video).Length / 1024} KB) in {sw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception e) { lines.Add(e.ToString()); }
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
            };
        // WAFLUENT_SELFTEST=video: plays WAFLUENT_TEST_VIDEO in the video lightbox.
        // WAFLUENT_SELFTEST=lightbox: opens the last photo on screen in the photo lightbox.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is "video" or "lightbox")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "video")
                {
                    if (Environment.GetEnvironmentVariable("WAFLUENT_TEST_CHAT") is { Length: > 0 } name
                        && ViewModel.Chats.FirstOrDefault(c => c.Name == name) is { } testChat)
                    {
                        ChatList.SelectedItem = testChat;
                        await Task.Delay(1500);
                    }
                    // From a video bubble on screen when there is one (the flight), else on its own.
                    var bubble = Descendants(Messages).OfType<FrameworkElement>()
                        .Where(f => f.Tag is Message { Kind: MessageKind.Video, IsGif: false } && f.ActualWidth > 0)
                        .OrderBy(f => f.ActualWidth).FirstOrDefault();
                    var video = bubble?.Tag as Message ?? new Message { Kind = MessageKind.Video, HasMedia = true, MediaWidth = 300, MediaHeight = 200 };
                    video.MediaPath = Environment.GetEnvironmentVariable("WAFLUENT_TEST_VIDEO");
                    OpenVideo(video, bubble);
                    return;
                }
                var picture = Descendants(Messages).OfType<FrameworkElement>()
                    .Where(f => f.Tag is Message { Kind: MessageKind.Image, MediaPath: not null } && f.ActualWidth > 0)
                    .OrderBy(f => f.ActualWidth).FirstOrDefault();
                if (picture?.Tag is Message m) OpenViewer(m, picture);
            };
        // WAFLUENT_SELFTEST=search: searches the open chat for WAFLUENT_TEST_QUERY.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "search")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                OpenSearch();
                MessageSearchBox.Text = Environment.GetEnvironmentVariable("WAFLUENT_TEST_QUERY") ?? "the";
            };
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "info")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                if (ViewModel.SelectedChat?.Messages.LastOrDefault(m => m.IsOutgoing && m.Kind == MessageKind.Text) is { } mine) OpenMessageInfo(mine);
            };
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "edit")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                if (ViewModel.SelectedChat?.Messages.LastOrDefault(m => m.IsOutgoing && m.Kind == MessageKind.Text) is { } mine)
                {
                    mine.UnixTs = DateTimeOffset.Now.ToUnixTimeSeconds();   // sample messages are old
                    BeginEdit(mine);
                }
            };
        // WAFLUENT_SELFTEST=open: opens the chat named WAFLUENT_TEST_CHAT.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "open")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                if (ViewModel.Chats.FirstOrDefault(c => c.Name == Environment.GetEnvironmentVariable("WAFLUENT_TEST_CHAT")) is { } target)
                    ChatList.SelectedItem = target;
            };
        // WAFLUENT_SELFTEST=mention: in WAFLUENT_TEST_CHAT, types "hi @" (the list opens).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "mention")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                if (ViewModel.Chats.FirstOrDefault(c => c.Name == Environment.GetEnvironmentVariable("WAFLUENT_TEST_CHAT")) is { } target)
                    ChatList.SelectedItem = target;
                await Task.Delay(1000);
                ComposerBox.Text = "hi @";
                ComposerBox.SelectionStart = ComposerBox.Text.Length;
                UpdateMentions();
                if (Environment.GetEnvironmentVariable("WAFLUENT_TEST_SEND") == "1")
                {
                    await Task.Delay(800);
                    ApplyMention();
                    ComposerBox.Text += "see you there";
                    SendCurrent();
                }
            };
        // WAFLUENT_SELFTEST=link: types WAFLUENT_TEST_TEXT into the composer (the link card should appear).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "link")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                ComposerBox.Text = Environment.GetEnvironmentVariable("WAFLUENT_TEST_TEXT") ?? "";
                ComposerBox.SelectionStart = ComposerBox.Text.Length;
                if (Environment.GetEnvironmentVariable("WAFLUENT_TEST_SEND") == "1")
                {
                    await Task.Delay(6000);
                    SendCurrent();
                }
            };
        // WAFLUENT_SELFTEST=voice: encodes WAFLUENT_TEST_WAV (no microphone) to %TEMP%\wafluent-selftest.txt,
        // and shows the recording bar.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "voice")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var result = VoiceRecorder.EncodeWav(Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV")!);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"),
                    result is var (p, s, w) ? $"{p}\n{s}\n{string.Join(",", w)}" : "none");
                RecordingTime.Text = "0:04";
                var rnd = new Random(4);
                for (var i = 0; i < 48; i++) RecordingWave.Push(i < 14 ? 0.3 + rnd.NextDouble() * 0.6 : rnd.NextDouble() < 0.2 ? 0.1 : 0);
                RecordingBar.Visibility = Visibility.Visible;
            };
        // WAFLUENT_SELFTEST=notify: notifies for a sample chat, counts WAFluent's notifications, removes them.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "notify")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var chat = ViewModel.Chats.First(c => c.Name == "Maya Kasuma");
                chat.IsMuted = false;
                _windowActive = false;
                Notify(chat, new Message { Id = "selftest-1", Text = "Test notification from WAFluent", Kind = MessageKind.Text });
                await Task.Delay(1500);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), $"shown={Notifications.Count()}");
                Notifications.ClearAll();
            };
        // WAFLUENT_SELFTEST=reply: replies to the photo in the open chat (the quote above the composer).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "reply")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                if (ViewModel.SelectedChat?.Messages.FirstOrDefault(m => m.Kind == MessageKind.Image) is { } photo) ViewModel.BeginReply(photo);
            };
        // WAFLUENT_SELFTEST=miniplayer: opens the open chat's voice note (WAFLUENT_TEST_WAV, paused at 40 %), then
        // another chat, so the mini player shows. It never plays out loud.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "miniplayer")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                if (ViewModel.SelectedChat?.Messages.FirstOrDefault(m => m.Kind == MessageKind.Voice) is not { } note) return;
                note.MediaPath = Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV");
                AudioPlayback.Load(note);   // paused: nothing plays
                AudioPlayback.Seek(note, 0.4);
                ChatList.SelectedItem = ViewModel.Chats.First(c => c.Name == "Baking Club");
            };
        // WAFLUENT_SELFTEST=dragout: the named copies a drag out of the photo and the document would hand to Explorer
        // (to %TEMP%\wafluent-selftest.txt), then the photo's menu.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "dragout")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var lines = new List<string>();
                foreach (var m in ViewModel.SelectedChat!.Messages.Where(m => m.Kind is MessageKind.Image or MessageKind.File && m.MediaPath is not null))
                {
                    var copy = NamedCopy(m, m.MediaPath!);
                    lines.Add($"{Path.GetFileName(copy)} | same={File.ReadAllBytes(copy).AsSpan().SequenceEqual(File.ReadAllBytes(m.MediaPath!))}");
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
                if (ViewModel.SelectedChat.Messages.LastOrDefault(m => m.Kind == MessageKind.Image) is { } photo)
                    BuildMessageMenu(photo, Messages, null).ShowAt(Messages, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Windows.Foundation.Point(600, 300) });
            };
        // WAFLUENT_SELFTEST=forward: opens Forward message to for the open chat's last message, two chats ticked.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "forward")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                await ForwardAsync([ViewModel.SelectedChat!.Messages.Last(m => m.Kind == MessageKind.Text)]);
                foreach (var row in _contacts.Skip(1).Take(2)) row.Picked = true;
                UpdatePickedContacts();
            };
        // WAFLUENT_SELFTEST=drafts | back: types a draft in the open chat and opens another ("Draft: …" in the list);
        // "back" then goes back (the mouse's back button), and the draft is in the composer again.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is "drafts" or "back")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                ComposerBox.Text = "See you at 6, can you bring the";
                await Task.Delay(300);
                ViewModel.SelectedChat = ViewModel.Chats.First(c => c.Name == "Baking Club");
                if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") != "back") return;
                await Task.Delay(800);
                GoBack();
            };
        // WAFLUENT_SELFTEST=gallery | gallery-docs | gallery-links: Contact info › Media, links and docs on that tab.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is { } galleryTest && galleryTest.StartsWith("gallery"))
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                OpenInfo();
                await Task.Delay(500);
                OpenGallery();
                GalleryTabs.Items[galleryTest switch { "gallery-docs" => 1, "gallery-links" => 2, _ => 0 }].IsSelected = true;
            };
        // WAFLUENT_SELFTEST=recorder: "records" WAFLUENT_TEST_WAV through the recorder's audio graph (not the microphone)
        // for 2 s; the recorded length and level go to %TEMP%\wafluent-selftest.txt.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "recorder")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                VoiceRecorder.TestInput = Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV");
                var recorder = new VoiceRecorder();
                string result;
                try
                {
                    await recorder.StartAsync();
                    await Task.Delay(2000);
                    result = $"elapsed={recorder.Elapsed.TotalSeconds:0.00} level={recorder.Level:0.000}";
                    await recorder.CancelAsync();
                }
                catch (Exception ex) { result = "failed: " + ex; }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), result);
            };
        // WAFLUENT_SELFTEST=playsent: encodes WAFLUENT_TEST_WAV like a sent voice note and opens it in a muted player;
        // whether it opened (and its length) goes to %TEMP%\wafluent-selftest.txt.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "playsent")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                // WAFLUENT_TEST_OGG: check an existing voice note instead (still muted).
                var encoded = Environment.GetEnvironmentVariable("WAFLUENT_TEST_OGG") is { } ogg
                    ? (ogg, 0, Array.Empty<byte>())
                    : VoiceRecorder.EncodeWav(Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV")!);
                var result = new TaskCompletionSource<string>();
                var player = new Windows.Media.Playback.MediaPlayer { IsMuted = true };
                player.MediaOpened += (p, _) => result.TrySetResult($"opened {p.PlaybackSession.NaturalDuration}");
                player.MediaFailed += (_, e) => result.TrySetResult($"failed {e.Error} {e.ErrorMessage} 0x{e.ExtendedErrorCode?.HResult:X8}");
                player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(encoded!.Value.Path));
                var done = await Task.WhenAny(result.Task, Task.Delay(5000));
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"),
                    $"{encoded.Value.Path} {new FileInfo(encoded.Value.Path).Length} bytes: " + (done == result.Task ? result.Task.Result : "timeout"));
                player.Dispose();
            };
        // WAFLUENT_SELFTEST=sendvoice: sends WAFLUENT_TEST_WAV as a voice note in the open chat, presses its play
        // button (muted) and writes whether it played to %TEMP%\wafluent-selftest.txt.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "sendvoice")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                AudioPlayback.Muted = true;
                var (path, seconds, waveform) = VoiceRecorder.EncodeWav(Environment.GetEnvironmentVariable("WAFLUENT_TEST_WAV")!)!.Value;
                ViewModel.SendVoice(path, seconds, waveform);
                ScrollToBottom();
                await Task.Delay(1500);
                var note = ViewModel.SelectedChat!.Messages.Last(m => m.Kind == MessageKind.Voice);
                var lines = new List<string> { $"path={note.MediaPath} hasFile={note.HasMediaFile} loading={note.IsMediaLoading} delivery={note.Delivery}" };
                var player = FindDescendant(Messages, e => e is Controls.VoicePlayer { Message: { } v } && v == note) as Controls.VoicePlayer;
                var button = player is null ? null : FindDescendant(player, e => e is Button { Name: "PlayButton" }) as Button;
                lines.Add($"player={player is not null} button={button is not null} enabled={button?.IsEnabled}");
                if (button is not null)
                {
                    new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button).Invoke();
                    await Task.Delay(1500);
                    lines.Add($"current={AudioPlayback.Current == note} playing={AudioPlayback.IsPlaying} position={AudioPlayback.Position}");
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
            };
        // WAFLUENT_SELFTEST=disappearing: marks Family Foodies unread (a dot), turns 7 days on for the open chat and
        // opens Contact info › Disappearing messages.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "disappearing")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                ViewModel.ChatAction(ViewModel.Chats.First(c => c.Name == "Family Foodies"), "markUnread");
                ViewModel.SetEphemeral(ViewModel.SelectedChat!, 604_800);
                OpenInfo();
                await Task.Delay(300);
                OpenDisappearing();
            };
        // WAFLUENT_SELFTEST=pins: pins three of the open chat's messages (24 hours, 7 days, 30 days) and shows the second
        // in the banner. WAFLUENT_SELFTEST=chatstarred: stars two messages and opens Contact info › Starred messages.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is "pins" or "chatstarred")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var chat = ViewModel.SelectedChat!;
                var texts = chat.Messages.Where(m => m.Kind == MessageKind.Text && !m.IsDeleted).TakeLast(3).ToList();
                if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "pins")
                {
                    int[] seconds = [86_400, 604_800, 2_592_000];
                    for (var i = 0; i < texts.Count; i++) ViewModel.PinMessage(texts[i], true, seconds[i]);
                    chat.PinIndex = 1;
                    return;
                }
                ViewModel.Star(texts.Take(2), true);
                OpenInfo();
                await Task.Delay(300);
                OpenChatStarred();
            };
        // WAFLUENT_SELFTEST=jump: goes to the open chat's first message (as a pin or a quote does).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "jump")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var first = ViewModel.SelectedChat!.Messages.First(m => m.Kind == MessageKind.Text);
                ViewModel.Reveal(first.Id, first.UnixTs);
            };
        // WAFLUENT_SELFTEST=hand: which cursor the window shows over a button, the conversation's background and a
        // chat row (to %TEMP%\wafluent-selftest.txt); then Contact info › Edit for the open chat.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "hand")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var row = ChatList.ContainerFromIndex(1) as DependencyObject;
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"),
                [
                    "attach button: " + Helpers.HandCursor.Probe(Root, AttachButton),
                    "conversation background: " + Helpers.HandCursor.Probe(Root, MessagesScroller),
                    "chat row: " + (row is null ? "no row" : Helpers.HandCursor.Probe(Root, row)),
                    "composer text box: " + Helpers.HandCursor.Probe(Root, ComposerBox),
                ]);
                OpenInfo();
                await Task.Delay(300);
                OpenNewContact(ViewModel.SelectedChat!, edit: true);
            };
        // WAFLUENT_SELFTEST=micmenu: opens the microphone list beside the mic button.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "micmenu")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                MicDevice_Click(MicDeviceButton, new RoutedEventArgs());
            };
        // WAFLUENT_SELFTEST=micdevices: opens each microphone the way the recorder would (without recording) and
        // writes whether it opened, and which device it was, to %TEMP%\\wafluent-selftest.txt.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "micdevices")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                var lines = new List<string>();
                foreach (var (id, name) in await VoiceRecorder.MicrophonesAsync())
                {
                    string result;
                    try { result = await VoiceRecorder.ProbeAsync(id); }
                    catch (Exception ex) { result = "failed: " + ex.Message; }
                    lines.Add($"{name} -> {result}");
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
            };
        // WAFLUENT_SELFTEST=notices: goes to the open chat's disappearing-messages notices.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "notices")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var notice = ViewModel.SelectedChat!.Messages.Last(m => m.IsTimerNotice);
                ViewModel.Reveal(notice.Id, notice.UnixTs);
            };
        // WAFLUENT_SELFTEST=newchat | newchat-number | newchat-contact | newchat-group: New chat on that page
        // (the group one with three members picked).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is { } newChatTest && newChatTest.StartsWith("newchat"))
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                NewChat_Click(this, new RoutedEventArgs());
                await Task.Delay(400);
                switch (newChatTest)
                {
                    case "newchat-number":
                        NewChatDialpad_Click(this, new RoutedEventArgs());
                        NumberBox.Text = "+92 300 1234567";
                        break;
                    case "newchat-contact":
                        NewContact_Click(this, new RoutedEventArgs());
                        NewContactFirst.Text = "Sara";
                        NewContactPhone.Text = "300 1234567";
                        break;
                    case "newchat-group":
                        NewGroup_Click(this, new RoutedEventArgs());
                        _groupMembers.AddRange(_newChatContacts.Take(3));
                        RebuildGroupChips();
                        FilterNewChat();
                        break;
                    case "newchat-blocked":   // a blocked contact in the list, then picked: the prompt
                        var blocked = _newChatContacts.First(c => c.Name == "Mark Rogers");
                        blocked.IsBlocked = true;
                        blocked.Subtitle = "Contact is blocked";
                        NewGroup_Click(this, new RoutedEventArgs());
                        _groupMembers.AddRange(_newChatContacts.Take(2));
                        RebuildGroupChips();
                        FilterNewChat();
                        await Task.Delay(300);
                        _ = PickMemberAsync(blocked);
                        break;
                }
            };
        // WAFLUENT_SELFTEST=group | groupinfo | groupinfo-name | groupsearch: the sample group at its start (the intro
        // card), its Group info, Group info with the name being edited, and Search messages.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is { } groupTest && groupTest.StartsWith("group"))
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                ChatList.SelectedItem = ViewModel.Chats.First(c => c.Name == "Baking Club");
                await Task.Delay(900);
                switch (groupTest)
                {
                    case "group": MessagesScroller.ChangeView(null, 0, null, true); break;
                    case "groupinfo": OpenInfo(); break;
                    case "groupinfo-name":
                        OpenInfo();
                        await Task.Delay(300);
                        InfoNameEdit_Click(this, new RoutedEventArgs());
                        break;
                    case "groupsearch": OpenSearch(); break;
                }
            };
        // WAFLUENT_SELFTEST=drop: shows what dragging files over the conversation looks like.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "drop")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                DropDetail.Text = $"Send to {ViewModel.SelectedChat?.Name}";
                DropOverlay.Visibility = Visibility.Visible;
            };
        // WAFLUENT_SELFTEST=paste: "pastes" WAFLUENT_TEST_PHOTO as a bare picture (a screenshot), without the clipboard.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "paste")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                var photo = await Windows.Storage.StorageFile.GetFileFromPathAsync(Environment.GetEnvironmentVariable("WAFLUENT_TEST_PHOTO"));
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(photo));
                await PasteFilesAsync(package.GetView());
            };
        // WAFLUENT_SELFTEST=recycle: opens other chats and comes back (rows are put aside and reused).
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "recycle")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                var home = ViewModel.SelectedChat;
                foreach (var name in new[] { "Baking Club", "Jason Ballmer", "Family Foodies" })
                {
                    ChatList.SelectedItem = ViewModel.Chats.First(c => c.Name == name);
                    await Task.Delay(700);
                }
                ChatList.SelectedItem = home;
                await Task.Delay(700);
                ScrollToBottom();
            };
        // WAFLUENT_SELFTEST=album: opens the album in WAFLUENT_TEST_CHAT in the album view.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") == "album")
            Messages.Loaded += async (_, _) =>
            {
                await Task.Delay(2500);
                if (ViewModel.Chats.FirstOrDefault(c => c.Name == Environment.GetEnvironmentVariable("WAFLUENT_TEST_CHAT")) is { } target)
                    ChatList.SelectedItem = target;
                await Task.Delay(1500);
                if (ViewModel.SelectedChat?.Messages.FirstOrDefault(m => m.Kind == MessageKind.Album) is { } album) OpenAlbum(album);
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
        ShowCalls(section == "Calls");
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
            "Chats" or "Settings" or "Calls" => false,
            "Archived" => ViewModel.ArchivedCount == 0,
            "Starred" => !ViewModel.HasStarred,
            _ => true,
        };
        SectionPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        (SectionPlaceholderIcon.Glyph, SectionPlaceholderText.Text) = section switch
        {
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
        if (_syncingList || ChatList.SelectedItem is not Chat chat || chat == ViewModel.SelectedChat) return;
        ViewModel.SelectedChat = chat;
        ScrollToStart();
    }

    /// <summary>A chat opens at its "unread messages" band when it has one, else at the bottom.</summary>
    private void ScrollToStart()
    {
        if (ViewModel.UnreadDividerIndex < 0)
        {
            ScrollToBottom();
            return;
        }
        _ignoreScrollUntil = DateTime.Now.AddMilliseconds(800);
        void Jump(int triesLeft)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var index = ViewModel.UnreadDividerIndex;
                if (index < 0) return;
                MessagesScroller.UpdateLayout();
                var band = Messages.GetOrCreateElement(index);
                band.UpdateLayout();
                band.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = false });
                if (triesLeft > 0) Jump(triesLeft - 1);   // rows measured on the way settle the position
            });
        }
        Jump(2);
    }

    // ───────────── Calls ─────────────

    private void VoiceCall_Click(object sender, RoutedEventArgs e) => StartCall(video: false);

    private void VideoCall_Click(object sender, RoutedEventArgs e) => StartCall(video: true);

    // ───────────── Composer ─────────────

    private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateShortcodes();
        UpdateMentions();
        UpdateLinkCard();
        var hasText = ComposerBox.Text.Trim().Length > 0;
        // A draft put back on opening its chat isn't typing.
        if (_restoredDraft is { } restored && ComposerBox.Text == restored) _restoredDraft = null;
        else ViewModel.ComposerEdited(hasText);
        SendButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        MicGroup.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The @mention and :shortcode: lists get ↑ ↓ Enter Tab Esc first.
        if (MentionKey(e.Key) || ShortcodeKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        // Editing: Esc stops; ↑ in an empty composer edits your latest message (like Discord).
        if (e.Key == VirtualKey.Escape && _editing is not null)
        {
            CancelEdit();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Up && ComposerBox.Text.Length == 0 && _editing is null && LatestEditable() is { } latest)
        {
            BeginEdit(latest);
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
        if (_editing is not null)
        {
            SaveEdit();
            return;
        }
        if (!ViewModel.Send(ComposerBox.Text, _mentions, ReadyLinkCard(), EveryoneInOpenChat())) return;
        _mentions.Clear();
        ResetLinkCard();
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
