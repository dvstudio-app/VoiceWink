namespace VoiceWink.Helpers;

/// <summary>
/// Resolved main-window geometry: a size that fits the work area, centered within it.
///
/// <para><see cref="ShouldMove"/> is false when the work area could not be trusted, and the caller
/// must then RESIZE ONLY (Codex diff review). <see cref="X"/>/<see cref="Y"/> still carry the
/// reported origin for logging, but moving to an untrusted origin — a stale negative one, say —
/// can place the window off-screen, which is the failure this type exists to prevent.</para>
/// </summary>
public readonly record struct WindowPlacement(int Width, int Height, int X, int Y, bool ShouldMove);

/// <summary>
/// Pure placement math for the MAIN window (owner finding, 2026-08-02). The sibling of
/// <see cref="MiniRecorderUserPlacement"/>, and it exists for the same reason: geometry that is
/// wrong only on hardware the developer does not have needs to be testable without that hardware.
///
/// <para><b>The bug it fixes.</b> The preferred size is a fixed 950x730 DIP multiplied by the
/// display scale and applied unclamped. At 150% — a Windows default on many 15" 1080p laptops —
/// 730 DIP is 1095 px against a work area of roughly 1040 px. Three things then break together: the
/// window is taller than the screen, the centering arithmetic
/// <c>(workAreaHeight - windowHeight) / 2</c> goes NEGATIVE and puts the title bar out of reach, and
/// the enhancement dialog's <c>ContentDialogMaxHeight</c> — derived from this window's height —
/// grows past the screen and carries its buttons off with it.</para>
///
/// <para>What matters is scale WITHOUT the pixels to back it, not a high scale factor by itself: a
/// 4K panel at 200% wants 1460 px and has ~2100 px of work area, so it needs no clamping at all
/// (Codex diff review r2 — an earlier version of this comment cited it as a breaking case while the
/// test proved the opposite).</para>
///
/// <para><b>Clamp, never grow.</b> The preferred size is a maximum, not a target: a work area
/// larger than the preferred size leaves the window at the preferred size. Only the too-big
/// direction is corrected, because that is the only direction that makes the window unusable.</para>
///
/// <para>The caller keeps the fail-soft path for an unavailable work area — an oversized window is
/// recoverable (the user can resize it), a window that failed to appear is not.</para>
/// </summary>
public static class MainWindowPlacement
{
    /// <summary>
    /// Clamp <paramref name="preferredWidth"/>/<paramref name="preferredHeight"/> to the work area
    /// and center the result inside it. A degenerate work area (either dimension &lt;= 0) is not
    /// trusted and yields the preferred size at the work-area origin — the same "recoverable
    /// oversize beats no window" rule the caller applies when the lookup throws.
    /// </summary>
    public static WindowPlacement ClampToWorkArea(
        int preferredWidth, int preferredHeight,
        int workAreaX, int workAreaY, int workAreaWidth, int workAreaHeight)
    {
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
            return new WindowPlacement(
                preferredWidth, preferredHeight, workAreaX, workAreaY, ShouldMove: false);

        var width = global::System.Math.Min(preferredWidth, workAreaWidth);
        var height = global::System.Math.Min(preferredHeight, workAreaHeight);

        // Max against the origin is belt-and-braces: the clamp above already makes these
        // differences non-negative, and it keeps that true if either value is later changed
        // independently of the other.
        var x = global::System.Math.Max(workAreaX, workAreaX + (workAreaWidth - width) / 2);
        var y = global::System.Math.Max(workAreaY, workAreaY + (workAreaHeight - height) / 2);

        return new WindowPlacement(width, height, x, y, ShouldMove: true);
    }
}
