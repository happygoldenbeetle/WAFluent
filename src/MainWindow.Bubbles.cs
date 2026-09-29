using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WhatsAppNative.Helpers;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Settings › iMessage-style bubbles: the look (Controls/Bubble.cs, Helpers/Ui.IMessage) and
/// its motion. New bubbles spring in from their corner (yours also rise from the composer),
/// and the typing bubble pops in and out. Classic bubbles stay still, as before.
/// </summary>
public sealed partial class MainWindow
{
    private void SetupBubbles()
    {
        IMessageSwitch.IsOn = _ui.IMessageBubbles;
        Messages.ElementPrepared += Messages_ElementPrepared;
        ApplyTypingBubbleMotion();
    }

    private void IMessage_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ui.IMessageBubbles == IMessageSwitch.IsOn) return;
        _ui.IMessageBubbles = IMessageSwitch.IsOn;
        _ui.Save();
        Ui.IMessage = _ui.IMessageBubbles;
        ViewModel.RefreshRuns();
        TypingBubble.Refresh();
        ApplyTypingBubbleMotion();
        // Redraw the open conversation in the new look.
        Messages.ItemsSource = null;
        Messages.ItemsSource = ViewModel.SelectedChat?.Messages;
        ScrollToBottom();
    }

    /// <summary>A message that just arrived or was just sent: spring it in (iMessage look only).</summary>
    private void Messages_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (!Ui.IMessage || e.Element is not Grid { Tag: Message { AnimateIn: true } message } row) return;
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

    /// <summary>The typing bubble pops in from its tail and shrinks away (iMessage look); classic just appears.</summary>
    private void ApplyTypingBubbleMotion()
    {
        if (!Ui.IMessage)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(TypingBubble, null);
            ElementCompositionPreview.SetImplicitHideAnimation(TypingBubble, null);
            return;
        }
        var visual = ElementCompositionPreview.GetElementVisual(TypingBubble);
        var compositor = visual.Compositor;
        TypingBubble.SizeChanged += (_, e) => visual.CenterPoint = new Vector3(0, (float)e.NewSize.Height, 0);

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
