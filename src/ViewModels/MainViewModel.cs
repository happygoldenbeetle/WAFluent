using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

public enum ConnectionState { Sample, Starting, Qr, Connecting, Syncing, Connected, LoggedOut, Error }

public sealed partial class MainViewModel : Observable
{
    private readonly CoreClient? _core;
    private readonly List<Chat> _allChats = new();
    private readonly Dictionary<string, Chat> _byId = new();
    private Chat? _selectedChat;
    private string _searchText = "";
    private ConnectionState _state;
    private string? _statusDetail;
    private ImageSource? _qrImage;

    public ObservableCollection<Chat> Chats { get; } = new();

    /// <summary>Raised when the open conversation got new messages (scroll to the end).</summary>
    public event Action? ConversationChanged;

    /// <summary>Sample data (no core) when <paramref name="core"/> is null.</summary>
    public MainViewModel(CoreClient? core)
    {
        _core = core;
        if (core is null)
        {
            _state = ConnectionState.Sample;
            foreach (var chat in SampleData.Create()) Track(chat);
            SyncVisible();
            SelectedChat = _allChats.First(c => c.Name == "Alice Whitman");
            return;
        }

        _state = ConnectionState.Starting;
        core.StatusChanged += OnStatus;
        core.QrReceived += OnQr;
        core.ChatsReceived += OnChats;
        core.ChatReceived += dto => { Upsert(dto); Reorder(); SyncVisible(); };
        core.MessagesReceived += OnMessages;
        core.MessageReceived += OnMessage;
        core.OlderMessagesReceived += OnOlderMessages;
        core.AvatarReceived += OnAvatar;
        core.MediaReceived += (chatId, messageId, path) => { if (Find(chatId, messageId) is { } m) m.MediaPath = path; };
        core.MediaFailed += (chatId, messageId, _) => { if (Find(chatId, messageId) is { } m) m.MediaFailed = true; };
        core.Sent += OnSent;
        core.SendFailed += (chatId, tempId, _) => { if (Find(chatId, tempId) is { } m) m.Delivery = Delivery.Failed; };
        core.ReceiptReceived += OnReceipt;
        HookActions(core);
        core.ReactionsReceived += (chatId, messageId, all, mine) =>
        {
            if (Find(chatId, messageId) is not { } m) return;
            m.Reactions = all;
            m.MyReaction = mine ?? "";
        };
    }

    // ───────────── Attachments ─────────────

    private Message? Find(string chatId, string messageId) =>
        _byId.TryGetValue(chatId, out var chat) ? chat.Messages.FirstOrDefault(m => m.Id == messageId) : null;

    /// <summary>
    /// Auto-downloads pictures, stickers and voice notes for messages now on screen, like
    /// WhatsApp on Wi-Fi. The core serves the latest request first, so sending oldest to
    /// newest gets the bottom of the chat first. Downloaded files come straight from the cache.
    /// </summary>
    private void RequestMedia(Chat chat, IEnumerable<Message> messages)
    {
        if (_core is null) return;
        foreach (var m in messages)
            if (m.HasMedia && m.MediaPath is null && !m.MediaFailed
                && m.Kind is MessageKind.Image or MessageKind.Sticker or MessageKind.Voice)
                _core.DownloadMedia(chat.Id, m.Id);
    }

    /// <summary>Tries a failed attachment download again (for the open chat).</summary>
    public void RetryDownload(Message message)
    {
        if (_core is null || _selectedChat is null || !message.HasMedia) return;
        message.MediaFailed = false;   // back to the loading spinner
        _core.DownloadMedia(_selectedChat.Id, message.Id, force: true);
    }

