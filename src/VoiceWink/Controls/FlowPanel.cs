using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VoiceWink.Controls;

/// <summary>
/// Simple wrap panel for WinUI 3 (which lacks a built-in WrapPanel).
/// Children flow left-to-right and wrap to the next row when they exceed the available width.
/// </summary>
public sealed class FlowPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Windows.Foundation.Size(availableSize.Width, y + rowHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            child.Arrange(new Windows.Foundation.Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Windows.Foundation.Size(finalSize.Width, y + rowHeight);
    }
}
