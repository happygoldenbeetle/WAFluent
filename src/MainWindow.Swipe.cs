using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Swipe to reply: drag any message to the right with the mouse (or pen/touch). The bubble
/// follows the pointer, a reply arrow grows in behind it, and letting go past the threshold
/// quotes the message in the composer. Then the bubble springs back.
/// Drags that start leftwards or vertically are left alone, so text selection still works.
/// </summary>
public sealed partial class MainWindow
{
    private const double SwipeStart = 8;       // px of rightward travel before it counts as a swipe
    private const double SwipeTrigger = 64;    // past this, letting go replies
    private const double SwipeMax = 96;        // rubber-band limit
    private const double HintSize = 32;

    private FrameworkElement? _swipeRow;
    private Message? _swipeMessage;
    private Point _swipeOrigin;
    private uint _swipePointer;
    private bool _swiping;
    private bool _swipeArmed;                  // past the trigger (the arrow has "popped")
    private double _swipeRowLeft;

    private void SetupSwipe()
    {
        // TextBlocks (selection) and buttons mark pointer events handled; listen anyway.
        Messages.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Swipe_PointerPressed), handledEventsToo: true);
        Messages.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Swipe_PointerMoved), handledEventsToo: true);
        Messages.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Swipe_PointerReleased), handledEventsToo: true);
        Messages.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Swipe_PointerEnded), handledEventsToo: true);
        Messages.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Swipe_PointerEnded), handledEventsToo: true);

        var hint = ElementCompositionPreview.GetElementVisual(ReplyHint);
        hint.Opacity = 0;
        hint.CenterPoint = new Vector3((float)HintSize / 2, (float)HintSize / 2, 0);
    }

    private void Swipe_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Messages);
        var mouse = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
        if (mouse && !point.Properties.IsLeftButtonPressed) return;

        _swipeRow = RowOf(e.OriginalSource as DependencyObject);
        _swipeMessage = _swipeRow?.Tag as Message;
        if (_swipeMessage is null || _swipeMessage.Kind == MessageKind.DateDivider
            || _swipeMessage.Delivery is Delivery.Pending or Delivery.Failed)
        {
            _swipeRow = null;
            return;
        }
        _swipeOrigin = point.Position;
        _swipePointer = e.Pointer.PointerId;
        _swiping = false;
        _swipeArmed = false;
    }

    private void Swipe_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_swipeRow is null || e.Pointer.PointerId != _swipePointer) return;
        var position = e.GetCurrentPoint(Messages).Position;
        var dx = position.X - _swipeOrigin.X;
        var dy = position.Y - _swipeOrigin.Y;

        if (!_swiping)
        {
            if (dx > SwipeStart && dx > 2 * Math.Abs(dy))
                BeginSwipe(e.Pointer);
            else if (Math.Abs(dy) > SwipeStart || dx < -SwipeStart)
                _swipeRow = null;   // a selection drag or a scroll, not ours
            if (!_swiping) return;
        }

        e.Handled = true;
        var offset = Rubber(dx - SwipeStart);
        SetSwipeOffset(offset);

        var armed = offset >= SwipeTrigger;
        if (armed != _swipeArmed)
        {
            _swipeArmed = armed;
            PopHint(armed);
        }
    }

    private void Swipe_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_swiping || e.Pointer.PointerId != _swipePointer) { _swipeRow = null; return; }
        e.Handled = true;
        Messages.ReleasePointerCapture(e.Pointer);   // raises CaptureLost -> EndSwipe
        EndSwipe();
    }

    private void Swipe_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_swiping && e.Pointer.PointerId == _swipePointer) EndSwipe();
    }

    private void BeginSwipe(Pointer pointer)
    {
        if (_swipeRow is null) return;
        _swiping = true;
        Messages.CapturePointer(pointer);   // takes the drag away from text selection
        ClearSelection(_swipeRow);

        ElementCompositionPreview.SetIsTranslationEnabled(_swipeRow, true);
        ElementCompositionPreview.GetElementVisual(_swipeRow).StopAnimation("Translation");

        // The arrow waits just left of the bubble's resting place, vertically centred on it.
        var origin = _swipeRow.TransformToVisual(SwipeLayer).TransformPoint(default);
        _swipeRowLeft = origin.X;
        Canvas.SetTop(ReplyHint, origin.Y + (_swipeRow.ActualHeight - HintSize) / 2);
        Canvas.SetLeft(ReplyHint, 0);
        var hint = ElementCompositionPreview.GetElementVisual(ReplyHint);
        hint.StopAnimation("Opacity");
        hint.StopAnimation("Scale");
    }

    private void EndSwipe()
    {
        if (!_swiping || _swipeRow is null) return;
        _swiping = false;
        var row = _swipeRow;
        var message = _swipeMessage;
        var reply = _swipeArmed;
        _swipeRow = null;
        _swipeArmed = false;

        // Spring the bubble home; the arrow fades out where it is.
        var visual = ElementCompositionPreview.GetElementVisual(row);
        var c = visual.Compositor;
        var spring = c.CreateSpringVector3Animation();
        spring.FinalValue = Vector3.Zero;
        spring.DampingRatio = 0.75f;
        spring.Period = TimeSpan.FromMilliseconds(45);
        visual.StartAnimation("Translation", spring);

        var hint = ElementCompositionPreview.GetElementVisual(ReplyHint);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        hint.StartAnimation("Opacity", fade);

        if (reply && message is not null) StartReply(message);
    }

    /// <summary>Moves the bubble and brings the arrow in behind it (fading and growing up to the trigger).</summary>
    private void SetSwipeOffset(double offset)
    {
        if (_swipeRow is null) return;
        ElementCompositionPreview.GetElementVisual(_swipeRow).Properties.InsertVector3("Translation", new Vector3((float)offset, 0, 0));

        var progress = (float)Math.Clamp(offset / SwipeTrigger, 0, 1);
        var hint = ElementCompositionPreview.GetElementVisual(ReplyHint);
        // Trails the bubble's left edge by 8 px, but never leaves the conversation on the left.
        var x = Math.Max(0, _swipeRowLeft + offset - HintSize - 8);
        Canvas.SetLeft(ReplyHint, x);
        hint.Opacity = progress;
        if (!_swipeArmed)
        {
            var scale = 0.5f + 0.5f * progress;
            hint.Scale = new Vector3(scale, scale, 1);
        }
    }

    /// <summary>A little bounce when the swipe crosses the "will reply" point, and back if it retreats.</summary>
    private void PopHint(bool armed)
    {
        var hint = ElementCompositionPreview.GetElementVisual(ReplyHint);
        var spring = hint.Compositor.CreateSpringVector3Animation();
        spring.InitialValue = armed ? new Vector3(1.25f, 1.25f, 1) : null;
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = 0.45f;
        spring.Period = TimeSpan.FromMilliseconds(40);
        hint.StartAnimation("Scale", spring);
    }

    /// <summary>Follows the pointer 1:1 up to the trigger, then resists.</summary>
    private static double Rubber(double dx)
    {
        if (dx <= 0) return 0;
        if (dx <= SwipeTrigger) return dx;
        var over = dx - SwipeTrigger;
        var room = SwipeMax - SwipeTrigger;
        return SwipeTrigger + room * (1 - Math.Exp(-over / (room * 1.5)));
    }

    /// <summary>The ItemsRepeater's direct child (a message's template root) containing <paramref name="source"/>.</summary>
    private FrameworkElement? RowOf(DependencyObject? source)
    {
        while (source is not null)
        {
            var parent = VisualTreeHelper.GetParent(source);
            if (parent == Messages) return source as FrameworkElement;
            source = parent;
        }
        return null;
    }

    private static void ClearSelection(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { IsTextSelectionEnabled: true } text && text.SelectedText.Length > 0)
                text.Select(text.ContentStart, text.ContentStart);
            ClearSelection(child);
        }
    }

    // ───────────── Replying ─────────────

    /// <summary>Quote a message in the composer (swipe, or "Reply" in its menu).</summary>
    private void StartReply(Message message)
    {
        ViewModel.BeginReply(message);
        if (ViewModel.IsReplying) ComposerBox.Focus(FocusState.Programmatic);
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelReply();
        ComposerBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Clicking a quote jumps to the message it quotes (when it's loaded) and flashes it.</summary>
    private void Quote_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Message reply } || ViewModel.SelectedChat is not { } chat) return;
        e.Handled = true;
        var index = chat.Messages.ToList().FindIndex(m => m.Id == reply.ReplyId);
        if (index < 0) return;

        var target = Messages.GetOrCreateElement(index);
        target.UpdateLayout();
        target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = true });
        Flash(target);
    }

    private static void Flash(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0.2f, 0.35f);
        pulse.InsertKeyFrame(0.45f, 1f);
        pulse.InsertKeyFrame(0.7f, 0.35f);
        pulse.InsertKeyFrame(1f, 1f);
        pulse.Duration = TimeSpan.FromMilliseconds(1100);
        pulse.DelayTime = TimeSpan.FromMilliseconds(250);   // let the scroll land first
        visual.StartAnimation("Opacity", pulse);
    }
}
