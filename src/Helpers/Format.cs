using System.Globalization;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.Helpers;

/// <summary>Turns core data into what the UI shows, WhatsApp style.</summary>
public static class Format
{
    public static DateTime FromUnix(long seconds) =>
        seconds <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    /// <summary>Chat list: "14:05", "Yesterday", "Monday", then a short date.</summary>
    public static string ListTime(DateTime local)
    {
        if (local == DateTime.MinValue) return "";
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("H:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return local.ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>Divider between days in a conversation.</summary>
    public static string DayLabel(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return "Today";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return local.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string PreviewGlyph(string kind) => kind switch
    {
        "image" => Glyphs.Photo,
        "video" or "gif" => Glyphs.Video,
        "voice" or "audio" => Glyphs.Mic,
        "document" => Glyphs.Document,
        "location" => Glyphs.Location,
        "contact" => Glyphs.Contact,
        "poll" => Glyphs.Poll,
        _ => "",
    };

    public static Delivery ToDelivery(int status) => status switch
    {
        3 => Delivery.Read,
        2 => Delivery.Delivered,
        _ => Delivery.Sent,
    };

    /// <summary>Media isn't downloaded yet, so non-text messages show a labelled line.</summary>
    public static Message ToMessage(MessageDto dto, bool isGroup)
    {
        var when = FromUnix(dto.Ts);
        var common = new Message
        {
            Id = dto.Id,
            IsOutgoing = dto.FromMe,
            Timestamp = when,
            UnixTs = dto.Ts,
            Time = when.ToString("H:mm"),
            Delivery = dto.FromMe ? ToDelivery(dto.Status) : Delivery.None,
            SenderName = isGroup && !dto.FromMe ? dto.SenderName : "",
        };

        if (dto.Kind == "document")
        {
            var name = string.IsNullOrEmpty(dto.FileName) ? "Document" : dto.FileName;
            var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return Clone(common, MessageKind.File, text: dto.Text, fileName: name,
                         fileDetails: ext.Length > 0 ? $"{ext} document" : "Document");
        }

        var text = dto.Kind switch
        {
            "text" => dto.Text,
            "image" => Label("📷", "Photo", dto.Text),
            "video" => Label("🎥", "Video", dto.Text),
            "gif" => Label("🎞️", "GIF", dto.Text),
            "voice" => "🎤 Voice message",
            "audio" => "🎵 Audio",
            "sticker" => "💟 Sticker",
            "location" => Label("📍", "Location", dto.Text),
            "contact" => Label("👤", "Contact", dto.Text),
            "poll" => Label("📊", "Poll", dto.Text),
            _ => dto.Text.Length > 0 ? dto.Text : "Unsupported message",
        };
        return Clone(common, MessageKind.Text, text: text);
    }

    private static string Label(string emoji, string name, string caption) =>
        caption.Length > 0 ? $"{emoji} {caption}" : $"{emoji} {name}";

    private static Message Clone(Message m, MessageKind kind, string text = "", string fileName = "", string fileDetails = "") => new()
    {
        Id = m.Id,
        Kind = kind,
        IsOutgoing = m.IsOutgoing,
        Timestamp = m.Timestamp,
        UnixTs = m.UnixTs,
        Time = m.Time,
        Delivery = m.Delivery,
        SenderName = m.SenderName,
        Text = text,
        FileName = fileName,
        FileDetails = fileDetails,
    };
}
