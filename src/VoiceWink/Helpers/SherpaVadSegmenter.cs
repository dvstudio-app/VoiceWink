using SherpaOnnx;

namespace VoiceWink.Helpers;

/// <summary>Why a segmentation attempt produced no usable segments. Every value except
/// <see cref="Ok"/> means the caller falls back to <see cref="LongAudioChunker.Plan"/> — none of them
/// is an error the user should see, and none may throw. See <see cref="SherpaVadSegmenter"/>.</summary>
internal enum VadSegmentationOutcome
{
    Ok,

    /// <summary>Build-time VAD kill switch is pulled (<c>-p:VadEnabled=false</c>).</summary>
    Disabled,

    /// <summary>Too little audio to hand the native VAD at all — see the segmenter's input floor.</summary>
    InputTooShort,

    /// <summary>Audio at any rate other than <see cref="SherpaVadSegmenter.RequiredSampleRate"/>.
    /// The model scores mis-sized frames as garbage timings, not as an error — so the guard refuses
    /// rather than letting a future caller find out.</summary>
    UnsupportedSampleRate,

    ModelMissing,
    ModelCorrupt,

    /// <summary>Native init or the run itself failed. Deliberately not split further: the useful
    /// distinction to a reader of the logs is "the VAD did not run", and the exception text carries
    /// the rest.</summary>
    NativeFailure,
}

/// <summary>Raw VAD output plus why it is empty when it is. Segments are ASCENDING, in samples,
/// UNPADDED — <see cref="VadDecodePlan"/> takes the merge decision on the raw gap, so padding must
/// not be applied before it.</summary>
internal readonly record struct VadSegmentationResult(
    VadSegmentationOutcome Outcome,
    IReadOnlyList<(int Start, int Count)> Segments)
{
    internal static VadSegmentationResult Failed(VadSegmentationOutcome outcome)
        => new(outcome, Array.Empty<(int, int)>());
}

/// <summary>
/// TRN-22: drives sherpa-onnx's own Silero VAD over a recording and returns speech segments. The
/// native half of the fix; <see cref="VadDecodePlan"/> is the pure half that turns these into a
/// decode plan. Since AUD-28 it has an APP caller too:
/// <c>VoiceActivityDetectionService</c>'s block-path second opinion runs it over the gate's
/// conditioned bytes at the GATE's own threshold — <see cref="VadTuning.SecondOpinionThreshold"/>
/// (0.25 since AUD-36; 0.50 under AUD-28) through the threshold-parameterized entry below — while
/// <see cref="Threshold"/> stays the DECODE-plan candidate's (harness-only) constant.
///
/// <para><b>Split from the planner because the harness links FILES, not projects.</b>
/// <c>tools/parakeet-long-audio</c> cannot reference the service (it would drag in Serilog and the
/// sherpa wrapper), so it links individual helpers — which is exactly why
/// <see cref="ChunkedDecode"/> exists as a helper rather than a method on the service. Keeping the
/// native driver and the planner apart lets the harness link both and measure the REAL path
/// instead of re-implementing a segmenter while claiming otherwise.</para>
///
/// <para><b>Nothing here logs.</b> No Serilog dependency, for the same linkability reason; the
/// outcome enum is how the caller reports what happened.</para>
///
/// <para><b>Fail-soft is the whole posture, and it is not defensive nervousness.</b> This is a
/// 643 KB asset in front of the ONLY local engine most users have. A missing file, an antivirus
/// quarantine, a half-applied update, or a native init that throws must degrade to today's chunker,
/// never take Parakeet down. That mirrors
/// <c>VoiceActivityDetectionService.VerifyBundledModel</c> one file over, which answers the same
/// question the same way for the no-speech gate.</para>
///
/// <para><b>One class it does NOT cover, stated so the promise stays honest:</b> an illegal
/// instruction inside native code is not a catchable managed exception, so a CPU lacking the
/// runtime's required instruction set is not handled here. The original "unreachable — such a
/// machine never gets a working sherpa recognizer" premise died with AUD-28 (cloud-only users
/// now reach this on the gate's block path); what covers them instead is INHERITANCE — the second
/// opinion runs only after a successful ggml init, whose AVX/AVX2/FMA/F16C host probe is strictly
/// stronger than sherpa-onnx's documented SSE2 floor (<c>ParakeetNativeProbe</c>), so a machine
/// that reaches this code has already proven the stronger instruction set.</para>
///
/// <para><b>Input floor before any native contact.</b> An empty waveform into sherpa's RECOGNIZER is
/// a measured process kill (<c>0xC0000409</c>, no managed exception). The VAD's behaviour on empty or
/// sub-window input is simply unverified — which the repo's own vocabulary calls an unverified native
/// assumption, and the honest response is not to find out in production.</para>
/// </summary>
internal static class SherpaVadSegmenter
{
    internal const string ModelFileName = "silero_vad.onnx";

