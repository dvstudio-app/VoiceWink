namespace VoiceWink.Helpers;

/// <summary>
/// AUD-31: the pill waveform's level → visual-amplitude mapping. ONE fixed dB window plus a
/// resting amplitude floor. No state, no clock, no per-recording calibration — the same level
/// always renders the same height.
///
/// <para><b>This replaces AUD-29's relative mapping and AUD-30's usability ceiling, and the reason
/// is a measurement error in AUD-29's premise rather than a change of taste.</b> AUD-29 concluded
/// that no absolute mapping can serve every setup, from the owner's no-speech recordings reading
/// −35.7…−42.4 dBFS against a speaking take's −30.4 — a ~10 dB gap no fixed window can split.
/// Those figures are <c>WavLevelAnalyzer</c> ACTIVE-RMS, which selects its window set RELATIVE to
/// each file's own distribution (the windows above that file's own 20th-percentile window) and
/// then reports that set's ABSOLUTE dBFS level. So on a no-speech recording it reports that room's
/// own loudest windows, and two such numbers taken from two different files cannot be subtracted
/// to learn anything about one recording's internal dynamic range. Neither is the quantity this
/// mapping is fed.</para>
///
/// <para><b>What the control is actually fed is <c>AudioRecorderService.AveragePower</c></b> — an
/// EMA (α 0.6, in the dB DOMAIN) over one capture chunk per driver delivery period. Replaying 434
/// retained recordings (2026-08-23 → 2026-08-30 — the corpus as it stood at THIS measurement;
/// <c>Recordings\Debug</c> accumulates, so the tune sweep hours later saw 453) through a
/// simulation of that chain puts the
/// MEDIAN PER-RECORDING pause→speech gap at <b>22.6–44.8 dB</b> across the four days broken out —
/// never below 22, and 25.2 on 2026-08-26 itself, the day AUD-27/29/30 were all written (pause p10
/// medians −76.5…−85.6, speech p90 medians −51.6…−60.7). A fixed window separates them
/// comfortably.</para>
///
/// <para><b>The resting floor is what makes the window safe to place.</b> Every earlier defect in
/// this control came from a level falling outside the window being a FAILURE state: the pre-AUD-27
/// −60 floor rendered a quiet-but-working microphone dead flat, and AUD-27's move to −80 to rescue
/// it pinned ordinary rooms near full. With <see cref="RestAmplitude"/> underneath, falling below
/// the window costs RESPONSIVENESS, not correctness — the waveform stays visibly alive — so the
/// window can sit where speech actually lives instead of being stretched to cover every setup.
/// AUD-27's bug is closed by the rest amplitude, not by finding a better floor (the bottom anchor
/// initially moved to −85, and same-day owner UAT then set it to −80; both follow from measuring
/// the meter rather than from abandoning the resting-floor fix).</para>
///
/// <para><b>What "alive" means here, precisely, because the name invites a wrong reading.</b> This
/// is a floor on the AMPLITUDE ENVELOPE, not on a bar's rendered height.
/// <c>AudioVisualizerControl.OnAnimationTick</c> multiplies the value returned here by a per-bar
/// sine (0..1) and a centre boost (0.6..1.0), so every bar still returns to <c>BarMinHeight</c> at
/// its own trough. The bars are phase-offset, so what the floor guarantees is that the WAVEFORM
/// keeps rippling — the display goes uniformly flat only at amplitude 0, which is reserved for a
/// capture delivering nothing. Do not restate this as a minimum bar height.</para>
///
/// <para><b>AUD-30's protection comes for free and needed no ceiling.</b> An absolute window
/// already knows what "too quiet to carry speech" means: the 13%-input take the owner listened to
/// and called silence sits at meter p90 −84.1, which this maps to <b>0.08</b> — against the
/// deleted ceiling, which capped that same take at a 0.19 multiplier (0.15–0.29 across the two
/// 13% takes). Levels measured to transcribe (meter −74…−60) map to <b>0.35–0.70</b>.</para>
///
/// <para><b>UNIT WARNING — these anchors are in METER units, unlike AUD-27's and AUD-30's, which
/// were active-RMS.</b> That confusion produced four cards in five days. The <c>Audio level:</c>
/// log line is active-RMS and is NOT comparable: it runs 20+ dB above the meter's PAUSE level on
/// the same recording (at speech p90 the meter is the higher of the two, by 2–6 dB — AUD-30's own
/// measurement; the two statements are about different statistics, which is exactly the trap). To
/// re-tune, replay WAVs through the meter chain — chunk RMS in dBFS, snap on peak &gt;
/// <c>SnapThresholdDb</c>, then the α 0.6 dB-domain EMA — and never read an anchor off a log line.
/// <b>Nothing sets the capture chunk size:</b> both <c>UpdateMeters</c> call sites take whatever
/// stock NAudio <c>WasapiCapture</c> delivers, which this repo's durable figure puts at one driver
/// delivery period, 10–30 ms (<c>CaptureRingBuffer</c>). The corpus replay simulated 10 ms; that is
/// a property of the SIMULATION, not of the shipped chain.</para>
///
/// <para><b>At a very high Windows input level the bars sit high even in silence, and that is
/// INTENDED — it is an over-gain indicator, not a defect</b> (owner decision, 2026-08-30 UAT:
/// "you might even see it from the positive side… a visual indicator to the user that perhaps his
/// volume is way too loud"). The measurement that closes the question: at 100% input his IDLE floor
/// is −53 dBFS meter, while his SPEECH at 60% is −58 — idle at 100% is LOUDER than speech at a
/// working level. So any fixed mapping drawing −53 small must draw −58 smaller, i.e. fixing 100%
/// necessarily breaks the levels that read correctly. <b>Crest factor cannot rescue it either and
/// is REFUSED on measurement</b>: idle crest is 8.8–9.6 dB against speech's 6.5–6.6, so room noise
/// is PEAKIER than speech at 10 ms granularity and the term would run backwards. The only remaining
/// separator is a moving reference that learns the room — AUD-29, which this card removed. Do not
/// widen the window to chase 100%; the follow-up is a hint about the input level ([[AUD-32]]), not a
/// re-tune here.</para>
///
/// <para>Pure. Monotonic in level. Exactly 0 for digital silence, which is the AUD-21
/// guarantee.</para>
/// </summary>
internal static class WaveformAmplitude
{
    /// <summary>Level at or below which audio is treated as absent — the METER's idle value, which
    /// is <c>-160</c>. A capture delivering nothing at all renders dead flat, which is how the
    /// AUD-21 incident was diagnosed; that honesty is the one thing no "make it livelier" edit may
    /// take.
    ///
    /// <para><b>−160 and NOT the −120 this inherited, and the difference is load-bearing rather
    /// than tidy-up</b> (self-review Blocker). −120 is <c>WavLevelAnalyzer</c>'s sentinel, a
    /// different domain; it arrived here verbatim from the deleted <c>WaveformNoiseFloor</c>, where
    /// it was explicitly defence in depth because the deadband already returned 0. Here it is the
    /// ONLY path to 0, so the band it guards stopped being unreachable padding.
    /// <c>AudioRecorderService</c>'s meter EMA runs in the <b>dB domain</b> (α 0.6) and an all-zero
    /// chunk contributes −160 dB, so ONE such chunk drops the meter ~60 dB: from −60 to exactly
    /// −120.000, from the corpus pause level of −84 to −129.6. An APO-gated endpoint emits exactly
    /// those runs — AUD-18 measured the owner's own array gating 34.56% of samples, and AUD-10
    /// found ≥40 ms zero runs in 64 of 630 recordings — so at −120 the bars would snap to the
    /// AUD-21 dead-capture flat during ordinary PAUSES on that hardware, flickering against rest.
    /// Dead-capture flatness is spent just as surely by firing it while the capture is alive.
    /// −160 is where the meter actually idles (<c>ResetMeters</c>, and
    /// <c>Math.Max(rmsDb, -160f)</c> means nothing can go below it), so it is reachable only by a
    /// stream genuinely delivering nothing: only a latched capture reaches it, because it never fires
    /// the snap latch and sits pinned there.</para>
    ///
    /// <para><b>A stream that DIES mid-recording never reaches it, and that is deliberate</b>
    /// (Codex diff r1 — the test it asked for corrected this card's own prose, which had claimed
    /// convergence "in 180–190 ms" from an offline replay). Against the shipped code the EMA
    /// settles at ≈−159.99999 and stays there forever: <c>0.6f * -160f</c> rounds to exactly
    /// −96f in float32, so the iteration's fixed point is <c>-96f / (1f - 0.6f)</c>, just ABOVE
    /// the sentinel. Moving the guard up to catch that would put the threshold within reach of an
    /// APO gate's zero runs (AUD-10 found ≥40 ms runs — several chunks, enough to cross any
    /// threshold near −160), which is the pause-flicker this move exists to prevent. So the two
    /// stay distinguishable, which is AUD-30's <c>MinCeiling</c> reasoning again: NOTHING EVER
    /// ARRIVED reads dead flat, ARRIVED THEN STOPPED keeps a faint ripple at rest. Pinned against
    /// the real <c>UpdateMeters</c> by <c>AudioRecorderMeterTests</c>.</para></summary>
    internal const double SilenceDbfs = -160.0;

