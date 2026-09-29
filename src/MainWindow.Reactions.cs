using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WhatsAppNative.Models;

namespace WhatsAppNative;

/// <summary>
/// Reactions, WhatsApp Desktop style: the right-click menu opens with a row of emoji on top
/// that pop in one after another. Picking one sends it flying in an arc into the pill under
/// the bubble, which bounces; picking your current reaction again takes it back.
/// </summary>
public sealed partial class MainWindow
{
    private const double ChoiceSize = 38;

    /// <summary>A menu item that just shows what's in its Tag (here: the emoji row).</summary>
    private static readonly Lazy<ControlTemplate> RowTemplate = new(() => (ControlTemplate)XamlReader.Load(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuFlyoutItem">
            <ContentPresenter Content="{TemplateBinding Tag}" Margin="6,6,6,2" />
        </ControlTemplate>
        """));

    /// <summary>
    /// The emoji row at the top of <paramref name="menu"/>: your five quick reactions (Settings)
    /// and "+" for any emoji. Your current reaction has a dot under it; a custom one takes the
    /// "+" slot so it can be taken back.
    /// </summary>
    private MenuFlyoutItem ReactionRow(MenuFlyout menu, Message message, FrameworkElement row)
    {
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var quick = _ui.QuickReactions;
        foreach (var emoji in quick)
            choices.Children.Add(Choice(new TextBlock { Text = emoji, FontSize = 22, FontFamily = Controls.EmojiPicker.EmojiFont }, emoji == message.MyReaction,
                                        choice => PickReaction(menu, message, row, choice, emoji),
                                        emoji == message.MyReaction ? $"Remove {emoji} reaction" : $"React with {emoji}"));

        var custom = message.MyReaction.Length > 0 && !quick.Contains(message.MyReaction);
        choices.Children.Add(custom
            ? Choice(new TextBlock { Text = message.MyReaction, FontSize = 22, FontFamily = Controls.EmojiPicker.EmojiFont }, true,
                     choice => PickReaction(menu, message, row, choice, message.MyReaction), $"Remove {message.MyReaction} reaction")
            : Choice(new FontIcon { Glyph = "\uE710", FontSize = 16 }, false, choice =>
              {
                  // Only once the menu is gone: closing it hands focus back to where it came from.
                  menu.Closed += (_, _) => OpenEmojiPicker(row, emoji => React(message, row, emoji, from: null), closeOnPick: true);
                  menu.Hide();
              }, "More reactions"));

        menu.Opened += (_, _) => PopIn(choices);
        return new MenuFlyoutItem { Template = RowTemplate.Value, Tag = choices, IsTabStop = false };
    }

    /// <summary>One slot: the emoji (or +), a dot under it when it's your reaction. No hover fill, just a swell.</summary>
    private static Button Choice(FrameworkElement face, bool active, Action<Button> picked, string name)
    {
        face.HorizontalAlignment = HorizontalAlignment.Center;
        face.VerticalAlignment = VerticalAlignment.Center;
        var content = new Grid { Width = ChoiceSize, Height = ChoiceSize + 6 };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChoiceSize) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        content.Children.Add(face);
        if (active)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Fill = Helpers.Themed.Brush("ChatAccentBrush"),
            };
            Grid.SetRow(dot, 1);
            content.Children.Add(dot);
        }

        // A Button (a menu item swallows clicks on anything else), with every fill see-through.
        var slot = new Button { Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
                                    "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            slot.Resources[key] = clear;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slot, name);
        slot.Click += (_, _) => picked(slot);
        slot.PointerEntered += (_, _) => Hover(face, true);
        slot.PointerExited += (_, _) => Hover(face, false);
        ElementCompositionPreview.SetIsTranslationEnabled(slot, true);
        ElementCompositionPreview.GetElementVisual(slot).Opacity = 0;   // until the pop-in starts
        return slot;
    }

    private void PickReaction(MenuFlyout menu, Message message, FrameworkElement row, FrameworkElement choice, string emoji)
    {
        var start = choice.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(ChoiceSize / 2, ChoiceSize / 2));
        menu.Hide();
        React(message, row, emoji, start);
    }

    /// <summary>Reacts (or takes the reaction back) and flies the emoji into the pill from <paramref name="from"/>.</summary>
    private void React(Message message, FrameworkElement row, string emoji, Windows.Foundation.Point? from)
    {
        if (!ViewModel.React(message, emoji)) return;   // taken back: the pill just updates
        row.UpdateLayout();
        if (FindDescendant(row, el => el.Name == "ReactionPill") is not { ActualWidth: > 0 } pill) return;
        if (from is { } start) FlyReaction(emoji, start, pill);
        else PopPill(pill);
    }

    /// <summary>Double-click a message: react with your first quick reaction (again: remove it).</summary>
    private void Messages_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.IsSelecting || Lightbox.Visibility == Visibility.Visible || RowOf(e.OriginalSource as DependencyObject) is not { Tag: Message message } row) return;
        if (message.Kind == MessageKind.DateDivider || message.Delivery is Delivery.Pending or Delivery.Failed) return;
        if (IsInside<Button>(e.OriginalSource as DependencyObject, row)) return;   // play button, file buttons...
        e.Handled = true;
        ClearSelection(row);   // the double-click also selected a word

        // Already reacted with it: a second double-click takes the reaction back.
        var first = _ui.QuickReactions[0];
        React(message, row, first, message.MyReaction == first ? null : e.GetPosition(Root));
    }

    private static bool IsInside<T>(DependencyObject? source, DependencyObject stop)
    {
        for (var d = source; d is not null && d != stop; d = VisualTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    // ───────────── Animations ─────────────

    /// <summary>The emoji rise and pop in one after another as the menu opens.</summary>
    private static void PopIn(Panel choices)
    {
        var i = 0;
        foreach (var choice in choices.Children.OfType<FrameworkElement>())
        {
            var v = ElementCompositionPreview.GetElementVisual(choice);
            var c = v.Compositor;
            v.CenterPoint = new Vector3((float)ChoiceSize / 2, (float)ChoiceSize / 2, 0);
            var delay = TimeSpan.FromMilliseconds(30 * i++);

            var pop = Spring(c, new Vector3(0.2f, 0.2f, 1), Vector3.One, 0.5f, 45);
            pop.DelayTime = delay;
            pop.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Scale", pop);

            var rise = Spring(c, new Vector3(0, 10, 0), Vector3.Zero, 0.55f, 45);
            rise.DelayTime = delay;
            rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Translation", rise);

            var fade = Fade(c, 0, 1, 90);
            fade.DelayTime = delay;
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>Hovered emoji swell a little (within the menu's padding, so nothing gets clipped).</summary>
    private static void Hover(FrameworkElement face, bool over)
    {
        var v = ElementCompositionPreview.GetElementVisual(face);
        v.CenterPoint = new Vector3((float)face.ActualWidth / 2, (float)face.ActualHeight / 2, 0);
        v.StartAnimation("Scale", Spring(v.Compositor, null, over ? new Vector3(1.2f, 1.2f, 1) : Vector3.One, 0.5f, 40));
    }

    /// <summary>The pill bounces (a reaction landed, or double-click on your existing one).</summary>
    private static void PopPill(FrameworkElement pill)
    {
        var v = ElementCompositionPreview.GetElementVisual(pill);
        v.CenterPoint = new Vector3((float)pill.ActualWidth / 2, (float)pill.ActualHeight / 2, 0);
        v.Opacity = 1;
        v.StartAnimation("Scale", Spring(v.Compositor, new Vector3(0.4f, 0.4f, 1), Vector3.One, 0.4f, 50));
    }

    /// <summary>The emoji arcs from the menu down into the pill, which then pops.</summary>
    private void FlyReaction(string emoji, Windows.Foundation.Point start, FrameworkElement pill)
    {
        var pillVisual = ElementCompositionPreview.GetElementVisual(pill);
        pillVisual.Opacity = 0;

        var end = pill.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
        var flyer = new TextBlock { Text = emoji, FontSize = 22, FontFamily = Controls.EmojiPicker.EmojiFont, IsHitTestVisible = false };
        FxLayer.Children.Add(flyer);
        flyer.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var half = new Vector3((float)flyer.DesiredSize.Width / 2, (float)flyer.DesiredSize.Height / 2, 0);
        Canvas.SetLeft(flyer, start.X - half.X);
        Canvas.SetTop(flyer, start.Y - half.Y);

        ElementCompositionPreview.SetIsTranslationEnabled(flyer, true);
        var v = ElementCompositionPreview.GetElementVisual(flyer);
        var c = v.Compositor;
        v.CenterPoint = half;

        // Up and over, then down into the pill (a rough parabola from three keyframes).
        var delta = new Vector3((float)(end.X - start.X), (float)(end.Y - start.Y), 0);
        var peak = Math.Min(0f, delta.Y) - 45f;
        var path = c.CreateVector3KeyFrameAnimation();
        var rise = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.6f), new Vector2(0.4f, 1f));
        var fall = c.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.8f, 0.4f));
        path.InsertKeyFrame(0, Vector3.Zero);
        path.InsertKeyFrame(0.4f, new Vector3(delta.X * 0.4f, peak, 0), rise);
        path.InsertKeyFrame(1, delta, fall);
        path.Duration = TimeSpan.FromMilliseconds(480);

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, Vector3.One);
        scale.InsertKeyFrame(0.4f, new Vector3(1.6f, 1.6f, 1));
        scale.InsertKeyFrame(1, new Vector3(0.55f, 0.55f, 1));
        scale.Duration = path.Duration;

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        v.StartAnimation("Translation", path);
        v.StartAnimation("Scale", scale);
        batch.End();
        batch.Completed += (_, _) =>
        {
            FxLayer.Children.Remove(flyer);
            PopPill(pill);
        };
    }

    // ───────────── Animation helpers ─────────────

    private static SpringVector3NaturalMotionAnimation Spring(Compositor c, Vector3? from, Vector3 to, float damping, double periodMs)
    {
        var spring = c.CreateSpringVector3Animation();
        if (from is { } f) spring.InitialValue = f;
        spring.FinalValue = to;
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        return spring;
    }

    private static ScalarKeyFrameAnimation Fade(Compositor c, float from, float to, double ms)
    {
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, from);
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromMilliseconds(ms);
        return fade;
    }
}
