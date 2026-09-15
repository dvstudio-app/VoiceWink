using System;
using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// The post-finish display lifetimes for the MiniRecorder pill (PILL-3, owner request
/// 2026-07-09; ERR-PERSIST, owner request 2026-07-20). Two tone-driven TIMED lifetimes —
/// success and warning, both 10 s since the owner shortened them on 2026-08-23 —
/// plus an ERROR that never auto-dismisses at all: the user must always see a failure, even
/// after walking away, and clears it only via the corner × or a replacement transition.
///
/// <see cref="DeadlineFor"/> is the single source of truth for tone→lifetime; callers never
/// branch on tone themselves. Never stack surfaces (no "error for 3 s, then redo for 10 s") —
/// a finished pipeline shows exactly one surface.
/// </summary>
internal static class MiniRecorderTimings
{
    /// <summary>
    /// Post-finish dismiss when everything succeeded (green redo pill). 15 s until the owner
    /// shortened it on 2026-08-23. This is also the window in which the REDO affordance can be
    /// tapped, so the trade was made explicitly: a third less time to hit Redo, in exchange for a
    /// pill that stops sitting on screen after a successful dictation.
    /// </summary>
    public const int SuccessDismissSeconds = 10;

    /// <summary>
    /// Post-finish dismiss for a non-successful WARNING (amber) pill. 30 s → 15 s on 2026-08-02
    /// ("30 seconds is too long") → 10 s on 2026-08-23 (still too long), so it continues to MATCH
    /// <see cref="SuccessDismissSeconds"/>. Kept as its own constant deliberately: the two are
    /// separate tone decisions that happen to agree today, and collapsing them would make the next
    /// change to one silently move the other. (Errors never share this — see <see cref="DeadlineFor"/>.)
    /// </summary>
    public const int AttentionSeconds = 10;

    /// <summary>
    /// AUD-1: revert deadline for the transient pipeline-scoped notice ("Selected mic
    /// unavailable — using default"). Deliberately SHORTER than <see cref="AttentionSeconds"/>:
    /// the notice replaces the pipeline pill's status line, so a full-length overlay would hide the
    /// state/timer for most of a dictation; 8 s is enough to read and act (stop + fix the mic).
    ///
    /// LEFT AT 8 s by the owner on 2026-08-23 while the two post-finish pills were cut to 10 s, so
    /// the gap is now 2 s rather than 7. That is deliberate and this stays a SEPARATE constant: it
    /// is a different surface — an overlay DURING a recording, not a post-finish pill — so the
    /// "pills sit on screen too long" complaint the 10 s cut answers does not apply to it. Do not
    /// fold it into <see cref="AttentionSeconds"/> just because the numbers are now close.
    /// </summary>
    public const int PipelineNoticeSeconds = 8;

    /// <summary>
    /// The post-finish dismiss deadline for a pill of the given tone, or <c>null</c> for NO
    /// deadline. Error (red) → <c>null</c>: never auto-dismisses (ERR-PERSIST — cleared only by an
    /// explicit dismiss or a replacement transition). Warning → <see cref="AttentionSeconds"/>.
    /// Success → <see cref="SuccessDismissSeconds"/>. Pure — unit-testable without a dispatcher.
    /// </summary>
    public static TimeSpan? DeadlineFor(MiniRecorderTone tone) => tone switch
    {
        MiniRecorderTone.Error => null,
        MiniRecorderTone.Warning => TimeSpan.FromSeconds(AttentionSeconds),
        _ => TimeSpan.FromSeconds(SuccessDismissSeconds),
    };
}
