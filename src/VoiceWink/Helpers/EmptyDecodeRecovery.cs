namespace VoiceWink.Helpers;

/// <summary>What one chunk's decode did, once it had come back empty — the log line's whole content.
/// Carries the level it was judged from for the same reason <see cref="DecodeGainDecision"/> does:
/// the tripwire must report what was MEASURED, not just what was decided.</summary>
internal readonly record struct EmptyDecodeOutcome(
    double FirstGainDb,
    double? RetryGainDb,
    double ActiveRmsDbfs,
    bool Recovered)
{
    internal bool Retried => RetryGainDb is not null;
}

/// <summary>
/// TRN-10c (2026-08-14): one bounded re-decode, at ZERO gain, of a chunk that came back EMPTY after
/// being decoded with gain applied.
///
/// <para><b>The defect, measured on a retained recording (case A).</b> A 4.4 s dictation transcribed
/// to nothing and reported success. VAD had confirmed speech, peak was −8.6 dBFS, and TRN-10b's gain
/// fired correctly onto its −26 dBFS target. Through <c>tools/parakeet-long-audio</c> against the
/// shipped model: RAW (0 dB) returned a correct 24-character transcript on 3 of 3 runs; the SHIPPED
/// path (+7.7 dB) returned 0 chars on 5 of 5. Both deterministic, so a retry with identical input can
/// never recover anything — the retry has to CHANGE the input. (Transcript text is deliberately not
/// quoted anywhere in this repo: it is real dictation, and the evidence rule in
/// <c>.agent-collab/config.md</c> admits opaque IDs and counts only.)</para>
///
/// <para><b>The mechanism is a decision boundary, NOT "gain is harmful" — and the distinction
/// decides what may be claimed here.</b> The evidence is one PAIR of measurements at the same
/// nominal level: an <b>int16-domain</b> +7.7 dB of this clip (each level written to a WAV and
/// decoded raw) DECODES, while the shipped <b>float-domain</b> +7.7 dB does not. Same level,
/// opposite outcome ⇒ what flips it is sub-LSB numerical difference, not loudness. A 1 dB-step
/// int16 sweep of the same clip is also non-monotonic (24 chars at 0, empty at +1 and +2, 24 chars
/// at every step from +3 to +14) — <b>that sweep is int16-domain evidence and says nothing directly
/// about the float path</b>, which is why it is quoted with its domain or not at all; read as a
/// float-path result it would contradict the 5-of-5 above. Level was independently ruled out on the
/// day's corpus: seven recordings reached a HIGHER post-gain peak and five were driven into hard
/// clip, all decoding fine, while this clip logged <c>clipped=0</c>. <b>Do not rewrite this as a
/// level story, and do not respond to it by retuning <see cref="DecodeInputGain"/>'s target: the
/// instability is the defect, and every target sits on some boundary.</b></para>
///
/// <para><b>This is a counterexample to <see cref="DecodeInputGain"/>'s calibration note</b>, which
/// recorded boosting as "harmless at every level tried (+3..+18 dB)". That held for the two captures
/// it was measured on; it is not a general property, and a correction is carried beside every copy
/// of the claim rather than the claim being deleted.</para>
///
/// <para><b>ONE arm, deliberately — the symmetric version was built and removed.</b> The obvious
/// design is symmetric: gained-and-empty ⇒ retry raw, raw-and-empty ⇒ retry boosted. The second half
/// was implemented, then dropped in self-review, and the reasoning is recorded because it will look
/// like an omission. It had NO measured instance; the boost would have been computed from the
/// CHUNK's level, reversing <see cref="DecodeInputGain"/>'s recorded "per-chunk adaptation was
/// reviewed and rejected as an unmeasured divergence"; and it opened a failure the app did not
/// previously have — a pause inside an otherwise loud dictation measures as room tone on its own
/// chunk, decodes empty CORRECTLY, and would then have been boosted by up to +30 dB and re-decoded,
/// with anything it emitted joined into the middle of real speech. <see cref="DecodeInputGain"/>
/// states outright that real room tone is not covered by the noise-hallucination measurement. So the
/// arm traded a carded-but-never-observed residual for an injection path into good transcripts.
/// Symmetry is not a failure scenario.</para>
///
/// <para><b>The safety property, stated at its real strength.</b> A retry is only ever reached when
/// the first attempt produced NOTHING, and its result is taken only when it produces something. So
/// it can never replace or truncate text the user would otherwise have got. <b>It does NOT
/// guarantee the recovered text is correct</b> — for audio where empty was the RIGHT answer, adding
/// text is itself a degradation, and an RMS above <see cref="DecodeInputGain.SilenceFloorDbfs"/> is
/// a loudness fact, not evidence of speech. An earlier version of this file claimed the retry "can
/// do nothing else"; that was false, and the gate below is what the honest version required.</para>
///
/// <para><b>WHOLE-RECORDING SLICES ONLY — this is the injection gate, not an optimisation.</b> The
/// retry is refused unless the slice IS the entire recording. On a multi-chunk plan a background or
/// speech-free chunk can decode empty <i>correctly</i> while surrounding chunks carry real speech;
/// <c>ChunkedDecode</c> joins any non-empty part unconditionally, so a hallucinated retry would be
/// spliced into the middle of a good transcript. That is precisely the injection class the symmetric
/// boost arm was removed for (see below) — it survives here without amplification, and refusing
/// multi-chunk retries removes it structurally rather than by argument. Both measured field failures
/// are single-chunk, so this costs nothing measured. What it costs is UNMEASURED: a long recording
/// whose gained decode collapses on one chunk is not recovered, and the tripwire Warning is what
/// reports it. Do not widen this without speech evidence for the exact slice — the
/// <see cref="VoiceWink.Services.Transcription.VoiceActivityDetectionService"/> gate is
/// whole-recording, and the Audio Transcribe path has no gate at all.</para>
///
/// <para><b>TRN-22 WIDENED it, on exactly the evidence the paragraph above demands.</b> A
/// VAD-derived plan (<see cref="VadDecodePlan"/>) makes every chunk a VAD-POSITIVE speech segment —
/// per-slice speech evidence, which nothing in this pipeline could produce before. So the gate is
/// now "whole recording OR VAD-derived plan", and the injection risk it was protecting against does
/// not transfer: a segment the VAD called speech is not the background chunk this refusal was
/// written for. The retry remains ZERO-GAIN, so widening cannot invent level.
///
/// Widening was not optional for a VAD-default plan. Before TRN-22 every recording under ~35 s was
/// single-chunk and so recovery-eligible; moving ordinary dictation to multi-segment plans would
/// have SILENTLY removed that protection from the common case — measured: one segment decoded
/// 45 chars un-gained and 0 at the recording's whole-file gain. The residual (VAD false positive
/// plus a zero-gain invention) is what <c>LogEmptyDecode</c> reports.
///
/// <b>No live app caller passes the widened form today</b> — the VAD-default plan was rejected by
/// the registered TRN-22 gate, the service call site passes <c>plan.Count == 1</c> again, and the
/// widening's sole live user is <c>tools/parakeet-long-audio</c>'s r3 candidate arm. The rule
/// stays documented here because any future VAD-derived plan needs it on day one.</para>
///
/// <para>At zero gain the retry cannot clip and <see cref="DecodeInputGain.Apply"/> returns the
/// caller's array unchanged, so the retry adds no ARRAY allocation. It is not free: production still
/// builds a fresh recognizer stream and runs a second native decode.</para>
///
/// <para><b>Bounded to ONE extra decode, never on silence, cancellable between attempts.</b> Silence
/// decodes empty CORRECTLY, so retrying it would double the cost of every silent recording to
/// re-confirm a right answer. The level is measured LAZILY, only once a decode has come back empty,
/// so the healthy path pays nothing at all.</para>
///
/// <para><b>Measured LIMIT: a second failure the same day is NOT recovered, and that is stated
/// rather than glossed.</b> A 2.1 s clip (−35.3 dBFS, gained +9.3 dB) decoded empty at every LEVEL
/// from 0 to +18 dB, so the raw retry finds nothing for it and logs "still empty". Of the two field
/// failures on 2026-08-14 (2 of 164 gate-passing attempts, 1.2%) this recovers one. It is a partial
/// fix by measurement, not by oversight.</para>
///
/// <para><b>That second failure is CLOSED, not outstanding — do not build a fallback for it.</b>
/// Local Whisper was measured decoding the same clip cleanly (2026-08-15), so the audio is
/// intelligible and the fault is Parakeet-specific; the technically indicated fix was a second-engine
/// fallback, and the owner DECLINED it: the user chooses one model and the app does not mix in
/// another, the failure is self-correctable by re-dictating, and 1.2% is not frequent enough to earn
/// that entanglement. Accepted as a known limitation of this engine, with the tripwire Warning as its
/// detector and REL-12/REL-13's retained WAV + amber Retry as the user's recourse. Full record on the
/// TRN-10d entry in <c>backlog-archive.md</c>.</para>
///
/// <para>Pure apart from the decode delegate it is handed, so <c>EmptyDecodeRecoveryTests</c>
/// exercises every row with a fake decoder and no native code — the seam that mattered, since a real
/// decode needs the sherpa runtime and a 670 MB model.</para>
/// </summary>
internal static class EmptyDecodeRecovery
{
    /// <summary>
    /// The gain the SECOND attempt should use, or <c>null</c> for "do not retry". Split from the
    /// decode so every boundary is testable without constructing audio that lands on it.
    /// </summary>
    /// <param name="firstGainDb">What the first attempt ran with. Zero or less means it already ran
    /// unconditioned — <see cref="DecodeInputGain.Apply"/> treats any non-positive gain as raw — so
    /// there is no opposite conditioning left to try.</param>
    /// <param name="levels">The slice's levels, measured on the RAW samples.</param>
    /// <param name="sliceIsWholeRecording">False for any chunk of a multi-chunk plan. The injection
    /// gate: a retry's text is joined into whatever the other chunks produced, so it is refused
    /// wherever there is surrounding transcript to corrupt.</param>
    internal static double? AlternateGainDb(double firstGainDb, WavLevels? levels, bool sliceIsWholeRecording)
    {
        // Checked FIRST so the refusal cannot be reasoned around by any level or gain combination.
        if (!sliceIsWholeRecording) return null;

        // Unmeasurable: no basis to tell silence from speech. The empty result stands.
        if (levels is not { } l) return null;

        // Silence decodes empty CORRECTLY. Retrying it would double the cost of every silent
        // recording to re-confirm the answer we already have.
        if (l.ActiveRmsDbfs <= DecodeInputGain.SilenceFloorDbfs) return null;

        // The only arm. A gained decode that produced nothing gets one look at the unconditioned
        // signal — which is also what the app decoded before TRN-10b existed.
        return firstGainDb > 0 ? 0 : null;
    }

