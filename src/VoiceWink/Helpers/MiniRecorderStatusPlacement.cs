namespace VoiceWink.Helpers;

/// <summary>
/// Pure placement rule for the MiniRecorder pill's status text ("Transcribing...",
/// error/redo messages): centered on the PILL's midpoint when the text fits between
/// the left zone (dot/timer/target label) and the right zone (stop/redo buttons) with
/// clearance; otherwise centered in the space BETWEEN the zones (the pre-2026-07-09
/// behavior), which can never overlap either side. Owner request 2026-07-09: the left
/// zone is wider than the right, so between-zones centering read visibly right of the
/// pill's midpoint. Pinned by MiniRecorderStatusPlacementTests.
/// </summary>
internal static class MiniRecorderStatusPlacement
{
    /// <summary>Minimum gap kept between pill-centered status text and either zone.</summary>
    public const double ClearanceDips = 8;

    public enum Mode
    {
        /// <summary>Status text centered on the pill's midpoint (no side margins).</summary>
        CenteredOnPill,

        /// <summary>Status text centered between the left/right zones (side margins = zone widths).</summary>
        CenteredBetweenZones,
    }

    /// <param name="innerWidth">The pill's inner layout width (all columns).</param>
    /// <param name="leftZoneWidth">Total width of the visible left-side elements incl. margins.</param>
    /// <param name="rightZoneWidth">Total width of the visible right-side elements incl. margins.</param>
    /// <param name="statusDesiredWidth">The status text's desired (unconstrained) width.</param>
    public static Mode Decide(
        double innerWidth, double leftZoneWidth, double rightZoneWidth, double statusDesiredWidth)
    {
        // Pre-layout (no measured width yet): fall back — re-decided on the next layout pass.
        if (innerWidth <= 0)
            return Mode.CenteredBetweenZones;

        // Half-width available around the pill's midpoint before hitting the nearer zone.
        var half = innerWidth / 2;
        var freeHalf = Math.Min(half - leftZoneWidth, half - rightZoneWidth) - ClearanceDips;

        return statusDesiredWidth / 2 <= freeHalf ? Mode.CenteredOnPill : Mode.CenteredBetweenZones;
    }
}
