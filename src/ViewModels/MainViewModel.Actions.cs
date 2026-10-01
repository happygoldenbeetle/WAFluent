using System.Collections.ObjectModel;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

public enum ChatFilter { All, Unread, Favourites, Groups }

/// <summary>A starred message in the Starred view.</summary>
/// <summary>A chat's Media, links and docs, each newest first.</summary>
public sealed record ChatMediaSet(Chat Chat, IReadOnlyList<Message> Media, IReadOnlyList<Message> Docs, IReadOnlyList<Message> Links)
{
    public int Count => Media.Count + Docs.Count + Links.Count;
}

public sealed class StarredItem
{
    public required string ChatId { get; init; }
    public required string ChatName { get; init; }
    public required Message Message { get; init; }
    public string Preview => Message.Kind switch
    {
        MessageKind.Image => Message.HasText ? "📷 " + Message.Text : "📷 Photo",
        MessageKind.Voice => "🎤 Voice message",
        MessageKind.Sticker => "Sticker",
        MessageKind.File => "📄 " + Message.FileName,
        MessageKind.Video => "🎥 " + Format.QuotePreview(Message),
        MessageKind.Location => "📍 " + Format.QuotePreview(Message),
        MessageKind.Contact => "👤 " + Message.ContactTitle,
        MessageKind.Poll => "📊 " + Message.Text,
        _ => Message.Text,
    };
    public string When => Format.ListTime(Message.Timestamp);
}

/// <summary>
/// Chat-list menu actions (archive, mute, pin, read state, favourites, block, clear, delete,
/// save contact), message actions (forward, pin, star, delete, report), select mode, the
/// Archived and Starred views and list filters. Live: sent to the core, which answers with
/// updated chats/messages. Sample mode: applied locally so the UI can be tried out.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A short confirmation or error for the toast (ok, text).</summary>
    public event Action<bool, string>? Toast;

    private readonly Dictionary<Chat, int> _typingVersion = new();

    /// <summary>
    /// Someone started or stopped typing. It clears itself after 20 s without a refresh, as the
    /// "paused" can get lost (they closed the app, lost signal).
    /// </summary>
    private async void OnTyping(string chatId, string who, string state)
    {
        if (!_byId.TryGetValue(chatId, out var chat)) return;
        var version = _typingVersion[chat] = _typingVersion.GetValueOrDefault(chat) + 1;
        var what = state == "recording" ? "recording audio…" : "typing…";
        chat.TypingText = state == "paused" ? "" : who.Length > 0 && chat.IsGroup ? $"{who} is {what}" : what;
        if (state == "paused") return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (_typingVersion.GetValueOrDefault(chat) == version) chat.TypingText = "";
    }

    // ───── Your own typing ─────

    private DateTime _typingSentAt;
    private int _typingEdits;

    /// <summary>
    /// The composer changed: tell the chat you're typing (at most every 8 s), and that you
    /// stopped once nothing has changed for 4 s.
    /// </summary>
    public async void ComposerEdited(bool hasText)
    {
        if (_core is null || _selectedChat is not { Id.Length: > 0 } chat) return;
        var edit = ++_typingEdits;
        if (!hasText)
        {
            StopTyping();
            return;
        }
        if (DateTime.Now - _typingSentAt > TimeSpan.FromSeconds(8))
        {
            _typingSentAt = DateTime.Now;
            _core.SendTyping(chat.Id, "typing");
        }
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (edit == _typingEdits && _selectedChat == chat) StopTyping();
    }

    /// <summary>Recording a voice note: the chat sees "recording audio…" (sent again every 10 s), or that you stopped.</summary>
    public void SetRecording(Chat chat, bool recording)
    {
        if (_core is null || chat.Id.Length == 0) return;
        _typingSentAt = default;
        _core.SendTyping(chat.Id, recording ? "recording" : "paused");
    }

    public void StopTyping()
    {
        if (_core is null || _selectedChat is not { Id.Length: > 0 } chat || _typingSentAt == default) return;
        _typingSentAt = default;
        _core.SendTyping(chat.Id, "paused");
    }

    private bool _available = true;

    /// <summary>The window came to the front or went away: WhatsApp shows you online accordingly (resent on reconnect).</summary>
    public void SetPresence(bool available)
    {
        _available = available;
        if (_state == ConnectionState.Connected) _core?.SetPresence(available);
    }

