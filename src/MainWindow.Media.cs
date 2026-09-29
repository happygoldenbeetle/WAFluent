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
        var chat = card.Phones.Select(ViewModel.FindChatByPhone).FirstOrDefault(c => c is not null);
        if (chat is null)
        {
            ShowToast(false, $"You don't have a chat with {card.Name} yet.");
            return;
        }
        ViewModel.SelectedChat = chat;
        ChatList.SelectedItem = chat;
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
