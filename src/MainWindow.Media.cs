using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// Clicks on the richer bubbles: videos and GIFs (download, then play in the video viewer),
/// documents (Open / Save As), locations (maps), link previews and contact cards.
/// </summary>
public sealed partial class MainWindow
{
    // ───────────── Attach menu ─────────────

    /// <summary>
    /// The attach menu's icons, filled like WhatsApp's, on a 16 px grid (Segoe Fluent Icons has
    /// no filled photo, camera, headphones or poll). F0 = even-odd, for the cut-outs.
    /// </summary>
    private static readonly (string Text, string Icon, uint Color)[] AttachItems =
    [
        ("Document", "F0 M4.2,1 H9 V3.8 A1.2,1.2 0 0 0 10.2,5 H13 V13.8 A1.2,1.2 0 0 1 11.8,15 H4.2 A1.2,1.2 0 0 1 3,13.8 V2.2 A1.2,1.2 0 0 1 4.2,1 Z " +
                     "M5.4,8.2 H10.6 V9.3 H5.4 Z M5.4,10.8 H10.6 V11.9 H5.4 Z M10.2,1.3 L12.7,3.8 H10.7 A0.5,0.5 0 0 1 10.2,3.3 Z", 0xFF7F66FF),
        ("Photos & videos", "F0 M3.2,1.8 H12.8 A2.2,2.2 0 0 1 15,4 V12 A2.2,2.2 0 0 1 12.8,14.2 H3.2 A2.2,2.2 0 0 1 1,12 V4 A2.2,2.2 0 0 1 3.2,1.8 Z " +
                            "M3,12.2 L6.3,8.3 L8.4,10.6 L10.4,8.2 L13,12.2 Z M10.8,3.9 A1.5,1.5 0 1 1 10.79,3.9 Z", 0xFF007BFC),
        ("Camera", "F0 M5.8,2.2 H10.2 A1,1 0 0 1 11.1,2.8 L11.7,4 H13.4 A1.6,1.6 0 0 1 15,5.6 V12.4 A1.6,1.6 0 0 1 13.4,14 H2.6 A1.6,1.6 0 0 1 1,12.4 V5.6 " +
                   "A1.6,1.6 0 0 1 2.6,4 H4.3 L4.9,2.8 A1,1 0 0 1 5.8,2.2 Z M8,5.6 A3.1,3.1 0 1 1 7.99,5.6 Z M8,7.3 A1.4,1.4 0 1 1 7.99,7.3 Z", 0xFFFF2E74),
        ("Audio", "M8,1.8 A6.2,6.2 0 0 1 14.2,8 V12.6 A1.6,1.6 0 0 1 12.6,14.2 H11.6 A1.1,1.1 0 0 1 10.5,13.1 V9.6 A1.1,1.1 0 0 1 11.6,8.5 H12.6 " +
                  "A4.6,4.6 0 0 0 3.4,8.5 H4.4 A1.1,1.1 0 0 1 5.5,9.6 V13.1 A1.1,1.1 0 0 1 4.4,14.2 H3.4 A1.6,1.6 0 0 1 1.8,12.6 V8 A6.2,6.2 0 0 1 8,1.8 Z", 0xFFFA6533),
        ("Contact", "M8,1.3 A3.1,3.1 0 1 1 7.99,1.3 Z M2.4,13.9 C2.4,10.6 4.9,8.9 8,8.9 C11.1,8.9 13.6,10.6 13.6,13.9 A0.9,0.9 0 0 1 12.7,14.8 H3.3 A0.9,0.9 0 0 1 2.4,13.9 Z", 0xFF009DE2),
        ("Poll", "M2.3,2.2 H9.7 A1.3,1.3 0 0 1 9.7,4.8 H2.3 A1.3,1.3 0 0 1 2.3,2.2 Z M2.3,6.7 H13.7 A1.3,1.3 0 0 1 13.7,9.3 H2.3 A1.3,1.3 0 0 1 2.3,6.7 Z " +
                 "M2.3,11.2 H6.7 A1.3,1.3 0 0 1 6.7,13.8 H2.3 A1.3,1.3 0 0 1 2.3,11.2 Z", 0xFFFFBC38),
        ("Event", "F0 M4.3,0.6 H5.7 V2.5 H4.3 Z M10.3,0.6 H11.7 V2.5 H10.3 Z M3.2,2.5 H12.8 A2.2,2.2 0 0 1 15,4.7 V12.8 A2.2,2.2 0 0 1 12.8,15 H3.2 A2.2,2.2 0 0 1 1,12.8 V4.7 " +
                  "A2.2,2.2 0 0 1 3.2,2.5 Z M2.6,5.6 H13.4 V6.5 H2.6 Z M4.2,8.5 H5.8 V10.1 H4.2 Z M7.2,8.5 H8.8 V10.1 H7.2 Z M10.2,8.5 H11.8 V10.1 H10.2 Z M4.2,11.4 H5.8 V13 H4.2 Z M7.2,11.4 H8.8 V13 H7.2 Z", 0xFFFF2E74),
        ("New sticker", "M3.8,0.8 H10.6 A3.2,3.2 0 0 1 13.8,4 V8.2 H11.2 A3.1,3.1 0 0 0 8.1,11.3 V13.9 H3.8 A3.2,3.2 0 0 1 0.6,10.7 V4 A3.2,3.2 0 0 1 3.8,0.8 Z " +
                        "M9.3,13.7 V11.5 A2.1,2.1 0 0 1 11.4,9.4 H13.6 Z", 0xFF02A698),
    ];

