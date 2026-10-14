namespace VoiceWink.Helpers;

/// <summary>
/// Time saved by dictating instead of typing — the ONE formula behind the Metrics page's "Time
/// Saved" card and, since 2026-10-14, Home's time-saved card (owner: a constant reminder of the
/// app's value). Moved out of <c>MetricsPage</c> so the two surfaces cannot show
/// different numbers.
/// </summary>
/// <remarks>
/// <para>Saved = (W/T + W/100·eₜ) − (W/D + W/100·eᵥ), for W lifetime words: T = typing speed,
/// D = careful dictation speed, eₜ / eᵥ = editing overhead in minutes per 100 words.</para>
///
/// <para>T was 60 — a confident touch typist — while voicewink.app's speed FAQ names 40 WPM as the
/// basis for its "4× faster than typing" claim, so the app and the site described different people.
/// Aligned on 40 by owner decision 2026-09-15. Both surfaces state the figure from this constant.</para>
/// </remarks>
internal static class TimeSavedEstimate
{
    public const double TypingWpm = 40.0;
    public const double DictationWpm = 130.0;
    public const double TypingEditPer100 = 0.1;   // typing needs minimal corrections
    public const double VoiceEditPer100 = 0.4;    // voice needs more review/fixes

    /// <summary>Minutes saved over <paramref name="totalWords"/> dictated words; 0 with no sessions.</summary>
    public static double Minutes(long totalWords, int sessions)
    {
        if (sessions <= 0 || totalWords <= 0) return 0;
        var w = (double)totalWords;
        var typing = w / TypingWpm + w / 100.0 * TypingEditPer100;
        var voice = w / DictationWpm + w / 100.0 * VoiceEditPer100;
        return Math.Max(0, typing - voice);
    }

    /// <summary>The Metrics card's value: whole minutes under an hour, hours to one decimal above.</summary>
    public static string CardText(double minutes) =>
        minutes < 60 ? $"{minutes:F0} min" : $"{minutes / 60:F1} hrs";

    /// <summary>The large figure on Home's time-saved card, shown from the very start (owner,
    /// 2026-10-14 — an hour's threshold hid it from new users): minutes until the first hour, then
    /// hours. Always rounded DOWN — an estimate shown as a reminder must never round up. Under a
    /// minute (a new install, or nothing dictated yet) reads "Less than a minute", never "0 minutes".</summary>
    public static string HomeValue(double minutes)
    {
        if (!(minutes >= 1)) return "Less than a minute";   // also NaN
        if (minutes < 60)
        {
            var wholeMinutes = (long)Math.Floor(minutes);
            return wholeMinutes == 1 ? "1 minute" : $"{wholeMinutes} minutes";
        }
        var hours = (long)Math.Floor(minutes / 60);
        return hours == 1 ? "1 hour" : $"{hours:N0} hours";
    }

    /// <summary>The line under the figure on Home's card.</summary>
    public const string HomeLabel = "saved with VoiceWink";

    /// <summary>The tooltip on Home's card: what the estimate assumes, and where the detail is.</summary>
    public static string HomeCardTooltip =>
        $"Estimated: typing at {TypingWpm:F0} words a minute compared with dictating, corrections " +
        "included. Opens Metrics.";
}
