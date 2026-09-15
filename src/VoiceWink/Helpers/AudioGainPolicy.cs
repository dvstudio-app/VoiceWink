namespace VoiceWink.Helpers;

/// <summary>Outcome vocabulary for the AUD-2 gain decision AND the final log line. The two
/// applied-class members mean "gain &gt; 0 was decided"; the caller downgrades them to
/// ApplyFailed wording when the file replacement fails — the log must report what HAPPENED,
/// never just what was decided (Codex plan review).</summary>
internal enum GainReason
{
    Applied,            // full target (or max-gain-capped) boost
    HeadroomLimited,    // boosted, but clamped by the clip guard below the wanted gain
    SkippedAlreadyLoud,   // wanted gain under the worthwhile threshold — normal-voice recordings
    SkippedDigitalSilence,// every sample is exactly zero — there is nothing to amplify (AUD-23)
    SkippedHeadroom,    // clip guard left less than a worthwhile gain
    Unmeasurable,       // not a canonical WAV / unreadable — untouched
}

internal readonly record struct GainDecision(double GainDb, GainReason Reason)
{
    internal bool AppliesGain => GainDb > 0;
}

/// <summary>
/// AUD-2 (2026-07-31): THE pure decision for post-recording gain. Calibrated from the owner's
/// measured corpus (same room/mic): normal voice ≈ −38..−41 dBFS active-RMS, whispers −46..−59;
/// ElevenLabs transcribed whispers cleanly to −56 while Deepgram degraded past ≈ −50 and produced
/// the beta tester's empty results further down. Target −40 leaves normal voice untouched (below
/// the worthwhile threshold) and lifts the quietest measured whisper (−58.7) by ≈ +18.7 dB.
///
/// <para>The gates' honesty does NOT rest on these constants — the pipeline evaluates the
/// no-speech gate on the RAW file before any gain (see <c>RecordingAudioPreparation</c>).</para>
///
/// <para><b>AUD-23 (2026-08-26): the −65 dBFS silence FLOOR is gone; the skip is now the
/// digital-silence FACT.</b> The floor's own justification — recorded in the target's summary
/// below as "across the source day only silence-class recordings (≤ −65, correctly unboosted) sat
/// under Deepgram's ≈ −50 floor" — was falsified by the 2026-08-25 corpus. Of the five recordings
/// that day the floor skipped, only TWO were silence (the AUD-21 all-zero incident, −120/−120);
/// the other three were real audio at −67.1, −70.2 and −76.4 dBFS active-RMS, and the −67.1 take
/// decoded to 557 clean characters (13% WER). A cloud user in that configuration uploaded
/// −67 dBFS audio unboosted, far under the ≈ −50 detection floor this policy exists to clear, and
/// got an empty result the app could not explain. The floor was calibrated on one day's hot-mic
/// audio and does not describe a laptop array at a sane input level.</para>
///
/// <para><b>Why the fact and not a lower number.</b> Every level is a threshold somebody has to
/// re-derive when the hardware changes — the same trap in the same week produced AUD-25 (the −55
/// activity family) and AUD-24's decision to give the gate's conditioning NO floor at all.
/// <see cref="WavLevels.IsDigitallySilent"/> needs no calibration: on the canonical byte path the
/// smallest non-zero 16-bit sample is −90.3 dBFS, far above the −120 sentinel, so the sentinel is
/// reachable from all-zero input and nothing else. It separates exactly the two populations the
/// floor was conflating, and it is the SAME predicate AUD-21 already uses to tell a dead capture
/// from a quiet room.</para>
///
/// <para><b>Domain:</b> both production callers measure through <c>WavLevelAnalyzer.MeasureFile</c>,
/// the byte path where that biconditional holds. It does NOT hold on the float decode-input path,
/// where a tiny-but-non-zero peak clamps to the sentinel — no caller reaches here from there, and
/// if one ever did the failure is benign (it would skip boosting audio that is inaudible anyway),
/// so this is scoping rather than a guard.</para>
///
/// <para><b>What changed in behaviour:</b> recordings between the old floor and true digital
/// silence are now normalized like any other quiet recording — bounded by the same +20 dB cap, so
/// the −76.4 dBFS case still lands at −56.4 and is not "rescued", merely no longer discarded.
/// Boosting a recording that holds only room noise costs nothing: the no-speech gate has already
/// judged the RAW file by then, and a blocked WAV is deliberately retained normalized so its
/// retry replays boosted audio.</para>
///
/// <para><b>This is not the only gain layer, and the split is deliberate (TRN-10b).</b> This
/// policy rewrites the STORED WAV toward −40 for cloud detection floors; Parakeet's quiet-audio
/// collapse sits ABOVE that target (cliff ≈ −35.5 dBFS active-RMS), so <c>DecodeInputGain</c>
/// separately conditions that engine's in-memory decode input toward −26. On the recording path
/// the two compound (this one first, on disk); the file path gained this on-disk layer too in AUD-9, so a file run compounds identically to a recording (disk toward -40, then DecodeInputGain toward -26).
/// Raising THIS target instead was considered and rejected — it would re-tune a calibrated
/// cloud-facing instrument, rewrite users' stored audio, and feed Whisper hotter noise floors,
/// to fix an engine this policy was never calibrated for.</para>
/// </summary>
internal static class AudioGainPolicy
{
    /// <summary>CONFIRMED against a 40-file corpus, three engines (AUD-15, 2026-08-15). The TARGET
    /// half of that confirmation stands; the FLOOR half does not, and the floor is gone (AUD-23,
    /// 2026-08-26 — type doc above). Its claim was "across the source day only silence-class
    /// recordings (≤ −65, correctly unboosted) sat under Deepgram's ≈ −50 floor", and the
    /// 2026-08-25 corpus falsified it directly: real dictation at −67.1 dBFS decoded to 557 clean
    /// characters. Read that as the limit of a one-day, one-room, one-mic corpus rather than as a
    /// mistake — which is also why the re-tuning bar below is written the way it is.
    /// Accuracy half: gpt-transcribe and Deepgram are level-indifferent across −40…−28; local
    /// Whisper gains ~1 pp at hotter targets (3 of 40 files) — inside single-voice-corpus noise.
    /// The decisive interlock: a disk target at or above DecodeInputGain's −30 gate stops that
    /// layer renormalising, so Parakeet — the DEFAULT engine — decodes at the disk level instead
    /// of its corpus-optimal −26, which TRN-16 measured ~2 pp worse at −28. The viable disk band
    /// is therefore below −31, engines are indifferent within it, and −40 keeps maximum headroom
    /// against the noisy-room risk no corpus has measured. Re-tuning this needs a multi-voice,
    /// noisy-included corpus AND a fresh look at the interlock, not a nudge.</summary>
    internal const double TargetActiveRmsDbfs = -40.0;
    internal const double MaxGainDb = 20.0;
    internal const double MinWorthwhileGainDb = 3.0;
    internal const double PeakCeilingDbfs = -1.0;

    internal static GainDecision Decide(WavLevels? levels)
    {
        if (levels is not { } l)
            return new GainDecision(0, GainReason.Unmeasurable);
        // AUD-23: the fact, not a level. See the type doc for why the −65 dBFS floor was removed
        // rather than lowered — three of the five recordings it skipped on 2026-08-25 were real,
        // decodable speech, and one of them transcribed cleanly.
        if (l.IsDigitallySilent)
            return new GainDecision(0, GainReason.SkippedDigitalSilence);

        var wanted = TargetActiveRmsDbfs - l.ActiveRmsDbfs;
        if (wanted < MinWorthwhileGainDb)
            return new GainDecision(0, GainReason.SkippedAlreadyLoud);

        var gain = global::System.Math.Min(wanted, MaxGainDb);
        var headroom = PeakCeilingDbfs - l.PeakDbfs;
        if (gain <= headroom)
            return new GainDecision(gain, GainReason.Applied);
        if (headroom >= MinWorthwhileGainDb)
            return new GainDecision(headroom, GainReason.HeadroomLimited);
        return new GainDecision(0, GainReason.SkippedHeadroom);
    }
}