    /// <summary>
    /// The + button: WhatsApp's attach menu, filled icons in their colours on a solid panel.
    /// The items don't send anything yet (sending media is still to come).
    /// </summary>
    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
        };
        menu.MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["AttachMenuPresenterStyle"];
        var itemStyle = (Style)Application.Current.Resources["AttachMenuItemStyle"];
        foreach (var (text, data, color) in AttachItems)
        {
            var icon = new PathIcon
            {
                Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), data),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                    (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            menu.Items.Add(new MenuFlyoutItem { Text = text, Icon = icon, Style = itemStyle });
        }
        menu.ShowAt(AttachButton);
    }

    // ───────────── Stickers and GIFs ─────────────

    private Flyout? _stickerFlyout;
    private Controls.StickerPanel? _stickerPanel;

    private void SetupStickers()
    {
        StickerButton.Content = Controls.StickerPanel.StickerGlyph();
        if (_core is null) return;
        _core.Stickers += (favorites, stickers, gifs) => _stickerPanel?.SetItems(favorites, stickers, gifs);
        _core.MediaReceived += (chatId, messageId, path) => _stickerPanel?.MediaArrived(chatId, messageId, path);
        _core.MediaFailed += (chatId, messageId, _) => _stickerPanel?.MediaFailed(chatId, messageId);
        _core.FavoritesChanged += () => { if (_stickerFlyout?.IsOpen == true) _core.LoadStickers(); };
    }

    private void Stickers_Click(object sender, RoutedEventArgs e)
    {
        if (_stickerFlyout is null)
        {
            _stickerPanel = new Controls.StickerPanel();
            _stickerPanel.DownloadWanted += item => _core?.DownloadMedia(item.ChatId, item.MessageId);
            _stickerPanel.GifPicked += SendGif;
            _stickerPanel.Picked += item =>
            {
                _stickerFlyout?.Hide();
                if (ViewModel.SelectedChat is { Id.Length: > 0 } chat) _core?.SendStored(item.ChatId, item.MessageId, chat.Id);
                ComposerBox.Focus(FocusState.Programmatic);
            };
            var presenter = new Style(typeof(FlyoutPresenter));
            presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 1000.0));
            presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 1000.0));
            presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            _stickerFlyout = new Flyout
            {
                Content = _stickerPanel,
                FlyoutPresenterStyle = presenter,
                ShouldConstrainToRootBounds = true,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft,
            };
        }
        _stickerPanel!.GiphyKey = _ui.GiphyKey.Trim() is { Length: > 0 } own ? own : Services.Giphy.BuiltInKey;
        _stickerPanel.Prefetch();
        _core?.LoadStickers();   // fresh recents each time
        _stickerFlyout.ShowAt(StickerButton);
    }

    /// <summary>A GIF from search: fetched as MP4 (with a preview), then uploaded and sent by the core.</summary>
    private async void SendGif(Services.Giphy.Gif gif)
    {
        _stickerFlyout?.Hide();
        ComposerBox.Focus(FocusState.Programmatic);
        if (ViewModel.SelectedChat is not { Id.Length: > 0 } chat || _core is null) return;
        try
        {
            var (mp4, thumb) = await Services.Giphy.DownloadAsync(gif);
            _core.SendGif(chat.Id, mp4, gif.Width, gif.Height, thumb);
        }
        catch (Exception)
        {
            ShowToast(false, "Couldn't get that GIF from GIPHY. Check your connection.");
        }
    }

    // ───────────── Downloads on demand ─────────────

    /// <summary>
    /// Runs <paramref name="then"/> with the file once it's on this PC: right away if it is,
    /// otherwise after downloading it (videos and documents don't download by themselves).
    /// </summary>
    private void WhenDownloaded(Message m, Action<string> then)
    {
        if (MediaActions.Exists(m))
        {
            then(m.MediaPath!);
            return;
        }
        if (!m.HasMedia)
        {
            ShowToast(false, "This file isn't available on this PC yet. Open WhatsApp on your phone to get it.");
            return;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (m.MediaPath is { } path)
            {
                m.PropertyChanged -= OnChanged;
                then(path);
            }
            else if (m.MediaFailed)
            {
                m.PropertyChanged -= OnChanged;
                ShowToast(false, "Couldn't download it. Your phone needs to be online.");
            }
        }
        m.PropertyChanged += OnChanged;
        ViewModel.Download(m);
    }

    // ───────────── Videos and GIFs ─────────────

    private void Video_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelecting || sender is not FrameworkElement { Tag: Message m }) return;
        e.Handled = true;
        if (m.IsMediaLoading) return;   // already on its way; it opens when it lands
        WhenDownloaded(m, _ => OpenVideo(m));
    }

    private void OpenVideo(Message m)
    {
        if (m.MediaPath is not { } path) return;
        AudioPlayback.Stop();   // one thing playing at a time
        VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
        VideoPlayer.AreTransportControlsEnabled = !m.IsGif;
        if (VideoPlayer.MediaPlayer is { } player)
        {
            player.IsLoopingEnabled = m.IsGif;
            player.IsMuted = m.IsGif;
        }
        VideoViewer.Visibility = Visibility.Visible;
        VideoPlayer.Focus(FocusState.Programmatic);
    }

    private void CloseVideo()
    {
        VideoPlayer.MediaPlayer?.Pause();
        VideoPlayer.Source = null;
        VideoViewer.Visibility = Visibility.Collapsed;
    }

    private void VideoClose_Click(object sender, RoutedEventArgs e) => CloseVideo();

    private void VideoClose_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        CloseVideo();
    }

    // ───────────── Documents ─────────────

    private void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Message m }) WhenDownloaded(m, MediaActions.OpenExternally);
    }

    private void FileSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Message m }) WhenDownloaded(m, path => _ = MediaActions.SaveAsAsync(this, path, m));
    }

    // ───────────── Locations and links ─────────────

    private static string Coordinates(Message m) =>
        string.Create(CultureInfo.InvariantCulture, $"{m.Latitude:0.######}, {m.Longitude:0.######}");

    /// <summary>The place in Google Maps (in the browser, or the Maps app if it handles the link).</summary>
    private static void OpenLocation(Message m)
    {
        if (m.Latitude == 0 && m.Longitude == 0) return;
        var query = Uri.EscapeDataString(Coordinates(m).Replace(" ", ""));
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.google.com/maps/search/?api=1&query={query}"));
    }

    private void Location_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelecting || sender is not FrameworkElement { Tag: Message m }) return;
        e.Handled = true;
        OpenLocation(m);
    }

    private void Link_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelecting || sender is not FrameworkElement { Tag: Message { HasLink: true } m }) return;
        e.Handled = true;
        var url = m.LinkUrl.Contains("://") ? m.LinkUrl : "https://" + m.LinkUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }

    // ───────────── Contact cards ─────────────

    /// <summary>"Message": opens your chat with them, if you have one.</summary>
    private void ContactMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message m }) return;
        var card = m.Contacts.FirstOrDefault(c => c.Phones.Count > 0);
        if (card is null) return;
        // An existing chat opens; a number you've never messaged starts a new chat.
        var existing = card.Phones.Select(ViewModel.FindChatByPhone).FirstOrDefault(c => c is not null);
        ViewModel.OpenNumber(existing is not null ? card.Phones.First(p => ViewModel.FindChatByPhone(p) == existing) : card.Phone);
    }

    // ───────────── Polls ─────────────

    /// <summary>What each option last showed: (poll, option) -> (bar length, yours).</summary>
    private static readonly Dictionary<(string Poll, string Option), (double Fraction, bool Selected)> PollShown = new();

    /// <summary>
    /// The poll bubble is rebuilt when a vote lands; animate from what was on screen: bars
    /// slide to their new length and a new tick pops in. Rows merely scrolling back into view
    /// (nothing changed) and first sightings stay still.
    /// </summary>
    private void PollOption_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PollOption option } row) return;
        var key = (option.Owner.Id, option.Name);
        var seen = PollShown.TryGetValue(key, out var was);
        PollShown[key] = (option.Fraction, option.Selected);
        if (!seen) return;

        if (Math.Abs(was.Fraction - option.Fraction) > 0.001 && row.FindName("PollTrack") is FrameworkElement track)
        {
            _pollBarFrom[track] = was.Fraction;   // animates once the track has its width
            if (track.ActualWidth > 0) SizePollBar(track, option);
        }
        if (option.Selected && !was.Selected && row.FindName("PollCheck") is UIElement fill && row.FindName("PollTick") is UIElement tick)
            FillCheck(fill, tick);
    }

    /// <summary>Bars waiting to slide from their old length (a fraction) once they have a width.</summary>
    private readonly Dictionary<FrameworkElement, double> _pollBarFrom = new();

    private void PollTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: null } track && FindPollOption(track) is { } option) SizePollBar(track, option);
    }

    private static PollOption? FindPollOption(FrameworkElement element)
    {
        for (DependencyObject? d = element; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: PollOption option }) return option;
        return null;
    }

    /// <summary>The fill's width is its share of the track; a pending change slides there from the old share.</summary>
    private void SizePollBar(FrameworkElement track, PollOption option)
    {
        if (track.FindName("PollFill") is not FrameworkElement fill || track.ActualWidth <= 0) return;
        var to = Math.Round(track.ActualWidth * option.Fraction);
        if (_pollBarFrom.Remove(track, out var from))
        {
            var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = Math.Round(track.ActualWidth * from),
                To = to,
                Duration = TimeSpan.FromMilliseconds(420),
                EnableDependentAnimation = true,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, fill);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "Width");
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            story.Children.Add(slide);
            story.Completed += (_, _) => { story.Stop(); fill.Width = Math.Round(track.ActualWidth * option.Fraction); };
            story.Begin();
            return;
        }
        fill.Width = to;
    }

    /// <summary>The green fill grows out from the middle of the hollow circle; the tick fades in as it fills.</summary>
    private static void FillCheck(UIElement fill, UIElement tick)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(fill);
        var compositor = visual.Compositor;
        var easeOut = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.3f, 1f));
        visual.CenterPoint = new System.Numerics.Vector3(10, 10, 0);
        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new System.Numerics.Vector3(0, 0, 1));
        grow.InsertKeyFrame(1, System.Numerics.Vector3.One, easeOut);
        grow.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Scale", grow);

        var tickVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(tick);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.55f, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(320);
        tickVisual.StartAnimation("Opacity", fade);
    }

    private void PollOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelecting || sender is not FrameworkElement { Tag: PollOption option }) return;
        e.Handled = true;
        ViewModel.VotePoll(option);
    }

    /// <summary>"View votes": every option with who picked it.</summary>
    private void PollVotes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message m } button) return;
        var list = new StackPanel { Spacing = 14, Width = 300 };
        list.Children.Add(new TextBlock { Text = m.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        foreach (var option in m.PollOptions)
        {
            var block = new StackPanel { Spacing = 4 };
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = option.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 60, 0) });
            header.Children.Add(new TextBlock
            {
                Text = option.Count == 1 ? "1 vote" : $"{option.Count} votes",
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Themed.Brush("MetaTextBrush"),
                FontSize = 12,
            });
            block.Children.Add(header);
            foreach (var voter in option.Voters)
            {
                var name = new Controls.Redact { HorizontalAlignment = HorizontalAlignment.Left };
                name.Children.Add(new TextBlock { Text = voter, FontSize = 13 });
                block.Children.Add(name);
            }
            if (option.Count == 0)
                block.Children.Add(new TextBlock { Text = "No votes", FontSize = 13, Foreground = Themed.Brush("MetaTextBrush") });
            list.Children.Add(block);
        }
        var flyout = new Flyout
        {
            Content = new ScrollViewer { Content = list, MaxHeight = 420, Padding = new Thickness(0, 0, 12, 0) },
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right,
        };
        flyout.ShowAt(button);
    }

    /// <summary>"Copy number" (one contact) or "View all" (a menu of everyone's numbers).</summary>
    private void ContactMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message m } button) return;
        if (m.Contacts.Count == 1)
        {
            MediaActions.CopyText(m.Contacts[0].Phone);
            ShowToast(true, "Number copied");
            return;
        }
        var menu = new MenuFlyout();
        foreach (var card in m.Contacts)
        {
            var item = new MenuFlyoutItem
            {
                Text = card.Phones.Count > 0 ? $"{card.Name}  ·  {card.Phone}" : card.Name,
                Icon = new FontIcon { Glyph = Glyphs.Contact },
                IsEnabled = card.Phones.Count > 0,
            };
            item.Click += (_, _) =>
            {
                MediaActions.CopyText(card.Phone);
                ShowToast(true, $"{card.Name}'s number copied");
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }
}
