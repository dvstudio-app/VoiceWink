namespace VoiceWink.Helpers;

/// <summary>
/// TRN-22 (2026-08-20): turn raw VAD speech segments into a Parakeet decode plan.
///
/// <para><b>The defect this exists to fix is SILENT, and it is not the one TRN-10 fixed.</b>
/// TRN-10 cut recordings above 35 s because a single sherpa pass drops speech on LONG audio. But
/// ordinary dictation is 15–35 s, takes exactly one pass, and drops speech there too — measured on
/// the owner's own recordings: <c>recording_20260820_162114_…wav</c> (30.21 s) decoded 96 chars in
/// one pass while 7.5 s windows over the same file recovered 137 across four sentences, and
/// <c>recording_20260820_162023_…wav</c> (32.70 s) decoded 166 against 240 windowed — losing a
/// MID-recording clause with no warning anywhere, because the decode still reached the final
/// sentence so <see cref="TailRescue"/>'s trigger saw nothing wrong. Content-dependent, not a length
/// cap: a <c>0:15</c> slice of the first file dropped a sentence the full 30.21 s pass kept, so no
/// value of <see cref="LongAudioChunker.MaxChunkSeconds"/> fixes it.</para>
///
/// <para><b>Upstream never hands the recognizer a long buffer.</b> sherpa-onnx's own
/// <c>SileroVadModelConfig</c> defaults to <c>MaxSpeechDuration = 5</c> seconds, and its documented
/// long-audio path for NeMo transducer models — our family — is <c>sherpa-onnx-vad-with-offline-asr</c>:
/// VAD, then decode each speech segment, then join. This type is that second step.</para>
///
/// <para><b>Padding NEVER causes a merge.</b> The one measured trap. Word onsets clip without
/// padding (both repro files lost their first word), but padding generously enough to catch an onset
/// makes neighbours overlap, and merging on that overlap produced a 7.78 s window that reintroduced
/// the very frame-skipping this exists to fix — 151 chars collapsing back to 109. So merging is
/// decided on the RAW gap alone, and where two segments are kept apart their facing padding is
/// clamped to <c>rawGap/2</c> so the windows ABUT instead of overlapping. That is also what keeps
/// the non-overlap contract below literal rather than coalesced into truth after the fact.</para>
///
/// <para><b>Total coverage is deliberately GIVEN UP, and that is a different contract.</b>
/// <see cref="LongAudioChunker.Plan"/> covers <c>[0, samples.Length)</c> exactly and is test-pinned
/// on it. A VAD plan covers speech ∪ padding only. Non-speech holds no words by the VAD's judgement
/// — but the VAD can be wrong, and a missed tail would be invisible exactly the way the original
/// defect was. Two things answer that, neither of them optional: the fallback below when the VAD
/// finds nothing at all, and the HARNESS's coverage tripwire (it compares
/// <see cref="TailRescue.LastActiveTime"/> against the last planned segment's end and WARNS —
/// implemented in <c>tools/parakeet-long-audio</c> today, and it must return to the app together
/// with any future VAD-derived plan; decision delta 14 records that amendment). Do not remove
/// either believing the other covers it — they answer different failures.</para>
///
/// <para><b>ACCEPTED RESIDUAL, named so the acceptance is explicit:</b> a VAD miss BETWEEN two
/// detected segments is undetectable by construction. The tripwire compares the plan's END against
/// last activity, so it sees a lost tail and cannot see a lost middle. Closing that would mean
/// measuring activity in every inter-segment gap and deciding a threshold for "that was speech" —
/// which is a second VAD, with its own false-positive rate, guarding the first one. Recorded as a
/// known limit rather than designed around (Kimi diff round).</para>
///
/// <para><b>Every constant here is SEEDED, not settled.</b> They come from a two-file spike, and the
/// corpus sweep in <c>tools/parakeet-long-audio</c> is what earns them. Upstream's own long-audio
/// <c>threshold=0.2</c> is a worked example of why: on this audio it made the first repro WORSE
/// (151 → 123 → 109 chars at 0.5 → 0.2 → 0.1), because upstream's examples are loud reference
/// recordings and this corpus sits at −33…−39 dBFS active RMS. Their mechanism transfers; their
/// numbers do not.</para>
/// </summary>
/// <summary>A decode plan plus whether the VAD actually produced it.
///
/// <para><see cref="IsVadDerived"/> is REPORTED rather than inferred by the caller, because
/// <see cref="VadDecodePlan.From"/> falls back to <see cref="LongAudioChunker.Plan"/> on a degenerate
/// VAD result and the two outcomes are not reliably distinguishable from the plan's shape — a
/// single-segment VAD plan and a single-chunk fallback can both be one chunk. The distinction decides
/// whether <c>EmptyDecodeRecovery</c>'s gate may widen, so guessing it is not an option.</para></summary>
internal readonly record struct VadPlanResult(List<DecodeChunk> Plan, bool IsVadDerived);

