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

        if (Math.Abs(was.Fraction - option.Fraction) > 0.001 && row.FindName("PollScale") is Microsoft.UI.Xaml.Media.ScaleTransform scale)
        {
            var grow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = was.Fraction,
                To = option.Fraction,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(grow, scale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(grow, "ScaleX");
            var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            story.Children.Add(grow);
            story.Begin();
        }
        if (option.Selected && !was.Selected && row.FindName("PollCheck") is UIElement fill && row.FindName("PollTick") is UIElement tick)
            FillCheck(fill, tick);
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
