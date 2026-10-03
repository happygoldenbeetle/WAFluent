using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative.Models;

/// <summary>One person's status updates of the last 24 hours (a row on the Status page).</summary>
public sealed class StatusPerson
{
    /// <summary>Their chat's id ("" for you).</summary>
    public required string Author { get; init; }
    public required string Name { get; init; }
    public string? AvatarPath { get; init; }
    /// <summary>Oldest first: the order they play in.</summary>
    public required List<StatusDto> Items { get; init; }

    public bool Mine => Author.Length == 0;
    public bool AllSeen => Items.All(s => s.Seen);
    public long LastTs => Items[^1].Message.Ts;

    /// <summary>Where watching starts: the first one not seen yet (the first, when all are).</summary>
    public int FirstUnseen => Math.Max(0, Items.FindIndex(s => !s.Seen));

    /// <summary>"Today at 7:21 am", "Yesterday at 23:40".</summary>
    public static string When(long ts)
    {
        var local = Format.FromUnix(ts);
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : Format.Date(local);
        return $"{day} at {Format.Clock(local)}";
    }

    /// <summary>The updates grouped by person, each person's oldest first.</summary>
    public static List<StatusPerson> Group(IEnumerable<StatusDto> statuses) =>
        statuses.GroupBy(s => s.Author)
            .Select(g => new StatusPerson
            {
                Author = g.Key,
                Name = g.Last().Name,
                AvatarPath = g.Last().Avatar,
                Items = g.OrderBy(s => s.Message.Ts).ToList(),
            })
            .ToList();
}
