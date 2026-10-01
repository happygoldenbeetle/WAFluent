using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WhatsAppNative.Controls;

/// <summary>
/// Lays children out left to right and wraps to the next line when the row is full
/// (the chips of picked group members). WinUI has no built-in wrap panel.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, row = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += row + VerticalSpacing;
                x = 0;
                row = 0;
            }
            x += size.Width + HorizontalSpacing;
            row = Math.Max(row, size.Height);
            widest = Math.Max(widest, x - HorizontalSpacing);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width, y + row);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += row + VerticalSpacing;
                x = 0;
                row = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            row = Math.Max(row, size.Height);
        }
        return finalSize;
    }
}
