using System.Globalization;
using System.Text;

namespace WhatsAppNative.Helpers;

/// <summary>
/// Messages that are only emoji (and spaces) show big and without a bubble, like WhatsApp,
/// Instagram and Discord do. Counts whole emoji: skin tones, ZWJ families, flags and keycaps
/// are one each.
/// </summary>
public static class EmojiText
{
    /// <summary>Up to this many emoji go big; longer runs stay in a normal bubble.</summary>
    public const int MaxJumbo = 10;

    /// <summary>How many emoji the text is, or 0 when it has anything else in it.</summary>
    public static int JumboCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 160) return 0;
        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (!IsEmoji(element)) return 0;
            if (++count > MaxJumbo) return 0;
        }
        return count;
    }

    /// <summary>Big but not huge: one emoji is largest, a handful a bit smaller.</summary>
    public static double Size(int count) => count switch
    {
        1 => 56,
        2 => 48,
        3 => 42,
        <= 6 => 36,
        _ => 30,
    };

    private static bool IsEmoji(string element)
    {
        if (element.Contains('⃣')) return true;   // keycap 1️⃣ #️⃣
        var c = Rune.GetRuneAt(element, 0).Value;
        var presentation = element.Contains('️');
        return c switch
        {
            >= 0x1F000 and <= 0x1FAFF => true,          // pictographs, faces, flags, transport, food...
            >= 0x2600 and <= 0x27BF => true,            // ☀ ☕ ✂ ✅ ❤ ...
            >= 0x2300 and <= 0x23FF => true,            // ⌚ ⏰ ⏳
            >= 0x2B00 and <= 0x2BFF => true,            // ⬆ ⭐ ⭕
            0x3030 or 0x303D or 0x3297 or 0x3299 => true,
            // Text symbols that are emoji only with the emoji selector: © ® ‼ ⁉ ™ ℹ ↔ ▶ ◀ ⤴
            0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x2934 or 0x2935 => presentation,
            >= 0x2190 and <= 0x21FF => presentation,
            >= 0x25A0 and <= 0x25FF => presentation,
            _ => false,
        };
    }
}
