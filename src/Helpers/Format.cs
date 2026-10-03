using System.Globalization;
using System.Text.Json;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.Helpers;

/// <summary>Turns core data into what the UI shows, WhatsApp style.</summary>
public static class Format
{
    /// <summary>"820 KB", "2 MB", "1.4 GB", like WhatsApp's file sizes.</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    public static DateTime FromUnix(long seconds) =>
        seconds <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    /// <summary>A disappearing-messages timer in words: "Off", "24 hours", "7 days", "90 days".</summary>
    public static string EphemeralText(int seconds) => seconds switch
    {
        <= 0 => "Off",
        86_400 => "24 hours",
        604_800 => "7 days",
        7_776_000 => "90 days",
        var s when s % 86_400 == 0 => $"{s / 86_400} days",
        var s when s >= 3_600 => $"{s / 3_600} hours",
        var s => $"{Math.Max(1, s / 60)} minutes",
    };

    /// <summary>Settings › Use 24-hour time: "18:27"; off: "6:27 pm".</summary>
    public static bool Use24Hour { get; set; } = true;

    /// <summary>A time of day in the chosen clock.</summary>
    public static string Clock(DateTime local) =>
        Use24Hour ? local.ToString("H:mm", CultureInfo.InvariantCulture) : local.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>A date the way the whole app writes one: day/month/year, "24/09/2026".</summary>
    public static string Date(DateTime local) => local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Chat list: "14:05", "Yesterday", "Monday", then the date.</summary>
    public static string ListTime(DateTime local)
    {
        if (local == DateTime.MinValue) return "";
        var today = DateTime.Today;
        if (local.Date == today) return Clock(local);
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return Date(local);
    }

    /// <summary>"last seen today at 14:54", "last seen yesterday at 9:02", "last seen 12/09/2026 at 18:30".</summary>
    public static string LastSeen(DateTime local)
    {
        if (local == DateTime.MinValue) return "";
        var day = local.Date == DateTime.Today ? "today"
                : local.Date == DateTime.Today.AddDays(-1) ? "yesterday"
                : Date(local);
        return $"last seen {day} at {Clock(local)}";
    }

    /// <summary>Divider between days in a conversation.</summary>
    public static string DayLabel(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return "Today";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return Date(local);
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
        "call" => Glyphs.Phone,
        _ => "",
    };

