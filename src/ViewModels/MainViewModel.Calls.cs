using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

/// <summary>Calls: the window (CallWindow) talks to WhatsApp through these.</summary>
public sealed partial class MainViewModel
{
    /// <summary>A call rang, connected or ended.</summary>
    public event Action<CallDto>? CallChanged;

    /// <summary>The other side's voice: 16-bit samples, or an Opus packet. Not on the UI thread.</summary>
    public event Action<byte[], bool>? CallAudio;

    /// <summary>The other side's picture: an H.264 access unit, whether a decoder can start at it, their camera's quarter turns. Not on the UI thread.</summary>
    public event Action<byte[], bool, int>? CallVideo;

    /// <summary>Video in the call: request | on | off | declined | ended | failed | keyframe.</summary>
    public event Action<string>? CallVideoState;

    public Chat? ChatById(string chatId) => _byId.GetValueOrDefault(chatId);

    // ───── The Calls page ─────

    /// <summary>The call history, newest first.</summary>
    public IReadOnlyList<CallLogDto> CallLog { get; private set; } = [];

    /// <summary><see cref="CallLog"/> was loaded or changed.</summary>
    public event Action? CallsChanged;

    /// <summary>The call link that was asked for: the link, and whether it's for video.</summary>
    public event Action<string, bool>? CallLinkReceived;

    private int _missedCalls;
    private long _callsSeenAt;

    /// <summary>Calls you missed since you last looked at the Calls page (the number on the rail's phone).</summary>
    public int MissedCalls { get => _missedCalls; private set => Set(ref _missedCalls, value); }

    /// <summary>When the Calls page was last looked at (the window keeps it in ui.json).</summary>
    public void UseCallsSeen(long unixSeconds)
    {
        _callsSeenAt = unixSeconds;
        CountMissedCalls();
    }

    /// <summary>The Calls page is being looked at: nothing is new any more. Returns the time to remember.</summary>
    public long MarkCallsSeen()
    {
        _callsSeenAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        MissedCalls = 0;
        return _callsSeenAt;
    }

    private void CountMissedCalls() => MissedCalls = CallLog.Count(c => CallRow.Missed(c) && c.Ts > _callsSeenAt);

    public void LoadCalls()
    {
        if (_core is not null)
        {
            _core.LoadCalls();
            return;
        }
        if (CallLog.Count == 0) CallLog = SampleCalls();
        CountMissedCalls();
        CallsChanged?.Invoke();
    }

    public void DeleteCalls(IEnumerable<CallLogDto> calls)
    {
        var ids = calls.Select(c => c.Id).ToHashSet();
        if (_core is not null)
        {
            foreach (var id in ids) _core.DeleteCall(id);
            return;
        }
        CallLog = CallLog.Where(c => !ids.Contains(c.Id)).ToList();
        CallsChanged?.Invoke();
    }

    public void CreateCallLink(bool video) => _core?.CreateCallLink(video);

    /// <summary>The chat a call in the history was with, when there is one.</summary>
    public Chat? ChatFor(CallLogDto call) =>
        call.ChatId.Length > 0 ? _byId.GetValueOrDefault(call.ChatId) : _core is null ? _allChats.FirstOrDefault(c => c.Name == call.Name) : null;

    /// <summary>Your favourites (the same list as the Favourites filter on chats), by name.</summary>
    public List<Chat> FavouriteChats() => _allChats.Where(c => c.IsFavourite).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Calls for the sample data: a few of its people, a few kinds of call.</summary>
    private List<CallLogDto> SampleCalls()
    {
        var people = _allChats.Where(c => !c.IsGroup).Take(6).ToList();
        var now = DateTimeOffset.Now;
        (int Person, double HoursAgo, bool Incoming, bool Video, string Result, int Seconds)[] made =
        [
            (0, 0.4, true, true, "missed", 0), (1, 1.1, false, true, "connected", 754), (2, 2.6, false, false, "connected", 96),
            (3, 4.2, false, false, "cancelled", 0), (3, 4.3, false, false, "connected", 310), (4, 26, false, false, "connected", 45),
            (4, 26.5, false, false, "connected", 1260), (5, 27, true, false, "connected", 520), (0, 50, true, false, "rejected", 0),
        ];
        return made.Where(m => m.Person < people.Count)
            .Select((m, i) => new CallLogDto($"sample-{i}", now.AddHours(-m.HoursAgo).ToUnixTimeSeconds(), m.Seconds, m.Incoming, m.Video, m.Result,
                                            "", people[m.Person].Name, ""))
            .ToList();
    }

    public void StartCall(Chat chat, bool video) => _core?.StartCall(chat.Id, video);

    public void AcceptCall(string callId, bool video) => _core?.AcceptCall(callId, video);

    public void RejectCall(string callId) => _core?.RejectCall(callId);

    public void EndCall() => _core?.EndCall();

    public void MuteCall(bool muted) => _core?.MuteCall(muted);

    public void SendCallAudio(byte[] pcm) => _core?.SendCallAudio(pcm);

    public void SetCallVideo(bool on) => _core?.SetCallVideo(on);

    public void SendCallVideo(byte[] unit) => _core?.SendCallVideo(unit);
}
