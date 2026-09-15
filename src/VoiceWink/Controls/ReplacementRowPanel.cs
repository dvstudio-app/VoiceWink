using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using Windows.Foundation;

namespace VoiceWink.Controls;

/// <summary>
/// The Word Replacements list row's "original → replacement" group: exactly three children, in
/// order — the original label, the arrow, the replacement label — laid out left to right and
/// vertically centred. A custom <see cref="Panel"/> rather than a horizontal <c>StackPanel</c>
/// for one reason: a StackPanel reports its children's UNCONSTRAINED width as its own desired
/// size, and a Grid star column floors at its cell's desired width — so a long original grew the
/// row's text column past the card and pushed the toggle / pencil / bin off it (self-review lens
/// B on the DCT-1 follow-up, 2026-09-13, read against WinUI's <c>Grid.cpp</c>). This panel measures
/// within the width it is given: the two labels' caps come from <see cref="ReplacementRowWidths.Split"/>,
/// each label is re-measured at its cap (a <c>TextTrimming</c> TextBlock fits itself to that), and
/// the panel's desired width never exceeds the constraint — so the star column resolves to its
/// true share and the controls beside it never move. Under an infinite constraint (no parent
/// should give one, but a StackPanel would) it lays the labels out at their natural widths.
/// </summary>
public sealed class ReplacementRowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
        {
            // Not the shape this panel is for — measure whatever is there, side by side.
            double w = 0, h = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                w += child.DesiredSize.Width;
                h = Math.Max(h, child.DesiredSize.Height);
            }
            return new Size(Bound(w, availableSize.Width), h);
        }

        var original = Children[0];
        var arrow = Children[1];
        var replacement = Children[2];
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        original.Measure(unbounded);
        arrow.Measure(unbounded);
        replacement.Measure(unbounded);

        // DesiredSize includes the child's Margin — the arrow's 10 px either side is in arrowWidth.
        var arrowWidth = arrow.DesiredSize.Width;
        var originalNatural = original.DesiredSize.Width;
        var replacementNatural = replacement.DesiredSize.Width;

        if (!double.IsInfinity(availableSize.Width))
        {
            var (originalMax, replacementMax) = ReplacementRowWidths.Split(
                availableSize.Width, arrowWidth, originalNatural, replacementNatural);
            // Re-measure only a label that must shrink: a constraint equal to a label's own measured
            // width can ellipsize on a layout-rounding hair (self-review lens A).
            if (originalMax < originalNatural)
                original.Measure(new Size(originalMax, availableSize.Height));
            if (replacementMax < replacementNatural)
                replacement.Measure(new Size(replacementMax, availableSize.Height));
        }

        var width = original.DesiredSize.Width + arrowWidth + replacement.DesiredSize.Width;
        var height = Math.Max(original.DesiredSize.Height,
            Math.Max(arrow.DesiredSize.Height, replacement.DesiredSize.Height));
        return new Size(Bound(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            // Full row height per child: each child's own VerticalAlignment (Center) places it.
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }

    private static double Bound(double width, double available)
        => double.IsInfinity(available) ? width : Math.Min(width, available);
}
