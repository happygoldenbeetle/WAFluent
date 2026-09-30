using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using WhatsAppNative.Models;
using WhatsAppNative.Services;

namespace WhatsAppNative;

/// <summary>
/// The video lightbox: the video at its own shape (no black bars), at most 75% of the window's
/// height, over the dimmed chat; a click outside closes it, a click on it plays/pauses.
/// Our own bar replaces Windows' transport controls: play on the left, seek, and the volume
/// slider always shown on the right (Windows' pop-up volume got clipped). GIFs loop, muted,
/// without the bar.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _videoTimer;
    private bool _videoSeeking;   // the seek bar is being moved by us, not the user
    private bool _videoIsGif;

    private void OpenVideo(Message m)
    {
        if (m.MediaPath is not { } path) return;
        AudioPlayback.Stop();   // one thing playing at a time
        _videoIsGif = m.IsGif;
        var player = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(path)),
            IsLoopingEnabled = m.IsGif,
            IsMuted = m.IsGif,
            AutoPlay = true,
            Volume = VideoVolume.Value / 100,
        };
        player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(FitVideo);
        player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateVideoBar);
        VideoPlayer.SetMediaPlayer(player);
        VideoBar.Visibility = m.IsGif ? Visibility.Collapsed : Visibility.Visible;

        // Until the video reports its size, use the sender's (bubble) proportions.
        FitVideo(m.MediaWidth, m.MediaHeight);
        VideoViewer.Visibility = Visibility.Visible;
        _videoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _videoTimer.Tick -= VideoTimer_Tick;
        _videoTimer.Tick += VideoTimer_Tick;
        _videoTimer.Start();
        VideoPlayButton.Focus(FocusState.Pointer);   // keyboard shortcuts work, no focus outline
    }

    private void CloseVideo()
    {
        _videoTimer?.Stop();
        if (VideoPlayer.MediaPlayer is { } player)
        {
            player.Pause();
            VideoPlayer.SetMediaPlayer(null);
            player.Dispose();
        }
        VideoViewer.Visibility = Visibility.Collapsed;
    }

    /// <summary>Sizes the frame to the video's own shape, as big as fits (up to 85% of the height, 2x its size).</summary>
    private void FitVideo()
    {
        if (VideoPlayer.MediaPlayer?.PlaybackSession is { NaturalVideoWidth: > 0, NaturalVideoHeight: > 0 } session)
            FitVideo(session.NaturalVideoWidth, session.NaturalVideoHeight);
    }

    private void FitVideo(double width, double height)
    {
        if (width <= 0 || height <= 0 || VideoViewer.ActualWidth <= 0) return;
        var maxWidth = Math.Max(200, VideoViewer.ActualWidth - 280);
        var maxHeight = Math.Max(150, VideoViewer.ActualHeight * 0.75);
        var scale = Math.Min(2, Math.Min(maxWidth / width, maxHeight / height));
        VideoPlayer.Width = Math.Round(width * scale);
        VideoPlayer.Height = Math.Round(height * scale);
        // Narrow (portrait) videos: the bar keeps a usable width, overhanging the sides.
        VideoFrame.Width = Math.Max(VideoPlayer.Width, _videoIsGif ? 0 : 440);
        VideoFrame.Height = VideoPlayer.Height;
    }

    private void VideoViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VideoViewer.Visibility == Visibility.Visible) FitVideo();
    }

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

    /// <summary>A click on the dimmed area closes; on the video it plays/pauses; the bar keeps its own.</summary>
    private void VideoViewer_Tapped(object sender, TappedRoutedEventArgs e) => CloseVideo();

    private void VideoFrame_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!_videoIsGif) TogglePlay();
    }

    private void VideoBar_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void VideoClose_Click(object sender, RoutedEventArgs e) => CloseVideo();

    private void VideoClose_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        CloseVideo();
    }

    private void VideoPlayPause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (VideoViewer.Visibility != Visibility.Visible || _videoIsGif) return;
        e.Handled = true;
        TogglePlay();
    }
}
