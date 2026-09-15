namespace VoiceWink.Helpers;

/// <summary>
/// PRM-6 (static two lines): may the pill's status text use a second line?
/// <para>Compares two MEASUREMENTS, never arithmetic: <paramref name="renderedProbeHeightDips"/> is
/// the <c>ActualHeight</c> of an invisible probe TextBlock that mirrors the status text's CONTENT at
/// the exact box width the visible text gets, configured as the promoted rendering would be
/// (<c>Wrap</c>, <c>MaxLines = 2</c>). That makes the height carry the real glyph metrics — CJK/emoji
/// fallback fonts have taller line boxes than Latin, and WinUI line stacking is not guaranteed to be
/// additive across text scales, so <c>2 × singleLineHeight</c> is not a safe gate (Codex plan rounds
/// 2026-07-26).</para>
/// <para>Fails SAFE to one line — today's ellipsized behaviour — whenever either measurement is
/// missing. At large Windows text scales two lines genuinely do not fit the fixed-height pill: the
/// user's font size wins, the pill never grows, and the font never shrinks.</para>
/// </summary>
internal static class MiniRecorderStatusLines
{
    /// <param name="availableHeightDips">The pill's inner layout height (the grid's ActualHeight).</param>
    /// <param name="renderedProbeHeightDips">The height probe's measured ActualHeight (see above).</param>
    internal static int Decide(double availableHeightDips, double renderedProbeHeightDips)
    {
        // Unmeasured / mid-layout junk (0, negative, NaN, Infinity) → fail safe to one line.
        if (!double.IsFinite(availableHeightDips) || availableHeightDips <= 0) return 1;
        if (!double.IsFinite(renderedProbeHeightDips) || renderedProbeHeightDips <= 0) return 1;

        // Equality genuinely fits: both sides are measured heights, not estimates.
        return renderedProbeHeightDips <= availableHeightDips ? 2 : 1;
    }
}