    /// <summary>Bottom of the window: at or below this the bars sit at <see cref="RestAmplitude"/>.
    ///
    /// <para><b>−80, raised from −85 by owner UAT on 2026-08-30</b> — *"the bars are a little bit
    /// too big when they are static… the static should be about half as high… silence should be
    /// really almost silent."* This is the lever that moves what he was looking at, and picking the
    /// other one would have done nearly nothing: his pauses sit at meter −80/−74/−71 (p15 of the
    /// three takes he made on the −85 build), which is ABOVE the old floor, so they were being
    /// rendered by the CURVE, not by <see cref="RestAmplitude"/>. Halving the rest constant alone
    /// was measured at 0.38 → 0.32; raising this floor to −80 gives 0.38 → 0.16, the requested
    /// half. On the full corpus at the time of the tune (453 recordings — 434 earlier the same day,
/// same folder, still growing), whose pauses are true room tone rather than
    /// inter-word gaps, it takes 0.28 → 0.08 — "almost silent" — while speech only moves 0.92 →
    /// 0.90.</para>
    ///
    /// <para><b>The general lesson, and why the first number was wrong:</b> a short take's quietest
    /// frame is an inter-word GAP that the α 0.6 EMA never settles into, not the room. Quoting a
    /// long-corpus pause percentile at a user who is testing with 3-second takes describes a
    /// different thing than what is on his screen.</para></summary>
    internal const double QuietDbfs = -80.0;