    /// <summary>Size and hash of the shipped asset, pinned the way the no-speech gate's model is.
    /// VERIFIED locally (2026-08-20) rather than read off a page: downloaded from
    /// <c>github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx</c>, which also
    /// answered a real <c>Range: bytes=0-99</c> request with HTTP 206.</summary>
    internal const long ModelSizeBytes = 643_854;

    /// <inheritdoc cref="ModelSizeBytes"/>
    internal const string ModelSha256 = "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6";

    /// <summary>Silero's frame size. Not ours to choose — the model is exported for it.</summary>
    internal const int WindowSize = 512;

    /// <summary>The rate the model is exported for — <see cref="WindowSize"/> is 32 ms only here.
    /// The app's capture pipeline is 16 kHz by construction and the harness reads 16 kHz WAVs, so a
    /// caller hitting this guard wrote a bug; the guard exists because the failure mode without it
    /// is silently wrong segment timings, not an exception.</summary>
    internal const int RequiredSampleRate = 16_000;

    /// <summary>Per-frame speech probability. sherpa's default, and the same value
    /// <see cref="VadTuning.Threshold"/> uses for the no-speech gate. Upstream's LONG-AUDIO examples
    /// drop this to 0.2; on this corpus that measured WORSE (the first repro fell 151 → 123 → 109
    /// chars at 0.5 → 0.2 → 0.1), because those examples are loud reference recordings and this audio
    /// sits at −33…−39 dBFS active RMS. Seeded, and owned by the corpus sweep.</summary>
    internal const float Threshold = 0.5f;

    /// <summary>Below this, a detection is not a segment. sherpa's default.</summary>
    internal const float MinSpeechSeconds = 0.25f;

    /// <summary>Silence shorter than this joins adjacent speech INSIDE the VAD. sherpa's default of
    /// 0.5 s, deliberately NOT <see cref="VadTuning.MinSilenceDuration"/>'s 100 ms: that value is
    /// tuned for deciding whether a recording contains speech at all, and at 100 ms the spike split
    /// one sentence into two decodes for no gain. Larger joins more.</summary>
    internal const float MinSilenceSeconds = 0.5f;

    /// <summary>
    /// The VAD's own hard cap, set HIGH on purpose so it effectively never fires on dictation.
    ///
    /// <para>Upstream defaults this to 5 s and that is the single number that explains the whole
    /// defect — but its cut is unconditional, landing mid-word wherever the clock runs out. Measured:
    /// the cap firing split a sentence across two decodes and lost it. <see cref="VadDecodePlan"/>
    /// caps length instead, cutting at the quietest point via
    /// <see cref="LongAudioChunker.FindQuietestPoint"/>, which is the same job done in silence rather
    /// than on a timer. Natural pauses do the real work regardless: the first repro produced an
    /// identical plan at 5, 10 and 20 s.</para>
    /// </summary>
    internal const float MaxSpeechSeconds = 20f;

    /// <summary>
    /// Speech segments for <paramref name="samples"/>, or an outcome saying why there are none.
    /// Never throws, never returns a zero-length segment.
    /// </summary>
    /// <param name="vadEnabled">Pass <c>VadFeature.IsEnabled</c> from the composition root — NOT an
    /// <c>#if</c> inside this method. The lever's whole point is being exercisable in an ordinary
    /// build, which is why <c>VadFeature</c> is a const passed as an argument.</param>
    internal static VadSegmentationResult Segment(
        float[] samples,
        int sampleRate,
        string modelPath,
        bool vadEnabled,
        CancellationToken ct)
        => Segment(samples, sampleRate, modelPath, vadEnabled, Threshold, ct);

