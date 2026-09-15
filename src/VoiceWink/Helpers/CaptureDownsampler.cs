// File-scoped so this compiles warning-free in BOTH consumers: the app project (nullable enabled)
// and tools/resampler-aliasing, which links this file. It was REQUIRED while that harness was
// `Nullable disable` (the `float[]?` on _kernels raised CS8632 there); the harness moved to
// `Nullable annotations` when it also linked CaptureFormatConverter, so the directive is now
// redundant for it and harmless in the app. Kept rather than churned. Kimi plan review, adjustment 4.
#nullable enable

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-8 (2026-08-04): the capture path's sample-rate conversion WITH an anti-alias low-pass, and
/// with the filter state plus the fractional read phase carried across capture buffers.
///
/// <para>Replaces the per-buffer, stateless <see cref="AudioResampler.ResampleInto"/> on the
/// enabled path. That method is named for linear interpolation but 48000/16000 is exactly 3.0, so
/// <c>frac</c> was always 0 and the output was bit-identical to take-every-third-sample —
/// <c>tools/resampler-aliasing</c> measured 100% of 8-24 kHz energy folding into the speech band.
/// <see cref="AudioResampler"/> stays in the tree as the kill-switch path and as the harness's
/// "shipped today" baseline; it is deliberately NOT modified.</para>
///
/// <para><b>Two stages, and a THREE-WAY branch on the rate relation</b> (Kimi plan review's
/// blocking find — the first design claimed "one code path for every device rate" and produced an
/// invalid filter below 14.4 kHz):</para>
/// <list type="bullet">
///   <item><c>source &gt; target</c> — the rate is being REDUCED, so aliasing is possible and this
///   is what the card is about. Stage A filters, Stage B decimates.</item>
///   <item><c>source == target</c> — no rate change, nothing folds. Bit-exact passthrough,
///   preserving the behaviour <c>AudioRecorderService</c> already had. A filter here would be
///   valid but would be pure signal loss: it would strip genuine 7.2-8 kHz content the WAV
///   currently carries, to fix a problem that is absent.</item>
///   <item><c>source &lt; target</c> — upsampling cannot alias, and this is exactly the range
///   where the tap design is invalid: the cutoff is a fixed 0.45x TARGET, so normalised
///   <c>fc = 7200/sourceRate</c> exceeds 0.5 for any source below 14.4 kHz (an 8 kHz hands-free
///   endpoint would give 0.9, and the windowed-sinc formula then describes no low-pass at all).
///   Stage B alone. Kernels are never DESIGNED on this branch rather than being built and skipped.</item>
/// </list>
///
/// <para><b>Filter:</b> Blackman-windowed sinc, cutoff 0.45x target, normalised to unity DC gain,
/// 401 taps at 48 kHz (rate-scaled — see <see cref="TapsFor"/>). The design is not a fresh choice —
/// it is exactly the reference <c>tools/resampler-aliasing</c> already characterises at 85-98 dB
/// rejection, so production and reference converge and the harness re-run confirms the shipped path
/// reaches known figures. <b>Evaluated ON DEMAND at each output's read position</b>
/// (<see cref="ValueAt"/>) — the whole-buffer alternative was implemented first, measured at 69.3% of
/// the buffer period at 192 kHz in the shipped build configuration, and replaced for it (details in
/// the cost paragraph below; <c>Process</c> carries the mechanism). NAudio's <c>WdlResampler</c> was
/// rejected for one reason: its own filter design and buffering are not the measured reference, so
/// adopting it would forfeit the only verification available short of a WER measurement nobody can
/// run yet.</para>
///
/// <para><b>Fractional positions are served by a PHASE-INTERPOLATED KERNEL BANK, not by linearly
/// interpolating two filtered samples</b> (Codex r7, High). The first implementation interpolated
/// between <c>ValueAt(idx)</c> and <c>ValueAt(idx+1)</c>; that two-point reconstruction has a sinc²
/// response which passes the sampled signal's spectral images attenuated but not gone, and sampling
/// the result at 16 kHz folds them back IN BAND — a clean 6.5 kHz tone measured spurs at
/// <b>-31 dB from 44.1 kHz and -17.7 dB from 22.05 kHz</b> capture (harness §10; integer ratios
/// never interpolate and were always clean). A doc sentence claiming "nothing left to alias" had
/// conflated out-of-band aliasing, which Stage A removes, with in-band imaging, which it cannot.
/// The bank holds <see cref="PhaseBankSize"/>+1 kernels, each the SAME windowed-sinc design
/// evaluated at a fractional tap offset p/<see cref="PhaseBankSize"/>; an output at fractional
/// position blends the two adjacent phase kernels. Blending kernels is algebraically one filter
/// whose shape is the linear interpolation of two neighbouring fractional-delay filters — its
/// residual error is the kernel's curvature across 1/32 of a sample, far below the stopband — so
/// imaging drops to at-or-below §10's ~-55 dB measurement floor, indistinguishable from the clean
/// integer control, for the SAME per-output cost as before (two convolutions). It also removed the
/// old interpolator's own passband droop (22.05 kHz's 6.5 kHz tone: -2.6 dB out before, -0.0
/// after).
/// Phase 0 is bit-identical to the previous integer kernel, so every integer-ratio result is
/// unchanged. The UPSAMPLE branch keeps plain linear interpolation of raw samples: its imaging is
/// pre-existing (the legacy path used the same interpolator), measured at -10.2 dB for 8 kHz in
/// §10 and pinned by <c>UpsampleImaging_IsTheAcceptedResidual</c>, and a sub-16 kHz hands-free
/// endpoint is already band-limited far below the artefacts' significance — a recorded residual,
/// not a silent one.</para>
///
/// <para><b>Real-time cost is MEASURED in the SHIPPED build configuration</b>
/// (<c>tools/resampler-aliasing</c> §8, 2026-08-04, owner's Core Ultra 7 258V, built with
/// <c>-p:Platform=x64 -p:Optimize=false</c> to match the app's Release settings — it disables C#
/// optimization for the WinUI XAML compiler). Every qualifier was added because a reviewer was
/// right: an arithmetic MAC/s figure is not evidence a synchronous callback fits its period (Codex
/// r2), an OPTIMIZED benchmark is not evidence about a build that ships unoptimized (Codex r4 — the
/// same code measured 1.2% optimized and 6.6% as shipped, a 5.5x gap), and an integer-ratio-only
/// benchmark is not evidence about the non-integer family that costs double (Codex r6). See §8 for
/// the per-rate table; the worst case is the highest NON-integer rate, and the integer family
/// (48/96/192 — what WASAPI shared mode gives essentially always) rides the <c>frac == 0</c> fast
/// path at ONE convolution per output.</para>
///
/// <para><b>The figures have ~2x run-to-run variance on this hardware, so read the WORST observed,
/// not the best.</b> Four runs of 176.4 kHz measured 12.0 / 16.2 / 20.1 / 22.1% of the buffer period
/// with light background load — most plausibly the OS placing a single-threaded loop on a P-core in
/// some runs and an LP E-core in others (Lunar Lake has both; see also the TRN-9 thread-count card
/// for the same hardware property biting elsewhere). Planning figures are therefore the worst
/// observed: <b>~3% at 48 kHz, ~10% at 88.2, ~12% at 192, ~22% at 176.4</b>.</para>
///
/// <para><b>Recorded limits, and one claim retracted.</b> An earlier version of this doc said a ~5x
/// slower CPU would stay "under budget" — that was computed from a best-case sample and does NOT
/// survive the worst one (22% x 5 exceeds the period). The honest statement: 48 kHz has ~30x
/// headroom and is safe on any machine this app runs on; rates above ~96 kHz are materially
/// expensive and a substantially slower CPU could approach the period there, in which case the
/// symptom is dropped buffers, i.e. gaps in the recording. No ceiling is ENFORCED — a rate cap
/// would turn a working-but-slow configuration into a broken recording, strictly worse than slow
/// filtering (Codex r6's cap-or-measure choice) — but <c>AudioRecorderService</c> logs a Warning
/// above 96 kHz so that outcome self-attributes in a field report, and multistage decimation is the
/// named headroom if it ever does. Kernel-bank construction is separately measured in §8: it is
/// per-RECORDING on the start path (~0.5 ms at 48 kHz, ~2 ms at 176.4), not per buffer. Re-run §8
/// after touching <c>Process</c>, <see cref="ValueAt"/>, <see cref="TapsFor"/> or the bank
/// build.</para>
///
/// <para><b>Accepted passband trade-off.</b> The 7200 Hz cutoff means the top of what a 16 kHz WAV
/// can represent is attenuated: flat to 7 kHz (measured -0.28 dB), ~6 dB down at 7.2 kHz, and
/// substantially gone by 7.5 kHz. Some roll-off before 8 kHz is inherent to anti-aliasing at all;
/// what was missing was pinning it, so a future cutoff change cannot quietly eat more speech band
/// (Codex r4). <c>PassbandEnvelope_IsPinnedAndAccepted</c> is that pin. The affected band carries
/// fricative and sibilant energy, so a regression there would show up as /s/-vs-/sh/ confusions —
/// which is exactly what the still-open owner WER measurement would catch.</para>
///
/// <para><b>Pure float math over primitives, and that is a CONSTRAINT, not a coincidence.</b> No
/// NAudio <c>WaveFormat</c>, no Serilog — and nullable annotations only because the file carries its
/// own <c>#nullable enable</c> (an earlier version of this sentence said "no nullable annotations"
/// while the fields are nullable; Codex r6). The dependency rule is the same one
/// <see cref="AudioResampler"/> carries, and it is what let a single-file
/// <c>&lt;Compile Include&gt;</c> link into <c>tools/resampler-aliasing</c> compile with no package
/// references at all. <c>AudioRecorderService</c> does the <c>WaveFormat</c> adaptation at the call
/// site.</para>
///
/// <para><b>That rule is no longer what MAKES the harness link work, and the difference is worth
/// stating (2026-08-05).</b> The harness's <c>convert</c> mode links
/// <see cref="CaptureFormatConverter"/> too, which needs NAudio and Serilog — so those packages are
/// now referenced there, and a <c>WaveFormat</c> in this file's signature would no longer break the
/// build. Keep the constraint anyway: it is what keeps this file measurable in isolation and
/// dependency-free for any FUTURE consumer, and the reason to hold it is design discipline rather
/// than a compile error that would now catch a violation for you.</para>
///
/// <para><b>Not thread-safe, by design.</b> One instance per recording attempt, touched only from
/// NAudio's single capture thread. Scratch buffers are fields reused across calls, so a steady
/// stream of same-sized buffers allocates nothing after the first — the capture thread must not be
/// handed avoidable GC pressure. See the lifetime and straggler notes in
/// <c>AudioRecorderService</c>.</para>
/// </summary>
internal sealed class CaptureDownsampler
{
    /// <summary>
    /// Tap count at <see cref="ReferenceRate"/>. Matches the harness reference exactly at 48 kHz.
    /// </summary>
    internal const int ReferenceTaps = 401;

