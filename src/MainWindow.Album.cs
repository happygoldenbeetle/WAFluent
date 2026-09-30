using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// An album opened up: every photo and video sent together, in a grid over the conversation
/// (the "+N" tile). A click opens one in the viewer; Esc or ✕
/// goes back to the chat.
/// </summary>
public sealed partial class MainWindow
{
    private void OpenAlbum(Message album)
    {
        if (album.AlbumItems is not { Count: > 0 } items) return;
        var photos = items.Count(i => i.Kind == MessageKind.Image);
        var videos = items.Count - photos;
        AlbumViewTitle.Text = album.IsOutgoing ? "You" : album.SenderName.Length > 0 ? album.SenderName : ViewModel.SelectedChat?.Name ?? "";
        AlbumViewSubtitle.Text = string.Join(", ", new[]
        {
            photos > 0 ? $"{photos} photo{(photos == 1 ? "" : "s")}" : null,
            videos > 0 ? $"{videos} video{(videos == 1 ? "" : "s")}" : null,
        }.Where(s => s is not null)) + (album.Timestamp == default ? "" : " · " + album.Timestamp.ToString("d MMM yyyy") + ", " + Format.Clock(album.Timestamp));

        AlbumViewGrid.Children.Clear();
        foreach (var item in items)
        {
            var tile = new Grid { Width = 182, Height = 182, Margin = new Thickness(4), CornerRadius = new CornerRadius(8), Background = Themed.Brush("FileCardBrush"), Tag = item };
            ImageSource? picture = item.MediaPath is { } path && item.Kind == MessageKind.Image && File.Exists(path)
                ? new BitmapImage(new Uri(path)) { DecodePixelWidth = 364 }
                : Ui.Thumb(item.Thumb);
            if (picture is not null)
                tile.Children.Add(new Border { Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill } });
            if (item.Kind == MessageKind.Video)
                tile.Children.Add(new Grid
                {
                    Width = 44, Height = 44,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)) },
                        new FontIcon { Glyph = "", FontSize = 16, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(3, 0, 0, 0) },
                    },
                });
            tile.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (item.Kind == MessageKind.Video) WhenDownloaded(item, _ => OpenVideo(item, tile));
                else if (item.MediaPath is { } p && File.Exists(p)) OpenViewer(item, tile);
            };
            tile.ContextRequested += Message_ContextRequested;
            AlbumViewGrid.Children.Add(tile);
        }
        AlbumView.Visibility = Visibility.Visible;
        AlbumView.Focus(FocusState.Programmatic);
    }

    private void CloseAlbum() => AlbumView.Visibility = Visibility.Collapsed;

    private void AlbumViewClose_Click(object sender, RoutedEventArgs e) => CloseAlbum();

    private void AlbumViewEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // The photo viewer over it closes first.
        if (AlbumView.Visibility != Visibility.Visible || Lightbox.Visibility == Visibility.Visible || VideoViewer.Visibility == Visibility.Visible) return;
        args.Handled = true;
        CloseAlbum();
    }
}