    /// <summary>One-line summary of a message for a quote ("Photo", the text, the file name...).</summary>
    /// <summary>Text with mentions as plain "@Name" (the markers and who they point at dropped).</summary>
    public static string PlainMentions(string text)
    {
        if (text.IndexOf('⁨') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var hidden = false;
        foreach (var c in text)
        {
            if (c == '⁨') continue;
            if (c == '⁣') { hidden = true; continue; }
            if (c == '⁩') { hidden = false; continue; }
            if (!hidden) sb.Append(c);
        }
        return sb.ToString();
    }

    public static string QuotePreview(Message m) => PlainMentions(QuotePreviewRaw(m));

    private static string QuotePreviewRaw(Message m) => m.Kind switch
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

    /// <summary>How long a call lasted, in words: "21 seconds", "1 minute", "12 minutes", "1 hour 5 minutes".</summary>
    public static string CallLength(long seconds)
    {
        static string Some(long n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
        if (seconds < 60) return Some(Math.Max(seconds, 1), "second");
        var minutes = (seconds + 30) / 60;
        return minutes < 60 ? Some(minutes, "minute") : minutes % 60 == 0 ? Some(minutes / 60, "hour") : $"{Some(minutes / 60, "hour")} {Some(minutes % 60, "minute")}";
    }

    /// <summary>A call card's details from the core's {call: {video, result, duration}}: video, missed, the line under the heading.</summary>
    private static (bool Video, bool Missed, string Detail) CallCard(MessageDto dto)
    {
        if (dto.Kind != "call") return (false, false, "");
        var (video, result, duration) = (dto.Text.Contains("ideo"), dto.Text.StartsWith("Missed") ? "missed" : "connected", 0L);
        if (dto.Extra is { ValueKind: JsonValueKind.Object } extra && extra.TryGetProperty("call", out var call))
        {
            if (call.TryGetProperty("video", out var v)) video = v.ValueKind == JsonValueKind.True;
            if (call.TryGetProperty("result", out var r)) result = r.GetString() ?? result;
            if (call.TryGetProperty("duration", out var d) && d.TryGetInt64(out var s)) duration = s;
        }
        var detail = result switch
        {
            "connected" when duration > 0 => CallLength(duration),
            "connected" => "",
            "missed" => "Not answered",
            "rejected" => "Declined",
            "elsewhere" => "Answered on another device",
            "failed" => "Couldn't connect",
            _ => "No answer",
        };
        return (video, result == "missed", detail);
    }

    /// <summary>"430", "1.2k", "219k", "234.2m": counts the way WhatsApp shortens them.</summary>
    public static string Compact(long n) => n switch
    {
        < 1000 => n.ToString(CultureInfo.InvariantCulture),
        < 100_000 => (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        < 1_000_000 => (n / 1000).ToString(CultureInfo.InvariantCulture) + "k",
        _ => (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "m",
    };

    /// <summary>A channel post's reactions from the core's {channel: {reactions: [[emoji, count]…]}}: the four most used and the total.</summary>
    private static (string Summary, List<(string Emoji, long Count)> All, string Forwards, string Mine) ChannelCounts(MessageDto dto)
    {
        if (dto.Extra is not { ValueKind: JsonValueKind.Object } extra || !extra.TryGetProperty("channel", out var channel)) return ("", [], "", "");
        var mine = channel.TryGetProperty("mine", out var own) ? own.GetString() ?? "" : "";
        var forwards = channel.TryGetProperty("forwards", out var f) && f.TryGetInt64(out var times) && times > 0 ? Compact(times) : "";
        if (!channel.TryGetProperty("reactions", out var list) || list.ValueKind != JsonValueKind.Array) return ("", [], forwards, mine);
        var counts = list.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 2)
            .Select(r => (Emoji: r[0].GetString() ?? "", Count: r[1].TryGetInt64(out var n) ? n : 0)).Where(r => r.Count > 0)
            // The same emoji arrives written two ways (with and without its "as a picture" mark): one entry, counted together.
            .GroupBy(r => r.Emoji.Replace("\uFE0F", ""))
            .Select(g => (Emoji: g.OrderByDescending(r => r.Count).First().Emoji, Count: g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count).ToList();
        return (counts.Count == 0 ? "" : $"{string.Concat(counts.Take(4).Select(r => r.Emoji))} {Compact(counts.Sum(r => r.Count))}", counts, forwards, mine);
    }

    private static Message Build(MessageDto dto, bool isGroup)
    {
        var when = FromUnix(dto.Ts);
        var media = dto.Media;
        var callCard = CallCard(dto);
        var channel = ChannelCounts(dto);

        Message Make(MessageKind kind, string text = "", string fileName = "", string fileDetails = "",
                     double width = 300, double height = 200) => new()
        {
            Id = dto.Id,
            Kind = kind,
            IsOutgoing = dto.FromMe,
            Timestamp = when,
            UnixTs = dto.Ts,
            Time = Clock(when),
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
            ReplyThumb = dto.Reply?.Thumb,
            Reactions = dto.Reactions ?? [],
            MyReaction = dto.MyReaction ?? channel.Mine,
            Starred = dto.Starred,
            Edited = dto.Edited,
            Forwarded = dto.Forwarded,
            IsDeleted = dto.Kind == "deleted",
            Thumb = dto.Thumb,
            IsVoiceNote = dto.Kind != "audio",
            IsGif = dto.Kind == "gif",
            ReactionSummary = channel.Summary,
            PostReactions = channel.All,
            ForwardCount = channel.Forwards,
            IsPost = dto.Extra is { ValueKind: JsonValueKind.Object } post && post.TryGetProperty("channel", out _),
            CallVideo = callCard.Video,
            CallMissed = callCard.Missed,
            CallDetail = callCard.Detail,
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
            case "call":
                return Make(MessageKind.Call, dto.Text);
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