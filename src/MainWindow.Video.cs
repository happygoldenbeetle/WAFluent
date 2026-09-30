using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The video lightbox, like the photo one: the video springs out of its bubble to its own
/// shape (rounded, no black bars, at most 75% of the window's height) while the chat dims,
/// and flies back in on close. A click outside closes it, a click on it plays/pauses, a
/// double-click (or the button, or F) goes full screen. Our own bar replaces Windows'
/// controls: play on the left, seek, volume always shown and full screen on the right.
/// GIFs loop, muted, without the bar.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _videoTimer;
    private bool _videoSeeking;   // the seek bar is being moved by us, not the user
    private bool _videoIsGif;
    private bool _videoFullScreen;
    private bool _videoClosing;
    private FrameworkElement? _videoSource;   // the bubble it came out of

    private void OpenVideo(Message m, FrameworkElement? from = null)
    {
        if (m.MediaPath is not { } path) return;
        AudioPlayback.Stop();   // one thing playing at a time
        _videoIsGif = m.IsGif;
        _videoSource = from;
        _videoClosing = false;
        var player = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(path)),
            IsLoopingEnabled = m.IsGif,
            IsMuted = m.IsGif,
            AutoPlay = true,
            Volume = VideoVolume.Value / 100,
        };
        player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(VideoOpened);
        player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(VideoOpened);
        player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateVideoBar);
        VideoPlayer.SetMediaPlayer(player);
        VideoBar.Visibility = m.IsGif ? Visibility.Collapsed : Visibility.Visible;
        VideoFullScreenIcon.Glyph = "";

        // Hidden until it knows its shape; then it flies out of the bubble.
        VideoFrame.Opacity = 0;
        VideoViewer.Visibility = Visibility.Visible;
        FitVideo(m.MediaWidth, m.MediaHeight);
        VideoScrim.Opacity = 1;
        _videoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _videoTimer.Tick -= VideoTimer_Tick;
        _videoTimer.Tick += VideoTimer_Tick;
        _videoTimer.Start();
        VideoPlayButton.Focus(FocusState.Pointer);   // keyboard shortcuts work, no focus outline
    }

    private void VideoOpened()
    {
        if (VideoViewer.Visibility != Visibility.Visible || _videoClosing || VideoFrame.Opacity > 0) return;
        FitVideo();
        VideoViewer.UpdateLayout();
        VideoFrame.Opacity = 1;
        if (_videoSource is { IsLoaded: true } from && IsOnScreen(from)) FlyVideo(from, opening: true);
        else PopVideo(opening: true);
    }

    private void CloseVideo()
    {
        if (VideoViewer.Visibility != Visibility.Visible || _videoClosing) return;
        if (_videoFullScreen) SetVideoFullScreen(false);
        _videoClosing = true;
        _videoTimer?.Stop();
        VideoPlayer.MediaPlayer?.Pause();
        VideoScrim.Opacity = 0;
        if (VideoFrame.Opacity > 0 && _videoSource is { IsLoaded: true } to && IsOnScreen(to)) FlyVideo(to, opening: false);
        else PopVideo(opening: false);
    }

    private void FinishCloseVideo()
    {
        if (VideoPlayer.MediaPlayer is { } player)
        {
            VideoPlayer.SetMediaPlayer(null);
            player.Dispose();
        }
        var visual = ElementCompositionPreview.GetElementVisual(VideoPlayer);
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        VideoBar.Opacity = 1;
        if (_videoSource is not null) _videoSource.Opacity = 1;
        _videoSource = null;
        VideoViewer.Visibility = Visibility.Collapsed;
        _videoClosing = false;
    }

    // ───── The flight, as the photos' (MainWindow.Lightbox.cs) ─────

    /// <summary>The video grows out of (or shrinks back into) its bubble; the bar fades after.</summary>
    private void FlyVideo(FrameworkElement bubble, bool opening)
    {
        var full = BoundsIn(VideoPlayer, VideoViewer);
        var from = BoundsIn(bubble, VideoViewer);
        if (full.Width < 1 || from.Width < 1)
        {
            if (opening) PopVideo(true); else FinishCloseVideo();
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(VideoPlayer, true);
        var visual = ElementCompositionPreview.GetElementVisual(VideoPlayer);
        visual.CenterPoint = new Vector3((float)full.Width / 2, (float)full.Height / 2, 0);
        var scale = (float)Math.Max(from.Width / full.Width, from.Height / full.Height);
        var small = new Vector3(scale, scale, 1);
        var offset = Offset(from, full);

        bubble.Opacity = 0;   // the video has "left" its bubble
        VideoBar.Opacity = 0;
        if (opening)
        {
            visual.Scale = small;
            visual.Properties.InsertVector3("Translation", offset);
        }
        var c = visual.Compositor;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Scale", opening ? Spring(c, Vector3.One) : Ease(c, small));
        visual.StartAnimation("Translation", opening ? Spring(c, Vector3.Zero) : Ease(c, offset));
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (opening)
            {
                if (!_videoClosing) VideoBar.Opacity = 1;
            }
            else
            {
                FinishCloseVideo();
            }
        });
    }

    /// <summary>No bubble to fly from or to: a small zoom and fade.</summary>
    private void PopVideo(bool opening)
    {
        var visual = ElementCompositionPreview.GetElementVisual(VideoFrame);
        visual.CenterPoint = new Vector3((float)VideoFrame.ActualWidth / 2, (float)VideoFrame.ActualHeight / 2, 0);
        var c = visual.Compositor;
        var scale = c.CreateVector3KeyFrameAnimation();
        if (opening) scale.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1));
        scale.InsertKeyFrame(1f, opening ? Vector3.One : new Vector3(0.92f, 0.92f, 1));
        scale.Duration = TimeSpan.FromMilliseconds(200);
        var fade = c.CreateScalarKeyFrameAnimation();
        if (opening) fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, opening ? 1f : 0f);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            if (!opening) FinishCloseVideo();
        });
    }

    // ───── Size and full screen ─────

    /// <summary>Sizes the frame to the video's own shape, as big as fits.</summary>
    private void FitVideo()
    {
        if (VideoPlayer.MediaPlayer?.PlaybackSession is { NaturalVideoWidth: > 0, NaturalVideoHeight: > 0 } session)
            FitVideo(session.NaturalVideoWidth, session.NaturalVideoHeight);
    }

    private void FitVideo(double width, double height)
    {
        if (width <= 0 || height <= 0 || VideoViewer.ActualWidth <= 0) return;
        // Full screen: edge to edge. Otherwise at most 75% of the height, clear of the sides, 2x its size.
        var maxWidth = _videoFullScreen ? VideoViewer.ActualWidth : Math.Max(200, VideoViewer.ActualWidth - 280);
        var maxHeight = _videoFullScreen ? VideoViewer.ActualHeight : Math.Max(150, VideoViewer.ActualHeight * 0.75);
        var scale = Math.Min(maxWidth / width, maxHeight / height);
        if (!_videoFullScreen) scale = Math.Min(2, scale);
        VideoPlayer.Width = Math.Round(width * scale);
        VideoPlayer.Height = Math.Round(height * scale);
        // Narrow (portrait) videos: the bar keeps a usable width, overhanging the sides.
        VideoFrame.Width = Math.Max(VideoPlayer.Width, _videoIsGif ? 0 : 480);
        VideoFrame.Height = VideoPlayer.Height;
        RoundVideoCorners();
    }

    /// <summary>Rounded like the photos, square in full screen.</summary>
    private void RoundVideoCorners()
    {
        var visual = ElementCompositionPreview.GetElementVisual(VideoPlayer);
        var geometry = visual.Clip is CompositionGeometricClip { Geometry: CompositionRoundedRectangleGeometry g } ? g : null;
        if (geometry is null)
        {
            geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
        }
        geometry.Size = new Vector2((float)VideoPlayer.Width, (float)VideoPlayer.Height);
        geometry.CornerRadius = new Vector2(_videoFullScreen ? 0 : CornerRadius);
    }

    private void SetVideoFullScreen(bool on)
    {
        _videoFullScreen = on;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        // Over the title bar too.
        Grid.SetRow(VideoViewer, on ? 0 : 1);
        Grid.SetRowSpan(VideoViewer, on ? 2 : 1);
        VideoCloseButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        VideoFullScreenIcon.Glyph = on ? "" : "";
        ToolTipService.SetToolTip(VideoFullScreenButton, on ? "Exit full screen (F)" : "Full screen (F)");
        DispatcherQueue.TryEnqueue(FitVideo);
    }

    private void VideoViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VideoViewer.Visibility == Visibility.Visible) FitVideo();
    }

    // ───── The bar ─────

    private void VideoTimer_Tick(object? sender, object e) => UpdateVideoBar();

    private void UpdateVideoBar()
    {
        if (VideoPlayer.MediaPlayer?.PlaybackSession is not { } session) return;
        var total = session.NaturalDuration.TotalSeconds;
        var now = session.Position.TotalSeconds;
        _videoSeeking = true;
        VideoSeek.Maximum = Math.Max(0.01, total);
        VideoSeek.Value = Math.Min(now, VideoSeek.Maximum);
        _videoSeeking = false;
        VideoTime.Text = $"{Clock(now)} / {Clock(total)}";
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        VideoPlayIcon.Glyph = playing ? "" : "";
        var muted = VideoPlayer.MediaPlayer.IsMuted || VideoVolume.Value == 0;
        VideoVolumeIcon.Glyph = muted ? "" : "";
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void TogglePlay()
    {
        if (VideoPlayer.MediaPlayer is not { } player) return;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
        else
        {
            // At the end, play again from the start.
            if (player.PlaybackSession.NaturalDuration - player.PlaybackSession.Position < TimeSpan.FromMilliseconds(250))
                player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
        }
        UpdateVideoBar();
    }

    private void VideoPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void VideoFullScreen_Click(object sender, RoutedEventArgs e) => SetVideoFullScreen(!_videoFullScreen);

    private void VideoMute_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.MediaPlayer is not { } player) return;
        player.IsMuted = !player.IsMuted;
        if (!player.IsMuted && VideoVolume.Value == 0) VideoVolume.Value = 50;
        UpdateVideoBar();
    }

    private void VideoSeek_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_videoSeeking || VideoPlayer.MediaPlayer is not { } player) return;
        player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void VideoVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VideoPlayer?.MediaPlayer is not { } player) return;
        player.Volume = e.NewValue / 100;
        if (e.NewValue > 0) player.IsMuted = false;
        UpdateVideoBar();
    }

    // ───── Clicks and keys ─────

    /// <summary>A click on the dimmed area closes; on the video it plays/pauses; the bar keeps its own.</summary>
    private void VideoViewer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_videoFullScreen) CloseVideo();
    }

    private void VideoFrame_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_videoIsGif) TogglePlay();
    }

    private void VideoFrame_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_videoIsGif) return;
        TogglePlay();   // undo the first click's play/pause
        SetVideoFullScreen(!_videoFullScreen);
    }

    private void VideoBar_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void VideoClose_Click(object sender, RoutedEventArgs e) => CloseVideo();

    /// <summary>Esc leaves full screen first, then closes.</summary>
    private void VideoClose_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (_videoFullScreen) SetVideoFullScreen(false);
        else CloseVideo();
    }

    private void VideoPlayPause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (VideoViewer.Visibility != Visibility.Visible || _videoIsGif) return;
        e.Handled = true;
        TogglePlay();
    }

    private void VideoFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (VideoViewer.Visibility != Visibility.Visible || _videoIsGif) return;
        e.Handled = true;
        SetVideoFullScreen(!_videoFullScreen);
    }
}