#if DEBUG
    /// <summary>Self-test: a reply lands in a sample chat the way a live one would.</summary>
    public void SimulateIncoming(Chat chat, string text)
    {
        _typingVersion[chat] = _typingVersion.GetValueOrDefault(chat) + 1;
        chat.TypingText = "";
        var now = DateTime.Now;
        var message = new Message { Id = Guid.NewGuid().ToString("N"), Text = text, Time = Format.Clock(now), Timestamp = now, AnimateIn = true };
        Append(chat, message);
        if (chat == _selectedChat) MessageArrived?.Invoke(message);
    }
#endif

    /// <summary>Tails again for every loaded chat (the bubble style changed).</summary>
    public void RefreshRuns()
    {
        foreach (var chat in _allChats.Where(c => c.MessagesLoaded)) UpdateRuns(chat);
    }

    /// <summary>A message changed (vote, edit, download details): its bubble is swapped in place.</summary>
    private void ApplyUpdate(Chat chat, MessageDto dto)
    {
        var i = IndexOf(chat, dto.Id);
        if (i < 0)
        {
            // Inside an album: the item is replaced there.
            if (chat.Messages.FirstOrDefault(m => m.AlbumItems?.Any(x => x.Id == dto.Id) == true) is not { AlbumItems: { } items } album) return;
            var before = items.First(x => x.Id == dto.Id);
            var updated = Format.ToMessage(dto, chat.IsGroup);
            updated.MediaPath ??= before.MediaPath;
            var at = chat.Messages.IndexOf(album);
            chat.Messages[at] = new Message { Id = album.Id, Kind = MessageKind.Album, IsOutgoing = album.IsOutgoing, SenderName = album.SenderName, Forwarded = album.Forwarded,
                Time = album.Time, Timestamp = album.Timestamp, UnixTs = album.UnixTs, Delivery = album.Delivery,
                AlbumItems = items.Select(x => x == before ? updated : x).ToList() };
            UpdateRuns(chat);
            return;
        }
        var old = chat.Messages[i];
        var fresh = Format.ToMessage(dto, chat.IsGroup);
        fresh.MediaPath ??= old.MediaPath;
        fresh.Selecting = old.Selecting;
        fresh.HasTail = old.HasTail;
        chat.Messages[i] = fresh;
        // A message whose download details just arrived from the phone: fetch it now.
        if (!old.HasMedia && fresh.HasMedia) RequestMedia(chat, [fresh]);
    }

    private void Notify(bool ok, string text) => Toast?.Invoke(ok, text);

    private void HookActions(CoreClient core)
    {
        core.MessageUpdated += (chatId, dto) =>
        {
            if (_byId.TryGetValue(chatId, out var chat)) ApplyUpdate(chat, dto);
        };
        core.MessageRemoved += (chatId, messageId) =>
        {
            if (!_byId.TryGetValue(chatId, out var chat)) return;
            if (IndexOf(chat, messageId) is var i and >= 0)
            {
                chat.Messages.RemoveAt(i);
                UpdateRuns(chat);
            }
            else if (chat.Messages.FirstOrDefault(m => m.AlbumItems?.Any(x => x.Id == messageId) == true) is { AlbumItems: { } items } album)
            {
                // Put the rest back as single bubbles; UpdateRuns makes an album again if two or more remain.
                var at = chat.Messages.IndexOf(album);
                chat.Messages.RemoveAt(at);
                foreach (var rest in items.Where(x => x.Id != messageId).Reverse()) chat.Messages.Insert(at, rest);
                UpdateRuns(chat);
            }
        };
        core.ChatRemoved += RemoveChat;
        core.StarredReceived += items =>
        {
            Starred.Clear();
            foreach (var item in items)
                Starred.Add(new StarredItem { ChatId = item.ChatId, ChatName = item.ChatName, Message = Format.ToMessage(item.Message, isGroup: false) });
            Raise(nameof(HasStarred));
        };
        core.Notice += Notify;
        core.Typing += OnTyping;
        core.Presence += (chatId, online, lastSeen) =>
        {
            if (_byId.TryGetValue(chatId, out var chat) && !chat.IsGroup)
                chat.Status = online ? "online" : lastSeen is { } s ? Format.LastSeen(Format.FromUnix(s)) : "";
        };
        core.Me += (name, phone) => { SelfName = name; SelfPhone = phone; };
        core.Opened += chatId =>
        {
            if (!_byId.TryGetValue(chatId, out var chat)) return;
            SelectedChat = chat;
            ChatOpened?.Invoke(chat);
        };
    }

    private static int IndexOf(Chat chat, string messageId)
    {
        for (var i = 0; i < chat.Messages.Count; i++)
            if (chat.Messages[i].Id == messageId) return i;
        return -1;
    }

    private void RemoveChat(string chatId)
    {
        if (!_byId.TryGetValue(chatId, out var chat)) return;
        _allChats.Remove(chat);
        _byId.Remove(chatId);
        if (chat == _selectedChat) SelectedChat = null;
        SyncVisible();
    }

    // ───────────── List views: Chats / Archived, filters, favourites ─────────────

    private bool _showArchived;
    private ChatFilter _filter;
    private HashSet<string> _favourites = new();

    /// <summary>The list shows archived chats (rail: Archived chats).</summary>
    public bool ShowArchived
    {
        get => _showArchived;
        set { if (Set(ref _showArchived, value)) SyncVisible(); }
    }

    public ChatFilter Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) SyncVisible(); }
    }

    /// <summary>Favourite chats (ids; names for sample chats), kept by the window in ui.json.</summary>
    public void UseFavourites(HashSet<string> favourites)
    {
        _favourites = favourites;
        foreach (var chat in _allChats) chat.IsFavourite = _favourites.Contains(Key(chat));
    }

    private static string Key(Chat chat) => chat.Id.Length > 0 ? chat.Id : chat.Name;

    private bool Visible(Chat c) =>
        c.IsArchived == _showArchived &&
        _filter switch
        {
            ChatFilter.Unread => c.HasUnread,
            ChatFilter.Favourites => c.IsFavourite,
            ChatFilter.Groups => c.IsGroup,
            _ => true,
        };

    public int ArchivedCount => _allChats.Count(c => c.IsArchived);

    // ───────────── Chat menu ─────────────

    public void ToggleFavourite(Chat chat)
    {
        chat.IsFavourite = !chat.IsFavourite;
        if (chat.IsFavourite) _favourites.Add(Key(chat)); else _favourites.Remove(Key(chat));
        FavouritesChanged?.Invoke();
        SyncVisible();
    }

    public event Action? FavouritesChanged;

    /// <summary>archive | unarchive | mute | unmute | markRead | markUnread | clear | delete | block | unblock.</summary>
    public void ChatAction(Chat chat, string action, TimeSpan? muteFor = null)
    {
        long? until = muteFor is { } span ? DateTimeOffset.Now.Add(span).ToUnixTimeMilliseconds() : null;
        // Shown at once; the core's answer (or a failure) sets the final state.
        switch (action)
        {
            case "archive": chat.IsArchived = true; break;
            case "unarchive": chat.IsArchived = false; break;
            case "mute": chat.IsMuted = true; break;
            case "unmute": chat.IsMuted = false; break;
            case "markRead": chat.Unread = 0; break;
            case "markUnread": chat.Unread = Math.Max(1, chat.Unread); break;
            case "block": chat.IsBlocked = true; break;
            case "unblock": chat.IsBlocked = false; break;
            case "clear": chat.Messages.Clear(); chat.PinnedMessageId = ""; break;
        }
        if (_core is null || chat.Id.Length == 0)
        {
            if (action == "delete") { _allChats.Remove(chat); if (chat == _selectedChat) SelectedChat = null; }
        }
        else
        {
            _core.ChatAction(chat.Id, action, until);
        }
        Raise(nameof(ArchivedCount));
        SyncVisible();
    }

    public void SaveContact(Chat chat, string first, string last, bool syncToPhone)
    {
        if (_core is null || chat.Id.Length == 0)
        {
            chat.Name = $"{first} {last}".Trim();
            chat.IsSaved = true;
            Notify(true, $"Saved {chat.Name} to your contacts");
            return;
        }
        _core.SaveContact(chat.Id, first, last, syncToPhone);
    }

    public void ReportContact(Chat chat)
    {
        if (_core is not null && chat.Id.Length > 0) _core.ReportContact(chat.Id);
        else Notify(true, "Reported to WhatsApp");
    }

    /// <summary>Exports everything this PC has of the chat (the core writes the file; sample: what's loaded).</summary>
    public void ExportChat(Chat chat, string path)
    {
        if (_core is not null && chat.Id.Length > 0)
        {
            _core.ExportChat(chat.Id, path);
            return;
        }
        var lines = chat.Messages.Where(m => m.Kind != MessageKind.DateDivider)
            .Select(m => $"{m.Timestamp:dd/MM/yyyy, HH:mm} - {(m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : chat.Name)}: {Format.QuotePreview(m)}");
        File.WriteAllLines(path, lines);
        Notify(true, $"Exported {chat.Messages.Count(m => m.Kind != MessageKind.DateDivider)} messages");
    }

    /// <summary>Photos, stickers, voice notes and files among the loaded messages.</summary>
    public int MediaCount(Chat chat) => chat.Messages.Count(m => m.Kind is MessageKind.Image or MessageKind.Sticker or MessageKind.Voice or MessageKind.File or MessageKind.Video);

    public void CloseChat() => SelectedChat = null;

    // ───────────── Message menu ─────────────

    public void Forward(IEnumerable<Message> messages, IReadOnlyList<Chat> to)
    {
        if (_selectedChat is not { } from) return;
        foreach (var m in messages.Where(m => !m.IsDeleted && m.Kind != MessageKind.DateDivider))
        {
            if (_core is not null && from.Id.Length > 0)
                _core.Forward(from.Id, m.Id, to.Select(c => c.Id).ToList());
            else
                foreach (var target in to) Append(target, Copy(m, outgoing: true));
        }
        if (_core is null) Notify(true, to.Count == 1 ? $"Forwarded to {to[0].Name}" : $"Forwarded to {to.Count} chats");
    }

    public void PinMessage(Message m, bool pin)
    {
        if (_selectedChat is not { } chat) return;
        chat.PinnedMessageId = pin ? m.Id : "";
        chat.PinnedMessagePreview = pin ? Format.QuotePreview(m) : "";
        if (_core is not null && chat.Id.Length > 0) _core.PinMessage(chat.Id, m.Id, pin);
    }

    public void Star(IEnumerable<Message> messages, bool star)
    {
        if (_selectedChat is not { } chat) return;
        foreach (var m in messages.ToList())
        {
            if (_core is not null && chat.Id.Length > 0) _core.StarMessage(chat.Id, m.Id, star);
            else if (IndexOf(chat, m.Id) is var i and >= 0) chat.Messages[i] = Copy(m, starred: star);
        }
    }

    public void Delete(IEnumerable<Message> messages, bool forEveryone)
    {
        if (_selectedChat is not { } chat) return;
        foreach (var m in messages.ToList())
        {
            if (_core is not null && chat.Id.Length > 0) { _core.DeleteMessage(chat.Id, m.Id, forEveryone); continue; }
            var i = IndexOf(chat, m.Id);
            if (i < 0) continue;
            if (forEveryone) chat.Messages[i] = Copy(m, deleted: true);
            else chat.Messages.RemoveAt(i);
        }
    }

    public void Report(Message m)
    {
        if (_selectedChat is { Id.Length: > 0 } chat && _core is not null) _core.Report(chat.Id, m.Id);
        else Notify(true, "Reported to WhatsApp");
    }

    /// <summary>A changed copy of a message (sample mode; live updates come from the core).</summary>
    private static Message Copy(Message m, bool? starred = null, bool? deleted = null, bool? outgoing = null)
    {
        var isDeleted = deleted ?? m.IsDeleted;
        var isOutgoing = outgoing ?? m.IsOutgoing;
        var now = DateTime.Now;
        return new Message
        {
            Id = outgoing is null ? m.Id : "copy-" + Guid.NewGuid().ToString("N"),
            Kind = isDeleted ? MessageKind.Text : m.Kind,
            IsOutgoing = isOutgoing,
            Text = isDeleted ? (isOutgoing ? "You deleted this message" : "This message was deleted") : m.Text,
            Time = outgoing is null ? m.Time : Format.Clock(now),
            Timestamp = outgoing is null ? m.Timestamp : now,
            UnixTs = m.UnixTs,
            Delivery = isOutgoing ? (outgoing is null ? m.Delivery : Delivery.Sent) : Delivery.None,
            SenderName = outgoing is null ? m.SenderName : "",
            HasMedia = !isDeleted && m.HasMedia,
            MediaPath = isDeleted ? null : m.MediaPath,
            MediaWidth = m.MediaWidth,
            MediaHeight = m.MediaHeight,
            Seconds = m.Seconds,
            Waveform = m.Waveform,
            FileName = m.FileName,
            FileDetails = m.FileDetails,
            Reactions = outgoing is null ? m.Reactions : [],
            MyReaction = outgoing is null ? m.MyReaction : "",
            Starred = !isDeleted && (starred ?? (outgoing is null && m.Starred)),
            IsDeleted = isDeleted,
        };
    }

    // ───────────── Select mode ─────────────

    private bool _isSelecting;
    public bool IsSelecting { get => _isSelecting; private set => Set(ref _isSelecting, value); }

    public IEnumerable<Message> SelectedMessages =>
        _selectedChat?.Messages.Where(m => m.IsSelected) ?? [];

    public int SelectionCount => SelectedMessages.Count();
    public string SelectionText => SelectionCount == 1 ? "1 selected" : $"{SelectionCount} selected";

    public void BeginSelect(Message first)
    {
        if (_selectedChat is not { } chat) return;
        foreach (var m in chat.Messages)
        {
            m.Selecting = m.Kind != MessageKind.DateDivider;
            m.IsSelected = m == first;
        }
        IsSelecting = true;
        RaiseSelection();
    }

    public void ToggleSelect(Message m)
    {
        if (!_isSelecting || m.Kind == MessageKind.DateDivider) return;
        m.IsSelected = !m.IsSelected;
        RaiseSelection();
    }

    public void EndSelect()
    {
        if (_selectedChat is { } chat)
            foreach (var m in chat.Messages) { m.Selecting = false; m.IsSelected = false; }
        IsSelecting = false;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectionCount));
        Raise(nameof(SelectionText));
    }

    // ───────────── Media, links and docs ─────────────

    /// <summary>Raised with a chat's media, documents and links (newest first) once they're read.</summary>
    public event Action<ChatMediaSet>? ChatMediaLoaded;

    /// <summary>Gallery messages that aren't loaded in their chat (older ones): downloads still find them.</summary>
    private readonly Dictionary<(string ChatId, string Id), Message> _gallery = new();

    private static readonly System.Text.RegularExpressions.Regex UrlPattern =
        new(@"(https?://|www\.)\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The first web address in a message (its link card's, or one in the text).</summary>
    public static string FirstUrl(Message m) =>
        m.LinkUrl.Length > 0 ? m.LinkUrl : UrlPattern.Match(m.Text) is { Success: true } x ? x.Value.TrimEnd('.', ',', ')', '!', '?') : "";

    public void LoadChatMedia(Chat chat)
    {
        if (_core is not null)
        {
            _core.LoadChatMedia(chat.Id);
            return;
        }
        var all = Everything(chat).Where(m => !m.IsDeleted && m.Kind != MessageKind.DateDivider).Reverse().ToList();
        ChatMediaLoaded?.Invoke(new ChatMediaSet(chat,
            all.Where(m => m.Kind is MessageKind.Image or MessageKind.Video).ToList(),
            all.Where(m => m.Kind == MessageKind.File).ToList(),
            all.Where(m => m.Kind == MessageKind.Text && FirstUrl(m).Length > 0).ToList()));
    }

    private void OnChatMedia(string chatId, IReadOnlyList<MessageDto> media, IReadOnlyList<MessageDto> docs, IReadOnlyList<MessageDto> links)
    {
        if (!_byId.TryGetValue(chatId, out var chat)) return;
        var loaded = Everything(chat).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        Message Adopt(MessageDto dto)
        {
            if (loaded.TryGetValue(dto.Id, out var shown)) return shown;
            if (_gallery.TryGetValue((chatId, dto.Id), out var known)) return known;
            var m = Format.ToMessage(dto, chat.IsGroup);
            _gallery[(chatId, dto.Id)] = m;
            return m;
        }
        ChatMediaLoaded?.Invoke(new ChatMediaSet(chat,
            media.Select(Adopt).ToList(),
            docs.Select(Adopt).ToList(),
            links.Select(Adopt).Where(m => FirstUrl(m).Length > 0).ToList()));
    }

    // ───────────── Starred view ─────────────

    public ObservableCollection<StarredItem> Starred { get; } = new();
    public bool HasStarred => Starred.Count > 0;

    public void LoadStarred()
    {
        if (_core is not null) { _core.LoadStarred(); return; }
        Starred.Clear();
        foreach (var chat in _allChats)
            foreach (var m in chat.Messages.Where(m => m.Starred))
                Starred.Add(new StarredItem { ChatId = chat.Id, ChatName = chat.Name, Message = m });
        Raise(nameof(HasStarred));
    }

    /// <summary>Chats to forward to: most recent first, archived ones included.</summary>
    public List<Chat> ForwardTargets() => _allChats.Where(c => !c.IsBlocked).OrderByDescending(c => c.LastActivity).ToList();

    /// <summary>Opens a chat from a list outside the Chats view (Starred).</summary>
    public Chat? FindChat(StarredItem item) =>
        item.ChatId.Length > 0 ? _byId.GetValueOrDefault(item.ChatId) : _allChats.FirstOrDefault(c => c.Name == item.ChatName);
}
