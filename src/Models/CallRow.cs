using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative.Models;

/// <summary>
/// One row of the Calls page's Recent list: a call, or several in a row with the same person
/// of the same kind on the same day ("Outgoing (2)"), newest first.
/// </summary>
public sealed class CallRow
{
    public required IReadOnlyList<CallLogDto> Calls { get; init; }

    /// <summary>The chat with that person (or group), when there is one.</summary>
    public Chat? Chat { get; init; }

    private CallLogDto First => Calls[0];

    public string ChatId => First.ChatId;
    public string Name => Chat?.Name is { Length: > 0 } name ? name : First.Name;
    public string? AvatarPath => Chat?.AvatarPath ?? First.Avatar;
    public string Phone => First.Phone;
    public bool IsGroup => First.Group;
    public bool IsVideo => First.Video;
    public bool IsMissed => Missed(First);

    /// <summary>A camera for video calls, a receiver for voice.</summary>
    public string Glyph => First.Video ? Glyphs.VideoCall : Glyphs.Phone;

    public string Label => Kind(First) + (Calls.Count > 1 ? $" ({Calls.Count})" : "");

    public string Time => Format.ListTime(When(First));

    public static DateTime When(CallLogDto call) => DateTimeOffset.FromUnixTimeSeconds(call.Ts).LocalDateTime;

    /// <summary>A call to you that nobody picked up.</summary>
    public static bool Missed(CallLogDto call) => call.Incoming && call.Result is "missed" or "cancelled" or "failed";

    /// <summary>Missed, Declined, Incoming or Outgoing.</summary>
    public static string Kind(CallLogDto call) =>
        Missed(call) ? "Missed" : !call.Incoming ? "Outgoing" : call.Result == "rejected" ? "Declined" : "Incoming";

    /// <summary>What two calls must share to be one row.</summary>
    public static (string, string, bool, DateTime) Key(CallLogDto call) =>
        (call.ChatId.Length > 0 ? call.ChatId : call.Name, Kind(call), call.Video, When(call).Date);
}

/// <summary>Who the Calls page's right side is about: a person (or group) and the calls with them.</summary>
public sealed record CallTarget(string ChatId, string Name, string Phone, string? Avatar, bool IsGroup, Chat? Chat);
