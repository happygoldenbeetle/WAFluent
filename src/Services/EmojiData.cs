using System.Text.Json;

namespace WhatsAppNative.Services;

/// <summary>One emoji: keyboard tab, name, shortcodes (sob, joy, +1...), search words, skin tones.</summary>
public sealed record EmojiEntry(string Emoji, int Tab, string Name, string[] Codes, string Words, string[] Skins, int Order)
{
    public bool HasSkins => Skins.Length > 0;
}

/// <summary>
/// Assets/emoji.json (built by tools/make-emoji-data.py from emojibase, MIT): every emoji the
/// bundled font can draw, in keyboard order. Loaded once, on first use.
/// </summary>
public static class EmojiData
{
    public static readonly string[] TabNames =
        ["Smileys & people", "Animals & nature", "Food & drink", "Activity", "Travel & places", "Objects", "Symbols", "Flags"];

    private static readonly Lazy<List<EmojiEntry>> _all = new(Load);
    private static readonly Lazy<Dictionary<string, EmojiEntry>> _byEmoji = new(() =>
    {
        var map = new Dictionary<string, EmojiEntry>();
        foreach (var e in All)
        {
            map.TryAdd(e.Emoji, e);
            foreach (var skin in e.Skins) map.TryAdd(skin, e);
        }
        return map;
    });

    public static IReadOnlyList<EmojiEntry> All => _all.Value;

    /// <summary>Starts loading in the background so the first keyboard opens instantly.</summary>
    public static void Warm() => Task.Run(() => _ = _byEmoji.Value);

    public static IEnumerable<EmojiEntry> InTab(int tab) => All.Where(e => e.Tab == tab);

    /// <summary>The entry an emoji (or one of its skin tones) belongs to.</summary>
    public static EmojiEntry? Find(string emoji) => _byEmoji.Value.GetValueOrDefault(emoji);

    /// <summary>Search by name, shortcode and keywords; best matches first.</summary>
    public static List<EmojiEntry> Search(string query, int max = 240)
    {
        var q = query.Trim().ToLowerInvariant().Trim(':');
        if (q.Length == 0) return [];
        return All
            .Select(e => (e, rank: Rank(e, q)))
            .Where(x => x.rank < 9)
            .OrderBy(x => x.rank).ThenBy(x => x.e.Order)
            .Take(max).Select(x => x.e).ToList();

        static int Rank(EmojiEntry e, string q)
        {
            if (e.Codes.Any(c => c == q)) return 0;
            if (e.Name.StartsWith(q, StringComparison.Ordinal)) return 1;
            if (e.Codes.Any(c => c.StartsWith(q, StringComparison.Ordinal))) return 2;
            if (e.Words.Split(' ').Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 3;
            if (e.Words.Contains(q, StringComparison.Ordinal)) return 4;
            return 9;
        }
    }

    /// <summary>":so" autocomplete: shortcodes starting with <paramref name="prefix"/>, exact and short ones first.</summary>
    public static List<(EmojiEntry Entry, string Code)> Shortcodes(string prefix, int max = 6)
    {
        var p = prefix.ToLowerInvariant();
        return All
            .Select(e => (e, code: e.Codes.Where(c => c.StartsWith(p, StringComparison.Ordinal)).OrderBy(c => c.Length).FirstOrDefault()))
            .Where(x => x.code is not null)
            .OrderBy(x => x.code == p ? 0 : 1).ThenBy(x => x.e.Codes[0].StartsWith(p, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.code!.Length).ThenBy(x => x.e.Order)
            .Take(max).Select(x => (x.e, x.code!)).ToList();
    }

    /// <summary>The emoji for a complete shortcode (":sob:" typed out), if there is one.</summary>
    public static EmojiEntry? ByCode(string code)
    {
        var c = code.ToLowerInvariant();
        return All.FirstOrDefault(e => e.Codes.Contains(c));
    }

    private static List<EmojiEntry> Load()
    {
        var list = new List<EmojiEntry>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "emoji.json")));
            var order = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                list.Add(new EmojiEntry(
                    e[0].GetString()!, e[1].GetInt32(), e[2].GetString()!,
                    e[3].EnumerateArray().Select(c => c.GetString()!).ToArray(),
                    e[4].GetString()!,
                    e[5].EnumerateArray().Select(c => c.GetString()!).ToArray(),
                    order++));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return list;
    }
}
