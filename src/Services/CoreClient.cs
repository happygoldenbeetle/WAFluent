using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace WhatsAppNative.Services;

public sealed record ChatDto(
    string Id, string Name, bool IsGroup, int Unread, bool Pinned, bool Archived, bool Muted,
    long LastTs, string Preview, string PreviewKind, bool LastFromMe, int LastStatus, string? LastSender,
    string? Avatar, long PinnedAt, bool Blocked, bool Saved, PinnedDto? PinnedMessage, string? PushName, PhoneDto? Phone);

public sealed record PhoneDto(string Region, string Code, string National);

public sealed record PinnedDto(string Id, string Preview);

public sealed record StarredDto(string ChatId, string ChatName, MessageDto Message);

public sealed record MessageDto(
    string Id, bool FromMe, string Sender, string SenderName, long Ts, string Kind, string Text,
    string? FileName, int Status, MediaDto? Media, ReplyDto? Reply, string[]? Reactions, string? MyReaction, bool Starred, bool Edited);

/// <summary>The message a reply quotes.</summary>
public sealed record ReplyDto(string Id, bool FromMe, string SenderName, string Kind, string Preview);

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
    public event Action<string, string?>? AvatarReceived;            // chat id ("self" = you), JPEG path or null
    public event Action<string, string, string>? MediaReceived;      // chat, message, file path
    public event Action<string, string, string>? MediaFailed;        // chat, message, reason
    public event Action<string, string, MessageDto>? Sent;           // chat, temp id, stored message
    public event Action<string, string, string>? SendFailed;         // chat, temp id, reason
    public event Action<string, string, IReadOnlyList<string>, string?>? ReactionsReceived;   // chat, message, all, yours
    public event Action<string, IReadOnlyList<string>, int>? ReceiptReceived;
    public event Action<string, MessageDto>? MessageUpdated;                 // chat, message (deleted/edited/starred)
    public event Action<string, string>? MessageRemoved;                     // chat, message id (deleted for me)
    public event Action<string>? ChatRemoved;                                // chat deleted
    public event Action<IReadOnlyList<StarredDto>>? StarredReceived;
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

    public void MarkRead(string chatId) => Send(new { cmd = "markRead", chatId });

    /// <summary>`force`: also retry one the phone said it no longer has.</summary>
    public void DownloadMedia(string chatId, string messageId, bool force = false) =>
        Send(new { cmd = "downloadMedia", chatId, messageId, force });

    /// <summary>
    /// Fill in media details for messages stored before media support existed: the phone
    /// resends the 50 messages before <paramref name="beforeId"/> (default: the newest).
    /// </summary>
    public void BackfillMedia(string chatId, string? beforeId) => Send(new { cmd = "backfillMedia", chatId, beforeId });

    /// <summary>Sends text, quoting <paramref name="replyTo"/> when set. Answered by Sent / SendFailed with <paramref name="tempId"/>.</summary>
    public void SendText(string chatId, string text, string? replyTo, string tempId) =>
        Send(new { cmd = "sendText", chatId, text, replyTo, tempId });

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

    public void PinMessage(string chatId, string messageId, bool pin) => Send(new { cmd = "pinMessage", chatId, messageId, pin });

    public void StarMessage(string chatId, string messageId, bool star) => Send(new { cmd = "starMessage", chatId, messageId, star });

    public void DeleteMessage(string chatId, string messageId, bool forEveryone) => Send(new { cmd = "deleteMessage", chatId, messageId, forEveryone });

    public void Report(string chatId, string messageId) => Send(new { cmd = "report", chatId, messageId });

    public void LoadStarred() => Send(new { cmd = "loadStarred" });

    public void Logout() => Send(new { cmd = "logout" });

    private void Send(object command)
    {
        var line = JsonSerializer.Serialize(command, Json);
        lock (_writeLock)
        {
            try { _stdin?.WriteLine(line); }
            catch (IOException) { /* core exited; StatusChanged already reported it */ }
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
            case "starred":
                var items = root.GetProperty("items").Deserialize<List<StarredDto>>(Json) ?? [];
                Post(() => StarredReceived?.Invoke(items));
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
