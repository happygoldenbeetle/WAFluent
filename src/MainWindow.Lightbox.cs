using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Full-window photo viewer. Opens like macOS Quick Look: the picture springs out of its
/// chat bubble to full size while the background dims, and flies back in on close.
/// Zoom: Ctrl+wheel, pinch, double-click or buttons (greyed out at the limits);
/// ←/→ step through the chat's pictures; copy / save / open in Photos.
/// </summary>
public sealed partial class MainWindow
{
    private List<Message> _viewerItems = [];
    private int _viewerIndex;
    private Message? _viewerOpenedFrom;        // the message whose bubble the viewer grew out of
    private FrameworkElement? _viewerSource;   // that bubble's picture element
    private bool _viewerClosing;

    // ───────────── Open / close ─────────────

    private void Media_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message { MediaPath: { } path } message } element || !File.Exists(path)) return;
        e.Handled = true;
        OpenViewer(message, element);
    }

    private void OpenViewer(Message message, FrameworkElement source)
    {
        // Every downloaded picture in this chat, in order, so ←/→ can walk through them.
        _viewerItems = ViewModel.SelectedChat?.Messages
            .Where(m => m.Kind is MessageKind.Image or MessageKind.Sticker && m.MediaPath is { } p && File.Exists(p))
            .ToList() ?? [];
        if (!_viewerItems.Contains(message)) _viewerItems = [message];
        _viewerIndex = _viewerItems.IndexOf(message);
        _viewerOpenedFrom = message;
        _viewerSource = source;
        _viewerClosing = false;

        Lightbox.Visibility = Visibility.Visible;
        LightboxImage.Opacity = 0;   // the flying copy stands in until it lands
        ShowViewerItem(animateFrom: source);
        SetChrome(visible: true);
        LightboxScroller.Focus(FocusState.Programmatic);
    }

    private void CloseViewer()
    {
        if (Lightbox.Visibility != Visibility.Visible || _viewerClosing) return;
        _viewerClosing = true;
        SetChrome(visible: false);

        var current = _viewerItems.Count > 0 ? _viewerItems[_viewerIndex] : null;
        var canFlyBack = current == _viewerOpenedFrom && _viewerSource is { IsLoaded: true }
                         && LightboxScroller.ZoomFactor < 1.01 && IsOnScreen(_viewerSource);
        if (canFlyBack) FlyBack(_viewerSource!);
        else FadeAway();
    }

    private void FinishClose()
    {
        Lightbox.Visibility = Visibility.Collapsed;
        FlyImage.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;   // release the full-size bitmap
        FlyImage.Source = null;
        if (_viewerSource is not null) _viewerSource.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(LightboxImage).Scale = Vector3.One;
        _viewerSource = null;
        _viewerClosing = false;
    }

    /// <summary>Scrim, top bar, arrows and caption fade together (their OpacityTransitions).</summary>
    private void SetChrome(bool visible)
    {
        var o = visible ? 1 : 0;
        LightboxScrim.Opacity = o;
        LightboxBar.Opacity = o;
        LightboxCaption.Opacity = o;
        LightboxPrev.Opacity = o;
        LightboxNext.Opacity = o;
    }

    // ───────────── Showing a picture ─────────────

    private void ShowViewerItem(FrameworkElement? animateFrom = null)
    {
        var m = _viewerItems[_viewerIndex];
        var chat = ViewModel.SelectedChat;

        // Full resolution here (bubbles decode small copies) so zooming stays sharp.
        var bitmap = new BitmapImage();
        LightboxImage.Source = bitmap;
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
        UpdateZoomButtons();

        if (animateFrom is not null)
        {
            // Fly once the picture's final size is known.
            void Opened(object s, RoutedEventArgs e)
            {
                bitmap.ImageOpened -= Opened;
                FlyIn(animateFrom, bitmap);
            }
            bitmap.ImageOpened += Opened;
            bitmap.ImageFailed += (_, _) => LightboxImage.Opacity = 1;
        }
        else
        {
            LightboxImage.Opacity = 1;
        }
        bitmap.UriSource = new Uri(m.MediaPath!);
    }

    /// <summary>At zoom 1 the picture fits the space; zooming scales it past that.</summary>
    private void FitViewerImage()
    {
        LightboxImage.MaxWidth = Math.Max(0, LightboxScroller.ViewportWidth - 96);   // room for the arrows
        LightboxImage.MaxHeight = Math.Max(0, LightboxScroller.ViewportHeight - 24);
    }

    // ───────────── The Quick Look flight ─────────────

    /// <summary>Springs a copy of the picture from the thumbnail's rectangle to the viewer's.</summary>
    private void FlyIn(FrameworkElement from, BitmapImage bitmap)
    {
        Lightbox.UpdateLayout();
        var target = BoundsIn(LightboxImage, Lightbox);
        var source = BoundsIn(from, Lightbox);
        if (target.Width < 1 || source.Width < 1 || _viewerClosing)
        {
            LightboxImage.Opacity = 1;
            return;
        }

        PlaceFlyImage(bitmap, target);
        from.Opacity = 0;   // the picture has "left" its bubble

        var visual = FlyVisual(target);
        visual.Scale = new Vector3((float)(source.Width / target.Width), (float)(source.Height / target.Height), 1);
        visual.Properties.InsertVector3("Translation", Offset(source, target));

        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation", Spring(compositor, Vector3.Zero));
        visual.StartAnimation("Scale", Spring(compositor, Vector3.One));
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_viewerClosing) return;
            LightboxImage.Opacity = 1;          // hand over to the zoomable picture
            FlyImage.Visibility = Visibility.Collapsed;
            from.Opacity = 1;
        });
    }

    /// <summary>The reverse: shrinks the picture back into its bubble.</summary>
    private void FlyBack(FrameworkElement to)
    {
        var from = BoundsIn(LightboxImage, Lightbox);
        var target = BoundsIn(to, Lightbox);
        if (from.Width < 1 || LightboxImage.Source is not BitmapImage bitmap)
        {
            FadeAway();
            return;
        }

        PlaceFlyImage(bitmap, from);
        LightboxImage.Opacity = 0;
        to.Opacity = 0;

        var visual = FlyVisual(from);
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);

        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.2f, 1f));
        Vector3KeyFrameAnimation To(Vector3 value)
        {
            var a = compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1f, value, ease);
            a.Duration = TimeSpan.FromMilliseconds(260);
            return a;
        }

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation", To(Offset(target, from)));
        visual.StartAnimation("Scale", To(new Vector3((float)(target.Width / from.Width), (float)(target.Height / from.Height), 1)));
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(FinishClose);
    }

    /// <summary>When there's no bubble to return to: shrink slightly and fade.</summary>
    private void FadeAway()
    {
        var visual = ElementCompositionPreview.GetElementVisual(LightboxImage);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)LightboxImage.ActualWidth / 2, (float)LightboxImage.ActualHeight / 2, 0);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1));
        scale.Duration = TimeSpan.FromMilliseconds(180);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(180);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            FinishClose();
        });
    }

    private void PlaceFlyImage(BitmapImage bitmap, Rect rect)
    {
        FlyImage.Source = bitmap;
        FlyImage.Width = rect.Width;
        FlyImage.Height = rect.Height;
        Canvas.SetLeft(FlyImage, rect.X);
        Canvas.SetTop(FlyImage, rect.Y);
        FlyImage.Visibility = Visibility.Visible;
    }

    /// <summary>FlyImage's composition visual, scaling around its centre, with Translation enabled.</summary>
    private Visual FlyVisual(Rect rect)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(FlyImage, true);
        var visual = ElementCompositionPreview.GetElementVisual(FlyImage);
        visual.CenterPoint = new Vector3((float)rect.Width / 2, (float)rect.Height / 2, 0);
        return visual;
    }

    /// <summary>Slightly bouncy, quick — close to Quick Look's feel.</summary>
    private static SpringVector3NaturalMotionAnimation Spring(Compositor compositor, Vector3 to)
    {
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = to;
        spring.DampingRatio = 0.72f;
        spring.Period = TimeSpan.FromMilliseconds(48);
        return spring;
    }

    /// <summary>Translation moving the centre of <paramref name="at"/> onto the centre of <paramref name="from"/>.</summary>
    private static Vector3 Offset(Rect from, Rect at) => new(
        (float)(from.X + from.Width / 2 - (at.X + at.Width / 2)),
        (float)(from.Y + from.Height / 2 - (at.Y + at.Height / 2)),
        0);

    private static Rect BoundsIn(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    /// <summary>The bubble is still visible in the conversation (not scrolled away).</summary>
    private bool IsOnScreen(FrameworkElement element)
    {
        var r = BoundsIn(element, MessagesScroller);
        return r.Bottom > 0 && r.Top < MessagesScroller.ActualHeight;
    }

    // ───────────── Navigation & zoom ─────────────

    private void Step(int delta)
    {
        var next = _viewerIndex + delta;
        if (Lightbox.Visibility != Visibility.Visible || _viewerClosing || next < 0 || next >= _viewerItems.Count) return;
        if (_viewerSource is not null) _viewerSource.Opacity = 1;
        FlyImage.Visibility = Visibility.Collapsed;
        _viewerIndex = next;
        ShowViewerItem();
    }

    private void Zoom(double factor)
    {
        var target = Math.Clamp(LightboxScroller.ZoomFactor * factor, LightboxScroller.MinZoomFactor, LightboxScroller.MaxZoomFactor);
        LightboxScroller.ChangeView(null, null, (float)target);
    }

    /// <summary>Zoom out is unavailable at fit, zoom in at the maximum.</summary>
    private void UpdateZoomButtons()
    {
        var z = LightboxScroller.ZoomFactor;
        LightboxZoomOutButton.IsEnabled = z > LightboxScroller.MinZoomFactor + 0.01;
        LightboxZoomInButton.IsEnabled = z < LightboxScroller.MaxZoomFactor - 0.01;
    }

    // ───────────── Event handlers ─────────────

    private void LightboxScroller_SizeChanged(object sender, SizeChangedEventArgs e) => FitViewerImage();

    private void LightboxScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateZoomButtons();

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
