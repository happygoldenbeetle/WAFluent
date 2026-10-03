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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _channelSearchTimer, _channelRefresh;

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
            _channelRefresh?.Stop();
            ViewModel.CloseChannel();   // a channel isn't a chat: it doesn't stay open on the other pages
            ShowChannelChrome();
            return;
        }
        ChannelsSearch.Text = "";
        FillChannels();
        ShowChannelChrome();
        ViewModel.LoadChannels();
        // While the page is open the lists are read again every half minute (and when the phone
        // says something changed): what you follow, leave or mute there shows up here.
        if (_channelRefresh is null)
        {
            _channelRefresh = DispatcherQueue.CreateTimer();
            _channelRefresh.Interval = TimeSpan.FromSeconds(30);
            _channelRefresh.Tick += (_, _) => { if (_channelsOpen && ViewModel.IsLive) ViewModel.LoadChannels(); };
        }
        _channelRefresh.Start();
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

    /// <summary>A post's reactions, each with its count ("476 reactions": 😂 311, 😢 73…).</summary>
    private void ReactionPill_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Models.Message { PostReactions.Count: > 0 } post } pill) return;
        e.Handled = true;
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        var row = RowOf(pill);
        var chips = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var (emoji, count) in post.PostReactions)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            chip.Children.Add(new TextBlock { Text = emoji, FontSize = 18, FontFamily = (FontFamily)Application.Current.Resources["EmojiFontFamily"] });
            chip.Children.Add(new TextBlock { Text = Format.Compact(count), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
            // Yours is marked. A click reacts with that one (it replaces yours: one per post); on yours, takes it back.
            var mine = post.MyReaction.Length > 0 && post.MyReaction.Replace("\uFE0F", "") == emoji.Replace("\uFE0F", "");
            var button = new Button
            {
                Content = chip,
                Padding = new Thickness(12, 5, 12, 6),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                BorderBrush = Themed.Brush(mine ? "ChatAccentBrush" : "ControlStrokeColorDefaultBrush"),
                Background = mine ? Themed.Brush("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            AutomationProperties.SetName(button, mine ? $"Take back {emoji}" : $"React with {emoji}");
            var choice = mine ? post.MyReaction : emoji;
            button.Click += (_, _) =>
            {
                flyout.Hide();
                if (row is not null) React(post, row, choice, null);
                else ViewModel.React(post, choice);
            };
            chips.Children.Add(button);
        }
        var total = post.PostReactions.Sum(r => r.Count);
        var content = new StackPanel { Spacing = 12, Width = 380 };
        content.Children.Add(new TextBlock { Text = total == 1 ? "1 reaction" : $"{total:N0} reactions", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Themed.Brush("TextFillColorSecondaryBrush") });
        content.Children.Add(chips);
        flyout.Content = content;
        flyout.ShowAt(pill);
    }

    /// <summary>A post's forward pill: the chats to forward it to.</summary>
    private void ForwardPill_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Models.Message post }) return;
        e.Handled = true;
        _ = ForwardAsync([post]);
    }

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

    /// <summary>The rows on screen, by channel: one is made again only when what it shows changed.</summary>
    private readonly Dictionary<string, (ChannelDto Channel, bool Open, Button Row)> _channelRowCache = new();
    private TextBlock? _channelsFindTitle;

    private Button CachedChannelRow(ChannelDto channel)
    {
        var open = ViewModel.SelectedChat?.Id == channel.Id;
        if (_channelRowCache.TryGetValue(channel.Id, out var kept) && kept.Channel == channel && kept.Open == open) return kept.Row;
        var row = ChannelRow(channel);
        _channelRowCache[channel.Id] = (channel, open, row);
        return row;
    }

    private void FillChannels()
    {
        var rows = new List<UIElement>();
        BuildChannelRows(rows);
        // Nothing moved: the list is left alone (pictures don't blink).
        if (rows.SequenceEqual(ChannelRows.Children)) return;
        ChannelRows.Children.Clear();
        foreach (var row in rows) ChannelRows.Children.Add(row);
    }

    private void BuildChannelRows(List<UIElement> ChannelRows)
    {
        if (ViewModel.ChannelResults is { } results)
        {
            foreach (var channel in results) ChannelRows.Add(CachedChannelRow(channel));
            if (results.Count == 0) ChannelRows.Add(ChannelNote("No channels found."));
            return;
        }
        foreach (var channel in ViewModel.Channels) ChannelRows.Add(CachedChannelRow(channel));
        if (ViewModel.SuggestedChannels.Count > 0)
        {
            ChannelRows.Add(_channelsFindTitle ??= new TextBlock
            {
                Text = "Find channels to follow",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(14, 18, 0, 6),
                Foreground = Themed.Brush("TextFillColorSecondaryBrush"),
            });
            foreach (var channel in ViewModel.SuggestedChannels) ChannelRows.Add(CachedChannelRow(channel));
        }
        else if (ViewModel.Channels.Count == 0)
        {
            ChannelRows.Add(ChannelNote(ViewModel.IsLive ? "Loading channels…" : "No channels."));
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
        // Right-click: put it away, or stop (or start) following it.
        var menu = new MenuFlyout();
        var close = new MenuFlyoutItem { Text = "Close channel", Icon = new FontIcon { Glyph = char.ConvertFromUtf32(0xE711) }, IsEnabled = ViewModel.SelectedChat?.Id == channel.Id };
        close.Click += (_, _) => ViewModel.CloseChannel();
        menu.Items.Add(close);
        if (channel.Followed)
        {
            var mute = new MenuFlyoutItem { Text = channel.Muted ? "Unmute" : "Mute", Icon = new FontIcon { Glyph = channel.Muted ? BellGlyph : BellOffGlyph } };
            mute.Click += (_, _) => ViewModel.ChannelAction(channel, channel.Muted ? "unmute" : "mute");
            menu.Items.Add(mute);
        }
        var toggle = new MenuFlyoutItem { Text = channel.Followed ? "Unfollow" : "Follow", Icon = new FontIcon { Glyph = char.ConvertFromUtf32(channel.Followed ? 0xF3B1 : 0xE710) } };
        toggle.Click += (_, _) =>
        {
            if (channel.Followed && ViewModel.SelectedChat?.Id == channel.Id) ViewModel.CloseChannel();
            ViewModel.ChannelAction(channel, channel.Followed ? "unfollow" : "follow");
        };
        menu.Items.Add(toggle);
        row.ContextFlyout = menu;
        return row;
    }
}