    /// <summary>The rate <see cref="ReferenceTaps"/> is defined at.</summary>
    internal const int ReferenceRate = 48000;

    /// <summary>Cutoff as a fraction of the TARGET rate — 0.45 x 16000 = 7200 Hz.</summary>
    internal const double CutoffFractionOfTarget = 0.45;

    /// <summary>
    /// Fractional-phase kernels per unit sample (the bank stores this + 1, so an output between
    /// phases p and p+1 never wraps). 32 is not tuned finely: blending adjacent kernels makes the
    /// phase-grid error the kernel's curvature across 1/32 sample, already far below the stopband,
    /// and doubling it would only shrink a number §10 measures as inaudible while doubling the
    /// bank's build time and memory.
    /// </summary>
    internal const int PhaseBankSize = 32;

    /// <summary>
    /// Tap count for a given source rate, scaled so the transition band is a constant width in
    /// HERTZ rather than a constant fraction of the source rate. Zero when no filter runs.
    ///
    /// <para><b>This scaling is a correctness requirement, not a quality knob</b> (Codex diff review
    /// r3, blocking). A Blackman window's transition width is ~5.5/N of the SOURCE rate, so a fixed
    /// 401 taps gives ~660 Hz at 48 kHz but ~2630 Hz at 192 kHz — which pushes the stopband edge
    /// from 7.5 kHz out past 8.5 kHz. Content between 8 kHz and that edge is then only partly
    /// attenuated, and since 192/16 is an integer ratio Stage B adds nothing, so it aliases straight
    /// back into 7.6-8 kHz. Measured on the committed coefficients before this fix: -86.9 dB at
    /// 8.05 kHz for 48 kHz input, but only -32.6 dB at 192 kHz. The filter silently stopped being an
    /// anti-alias filter on high-rate devices — the exact defect this whole card exists to remove.</para>
    ///
    /// <para>Kept ODD so the filter stays symmetric Type-I with an integer group delay. A pleasant
    /// consequence: because both the tap count and the sample rate scale together, the group delay
    /// becomes constant in TIME (~4.18 ms) at every rate, which is what makes the delay figure on
    /// <c>PrepareScratch</c> rate-independent rather than a 48 kHz-only claim.</para>
    ///
    /// <para>Deliberately NOT capped. A cap would bound the cost by silently restoring the defect
    /// above some rate; the cost is measured instead (harness §8) and a multistage/polyphase design
    /// is the named headroom if a high-rate device ever makes it matter.</para>
    /// </summary>
    internal static int TapsFor(int sourceRate, int targetRate)
    {
        if (sourceRate <= targetRate) return 0;
        var scaled = (int)global::System.Math.Round((double)ReferenceTaps * sourceRate / ReferenceRate);
        // Floor: defence for a hypothetical non-16 kHz target only. Unreachable in production —
        // any sourceRate > 16000 scales to >= 134 taps (Kimi r5: an earlier "at any legal rate"
        // comment oversold this as a live code path).
        if (scaled < 31) scaled = 31;
        if (scaled % 2 == 0) scaled++;         // odd => symmetric, integer group delay
        return scaled;
    }

