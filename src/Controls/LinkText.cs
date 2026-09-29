using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative.Controls;

/// <summary>
/// Message text with its email addresses and web links underlined in WhatsApp green.
/// Click one to open it (mailto: goes to your mail app); right-click one for its own menu
/// (see <see cref="HitTest"/>). The invisible time spacer run stays last.
/// </summary>
public static partial class LinkText
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(Message), typeof(LinkText), new PropertyMetadata(null, (d, _) => Build((TextBlock)d)));

    public static Message? GetMessage(TextBlock element) => (Message?)element.GetValue(MessageProperty);
    public static void SetMessage(TextBlock element, Message? value) => element.SetValue(MessageProperty, value);

    [GeneratedRegex(@"(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})|(?<url>(?:https?://|www\.)[^\s<>""]*[^\s<>"".,;:!?)\]'])")]
    private static partial Regex Links();

    /// <summary>What a link points at: "mailto:…" or a web address, and the text shown.</summary>
    public sealed record Target(string Uri, string Text, bool IsEmail);

    private static readonly ConditionalWeakTable<Hyperlink, Target> Targets = new();
    private static readonly ConditionalWeakTable<TextBlock, object> Hooked = new();

    private static void Build(TextBlock block)
    {
        block.Inlines.Clear();
        if (GetMessage(block) is not { } m) return;

        // Links pick up the theme's green; rebuild when the theme flips.
        if (!Hooked.TryGetValue(block, out _))
        {
            Hooked.Add(block, new object());
            block.ActualThemeChanged += (s, _) => Build((TextBlock)s);
        }

        var text = m.Text;
        var at = 0;
        if (!m.IsDeleted)
        {
            foreach (Match match in Links().Matches(text))
            {
                if (match.Index > at) block.Inlines.Add(new Run { Text = text[at..match.Index] });
                var email = match.Groups["email"].Success;
                var uri = email ? "mailto:" + match.Value : match.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + match.Value : match.Value;
                var link = new Hyperlink
                {
                    Foreground = Themed.Brush("ChatAccentTextBrush"),
                    UnderlineStyle = UnderlineStyle.Single,
                };
                link.Inlines.Add(new Run { Text = match.Value });
                if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) link.NavigateUri = parsed;
                Targets.Add(link, new Target(uri, match.Value, email));
                block.Inlines.Add(link);
                at = match.Index + match.Length;
            }
        }
        if (at < text.Length) block.Inlines.Add(new Run { Text = text[at..] });
        block.Inlines.Add(new Run { Text = m.TimeSpacer, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent) });
    }

    /// <summary>The link under <paramref name="point"/> (relative to the TextBlock), if any.</summary>
    public static Target? HitTest(TextBlock block, Point point)
    {
        foreach (var link in block.Inlines.OfType<Hyperlink>())
        {
            if (!Targets.TryGetValue(link, out var target)) continue;
            var length = link.ContentStart.Offset <= link.ContentEnd.Offset ? link.ContentEnd.Offset - link.ContentStart.Offset : 0;
            for (var i = 0; i < length; i++)
            {
                var rect = link.ContentStart.GetPositionAtOffset(i, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                // Character rects are hairline-wide at their start; use the gap to the next one.
                var next = link.ContentStart.GetPositionAtOffset(i + 1, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                var right = next.Y == rect.Y && next.X > rect.X ? next.X : rect.X + Math.Max(rect.Width, 8);
                if (point.X >= rect.X && point.X <= right && point.Y >= rect.Y - 2 && point.Y <= rect.Y + rect.Height + 2)
                    return target;
            }
        }
        return null;
    }
}
