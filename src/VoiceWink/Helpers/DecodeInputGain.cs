namespace VoiceWink.Helpers;

/// <summary>The whole-recording gain decision plus the level it was made from — the level rides
/// along because the log lines (and the empty-decode tripwire) must report what was MEASURED,
/// not just what was decided.</summary>
internal readonly record struct DecodeGainDecision(double GainDb, double ActiveRmsDbfs)
{
    internal bool AppliesGain => GainDb > 0;
}

/// <summary>
/// TRN-10b (2026-08-05): bounded boost-only gain for a local decoder's INPUT — in-memory float
/// domain, never the stored WAV. Built for, and wired only into, Parakeet; nothing in here is
/// Parakeet-specific, deliberately, so a future engine with its own measured cliff can adopt it
/// with its own constants (universal mechanism, per-model application — owner-reviewed decision).
///
/// <para><b>The defect this closes is SILENT and total.</b> Parakeet TDT (int8, sherpa-onnx)
/// collapses to an EMPTY result — all-or-nothing per decode call, worsening with slice length,
/// content-dependent at the margin — when the input is quiet. Measured on a real capture
/// (94 s, −37.4 dBFS active-RMS): 3 of 4 chunks and the whole-file single pass all decoded to
/// exactly 0 chars while every 5–15 s micro-slice of the same regions decoded fine, Whisper
/// scored 13.76% WER on the same file, and the app reported success throughout. +3 dB flipped
/// the file to fully decoding (cross-checked on a second capture); zero-filling the mic's
/// digital-silence gaps and ±1 LSB dither changed nothing — LEVEL is the lever. Cliff for that
/// content: between −35.5 (collapses) and −34.4 (decodes) active-RMS.</para>
///
/// <para><b>Why AUD-2 cannot be the fix:</b> <see cref="AudioGainPolicy"/> targets −40 active-RMS
/// (calibrated for CLOUD detection floors, Deepgram ≈ −50) and correctly decides
/// <c>SkippedAlreadyLoud</c> for everything above −43 — the Parakeet cliff sits ABOVE its target,
/// and the Audio Transcribe file path applies no gain at all. The two layers COMPOUND on the
/// recording path (a −58 whisper lands at −40 on disk via AUD-2, then −26 here) and this one
/// alone serves the file path.</para>
///
/// <para><b>Calibration (all measured 2026-08-05, active-RMS domain):</b> target −26 is the
/// center of the proven-good zone (the owner's live corpus runs −25..−33.5 and decodes fine,
/// including 177.7 s through 7 chunks). The boost gate −30 is an explicit owner decision
/// ("OK to start boosting a bit earlier than strictly necessary") over the reviewer-suggested
/// −33: margin over the worst measured collapse becomes 5.5 dB, and roughly half the owner's
/// daily recordings take a boost — accepted, because boosting measured harmless at every level
/// tried (+3..+18 dB, including 6 dB INTO hard clip) while under-boosting costs a whole silent
/// transcript. Minimum effective gain is Target − Gate = 4 dB by construction, which is why
/// there is no tiny-gain epsilon branch.</para>
///
/// <para><b>CORRECTION (TRN-10c, 2026-08-14): "harmless at every level tried" is NOT a general
/// property, and the counterexample comes from the owner's live corpus rather than synthesis.</b>
/// A 4.4 s recording at −33.7 dBFS decoded correctly RAW (3/3) and EMPTY through this policy's
/// +7.7 dB (5/5). <b>The cause is not loudness.</b> The evidence is one pair at the SAME nominal
/// level: an <b>int16-domain</b> +7.7 dB of that clip decodes while the shipped <b>float-domain</b>
/// +7.7 dB does not — so what flips it is sub-LSB numerical difference. (A 1 dB-step int16 sweep is
/// also non-monotonic, but that is int16-domain evidence and is never to be quoted as a float-path
/// result — read that way it would contradict the 5-of-5 above.) Level was ruled out separately:
/// seven recordings that day reached a HIGHER post-gain peak, five driven into hard clip, and all
/// decoded normally while this clip logged <c>clipped=0</c>. <b>The response is NOT to retune these
/// constants</b> — every target sits on some boundary, so moving one only relocates which clips land
/// on it. <see cref="EmptyDecodeRecovery"/> is the response: a gained decode that comes back empty
/// is re-decoded once at zero gain. The calibration above stands; only its generality claim was
/// wrong, and this paragraph is deliberately placed beside it rather than replacing it, so a reader
/// who greps for the original sentence lands on the correction too.</para>
///
/// <para><b>Scale-then-hard-clip, deliberately NOT AUD-2's peak clamp.</b> A transient defeats a
/// peak clamp: one −5.7 dBFS click over −35.5 speech (measured, second capture) would cap the
/// rescue at +4.7 dB where the model needs +9.5. Every experiment that established the lever ran
/// scale+clip, and +6 dB into clipping still decoded at full quality — we ship the transform
/// that was measured. Clipping touches only samples above −gain dBFS (speech RMS lands at −26)
/// and the count is surfaced to the caller for the log line.</para>
///
/// <para><b>Known residuals, each deliberate:</b> audio AT or ABOVE the −30 gate whose
/// content-dependent cliff sits higher than any measured collapse is never boosted — the
/// empty-decode tripwire in <c>ParakeetTranscriptionService</c> is the detector and this
/// constant is the named lever to revisit on first field hit. A loud-average recording
/// containing one sub-cliff quiet chunk gets no gain (the decision is whole-recording — the
/// measured configuration — not per-chunk); same detector. With <see cref="MaxGainDb"/> 30,
/// file-path audio below −56 active lands short of target (the recording path compounds AUD-2
/// first, so this affects only Audio Transcribe inputs).</para>
///
/// <para>Pure and deterministic — a retry replays the retained WAV byte-identically, so it
/// replays this decision too. Pinned by <c>DecodeInputGainTests</c>.</para>
/// </summary>
internal static class DecodeInputGain
{
    /// <summary>Center of the measured-good zone (owner live corpus −25..−33.5, all decoding).
    /// CONFIRMED against a 40-file corpus with cloud references (TRN-16, 2026-08-15): −26 was the
    /// sweep's accuracy optimum (11.46% aggregate word-edit vs 12.35–13.39% for −31/−28/−23/−20
    /// and 29.32% + 3 total collapses with the boost OFF), tied-best final-word retention, fewest
    /// TRN-15 rescue firings — and no candidate recovered a single final word this target loses.
    /// Neighbours are non-monotonic (−28 measured WORSE than both −31 and −26), so there is no
    /// smooth optimum to tune toward; re-tuning this needs a new corpus, not a nudge.</summary>
    internal const double TargetActiveRmsDbfs = -26.0;

