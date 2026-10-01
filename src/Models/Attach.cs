using Microsoft.UI.Xaml.Media;

namespace WhatsAppNative.Models;

/// <summary>A file in the send preview (MainWindow.Attach.cs): what the core needs, plus its caption.</summary>
public sealed class OutgoingFile : Observable
{
    public required string Path { get; init; }
    /// <summary>"image", "video" or "document".</summary>
    public required string Kind { get; init; }
    public required string Mime { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Seconds { get; init; }
    /// <summary>Small JPEG for the chat bubble (pictures and videos).</summary>
    public string? Thumb { get; init; }
    /// <summary>The strip's tile and the big preview for pictures and videos.</summary>
    public ImageSource? Preview { get; init; }
    public long Size { get; init; }

    /// <summary>Bigger than standard quality, so HD keeps more (photos over 1600 px, videos over 480p).</summary>
    public bool SupportsHd => Helpers.MediaCompression.SupportsHd(Kind, Width, Height);

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public bool IsAudio => Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    public bool IsImage => Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"2 MB - PNG", under "No preview available".</summary>
    public string SizeLabel => $"{Helpers.Format.FileSize(Size)} - {(Extension.Length > 0 ? Extension : "File")}";

    private string _caption = "";
    public string Caption { get => _caption; set => Set(ref _caption, value); }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Someone in the Send contacts list.</summary>
public sealed class ContactRow : Observable
{
    public required string Name { get; init; }
    /// <summary>Digits only.</summary>
    public required string Phone { get; init; }
    private string _subtitle = "";
    public string Subtitle
    {
        get => _subtitle;
        set { if (Set(ref _subtitle, value)) Raise(nameof(HasSubtitle)); }
    }
    public string? AvatarPath { get; init; }
    /// <summary>New chat: their 1:1 chat's id, and whether you've blocked them.</summary>
    public string ChatId { get; init; } = "";
    public bool IsBlocked { get; set; }

    /// <summary>Forwarding: the chat this row sends to.</summary>
    public Chat? Chat { get; init; }
    public bool HasSubtitle => Subtitle.Length > 0;

    private bool _picked;
    public bool Picked { get => _picked; set => Set(ref _picked, value); }
}

/// <summary>"You" or "Contacts", with its people.</summary>
public sealed class ContactGroup(string key, IEnumerable<ContactRow> rows) : List<ContactRow>(rows)
{
    public string Key { get; } = key;
}

/// <summary>Someone @mentioned in the composer: their name, JID, 1:1 chat and number ("all": everyone).</summary>
public sealed record Mention(string Name, string Jid, string ChatId, string Phone);

/// <summary>An option box in Create poll.</summary>
public sealed class PollOptionDraft : Observable
{
    private string _text = "";
    public string Text { get => _text; set => Set(ref _text, value); }
}