    /// <summary>
    /// Measured proportionality between the work metric below and the share of a 10 ms buffer period
    /// <c>Process</c> consumes, in percent per work unit. Fitted to harness §8's six rates
    /// (44.1/48/88.2/96/176.4/192 kHz) in the SHIPPED build configuration: the per-rate ratios span
    /// 0.00645-0.00749 — a 1.16x spread across a 4.4x rate range — so <c>taps x
    /// convolutions-per-output</c> predicts cost well enough to threshold on. Re-fit if §8 moves.
    /// </summary>
    private const double MeasuredPercentPerWorkUnit = 0.0069;

    /// <summary>
    /// The work metric: taps times convolutions per output. Integer ratios take the
    /// <c>frac == 0</c> fast path at ONE convolution; fractional ratios blend two phase kernels and
    /// pay TWO. Zero when no filter runs.
    /// </summary>
    internal int RelativeWorkPerOutput =>
        _kernels == null ? 0 : _kernels[0].Length * (IsIntegerRatio ? 1 : 2);

    /// <summary>
    /// Estimated share of a 10 ms buffer period <c>Process</c> will consume, in percent — the basis
    /// for the recorder's high-cost Warning.
    ///
    /// <para>Exists because a RATE threshold is the wrong shape: 88.2 kHz measures 9.5% while the
    /// HIGHER 96 kHz measures 5.2%, since the fractional ratio pays two convolutions per output. A
    /// "warn above 96 kHz" rule therefore stayed silent on the more expensive configuration while
    /// firing on the cheaper one, and the docs claimed cost-based coverage it did not have
    /// (Codex r10). An estimate, deliberately: the alternative is timing the filter at recording
    /// start, which spends the very budget it is trying to protect.</para>
    /// </summary>
    internal double EstimatedBufferCostPercent => RelativeWorkPerOutput * MeasuredPercentPerWorkUnit;