    /// <summary>Boost everything below this. −30 is the owner's margin-over-purity call
    /// (2026-08-05); the type remarks carry the numbers.</summary>
    internal const double BoostBelowActiveRmsDbfs = -30.0;

    /// <summary>Cap so near-floor audio cannot demand absurd amplification. Same value class as
    /// AUD-2's cap reasoning, deliberately higher (+30 vs +20): no stored file is being rewritten,
    /// and the deepest whispers need the reach.</summary>
    internal const double MaxGainDb = 30.0;

    /// <summary>Signals at or below −65 dBFS active-RMS are not boosted. In-band noise ABOVE the
    /// floor IS boosted (a reviewer challenge), and the hallucination risk that implies was then
    /// MEASURED absent for synthetic uniform noise: noise-only input at −64.7/−54.7/−44.7 active,
    /// boosted +30/+28.7/+18.7 dB through the shipped chunk path, decoded to 0 chars at every
    /// level. Real room tone (fans, babble) is not covered by that synthesis — the empty-decode
    /// tripwire and owner UAT carry the residual.</summary>
    internal const double SilenceFloorDbfs = -65.0;

    /// <summary>Measure the WHOLE recording and decide once — the measured lever was uniform
    /// gain, and per-chunk adaptation was reviewed and rejected as an unmeasured divergence.</summary>
    internal static DecodeGainDecision Decide(ReadOnlySpan<float> samples, int sampleRate)
        => Decide(WavLevelAnalyzer.Measure(samples, sampleRate));

    /// <summary>The pure rule, split from the measurement so the exact gate boundaries are
    /// testable without constructing audio whose float-rounded level lands on them.</summary>
    internal static DecodeGainDecision Decide(WavLevels? levels)
    {
        if (levels is not { } l)
            return new DecodeGainDecision(0, WavLevelAnalyzer.SilenceSentinelDbfs);
        if (l.ActiveRmsDbfs <= SilenceFloorDbfs)
            return new DecodeGainDecision(0, l.ActiveRmsDbfs);
        if (l.ActiveRmsDbfs >= BoostBelowActiveRmsDbfs)
            return new DecodeGainDecision(0, l.ActiveRmsDbfs);

        var gain = global::System.Math.Min(TargetActiveRmsDbfs - l.ActiveRmsDbfs, MaxGainDb);
        return new DecodeGainDecision(gain, l.ActiveRmsDbfs);
    }

    /// <summary>
    /// Scale into a NEW array; hard-clip to [−1, 1]; count the clipped samples for the log.
    ///
    /// <para>Zero gain returns the SAME instance — that keeps the healthy path (≥ −30) zero-alloc
    /// and byte-identical. When gain applies the copy is mandatory, not an optimization:
    /// <c>ChunkedDecode.SliceFor</c> hands the ORIGINAL recording array through for single-chunk
    /// plans (test-pinned <c>Assert.Same</c>), and the harness reuses one <c>samples</c> array
    /// across modes in a single process — scaling in place would corrupt both. Cost: one
    /// full-length float alloc per gained slice (LOB at 25 s ≈ 1.6 MB), quiet recordings only.</para>
    /// </summary>
    internal static float[] Apply(float[] samples, double gainDb, out int clippedSamples)
    {
        clippedSamples = 0;
        if (gainDb <= 0) return samples;

        var gain = (float)global::System.Math.Pow(10.0, gainDb / 20.0);
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = samples[i] * gain;
            if (scaled > 1f) { scaled = 1f; clippedSamples++; }
            else if (scaled < -1f) { scaled = -1f; clippedSamples++; }
            result[i] = scaled;
        }
        return result;
    }
}
