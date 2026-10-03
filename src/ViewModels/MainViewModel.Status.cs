using System.Text.Json;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

/// <summary>Status updates: what the Status page (MainWindow.Status.cs) lists and shows.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The id status updates are kept under in the core (their pictures download with it).</summary>
    public const string StatusChat = "status@broadcast";

    /// <summary>Status updates of the last 24 hours, oldest first.</summary>
    public IReadOnlyList<StatusDto> Statuses { get; private set; } = [];

    /// <summary><see cref="Statuses"/> was loaded or changed.</summary>
    public event Action? StatusesChanged;

    /// <summary>A status update's picture or video arrived (its id, the file), or couldn't be had (null).</summary>
    public event Action<string, string?>? StatusMedia;

    private bool _hasNewStatus;

    /// <summary>Someone posted a status you haven't looked at (the dot on the rail's Status).</summary>
    public bool HasNewStatus { get => _hasNewStatus; private set => Set(ref _hasNewStatus, value); }

    private void WireStatuses(CoreClient core)
    {
        core.StatusesReceived += statuses => SetStatuses(statuses);
        core.MediaReceived += (chatId, id, path) =>
        {
            if (chatId != StatusChat) return;
            // Kept on the update too, so it isn't asked for again.
            Statuses = Statuses.Select(s => s.Message.Id == id && s.Message.Media is { } media
                ? s with { Message = s.Message with { Media = media with { Path = path } } }
                : s).ToList();
            StatusMedia?.Invoke(id, path);
        };
        core.MediaFailed += (chatId, id, _) => { if (chatId == StatusChat) StatusMedia?.Invoke(id, null); };
    }

    private void SetStatuses(IReadOnlyList<StatusDto> statuses)
    {
        Statuses = statuses;
        HasNewStatus = statuses.Any(s => !s.Seen && s.Author.Length > 0);
        StatusesChanged?.Invoke();
    }

    public void LoadStatuses()
    {
        if (_core is not null)
        {
            _core.LoadStatuses();
            return;
        }
        if (Statuses.Count == 0) SetStatuses(SampleStatuses());
        else StatusesChanged?.Invoke();
    }

    /// <summary>You're looking at this one: it counts as seen, and its author is told.</summary>
    public void StatusSeen(StatusDto status)
    {
        if (status.Seen) return;
        _core?.StatusSeen([status.Message.Id]);
        SetStatuses(Statuses.Select(s => s.Message.Id == status.Message.Id ? s with { Seen = true } : s).ToList());
    }

    /// <summary>Asks for a status update's picture or video; answered by <see cref="StatusMedia"/>.</summary>
    public void DownloadStatus(string id) => _core?.DownloadMedia(StatusChat, id);

    /// <summary>Replies to someone's status: a message in your chat with them that quotes it.</summary>
    public void ReplyStatus(StatusDto status, string text)
    {
        if (_core is null) Toast?.Invoke(true, "Reply sent");
        else _core.ReplyStatus(status.Message.Id, text);
    }

    /// <summary>Status updates for the sample data: a few of its people, text cards and a picture.</summary>
    private List<StatusDto> SampleStatuses()
    {
        var people = _allChats.Where(c => !c.IsGroup).Take(4).ToList();
        var now = DateTimeOffset.Now;
        var picture = Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpaper.Dark.png");
        (int Person, double HoursAgo, string Kind, string Text, uint Colour, bool Seen)[] made =
        [
            (0, 5.5, "text", "Good morning everyone", 0xFF1E6E4F, false), (0, 2.2, "image", "On the way", 0, false),
            (1, 13, "text", "Some days you just need tea and a quiet room.", 0xFF8B5CF6, false),
            (2, 15, "text", "Eid Mubarak!", 0xFFC2410C, true), (2, 14.5, "image", "", 0, true),
            (-1, 3, "text", "Working on something new", 0xFF2563EB, true),
        ];
        var list = new List<StatusDto>();
        foreach (var (m, i) in made.Select((m, i) => (m, i)))
        {
            if (m.Person >= people.Count) continue;
            var mine = m.Person < 0;
            var media = m.Kind == "image" ? new MediaDto("image/png", 1080, 1920, 0, null, picture) : null;
            JsonElement? extra = m.Kind == "text" ? JsonDocument.Parse($"{{\"status\":{{\"background\":{m.Colour},\"font\":0}}}}").RootElement.Clone() : null;
            var message = new MessageDto($"sample-status-{i}", mine, "", mine ? "" : people[m.Person].Name, now.AddHours(-m.HoursAgo).ToUnixTimeSeconds(),
                                         m.Kind, m.Text, null, 0, media, null, null, null, false, false, null, extra);
            list.Add(new StatusDto(mine ? "" : $"sample:{people[m.Person].Name}", mine ? "My status" : people[m.Person].Name,
                                   mine ? SelfAvatarPath : people[m.Person].AvatarPath, m.Seen, mine ? 7 : 0, message));
        }
        return list.OrderBy(s => s.Message.Ts).ToList();
    }
}
