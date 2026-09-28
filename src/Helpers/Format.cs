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

    /// <summary>One-line summary of a message for a quote ("Photo", the text, the file name...).</summary>
    public static string QuotePreview(Message m) => m.Kind switch
    {
        MessageKind.Image => m.HasText ? m.Text : "Photo",
        MessageKind.Sticker => "Sticker",
        MessageKind.Voice => $"Voice message ({Duration(TimeSpan.FromSeconds(m.Seconds))})",
        MessageKind.File => m.FileName,
        _ => m.Text,
    };

    public static string QuoteGlyph(Message m) => m.Kind switch
    {
        MessageKind.Image => Glyphs.Photo,
        MessageKind.Voice => Glyphs.Mic,
        MessageKind.File => Glyphs.Document,
        _ => "",
    };

    public static Delivery ToDelivery(int status) => status switch
    {
        3 => Delivery.Read,
        2 => Delivery.Delivered,
        _ => Delivery.Sent,
    };

    /// <summary>
    /// Images, stickers and voice notes get their own bubbles once their download details are
    /// known; other media (video, documents...) still show as a labelled line for now.
    /// </summary>
    public static Message ToMessage(MessageDto dto, bool isGroup)
    {
        var when = FromUnix(dto.Ts);
        var media = dto.Media;

        Message Make(MessageKind kind, string text = "", string fileName = "", string fileDetails = "",
                     double width = 300, double height = 200) => new()
        {
            Id = dto.Id,
            Kind = kind,
            IsOutgoing = dto.FromMe,
            Timestamp = when,
            UnixTs = dto.Ts,
            Time = when.ToString("H:mm"),
            Delivery = dto.FromMe ? ToDelivery(dto.Status) : Delivery.None,
            SenderName = isGroup && !dto.FromMe ? dto.SenderName : "",
            Text = text,
            FileName = fileName,
            FileDetails = fileDetails,
            HasMedia = media is not null,
            MediaPath = media?.Path,
            MediaWidth = width,
            MediaHeight = height,
            Seconds = media?.Seconds ?? 0,
            Waveform = media?.Waveform ?? [],
            ReplyId = dto.Reply?.Id ?? "",
            ReplyName = dto.Reply?.SenderName ?? "",
            ReplyPreview = dto.Reply?.Preview ?? "",
            ReplyGlyph = PreviewGlyph(dto.Reply?.Kind ?? ""),
            ReplyFromMe = dto.Reply?.FromMe ?? false,
            Reactions = dto.Reactions ?? [],
            MyReaction = dto.MyReaction ?? "",
        };

        switch (dto.Kind)
        {
            case "image" when media is not null:
                var (w, h) = Fit(media.Width, media.Height, maxWidth: 300, maxHeight: 360);
                return Make(MessageKind.Image, dto.Text, width: w, height: h);
            case "sticker" when media is not null:
                return Make(MessageKind.Sticker, width: 150, height: 150);
            case "voice" or "audio" when media is not null:
                return Make(MessageKind.Voice);
            case "document":
                var name = string.IsNullOrEmpty(dto.FileName) ? "Document" : dto.FileName;
                var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
                return Make(MessageKind.File, dto.Text, name, ext.Length > 0 ? $"{ext} document" : "Document");
        }

        var label = dto.Kind switch
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
        return Make(MessageKind.Text, label);
    }

    /// <summary>Scales a picture into the bubble keeping its proportions (unknown size: 300x200).</summary>
    private static (double Width, double Height) Fit(int width, int height, double maxWidth, double maxHeight)
    {
        if (width <= 0 || height <= 0) return (maxWidth, 200);
        var scale = Math.Min(maxWidth / width, maxHeight / height);
        return (Math.Max(160, Math.Round(width * scale)), Math.Max(100, Math.Round(height * scale)));
    }

    private static string Label(string emoji, string name, string caption) =>
        caption.Length > 0 ? $"{emoji} {caption}" : $"{emoji} {name}";

    /// <summary>"0:07", "1:23", "1:02:03".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}