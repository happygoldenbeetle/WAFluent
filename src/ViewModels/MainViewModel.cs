using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

public enum ConnectionState { Sample, Starting, Qr, Connecting, Syncing, Connected, LoggedOut, Error }

public sealed class MainViewModel : Observable
{
    private readonly CoreClient? _core;
    private readonly List<Chat> _allChats = new();
    private readonly Dictionary<string, Chat> _byId = new();
    private readonly HashSet<string> _archived = new();
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
    }

    // ───────────── Profile pictures ─────────────

    private static readonly Uri ProfilePlaceholder = new("ms-appx:///Assets/ProfilePlaceholder.png");
    private ImageSource _profileIcon = new BitmapImage(ProfilePlaceholder);

    /// <summary>Your own picture, round-cropped for the rail (placeholder until known).</summary>
    public ImageSource ProfileIcon { get => _profileIcon; private set => Set(ref _profileIcon, value); }

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

    public bool CanSend => !IsLive;   // live sending arrives with the "send" step
    public string ComposerPlaceholder => IsLive ? "Sending isn't available yet" : "Type a message";

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

    // ───────────── Chat list ─────────────

    public Chat? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (!Set(ref _selectedChat, value)) return;
            if (value is not null) Open(value);
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedChat is not null;

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
        chat.LastActivity = Format.FromUnix(dto.LastTs);
        chat.Time = Format.ListTime(chat.LastActivity);
        chat.Preview = dto.LastSender is { Length: > 0 } who ? $"{who}: {dto.Preview}" : dto.Preview;
        chat.PreviewGlyph = Format.PreviewGlyph(dto.PreviewKind);
        chat.LastDelivery = dto.LastFromMe ? Format.ToDelivery(dto.LastStatus) : Delivery.None;

        // Messages arriving in the open chat are read as they come in.
        if (chat == _selectedChat && dto.Unread > 0) _core?.MarkRead(chat.Id);
        chat.Unread = chat == _selectedChat ? 0 : dto.Unread;

        if (dto.Archived) _archived.Add(dto.Id); else _archived.Remove(dto.Id);
        return chat;
    }

    /// <summary>Pinned first, then most recent, like WhatsApp.</summary>
    private void Reorder()
    {
        var ordered = _allChats.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.LastActivity).ToList();
        _allChats.Clear();
        _allChats.AddRange(ordered);
    }

    /// <summary>Applies search/archive filtering with minimal collection changes (keeps scroll and selection).</summary>
    private void SyncVisible()
    {
        var target = _allChats
            .Where(c => !_archived.Contains(c.Id))
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
        var older = messages.Where(d => !known.Contains(d.Id)).Select(d => Format.ToMessage(d, chat.IsGroup)).ToList();
        if (older.Count == 0) return;
        Prepend(chat, older);

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
        Append(chat, Format.ToMessage(dto, chat.IsGroup));
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

    /// <summary>Adds an outgoing text message to the open chat (sample mode only for now).</summary>
    public bool Send(string text)
    {
        text = text.Trim();
        if (!CanSend || _selectedChat is null || text.Length == 0) return false;

        var now = DateTime.Now;
        var time = now.ToString("H:mm");
        _selectedChat.Messages.Add(new Message { Text = text, Time = time, Timestamp = now, IsOutgoing = true, Delivery = Delivery.Sent });
        _selectedChat.Preview = text;
        _selectedChat.Time = time;
        _selectedChat.LastActivity = now;
        _selectedChat.LastDelivery = Delivery.Sent;
        Reorder();
        SyncVisible();
        return true;
    }
}
