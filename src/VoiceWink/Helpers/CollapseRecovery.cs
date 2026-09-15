namespace VoiceWink.Helpers;

/// <summary>
/// TRN-19 (2026-08-19): the trigger-and-acceptance decisions for recovering a NON-EMPTY collapsed
/// Parakeet decode. The measured incident: a 3.12 s recording with 2.36 s of voiced audio decoded
/// to five characters under TRN-10b's +12.2 dB while the RAW decode of the same samples returned
/// the full 49-character sentence — and the pipeline pasted the five characters, because every
/// existing guard keys on EMPTY ([[TRN-10c]]'s re-decode, TRN-18's surface) and five characters is
/// a successful decode by every test the pipeline applies.
///
/// <para><b>The trigger reuses a signal the pipeline already computes.</b> <c>TailRescue</c>'s gap
/// (last active 20 ms frame end − last token START) fired on the incident in production and
/// measured the loss exactly: 2.80 s on a 3.12 s recording. What it lacked was a denominator.
/// Corpus-calibrated on all 630 retained recordings (2026-08-19): gap ÷ duration at
/// <see cref="TriggerGapRatio"/> ≥ 0.70 fires on EXACTLY ONE file — the incident, at 0.90 — with
/// zero false positives; the next-highest ratio in the corpus is 0.63. Do not tune this constant
/// by reasoning; it is a measured boundary (TailRescue's header records what happened to every
/// reasoned lever). Known residual, accepted: <c>LastActiveTime</c> is an energy scan, not a
/// speech classifier, so late NON-SPEECH energy (a cough, a chair, the key release) inflates the
/// gap and can fire the trigger on a CORRECT transcript — which is why acceptance below never
/// trusts the trigger and demands the retry beat the original on TWO independent axes before any
/// user-visible text changes.</para>
///
/// <para><b>Acceptance is TIME COVERAGE, never text length — [[TRN-21]] is the measured reason.</b>
/// On 2026-08-19 the engine APPENDED invented words to a correct transcript on clean audio at
/// +0.0 dB: the longer decode was the WRONG one, by 2×. So length can never ACCEPT a retry. Three
/// gates, all required: (1) the retry's own token timeline must reach the speech (its gap ratio
/// under the same threshold); (2) the retry must cover DECISIVELY more than the original — its gap
/// at most half the original's (the card's recorded design: "keep whichever covers more of the
/// voiced audio", made comparative rather than a lone absolute bar, so a marginal 0.71-vs-0.69
/// difference inside timing noise can never flip a transcript); (3) a sanity floor: a retry that
/// kept LESS text than the collapse did cannot be its recovery — this is a REFUSAL bound only,
/// never an acceptance reason, and it is what stops a single invented late-starting token (the
/// TRN-21 shape) from passing the coverage gates on timing alone. Both decisions are pure; the
/// caller owns the one extra decode.</para>
///
/// <para><b>What this deliberately is NOT:</b> a second-engine fallback (owner-closed, TRN-10d), a
/// split-and-rejoin (refuted — splices confabulation), or a gain retune (forbidden on
/// <c>DecodeInputGain</c>, and the corpus measured the lever NET POSITIVE: 15 rescues to 1 harm on
/// single-chunk files — the constant must not move for the 1). It is TRN-10c's existing zero-gain
/// re-decode, reached by a trigger that can see a non-empty collapse. The whole-recording
/// injection gate is inherited: single-chunk plans only, same as <c>EmptyDecodeRecovery</c>, and
/// the retry only runs when the surviving text came from a GAINED decode — when zero gain already
/// produced the text, re-decoding at zero gain is the same decode again, and the symmetric
/// boosted arm stays removed (its injection failure is recorded on <c>EmptyDecodeRecovery</c>).
/// It is also NOT TailRescue's bigram-anchored merge: an agreement anchor was considered and
/// REJECTED on the incident's own evidence — the correct 49-char recovery shares zero words with
/// the 5-char collapse it replaces, so demanding agreement with collapse garbage would refuse
/// exactly the recovery this exists to perform.</para>
/// </summary>
internal static class CollapseRecovery
{
    /// <summary>Measured boundary, not a preference: 1 hit in 630 corpus recordings (the known
    /// collapse, at 0.90); next-highest healthy ratio 0.63. Re-run the corpus before moving it.</summary>
    internal const double TriggerGapRatio = 0.70;

