using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Full-window photo viewer: opens on a picture click, zooms (Ctrl+wheel, pinch, double-click,
/// buttons), steps through the chat's pictures with ←/→, and copies/saves/opens the file.
/// </summary>
public sealed partial class MainWindow
{
    private List<Message> _viewerItems = [];
    private int _viewerIndex;
    private DispatcherQueueTimer? _viewerHide;

    private void Media_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Message { MediaPath: { } path } message || !File.Exists(path)) return;
        e.Handled = true;
        OpenViewer(message);
    }

    private void OpenViewer(Message message)
    {
        // Every downloaded picture in this chat, in order, so ←/→ can walk through them.
        _viewerItems = ViewModel.SelectedChat?.Messages
            .Where(m => m.Kind is MessageKind.Image or MessageKind.Sticker && m.MediaPath is { } p && File.Exists(p))
            .ToList() ?? [];
        if (!_viewerItems.Contains(message)) _viewerItems = [message];
        _viewerIndex = _viewerItems.IndexOf(message);

        _viewerHide?.Stop();
        ShowViewerItem();
        Lightbox.Visibility = Visibility.Visible;
        Lightbox.Opacity = 1;
        LightboxScroller.Focus(FocusState.Programmatic);
    }

    private void CloseViewer()
    {
        if (Lightbox.Visibility != Visibility.Visible) return;
        Lightbox.Opacity = 0;   // fades via the OpacityTransition
        _viewerHide ??= CreateHideTimer();
        _viewerHide.Start();
    }

    private DispatcherQueueTimer CreateHideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(160);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Lightbox.Visibility = Visibility.Collapsed;
            LightboxImage.Source = null;   // release the full-size bitmap
        };
        return timer;
    }

    private void ShowViewerItem()
    {
        var m = _viewerItems[_viewerIndex];
        var chat = ViewModel.SelectedChat;

        // Full resolution here (bubbles decode small copies) so zooming stays sharp.
        LightboxImage.Source = new BitmapImage(new Uri(m.MediaPath!));
        LightboxScroller.ChangeView(0, 0, 1, disableAnimation: true);

        var sender = m.IsOutgoing ? "You" : m.SenderName.Length > 0 ? m.SenderName : chat?.Name ?? "";
        LightboxSender.Text = sender;
        LightboxAvatar.DisplayName = sender;
        LightboxAvatar.Source = !m.IsOutgoing && chat is { IsGroup: false } ? chat.AvatarPath : null;
        LightboxWhen.Text = $"{Format.DayLabel(m.Timestamp)} at {m.Time}";
        LightboxCaption.Text = m.Kind == MessageKind.Image ? m.Text : "";
        LightboxCaption.Visibility = LightboxCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        LightboxPrev.Visibility = _viewerIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightboxNext.Visibility = _viewerIndex < _viewerItems.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        FitViewerImage();
    }

    /// <summary>At zoom 1 the picture fits the space; zooming scales it past that.</summary>
    private void FitViewerImage()
    {
        LightboxImage.MaxWidth = Math.Max(0, LightboxScroller.ViewportWidth - 96);   // room for the arrows
        LightboxImage.MaxHeight = Math.Max(0, LightboxScroller.ViewportHeight - 24);
    }

    private void Step(int delta)
    {
        var next = _viewerIndex + delta;
        if (Lightbox.Visibility != Visibility.Visible || next < 0 || next >= _viewerItems.Count) return;
        _viewerIndex = next;
        ShowViewerItem();
    }

    private void Zoom(double factor)
    {
        var target = Math.Clamp(LightboxScroller.ZoomFactor * factor, LightboxScroller.MinZoomFactor, LightboxScroller.MaxZoomFactor);
        LightboxScroller.ChangeView(null, null, (float)target);
    }

    // ───────────── Event handlers ─────────────

    private void LightboxScroller_SizeChanged(object sender, SizeChangedEventArgs e) => FitViewerImage();

    /// <summary>Clicks on the dimmed area close the viewer; the picture and bars swallow theirs.</summary>
    private void Lightbox_BackgroundTapped(object sender, TappedRoutedEventArgs e) => CloseViewer();

    private void LightboxEat_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void LightboxImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (LightboxScroller.ZoomFactor > 1.05)
        {
            LightboxScroller.ChangeView(0, 0, 1);
            return;
        }
        // Zoom in around the point that was double-clicked.
        const float zoom = 2.5f;
        var p = e.GetPosition(LightboxScroller);
        LightboxScroller.ChangeView(p.X * zoom - LightboxScroller.ViewportWidth / 2,
                                    p.Y * zoom - LightboxScroller.ViewportHeight / 2, zoom);
    }

    private void LightboxZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.5);
    private void LightboxZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.5);
    private void LightboxPrev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void LightboxNext_Click(object sender, RoutedEventArgs e) => Step(1);
    private void LightboxClose_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private void LightboxClose_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseViewer();
    }

    private void LightboxPrev_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Step(-1);
    }

    private void LightboxNext_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Step(1);
    }

    private void LightboxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerPath() is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void LightboxCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerPath() is not { } path) return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));   // paste into chats/editors
        data.SetStorageItems([file]);                                        // paste into Explorer
        Clipboard.SetContent(data);
        FlashViewerStatus("Copied");
    }

    private async void LightboxSave_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerPath() is not { } path) return;
        var m = _viewerItems[_viewerIndex];
        var ext = Path.GetExtension(path);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"WhatsApp Image {m.Timestamp:yyyy-MM-dd 'at' HH.mm.ss}",
        };
        picker.FileTypeChoices.Add("Image", [ext]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var target = await picker.PickSaveFileAsync();
        if (target is null) return;
        File.Copy(path, target.Path, overwrite: true);
        FlashViewerStatus("Saved");
    }

    private string? CurrentViewerPath() =>
        _viewerItems.Count > 0 && _viewerItems[_viewerIndex].MediaPath is { } p && File.Exists(p) ? p : null;

    /// <summary>Briefly shows "Copied"/"Saved" in place of the date line.</summary>
    private async void FlashViewerStatus(string text)
    {
        var original = LightboxWhen.Text;
        LightboxWhen.Text = text;
        await Task.Delay(1500);
        if (LightboxWhen.Text == text) LightboxWhen.Text = original;
    }
}
