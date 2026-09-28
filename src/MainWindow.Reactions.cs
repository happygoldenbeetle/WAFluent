using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private static readonly string[] QuickReactions = ["👍", "❤️", "😂", "😮", "😢", "🙏"];
    private const double ChoiceSize = 38;

    /// <summary>A menu item that just shows what's in its Tag (here: the emoji row).</summary>
    private static readonly Lazy<ControlTemplate> RowTemplate = new(() => (ControlTemplate)XamlReader.Load(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuFlyoutItem">
            <ContentPresenter Content="{TemplateBinding Tag}" Margin="6,2,6,4" />
        </ControlTemplate>
        """));

    /// <summary>The emoji row at the top of <paramref name="menu"/>, for <paramref name="row"/>'s message.</summary>
    private MenuFlyoutItem ReactionRow(MenuFlyout menu, Message message, FrameworkElement row)
    {
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var emoji in QuickReactions)
        {
            var mine = emoji == message.MyReaction;
            var choice = new Button
            {
                Width = ChoiceSize,
                Height = ChoiceSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(ChoiceSize / 2),
                BorderThickness = new Thickness(0),
                // Your current reaction sits on a soft circle.
                Background = new SolidColorBrush(mine
                    ? Windows.UI.Color.FromArgb(0x40, 0x00, 0xA8, 0x84)   // WhatsApp green, faint
                    : Microsoft.UI.Colors.Transparent),
                Content = new TextBlock { Text = emoji, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, mine ? $"Remove {emoji} reaction" : $"React with {emoji}");
            if (mine) ToolTipService.SetToolTip(choice, "Remove");
            choice.Click += (_, _) => PickReaction(menu, message, row, choice, emoji);
            choice.PointerEntered += (_, _) => Hover(choice, true);
            choice.PointerExited += (_, _) => Hover(choice, false);
            ElementCompositionPreview.SetIsTranslationEnabled(choice, true);
            ElementCompositionPreview.GetElementVisual(choice).Opacity = 0;   // until the pop-in starts
            choices.Children.Add(choice);
        }

        menu.Opened += (_, _) => PopIn(choices);
        return new MenuFlyoutItem { Template = RowTemplate.Value, Tag = choices, IsTabStop = false };
    }

    private void PickReaction(MenuFlyout menu, Message message, FrameworkElement row, Button choice, string emoji)
    {
        var start = choice.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(ChoiceSize / 2, ChoiceSize / 2));
        menu.Hide();
        if (!ViewModel.React(message, emoji)) return;   // taken back: the pill just updates

        row.UpdateLayout();
        if (FindDescendant(row, el => el.Name == "ReactionPill") is { ActualWidth: > 0 } pill)
            FlyReaction(emoji, start, pill);
    }

    // ───────────── Animations ─────────────

    /// <summary>The emoji rise and pop in one after another as the menu opens.</summary>
    private static void PopIn(Panel choices)
    {
        var i = 0;
        foreach (var choice in choices.Children.OfType<Button>())
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

    /// <summary>Hovered emoji swell and lift a little.</summary>
    private static void Hover(Button choice, bool over)
    {
        var v = ElementCompositionPreview.GetElementVisual(choice);
        v.CenterPoint = new Vector3((float)ChoiceSize / 2, (float)ChoiceSize / 2, 0);
        var c = v.Compositor;
        v.StartAnimation("Scale", Spring(c, null, over ? new Vector3(1.3f, 1.3f, 1) : Vector3.One, 0.5f, 40));
        v.StartAnimation("Translation", Spring(c, null, over ? new Vector3(0, -3, 0) : Vector3.Zero, 0.6f, 40));
    }

    /// <summary>The emoji arcs from the menu down into the pill, which then pops.</summary>
    private void FlyReaction(string emoji, Windows.Foundation.Point start, FrameworkElement pill)
    {
        var pillVisual = ElementCompositionPreview.GetElementVisual(pill);
        pillVisual.Opacity = 0;

        var end = pill.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(pill.ActualWidth / 2, pill.ActualHeight / 2));
        var flyer = new TextBlock { Text = emoji, FontSize = 22, IsHitTestVisible = false };
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
            pillVisual.CenterPoint = new Vector3((float)pill.ActualWidth / 2, (float)pill.ActualHeight / 2, 0);
            pillVisual.Opacity = 1;
            pillVisual.StartAnimation("Scale", Spring(c, new Vector3(0.4f, 0.4f, 1), Vector3.One, 0.4f, 50));
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