    /// <summary>True when the decimation ratio is a whole number, i.e. every read position is
    /// integral and Stage B never interpolates.</summary>
    internal bool IsIntegerRatio => _ratio == global::System.Math.Floor(_ratio);

    /// <summary>
    /// Group delay the filter introduces, in SOURCE samples — the symmetric window's dominant tap
    /// offset. 200 samples is 4.17 ms at 48 kHz. Zero when no filter runs (equal rates, upsampling).
    /// Exposed so the delay is a pinned, named quantity rather than an emergent accident; see
    /// <c>PrepareScratch</c> for why it is accepted rather than compensated or flushed.
    /// </summary>
    internal int DelayedBySourceSamples => _kernels == null ? 0 : _kernels[0].Length / 2;

    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly double _ratio;

    // The phase bank: [PhaseBankSize + 1][taps]. Kernel p is the windowed-sinc design evaluated at
    // fractional tap offset p / PhaseBankSize; kernel 0 is BIT-IDENTICAL to the pre-bank integer
    // design, which is what keeps every integer-ratio output byte-for-byte unchanged. Null on the
    // == and < branches: Stage A is structurally absent there, not disabled.
    private readonly float[][]? _kernels;

    // [ history | current buffer ] laid out contiguously, so a kernel evaluation reads a single
    // array with no per-tap bounds branch.
    //
    // Sizing: `taps` for the filtered branch, one MORE than the classic taps-1. That extra sample is
    // DEFENSIVE, not required — the filtered branch breaks at `idx >= inputLength`, so its rebased
    // entry phase is always >= 0 and idx == -1 is unreachable there (Codex r9 corrected an earlier
    // comment that justified the extra sample by a reachable idx == -1). It is kept because the
    // margin costs one float and the alternative is a bounds argument that must be re-derived every
    // time the break conditions change.
    //
    // The UPSAMPLE branch genuinely needs its 1: it breaks at `idx + 1 >= inputLength`, so the
    // rebased phase CAN land in (-1, 0) and its left interpolation neighbour is the previous
    // buffer's last sample.
    //
    // The leading window starts zeroed, which is what produces the ~4 ms startup transient noted on
    // the card (and is the ordinary behaviour for a fresh FIR).
    private float[] _scratch;
    private readonly int _historyLength;

