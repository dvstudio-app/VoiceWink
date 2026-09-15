using System;

namespace VoiceWink.Helpers;

/// <summary>
/// Screen-capture exclusion for the MiniRecorder pill (owner request 2026-07-30). The native call is
/// INJECTED so the apply / skip / retry-after-failure decisions are unit-testable without a window —
/// the launch-freeze rules also forbid growing <c>MiniRecorderWindow</c>'s responsibilities, so the
/// decision lives here and the window keeps only the plumbing.
/// </summary>
internal static class CaptureAffinity
{
    internal enum ApplyOutcome
    {
        /// <summary>The desired value already succeeded on this window — no native call made.</summary>
        Unchanged,
        Applied,
        Failed,
    }

    /// <summary>
    /// Apply <paramref name="exclude"/> to <paramref name="hwnd"/> via <paramref name="setAffinity"/>,
    /// tracking the last SUCCESSFULLY applied value in <paramref name="lastApplied"/> (null = never
    /// applied) so a redundant call is skipped.
    /// <para>A FAILURE IS NEVER RECORDED as applied: <paramref name="lastApplied"/> is left untouched,
    /// so the next call retries. A transient failure must not permanently give up on exclusion.</para>
    /// </summary>
    internal static ApplyOutcome Apply(IntPtr hwnd, bool exclude, ref bool? lastApplied,
                                       Func<IntPtr, uint, bool> setAffinity)
    {
        if (setAffinity is null) throw new ArgumentNullException(nameof(setAffinity));
        if (lastApplied == exclude)
            return ApplyOutcome.Unchanged;

        var affinity = exclude
            ? NativeInterop.WDA_EXCLUDEFROMCAPTURE
            : NativeInterop.WDA_NONE;
        if (!setAffinity(hwnd, affinity))
            return ApplyOutcome.Failed;

        lastApplied = exclude;
        return ApplyOutcome.Applied;
    }
}
