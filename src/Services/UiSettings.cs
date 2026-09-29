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

    /// <summary>System (follows Windows), Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Minimizing hides the window to a tray icon.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>The Windows accent colour instead of WhatsApp green (Helpers/AppColors).</summary>
    public bool UseSystemAccent { get; set; }

    /// <summary>WhatsApp's classic bubbles (off: the rounder iMessage-style ones, in WhatsApp's colours).</summary>
    public bool ClassicBubbles { get; set; }

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
