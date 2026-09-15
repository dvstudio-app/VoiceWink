using System;

namespace VoiceWink.Helpers;

/// <summary>
/// How a Word Replacements list row splits its width between the original text and the
/// replacement text — the pure arithmetic behind <c>DictionaryPage</c>'s row layout, so a case
/// table can pin it (owner UAT finding on DCT-1, 2026-09-13). Called from
/// <c>Controls/ReplacementRowPanel.MeasureOverride</c> with the width the row's star column hands
/// the panel. DCT-1 laid the labels out in a horizontal StackPanel — infinite width for its
/// children, so nothing trims unless a <c>MaxWidth</c> is set — and set a hard 200 px on both;
/// the shipped six-variant VoiceWink rule trimmed at "voice-wi…" with most of the row empty. The
/// caps now come from the width the row actually has, on every measure: both labels keep their natural width while they
/// fit; when they do not, the replacement is guaranteed <see cref="ReplacementShare"/> of the row
/// (or whatever the original leaves free, if that is more) and the original gets the rest — so a
/// long original list uses the whole row before its ellipsis appears, a long replacement beside a
/// short original is not trimmed while the row has room, and neither can push the other off.
/// </summary>
public static class ReplacementRowWidths
{
    /// <summary>The share of the row a long REPLACEMENT is guaranteed when both labels overflow it.</summary>
    public const double ReplacementShare = 0.4;

    /// <summary>
    /// <paramref name="available"/> is the width the text group has (the row's star column);
    /// <paramref name="arrow"/> the arrow glyph including its margins; <paramref name="originalNatural"/>
    /// and <paramref name="replacementNatural"/> the two labels' unconstrained widths. Non-finite or
    /// negative inputs count as zero rather than throwing — this runs inside a layout pass. Bound,
    /// stated: below 5 arrow widths of row (arrow / (1 - 2 × share)) the original's cap is smaller
    /// than the replacement's share, and below ~1.7 arrow widths (arrow / (1 - share)) it is zero —
    /// reachable only by shrinking the window to nothing, and then the ellipsis is the honest answer.
    /// </summary>
    public static (double OriginalMax, double ReplacementMax) Split(
        double available, double arrow, double originalNatural, double replacementNatural)
    {
        if (!IsUsable(available) || available <= 0) return (0, 0);
        var arrowWidth = Positive(arrow);
        var origNatural = Positive(originalNatural);
        var replNatural = Positive(replacementNatural);
        var free = Math.Max(0, available - arrowWidth - origNatural);   // what the original leaves
        var replacementMax = Math.Min(replNatural, Math.Max(available * ReplacementShare, free));
        var originalMax = Math.Max(0, available - arrowWidth - replacementMax);
        return (originalMax, replacementMax);
    }

    private static double Positive(double v) => IsUsable(v) && v > 0 ? v : 0;

    private static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
