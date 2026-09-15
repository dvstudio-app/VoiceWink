namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for the background image job's DEFERRED completion presentation (IMG-BG,
/// 2026-07-16). A finished job may not present immediately: the pipeline could be mid-recording,
/// or a newer terminal presentation (a plain dictation's "Done", a cancel, another arm) may have
/// landed after the job completed — in which case the job's announcement must be dropped
/// entirely rather than overwrite newer state (the history row and image file survive either
/// way). Extracted from <c>MainViewModel</c> so the fence rules are unit-testable; the epoch
/// counter itself lives on the ViewModel (UI-thread-only monotonic long).
/// </summary>
internal static class ImageJobPresentationGate
{
    internal enum Decision
    {
        /// <summary>Pipeline idle and no newer presentation — apply now.</summary>
        Apply,

        /// <summary>Pipeline busy — keep the pending completion; the next Idle transition
        /// re-schedules the apply.</summary>
        Defer,

        /// <summary>A newer terminal presentation landed after the job completed — drop the
        /// pending completion (release its claims, apply nothing).</summary>
        Discard,
    }

    /// <summary>
    /// Gate for applying a pending completion. Epoch mismatch means SOMETHING presented after
    /// the job finished — the pending announcement lost the recency race (Codex plan rounds
    /// 2–3: an older image completion must never overwrite a newer transcription's
    /// LastTranscription or steal its pill).
    /// </summary>
    internal static Decision Decide(bool pipelineIdle, long pendingEpoch, long currentEpoch)
    {
        if (!pipelineIdle)
            return Decision.Defer;
        return pendingEpoch == currentEpoch ? Decision.Apply : Decision.Discard;
    }

    /// <summary>
    /// HIS-4, the BLUNT wipe rule (owner decision 2026-07-29, after 8 review rounds —
    /// see the plan trail's <c>12-owner-decision-simplify-blunt-wipe-rule.md</c>): a
    /// completion is stale once ANY image-covering bulk History delete ran after its
    /// job STARTED — no row-id correlation, no history-participation typing, no stamp
    /// floors. Over-discard is embraced by design: every wipe-during-job ordering
    /// (including all the per-row races review rounds 3/6/7/8 fenced individually)
    /// resolves to "the announcement and its redo arm are dropped"; the rows and files
    /// that survived the delete remain in History, where redo stays available. The
    /// discard releases the completion's claims, so a deleted row's reference copies
    /// are never re-pinned — which is the invariant that actually matters.
    /// </summary>
    internal static bool IsStaleAfterBulkDelete(long wipeStampAtJobStart, long currentStamp)
        => wipeStampAtJobStart != currentStamp;

    // NOTE: the former ResumeWinner/TimerResume (which affordance dismiss timer resumes at job end)
    // was removed with the ERR-PERSIST controller refactor (2026-07-21): the controller now owns pill
    // timing and pauses/resumes a TimedVisible affordance behind the idle image-job pill on its own.
}
