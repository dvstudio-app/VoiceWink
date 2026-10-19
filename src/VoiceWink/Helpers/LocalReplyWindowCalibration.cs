namespace VoiceWink.Helpers;

/// <summary>One healthy measured reply: the prompt's length, when its first text arrived and the
/// longest silence between two text chunks after that.</summary>
internal readonly record struct LocalReplyTiming(int PromptChars, long FirstTextMs, long LongestGapMs);

/// <summary>
/// LAI-15: the rule that turns the owner's measured healthy replies into <see cref="LocalReplyWindows"/>
/// (owner decision 2026-10-18: margin 3). Pure — <c>tools/local-reply-timing</c> links it, so the
/// constants in <see cref="LocalReplyWindows.For"/> are computed by the shipped code, not by hand.
/// <para><b>First text</b> = margin × the UPPER ENVELOPE of every healthy point from both machines:
/// the least-squares slope, with the intercept raised until no point lies above the line (Codex plan
/// round B1 — a plain least-squares line averages warm replies over a slow cold load and cuts that load
/// off). <b>Stall</b> = margin × the longest healthy gap. A first-text line that reaches the provider's
/// total deadline even at the shortest prompt, or a stall window that reaches it, is left unarmed: two
/// clocks at the same value add nothing.</para>
/// </summary>
internal static class LocalReplyWindowCalibration
{
    internal const double Margin = 3;

    internal static LocalReplyWindows Compute(IReadOnlyList<LocalReplyTiming> healthy, TimeSpan totalDeadline, double margin = Margin)
    {
        if (healthy.Count == 0)
            return LocalReplyWindows.Unarmed;

        var (baseMs, perChar) = UpperEnvelope(healthy);
        double? firstBase = margin * baseMs, firstPerChar = margin * perChar;
        // Unarmed only when the line reaches the deadline even at the SHORTEST prompt: a line useful for
        // short prompts stays armed, and past the deadline the total fires first (Codex, 2026-10-18).
        var shortestPrompt = healthy.Min(t => t.PromptChars);
        if (firstBase + firstPerChar * shortestPrompt >= totalDeadline.TotalMilliseconds)
            (firstBase, firstPerChar) = (null, null);

        TimeSpan? stall = TimeSpan.FromMilliseconds(Math.Ceiling(margin * healthy.Max(t => t.LongestGapMs)));
        if (stall >= totalDeadline)
            stall = null;
        return new LocalReplyWindows(firstBase, firstPerChar, stall);
    }

    /// <summary>The least-squares line through (chars, first-text ms) with its intercept raised so every
    /// point lies on or below it. One point (or one length) gives a flat line at the slowest point.</summary>
    internal static (double BaseMs, double MsPerChar) UpperEnvelope(IReadOnlyList<LocalReplyTiming> points)
    {
        var n = points.Count;
        var meanX = points.Average(p => (double)p.PromptChars);
        var meanY = points.Average(p => (double)p.FirstTextMs);
        double sxx = 0, sxy = 0;
        foreach (var p in points)
        {
            sxx += (p.PromptChars - meanX) * (p.PromptChars - meanX);
            sxy += (p.PromptChars - meanX) * (p.FirstTextMs - meanY);
        }
        // A negative slope would mean longer prompts read faster - noise; flat is the honest reading.
        var slope = n > 1 && sxx > 0 ? Math.Max(0, sxy / sxx) : 0;
        var intercept = meanY - slope * meanX;
        var lift = points.Max(p => p.FirstTextMs - (intercept + slope * p.PromptChars));
        // Never below zero at zero length: a steep line through two long prompts would otherwise give a
        // prompt shorter than any measured one almost no time. Raising the line keeps every point under it.
        return (Math.Max(0, intercept + Math.Max(0, lift)), slope);
    }

    /// <summary>How many healthy replies the windows would have cut off — must be zero before the
    /// windows ship (plan acceptance rule).</summary>
    internal static int FalseStalls(LocalReplyWindows windows, IReadOnlyList<LocalReplyTiming> healthy)
        => healthy.Count(t =>
            (windows.FirstTextFor(t.PromptChars) is { } first && t.FirstTextMs >= first.TotalMilliseconds)
            || (windows.Stall is { } stall && t.LongestGapMs >= stall.TotalMilliseconds));
}
