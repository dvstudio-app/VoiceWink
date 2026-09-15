namespace VoiceWink.Helpers;

/// <summary>
/// Pure placement math for the user-draggable MiniRecorder pill (PILL-4).
///
/// The user's dragged position is persisted as a pair of FRACTIONS of the monitor
/// work area's available span (work size minus window size): 0 = left/top edge,
/// 1 = right/bottom edge, 0.5 = centered. Fractions are monitor- and DPI-independent,
/// so the pill lands at the same relative spot on whichever monitor it shows on
/// (the pill deliberately follows the monitor of the app being dictated into) and
/// survives resolution/scaling changes. No saved fraction → the historical default:
/// horizontally centered, top edge + padding.
///
/// All results are clamped fully inside the work area so a position saved on a large
/// monitor can never park the pill off-screen on a smaller one.
/// </summary>
public static class MiniRecorderUserPlacement
{
    /// <summary>
    /// Compute the pill's physical position inside a work area.
    /// <paramref name="fractionX"/>/<paramref name="fractionY"/> are the persisted
    /// user fractions (pass null when none is saved — yields the default placement).
    /// <paramref name="padding"/> is the default top margin in physical pixels
    /// (only used by the default placement).
    /// </summary>
    public static (int X, int Y) ComputePosition(
        int workLeft, int workTop, int workRight, int workBottom,
        int width, int height, int padding,
        double? fractionX, double? fractionY)
    {
        var spanX = (workRight - workLeft) - width;
        var spanY = (workBottom - workTop) - height;

        int x, y;
        if (Sanitize(fractionX) is double fx && Sanitize(fractionY) is double fy)
        {
            x = workLeft + (int)Math.Round(fx * Math.Max(0, spanX));
            y = workTop + (int)Math.Round(fy * Math.Max(0, spanY));
        }
        else
        {
            x = workLeft + spanX / 2;
            y = workTop + padding;
        }

        // Clamp fully on-screen; on a work area smaller than the pill, pin to the
        // top-left so at least the leading edge (dot + status) stays reachable.
        x = Math.Clamp(x, workLeft, Math.Max(workLeft, workLeft + spanX));
        y = Math.Clamp(y, workTop, Math.Max(workTop, workTop + spanY));
        return (x, y);
    }

    /// <summary>
    /// Inverse of <see cref="ComputePosition"/>: turn a dragged physical position into
    /// the persistable fractions, clamped to [0, 1]. A degenerate span (work area not
    /// larger than the pill) maps to 0 on that axis.
    /// </summary>
    public static (double FractionX, double FractionY) ComputeFraction(
        int x, int y,
        int workLeft, int workTop, int workRight, int workBottom,
        int width, int height)
    {
        var spanX = (workRight - workLeft) - width;
        var spanY = (workBottom - workTop) - height;
        var fx = spanX > 0 ? (x - workLeft) / (double)spanX : 0.0;
        var fy = spanY > 0 ? (y - workTop) / (double)spanY : 0.0;
        return (Math.Clamp(fx, 0.0, 1.0), Math.Clamp(fy, 0.0, 1.0));
    }

    /// <summary>
    /// A persisted fraction is usable only when finite and in [0, 1] — anything else
    /// (NaN/Infinity from a hand-edited settings file, or an out-of-range value from
    /// a future format change) falls back to the default placement rather than
    /// computing a garbage position.
    /// </summary>
    public static double? Sanitize(double? fraction) =>
        fraction is double f && double.IsFinite(f) && f >= 0.0 && f <= 1.0 ? f : null;
}
