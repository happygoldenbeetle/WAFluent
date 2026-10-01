using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Bubble motion: a message that just arrived or was just sent springs in from its corner
/// (yours also rises from the composer), and the typing bubble pops in from its tail and
/// shrinks away.
/// </summary>
public sealed partial class MainWindow
{
    // ───────────── Jump to the latest message ─────────────

    private int _missedWhileUp;

    /// <summary>The button shows once you're a screen or so above the latest message.</summary>
    private void UpdateJumpDown()
    {
        var away = MessagesScroller.ScrollableHeight - MessagesScroller.VerticalOffset > 320;
        if (!away) _missedWhileUp = 0;
        JumpDown.Visibility = away ? Visibility.Visible : Visibility.Collapsed;
        JumpDownBadge.Value = _missedWhileUp;
        JumpDownBadge.Visibility = _missedWhileUp > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JumpDown_Click(object sender, RoutedEventArgs e)
    {
        _missedWhileUp = 0;
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null, disableAnimation: false);
    }

    /// <summary>It fades and grows in / out instead of popping.</summary>
    private void SetupJumpDownMotion()
    {
        var visual = ElementCompositionPreview.GetElementVisual(JumpDown);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(20, 20, 0);
        CompositionAnimationGroup Fade(float from, float to)
        {
            var group = compositor.CreateAnimationGroup();
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.Target = "Scale";
            scale.InsertKeyFrame(0, new Vector3(from, from, 1));
            scale.InsertKeyFrame(1, new Vector3(to, to, 1));
            scale.Duration = TimeSpan.FromMilliseconds(160);
            group.Add(scale);
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Target = "Opacity";
            opacity.InsertKeyFrame(0, from < to ? 0 : 1);
            opacity.InsertKeyFrame(1, from < to ? 1 : 0);
            opacity.Duration = TimeSpan.FromMilliseconds(160);
            group.Add(opacity);
            return group;
        }
        ElementCompositionPreview.SetImplicitShowAnimation(JumpDown, Fade(0.7f, 1));
        ElementCompositionPreview.SetImplicitHideAnimation(JumpDown, Fade(1, 0.7f));
    }

    private void SetupBubbleMotion()
    {
        SetupJumpDownMotion();
        Use24HourSwitch.IsOn = _ui.Use24Hour;
        NotificationsSwitch.IsOn = _ui.Notifications;
        HdMediaSwitch.IsOn = _ui.HdMedia;
        Messages.ElementPrepared += Messages_ElementPrepared;
        SetupMemory();
        SetupMiniPlayer();
        SetupGallery();
        SetupDrafts();
        SetupNavigation();
        SetupPins();
        SetupTypingBubbleMotion();
    }

    /// <summary>Settings › Use 24-hour time: every time is written again and the conversation redrawn.</summary>
    private void Use24Hour_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ui.Use24Hour == Use24HourSwitch.IsOn) return;
        _ui.Use24Hour = Use24HourSwitch.IsOn;
        _ui.Save();
        Helpers.Format.Use24Hour = _ui.Use24Hour;
        ViewModel.RefreshTimes();
        Messages.ItemsSource = null;
        Messages.ItemsSource = ViewModel.SelectedChat?.Messages;
        ScrollToBottom();
    }

    private void Messages_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element is not Grid { Tag: Message { AnimateIn: true } message } row) return;
        message.AnimateIn = false;
        if (row.Children.OfType<StackPanel>().FirstOrDefault() is { } column) SpringIn(column, message.IsOutgoing);
    }

    private static void SpringIn(FrameworkElement bubble, bool outgoing)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(bubble, true);
        var visual = ElementCompositionPreview.GetElementVisual(bubble);
        var compositor = visual.Compositor;

        // Grow out of the bottom corner on its own side.
        void Pivot() => visual.CenterPoint = new Vector3(outgoing ? (float)bubble.ActualWidth : 0, (float)bubble.ActualHeight, 0);
        if (bubble.ActualWidth > 0) Pivot();
        else
        {
            SizeChangedEventHandler? once = null;
            once = (_, _) => { bubble.SizeChanged -= once; Pivot(); };
            bubble.SizeChanged += once;
        }

        var grow = compositor.CreateSpringVector3Animation();
        grow.InitialValue = outgoing ? new Vector3(0.82f, 0.82f, 1) : new Vector3(0.55f, 0.55f, 1);
        grow.FinalValue = Vector3.One;
        grow.DampingRatio = 0.62f;
        grow.Period = TimeSpan.FromMilliseconds(48);
        visual.StartAnimation("Scale", grow);

        if (outgoing)
        {
            // Yours rises from the composer into place.
            var rise = compositor.CreateSpringVector3Animation();
            rise.InitialValue = new Vector3(0, 56, 0);
            rise.FinalValue = Vector3.Zero;
            rise.DampingRatio = 0.72f;
            rise.Period = TimeSpan.FromMilliseconds(52);
            visual.StartAnimation("Translation", rise);
        }

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>The typing bubble pops in from its tail (bottom left, or top left for classic) and shrinks back into it.</summary>
    private void SetupTypingBubbleMotion()
    {
        var visual = ElementCompositionPreview.GetElementVisual(TypingBubble);
        var compositor = visual.Compositor;
        TypingBubble.SizeChanged += (_, e) =>
            visual.CenterPoint = new Vector3(0, Helpers.Ui.IMessage ? (float)e.NewSize.Height : 0, 0);

        var show = compositor.CreateAnimationGroup();
        var pop = compositor.CreateVector3KeyFrameAnimation();
        pop.Target = "Scale";
        pop.InsertKeyFrame(0, new Vector3(0.2f, 0.2f, 1));
        pop.InsertKeyFrame(0.65f, new Vector3(1.07f, 1.07f, 1));
        pop.InsertKeyFrame(1, Vector3.One);
        pop.Duration = TimeSpan.FromMilliseconds(340);
        show.Add(pop);
        var appear = compositor.CreateScalarKeyFrameAnimation();
        appear.Target = "Opacity";
        appear.InsertKeyFrame(0, 0);
        appear.InsertKeyFrame(1, 1);
        appear.Duration = TimeSpan.FromMilliseconds(160);
        show.Add(appear);
        ElementCompositionPreview.SetImplicitShowAnimation(TypingBubble, show);

        var hide = compositor.CreateAnimationGroup();
        var shrink = compositor.CreateVector3KeyFrameAnimation();
        shrink.Target = "Scale";
        shrink.InsertKeyFrame(0, Vector3.One);
        shrink.InsertKeyFrame(1, new Vector3(0.2f, 0.2f, 1));
        shrink.Duration = TimeSpan.FromMilliseconds(190);
        hide.Add(shrink);
        var vanish = compositor.CreateScalarKeyFrameAnimation();
        vanish.Target = "Opacity";
        vanish.InsertKeyFrame(0, 1);
        vanish.InsertKeyFrame(1, 0);
        vanish.Duration = TimeSpan.FromMilliseconds(190);
        hide.Add(vanish);
        ElementCompositionPreview.SetImplicitHideAnimation(TypingBubble, hide);
    }
}
