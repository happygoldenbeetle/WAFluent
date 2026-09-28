using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Full-window photo viewer. Opens like macOS Quick Look: the picture springs out of its
/// chat bubble to full size while the background dims, and flies back in on close.
/// Zoom: Ctrl+wheel, pinch, double-click, Ctrl+/Ctrl-; right-click for zoom/copy/save/open;
/// ←/→ step through the chat's pictures; Esc or a click outside closes.
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
        if (ViewModel.IsSelecting) return;   // in select mode a tap picks the message instead
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

        EnsureViewerSetup();
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

    /// <summary>Scrim, arrows and caption fade together (their OpacityTransitions).</summary>
    private void SetChrome(bool visible)
    {
        var o = visible ? 1 : 0;
        LightboxScrim.Opacity = o;
        LightboxCaption.Opacity = o;
        LightboxPrev.Opacity = o;
        LightboxNext.Opacity = o;
    }

    // ───────────── Showing a picture ─────────────

    private void ShowViewerItem(FrameworkElement? animateFrom = null)
    {
        var m = _viewerItems[_viewerIndex];

        // Full resolution here (bubbles decode small copies) so zooming stays sharp.
        var bitmap = new BitmapImage();
        LightboxImage.Source = bitmap;
        LightboxScroller.ChangeView(0, 0, 1, disableAnimation: true);

        LightboxCaption.Text = m.Kind == MessageKind.Image ? m.Text : "";
        LightboxCaption.Visibility = LightboxCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightboxPrev.Visibility = _viewerIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightboxNext.Visibility = _viewerIndex < _viewerItems.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        FitViewerImage();

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

    // The picture is never stretched. A bubble shows it cropped to fill its box (stickers: whole),
    // so the flight scales the full picture *uniformly* and animates a rounded crop window
    // between "exactly what the bubble shows" and "the whole picture". First and last frames
    // then match the thumbnail and the viewer pixel for pixel — no snap at either end.

    private const float BubbleCornerRadius = 6f;   // the bubble picture's Border.CornerRadius

    /// <summary>Scale/position of the full picture plus the part of it that shows (in its own coordinates).</summary>
    private readonly record struct Pose(float Scale, Vector3 Translation, Vector2 ClipOffset, Vector2 ClipSize, float Radius);

    /// <summary>How the full picture (laid out at <paramref name="full"/>) looks as its bubble.</summary>
    private static Pose BubblePose(Rect bubble, Rect full, bool cropped)
    {
        var fill = Math.Max(bubble.Width / full.Width, bubble.Height / full.Height);   // UniformToFill
        var fit = Math.Min(bubble.Width / full.Width, bubble.Height / full.Height);    // Uniform
        var s = cropped ? fill : fit;
        var clip = cropped ? new Vector2((float)(bubble.Width / s), (float)(bubble.Height / s))
                           : new Vector2((float)full.Width, (float)full.Height);
        var offset = new Vector2(((float)full.Width - clip.X) / 2, ((float)full.Height - clip.Y) / 2);   // centred crop
        return new((float)s, Offset(bubble, full), offset, clip, cropped ? BubbleCornerRadius / (float)s : 0);
    }

    private static Pose FullPose(Rect full) =>
        new(1, Vector3.Zero, Vector2.Zero, new((float)full.Width, (float)full.Height), CornerRadius);

    /// <summary>Springs the picture out of its bubble into the viewer.</summary>
    private void FlyIn(FrameworkElement from, BitmapImage bitmap)
    {
        Lightbox.UpdateLayout();
        var full = BoundsIn(LightboxImage, Lightbox);
        var bubble = BoundsIn(from, Lightbox);
        if (full.Width < 1 || bubble.Width < 1 || _viewerClosing)
        {
            LightboxImage.Opacity = 1;
            return;
        }

        var cropped = _viewerItems[_viewerIndex].Kind == MessageKind.Image;
        var (visual, clip) = StartFlight(bitmap, full, BubblePose(bubble, full, cropped));
        from.Opacity = 0;   // the picture has "left" its bubble

        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        AnimateTo(visual, clip, FullPose(full), spring: true);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_viewerClosing) return;
            LightboxImage.Opacity = 1;          // hand over to the zoomable picture
            FlyImage.Visibility = Visibility.Collapsed;
            from.Opacity = 1;
        });
    }

    /// <summary>The reverse: the picture shrinks back into its bubble, cropping as it goes.</summary>
    private void FlyBack(FrameworkElement to)
    {
        var full = BoundsIn(LightboxImage, Lightbox);
        var bubble = BoundsIn(to, Lightbox);
        if (full.Width < 1 || LightboxImage.Source is not BitmapImage bitmap)
        {
            FadeAway();
            return;
        }

        var cropped = _viewerItems[_viewerIndex].Kind == MessageKind.Image;
        var (visual, clip) = StartFlight(bitmap, full, FullPose(full));
        LightboxImage.Opacity = 0;
        to.Opacity = 0;

        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        AnimateTo(visual, clip, BubblePose(bubble, full, cropped), spring: false);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(FinishClose);
    }

    /// <summary>Shows FlyImage at the full picture's layout rect, set to <paramref name="pose"/>.</summary>
    private (Visual Visual, CompositionRoundedRectangleGeometry Clip) StartFlight(BitmapImage bitmap, Rect full, Pose pose)
    {
        FlyImage.Source = bitmap;
        FlyImage.Width = full.Width;
        FlyImage.Height = full.Height;
        Canvas.SetLeft(FlyImage, full.X);
        Canvas.SetTop(FlyImage, full.Y);
        FlyImage.Visibility = Visibility.Visible;

        ElementCompositionPreview.SetIsTranslationEnabled(FlyImage, true);
        var visual = ElementCompositionPreview.GetElementVisual(FlyImage);
        visual.CenterPoint = new Vector3((float)full.Width / 2, (float)full.Height / 2, 0);

        var clip = visual.Compositor.CreateRoundedRectangleGeometry();
        visual.Clip = visual.Compositor.CreateGeometricClip(clip);

        visual.Scale = new Vector3(pose.Scale, pose.Scale, 1);
        visual.Properties.InsertVector3("Translation", pose.Translation);
        clip.Offset = pose.ClipOffset;
        clip.Size = pose.ClipSize;
        clip.CornerRadius = new Vector2(pose.Radius);
        return (visual, clip);
    }

    /// <summary>Scale, position, crop window and corner radius all move together, on one curve.</summary>
    private static void AnimateTo(Visual visual, CompositionRoundedRectangleGeometry clip, Pose pose, bool spring)
    {
        var c = visual.Compositor;
        visual.StartAnimation("Scale", spring ? Spring(c, new Vector3(pose.Scale, pose.Scale, 1)) : Ease(c, new Vector3(pose.Scale, pose.Scale, 1)));
        visual.StartAnimation("Translation", spring ? Spring(c, pose.Translation) : Ease(c, pose.Translation));
        clip.StartAnimation("Offset", spring ? Spring(c, pose.ClipOffset) : Ease(c, pose.ClipOffset));
        clip.StartAnimation("Size", spring ? Spring(c, pose.ClipSize) : Ease(c, pose.ClipSize));
        clip.StartAnimation("CornerRadius", spring ? Spring(c, new Vector2(pose.Radius)) : Ease(c, new Vector2(pose.Radius)));
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

    private const float CornerRadius = 12f;   // about what Quick Look uses

    /// <summary>
    /// Rounded-rectangle clip on the element's composition visual. It lives in the element's own
    /// coordinates, so the flight's Scale and the viewer's zoom scale the corners along with it.
    /// </summary>
    private static void RoundCorners(UIElement element, double width, double height)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        if (visual.Clip is CompositionGeometricClip { Geometry: CompositionRoundedRectangleGeometry existing })
        {
            existing.Size = new Vector2((float)width, (float)height);
            return;
        }
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(CornerRadius);
        geometry.Size = new Vector2((float)width, (float)height);
        visual.Clip = compositor.CreateGeometricClip(geometry);
    }

    // Opening springs (slightly bouncy, quick — close to Quick Look); closing eases out.
    // Every property of a flight uses the same curve so they stay in lockstep.
    private const float SpringDamping = 0.72f;
    private static readonly TimeSpan SpringPeriod = TimeSpan.FromMilliseconds(48);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(260);

    private static SpringVector3NaturalMotionAnimation Spring(Compositor c, Vector3 to)
    {
        var spring = c.CreateSpringVector3Animation();
        spring.FinalValue = to;
        spring.DampingRatio = SpringDamping;
        spring.Period = SpringPeriod;
        return spring;
    }

    private static SpringVector2NaturalMotionAnimation Spring(Compositor c, Vector2 to)
    {
        var spring = c.CreateSpringVector2Animation();
        spring.FinalValue = to;
        spring.DampingRatio = SpringDamping;
        spring.Period = SpringPeriod;
        return spring;
    }

    private static Vector3KeyFrameAnimation Ease(Compositor c, Vector3 to)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1f, to, CloseEasing(c));
        a.Duration = CloseDuration;
        return a;
    }

    private static Vector2KeyFrameAnimation Ease(Compositor c, Vector2 to)
    {
        var a = c.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(1f, to, CloseEasing(c));
        a.Duration = CloseDuration;
        return a;
    }

    private static CompositionEasingFunction CloseEasing(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.2f, 1f));

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

    private bool CanZoomIn => LightboxScroller.ZoomFactor < LightboxScroller.MaxZoomFactor - 0.01;
    private bool CanZoomOut => LightboxScroller.ZoomFactor > LightboxScroller.MinZoomFactor + 0.01;

    /// <summary>Right-click menu: zoom items are greyed out at the limits.</summary>
    private void LightboxMenu_Opening(object? sender, object e)
    {
        LightboxZoomInItem.IsEnabled = CanZoomIn;
        LightboxZoomOutItem.IsEnabled = CanZoomOut;
    }

    private bool _viewerReady;

    /// <summary>
    /// One-time setup: rounded corners that follow the picture's size, and the shortcuts
    /// Ctrl+/Ctrl- (main row or numpad), Ctrl+0, Ctrl+C, Ctrl+S while the viewer is open.
    /// </summary>
    private void EnsureViewerSetup()
    {
        if (_viewerReady) return;
        _viewerReady = true;
        LightboxImage.SizeChanged += (_, e) => RoundCorners(LightboxImage, e.NewSize.Width, e.NewSize.Height);

        void Add(VirtualKey key, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                if (Lightbox.Visibility != Visibility.Visible) return;
                args.Handled = true;
                action();
            };
            Lightbox.KeyboardAccelerators.Add(accelerator);
        }
        const VirtualKey OemPlus = (VirtualKey)0xBB, OemMinus = (VirtualKey)0xBD;
        Add(OemPlus, () => { if (CanZoomIn) Zoom(1.5); });
        Add(VirtualKey.Add, () => { if (CanZoomIn) Zoom(1.5); });
        Add(OemMinus, () => { if (CanZoomOut) Zoom(1 / 1.5); });
        Add(VirtualKey.Subtract, () => { if (CanZoomOut) Zoom(1 / 1.5); });
        Add(VirtualKey.Number0, () => LightboxScroller.ChangeView(0, 0, 1));
        Add(VirtualKey.C, () =>
        {
            // Let a caption text selection copy as text.
            if (LightboxCaption.SelectedText.Length == 0) LightboxCopy_Click(this, new RoutedEventArgs());
        });
        Add(VirtualKey.S, () => LightboxSave_Click(this, new RoutedEventArgs()));
    }

    // ───────────── Event handlers ─────────────

    private void LightboxScroller_SizeChanged(object sender, SizeChangedEventArgs e) => FitViewerImage();

    /// <summary>Clicks on the dimmed area close the viewer; the picture, arrows and caption swallow theirs.</summary>
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
        if (CurrentViewerPath() is { } path) MediaActions.OpenExternally(path);
    }

    private async void LightboxCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerPath() is not { } path) return;
        await MediaActions.CopyImageAsync(path);
        FlashViewerStatus("Copied");
    }

    private async void LightboxSave_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentViewerPath() is not { } path) return;
        if (await MediaActions.SaveAsAsync(this, path, _viewerItems[_viewerIndex])) FlashViewerStatus("Saved");
    }

    private string? CurrentViewerPath() =>
        _viewerItems.Count > 0 && _viewerItems[_viewerIndex].MediaPath is { } p && File.Exists(p) ? p : null;

    /// <summary>Briefly shows "Copied"/"Saved" in a pill near the bottom, then fades it away.</summary>
    private async void FlashViewerStatus(string text)
    {
        LightboxToastText.Text = text;
        LightboxToast.Opacity = 1;
        await Task.Delay(1400);
        if (LightboxToastText.Text == text) LightboxToast.Opacity = 0;
    }
}