internal static class VadDecodePlan
{
    /// <summary>Head and tail padding around each speech segment. The VAD reports onset late enough
    /// to clip the first word — measured on both repro files. Clamped against a neighbour so it can
    /// never manufacture an overlap; see the type remarks.</summary>
    internal const double PadSeconds = 0.4;

    /// <summary>Raw (UNPADDED) silence below which two segments become one decode. The VAD's own
    /// <c>MinSilenceDuration</c> has already joined anything shorter than its setting, so this is the
    /// plan-level pass that folds the leftover slivers a quiet speaker produces — the second repro
    /// emitted a 2-char and a 5-char segment without it.</summary>
    internal const double MergeGapSeconds = 0.35;

    /// <summary>
    /// Hard ceiling on one decode. **Upstream's own <c>MaxSpeechDuration</c> default**, and inside
    /// the band this repo has measured as reliable (3–4 s decoded correctly in the spike; 7.78 s did
    /// not).
    ///
    /// <para><b>It was 8.0 for one review round and that was wrong</b> — above the very 7.78 s
    /// window this type's remarks cite as having reintroduced the frame-skip (151 chars back to
    /// 109), on the one path where every loss detector is deliberately off: <see cref="TailRescue"/>
    /// watches only the final chunk, <c>CollapseRecovery</c> is coverage-gated off for VAD plans by
    /// design, and the caller's tripwire compares only the plan's END against last activity. A cap
    /// admitting a measured-failing length there would have shipped this change's own defect back
    /// (Kimi diff round).</para>
    ///
    /// <para><b>What is NOT claimed:</b> whether that 7.78 s failure was caused by LENGTH or by the
    /// padding-merge that produced the window. This file cites it in both roles and the two were
    /// never separated. The cap is therefore set conservatively rather than tuned — under
    /// uncertainty, on a detector-free path, the defensible choice is the value upstream ships and
    /// the corpus has not contradicted. The empirical cap sweep is the follow-up, not this
    /// constant's justification.</para>
    ///
    /// <para>A SAFETY NET, not the primary mechanism: natural silence does the splitting on real
    /// dictation (the first repro's plan was identical at 5, 10 and 20 s). The cost of cutting too
    /// eagerly is a split word — one word against a silently dropped sentence, which is the trade
    /// this whole change already makes.</para>
    /// </summary>
    internal const double MaxSegmentSeconds = 5.0;

    /// <summary>Floor on a planned segment. This is what makes a zero-length chunk unreachable BY
    /// CONSTRUCTION rather than caught downstream, the same standard
    /// <see cref="LongAudioChunker"/>'s no-stub precondition meets — and for the same measured
    /// reason: an empty waveform into sherpa is not a bad transcript, it is a process kill
    /// (<c>0xC0000409</c>, STATUS_STACK_BUFFER_OVERRUN, no managed exception to catch). Set below the
    /// VAD's own minimum speech duration so it only ever catches a degenerate segment.</summary>
    internal const double MinSegmentSeconds = 0.2;