    /// <summary>Top of the window: at or above this the bars are full. Measured — corpus speech
    /// runs p90 ≈ −52…−61 with per-recording maxima around −45, so ordinary dictation reaches the
    /// top of the bar on its peaks without living there.</summary>
    internal const double LoudDbfs = -45.0;

    /// <summary>Floor on the amplitude envelope while any audio at all is arriving — NOT a floor on
    /// bar height (see the class summary). Deliberately non-zero, and the reason is the one above:
    /// it converts "outside the window" from a failure state into a calm one. Measured effect over
    /// the corpus — pauses render 0.08 and speech 0.90 (frame-wise medians over the quietest and
    /// loudest 15% of frames, a different statistic from the p10/p90 dB figures above).
    /// <para><b>0.08, reduced from 0.15 by the same 2026-08-30 owner UAT</b> ("silence should be
    /// really almost silent"). It stays non-zero for the reason above and for AUD-30's: rest must
    /// remain visibly below what real speech reaches, and a live-but-unusable input must stay
    /// distinguishable from a capture delivering nothing, which is the only thing that renders 0.
    /// At 0.08 the centre bar ripples between 4 and 6.2 DIP of the 32-DIP canvas; on speech (0.90)
    /// it reaches ≈29 of those 32 at the centre, less toward the edges. Note this constant was NOT
    /// the lever for the owner's complaint — <see cref="QuietDbfs"/> was; halving it is what makes
    /// true silence read as silence, which was the second half of the same report.</para></summary>
    internal const double RestAmplitude = 0.08;

    /// <summary>Perceptual curve over the window; 1.0 would be linear-in-dB. Unchanged from the
    /// pre-AUD-27 value (AUD-27 lowered it to 0.5 to lift a low end its floor had compressed;
    /// AUD-29 restored it).</summary>
    internal const double Exponent = 0.7;

    /// <summary>
    /// Map a meter level to a 0..1 visual amplitude. Exactly 0 at or below
    /// <see cref="SilenceDbfs"/>; <see cref="RestAmplitude"/> at or below <see cref="QuietDbfs"/>;
    /// exactly 1 at or above <see cref="LoudDbfs"/>.
    /// </summary>
    internal static double FromDb(double levelDb)
    {
        if (levelDb <= SilenceDbfs) return 0.0;

        var normalized = global::System.Math.Clamp(
            (levelDb - QuietDbfs) / (LoudDbfs - QuietDbfs), 0.0, 1.0);

        return RestAmplitude + (1.0 - RestAmplitude) * global::System.Math.Pow(normalized, Exponent);
    }
}
