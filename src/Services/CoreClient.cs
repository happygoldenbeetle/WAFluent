using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace WhatsAppNative.Services;

public sealed record ChatDto(
    string Id, string Name, bool IsGroup, int Unread, bool Pinned, bool Archived, bool Muted,
    long LastTs, string Preview, string PreviewKind, bool LastFromMe, int LastStatus, string? LastSender,
    string? Avatar, long PinnedAt, bool Blocked, bool Saved, PinnedDto[]? PinnedMessages, string? PushName, PhoneDto? Phone,
    int Ephemeral = 0, bool MarkedUnread = false);

public sealed record PhoneDto(string Region, string Code, string National);

/// <summary>A group's details: members (you included), how many are contacts, who made it and when, its description.</summary>
public sealed record GroupInfoDto(int Members, int Contacts, string Creator, long Created, string Description, bool Admin);

/// <summary>A saved contact (New chat): their chat id, name, number (digits), picture, and whether they're blocked.</summary>
public sealed record ContactDto(string ChatId, string Name, string Phone, string? Avatar = null, bool Blocked = false, bool HasChat = false);

/// <summary>A pinned message: its id, a one-line preview, its time (to load back to it) and when the pin runs out.</summary>
public sealed record PinnedDto(string Id, string Preview, long Ts = 0, long ExpiresAt = 0);

/// <summary>
/// A call's state: ringing (someone is calling you) | calling (yours is ringing there) | connecting |
/// connected | ended. Reason (ended): ended | declined | noAnswer | missed | elsewhere | failed.
/// Link: it rings because someone entered a call link of yours, and answering joins them there.
/// </summary>
public sealed record CallDto(string CallId, string ChatId, string State, bool Video, bool Outgoing, string? Reason = null, string? Detail = null,
                             bool Link = false);

/// <summary>
/// One call in the history. Result: connected | missed | rejected | cancelled | elsewhere | failed.
/// ChatId is the person's (or group's) chat, empty for a call with several people outside a group.
/// </summary>
public sealed record CallLogDto(string Id, long Ts, long Duration, bool Incoming, bool Video, string Result, string ChatId, string Name,
                                string Phone, string? Avatar = null, bool Group = false);

public sealed record StarredDto(string ChatId, string ChatName, MessageDto Message);

public sealed record MessageDto(
    string Id, bool FromMe, string Sender, string SenderName, long Ts, string Kind, string Text,
    string? FileName, int Status, MediaDto? Media, ReplyDto? Reply, string[]? Reactions, string? MyReaction, bool Starred, bool Edited,
    string? Thumb = null, System.Text.Json.JsonElement? Extra = null, int Forwarded = 0);

/// <summary>A sticker or GIF for the sticker panel: the message it came in.</summary>
public sealed record StickerDto(string ChatId, string MessageId, int Width, int Height, string? Path, string? Thumb);

/// <summary>A group member for @mentions: the JID to mention, name, their 1:1 chat, number.</summary>
public sealed record MemberDto(string Jid, string Name, string ChatId, string Phone);

/// <summary>One person's receipt for your message: delivered (2) or read (3), when (Unix seconds).</summary>
public sealed record ReceiptDto(string User, string ChatId, string Name, int Status, long Ts, long DeliveredTs = 0);

/// <summary>The message a reply quotes.</summary>
public sealed record ReplyDto(string Id, bool FromMe, string SenderName, string Kind, string Preview, string? Thumb = null);

/// <summary>Attachment details; <c>Path</c> is set once the file has been downloaded.</summary>
public sealed record MediaDto(string Mime, int Width, int Height, int Seconds, int[]? Waveform, string? Path);

