using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative.ViewModels;

/// <summary>
/// Channels: what the Channels page (MainWindow.Channels.cs) lists. An opened channel is shown
/// in the conversation pane as a read-only <see cref="Chat"/> (IsChannel) that isn't in the
/// chat list: its posts load, scroll back and download their pictures the way messages do.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The channels you follow, newest post first.</summary>
    public IReadOnlyList<ChannelDto> Channels { get; private set; } = [];

    /// <summary>Ones WhatsApp suggests following.</summary>
    public IReadOnlyList<ChannelDto> SuggestedChannels { get; private set; } = [];

    /// <summary>What the search box found (null: not searching).</summary>
    public IReadOnlyList<ChannelDto>? ChannelResults { get; private set; }

    /// <summary>The lists, or what's known about a channel, changed.</summary>
    public event Action? ChannelsChanged;

    private readonly Dictionary<string, ChannelDto> _channelInfo = new();
    private string _channelQuery = "";
    private int _unreadChannels;

    /// <summary>Channels you follow with posts since you last opened them (the number on the rail).</summary>
    public int UnreadChannels { get => _unreadChannels; private set => Set(ref _unreadChannels, value); }

    private void WireChannels(CoreClient core)
    {
        core.ChannelsReceived += (followed, suggested, fresh) =>
        {
            // The one that's open has just been read.
            SetChannels(followed.Select(c => c.Id == _selectedChat?.Id ? c with { Unread = 0 } : c).ToList());
            if (suggested.Count > 0 || fresh) SuggestedChannels = suggested.Count > 0 ? suggested : SuggestedChannels.Where(s => followed.All(f => f.Id != s.Id)).ToList();
            foreach (var channel in followed.Concat(suggested)) Remember(channel);
            ChannelsChanged?.Invoke();
        };
        core.ChannelSearchReceived += (query, results) =>
        {
            if (query != _channelQuery) return;
            ChannelResults = results;
            foreach (var channel in results) Remember(channel);
            ChannelsChanged?.Invoke();
        };
        core.ChannelChanged += id =>
        {
            if (_selectedChat?.Id == id) core.LoadMessages(id);
            core.LoadChannels();
        };
    }

    private void SetChannels(IReadOnlyList<ChannelDto> followed)
    {
        Channels = followed;
        UnreadChannels = followed.Count(c => c.Unread > 0);
    }

    /// <summary>What's known about a channel is kept, and shown on its open conversation.</summary>
    private void Remember(ChannelDto channel)
    {
        _channelInfo[channel.Id] = channel;
        if (!_byId.TryGetValue(channel.Id, out var chat) || !chat.IsChannel) return;
        chat.Name = channel.Name;
        chat.AvatarPath = channel.Avatar;
        chat.Status = FollowersText(channel.Followers);
    }

    public static string FollowersText(long followers) => followers == 1 ? "1 follower" : $"{Format.Compact(followers)} followers";

    /// <summary>The channel behind an open conversation.</summary>
    public ChannelDto? ChannelOf(Chat? chat) => chat is { IsChannel: true } ? _channelInfo.GetValueOrDefault(chat.Id) : null;

    public void LoadChannels()
    {
        if (_core is not null)
        {
            _core.LoadChannels();
            return;
        }
        if (Channels.Count == 0 && SuggestedChannels.Count == 0)
        {
            SetChannels([new ChannelDto("sample-1@newsletter", "City News", "", 219_000, false, null, true, false, DateTimeOffset.Now.AddHours(-20).ToUnixTimeSeconds(),
                                        "Fuel prices increased from midnight", 2)]);
            SuggestedChannels =
            [
                new ChannelDto("sample-2@newsletter", "World Scholarship Opportunities", "", 171_000, false, null, false, false, 0, "", 0),
                new ChannelDto("sample-3@newsletter", "Daad Scholarship", "", 172_000, true, null, false, false, 0, "", 0),
                new ChannelDto("sample-4@newsletter", "WhatsApp", "", 234_200_000, true, null, false, false, 0, "", 0),
                new ChannelDto("sample-5@newsletter", "Scholarships Corner", "", 119_000, false, null, false, false, 0, "", 0),
            ];
            foreach (var channel in Channels.Concat(SuggestedChannels)) _channelInfo[channel.Id] = channel;
        }
        ChannelsChanged?.Invoke();
    }

    /// <summary>The search box: channels by name ("" ends the search).</summary>
    public void SearchChannels(string query)
    {
        _channelQuery = query = query.Trim();
        if (query.Length < 2)
        {
            ChannelResults = null;
            ChannelsChanged?.Invoke();
            return;
        }
        if (_core is not null) _core.SearchChannels(query);
        else
        {
            ChannelResults = Channels.Concat(SuggestedChannels).Where(c => c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            ChannelsChanged?.Invoke();
        }
    }

    /// <summary>follow | unfollow | mute | unmute.</summary>
    public void ChannelAction(ChannelDto channel, string action)
    {
        if (_core is not null)
        {
            _core.ChannelAction(channel.Id, action);
            if (action is "follow" or "unfollow") return;   // the lists come back with it moved
        }
        // Shown at once (and all there is to it on the sample data).
        var now = action switch
        {
            "follow" => channel with { Followed = true },
            "unfollow" => channel with { Followed = false },
            _ => channel with { Muted = action == "mute" },
        };
        _channelInfo[channel.Id] = now;
        SetChannels(action == "unfollow" ? Channels.Where(c => c.Id != channel.Id).ToList()
            : Channels.Any(c => c.Id == channel.Id) ? Channels.Select(c => c.Id == channel.Id ? now : c).ToList()
            : Channels.Append(now).ToList());
        SuggestedChannels = SuggestedChannels.Where(c => c.Id != channel.Id || !now.Followed).ToList();
        ChannelsChanged?.Invoke();
        if (_core is null) Toast?.Invoke(true, action switch { "follow" => "Following", "unfollow" => "Unfollowed", "mute" => "Channel muted", _ => "Channel unmuted" });
    }

    /// <summary>Shows a channel's posts in the conversation pane.</summary>
    public void OpenChannel(ChannelDto channel)
    {
        _channelInfo[channel.Id] = _channelInfo.GetValueOrDefault(channel.Id) ?? channel;
        if (!_byId.TryGetValue(channel.Id, out var chat))
        {
            chat = new Chat { Id = channel.Id, IsChannel = true, Name = channel.Name, AvatarPath = channel.Avatar, Status = FollowersText(channel.Followers) };
            _byId[channel.Id] = chat;
        }
        if (_core is not null) chat.MessagesLoaded = false;   // read again each time: posts and their reaction counts move
        else if (chat.Messages.Count == 0)
        {
            string[] posts =
            [
                "The second round of talks will be held tomorrow at 2 PM. The discussions were held in a positive atmosphere.",
                "Pakistan vs India T20 for the gold medal in the Asian Games cricket final is tomorrow at 9:30 AM",
                "Fuel prices increased from midnight\n\npetrol increased by 2.10\ndiesel increased by 0.30",
            ];
            foreach (var (text, i) in posts.Select((t, i) => (t, i)))
                chat.Messages.Add(new Message { Id = $"sample-post-{i}", Text = text, Time = $"{6 + i * 2}:1{i} pm", ReactionSummary = i < 2 ? "😂❤️🙏👍 " + (430 - i * 190) : "" });
            chat.MessagesLoaded = true;
        }
        if (Channels.Any(c => c.Id == channel.Id && c.Unread > 0)) SetChannels(Channels.Select(c => c.Id == channel.Id ? c with { Unread = 0 } : c).ToList());
        SelectedChat = chat;
        ChannelsChanged?.Invoke();
    }

    /// <summary>The open channel is put away (leaving the Channels page, or its menu's Close channel).</summary>
    public void CloseChannel()
    {
        if (_selectedChat is { IsChannel: true }) SelectedChat = null;
    }
}