    /// <summary>
    /// Messages saved before media support lack download details (they show as "📷 Photo").
    /// Asks the phone to resend them: each request covers the 50 messages before its anchor,
    /// the message just after a missing one. <paramref name="after"/> is the message that
    /// follows this batch in the chat (for older pages). Upgraded messages come back one by
    /// one as updates.
    /// </summary>
    private void BackfillIfNeeded(Chat chat, IReadOnlyList<MessageDto> batch, string? after = null)
    {
        if (_core is null) return;
        for (var i = batch.Count - 1; i >= 0; i--)
        {
            var d = batch[i];
            if (d.Media is not null || d.Kind is not ("image" or "voice" or "audio" or "sticker" or "video" or "gif" or "document")) continue;
            var anchor = i + 1 < batch.Count ? batch[i + 1].Id : after;
            _core.BackfillMedia(chat.Id, anchor);
            i -= 49;   // the rest of that window comes with the same answer
        }
    }

    // ───────────── Profile pictures ─────────────

    private static readonly Uri ProfilePlaceholder = new("ms-appx:///Assets/ProfilePlaceholder.png");
    private static readonly ImageSource Placeholder = new BitmapImage(ProfilePlaceholder);
    private ImageSource _profileIcon = Placeholder;
    private bool _hideProfilePhoto;

    /// <summary>Your own picture, round-cropped for the rail (placeholder until known, or in developer mode).</summary>
    public ImageSource ProfileIcon
    {
        get => _hideProfilePhoto ? Placeholder : _profileIcon;
        private set { _profileIcon = value; Raise(nameof(ProfileIcon)); }
    }

    /// <summary>Developer mode: the rail shows the placeholder instead of your photo.</summary>
    public bool HideProfilePhoto
    {
        get => _hideProfilePhoto;
        set { if (Set(ref _hideProfilePhoto, value)) Raise(nameof(ProfileIcon)); }
    }

    private async void OnAvatar(string chatId, string? path)
    {
        if (chatId == "self")
        {
            ProfileIcon = path is null ? new BitmapImage(ProfilePlaceholder)
                                       : await CircleImage.CreateAsync(path) ?? new BitmapImage(ProfilePlaceholder);
            return;
        }
        if (_byId.TryGetValue(chatId, out var chat)) chat.AvatarPath = path;
    }

    // ───────────── Connection state (live mode) ─────────────

    public bool IsLive => _core is not null;
    public ConnectionState State => _state;
    public string? StatusDetail => _statusDetail;
    public ImageSource? QrImage { get => _qrImage; private set => Set(ref _qrImage, value); }

    /// <summary>Full-window "link your phone" screen.</summary>
    public bool ShowLinkScreen =>
        IsLive && (_state is ConnectionState.Qr or ConnectionState.LoggedOut
                   || (_allChats.Count == 0 && _state != ConnectionState.Connected));

    public bool ShowQr => _state == ConnectionState.Qr && _qrImage is not null;
    public bool ShowLinkProgress => !ShowQr && _state != ConnectionState.Error;

    public string LinkStatusText => _state switch
    {
        ConnectionState.Qr when _qrImage is null => "Getting a QR code…",
        ConnectionState.Qr => "Scan with WhatsApp on your phone. The code refreshes automatically.",
        ConnectionState.Syncing => _statusDetail ?? "Linked. Loading your chats…",
        ConnectionState.LoggedOut => "You've been logged out. Getting a new QR code…",
        ConnectionState.Error => _statusDetail ?? "Something went wrong.",
        _ => "Connecting to WhatsApp…",
    };

    /// <summary>Thin strip over the chat list while reconnecting/syncing with chats already shown.</summary>
    public bool ShowBanner => IsLive && !ShowLinkScreen && _state != ConnectionState.Connected;

    public string BannerText => _state switch
    {
        ConnectionState.Syncing => _statusDetail ?? "Loading your chats…",
        ConnectionState.Error => _statusDetail ?? "Connection problem",
        _ => "Connecting…",
    };

    public bool CanSend => true;
    public string ComposerPlaceholder => "Type a message";