    /// <summary>The comparative acceptance bar: the retry's gap must be at most the original's
    /// divided by this. 2.0 = "at least halve the uncovered speech" — scale-free (a 1 s recording
    /// and a 30 s one get the same relative bar), decisively outside token-start jitter (~0.1–0.3 s
    /// on corpus timings), and comfortably met by the incident (2.80 s → ~0.24 s, an 11.7× shrink).</summary>
    internal const double MinGapShrinkFactor = 2.0;

    /// <summary>Should the one zero-gain re-decode run at all?</summary>
    /// <param name="planCoversWholeRecording">The whole-recording injection gate: multi-chunk
    /// collapse semantics are per-chunk and unmeasured, and a hallucinated retry would be spliced
    /// into good neighbours (the EmptyDecodeRecovery gate, inherited verbatim).
    ///
    /// <para><b>Was <c>chunkCount == 1</c> until TRN-22, and the count was only ever a PROXY for
    /// what this doc already called it.</b> Once the plan comes from a VAD, the proxy breaks in the
    /// worst direction: a single-segment VAD plan satisfies <c>chunkCount == 1</c> while EXCLUDING
    /// audio, and the retry decodes the whole raw buffer — so the "recovery" would swap in exactly
    /// the whole-buffer decode shape TRN-22 removes, and its acceptance test (the token timeline
    /// reaching the last active frame) is PASSED by the defect's own signature, which reaches the
    /// final sentence while skipping the middle. Gate on coverage, not on the count.</para></param>
    /// <param name="joinedText">The surviving transcript (post-TailRescue-append — reachable here
    /// only when the append count was ZERO, per <paramref name="tailRescueAppended"/>, so the text
    /// judged is in practice the un-appended decode). EMPTY is TRN-10c's jurisdiction — this
    /// trigger exists precisely for the non-empty collapse it cannot see.</param>
    /// <param name="survivingGainDb">The gain that produced the surviving text. Zero means the
    /// text already IS the raw decode (TRN-10c recovered it, or the recording took no gain) — a
    /// zero-gain retry would reproduce it.</param>
    /// <param name="gapSeconds">TailRescue's gap for the final (sole) chunk — non-zero only when
    /// its trigger fired or the gap exceeded its rescue window; 0 means healthy timing.</param>
    /// <param name="durationSeconds">The recording's duration.</param>
    /// <param name="tailRescueAppended">True when TailRescue FIRED AND APPENDED anchored words.
    /// An anchored append is the pipeline's highest-confidence repair (bigram-verified against the
    /// main decode), and on a short recording four appended words can span the very ≥70% gap this
    /// trigger keys on — the gap is PRE-append, so without this refusal a successful rescue would
    /// launch an unanchored whole-transcript replacement of its own repaired text (Codex diff
    /// round, 2026-08-19). The incident is preserved: its rescue appended ZERO words (no anchor
    /// exists in collapse garbage), which is precisely what separates a repaired tail from an
    /// unrepaired collapse. No default — compile-forced at every call site.</param>
    internal static bool ShouldAttempt(
        bool planCoversWholeRecording, string joinedText, double survivingGainDb, double gapSeconds,
        double durationSeconds, bool tailRescueAppended)
        => planCoversWholeRecording
           && !string.IsNullOrWhiteSpace(joinedText)
           && !tailRescueAppended
           && survivingGainDb > 0
           && durationSeconds > 0
           // Written as `>=` over a computed ratio; NaN from a 0/0 cannot reach here (both
           // operands are guarded above), and a negative gap fails the comparison naturally.
           && gapSeconds / durationSeconds >= TriggerGapRatio;

