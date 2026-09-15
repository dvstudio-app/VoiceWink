namespace VoiceWink.Helpers;

/// <summary>
/// Pure bar-layout rule for the MiniRecorder waveform (AudioVisualizerControl): the bar
/// GEOMETRY (width/spacing/heights) is fixed — visual identity preserved — while the bar
/// COUNT derives from the width the pill actually gives the control, so the waveform fills
/// the free space between the target label and the stop button instead of floating as a
/// fixed 198-DIP island (owner 2026-07-10). Lives outside the control class because the
/// control's DependencyProperty statics need the WinUI runtime, which xUnit doesn't have —
/// this helper is what the tests pin (WaveformLayoutTests).
/// </summary>
internal static class WaveformLayout
{
    public const double BarWidth = 3;
    public const double BarSpacing = 2;

    /// <summary>
    /// Lower bound so a degenerate layout never collapses the waveform entirely. NOTE:
    /// below <c>CanvasWidthFor(MinBars)</c> = 38 DIP this clamp means the canvas exceeds
    /// the given width — deliberate (a visible waveform beats a vanished one), and
    /// unreachable in the real pill, whose star column is always ≳200 DIP. The
    /// overflow-proof guarantee therefore holds for widths ≥ 38 DIP (pinned by the
    /// sweep in WaveformLayoutTests).
    /// </summary>
    public const int MinBars = 8;

    /// <summary>Upper bound on allocation; 96 bars = 478 DIP, beyond the pill's inner width.</summary>
    public const int MaxBars = 96;

    /// <summary>Pre-layout fallback — the pre-2026-07-10 fixed count (PILL-2's 40 bars),
    /// used until the first SizeChanged supplies a real width.</summary>
    public const int DefaultBars = 40;

    /// <summary>
    /// How many bars fit the available width. Even count on purpose — the animation's
    /// center boost is symmetric around (count-1)/2 and an even count keeps the two
    /// tallest bars centered as a pair (matches the historic 32/40-bar look).
    /// </summary>
    public static int BarCountFor(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
            return DefaultBars;

        // n bars occupy n*BarWidth + (n-1)*BarSpacing ≤ width  ⇔  n ≤ (width+spacing)/(bar+spacing)
        var fit = (int)Math.Floor((availableWidth + BarSpacing) / (BarWidth + BarSpacing));
        fit -= fit % 2;
        return Math.Clamp(fit, MinBars, MaxBars);
    }

    /// <summary>The exact canvas width a bar count occupies (no trailing spacing).</summary>
    public static double CanvasWidthFor(int barCount)
        => barCount * BarWidth + (barCount - 1) * BarSpacing;
}