    /// <summary>
    /// Contiguous, ASCENDING, NON-OVERLAPPING, NON-EMPTY chunks covering the VAD's speech plus
    /// padding — never the whole recording (see the type remarks on the coverage contract).
    ///
    /// <para>Falls back to <see cref="LongAudioChunker.Plan"/> — today's behaviour — whenever the VAD
    /// gives us nothing to work with: no segments, or nothing that clears
    /// <see cref="MinSegmentSeconds"/>. <b>An empty plan is never returned.</b> That would turn a
    /// recognition defect into total silence, which is strictly worse than the bug being fixed, and
    /// it is the one outcome this method must make impossible.</para>
    ///
    /// <para>Overlap is a CORRECTNESS bug, not an aesthetic one: <see cref="ChunkedDecode.Run"/>
    /// joins per-chunk text, so two windows over the same audio duplicate words into the
    /// transcript.</para>
    /// </summary>
    /// <param name="rawSegments">Speech segments from the VAD, ascending, in samples. Start/Count as
    /// the VAD reported them — UNPADDED, because the merge decision is taken on the raw gap.</param>
    internal static VadPlanResult From(
        IReadOnlyList<(int Start, int Count)> rawSegments,
        ReadOnlySpan<float> samples,
        int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (rawSegments.Count == 0) return Fallback(samples, sampleRate);

        var total = samples.Length;
        var pad = (int)(PadSeconds * sampleRate);
        var mergeGap = (int)(MergeGapSeconds * sampleRate);
        var minLen = Math.Max(1, (int)(MinSegmentSeconds * sampleRate));

        // ── 1. Merge on the RAW gap. Nothing about padding participates in this decision. ────────
        var merged = new List<(int Start, int End)>(rawSegments.Count);
        foreach (var (start, count) in rawSegments)
        {
            // Clamp into the buffer before anything reasons about the segment. A VAD that reports
            // past the end (or a caller that fed it a different buffer) must not index out of range.
            var from = Math.Clamp(start, 0, total);
            var to = Math.Clamp(start + count, from, total);
            if (to == from) continue;

            if (merged.Count > 0 && from - merged[^1].End <= mergeGap)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, to));
                continue;
            }

            merged.Add((from, to));
        }

        // The floor applies to the RAW merged segment, BEFORE padding. Padding only ever grows a
        // window, so applying it afterwards would lift a 50 ms sliver to 850 ms and decode it — the
        // floor would filter nothing and its own doc would be false. The question the floor asks is
        // "was this real speech", and only the unpadded detection can answer that.
        merged.RemoveAll(m => m.End - m.Start < minLen);

        if (merged.Count == 0) return Fallback(samples, sampleRate);

        // ── 2. Pad, clamping each facing side to rawGap/2 so kept-apart windows ABUT. ────────────
        var padded = new List<(int Start, int End)>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var (start, end) = merged[i];

            var headRoom = i == 0 ? start : (start - merged[i - 1].End) / 2;
            var tailRoom = i == merged.Count - 1 ? total - end : (merged[i + 1].Start - end) / 2;

            padded.Add((
                Math.Max(0, start - Math.Min(pad, headRoom)),
                Math.Min(total, end + Math.Min(pad, tailRoom))));
        }

        // ── 3. Cap length at the quietest point, then ── 4. apply the floor. ────────────────────
        var maxLen = (int)(MaxSegmentSeconds * sampleRate);
        var probe = Math.Max(1, (int)(LongAudioChunker.ProbeWindowSeconds * sampleRate));
        var plan = new List<DecodeChunk>(padded.Count);

        foreach (var (segStart, segEnd) in padded)
        {
            var start = segStart;
            var remaining = segEnd - start;

            while (remaining > maxLen)
            {
                // Reserve at least the floor for the tail. Otherwise an argmin landing at the
                // nominal ceiling leaves a sub-floor remainder that Emit silently DROPS — losing
                // real speech out of a valid VAD-positive segment, which is the exact failure class
                // this whole change exists to remove. Found by Codex on the diff round; the
                // over-cap test above checked chunk SIZE and never checked preservation.
                var latestCut = Math.Min(start + maxLen, segEnd - minLen);

                // Search the second half of the window, so a cut always makes progress and never
                // strands a sliver at the head.
                var cut = LongAudioChunker.FindQuietestPoint(
                    samples, start + maxLen / 2, latestCut, probe);

                if (cut <= start || cut >= segEnd) break;   // no progress: emit the rest whole
                Emit(plan, start, cut - start, minLen);
                remaining -= cut - start;
                start = cut;
            }

            Emit(plan, start, remaining, minLen);
        }

        // Everything fell below the floor — a degenerate VAD result. Today's behaviour, not silence.
        return plan.Count == 0 ? Fallback(samples, sampleRate) : new VadPlanResult(plan, true);
    }

    /// <summary>Today's chunk plan, flagged as NOT VAD-derived. The single exit for every degenerate
    /// VAD result, so the flag can never drift from the plan it describes.</summary>
    private static VadPlanResult Fallback(ReadOnlySpan<float> samples, int sampleRate)
        => new(LongAudioChunker.Plan(samples, sampleRate), false);

    /// <summary>Append a chunk unless it is below the floor. The floor drop is what upholds the
    /// non-empty contract; it is deliberately silent here because the CALLER owns observability —
    /// the same split <see cref="ChunkedDecode"/> documents, for the same reason (a logger
    /// dependency in a file <c>tools/parakeet-long-audio</c> links).</summary>
    private static void Emit(List<DecodeChunk> plan, int start, int count, int minLen)
    {
        if (count >= minLen) plan.Add(new DecodeChunk(start, count));
    }
}