    // Stage B read position, in source samples, relative to the start of the CURRENT buffer. This
    // is what removes the per-buffer remainder drop (card item 4): no output position is skipped,
    // because the position is never reset.
    private double _phase;

    internal CaptureDownsampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0) throw new global::System.ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0) throw new global::System.ArgumentOutOfRangeException(nameof(targetRate));

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _ratio = (double)sourceRate / targetRate;

        if (sourceRate > targetRate)
        {
            var taps = TapsFor(sourceRate, targetRate);
            var kernels = new float[PhaseBankSize + 1][];
            for (var p = 0; p <= PhaseBankSize; p++)
                kernels[p] = DesignLowPass(taps, CutoffFractionOfTarget * targetRate, sourceRate,
                    (double)p / PhaseBankSize);
            _kernels = kernels;
            _historyLength = taps;
        }
        else if (sourceRate < targetRate)
        {
            _kernels = null;
            _historyLength = 1;
        }
        else
        {
            _kernels = null;
            _historyLength = 0;
        }

        _scratch = global::System.Array.Empty<float>();
    }

    /// <summary>True when the rates are equal, i.e. samples pass through untouched. Exposed so the
    /// recorder's fast path stays visible at the call site rather than being an invisible property
    /// of a Process() that happens to be an identity.</summary>
    internal bool IsPassthrough => _sourceRate == _targetRate;

    /// <summary>True when Stage A is present. False for passthrough and for upsampling.</summary>
    internal bool FiltersInput => _kernels != null;

    /// <summary>
    /// The minimum output buffer a caller must pass for an input of <paramref name="inputLength"/>
    /// source samples — and, per the phase invariant, a strict upper bound on what <see cref="Process"/>
    /// will write: entry phase is at least -1, so writes ≤ ceil(inputLength/ratio) + 1, which this
    /// figure covers. An oversized <c>ArrayPool</c> rental therefore changes nothing; the array's
    /// extra length is simply never reached. (This doc has been wrong twice — "samples are lost",
    /// then "Process writes past this figure into a larger array" — both corrected by review;
    /// Kimi r4/r5.) The actual count varies per buffer because the read phase is carried — that
    /// variation IS the fix for the remainder drop — so callers size a buffer with this and then
    /// use <see cref="Process"/>'s return value.
    ///
    /// <para><b>Never pass a smaller buffer — and note the failure is LOUD, not silent.</b> The loop
    /// exits on <c>written == output.Length</c> without consuming the pending position, the rebase
    /// then pushes the carried phase below -1, and the NEXT call's <see cref="ValueAt"/> reads
    /// <c>_scratch</c> at a negative index and throws. <c>OnDataAvailable</c>'s catch-all turns that
    /// into a dropped buffer plus an error line per callback. So misuse is noisy corruption of the
    /// carried phase, not a quiet truncation.</para>
    ///
    /// <para>This paragraph has now been wrong three times, each caught by review, which is itself
    /// the argument for keeping the contract one sentence long: "the samples are lost" (no — the
    /// phase is left unadvanced), "they are deferred to the next call" (only for a deferral of at
    /// most one source sample), and "the caller silently under-accounts" (no — the next call throws;
    /// Kimi r9). The contract is simply: size from this method.</para>
    /// </summary>
    internal int MaxOutputFor(int inputLength) =>
        IsPassthrough ? inputLength : (int)(inputLength / _ratio) + 2;

    /// <summary>
    /// Filters (when the rate is being reduced) and resamples one capture buffer, returning the
    /// number of samples written to <paramref name="output"/>.
    /// </summary>
    internal int Process(float[] input, int inputLength, float[] output)
    {
        if (input == null) throw new global::System.ArgumentNullException(nameof(input));
        if (output == null) throw new global::System.ArgumentNullException(nameof(output));
        if (inputLength <= 0) return 0;
        if (inputLength > input.Length) throw new global::System.ArgumentOutOfRangeException(nameof(inputLength));

        // A zero-length output is the one contract violation that would corrupt QUIETLY: the write
        // loop is skipped but the rebase below would still run, pushing the carried phase below -1 so
        // that every LATER call throws — damage with no signal at the call that caused it. Returning
        // here keeps the documented posture (misuse is loud, at the point of misuse) intact. Not
        // reachable from production, whose sole caller sizes from MaxOutputFor — whose minimum for any
        // inputLength >= 1 is 2 (Kimi r11).
        if (output.Length == 0) return 0;

        if (IsPassthrough)
        {
            var copy = global::System.Math.Min(inputLength, output.Length);
            global::System.Array.Copy(input, 0, output, 0, copy);
            return copy;
        }

        // Stage A is evaluated ON DEMAND, at each output's read position, never across the whole
        // buffer. Filtering every input sample and keeping one in `ratio` of them measured 69.3% of
        // a 10 ms period at 192 kHz in the SHIPPED build configuration (Codex r4) — an overrun
        // risk, not a documentation gap.
        var kernels = _kernels;
        var hist = _historyLength;
        PrepareScratch(input, inputLength, hist);
        var scratch = _scratch;

        var written = 0;
        while (written < output.Length)
        {
            var idx = (int)global::System.Math.Floor(_phase);
            var frac = _phase - idx;

            // Phase invariant: entry phase >= -1. It is >= 0 on the FILTERED branch, which breaks at
            // `idx >= inputLength` and therefore rebases from a position >= inputLength; only the
            // UPSAMPLE branch, breaking at `idx + 1 >= inputLength`, can rebase into (-1, 0), and
            // its history window of 1 is sized for exactly that (Codex r9 corrected an earlier
            // comment that attributed idx == -1 to both branches).
            float value;
            if (kernels != null)
            {
                if (idx >= inputLength) break;

                if (frac == 0d)
                {
                    // Integer read position — the PERMANENT state of every integer ratio
                    // (48/96/192 -> 16: the phase starts at 0 and advances by a whole number,
                    // exactly, in double), and therefore the path virtually every real capture
                    // takes. One convolution with the phase-0 kernel; the general case below costs
                    // two (Codex r5). Exact `== 0` is deliberate: a nonzero frac cannot round to
                    // zero here, and even the hypothetical miss would only take the general path
                    // and compute the same value more expensively.
                    value = ValueAt(scratch, kernels[0], hist, idx);
                }
                else
                {
                    // Fractional position: blend the two phase kernels bracketing frac. This is the
                    // imaging fix (Codex r7) — see the class doc. Same cost as the two-point
                    // interpolation it replaced: two convolutions.
                    var scaledPhase = frac * PhaseBankSize;
                    // Clamp so `p + 1` can never leave the bank. Today it provably cannot anyway:
                    // PhaseBankSize is a power of two, so `frac * PhaseBankSize` is EXACT scaling
                    // (no rounding), and frac < 1 therefore gives a product < PhaseBankSize.
                    //
                    // The clamp is here because that argument rests on an unstated property of the
                    // constant: change PhaseBankSize to a non-power-of-two and the product needs
                    // rounding, at which case a frac just below 1 could round UP to PhaseBankSize and
                    // index kernels[PhaseBankSize + 1]. Worth one Math.Min because the failure mode
                    // is not a dropped buffer — the throw precedes the phase advance and the rebase,
                    // so every later buffer re-throws at its first output and capture stalls for the
                    // rest of the recording.
                    //
                    // At the limit the clamped result is exactly right: a == 1 selects kernel
                    // PhaseBankSize, which is the correct kernel as frac -> 1.
                    //
                    // Kimi r10 raised this, with arithmetic that does NOT hold (it claimed
                    // 1-2^-53 scaled to exactly 32.0 by round-to-even; the product is 32-2^-48,
                    // representable, and truncates to 31 — verified). The finding is kept because
                    // the power-of-two dependence it stumbled onto is real and was unpinned; see
                    // PhaseBank_ScalingCannotOverflowTheBank.
                    var p = global::System.Math.Min((int)scaledPhase, PhaseBankSize - 1);
                    var a = (float)(scaledPhase - p);
                    var lo = ValueAt(scratch, kernels[p], hist, idx);
                    var hi = ValueAt(scratch, kernels[p + 1], hist, idx);
                    value = lo * (1f - a) + hi * a;
                }
            }
            else if (frac == 0d)
            {
                // Upsample, exact position.
                if (idx >= inputLength) break;
                value = scratch[hist + idx];
            }
            else
            {
                // Upsample, fractional: plain linear interpolation of RAW neighbours — the
                // pre-existing quality of the legacy path, kept deliberately (class doc; §10
                // measures its imaging). The right neighbour arrives with the next buffer.
                if (idx + 1 >= inputLength) break;
                value = (float)(scratch[hist + idx] * (1d - frac) + scratch[hist + idx + 1] * frac);
            }

            output[written++] = value;
            _phase += _ratio;
        }

        // Rebase onto the NEXT buffer's coordinates and slide the trailing history window, so the
        // next call's kernel windows (and the upsampler's left neighbour) reach back across the
        // buffer boundary.
        //
        // Known characteristic, measured rather than assumed: at a FRACTIONAL ratio the chunked total
        // can exceed the whole-signal total by ONE sample. The whole-signal path accumulates
        // `phase += ratio` from zero; here the same additions interleave with this subtraction, so at
        // a ratio whose ideal final position lands exactly on a buffer boundary the accumulated
        // rounding can put it a hair below `inputLength` and emit one more output. Replaying the
        // arithmetic at 0.25/1/4/16/60 s for 88.2 and 176.4 kHz, the delta stays 0 or +1 — it does
        // NOT accumulate, because this rebase keeps the phase small so its ULP stays tiny. One sample
        // is 62 us, and chunked is never SHORTER, which is the property the remainder-drop fix needs
        // (Kimi r10 asked for the fractional short-buffer case to be pinned; the new rows failed on a
        // one-sided tolerance and this is what they found).
        _phase -= inputLength;
        SlideHistory(hist, inputLength);

        return written;
    }

    /// <summary>Clears the carried state (delay line and read phase). Not used in production —
    /// each recording attempt builds a fresh instance — but it lets a test show that carried state
    /// is precisely what makes chunked and whole-signal results agree.</summary>
    internal void Reset()
    {
        if (_scratch.Length > 0) global::System.Array.Clear(_scratch, 0, _scratch.Length);
        _phase = 0;
    }

    /// <summary>
    /// Lays the buffer out behind the retained history window so <see cref="ValueAt"/>'s tap windows
    /// are continuous across callbacks. (The convolution itself lives in <see cref="ValueAt"/>; this
    /// doc stays here because the scratch window is what MAKES the filter causal-with-history, and
    /// the delay rationale below belongs to that design, not to any one evaluation site.)
    ///
    /// <para><b>This filter is CAUSAL and therefore DELAYS the signal.</b> An output at position
    /// <c>i</c> is a weighted sum of <c>input[i]</c> back to <c>input[i-(taps-1)]</c>, and the
    /// symmetric window's dominant tap sits at offset taps/2 — so the output tracks the input from
    /// ~200 source samples earlier at 48 kHz, i.e. <b>4.17 ms</b> (rate-independent in TIME because
    /// taps scale with the rate). Reading history avoids zero-padding; it does NOT absorb the group
    /// delay. An earlier version of this comment claimed it did, which was simply false (Codex diff
    /// review).</para>
    ///
    /// <para>Two consequences, both accepted rather than fixed, because the recording is a
    /// standalone WAV that is never time-aligned against anything:</para>
    /// <list type="bullet">
    ///   <item>Every recording is uniformly ~4.17 ms late. Nothing downstream cares: AUD-2's level
    ///   measurement, the VAD gate and every transcriber read the whole file.</item>
    ///   <item>The final ~4 ms of filter tail are never emitted, because nothing flushes the delay
    ///   line at stop. Deliberately NOT fixed: a flush means writing to the WAV after capture has
    ///   stopped, which entangles the stop/TCS/Cleanup ordering that AUD-1 r2 spent a review round
    ///   getting right — a materially worse risk than 4 ms of trailing audio in a recording that
    ///   ends in silence, and far shorter than the ~50-100 ms of a single phoneme.
    ///   <c>DelayedBySourceSamples</c> pins the figure so it stays a known quantity rather than an
    ///   accident.</item>
    /// </list>
    /// </summary>
    private void PrepareScratch(float[] input, int inputLength, int hist)
    {
        var needed = hist + inputLength;
        if (_scratch.Length < needed)
        {
            var grown = new float[needed];
            // Preserve the existing history window when growing.
            global::System.Array.Copy(_scratch, 0, grown, 0, global::System.Math.Min(hist, _scratch.Length));
            _scratch = grown;
        }
        global::System.Array.Copy(input, 0, _scratch, hist, inputLength);
    }

    /// <summary>Slides the trailing <c>hist</c> samples of the just-consumed stream into the
    /// history window. Overlapping self-copy is deliberate and memmove-safe, including when the
    /// buffer is shorter than the window (pinned by the short-buffer chunked tests).</summary>
    private void SlideHistory(int hist, int inputLength) =>
        global::System.Array.Copy(_scratch, inputLength, _scratch, 0, hist);

    /// <summary>
    /// One kernel evaluation at input index <paramref name="i"/>. The TAP WINDOW reaches back into
    /// retained history, but <paramref name="i"/> itself is never negative: this is called only from
    /// the filtered branch, whose entry phase is always >= 0 (it breaks at
    /// <c>idx &gt;= inputLength</c>, so it rebases from a position at or past the buffer end). An
    /// earlier version of this doc said <c>i</c> "may be -1", carrying over from the pre-kernel-bank
    /// design where the upsample and filtered branches shared this path (Kimi r11). The kernel
    /// decides the phase; the caller blends two adjacent phases for a fractional position.
    /// </summary>
    private static float ValueAt(float[] scratch, float[] kernel, int hist, int i)
    {
        var baseIdx = hist + i;
        var n = kernel.Length;
        double acc = 0;
        for (var k = 0; k < n; k++) acc += scratch[baseIdx - k] * kernel[k];
        return (float)acc;
    }

    /// <summary>
    /// Blackman-windowed sinc low-pass, normalised to unity gain at DC, evaluated at a fractional
    /// tap offset — <paramref name="phaseOffset"/> 0 reproduces the classic integer design
    /// bit-for-bit (same expression shapes on purpose), which is what keeps integer-ratio output
    /// byte-identical to the pre-bank implementation.
    ///
    /// <para>Deliberately a SECOND copy of the design the harness's <c>DesignLowPass</c> carries. Do
    /// NOT "dedupe" them — but be precise about what the duplication buys, because an earlier comment
    /// oversold it (Kimi diff review r3): the two tap designs are near-identical transliterations, so
    /// a systematic error in the DESIGN would survive in both. What is genuinely independent is the
    /// APPLICATION machinery — the harness convolves the whole signal, centered; production runs a
    /// causal stateful filter with phase-carrying decimation. That is what the prod-vs-reference
    /// comparison actually validates. The taps themselves are corroborated externally instead, by the
    /// absolute rejection figures matching Blackman-sinc theory.</para>
    ///
    /// <para>Each phase kernel is normalised to unity DC INDEPENDENTLY — without that, the tiny
    /// sum differences between phases would amplitude-modulate the output at the fractional-phase
    /// rate. The window is clamped to zero beyond its domain (only the final tap of a shifted
    /// kernel can land there, where Blackman is already ~0).</para>
    /// </summary>
    private static float[] DesignLowPass(int taps, double cutoffHz, int rate, double phaseOffset = 0d)
    {
        var h = new float[taps];
        var mid = taps / 2;
        var fc = cutoffHz / rate;
        double sum = 0;
        for (var i = 0; i < taps; i++)
        {
            var u = i + phaseOffset;
            var n = u - mid;
            var sinc = n == 0d
                ? 2 * fc
                : global::System.Math.Sin(2 * global::System.Math.PI * fc * n) / (global::System.Math.PI * n);
            var w = u > taps - 1
                ? 0d
                : 0.42
                  - 0.5 * global::System.Math.Cos(2 * global::System.Math.PI * u / (taps - 1))
                  + 0.08 * global::System.Math.Cos(4 * global::System.Math.PI * u / (taps - 1));
            h[i] = (float)(sinc * w);
            sum += h[i];
        }
        for (var i = 0; i < taps; i++) h[i] /= (float)sum;
        return h;
    }
}