    /// <summary>Threshold-parameterized entry (AUD-28): the sensitivity comparison in
    /// <c>tools/vad-gate-tune</c> sweeps this runtime against the shipped ggml gate, and the
    /// <see cref="Threshold"/> const cannot vary at runtime. Since AUD-36 it is ALSO the gate's
    /// production entry — <c>VoiceActivityDetectionService</c> passes
    /// <see cref="VadTuning.SecondOpinionThreshold"/> (source-contract-pinned by
    /// <c>VadTuningTests</c>); the decode-plan arms keep the const overload above.</summary>
    internal static VadSegmentationResult Segment(
        float[] samples,
        int sampleRate,
        string modelPath,
        bool vadEnabled,
        float threshold,
        CancellationToken ct)
    {
        if (!vadEnabled) return VadSegmentationResult.Failed(VadSegmentationOutcome.Disabled);
        if (sampleRate != RequiredSampleRate)
        {
            return VadSegmentationResult.Failed(VadSegmentationOutcome.UnsupportedSampleRate);
        }

        if (samples.Length < WindowSize)
        {
            return VadSegmentationResult.Failed(VadSegmentationOutcome.InputTooShort);
        }

        if (!File.Exists(modelPath)) return VadSegmentationResult.Failed(VadSegmentationOutcome.ModelMissing);

        try
        {
            if (new FileInfo(modelPath).Length != ModelSizeBytes) return VadSegmentationResult.Failed(VadSegmentationOutcome.ModelCorrupt);

            using (var stream = File.OpenRead(modelPath))
            {
                var hash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(stream));
                if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return VadSegmentationResult.Failed(VadSegmentationOutcome.ModelCorrupt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not corrupt, but the caller's response is identical, and reporting
            // "corrupt" for a sharing violation would send a reader hunting a tampered file.
            return VadSegmentationResult.Failed(VadSegmentationOutcome.NativeFailure);
        }

        try
        {
            var config = new VadModelConfig();
            config.SileroVad.Model = modelPath;
            config.SileroVad.Threshold = threshold;
            config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
            config.SileroVad.MinSilenceDuration = MinSilenceSeconds;
            config.SileroVad.MaxSpeechDuration = MaxSpeechSeconds;
            config.SileroVad.WindowSize = WindowSize;
            config.SampleRate = sampleRate;
            config.NumThreads = 1;

            // The detector's ring buffer must hold the longest segment it may emit; MaxSpeechSeconds
            // bounds that, plus a window's slack for the flush.
            var bufferSeconds = MaxSpeechSeconds + 1f;

            // Created and disposed per decode, inside the service's existing lock. Caching it beside
            // the recognizer would buy a model load (643 KB) at the cost of joining that lock's
            // bounded-Dispose discipline with a second native object — not worth it against a
            // recording measured in seconds.
            using var vad = new VoiceActivityDetector(config, bufferSeconds);

            var segments = new List<(int Start, int Count)>();
            for (var offset = 0; offset < samples.Length; offset += WindowSize)
            {
                ct.ThrowIfCancellationRequested();

                // The native VAD wants whole windows; a short final slice is dropped rather than
                // zero-padded, because zero-padding fabricates silence the model would score.
                //
                // At most 511 samples (32 ms). `Flush` does NOT recover it — Flush closes the
                // segment the VAD is mid-way through, it cannot see samples never handed to
                // AcceptWaveform, and an earlier version of this comment claimed otherwise (Kimi
                // diff round). What actually covers those 32 ms is the last planned segment's
                // 0.4 s tail padding, plus the caller's coverage tripwire if speech really did
                // extend past the plan.
                var take = Math.Min(WindowSize, samples.Length - offset);
                if (take < WindowSize) break;

                var window = new float[WindowSize];
                Array.Copy(samples, offset, window, 0, WindowSize);
                vad.AcceptWaveform(window);
                Drain(vad, segments, samples.Length);
            }

            vad.Flush();
            Drain(vad, segments, samples.Length);

            return new VadSegmentationResult(VadSegmentationOutcome.Ok, segments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Native init on a CPU without the required instruction set, a missing runtime DLL, a
            // model the loader rejects. The gate one file over answers the same class the same way,
            // and the alternative here is taking down the engine over a VAD.
            return VadSegmentationResult.Failed(VadSegmentationOutcome.NativeFailure);
        }
    }

    /// <summary>Pull every completed segment out of the detector. Clamped into the buffer and
    /// zero-length dropped HERE, so the planner never has to reason about either.</summary>
    private static void Drain(
        VoiceActivityDetector vad, List<(int Start, int Count)> segments, int totalSamples)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();

            var start = Math.Clamp(segment.Start, 0, totalSamples);
            var count = Math.Min(segment.Samples.Length, totalSamples - start);
            if (count > 0) segments.Add((start, count));
        }
    }
}
