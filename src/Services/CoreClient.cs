using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace WhatsAppNative.Services;

public sealed record ChatDto(
    string Id, string Name, bool IsGroup, int Unread, bool Pinned, bool Archived, bool Muted,
    long LastTs, string Preview, string PreviewKind, bool LastFromMe, int LastStatus, string? LastSender);

public sealed record MessageDto(
    string Id, bool FromMe, string Sender, string SenderName, long Ts, string Kind, string Text,
    string? FileName, int Status);

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