/// <summary>
/// Runs core\wafluent-core.exe (the Rust WhatsApp connection) and talks to it with
/// one JSON object per line over stdin/stdout. Events are raised on the UI thread.
/// Protocol: core/src/protocol.rs.
/// </summary>
public sealed class CoreClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DispatcherQueue _ui;
    private readonly object _writeLock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private bool _disposed;

    public event Action<string, string?>? StatusChanged;              // state, detail
    public event Action<string>? QrReceived;                         // pairing payload
    public event Action<IReadOnlyList<ChatDto>>? ChatsReceived;       // full list
    public event Action<ChatDto>? ChatReceived;                      // one chat changed
    public event Action<string, IReadOnlyList<MessageDto>>? MessagesReceived;
    public event Action<string, MessageDto>? MessageReceived;
    public event Action<string, IReadOnlyList<MessageDto>, bool>? OlderMessagesReceived;   // chat, messages, complete
    public event Action<string, string, IReadOnlyList<MessageDto>, long?>? SearchResults;  // chat, query, results, oldest time
    public event Action<string, string?, long>? FoundMessage;
    public event Action<string, string, IReadOnlyList<ReceiptDto>>? MessageInfoReceived;
    public event Action<string, IReadOnlyList<MessageDto>, IReadOnlyList<MessageDto>, IReadOnlyList<MessageDto>>? ChatMediaReceived;   // chat, media, docs, links   // chat, message, receipts
    public event Action<string, IReadOnlyList<MemberDto>>? GroupMembersReceived;            // group, members (not you)                // chat, message (none: nothing there), its time
    public event Action<string, string?>? AvatarReceived;            // chat id ("self" = you), JPEG path or null
    public event Action<string, string, string>? MediaReceived;      // chat, message, file path
    public event Action<string, string, string>? MediaFailed;        // chat, message, reason
    public event Action<string, string, MessageDto>? Sent;           // chat, temp id, stored message
    public event Action<string, string, string>? SendFailed;         // chat, temp id, reason
    public event Action<string, string, IReadOnlyList<string>, string?>? ReactionsReceived;   // chat, message, all, yours
    public event Action<string, IReadOnlyList<string>, int>? ReceiptReceived;
    public event Action<string, IReadOnlyList<string>>? ReceiptsChanged;
    public event Action<IReadOnlyList<ContactDto>>? ContactsReceived;
    public event Action<string, GroupInfoDto>? GroupInfoReceived;   // someone's receipt for these was recorded
    public event Action<string, MessageDto>? MessageUpdated;                 // chat, message (deleted/edited/starred)
    public event Action<string, string>? MessageRemoved;                     // chat, message id (deleted for me)
    public event Action<string>? ChatRemoved;                                // chat deleted
    public event Action<IReadOnlyList<StarredDto>>? StarredReceived;
    public event Action<string>? Opened;                                     // chat to show (openNumber)
    public event Action<CallDto>? CallChanged;
    public event Action<IReadOnlyList<string>, int>? CallPeople;            // a group call: who's in it besides you, how many more were rung
    public event Action<IReadOnlyList<CallLogDto>>? CallsReceived;          // the call history, newest first
    public event Action<IReadOnlyList<string>, bool>? FavouritesReceived;   // the phone's favourite chats; whether it's been heard yet
    public event Action<string, bool>? CallLinkReceived;                    // link, video
    /// <summary>The other side's voice in a call: 16-bit samples, or an Opus packet. Raised off the UI thread.</summary>
    public event Action<byte[], bool>? CallAudio;
    /// <summary>The other side's picture: an H.264 access unit, whether a decoder can start at it, their camera's quarter turns. Raised off the UI thread.</summary>
    public event Action<byte[], bool, int>? CallVideo;
    /// <summary>Video in the call: request | on | off | declined | ended | failed | keyframe.</summary>
    public event Action<string>? CallVideoState;
    public event Action<string, string>? Me;                                 // your name, number
    public event Action? FavoritesChanged;                                   // starred/unstarred on the phone
    public event Action<IReadOnlyList<StickerDto>, IReadOnlyList<StickerDto>, IReadOnlyList<StickerDto>>? Stickers;   // favourites, recent stickers, GIFs
    public event Action<string, string, string>? Typing;                     // chat, who (groups), typing | recording | paused
    public event Action<string, bool, long?>? Presence;                      // chat, online, last seen (Unix s)
    public event Action<bool, string>? Notice;                               // ok, text for a toast   // chat, message ids, 2 delivered / 3 read

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAFluent");

    public static string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, "core", "wafluent-core.exe");

    public static bool IsAvailable => File.Exists(ExecutablePath);

    public CoreClient(DispatcherQueue ui) => _ui = ui;

    public void Start()
    {
        Directory.CreateDirectory(DataDirectory);
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(DataDirectory);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start wafluent-core.");
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;

        _ = Task.Run(() => ReadEvents(_process.StandardOutput));
        _ = Task.Run(() => CopyLog(_process.StandardError));
    }

    // ───────────── Commands ─────────────

    public void LoadMessages(string chatId, int limit = 300) => Send(new { cmd = "loadMessages", chatId, limit });

    /// <summary>Messages before the given one: from the local store, else requested from the phone.</summary>
    public void LoadOlder(string chatId, long beforeTs, string beforeId, int limit = 100) =>
        Send(new { cmd = "loadOlder", chatId, beforeTs, beforeId, limit });

    /// <summary>Everything older than the given message back to <paramref name="untilTs"/> (answered by OlderMessagesReceived).</summary>
    public void LoadOlderUntil(string chatId, long beforeTs, string beforeId, long untilTs) =>
        Send(new { cmd = "loadOlderUntil", chatId, beforeTs, beforeId, untilTs });

    /// <summary>Messages in a chat containing the text, newest first (answered by SearchResults).</summary>
    public void SearchMessages(string chatId, string query) => Send(new { cmd = "searchMessages", chatId, query });

    /// <summary>The first message on or after a date (answered by FoundMessage).</summary>
    public void FindMessageAt(string chatId, long ts) => Send(new { cmd = "findMessageAt", chatId, ts });

    public void MarkRead(string chatId) => Send(new { cmd = "markRead", chatId });

    /// <summary>Disappearing messages: seconds (0 off). Synced with the phone and the other side.</summary>
    public void SetEphemeral(string chatId, int seconds) => Send(new { cmd = "setEphemeral", chatId, seconds });

    /// <summary>Recent stickers and GIFs for the panel; answered by <see cref="Stickers"/>.</summary>
    public void LoadStickers() => Send(new { cmd = "loadStickers" });

    /// <summary>Sends a sticker or GIF from the panel (not as forwarded).</summary>
    public void SendStored(string chatId, string messageId, string to) => Send(new { cmd = "sendStored", chatId, messageId, to });

    /// <summary>Uploads and sends a file: <paramref name="kind"/> "image", "video" or "document"; <paramref name="thumb"/> a JPEG preview file.</summary>
    public void SendMedia(string chatId, string path, string kind, string caption, string mime, int width, int height, int seconds, string? thumb, string tempId,
                          IReadOnlyList<byte>? waveform = null) =>
        Send(new { cmd = "sendMedia", chatId, path, kind, caption, mime, width, height, seconds, thumb, tempId,
                   waveform = (waveform ?? []).Select(b => (int)b).ToArray() });

    /// <summary>Stops an upload that hasn't been sent yet (its bubble's ✕).</summary>
    public void CancelSend(string tempId) => Send(new { cmd = "cancelSend", tempId });

    /// <summary>Shares contact cards (several go as one message).</summary>
    public void SendContacts(string chatId, IEnumerable<(string Name, string Phone)> contacts) =>
        Send(new { cmd = "sendContacts", chatId, contacts = contacts.Select(c => new { name = c.Name, phone = c.Phone }).ToList() });

    /// <summary>Starts a poll; <paramref name="endTime"/>: voting closes then (Unix seconds).</summary>
    public void SendPoll(string chatId, string question, IReadOnlyList<string> options, bool multiple, bool hideVoters, long? endTime) =>
        Send(new { cmd = "sendPoll", chatId, question, options, multiple, hideVoters, endTime });

    /// <summary>Uploads and sends an MP4 from GIF search as a GIF; <paramref name="thumb"/> is a JPEG preview file.</summary>
    public void SendGif(string to, string path, int width, int height, string? thumb) => Send(new { cmd = "sendGif", to, path, width, height, thumb });

    /// <summary>Your choice in a poll (none = take your vote back).</summary>
    public void VotePoll(string chatId, string messageId, IReadOnlyList<string> options) =>
        Send(new { cmd = "votePoll", chatId, messageId, options });

    /// <summary>Online while the window is in front: WhatsApp only sends typing/online updates then.</summary>
    public void SetPresence(bool available) => Send(new { cmd = "setPresence", available });

    /// <summary>Follow a 1:1 chat's online / last seen / typing.</summary>
    public void WatchPresence(string chatId) => Send(new { cmd = "watchPresence", chatId });

    /// <summary>Your typing state: "typing", "recording" or "paused".</summary>
    public void SendTyping(string chatId, string state) => Send(new { cmd = "sendTyping", chatId, state });

    /// <summary>Opens (or starts) the chat with a phone number; answered by <see cref="Opened"/>.</summary>
    public void OpenNumber(string phone) => Send(new { cmd = "openNumber", phone });

    /// <summary>`force`: also retry one the phone said it no longer has.</summary>
    public void DownloadMedia(string chatId, string messageId, bool force = false) =>
        Send(new { cmd = "downloadMedia", chatId, messageId, force });

    /// <summary>
    /// Fill in media details for messages stored before media support existed: the phone
    /// resends the 50 messages before <paramref name="beforeId"/> (default: the newest).
    /// </summary>
    public void BackfillMedia(string chatId, string? beforeId) => Send(new { cmd = "backfillMedia", chatId, beforeId });

    /// <summary>Sends text, quoting <paramref name="replyTo"/> when set. Answered by Sent / SendFailed with <paramref name="tempId"/>.</summary>
    public void SendText(string chatId, string text, string? replyTo, string tempId, IReadOnlyList<string>? mentions = null, LinkPreviews.Card? link = null,
                         bool everyone = false) =>
        Send(new
        {
            cmd = "sendText", chatId, text, replyTo, tempId, mentions = mentions ?? [], everyone,
            link = link is null ? null : new { url = link.Url, title = link.Title, description = link.Description, thumb = link.Thumb },
        });

    /// <summary>A group's members, for @mentions (answered by GroupMembersReceived).</summary>
    public void GroupMembers(string chatId) => Send(new { cmd = "groupMembers", chatId });

    /// <summary>React to a message; "" removes your reaction. Answered by ReactionsReceived.</summary>
    public void React(string chatId, string messageId, string emoji) => Send(new { cmd = "react", chatId, messageId, emoji });

    /// <summary>Pin or unpin a chat on the phone too; the core answers with the chat's stored state.</summary>
    public void SetPinned(string chatId, bool pinned) => Send(new { cmd = "setPinned", chatId, pinned });

    /// <summary>archive | unarchive | mute (untilMs, null = always) | unmute | markRead | markUnread | clear | delete | block | unblock.</summary>
    public void ChatAction(string chatId, string action, long? untilMs = null) => Send(new { cmd = "chatAction", chatId, action, untilMs });

    public void SaveContact(string chatId, string firstName, string lastName, bool syncToPhone) =>
        Send(new { cmd = "saveContact", chatId, firstName, lastName, syncToPhone });

    public void ReportContact(string chatId) => Send(new { cmd = "reportContact", chatId });

    /// <summary>Writes the whole chat as text to <paramref name="path"/> (times in this PC's zone).</summary>
    public void ExportChat(string chatId, string path) =>
        Send(new { cmd = "exportChat", chatId, path, utcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes });

    public void Forward(string chatId, string messageId, IReadOnlyList<string> to) => Send(new { cmd = "forward", chatId, messageId, to });

    /// <summary><paramref name="duration"/>: seconds the pin lasts (86400, 604800 or 2592000).</summary>
    public void PinMessage(string chatId, string messageId, bool pin, int duration = 604_800) =>
        Send(new { cmd = "pinMessage", chatId, messageId, pin, duration });

    public void StarMessage(string chatId, string messageId, bool star) => Send(new { cmd = "starMessage", chatId, messageId, star });

    public void DeleteMessage(string chatId, string messageId, bool forEveryone) => Send(new { cmd = "deleteMessage", chatId, messageId, forEveryone });

    public void Report(string chatId, string messageId) => Send(new { cmd = "report", chatId, messageId });

    /// <summary>New text for one of your messages (the bubble updates through MessageUpdated).</summary>
    public void EditMessage(string chatId, string messageId, string text) => Send(new { cmd = "editMessage", chatId, messageId, text });

    /// <summary>Who got and read one of your messages (answered by MessageInfoReceived).</summary>
    public void MessageInfo(string chatId, string messageId) => Send(new { cmd = "messageInfo", chatId, messageId });

    public void LoadStarred() => Send(new { cmd = "loadStarred" });

    public void SetGroupSubject(string chatId, string subject) => Send(new { cmd = "setGroupSubject", chatId, subject });
    public void SetGroupDescription(string chatId, string description) => Send(new { cmd = "setGroupDescription", chatId, description });
    /// <summary><paramref name="path"/>: a square JPEG.</summary>
    public void SetGroupPicture(string chatId, string path) => Send(new { cmd = "setGroupPicture", chatId, path });
    public void AddGroupMembers(string chatId, IReadOnlyList<string> members) => Send(new { cmd = "addGroupMembers", chatId, members });

    /// <summary>Your saved contacts; answered by <see cref="ContactsReceived"/>.</summary>
    public void LoadContacts() => Send(new { cmd = "loadContacts" });

    /// <summary>Creates a group with these members (chat ids); the new group is then opened.</summary>
    public void CreateGroup(string subject, IReadOnlyList<string> members) => Send(new { cmd = "createGroup", subject, members });

    /// <summary>Saves a number as a contact and opens its chat.</summary>
    public void SaveNewContact(string phone, string firstName, string lastName, bool syncToPhone) =>
        Send(new { cmd = "saveNewContact", phone, firstName, lastName, syncToPhone });

    /// <summary>A chat's Media, links and docs; answered by <see cref="ChatMediaReceived"/>.</summary>
    public void LoadChatMedia(string chatId) => Send(new { cmd = "loadChatMedia", chatId });

    /// <summary>Your favourite chats, all of them: written to the phone.</summary>
    public void SetFavourites(IReadOnlyList<string> ids) => Send(new { cmd = "setFavourites", ids });

    /// <summary>Joins the call behind a call link; answered by <see cref="CallChanged"/>.</summary>
    public void JoinCallLink(string url, bool video) => Send(new { cmd = "joinCallLink", url, video });

    /// <summary>The call history; answered by <see cref="CallsReceived"/> (and again whenever it changes).</summary>
    public void LoadCalls() => Send(new { cmd = "loadCalls" });
    public void DeleteCall(string id) => Send(new { cmd = "deleteCall", id });
    /// <summary>A link anyone with WhatsApp can join a call with; answered by <see cref="CallLinkReceived"/>.</summary>
    public void CreateCallLink(bool video) => Send(new { cmd = "createCallLink", video });

    /// <summary>Calls a chat (voice); answered by <see cref="CallChanged"/>.</summary>
    public void StartCall(string chatId, bool video) => Send(new { cmd = "startCall", chatId, video });
    /// <summary><paramref name="video"/>: answer a video call with your camera too.</summary>
    public void AcceptCall(string callId, bool video) => Send(new { cmd = "acceptCall", callId, video });
    public void RejectCall(string callId) => Send(new { cmd = "rejectCall", callId });
    /// <summary>Hangs up, or stops calling.</summary>
    public void EndCall() => Send(new { cmd = "endCall" });
    public void MuteCall(bool muted) => Send(new { cmd = "muteCall", muted });
    /// <summary>Your camera on (asks to switch to video, or accepts their asking) or off.</summary>
    public void SetCallVideo(bool on) => Send(new { cmd = "setCallVideo", on });
    /// <summary>Your camera in a call: one H.264 access unit.</summary>
    public void SendCallVideo(byte[] unit) => Send(new { cmd = "callVideo", data = Convert.ToBase64String(unit) });
    /// <summary>Your microphone in a call: 960 samples (60 ms at 16 kHz), 16-bit.</summary>
    public void SendCallAudio(byte[] pcm) => Send(new { cmd = "callAudio", data = Convert.ToBase64String(pcm) });

    public void Logout() => Send(new { cmd = "logout" });

    private void Send(object command)
    {
        var line = JsonSerializer.Serialize(command, Json);
        lock (_writeLock)
        {
            try { _stdin?.WriteLine(line); }
            catch (IOException) { /* core exited; StatusChanged already reported it */ }
            catch (ObjectDisposedException) { /* shutting down */ }
        }
    }

    // ───────────── Events ─────────────

    private async Task ReadEvents(StreamReader stdout)
    {
        while (await stdout.ReadLineAsync() is { } line)
        {
            try { Dispatch(line); }
            catch (Exception e) { Debug.WriteLine($"wafluent-core: bad event {line}: {e.Message}"); }
        }
        if (!_disposed) Post(() => StatusChanged?.Invoke("error", "The WhatsApp connection stopped unexpectedly."));
    }

    private void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "status":
                var state = root.GetProperty("state").GetString() ?? "";
                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                Post(() => StatusChanged?.Invoke(state, detail));
                break;
            case "qr":
                var code = root.GetProperty("code").GetString() ?? "";
                Post(() => QrReceived?.Invoke(code));
                break;
            case "chats":
                var chats = root.GetProperty("chats").Deserialize<List<ChatDto>>(Json) ?? [];
                Post(() => ChatsReceived?.Invoke(chats));
                break;
            case "chat":
                var chat = root.GetProperty("chat").Deserialize<ChatDto>(Json);
                if (chat is not null) Post(() => ChatReceived?.Invoke(chat));
                break;
            case "messages":
                var chatId = root.GetProperty("chatId").GetString() ?? "";
                var messages = root.GetProperty("messages").Deserialize<List<MessageDto>>(Json) ?? [];
                Post(() => MessagesReceived?.Invoke(chatId, messages));
                break;
            case "searchResults":
                var searchChat = root.GetProperty("chatId").GetString() ?? "";
                var searchQuery = root.GetProperty("query").GetString() ?? "";
                var hits = root.GetProperty("results").Deserialize<List<MessageDto>>(Json) ?? [];
                long? oldestTs = root.TryGetProperty("oldestTs", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : null;
                Post(() => SearchResults?.Invoke(searchChat, searchQuery, hits, oldestTs));
                break;
            case "groupInfo":
                var groupChat = root.GetProperty("chatId").GetString() ?? "";
                if (root.GetProperty("info").Deserialize<GroupInfoDto>(Json) is { } groupInfo)
                    Post(() => GroupInfoReceived?.Invoke(groupChat, groupInfo));
                break;
            case "groupMembers":
                var membersChat = root.GetProperty("chatId").GetString() ?? "";
                var members = root.GetProperty("members").Deserialize<List<MemberDto>>(Json) ?? [];
                Post(() => GroupMembersReceived?.Invoke(membersChat, members));
                break;
            case "messageInfo":
                var infoChat = root.GetProperty("chatId").GetString() ?? "";
                var infoId = root.GetProperty("messageId").GetString() ?? "";
                var receipts = root.GetProperty("receipts").Deserialize<List<ReceiptDto>>(Json) ?? [];
                Post(() => MessageInfoReceived?.Invoke(infoChat, infoId, receipts));
                break;
            case "foundMessage":
                var foundChat = root.GetProperty("chatId").GetString() ?? "";
                var foundId = root.TryGetProperty("messageId", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                var foundTs = root.GetProperty("ts").GetInt64();
                Post(() => FoundMessage?.Invoke(foundChat, foundId, foundTs));
                break;
            case "olderMessages":
                var olderChat = root.GetProperty("chatId").GetString() ?? "";
                var older = root.GetProperty("messages").Deserialize<List<MessageDto>>(Json) ?? [];
                var complete = root.GetProperty("complete").GetBoolean();
                Post(() => OlderMessagesReceived?.Invoke(olderChat, older, complete));
                break;
            case "media":
            case "mediaFailed":
                var mediaChat = root.GetProperty("chatId").GetString() ?? "";
                var mediaMsg = root.GetProperty("messageId").GetString() ?? "";
                if (root.GetProperty("type").GetString() == "media")
                {
                    var file = root.GetProperty("path").GetString() ?? "";
                    Post(() => MediaReceived?.Invoke(mediaChat, mediaMsg, file));
                }
                else
                {
                    var reason = root.GetProperty("reason").GetString() ?? "";
                    Post(() => MediaFailed?.Invoke(mediaChat, mediaMsg, reason));
                }
                break;
            case "sent":
                var sentChat = root.GetProperty("chatId").GetString() ?? "";
                var sentTemp = root.GetProperty("tempId").GetString() ?? "";
                var sentMessage = root.GetProperty("message").Deserialize<MessageDto>(Json);
                if (sentMessage is not null) Post(() => Sent?.Invoke(sentChat, sentTemp, sentMessage));
                break;
            case "sendFailed":
                var failedChat = root.GetProperty("chatId").GetString() ?? "";
                var failedTemp = root.GetProperty("tempId").GetString() ?? "";
                var failReason = root.GetProperty("reason").GetString() ?? "";
                Post(() => SendFailed?.Invoke(failedChat, failedTemp, failReason));
                break;
            case "reactions":
                var reactChat = root.GetProperty("chatId").GetString() ?? "";
                var reactMsg = root.GetProperty("messageId").GetString() ?? "";
                var all = root.GetProperty("reactions").Deserialize<List<string>>(Json) ?? [];
                var mine = root.TryGetProperty("myReaction", out var my) && my.ValueKind == JsonValueKind.String ? my.GetString() : null;
                Post(() => ReactionsReceived?.Invoke(reactChat, reactMsg, all, mine));
                break;
            case "messageUpdated":
                var updChat = root.GetProperty("chatId").GetString() ?? "";
                var updated = root.GetProperty("message").Deserialize<MessageDto>(Json);
                if (updated is not null) Post(() => MessageUpdated?.Invoke(updChat, updated));
                break;
            case "messageRemoved":
                var remChat = root.GetProperty("chatId").GetString() ?? "";
                var remId = root.GetProperty("messageId").GetString() ?? "";
                Post(() => MessageRemoved?.Invoke(remChat, remId));
                break;
            case "chatRemoved":
                var goneChat = root.GetProperty("chatId").GetString() ?? "";
                Post(() => ChatRemoved?.Invoke(goneChat));
                break;
            case "chatMedia":
                var galleryChat = root.GetProperty("chatId").GetString() ?? "";
                Func<string, List<MessageDto>> list = name => root.GetProperty(name).Deserialize<List<MessageDto>>(Json) ?? [];
                var (galleryMedia, galleryDocs, galleryLinks) = (list("media"), list("docs"), list("links"));
                Post(() => ChatMediaReceived?.Invoke(galleryChat, galleryMedia, galleryDocs, galleryLinks));
                break;
            case "contacts":
                var contacts = root.GetProperty("contacts").Deserialize<List<ContactDto>>(Json) ?? [];
                Post(() => ContactsReceived?.Invoke(contacts));
                break;
            case "starred":
                var items = root.GetProperty("items").Deserialize<List<StarredDto>>(Json) ?? [];
                Post(() => StarredReceived?.Invoke(items));
                break;
            case "typing":
                var typingChat = root.GetProperty("chatId").GetString() ?? "";
                var who = root.GetProperty("who").GetString() ?? "";
                var typingState = root.GetProperty("state").GetString() ?? "";
                Post(() => Typing?.Invoke(typingChat, who, typingState));
                break;
            case "presence":
                var presenceChat = root.GetProperty("chatId").GetString() ?? "";
                var online = root.GetProperty("online").GetBoolean();
                long? lastSeen = root.TryGetProperty("lastSeen", out var ls) && ls.ValueKind == System.Text.Json.JsonValueKind.Number ? ls.GetInt64() : null;
                Post(() => Presence?.Invoke(presenceChat, online, lastSeen));
                break;
            case "favoritesChanged":
                Post(() => FavoritesChanged?.Invoke());
                break;
            case "stickers":
                var favorites = root.GetProperty("favorites").Deserialize<List<StickerDto>>(Json) ?? [];
                var stickers = root.GetProperty("stickers").Deserialize<List<StickerDto>>(Json) ?? [];
                var gifs = root.GetProperty("gifs").Deserialize<List<StickerDto>>(Json) ?? [];
                Post(() => Stickers?.Invoke(favorites, stickers, gifs));
                break;
            case "me":
                var myName = root.GetProperty("name").GetString() ?? "";
                var myPhone = root.GetProperty("phone").GetString() ?? "";
                Post(() => Me?.Invoke(myName, myPhone));
                break;
            case "callAudio":
                // Straight to the player: the UI thread's queue would make the voice stutter.
                var sound = root.GetProperty("data").GetBytesFromBase64();
                CallAudio?.Invoke(sound, root.TryGetProperty("opus", out var op) && op.ValueKind == JsonValueKind.True);
                break;
            case "favourites":
                var favouriteIds = root.GetProperty("ids").Deserialize<List<string>>(Json) ?? [];
                var favouritesSynced = root.GetProperty("synced").GetBoolean();
                Post(() => FavouritesReceived?.Invoke(favouriteIds, favouritesSynced));
                break;
            case "calls":
                var callLog = root.GetProperty("calls").Deserialize<List<CallLogDto>>(Json) ?? [];
                Post(() => CallsReceived?.Invoke(callLog));
                break;
            case "callLink":
                var linkUrl = root.GetProperty("url").GetString() ?? "";
                var linkVideo = root.GetProperty("video").GetBoolean();
                Post(() => CallLinkReceived?.Invoke(linkUrl, linkVideo));
                break;
            case "callVideo":
                var picture = root.GetProperty("data").GetBytesFromBase64();
                var key = root.GetProperty("key").GetBoolean();
                CallVideo?.Invoke(picture, key, root.TryGetProperty("rotation", out var turn) ? turn.GetInt32() : 0);
                break;
            case "callPeople":
                var inCall = root.GetProperty("names").Deserialize<List<string>>(Json) ?? [];
                var stillRung = root.GetProperty("waiting").GetInt32();
                Post(() => CallPeople?.Invoke(inCall, stillRung));
                break;
            case "callVideoState":
                var videoState = root.GetProperty("state").GetString() ?? "";
                Post(() => CallVideoState?.Invoke(videoState));
                break;
            case "call":
                if (root.Deserialize<CallDto>(Json) is { } call) Post(() => CallChanged?.Invoke(call));
                break;
            case "opened":
                var openedChat = root.GetProperty("chatId").GetString() ?? "";
                Post(() => Opened?.Invoke(openedChat));
                break;
            case "notice":
                var ok = root.GetProperty("ok").GetBoolean();
                var noticeText = root.GetProperty("text").GetString() ?? "";
                Post(() => Notice?.Invoke(ok, noticeText));
                break;
            case "receipt":
                var receiptChat = root.GetProperty("chatId").GetString() ?? "";
                var ids = root.GetProperty("messageIds").Deserialize<List<string>>(Json) ?? [];
                var status = root.GetProperty("status").GetInt32();
                Post(() => ReceiptReceived?.Invoke(receiptChat, ids, status));
                break;
            case "receiptsChanged":
                var changedChat = root.GetProperty("chatId").GetString() ?? "";
                var changedIds = root.GetProperty("messageIds").Deserialize<List<string>>(Json) ?? [];
                Post(() => ReceiptsChanged?.Invoke(changedChat, changedIds));
                break;
            case "avatar":
                var avatarChat = root.GetProperty("chatId").GetString() ?? "";
                var avatarPath = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                Post(() => AvatarReceived?.Invoke(avatarChat, avatarPath));
                break;
            case "message":
                var target = root.GetProperty("chatId").GetString() ?? "";
                var message = root.GetProperty("message").Deserialize<MessageDto>(Json);
                if (message is not null) Post(() => MessageReceived?.Invoke(target, message));
                break;
        }
    }

    /// <summary>Core logs (stderr) go to %LOCALAPPDATA%\WAFluent\core.log, replaced each run.</summary>
    private static async Task CopyLog(StreamReader stderr)
    {
        try
        {
            await using var log = new StreamWriter(Path.Combine(DataDirectory, "core.log"), append: false) { AutoFlush = true };
            while (await stderr.ReadLineAsync() is { } line) await log.WriteLineAsync(line);
        }
        catch (IOException) { }
    }

    private void Post(Action action) => _ui.TryEnqueue(() => action());

    /// <summary>Closing stdin tells the core to shut down cleanly; kill it if it doesn't.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_writeLock)
        {
            try { _stdin?.Close(); } catch (IOException) { }
        }
        if (_process is { HasExited: false } p && !p.WaitForExit(3000)) p.Kill();
        _process?.Dispose();
    }
}
