using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhatsAppNative.Models;

public enum Delivery { None, Sent, Delivered, Read }

public enum MessageKind { Text, Image, File, DateDivider }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Message
{
    public string Id { get; init; } = "";
    public MessageKind Kind { get; init; }
    public bool IsOutgoing { get; init; }
    public string Text { get; init; } = "";
    public string Time { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public Delivery Delivery { get; init; }
    public string Reaction { get; init; } = "";

    /// <summary>Shown above incoming messages in groups.</summary>
    public string SenderName { get; init; } = "";

    // Image messages
    public string? ImagePath { get; init; }

    // File messages
    public string FileName { get; init; } = "";
    public string FileDetails { get; init; } = "";

    public bool HasText => Text.Length > 0;
    public bool ShowSender => !IsOutgoing && SenderName.Length > 0;

    /// <summary>
    /// Invisible run appended to the text so the last line leaves room for the time/ticks overlay.
    /// Must end in a non-whitespace character: trailing spaces take no width at a line end.
    /// </summary>
    public string TimeSpacer => "  " + Time + (IsOutgoing ? " ___" : "");
}

public sealed class Chat : Observable
{
    private string _name = "";
    private string _preview = "";
    private string _previewGlyph = "";
    private string _time = "";
    private int _unread;
    private bool _isPinned;
    private Delivery _lastDelivery;

    /// <summary>WhatsApp JID for live chats; empty for sample data.</summary>
    public string Id { get; init; } = "";
    public bool IsGroup { get; init; }
    public string Status { get; init; } = "";
    public bool IsTyping { get; init; }
    public bool HasMention { get; init; }
    public bool HasStatus { get; init; }   // unseen status update: green ring around the avatar
    public DateTime LastActivity { get; set; }

    /// <summary>True once this chat's history has been fetched from the core.</summary>
    public bool MessagesLoaded { get; set; }

    public ObservableCollection<Message> Messages { get; } = new();

    public required string Name { get => _name; set => Set(ref _name, value); }
    public string Preview { get => _preview; set => Set(ref _preview, value); }
    public string PreviewGlyph { get => _previewGlyph; set => Set(ref _previewGlyph, value); }   // e.g. a camera icon before the preview
    public string Time { get => _time; set => Set(ref _time, value); }
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    public int Unread
    {
        get => _unread;
        set { if (Set(ref _unread, value)) Raise(nameof(HasUnread)); }
    }

    /// <summary>Delivery state of the last message, when it was outgoing.</summary>
    public Delivery LastDelivery { get => _lastDelivery; set => Set(ref _lastDelivery, value); }

    public bool HasUnread => Unread > 0;
}
