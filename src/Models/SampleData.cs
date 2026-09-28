using WhatsAppNative.Helpers;

namespace WhatsAppNative.Models;

/// <summary>Placeholder chats for building the UI before the WhatsApp backend exists.</summary>
public static class SampleData
{
    private static string? LocalImage(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static Message In(string text, string time, string reaction = "") =>
        new() { Id = NewId(), Text = text, Time = time, Reactions = reaction.Length > 0 ? [reaction] : [] };

    private static Message Out(string text, string time, Delivery d = Delivery.Read) =>
        new() { Id = NewId(), Text = text, Time = time, IsOutgoing = true, Delivery = d };

    private static Message Day(string label) => new() { Kind = MessageKind.DateDivider, Text = label };

    private static Chat With(this Chat chat, params Message[] messages)
    {
        foreach (var m in messages) chat.Messages.Add(m);
        return chat;
    }

    public static List<Chat> Create() =>
    [
        new Chat { Name = "Maya Kasuma", Status = "last seen today at 14:54", Preview = "Yes! OK", Time = "14:54", IsPinned = true }
            .With(Day("Today"), Out("Coffee after the review?", "14:50"), In("Yes! OK", "14:54")),

        new Chat { Name = "Jason Ballmer", Status = "online", Preview = "Video", PreviewGlyph = Glyphs.Video, Time = "15:26", Unread = 3 }
            .With(Day("Today"), In("Check this out", "15:25"), In("You have to see the ending", "15:25"), In("🎥 Video", "15:26")),

        new Chat { Name = "Alice Whitman", Status = "online", Preview = "Wow! Have great time. Enjoy.", Time = "15:12", LastDelivery = Delivery.Read }
            .With(
                Day("Yesterday"),
                In("Did you get a chance to pull everything together?", "17:40"),
                Out("Here are all the files. Let me know once you've had a look.", "18:02"),
                new Message { Kind = MessageKind.File, IsOutgoing = true, FileName = "All-files.zip", FileDetails = "23.5 MB · Compressed (zipped) Folder", Time = "18:02", Delivery = Delivery.Read },
                In("OK! 👍", "14:04"),
                Day("Today"),
                new Message
                {
                    Id = "sample-photo",
                    Kind = MessageKind.Image, Text = "So beautiful here!", Time = "15:06", Reactions = ["❤️"], MyReaction = "❤️",
                    Timestamp = DateTime.Today.AddHours(15).AddMinutes(6),
                    HasMedia = true,
                    MediaPath = LocalImage(@"C:\Windows\Web\Screen\img102.jpg", @"C:\Windows\Web\Wallpaper\Windows\img0.jpg"),
                },
                new Message
                {
                    Id = NewId(), Text = "Wow! Have great time. Enjoy.", Time = "15:12", IsOutgoing = true, Delivery = Delivery.Read,
                    ReplyId = "sample-photo", ReplyName = "Alice Whitman", ReplyPreview = "So beautiful here!", ReplyGlyph = Glyphs.Photo,
                }),

        new Chat { Name = "Baking Club", IsGroup = true, Status = "Rebecca, Chris, Maya, You", PreviewSender = "Rebecca:", Preview = "@Chris R?", Time = "14:43", Unread = 1, HasMention = true }
            .With(Day("Today"), In("Who's bringing the sourdough starter?", "14:40"), In("@Chris R?", "14:43")),

        new Chat { Name = "Stasa Benko", Status = "last seen today at 13:56", Preview = "Aww no problem.", Time = "13:56", Unread = 2, HasStatus = true }
            .With(Day("Today"), Out("Sorry, can't make it tonight 😞", "13:50"), In("Aww", "13:55"), In("Aww no problem.", "13:56")),

        new Chat { Name = "Family Foodies", IsGroup = true, Status = "Mom, Dad, Sam, You", Preview = "Dinner last night", PreviewGlyph = Glyphs.Photo, Time = "11:21", LastDelivery = Delivery.Read }
            .With(Day("Today"), In("Who made the lasagna??", "11:02"), Out("📷 Dinner last night", "11:21")),

        new Chat { Name = "Mark Rogers", Status = "typing…", IsTyping = true, Preview = "typing…", Time = "10:56" }
            .With(Day("Today"), In("Are we still on for Friday?", "10:50"), Out("Yep!", "10:55")),

        new Chat { Name = "Dawn Jones", Status = "last seen today at 08:32", Preview = "Yes that's my fave too!", Time = "8:32", LastDelivery = Delivery.Read }
            .With(Day("Today"), In("The blue one is so good", "8:30"), Out("Yes that's my fave too!", "8:32")),

        new Chat { Name = "Tony Woodley", Status = "last seen yesterday at 21:10", Preview = "See you tomorrow", Time = "Yesterday" }
            .With(Day("Yesterday"), In("See you tomorrow", "21:10")),
    ];
}
