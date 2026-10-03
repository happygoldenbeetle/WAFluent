using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Controls;
using WhatsAppNative.Helpers;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The Channels page (the rail's megaphone), like WhatsApp's.
///   Left  : search (channels by name, from WhatsApp's directory), the channels you follow with
///           their newest post and how many are new, then "Find channels to follow" with a
///           Follow button each.
///   Right : "Discover channels" until one is opened; then its posts in the conversation pane,
///           read-only (no message box), with the reactions WhatsApp counts under each. The
///           header has Follow (for one you don't follow), mute, and a menu: Close channel, Unfollow.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string BellGlyph = char.ConvertFromUtf32(0xEA8F), BellOffGlyph = char.ConvertFromUtf32(0xE7ED),
                                   VerifiedGlyph = char.ConvertFromUtf32(0xEC61);

    private bool _channelsOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _channelSearchTimer;

    private void SetupChannels()
    {
        ViewModel.ChannelsChanged += () =>
        {
            if (_channelsOpen) FillChannels();
            ShowChannelChrome();
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedChat)) ShowChannelChrome();
        };
#if DEBUG
        // WAFLUENT_SELFTEST=channels | channels-open: the page on the sample data.
        if (Environment.GetEnvironmentVariable("WAFLUENT_SELFTEST") is { } test && test.StartsWith("channels"))
            Root.Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                Nav.SelectedItem = Nav.MenuItems[3];
                await Task.Delay(600);
                if (test == "channels-open" && ViewModel.Channels.FirstOrDefault() is { } first) ViewModel.OpenChannel(first);
                if (test != "channels-live") return;
                // On the linked account: the lists from WhatsApp, then a channel opened (one you follow, else a suggested
                // one). Counts only go to the file: no names, no text.
                var lines = new List<string>();
                for (var i = 0; i < 25 && ViewModel.Channels.Count + ViewModel.SuggestedChannels.Count == 0; i++) await Task.Delay(1000);
                await Task.Delay(3000);
                lines.Add($"followed {ViewModel.Channels.Count}, suggested {ViewModel.SuggestedChannels.Count}, with pictures {ViewModel.Channels.Concat(ViewModel.SuggestedChannels).Count(c => c.Avatar is not null)}, verified {ViewModel.SuggestedChannels.Count(c => c.Verified)}");
                foreach (var pick in ViewModel.Channels.Concat(ViewModel.SuggestedChannels).Take(6).ToList())
                {
                    ViewModel.OpenChannel(pick);
                    await Task.Delay(11000);
                    var posts = ViewModel.SelectedChat?.Messages.Where(m => m.Kind != Models.MessageKind.DateDivider).ToList() ?? [];
                    var all = posts.SelectMany(m => m.AlbumItems ?? [m]).ToList();
                    lines.Add($"opened one ({(pick.Followed ? "followed" : "suggested")}, {pick.Followers} followers): {posts.Count} posts; "
                              + string.Join(", ", posts.GroupBy(m => m.Kind).Select(g => $"{g.Key} {g.Count()}"))
                              + $"; with media {all.Count(m => m.HasMedia)}, downloaded {all.Count(m => m.MediaPath is not null)}, failed {all.Count(m => m.MediaFailed)}, with reaction counts {posts.Count(m => m.ReactionSummary.Length > 0)}");
                    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
                }
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wafluent-selftest.txt"), lines);
            };