    /// <summary>
    /// Decode one chunk, retrying once at zero gain if the first attempt is empty.
    /// </summary>
    /// <param name="slice">The chunk's RAW samples. Never mutated — <paramref name="decodeAt"/>
    /// applies gain out-of-place (<see cref="DecodeInputGain.Apply"/> copies when it gains), and on a
    /// single-chunk plan this array IS the caller's whole recording.</param>
    /// <param name="decodeAt">Applies a gain and decodes: <c>(slice, gainDb) → text</c>. A
    /// <c>null</c> return is treated as empty, so a delegate that forgets to coalesce cannot put a
    /// null into a caller's <c>string</c>.</param>
    /// <param name="onEmpty">Reports the first empty decode and what became of it. Invoked at most
    /// once per call, and on EVERY empty first attempt — including when no retry was possible,
    /// because "empty, and we did not even try" is the case the tripwire most needs to keep saying
    /// out loud.</param>
    /// <param name="sliceIsWholeRecording">The injection gate — see the type remarks. The app's
    /// caller passes <c>plan.Count == 1</c>; the harness's r3 candidate arm passes the TRN-22
    /// widened form (<c>plan.Count == 1 || planIsVadDerived</c>); anything else refuses the retry.
    /// The parameter keeps its original name because the QUESTION it asks is unchanged — "may a
    /// retry's text be spliced in here" — and a VAD-positive segment answers it the same way a
    /// whole-recording slice does.</param>
    /// <param name="ct">Checked between the two attempts. The retry is native work of the same order
    /// as the decode that just failed, and a cancelled pipeline discards the result either way, so
    /// running it would be guaranteed-wasted work holding the service lock.</param>
    internal static string DecodeWithRecovery(
        float[] slice,
        int sampleRate,
        double gainDb,
        bool sliceIsWholeRecording,
        Func<float[], double, string> decodeAt,
        Action<EmptyDecodeOutcome>? onEmpty = null,
        CancellationToken ct = default)
    {
        var first = decodeAt(slice, gainDb) ?? string.Empty;
        if (first.Length > 0) return first;

        // Lazy, and on the RAW slice: the level has to mean the same thing as the gain printed
        // beside it, and the happy path must not pay for a full pass over the samples.
        var levels = WavLevelAnalyzer.Measure(slice, sampleRate);
        var alternate = AlternateGainDb(gainDb, levels, sliceIsWholeRecording);
        var activeRms = levels?.ActiveRmsDbfs ?? WavLevelAnalyzer.SilenceSentinelDbfs;

        if (alternate is not { } retryGain || ct.IsCancellationRequested)
        {
            onEmpty?.Invoke(new EmptyDecodeOutcome(gainDb, null, activeRms, Recovered: false));
            return first;
        }

        var second = decodeAt(slice, retryGain) ?? string.Empty;
        var recovered = second.Length > 0;
        onEmpty?.Invoke(new EmptyDecodeOutcome(gainDb, retryGain, activeRms, recovered));

        // `second` when it has content, else `first` — which is empty, so this returns an empty
        // string either way and the caller's empty-chunk handling is unchanged.
        return recovered ? second : first;
    }
}