    /// <summary>Accept the retry ONLY when it beats the original on every gate: its own token
    /// timeline reaches the speech, it covers decisively MORE than the original (gap at least
    /// halved), and it kept at least as much text as the collapse plus one character (a refusal
    /// floor — text length is deliberately never an ACCEPTANCE reason; TRN-21: longer was wrong
    /// by 2×).</summary>
    /// <param name="originalText">The surviving transcript the trigger fired on.</param>
    /// <param name="originalGapSeconds">The original decode's gap — the same value the trigger
    /// judged. The comparative gate is against THIS, so "the retry covers more" is measured
    /// against the decode it would replace, never against a fixed bar alone.</param>
    /// <param name="retryText">The zero-gain decode's text. Empty ⇒ the retry found nothing and
    /// the original stands (this recovery never trades text for nothing).</param>
    /// <param name="retryTimestamps">The retry's per-token start times. Null/empty ⇒ no timing
    /// evidence ⇒ keep the original — timing we cannot trust is timing we do not act on
    /// (TailRescue's rule, inherited).</param>
    /// <param name="lastActiveSeconds">The recording's last active 20 ms frame end
    /// (<c>TailRescue.LastActiveTime</c> over the SAME raw samples the trigger judged).
    /// Its "no active frame" sentinel is −1, and −1 minus any token start is a large negative
    /// gap that would read as full coverage — so non-positive is refused HERE, not left to a
    /// caller invariant.</param>
    /// <param name="durationSeconds">The recording's duration.</param>
    internal static bool ShouldSwap(
        string originalText, double originalGapSeconds,
        string retryText, IReadOnlyList<float>? retryTimestamps,
        double lastActiveSeconds, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(retryText)) return false;
        if (retryTimestamps is null || retryTimestamps.Count == 0) return false;
        if (durationSeconds <= 0) return false;
        // TailRescue.LastActiveTime returns −1 for "no measurable speech"; NaN is refused by the
        // same negated comparison. An unmeasurable recording must read as "no evidence", never as
        // "full coverage" — without this line, −1 fail-opens into a swap.
        if (!(lastActiveSeconds > 0)) return false;

        // Sanity floor, refusal-only: a recovery of a collapse must recover MORE text than the
        // collapse kept. A shorter-or-equal retry is evidence of a second collapse (or of one
        // invented token — the TRN-21 shape), never of recovery. This can only ever REFUSE;
        // acceptance still requires the coverage gates below.
        if (retryText.Length <= (originalText?.Length ?? 0)) return false;

        var lastTokenStart = retryTimestamps[^1];
        // A last token starting at exactly 0 is the timing-regression signature TailRescue refuses
        // for the same reason; !(x > 0) also refuses NaN, which would otherwise pass the ratio
        // comparison below (NaN comparisons are false, and `false` there means SWAP — fail-open).
        // A token starting AFTER the recording ends is equally invalid evidence — its strongly
        // negative gap would read as maximal coverage (Codex diff round, 2026-08-19): timing we
        // cannot trust is timing we do not act on, in both directions.
        if (!(lastTokenStart > 0) || lastTokenStart > durationSeconds) return false;

        var retryGap = lastActiveSeconds - lastTokenStart;
        // Gate 1, absolute: the retry itself must look healthy — its own gap ratio under the same
        // measured threshold. Negated so a NaN ratio (NaN lastActive is already refused above;
        // this is defense in depth) refuses rather than swaps.
        if (!(retryGap / durationSeconds < TriggerGapRatio)) return false;

        // Gate 2, comparative — the card's recorded design ("keep whichever covers more of the
        // voiced audio"): the retry must at least HALVE the uncovered gap. A marginal difference
        // inside token-timing noise never flips a transcript; a NaN originalGap refuses (NaN
        // comparisons are false and `false` here means keep).
        return retryGap <= originalGapSeconds / MinGapShrinkFactor;
    }
}
