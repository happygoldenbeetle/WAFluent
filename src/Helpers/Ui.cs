using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Models;

namespace WhatsAppNative.Helpers;

/// <summary>Small functions used from x:Bind in XAML.</summary>
public static class Ui
{
    /// <summary>
    /// Round bubbles (the default; Settings › Classic bubbles turns them off): WhatsApp's colours,
    /// times and ticks, with rounder corners and the curled tail on the last bubble of a run.
    /// Set before a conversation is drawn; the window redraws it when it changes.
    /// </summary>
    public static bool IMessage { get; set; } = true;

    /// <summary>Corner radius of an iMessage-style bubble (round, not a pill).</summary>
    public const double BubbleRadius = 12;

    /// <summary>
    /// Corners for a picture, map, card or link preview inside a bubble: sides that touch the
    /// bubble's top or bottom edge follow its curve (its radius minus the gap to the edge),
    /// the others keep the usual small rounding.
    /// </summary>
    public static CornerRadius InsetCorners(bool touchesTop, bool touchesBottom, double inset)
    {
        const double small = 6;
        if (!IMessage) return new CornerRadius(small);
        var curve = Math.Max(small, BubbleRadius - inset);
        double top = touchesTop ? curve : small, bottom = touchesBottom ? curve : small;
        return new CornerRadius(top, top, bottom, bottom);
    }


    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotEmpty(string? value) => Visible(!string.IsNullOrEmpty(value));
    public static Visibility HasDelivery(Delivery d) => Visible(d != Delivery.None);

    /// <summary>Chat list: the last message's parts give way to "typing…".</summary>
    public static Visibility TextUnlessTyping(string? value, bool typing) => Visible(!typing && !string.IsNullOrEmpty(value));
    public static Visibility DeliveryUnlessTyping(Delivery d, bool typing) => Visible(!typing && d != Delivery.None);
    public static Visibility IsNull(object? value) => Visible(value is null);
    public static Visibility IsNotNull(object? value) => Visible(value is not null);
    public static Visibility Positive(int value) => Visible(value > 0);
    public static Visibility Both(bool a, bool b) => Visible(a && b);
    public static Visibility VisibleAndNot(bool a, bool b) => Visible(a && !b);

    /// <summary>Deleted messages read in italics.</summary>
    public static Windows.UI.Text.FontStyle Italic(bool value) => value ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    /// <summary>Select mode: picked rows get a faint green wash.</summary>
    public static Brush SelectionTint(bool selected) =>
        new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(0x33, 0x00, 0xA8, 0x84) : Microsoft.UI.Colors.Transparent);

    /// <summary>The bubble colour: green for yours (time pills under stickers and big emoji match).</summary>
    /// <summary>Red for a missed call, the usual grey otherwise.</summary>
    /// <summary>The icon on a call's card in a chat: red for one you missed.</summary>
    public static Brush CallCardBrush(bool missed) => Themed.Brush(missed ? "DangerBrush" : "BubbleTextBrush");

    public static string CallAgainTip(bool video) => video ? "Video call" : "Voice call";

    public static Brush CallBrush(bool missed) => Themed.Brush(missed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");

    public static Brush BubbleBrush(bool isOutgoing) => Themed.Brush(isOutgoing ? "OutgoingBubbleBrush" : "IncomingBubbleBrush");

    /// <summary>A channel's post sits in the middle of the pane.</summary>
    public static HorizontalAlignment Align(bool isOutgoing, bool isPost) => isPost ? HorizontalAlignment.Center : Align(isOutgoing);

    /// <summary>A channel's posts are one column, all the same width (a message is as wide as what's in it).</summary>
    /// (The widest a bubble gets, so a post's bubble starts where its column does.)
    public static double PostWidth(bool isPost) => isPost ? 520 : double.NaN;

    /// <summary>A link's card is at most this wide in a message; in a channel's post it's as wide as the post.</summary>
    public static double LinkCardWidth(bool isPost) => isPost ? double.PositiveInfinity : 420;

    /// <summary>There's something in it (for x:Load: a part of a bubble exists only when it has something to show).</summary>
    public static bool Has(string? text) => !string.IsNullOrEmpty(text);

    /// <summary>The forward pill: beside the reactions, or where they'd be when there are none.</summary>
    public static Thickness ForwardPillMargin(string reaction) => new(reaction.Length > 0 ? 6 : 8, -6, 0, 0);

    public static HorizontalAlignment Align(bool isOutgoing) =>
        isOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public static ImageSource? Image(string? path) =>
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 640 };

    /// <summary>Media, links and docs: a photo's tile (videos show their preview only).</summary>
    public static ImageSource? GalleryImage(string? path, Models.MessageKind kind) =>
        kind == Models.MessageKind.Image && path is not null && File.Exists(path) ? new BitmapImage(new Uri(path)) { DecodePixelWidth = 240 } : null;

    public static Visibility IsVideoTile(Models.MessageKind kind) => kind == Models.MessageKind.Video ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A video tile's corner: a camera and its length, or GIF.</summary>
    public static string VideoTileGlyph(bool gif) => gif ? "" : "\uE714";
    public static string VideoTileText(bool gif, int seconds) => gif ? "GIF" : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>A sticker: shown at 150 px, so decoded at 320 (sharp at 200 % scaling) rather than 640.</summary>
    public static ImageSource? Sticker(string? path) =>
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 320 };

    /// <summary>Previews already turned into images, by the text they came from (a bubble asks more than once).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, BitmapImage> Thumbs = new();

    /// <summary>
    /// The sender's base64 JPEG preview (a few KB) as an image. It's decoded off the UI thread
    /// (the image fills in a moment later), and once per preview.
    /// </summary>
    public static ImageSource? Thumb(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (Thumbs.TryGetValue(base64, out var kept)) return kept;
        try
        {
            var image = new BitmapImage();
            _ = image.SetSourceAsync(new MemoryStream(Convert.FromBase64String(base64)).AsRandomAccessStream());
            Thumbs.AddOrUpdate(base64, image);
            return image;
        }
        catch (Exception)
        {
            return null;   // not a picture the decoder knows
        }
    }

    /// <summary>Round video messages are circles; other media has softly rounded corners.</summary>
    public static CornerRadius MediaCorner(bool round, double size) => new(round ? size / 2 : 6);

    /// <summary>Poll option mark: always a circle.</summary>
    public static CornerRadius PollMark(bool multi) => new(10);

    /// <summary>Second button on a contact card.</summary>
    public static string ContactAction(int count) => count > 1 ? "View all" : "Copy number";

    public static string FileIcon(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" or ".rar" or ".7z" => "🗂️",
        ".pdf" => "📕",
        ".doc" or ".docx" or ".txt" => "📄",
        ".xls" or ".xlsx" or ".csv" => "📊",
        ".mp3" or ".wav" or ".m4a" => "🎵",
        _ => "📁",
    };
}
