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

    /// <summary>A mention was clicked: their chat id and number (the window opens or starts the chat).</summary>
    public static Action<string, string>? MentionClicked;

    [GeneratedRegex(@"(?<mention>@\u2068[^\u2069]*\u2069)|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})|(?<url>(?:https?://|www\.)[^\s<>""]*[^\s<>"".,;:!?)\]'])")]
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
                at = match.Index + match.Length;
                if (match.Groups["mention"].Success)
                {
                    // @Name (marked with Unicode isolates, then who it is): green, like WhatsApp; a click opens their chat.
                    var parts = match.Value[2..^1].Split('\u2063');
                    var run = new Run { Text = "@" + parts[0] };
                    if (parts.Length >= 3)
                    {
                        var (mentionChat, phone) = (parts[1], parts[2]);
                        var mention = new Hyperlink
                        {
                            Foreground = Themed.Brush("ChatAccentTextBrush"),
                            FontWeight = FontWeights.SemiBold,
                            UnderlineStyle = UnderlineStyle.None,
                        };
                        mention.Inlines.Add(run);
                        mention.Click += (_, _) => MentionClicked?.Invoke(mentionChat, phone);
                        block.Inlines.Add(mention);
                    }
                    else
                    {
                        run.Foreground = Themed.Brush("ChatAccentTextBrush");
                        run.FontWeight = FontWeights.SemiBold;
                        block.Inlines.Add(run);
                    }
                    continue;
                }
                var email = match.Groups["email"].Success;
                var uri = email ? "mailto:" + match.Value : match.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + match.Value : match.Value;
                var link = new Hyperlink
                {
                    Foreground = Themed.Brush("ChatAccentTextBrush"),
                    UnderlineStyle = UnderlineStyle.Single,
                };
                link.Inlines.Add(new Run { Text = match.Value });
                var target = new Target(uri, match.Value, email);
                link.Click += (_, _) => Open(target);
                Targets.Add(link, target);
                block.Inlines.Add(link);
                at = match.Index + match.Length;
            }
        }
        if (at < text.Length) block.Inlines.Add(new Run { Text = text[at..] });
        block.Inlines.Add(new Run { Text = m.TimeSpacer, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent) });
    }

    /// <summary>
    /// Opens a link the way Explorer would: mailto: starts your mail app, web links your browser.
    /// With no mail app set up, Windows asks which app to use.
    /// </summary>
    public static void Open(Target target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.Uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            if (Uri.TryCreate(target.Uri, UriKind.Absolute, out var uri))
                _ = Windows.System.Launcher.LaunchUriAsync(uri, new Windows.System.LauncherOptions { DisplayApplicationPicker = true });
        }
    }

    /// <summary>The link under <paramref name="point"/> (relative to the TextBlock), if any.</summary>
    public static Target? HitTest(TextBlock block, Point point)
    {
        foreach (var link in block.Inlines.OfType<Hyperlink>())
        {
            if (!Targets.TryGetValue(link, out var target)) continue;
            // Walk the link's characters; each rect is hairline-wide at its start, so a
            // character spans to the next one (or ~a glyph at a line end).
            var length = Math.Max(0, link.ContentEnd.Offset - link.ContentStart.Offset);
            Rect? previous = null;
            for (var i = 0; i <= length; i++)
            {
                if (link.ContentStart.GetPositionAtOffset(i, LogicalDirection.Forward) is not { } position) continue;
                var rect = position.GetCharacterRect(LogicalDirection.Forward);
                if (previous is { } p)
                {
                    var right = rect.Y == p.Y && rect.X > p.X ? rect.X : p.X + 9;
                    if (point.X >= p.X - 1 && point.X <= right + 1 && point.Y >= p.Y - 3 && point.Y <= p.Y + Math.Max(p.Height, 16) + 3)
                        return target;
                }
                previous = rect;
            }
        }
        return null;
    }
}