#endif
    }

    /// <summary>The rail's Channels was picked (or left).</summary>
    private void ShowChannels(bool show)
    {
        _channelsOpen = show;
        ChannelsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            ViewModel.CloseChannel();   // a channel isn't a chat: it doesn't stay open on the other pages
            ShowChannelChrome();
            return;
        }
        ChannelsSearch.Text = "";
        FillChannels();
        ShowChannelChrome();
        ViewModel.LoadChannels();
    }

    /// <summary>What a channel changes around the conversation: the header's buttons, and "Discover channels" until one is open.</summary>
    private void ShowChannelChrome()
    {
        var channel = ViewModel.ChannelOf(ViewModel.SelectedChat);
        var open = ViewModel.SelectedChat is { IsChannel: true };
        ChatHeaderButtons.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        ChannelHeaderButtons.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChannelsPane.Visibility = _channelsOpen && !open ? Visibility.Visible : Visibility.Collapsed;
        if (channel is null) return;
        ChannelFollowButton.Visibility = channel.Followed ? Visibility.Collapsed : Visibility.Visible;
        ChannelMuteButton.Visibility = ChannelUnfollowItem.Visibility = channel.Followed ? Visibility.Visible : Visibility.Collapsed;
        ChannelMuteIcon.Glyph = channel.Muted ? BellOffGlyph : BellGlyph;
        ToolTipService.SetToolTip(ChannelMuteButton, channel.Muted ? "Unmute" : "Mute");
    }

    private void ChannelFollow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ChannelOf(ViewModel.SelectedChat) is { } channel) ViewModel.ChannelAction(channel, "follow");
    }

    private void ChannelMute_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ChannelOf(ViewModel.SelectedChat) is { } channel) ViewModel.ChannelAction(channel, channel.Muted ? "unmute" : "mute");
    }

    private void ChannelUnfollow_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ChannelOf(ViewModel.SelectedChat) is not { } channel) return;
        ViewModel.CloseChannel();
        ViewModel.ChannelAction(channel, "unfollow");
    }

    private void ChannelClose_Click(object sender, RoutedEventArgs e) => ViewModel.CloseChannel();

    /// <summary>Searching waits until you stop typing for a moment.</summary>
    private void ChannelsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_channelSearchTimer is null)
        {
            _channelSearchTimer = DispatcherQueue.CreateTimer();
            _channelSearchTimer.Interval = TimeSpan.FromMilliseconds(450);
            _channelSearchTimer.IsRepeating = false;
            _channelSearchTimer.Tick += (_, _) => ViewModel.SearchChannels(ChannelsSearch.Text);
        }
        _channelSearchTimer.Stop();
        _channelSearchTimer.Start();
    }

    // ───── The list ─────

    private void FillChannels()
    {
        ChannelRows.Children.Clear();
        if (ViewModel.ChannelResults is { } results)
        {
            foreach (var channel in results) ChannelRows.Children.Add(ChannelRow(channel));
            if (results.Count == 0) ChannelRows.Children.Add(ChannelNote("No channels found."));
            return;
        }
        foreach (var channel in ViewModel.Channels) ChannelRows.Children.Add(ChannelRow(channel));
        if (ViewModel.SuggestedChannels.Count > 0)
        {
            ChannelRows.Children.Add(new TextBlock
            {
                Text = "Find channels to follow",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(14, 18, 0, 6),
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
            foreach (var channel in ViewModel.SuggestedChannels) ChannelRows.Children.Add(ChannelRow(channel));
        }
        else if (ViewModel.Channels.Count == 0)
        {
            ChannelRows.Children.Add(ChannelNote(ViewModel.IsLive ? "Loading channels…" : "No channels."));
        }
    }

    private static TextBlock ChannelNote(string text) => new()
    {
        Text = text,
        Margin = new Thickness(14, 22, 14, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
    };

    /// <summary>
    /// One channel: followed ones show their newest post, when it was and how many are new; the
    /// others their followers and a Follow button. Clicking the row opens it either way.
    /// </summary>
    private Button ChannelRow(ChannelDto channel)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var picture = new Redact { VeilRadius = new CornerRadius(24), VerticalAlignment = VerticalAlignment.Center };
        picture.Children.Add(new Avatar { DisplayName = channel.Name, Source = channel.Avatar, Size = 48, IsGroup = true });
        grid.Children.Add(picture);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        name.Children.Add(new TextBlock { Text = channel.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = channel.Followed ? 190 : 150 });
        if (channel.Verified)
            name.Children.Add(new FontIcon { Glyph = VerifiedGlyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1D, 0x9B, 0xF0)) });
        var detail = channel.Followed && channel.Preview.Length > 0 ? channel.Preview.ReplaceLineEndings(" ") : ViewModels.MainViewModel.FollowersText(channel.Followers);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = detail, FontSize = 13, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (channel.Followed)
        {
            var side = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var unread = channel.Unread > 0;
            if (channel.LastTs > 0)
                side.Children.Add(new TextBlock
                {
                    Text = Format.ListTime(Format.FromUnix(channel.LastTs)),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = Themed.Brush(unread ? "ChatAccentTextBrush" : "TextFillColorSecondaryBrush"),
                });
            if (unread)
                side.Children.Add(new Border
                {
                    Background = Themed.Brush("ChatAccentBrush"),
                    CornerRadius = new CornerRadius(10),
                    MinWidth = 20,
                    Height = 20,
                    Padding = new Thickness(6, 0, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock
                    {
                        Text = channel.Unread >= 10 ? "9+" : channel.Unread.ToString(),
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0B, 0x14, 0x1A)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            else if (channel.Muted)
                side.Children.Add(new FontIcon { Glyph = BellOffGlyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            Grid.SetColumn(side, 2);
            grid.Children.Add(side);
        }
        else
        {
            var follow = new Button
            {
                Content = "Follow",
                Padding = new Thickness(16, 5, 16, 6),
                CornerRadius = new CornerRadius(16),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Themed.Brush("ChatAccentTextBrush"),
            };
            AutomationProperties.SetName(follow, $"Follow {channel.Name}");
            follow.Click += (_, _) => ViewModel.ChannelAction(channel, "follow");
            Grid.SetColumn(follow, 2);
            grid.Children.Add(follow);
        }

        var row = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 9, 8, 9),
            BorderThickness = new Thickness(0),
            Background = ViewModel.SelectedChat?.Id == channel.Id ? Themed.Brush("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.Click += (_, _) => ViewModel.OpenChannel(channel);
        return row;
    }
}
