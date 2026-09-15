using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for the MiniRecorder paste-target label (PILL-1): the small
/// "→ AppName" indicator shown while recording, so the user SEES where the paste
/// will go. Windows keyboard focus follows the last click/Alt-Tab — never the
/// user's eyes — and field incidents showed dictations landing in windows the
/// user wasn't looking at (a browser video page; an unrelated utility window).
/// The label reflects the RECORDING-START target — exactly the window the paste
/// pipeline will force-foreground — so no live focus re-tracking.
/// </summary>
internal static class MiniRecorderTargetLabel
{
    /// <summary>Hard cap on the display name — the label column is 160 DIP and the
    /// label must never crowd the visualizer.</summary>
    private const int MaxNameLength = 24;

    /// <summary>
    /// Compose the display label: null (hidden) for a missing name, else
    /// "→ {name}" — sanitized and capped with an ellipsis. The name comes from
    /// UNTRUSTED executable metadata, and this label is a trust cue — a bidi
    /// override in a malicious FileDescription must not visually spoof the target.
    ///
    /// <para>The sanitizing itself lives in <see cref="UntrustedTextSanitizer"/> since the
    /// local-model seam needs the identical treatment for a non-catalog model name. This method
    /// keeps only what is specific to the pill: the arrow prefix and the 24-char column budget.
    /// (That prefix is exactly why the sanitizer had to be extracted rather than reused in
    /// place — a caller wanting clean text would have got an arrow too.)</para>
    /// </summary>
    public static string? Compose(string? appName)
    {
        var clean = UntrustedTextSanitizer.Sanitize(appName, MaxNameLength);
        return clean is null ? null : "→ " + clean;
    }

    /// <summary>
    /// Fallback chain for the resolved process metadata: the friendly
    /// FileDescription ("Windows Terminal") when present, else the process name
    /// ("WindowsTerminal"), else null (no label).
    /// </summary>
    public static string? PickName(string? fileDescription, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription))
            return fileDescription;
        if (!string.IsNullOrWhiteSpace(processName))
            return processName;
        return null;
    }

    /// <summary>
    /// Logical capture identity for the stale-publish guard. Deliberately NOT
    /// reference equality: <c>EnrichPasteTargetSnapshotClass</c> replaces the
    /// snapshot with <c>original with { ClassName = … }</c>, so a valid app-name
    /// resolution must still publish after class enrichment won the CAS. Compares
    /// the fields the <c>with</c> mutation never touches.
    /// </summary>
    public static bool IsSameCapture(PasteTargetSnapshot? captured, PasteTargetSnapshot? current)
        => captured != null && current != null
        && captured.Hwnd == current.Hwnd
        && captured.Pid == current.Pid
        && captured.ThreadId == current.ThreadId
        && captured.CapturedAtUtc == current.CapturedAtUtc;

    /// <summary>
    /// The complete stale-publish decision: the resolver's generation must still be
    /// current (no reset happened — covers the zero-target History redo, where the
    /// snapshot deliberately survives) AND the snapshot must still be the same logical
    /// capture. The CALLER must evaluate this and the assignment as an atomic pair
    /// (under the same lock the reset takes) — checked-then-published without
    /// serialization re-opens the race this exists to close.
    /// </summary>
    public static bool ShouldPublish(
        int capturedGeneration, int currentGeneration,
        PasteTargetSnapshot? captured, PasteTargetSnapshot? current)
        => capturedGeneration == currentGeneration && IsSameCapture(captured, current);
}
