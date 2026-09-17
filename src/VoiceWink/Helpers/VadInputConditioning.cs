namespace VoiceWink.Helpers;

/// <summary>
/// AUD-24: bounded gain conditioning for the no-speech gate's INPUT COPY — the fix for the gate's
/// measured level blindness. Silero (the bundled ggml v6.2.0 build) collapses on quiet RAW audio:
/// five real-dictation recordings from 2026-08-25 scored ZERO speech segments at every threshold
/// down to 0.05, while the same audio gain-conditioned to ≈−26 dBFS active-RMS scored 25–31
/// SECONDS of speech at the SHIPPED threshold. The retune therefore changes the gate's input,
/// never its calibration: <c>VadTuning</c> is untouched, the REL-15 posture is preserved as far
/// as the block corpus can attest (real train noise, digital silence, white noise — all at
/// 0 segments even boosted +20 dB; the ORIGINAL transient-sound hallucination corpus no longer
/// exists, so those are its proxies — 10-plan.md records the bound), and the verdict still
/// attributes to the raw file, which is never modified on disk.
///
/// <para><b>Constants are VAD-OWN, not DecodeInputGain's</b> (its doc: "universal mechanism,
/// per-model application with its own constants"). Target −26 dBFS active-RMS matches TRN-10b;
/// the cap is <b>+30 dB since AUD-36 (2026-09-17)</b> — AUD-24 shipped +20 because a FLAT +26 dB
/// measurably degraded Silero (clipping distortion dropped two takes that only wanted +12/+13 back
/// to zero), but the target rule hands a take what it wants and no more: a take wanting +28 gets
/// +28 and one wanting +12 still gets +12 — and the claim rests on the MEASUREMENT, not the rule,
/// because <see cref="ConditionCopy"/> has no peak guard and a whispered take's crest factor
/// exceeds 26 dB (17:03:49: peak −18.6 vs −54.3 active → +28.3 clips its peaks). Whispered
/// open-office dictation (2026-09-17, −54…−58 dBFS active-RMS) wants +28…+32, and at the +20 cap
/// the ggml build scored ZERO segments on 7 of 14 takes; at +30 it passes 13 of 14 and the whole
/// registered A/B corpus is unchanged (the five +20-capped B takes gained +10…+920 ms of detected
/// speech, no take lost any) while the must-block corpus stays at zero — measured through
/// <c>tools/vad-gate-tune --cap=30</c>. And there is <b>deliberately NO silence floor</b> —
/// AUD-23's lesson (a −65 floor excluded real speech measured at −67), and boosting genuine
/// silence or noise is measured harmless here because the must-block corpus blocks at full gain.
/// Skip under +3 dB, the family convention.</para>
///
/// <para><b>What this cannot fix, on the record:</b> gain preserves the speech-to-noise ratio, so
/// speech buried DEEP in noise (the 2026-08-25 "D" take — real dictation the owner confirmed by
/// ear, invisible to Silero at every threshold and every gain) stays blocked. That class is
/// AUD-28's; the amber Retry remains its escape.</para>
/// </summary>
internal static class VadInputConditioning
{
    /// <summary>Where the conditioned copy's active-RMS should land — TRN-10b's decode target.</summary>
    internal const double TargetActiveRmsDbfs = -26.0;

    /// <summary>Hard gain ceiling — DecodeInputGain's +30 since AUD-36 (was +20: the AUD-24
    /// "+26 flat degraded Silero" measurement was an OVER-boost, which the target rule cannot
    /// produce; whispered dictation wants +28…+32 and blocked at +20).</summary>
    internal const double MaxGainDb = 30.0;

    /// <summary>Below this the boost is not worth a rewrite of the copy — family convention.</summary>
    internal const double MinWorthwhileGainDb = 3.0;

    /// <summary>Gain (dB) to apply to the gate's input copy; 0 = feed the raw bytes. Null levels
    /// (unmeasurable) condition nothing — the gate then behaves exactly as before AUD-24.</summary>
    internal static double DecideGainDb(WavLevels? levels) => DecideGainDb(levels, MaxGainDb);

    /// <summary>The same rule with the cap as a parameter — the harness's sensitivity lever
    /// (<c>tools/vad-gate-tune --cap=N</c>), so a candidate cap is measured through the SHIPPED
    /// decision rather than a re-implementation. Production takes the const overload only.</summary>
    internal static double DecideGainDb(WavLevels? levels, double maxGainDb)
    {
        if (levels is not { } l) return 0.0;
        var wanted = TargetActiveRmsDbfs - l.ActiveRmsDbfs;
        if (wanted < MinWorthwhileGainDb) return 0.0;
        return global::System.Math.Min(wanted, maxGainDb);
    }

    /// <summary>
    /// Apply <paramref name="gainDb"/> to a copy of a WAV's <c>data</c> chunk read as PCM16
    /// (clamped, header untouched). Returns the ORIGINAL array reference when gain is 0 or no
    /// usable <c>data</c> chunk is found — the caller feeds whatever comes back, so every
    /// degradation is "the gate sees the raw audio", never a failure. <b>This function does NOT
    /// verify the format itself</b> (no fmt-chunk read): the canonical-PCM16 guarantee comes from
    /// the CALLER's <c>WavLevelAnalyzer.Measure</c> gate, which returns null — and therefore gain
    /// 0 — for anything non-canonical. A future caller that skips that gate would scale a
    /// non-PCM16 data chunk as if it were PCM16 (self-review; both current callers gate).
    /// </summary>
    internal static byte[] ConditionCopy(byte[] wav, double gainDb)
    {
        if (gainDb <= 0.0 || wav.Length < 44 || wav[0] != 'R' || wav[8] != 'W') return wav;

        var pos = 12;
        var dataOffset = -1;
        var dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            var len = global::System.BitConverter.ToInt32(wav, pos + 4);
            if (len < 0) return wav;
            if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
            {
                dataOffset = pos + 8;
                dataLen = global::System.Math.Min(len, wav.Length - dataOffset);
                break;
            }
            pos += 8 + len + (len & 1);
        }
        if (dataOffset < 0 || dataLen < 2) return wav;

        var copy = (byte[])wav.Clone();
        var scale = global::System.Math.Pow(10.0, gainDb / 20.0);
        for (var i = dataOffset; i + 1 < dataOffset + dataLen; i += 2)
        {
            var sample = (short)(copy[i] | (copy[i + 1] << 8));
            var boosted = (int)global::System.Math.Round(sample * scale);
            var clamped = (short)global::System.Math.Clamp(boosted, short.MinValue, short.MaxValue);
            copy[i] = (byte)(clamped & 0xFF);
            copy[i + 1] = (byte)((clamped >> 8) & 0xFF);
        }
        return copy;
    }
}
