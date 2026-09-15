namespace VoiceWink.Helpers;

/// <summary>
/// The ONE place the length of the free trial (<c>LicenseStatus.FirstRunGrace</c>, the only try path
/// since LIC-21) is turned into words for the UI (2026-09-02, the onboarding License-step redesign).
///
/// <para><b>Why the length is derived and never typed.</b> The window is
/// <c>LicenseService.FirstRunGraceDuration</c> — 7 days since LIC-21 PR A (owner decision
/// 2026-09-06; 72 hours under the earlier LIC-4 design, and a 3650-day tester scaffold in between).
/// Copy that typed a number contradicted the shipped window every time the constant moved (the
/// repo neutralised its "14-day trial" copy for exactly that, LIC-4 item 1), and under the scaffold
/// a typed "3650 days" read as a bug. So the pages ask this helper: the constant went to 7 days and
/// the wizard said "7 days" with no copy edit, which is the property this type exists for.</para>
///
/// <para><b>The 60-day ceiling is shared with the countdown.</b>
/// <c>LicenseViewModel.TrialTimeRemainingDisplay</c> drops its precise count above the same boundary;
/// both read <see cref="MaxQuotedDays"/> so the offer and the countdown can never disagree about
/// which windows are real tryouts.</para>
/// </summary>
public static class TryoutWindowCopy
{
    /// <summary>
    /// Longest window the UI quotes as a number. Above it the window is not a trial (the retired
    /// 3650-day tester scaffold was the case that shipped), and callers render their sentence
    /// without a length.
    /// </summary>
    public const int MaxQuotedDays = 60;

    /// <summary>
    /// "7 days", "1 day", "36 hours", "1 hour" — or <c>null</c> when the window should not be
    /// quoted: non-positive, under an hour, or longer than <see cref="MaxQuotedDays"/>. A window
    /// that is not a whole number of days is floored to whole hours (a trial is never described
    /// to the minute).
    /// </summary>
    public static string? Describe(TimeSpan window)
    {
        if (window <= TimeSpan.Zero || window.TotalDays > MaxQuotedDays) return null;

        if (window.TotalDays >= 1 && window.TotalDays == Math.Floor(window.TotalDays))
        {
            var days = (int)window.TotalDays;
            return days == 1 ? "1 day" : $"{days} days";
        }

        var hours = (int)Math.Floor(window.TotalHours);
        if (hours < 1) return null;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    /// <summary>
    /// The running countdown — "Free trial — 2 days 5h remaining." Days-aware so a multi-day window
    /// reads in days rather than "317h 42m remaining."; hours+minutes precision once below a day,
    /// where minute granularity matters; the singular "day" when only one remains. <c>null</c>
    /// renders as zero ("0h 00m"), never as a blank line. Above <see cref="MaxQuotedDays"/> the
    /// precise count is dropped ("Your free trial is running.") — under the retired 3650-day tester
    /// scaffold "… 3650 days 0h remaining." read as a bug, and the same ceiling keeps the offer copy
    /// and the countdown agreeing about which windows are real trials.
    ///
    /// <para>Moved here from <c>LicenseViewModel</c> for LNC-11 (2026-09-13): the License page, the
    /// wizard AND the Home page now render this ONE string, so no page can grow a second countdown
    /// format. The ViewModel's <c>TrialTimeRemainingDisplay</c> delegates here.</para>
    /// </summary>
    public static string FormatRemaining(TimeSpan? remaining)
    {
        var r = remaining ?? TimeSpan.Zero;
        if (r.TotalDays > MaxQuotedDays)
        {
            return "Your free trial is running.";
        }
        if (r.TotalDays >= 1)
        {
            var dayWord = r.Days == 1 ? "day" : "days";
            return $"Free trial — {r.Days} {dayWord} {r.Hours}h remaining.";
        }
        return $"Free trial — {r.Hours}h {r.Minutes:D2}m remaining.";
    }
}
