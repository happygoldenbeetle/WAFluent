namespace WhatsAppNative.Models;

/// <summary>Placeholder chats for building the UI before the WhatsApp backend exists.</summary>
public static class SampleData
{
    private const string WallpaperPath = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";

    private static Message In(string text, string time) => new() { Text = text, Time = time };

    private static Message Out(string text, string time, Delivery d = Delivery.Read) =>
        new() { Text = text, Time = time, IsOutgoing = true, Delivery = d };

    private static Message Day(string label) => new() { Kind = MessageKind.DateDivider, Text = label };

    public static List<Chat> Create()
    {
        var emma = new Chat { Name = "Emma Warren", Status = "Online", Preview = "😍 Yes that's my fave too", Time = "8:32 AM", LastDelivery = Delivery.Read };
        foreach (var m in new[]
        {
            Day("Yesterday"),
            In("Hey! Did you finish the wallpaper pack?", "5:41 PM"),
            Out("Almost, just exporting the last few", "5:43 PM"),
            In("Can't wait to see them 🙌", "5:44 PM"),
            Out("Here are all the backgrounds. Let me know your favourite!", "6:04 PM"),
            new Message { Kind = MessageKind.File, IsOutgoing = true, FileName = "Backgrounds.zip", FileDetails = "23.5 MB · Compressed (zipped) Folder", Time = "6:04 PM", Delivery = Delivery.Read },
            Day("Today"),
            new Message { Kind = MessageKind.Image, ImagePath = File.Exists(WallpaperPath) ? WallpaperPath : null, Text = "This is beautiful", Time = "8:15 AM" },
            Out("😍 Yes that's my fave too", "8:32 AM"),
        }) emma.Messages.Add(m);

        var keira = new Chat { Name = "Keira Harrison", Status = "last seen today at 3:48 PM", Preview = "Yes OK!", Time = "3:48 PM", Unread = 1 };
        foreach (var m in new[] { Day("Today"), Out("Lunch at 1 tomorrow?", "3:40 PM"), In("Yes OK!", "3:48 PM") }) keira.Messages.Add(m);

        var kurt = new Chat { Name = "Kurt Thomas", Status = "Online", Preview = "Developer, Developers, developers, developers!", Time = "2:54 PM", Unread = 4 };
        foreach (var m in new[]
        {
            Day("Today"), Out("How was the conference?", "2:40 PM"),
            In("Loud.", "2:52 PM"), In("Very loud.", "2:52 PM"), In("Someone was chanting on stage", "2:53 PM"),
            In("Developer, Developers, developers, developers!", "2:54 PM"),
        }) kurt.Messages.Add(m);

        var eha = new Chat { Name = "Eha Meri", Status = "last seen today at 10:04 AM", Preview = "Call me when you can you have a minute", Time = "10:04 AM", Unread = 1 };
        foreach (var m in new[] { Day("Today"), In("Call me when you can you have a minute", "10:04 AM") }) eha.Messages.Add(m);

        var ninja = new Chat { Name = "Ninjacat", Status = "last seen recently", Preview = "Are you there?", Time = "12:32 AM" };
        foreach (var m in new[] { Day("Today"), In("Are you there?", "12:32 AM") }) ninja.Messages.Add(m);

        var lloyd = new Chat { Name = "Lloyd Berry", Status = "typing…", IsTyping = true, Preview = "typing…", Time = "05/11" };
        foreach (var m in new[] { Day("05/11"), In("Got the tickets 🎟️", "7:12 PM"), Out("Legend", "7:15 PM") }) lloyd.Messages.Add(m);

        var jihoon = new Chat { Name = "Jihoon Seo", Status = "last seen 05/11", Preview = "Big jump!", PreviewGlyph = "\uE714", Time = "05/11", LastDelivery = Delivery.Read };
        foreach (var m in new[] { Day("05/11"), In("Did you see my video?", "4:02 PM"), Out("Big jump!", "4:10 PM") }) jihoon.Messages.Add(m);

        var design = new Chat { Name = "Design Team", Status = "Keira, Kurt, Emma, You", Preview = "Kurt: Pushed the new icons", Time = "05/10" };
        foreach (var m in new[] { Day("05/10"), In("Standup moved to 10", "9:01 AM"), In("Pushed the new icons", "11:20 AM") }) design.Messages.Add(m);

        var mom = new Chat { Name = "Mom", Status = "last seen 05/09", Preview = "Will do!", Time = "05/09", LastDelivery = Delivery.Delivered };
        foreach (var m in new[] { Day("05/09"), In("Call me when you land ❤️", "6:30 AM"), Out("Will do!", "6:31 AM", Delivery.Delivered) }) mom.Messages.Add(m);

        return [keira, kurt, eha, emma, ninja, lloyd, jihoon, design, mom];
    }
}
