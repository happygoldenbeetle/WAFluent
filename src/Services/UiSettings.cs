using System.Text.Json;

namespace WhatsAppNative.Services;

/// <summary>
/// Window/layout preferences, kept in %LOCALAPPDATA%\WAFluent\ui.json (the app is
/// unpackaged, so there is no ApplicationData). Unreadable or missing file = defaults.
/// </summary>
public sealed class UiSettings
{
    private static readonly string FilePath = Path.Combine(CoreClient.DataDirectory, "ui.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Width of the chat list pane, in effective pixels.</summary>
    public double ChatListWidth { get; set; } = 340;

    /// <summary>The chat list was dragged shut (or double-clicked closed).</summary>
    public bool ChatListCollapsed { get; set; }

    /// <summary>The window's last size and place (physical pixels, restored bounds) and whether it was maximized.</summary>
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>System (follows Windows), Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Minimizing hides the window to a tray icon.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>The Windows accent colour instead of WhatsApp green (Helpers/AppColors).</summary>
    public bool UseSystemAccent { get; set; }

    /// <summary>The microphone voice notes record from (its device id; empty: Windows' default).</summary>
    public string MicrophoneId { get; set; } = "";

    /// <summary>When the Calls page was last looked at (Unix seconds): missed calls after it are counted on the rail.</summary>
    public long CallsSeenAt { get; set; }

    /// <summary>The camera video calls use ("" = Windows' default).</summary>
    public string CameraId { get; set; } = "";

    /// <summary>The pointer is a hand over anything clickable (Helpers/HandCursor).</summary>
    public bool HandCursor { get; set; } = true;

    /// <summary>Times as "18:27" (off: "6:27 pm").</summary>
    public bool Use24Hour { get; set; } = true;

    /// <summary>Photos and videos go in HD by default (when they're bigger than standard).</summary>
    public bool HdMedia { get; set; }

    /// <summary>Windows notifications for new messages (not for muted chats).</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>A GIPHY API key to use instead of the built-in one (no UI; empty: built-in).</summary>
    public string GiphyKey { get; set; } = "";

    /// <summary>Blur profile photos, names and numbers (Controls/Redact).</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>The five reactions in the message menu; the first is also the double-click reaction.</summary>
    public string[] QuickReactions
    {
        get => _quick;
        set => _quick = value is { Length: 5 } && value.All(e => !string.IsNullOrWhiteSpace(e)) ? value : _quick;
    }

    private string[] _quick = ["❤️", "👍", "😂", "😮", "😢"];

    /// <summary>Favourite chats (ids), kept on this PC.</summary>
    public HashSet<string> Favourites { get; set; } = [];

    /// <summary>The favourites kept here have been joined with the phone's (from then on the phone's list is the list).</summary>
    public bool FavouritesMerged { get; set; }

    /// <summary>Emoji keyboard: most recent first, exactly as used (skin tone included).</summary>
    public List<string> RecentEmoji { get; set; } = [];

    /// <summary>Emoji keyboard: the skin tone you last picked for each emoji (base emoji -> variant).</summary>
    public Dictionary<string, string> SkinTones { get; set; } = [];

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CoreClient.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
