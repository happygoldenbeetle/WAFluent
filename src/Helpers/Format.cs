using System.Globalization;
using System.Text.Json;
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
        "image" or "viewonce" => Glyphs.Photo,
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
        MessageKind.Voice => m.IsVoiceNote ? $"Voice message ({Duration(TimeSpan.FromSeconds(m.Seconds))})" : "Audio",
        MessageKind.File => m.FileName,
        MessageKind.Video => m.HasText ? m.Text : m.IsGif ? "GIF" : m.IsVideoNote ? "Video message" : "Video",
        MessageKind.Location => m.PlaceTitle,
        MessageKind.Contact => m.ContactTitle,
        MessageKind.Poll => m.Text,
        _ => m.Text,
    };

    public static string QuoteGlyph(Message m) => m.Kind switch
    {
        MessageKind.Image => Glyphs.Photo,
        MessageKind.Voice => Glyphs.Mic,
        MessageKind.File => Glyphs.Document,
        MessageKind.Video => Glyphs.Video,
        MessageKind.Location => Glyphs.Location,
        MessageKind.Contact => Glyphs.Contact,
        MessageKind.Poll => Glyphs.Poll,
        _ => "",
    };

    public static Delivery ToDelivery(int status) => status switch
    {
        3 => Delivery.Read,
        2 => Delivery.Delivered,
        _ => Delivery.Sent,
    };

    /// <summary>
    /// Every kind gets its own bubble: pictures, stickers, voice notes and audio, videos and
    /// GIFs, documents, locations (map snapshot), contact cards, polls, link previews and
    /// WhatsApp's notices. Media sent before this PC stored download details shows as a
    /// labelled line until the phone fills them in.
    /// </summary>
    public static Message ToMessage(MessageDto dto, bool isGroup)
    {
        var message = Build(dto, isGroup);
        if (dto.Extra is { ValueKind: JsonValueKind.Object } extra) AddDetails(message, extra);
        return message;
    }

    /// <summary>What <c>extra</c> carries per kind (core/src/protocol.rs, MessageDto).</summary>
    private static void AddDetails(Message m, JsonElement x)
    {
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        switch (m.Kind)
        {
            case MessageKind.Location:
                m.Latitude = Num(x, "lat");
                m.Longitude = Num(x, "lng");
                m.PlaceName = Str(x, "name");
                m.PlaceAddress = Str(x, "address");
                m.IsLiveLocation = Bool(x, "live");
                break;
            case MessageKind.Contact when x.TryGetProperty("contacts", out var cards) && cards.ValueKind == JsonValueKind.Array:
                m.Contacts = cards.EnumerateArray()
                    .Select(c => new ContactCard(Str(c, "name"),
                        c.TryGetProperty("phones", out var ph) && ph.ValueKind == JsonValueKind.Array
                            ? ph.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList()
                            : []))
                    .ToList();
                break;
            case MessageKind.Poll when x.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array:
                m.PollMulti = Bool(x, "multi");
                var voters = new Dictionary<string, List<string>>();
                if (x.TryGetProperty("votes", out var votes) && votes.ValueKind == JsonValueKind.Array)
                    foreach (var v in votes.EnumerateArray())
                        voters[Str(v, "name")] = v.TryGetProperty("voters", out var who) && who.ValueKind == JsonValueKind.Array
                            ? who.EnumerateArray().Select(w => w.GetString() ?? "").ToList()
                            : [];
                var mine = x.TryGetProperty("mine", out var my) && my.ValueKind == JsonValueKind.Array
                    ? my.EnumerateArray().Select(o => o.GetString() ?? "").ToHashSet()
                    : [];
                m.PollOptions = PollOptions(m, options.EnumerateArray().Select(o => o.GetString() ?? "").ToList(), voters, mine);
                break;
            case MessageKind.Text when x.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object:
                m.LinkUrl = Str(link, "url");
                m.LinkTitle = Str(link, "title");
                m.LinkDescription = Str(link, "description");
                break;
            case MessageKind.Video:
                m.IsVideoNote = Bool(x, "note");
                break;
            case MessageKind.File:
                m.Pages = (int)Num(x, "pages");
                break;
        }
    }

    /// <summary>Options with their voters; bars are each option's share of everyone who voted.</summary>
    public static List<PollOption> PollOptions(Message owner, IReadOnlyList<string> names,
                                               IReadOnlyDictionary<string, List<string>> voters, IReadOnlySet<string> mine)
    {
        var total = voters.Values.SelectMany(v => v).Distinct().Count();
        return names.Select(name =>
        {
            var who = voters.TryGetValue(name, out var list) ? list : [];
            return new PollOption
            {
                Owner = owner,
                Name = name,
                Voters = who,
                Selected = mine.Contains(name),
                Fraction = total > 0 ? (double)who.Count / total : 0,
            };
        }).ToList();
    }

    private static Message Build(MessageDto dto, bool isGroup)
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
            Starred = dto.Starred,
            Edited = dto.Edited,
            IsDeleted = dto.Kind == "deleted",
            Thumb = dto.Thumb,
            IsVoiceNote = dto.Kind != "audio",
            IsGif = dto.Kind == "gif",
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
                return Make(MessageKind.File, dto.Text, name, ext.Length > 0 ? ext : "Document");
            case "video" or "gif" when media is not null || dto.Thumb is not null:
                var note = dto.Extra is { ValueKind: JsonValueKind.Object } x && x.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.True;
                var (vw, vh) = note ? (240, 240) : Fit(media?.Width ?? 0, media?.Height ?? 0, maxWidth: 300, maxHeight: 360);
                return Make(MessageKind.Video, dto.Text, width: vw, height: vh);
            case "location" when dto.Extra is not null:
                return Make(MessageKind.Location, dto.Text, width: 300, height: 150);
            case "contact" when dto.Extra is not null:
                return Make(MessageKind.Contact, dto.Text);
            case "poll" when dto.Extra is not null:
                return Make(MessageKind.Poll, dto.Text);
            case "system":
                return Make(MessageKind.System, dto.Text);
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
            "viewonce" => $"📷 View once {dto.Text}. Open WhatsApp on your phone to see it.",
            "deleted" => dto.FromMe ? "You deleted this message" : "This message was deleted",
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