    private void OnStatus(string state, string? detail)
    {
        _state = state switch
        {
            "starting" => ConnectionState.Starting,
            "qr" => ConnectionState.Qr,
            "connecting" => ConnectionState.Connecting,
            "syncing" => ConnectionState.Syncing,
            "connected" => ConnectionState.Connected,
            "loggedOut" => ConnectionState.LoggedOut,
            _ => ConnectionState.Error,
        };
        _statusDetail = detail;
        if (_state != ConnectionState.Qr) QrImage = null;
        RaiseConnection();
    }

    private async void OnQr(string code)
    {
        QrImage = await QrRenderer.RenderAsync(code);
        RaiseConnection();
    }

    private void RaiseConnection()
    {
        foreach (var name in new[] { nameof(State), nameof(StatusDetail), nameof(ShowLinkScreen), nameof(ShowQr),
                                     nameof(ShowLinkProgress), nameof(LinkStatusText), nameof(ShowBanner), nameof(BannerText) })
            Raise(name);
    }

    public void Logout() => _core?.Logout();

    /// <summary>Problems on the app side (e.g. the core can't start) show like core errors.</summary>
    public void ReportError(string message) => OnStatus("error", message);

    // ───────────── Chat list ─────────────

    public Chat? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (value == _selectedChat) return;
            if (_isSelecting) EndSelect();   // selections belong to the chat they were made in
            Set(ref _selectedChat, value);
            CancelReply();   // a quote belongs to its chat
            if (value is not null) Open(value);
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedChat is not null;

    public int PinnedCount => _allChats.Count(c => c.IsPinned && !c.IsArchived);

    /// <summary>Pins/unpins right away; the core syncs it to the phone and sends back the result.</summary>
    public void SetPinned(Chat chat, bool pinned)
    {
        chat.IsPinned = pinned;
        chat.PinnedAt = pinned ? DateTimeOffset.Now.ToUnixTimeSeconds() : 0;
        Reorder();
        SyncVisible();
        if (chat.Id.Length > 0) _core?.SetPinned(chat.Id, pinned);
    }

