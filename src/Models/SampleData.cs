using WhatsAppNative.Helpers;

namespace WhatsAppNative.Models;

/// <summary>Placeholder chats for building the UI before the WhatsApp backend exists.</summary>
public static class SampleData
{
    private static string? LocalImage(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>A local picture as a base64 "preview" (the core sends small JPEGs the same way).</summary>
    private static string? Preview(string? path) => path is null ? null : Convert.ToBase64String(File.ReadAllBytes(path));

    private static readonly string? Picture = LocalImage(@"C:\Windows\Web\Screen\img102.jpg", @"C:\Windows\Web\Wallpaper\Windows\img0.jpg");
    private static readonly string? Picture2 = LocalImage(@"C:\Windows\Web\Screen\img103.png", @"C:\Windows\Web\Screen\img101.jpg", @"C:\Windows\Web\Wallpaper\Windows\img19.jpg");

    private static Message In(string text, string time, string reaction = "") =>
        new() { Id = NewId(), Text = text, Time = time, Reactions = reaction.Length > 0 ? [reaction] : [] };

    private static Message Out(string text, string time, Delivery d = Delivery.Read) =>
        new() { Id = NewId(), Text = text, Time = time, IsOutgoing = true, Delivery = d };

    private static Message Poll(string question, string time, params (string Name, string[] Voters)[] options)
    {
        var poll = new Message { Id = NewId(), Kind = MessageKind.Poll, Time = time, Text = question };
        poll.PollOptions = Format.PollOptions(poll, options.Select(o => o.Name).ToList(),
                                              options.ToDictionary(o => o.Name, o => o.Voters.ToList()),
                                              new HashSet<string> { options[0].Name });
        return poll;
    }

    private static Message Day(string label) => new() { Kind = MessageKind.DateDivider, Text = label };

    private static Chat With(this Chat chat, params Message[] messages)
    {
        foreach (var m in messages) chat.Messages.Add(m);
        return chat;
    }

    public static List<Chat> Create() =>
    [
        new Chat { Name = "Maya Kasuma", Status = "last seen today at 14:54", Preview = "Yes! OK", Time = "14:54", IsPinned = true, IsMuted = true }
            .With(Day("Today"), Out("Coffee after the review?", "14:50"), In("Yes! OK", "14:54")),

        new Chat { Name = "Jason Ballmer", Status = "online", Preview = "Video", PreviewGlyph = Glyphs.Video, Time = "15:26", Unread = 3 }
            .With(Day("Today"), In("Check this out", "15:25"), In("You have to see the ending", "15:25"), In("🎥 Video", "15:26")),

        new Chat { Name = "Alice Whitman", Status = "online", Preview = "Thanks! Could you send me the photos from Saturday", Time = "15:20" }
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
                },
                In("Thanks! Could you send me the photos from Saturday when you get a moment? I'd like to put a few of them in the album before Mum's birthday on Sunday.", "15:20"),
                new Message { Id = NewId(), Kind = MessageKind.Voice, Time = "15:21", Seconds = 19, HasMedia = true, MediaPath = Picture,
                              Waveform = Enumerable.Range(0, 64).Select(i => 20 + (i * 37 % 70)).ToArray() },
                new Message { Id = NewId(), Kind = MessageKind.Voice, Time = "15:22", Seconds = 14, IsOutgoing = true, Delivery = Delivery.Read, HasMedia = true, MediaPath = Picture },
                new Message { Id = NewId(), Kind = MessageKind.Voice, Time = "15:22", Seconds = 204, IsVoiceNote = false, HasMedia = true, MediaPath = Picture },
                new Message { Id = NewId(), Kind = MessageKind.Video, Time = "15:23", Seconds = 32, HasMedia = true, Thumb = Preview(Picture2), MediaWidth = 300, MediaHeight = 200, Text = "The waterfall!" },
                new Message { Id = NewId(), Kind = MessageKind.Video, Time = "15:23", IsGif = true, HasMedia = true, Thumb = Preview(Picture), MediaWidth = 220, MediaHeight = 160, IsOutgoing = true, Delivery = Delivery.Read },
                new Message { Id = NewId(), Kind = MessageKind.Location, Time = "15:24", Thumb = Preview(Picture2), MediaWidth = 300, MediaHeight = 150,
                              Latitude = 31.5204, Longitude = 74.3587, PlaceName = "Lahore Fort", PlaceAddress = "Fort Rd, Walled City of Lahore" },
                new Message { Id = NewId(), Kind = MessageKind.Contact, Time = "15:25", Text = "Sara Khan", Contacts = [new ContactCard("Sara Khan", ["+92 300 1234567"])] },
                Poll("Where should we eat on Sunday?", "15:26", ("Monal", ["Alice Whitman", "You"]), ("Cafe Aylanto", ["Sam"]), ("Home — I'll cook", [])),
                In("Mail me the tickets at alice.whitman@example.com or check www.example.com/tickets", "15:26"),
                new Message { Id = NewId(), Kind = MessageKind.Location, Time = "15:26", Thumb = Preview(Picture2), MediaWidth = 300, MediaHeight = 150,
                              Latitude = 31.5820, Longitude = 74.3294 },
                new Message { Id = NewId(), Kind = MessageKind.File, FileName = "Boarding pass.png", FileDetails = "PNG", Time = "15:27", IsOutgoing = true, Delivery = Delivery.Read },
                new Message { Id = NewId(), Kind = MessageKind.System, Text = "📞 Missed voice call" },
                new Message { Id = NewId(), Text = "https://github.com/happygoldenbeetle/WAFluent", Time = "15:27", IsOutgoing = true, Delivery = Delivery.Delivered,
                              LinkUrl = "https://github.com/happygoldenbeetle/WAFluent", LinkTitle = "WAFluent", LinkDescription = "A native WinUI 3 WhatsApp client.", Thumb = Preview(Picture) },
                new Message { Id = NewId(), Kind = MessageKind.File, FileName = "Itinerary.pdf", FileDetails = "PDF", Pages = 3, Time = "15:28", Thumb = Preview(Picture2) },
                In("😂", "15:29"),
                Out("❤️🔥👍🏽", "15:29"),
                In("🇵🇰🎉 ok see you", "15:30")),

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