    /// <summary>Number of chats with unread messages (the badge on the Chats rail item).</summary>
    public int UnreadChats => _allChats.Count(c => c.HasUnread);

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) SyncVisible(); }
    }

    private void Open(Chat chat)
    {
        if (_core is not null && chat.Id.Length > 0)
        {
            if (!chat.MessagesLoaded) _core.LoadMessages(chat.Id);
            if (chat.Unread > 0) _core.MarkRead(chat.Id);
        }
        chat.Unread = 0;
    }

    private void Track(Chat chat)
    {
        chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Chat.Unread)) Raise(nameof(UnreadChats)); };
        _allChats.Add(chat);
        if (chat.Id.Length > 0) _byId[chat.Id] = chat;
    }

    private void OnChats(IReadOnlyList<ChatDto> snapshot)
    {
        var seen = new HashSet<string>();
        foreach (var dto in snapshot)
        {
            Upsert(dto);
            seen.Add(dto.Id);
        }
        foreach (var gone in _allChats.Where(c => !seen.Contains(c.Id)).ToList())
        {
            _allChats.Remove(gone);
            _byId.Remove(gone.Id);
            if (gone == _selectedChat) SelectedChat = null;
        }
        Reorder();
        SyncVisible();
        RaiseConnection();   // the link screen depends on whether any chats exist
        Raise(nameof(UnreadChats));
    }

    private Chat Upsert(ChatDto dto)
    {
        if (!_byId.TryGetValue(dto.Id, out var chat))
        {
            chat = new Chat { Id = dto.Id, Name = dto.Name, IsGroup = dto.IsGroup, Status = dto.IsGroup ? "Group" : "" };
            Track(chat);
        }

        chat.Name = dto.Name;
        chat.AvatarPath = dto.Avatar;
        chat.IsPinned = dto.Pinned;
        chat.PinnedAt = dto.PinnedAt;
        chat.LastActivity = Format.FromUnix(dto.LastTs);
        chat.Time = Format.ListTime(chat.LastActivity);
        chat.PreviewSender = dto.LastSender is { Length: > 0 } who ? who + ":" : "";
        chat.Preview = dto.Preview;
        chat.PreviewGlyph = Format.PreviewGlyph(dto.PreviewKind);
        chat.LastDelivery = dto.LastFromMe ? Format.ToDelivery(dto.LastStatus) : Delivery.None;

        // Messages arriving in the open chat are read as they come in.
        if (chat == _selectedChat && dto.Unread > 0) _core?.MarkRead(chat.Id);
        chat.Unread = chat == _selectedChat ? 0 : dto.Unread;

        chat.IsArchived = dto.Archived;
        chat.IsMuted = dto.Muted;
        chat.IsBlocked = dto.Blocked;
        chat.IsSaved = dto.Saved || dto.IsGroup;
        chat.IsFavourite = _favourites.Contains(dto.Id);
        chat.PushName = dto.PushName ?? "";
        chat.PhoneRegion = dto.Phone?.Region ?? "";
        chat.PhoneCode = dto.Phone?.Code ?? "";
        chat.PhoneNational = dto.Phone?.National ?? "";
        chat.PinnedMessageId = dto.PinnedMessage?.Id ?? "";
        chat.PinnedMessagePreview = dto.PinnedMessage?.Preview ?? "";
        return chat;
    }

    /// <summary>Pinned first (newest pin on top), then most recent, like WhatsApp.</summary>
    private void Reorder()
    {
        var ordered = _allChats.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.IsPinned ? c.PinnedAt : 0)
                               .ThenByDescending(c => c.LastActivity).ToList();
        _allChats.Clear();
        _allChats.AddRange(ordered);
    }

    /// <summary>Applies search/archive filtering with minimal collection changes (keeps scroll and selection).</summary>
    private void SyncVisible()
    {
        var target = _allChats
            .Where(Visible)
            .Where(c => c.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var keep = target.ToHashSet();
        for (var i = Chats.Count - 1; i >= 0; i--)
            if (!keep.Contains(Chats[i])) Chats.RemoveAt(i);

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Chats.Count && Chats[i] == target[i]) continue;
            var existing = Chats.IndexOf(target[i]);
            if (existing >= 0) Chats.Move(existing, i);
            else Chats.Insert(i, target[i]);
        }
    }

    // ───────────── Messages ─────────────

    private void OnMessages(string chatId, IReadOnlyList<MessageDto> messages)
    {
        if (!_byId.TryGetValue(chatId, out var chat)) return;
        chat.Messages.Clear();
        foreach (var dto in messages) Append(chat, Format.ToMessage(dto, chat.IsGroup));
        chat.MessagesLoaded = true;
        if (chat == _selectedChat) ConversationChanged?.Invoke();
        RequestMedia(chat, chat.Messages);
        BackfillIfNeeded(chat, messages);

        // A newly linked device only gets a message or two for most chats; fetch more right away.
        if (messages.Count < 25) LoadOlder(chat);
    }

    /// <summary>
    /// Asks the core for messages older than the oldest one shown. The core answers from its
    /// store, or asks the phone (which can take a few seconds, or never come if it's offline).
    /// </summary>
    public async void LoadOlder(Chat? chat)
    {
        if (_core is null || chat is null || !chat.MessagesLoaded || chat.LoadingOlder || chat.HistoryComplete) return;
        var oldest = chat.Messages.FirstOrDefault(m => m.Kind != MessageKind.DateDivider);
        if (oldest is null) return;

        chat.LoadingOlder = true;
        _core.LoadOlder(chat.Id, oldest.UnixTs, oldest.Id);

        await Task.Delay(TimeSpan.FromSeconds(25));
        if (chat.LoadingOlder && chat.Messages.FirstOrDefault(m => m.Kind != MessageKind.DateDivider) == oldest)
            chat.LoadingOlder = false;   // no answer; allow another try on the next scroll
    }

    private void OnOlderMessages(string chatId, IReadOnlyList<MessageDto> messages, bool complete)
    {
        if (!_byId.TryGetValue(chatId, out var chat)) return;
        chat.LoadingOlder = false;
        if (complete) chat.HistoryComplete = true;

        var known = chat.Messages.Select(m => m.Id).ToHashSet();
        var fresh = messages.Where(d => !known.Contains(d.Id)).ToList();
        if (fresh.Count == 0) return;
        var after = chat.Messages.FirstOrDefault(m => m.Kind != MessageKind.DateDivider)?.Id;
        var older = fresh.Select(d => Format.ToMessage(d, chat.IsGroup)).ToList();
        Prepend(chat, older);
        RequestMedia(chat, older);
        BackfillIfNeeded(chat, fresh, after);

        // Keep filling until there's enough to scroll through.
        if (!complete && chat.Messages.Count(m => m.Kind != MessageKind.DateDivider) < 25) LoadOlder(chat);
    }

    /// <summary>Inserts older messages (oldest first) above the current ones, fixing up day dividers.</summary>
    private static void Prepend(Chat chat, List<Message> older)
    {
        var block = new List<Message>();
        foreach (var m in older)
        {
            var last = block.LastOrDefault(x => x.Kind != MessageKind.DateDivider);
            if (last is null || last.Timestamp.Date != m.Timestamp.Date)
                block.Add(new Message { Kind = MessageKind.DateDivider, Text = Format.DayLabel(m.Timestamp), Timestamp = m.Timestamp });
            block.Add(m);
        }

        // The existing first divider is redundant if the older block ends on the same day.
        if (chat.Messages.FirstOrDefault() is { Kind: MessageKind.DateDivider } first
            && first.Timestamp.Date == older[^1].Timestamp.Date)
            chat.Messages.RemoveAt(0);

        for (var i = 0; i < block.Count; i++) chat.Messages.Insert(i, block[i]);
    }

    private void OnMessage(string chatId, MessageDto dto)
    {
        if (!_byId.TryGetValue(chatId, out var chat) || !chat.MessagesLoaded) return;
        if (chat.Messages.Any(m => m.Id == dto.Id)) return;
        var message = Format.ToMessage(dto, chat.IsGroup);
        Append(chat, message);
        RequestMedia(chat, [message]);
        if (chat == _selectedChat) ConversationChanged?.Invoke();
    }

    /// <summary>Adds a message, inserting a "Today"/"Yesterday"/date divider when the day changes.</summary>
    private static void Append(Chat chat, Message message)
    {
        var last = chat.Messages.LastOrDefault(m => m.Kind != MessageKind.DateDivider);
        if (last is null || last.Timestamp.Date != message.Timestamp.Date)
            chat.Messages.Add(new Message { Kind = MessageKind.DateDivider, Text = Format.DayLabel(message.Timestamp), Timestamp = message.Timestamp });
        chat.Messages.Add(message);
    }

    // ───────────── Sending and replying ─────────────

    private Message? _replyingTo;

    /// <summary>The message the composer is replying to (swipe right or "Reply" on it).</summary>
    public Message? ReplyingTo
    {
        get => _replyingTo;
        private set
        {
            if (!Set(ref _replyingTo, value)) return;
            Raise(nameof(IsReplying));
            Raise(nameof(ReplyingToName));
            Raise(nameof(ReplyingToPreview));
            Raise(nameof(ReplyingToGlyph));
            Raise(nameof(ReplyingToFromMe));
        }
    }

    public bool IsReplying => _replyingTo is not null;
    public string ReplyingToName => _replyingTo is null ? "" : AuthorName(_replyingTo);
    public string ReplyingToPreview => _replyingTo is null ? "" : Format.QuotePreview(_replyingTo);
    public string ReplyingToGlyph => _replyingTo is null ? "" : Format.QuoteGlyph(_replyingTo);
    public bool ReplyingToFromMe => _replyingTo?.IsOutgoing ?? false;

    public void BeginReply(Message message)
    {
        if (message.Kind == MessageKind.DateDivider || message.Delivery is Delivery.Pending or Delivery.Failed) return;
        ReplyingTo = message;
    }

    public void CancelReply() => ReplyingTo = null;

    /// <summary>"You", the group member, or the person you're chatting with.</summary>
    private string AuthorName(Message m) =>
        m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : _selectedChat?.Name ?? "";

    /// <summary>
    /// Adds the message to the open chat right away (clock icon) and hands it to the core;
    /// <see cref="OnSent"/> swaps in the real id and a tick. Sample mode just shows it as sent.
    /// </summary>
    public bool Send(string text)
    {
        text = text.Trim();
        if (_selectedChat is not { } chat || text.Length == 0) return false;

        var now = DateTime.Now;
        var quote = _replyingTo;
        var message = new Message
        {
            Id = "pending-" + Guid.NewGuid().ToString("N"),
            Text = text,
            Time = now.ToString("H:mm"),
            Timestamp = now,
            UnixTs = DateTimeOffset.Now.ToUnixTimeSeconds(),
            IsOutgoing = true,
            Delivery = _core is null ? Delivery.Sent : Delivery.Pending,
            ReplyId = quote?.Id ?? "",
            ReplyName = quote is null ? "" : AuthorName(quote),
            ReplyPreview = quote is null ? "" : Format.QuotePreview(quote),
            ReplyGlyph = quote is null ? "" : Format.QuoteGlyph(quote),
            ReplyFromMe = quote?.IsOutgoing ?? false,
        };
        Append(chat, message);
        CancelReply();
        _core?.SendText(chat.Id, text, message.HasReply ? message.ReplyId : null, message.Id);

        chat.Preview = text;
        chat.PreviewSender = "";
        chat.PreviewGlyph = "";
        chat.Time = message.Time;
        chat.LastActivity = now;
        chat.LastDelivery = message.Delivery;
        Reorder();
        SyncVisible();
        return true;
    }

    /// <summary>Sends a message that failed again (same pending bubble).</summary>
    public void RetrySend(Message message)
    {
        if (_core is null || _selectedChat is null || message.Delivery != Delivery.Failed) return;
        message.Delivery = Delivery.Pending;
        _core.SendText(_selectedChat.Id, message.Text, message.HasReply ? message.ReplyId : null, message.Id);
    }

    private void OnSent(string chatId, string tempId, MessageDto dto)
    {
        if (Find(chatId, tempId) is not { } message) return;
        message.Id = dto.Id;
        if (message.Delivery is Delivery.Pending or Delivery.Failed) message.Delivery = Format.ToDelivery(dto.Status);
    }

    /// <summary>
    /// Your reaction: shown at once, then confirmed (or put back) by the core.
    /// Picking the emoji you already reacted with removes it. Returns true if it was added.
    /// </summary>
    public bool React(Message message, string emoji)
    {
        if (_selectedChat is not { } chat || message.Kind == MessageKind.DateDivider) return false;
        var remove = message.MyReaction == emoji;
        var others = message.Reactions.ToList();
        if (message.MyReaction.Length > 0) others.Remove(message.MyReaction);
        if (!remove) others.Add(emoji);
        message.Reactions = others;
        message.MyReaction = remove ? "" : emoji;
        _core?.React(chat.Id, message.Id, remove ? "" : emoji);
        return !remove;
    }

    private void OnReceipt(string chatId, IReadOnlyList<string> ids, int status)
    {
        var delivery = Format.ToDelivery(status);
        foreach (var id in ids)
            if (Find(chatId, id) is { Delivery: Delivery.Sent or Delivery.Delivered } m && delivery > m.Delivery)
                m.Delivery = delivery;
    }
}
