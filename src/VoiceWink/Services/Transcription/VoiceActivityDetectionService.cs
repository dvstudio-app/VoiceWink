using Serilog;
using VoiceWink.Helpers;
using Whisper.net;

namespace VoiceWink.Services.Transcription;

/// <summary>What the AUD-28 sherpa second opinion saw (see the union block in
/// <see cref="VoiceActivityDetectionService.EvaluateAsync"/>): the segmenter's outcome, its raw
/// segments in SAMPLES, and the parsed rate they are expressed in — the union decision converts
/// them to time at exactly that rate, never an assumed 16 kHz.</summary>
internal readonly record struct SherpaSecondOpinion(
    VadSegmentationOutcome Outcome,
    IReadOnlyList<(int Start, int Count)> Segments,
    int SampleRate);

/// <summary>
/// Silero-VAD no-speech gate over recorded audio (2026-07-23 incident: the whole-file
/// RMS gate passes "3 s of silence + one click" and Whisper hallucinates caption text).
/// Wraps Whisper.net's VAD API around the BUNDLED model
/// (Assets/Models/ggml-silero-v6.2.0.bin — SHA-pinned, ships with the app; deliberately
/// NOT under %LOCALAPPDATA%/Models, where ModelDownloadManager would enumerate it as a
/// selectable Whisper model). Fail-open by design: any unavailability yields
/// <see cref="NoSpeechVerdict.Unavailable"/> and the caller falls back to the legacy
/// RMS gate, so a VAD problem can never block dictation.
///
/// <para><b>AUD-28 (2026-08-28): a BLOCK gets a second opinion from the other Silero runtime.</b>
/// The two runtimes have measured complementary blind spots on the owner's corpus: sherpa-onnx
/// (<see cref="SherpaVadSegmenter"/>, at its shipped 0.50 threshold, over the SAME conditioned
/// bytes) sees the deep-in-noise far-field speech the ggml build scores at zero, and the ggml
/// build sees a take sherpa is blind to. So the gate is their UNION: only when BOTH runtimes
/// score below <see cref="Helpers.VadTuning.MinTotalSpeech"/> does the recording block. The
/// second opinion runs on the block path only — a ggml pass never pays for it — and can only
/// RESCUE, never block more, so the REL-15 must-block posture is preserved as far as the
/// measured proxies attest (all of train-noise/silence/white-noise stays at zero segments
/// through sherpa at every threshold ≥ 0.15). Evidence:
/// docs/plans/2026-08-28-aud28-sherpa-vad-compare/50-decision.md.</para>
/// </summary>
public sealed class VoiceActivityDetectionService : IDisposable, IAsyncDisposable
{
    private static ILogger Logger => Log.ForContext<VoiceActivityDetectionService>();

    /// <summary>Vendor-time integrity pin (upstream Hugging Face LFS pointer). Verified
    /// BEFORE the native loader ever touches the file; also pinned in CI by
    /// <c>BundledVadModelTests</c> and at release by the payload/ZIP gates.</summary>
    internal const string ModelSha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
    internal const long ModelSizeBytes = 885_098;
    internal const string ModelFileName = "ggml-silero-v6.2.0.bin";

    /// <summary>One lock across lazy init, detection, and disposal — bounded disposal
    /// must never overlap an in-flight native call (the WhisperTranscriptionService
    /// discipline).</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _modelPath;
    private WhisperVadFactory? _factory;
    private WhisperVadProcessor? _processor;
    private bool _unavailable; // session latch: model missing/corrupt or native init failed

    // Latched by BOTH dispose paths (before they return, even on lock timeout) so a
    // queued or later evaluation can never re-run TryInitialize and resurrect native
    // state after DI teardown began (Codex diff review, Medium). volatile: the timeout
    // path sets it without holding the lock.
    private volatile bool _disposed;

    // Upper bound on how long disposal waits for an in-flight evaluation to release the
    // lock. Dispose runs from DI-container teardown at app exit; on timeout we skip
    // native disposal (touching it mid-inference can crash) and let process exit
    // reclaim the memory. internal so tests can shorten it.
    internal TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(5);

    // Test seam replacing the native DetectSpeechAsync call (deterministic
    // cancel-in-flight / dispose-in-flight tests — the SettingsService._readFile
    // pattern). When set, native init is skipped entirely. Null in production.
    internal Func<Stream, CancellationToken, Task<IReadOnlyList<VadSegmentData>>>? DetectSpeechOverride { get; set; }

    // AUD-28 test seam at the WHOLE-second-opinion grain (bytes in → outcome + segments out),
    // deliberately not just around Segment: the fail-toward-current invariant covers the
    // WavPcm parse and the samples→time conversion too, and only a seam spanning them can
    // pin "a throw leaves the ggml NoSpeech verdict standing" (Kimi plan round). Null in
    // production; the DetectSpeechOverride pattern.
    internal Func<byte[], CancellationToken, SherpaSecondOpinion>? SecondOpinionOverride { get; set; }

    // Host-compatibility probe, checked BEFORE any native code is touched (Codex diff
    // review, High): the default Whisper.net.Runtime is compiled with AVX-class
    // optimizations (a separate Runtime.NoAvx package exists for older CPUs), and an
    // illegal instruction is a process kill no managed catch contains. The gate runs
    // for EVERY recording — including cloud-only configurations that never load
    // whisper native code otherwise — so an unsupported host must fall back to the RMS
    // gate without loading the runtime. Checks the runtime's full documented
    // instruction set: AVX/AVX2/FMA via intrinsics flags, F16C via CPUID (leaf 1,
    // ECX bit 29 — .NET exposes no intrinsic flag for it). ARM64 x64-emulation reports
    // these false where unsupported, which correctly routes to the fallback.
    // Deliberately NO OS-version gate (recorded decision, diff round 3): the identical
    // whisper.dll already loads on Windows 10 for every local-model user with no OS
    // probe, and OS-level incompatibilities surface as CATCHABLE load exceptions that
    // fail open to the RMS fallback — only unsupported instructions kill the process.
    // Local-model transcription has no probe at all today; that pre-existing exposure
    // for users who explicitly select a local model is unchanged — this probe only
    // stops the gate from WIDENING it to everyone. The reviewer's counter-proposal — an OS
    // gate that routes Windows 10 hosts to the fallback — is moot since 2026-08-30: the
    // supported baseline is Windows 11 ONLY (owner decision). Injectable for tests.
    internal Func<bool> HostSupportsNativeVad { get; set; } = static () =>
    {
        if (!global::System.Runtime.Intrinsics.X86.X86Base.IsSupported) return false;
        if (!global::System.Runtime.Intrinsics.X86.Avx.IsSupported ||
            !global::System.Runtime.Intrinsics.X86.Avx2.IsSupported ||
            !global::System.Runtime.Intrinsics.X86.Fma.IsSupported)
        {
            return false;
        }
        var (_, _, ecx, _) = global::System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
        return (ecx & (1 << 29)) != 0; // F16C
    };

    // REL-15 response lever (Batch A6). Carried as a field rather than read from
    // VadFeature.IsEnabled inline: a const read would make the rest of EvaluateAsync
    // provably unreachable in one configuration (CS0162, and the repo gates on 0
    // warnings), and injecting it lets the disabled behaviour be tested in an ordinary
    // default build. Same shape as UpdateService(featureEnabled:).
    private readonly bool _featureEnabled;

    // AUD-28: the sherpa Silero model the second opinion runs against — computed once in the
    // ctor chain beside _modelPath (App.xaml.cs's registration needs no change). "" in tests
    // that pass no path: SherpaVadSegmenter's own File.Exists guard then reports ModelMissing
    // and the ggml verdict stands, so ggml-focused rows never load sherpa native code.
    private readonly string _sileroVadModelPath;

    public VoiceActivityDetectionService()
        : this(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", ModelFileName),
               VadFeature.IsEnabled,
               Path.Combine(AppContext.BaseDirectory, "Assets", "Models", SherpaVadSegmenter.ModelFileName))
    {
    }

    /// <summary>Test seam: point the service at an arbitrary (or missing) model path, and
    /// optionally compile-out-equivalent the gate. <paramref name="featureEnabled"/> defaults
    /// to true so existing callers keep meaning exactly what they meant before A6;
    /// <paramref name="sileroVadModelPath"/> defaults to missing so pre-AUD-28 callers get a
    /// second opinion that DECLINES rather than a native load — ModelMissing via the segmenter's
    /// File.Exists guard, or one of its earlier guards' outcomes (InputTooShort /
    /// UnsupportedSampleRate) for audio those refuse first; every one leaves the verdict standing.</summary>
    internal VoiceActivityDetectionService(string modelPath, bool featureEnabled = true,
        string? sileroVadModelPath = null)
    {
        _modelPath = modelPath;
        _featureEnabled = featureEnabled;
        _sileroVadModelPath = sileroVadModelPath ?? "";
    }

    /// <summary>
    /// Evaluate a recorded WAV for speech. <see cref="OperationCanceledException"/>
    /// propagates (pipeline cancel semantics); any other failure returns
    /// <see cref="NoSpeechVerdict.Unavailable"/> so the caller falls back to the RMS gate.
    /// </summary>
    public async Task<NoSpeechVerdict> EvaluateAsync(string wavPath, CancellationToken ct = default)
    {
        if (!_featureEnabled)
        {
            // REL-15 response lever (Batch A6): return before the lock and before ANY
            // native path — no host probe, no model read, no factory. The caller's
            // Unavailable route runs the legacy RMS gate.
            //
            // The token is honoured because every other Unavailable return sits behind
            // `_lock.WaitAsync(ct)`, which throws on a cancelled token; skipping it would
            // let this lever change cancellation semantics as a side effect, and it should
            // change exactly one thing.
            ct.ThrowIfCancellationRequested();

            // POSITIVE marker, deliberately: the release smoke and the exposure tally both
            // need proof the disabled branch RAN. "No VAD gate: lines" is equally satisfied
            // by nobody having dictated. The string must NOT contain "VAD gate:" — the
            // tally counts those as gated recordings (scripts/rel15-tally.ps1).
            Logger.Information("VAD disabled by build - RMS fallback for this recording");
            return NoSpeechVerdict.Unavailable;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return NoSpeechVerdict.Unavailable;

            if (DetectSpeechOverride == null)
            {
                if (_unavailable) return NoSpeechVerdict.Unavailable;
                if (_processor == null && !await Task.Run(TryInitialize, ct).ConfigureAwait(false))
                    return NoSpeechVerdict.Unavailable;
            }

            // AUD-24: condition the gate's INPUT COPY — Silero measurably collapses on quiet raw
            // audio (five real recordings at zero segments across the whole threshold range), and
            // the same audio conditioned toward −26 dBFS scores 25–31 s of speech at the SHIPPED
            // threshold. The raw file is untouched; the verdict still attributes to it. Fail-soft
            // MOSTLY by shape: an unmeasurable file decides gain 0 and a non-canonical layout
            // returns the original bytes — the pre-AUD-24 behavior exactly. The residual class
            // (an absurd chunk length overflowing the analyzer's walk) fails by CATCH into
            // Unavailable → RMS fallback, which is still fail-open (self-review, probed).
            // OCE propagates from the reads unchanged.
            var wavBytes = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
            var inputGainDb = VadInputConditioning.DecideGainDb(WavLevelAnalyzer.Measure(wavBytes));
            var conditioned = VadInputConditioning.ConditionCopy(wavBytes, inputGainDb);

            IReadOnlyList<VadSegmentData> segments;
            long detectMs;
            var stream = new MemoryStream(conditioned, writable: false);
            await using (stream.ConfigureAwait(false))
            {
                // Detect call only — excludes lock wait and lazy init, includes the
                // wrapper's WAV parse + native evaluation (REL-15 field observability:
                // the single-threaded gate's cost must be visible in logs).
                var detectTimer = global::System.Diagnostics.Stopwatch.StartNew();
                segments = DetectSpeechOverride != null
                    ? await DetectSpeechOverride(stream, ct).ConfigureAwait(false)
                    : await _processor!.DetectSpeechAsync(stream, ct).ConfigureAwait(false);
                detectTimer.Stop();
                detectMs = detectTimer.ElapsedMilliseconds;
            }

            // The native VAD does not poll the token once running (Whisper.net 1.9.1) —
            // a stop during detection must not continue into transcription.
            ct.ThrowIfCancellationRequested();

            var isNoSpeech = NoSpeechGate.IsNoSpeech(segments.Select(s => (s.Start, s.End)));

            // AUD-28 second opinion (block path ONLY — a ggml pass never pays for it): the sherpa
            // Silero runtime re-scores the SAME conditioned bytes, and the verdict is the UNION —
            // either runtime seeing ≥ MinTotalSpeech passes the recording. Fail-toward-current is
            // structural, not input-contingent: EVERY failure inside this block — the WavPcm parse,
            // the segmenter, the samples→time conversion — leaves the ggml NoSpeech verdict
            // STANDING, because letting it reach the outer catch would convert a block into
            // Unavailable → the RMS fallback, which passes "3 s of silence + one click" — fail-open
            // on the exact class this gate exists to stop. Only OCE propagates (pipeline cancel
            // semantics unchanged; sherpa polls the token per 32 ms window). The secondOpinion
            // token doubles as the REL-15 crash-attribution marker: a native crash after a
            // "rescued" verdict implicates onnxruntime, which does not use the vcomp140/OpenMP
            // path REL-15 suspected.
            var secondOpinion = "none";
            if (isNoSpeech)
            {
                var opinionTimer = global::System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var opinion = SecondOpinionOverride != null
                        ? SecondOpinionOverride(conditioned, ct)
                        : RunSherpaSecondOpinion(conditioned, ct);
                    opinionTimer.Stop();
                    if (opinion.Outcome == VadSegmentationOutcome.Ok)
                    {
                        // NoSpeechGate is the ONE summer (overlap-unioning is its pinned job);
                        // segments convert at the opinion's own parsed rate.
                        var rescued = !NoSpeechGate.IsNoSpeech(opinion.Segments.Select(s => (
                            TimeSpan.FromSeconds(s.Start / (double)opinion.SampleRate),
                            TimeSpan.FromSeconds((s.Start + s.Count) / (double)opinion.SampleRate))));
                        if (rescued) isNoSpeech = false;
                        secondOpinion = $"{(rescued ? "rescued" : "declined")}({opinion.Segments.Count} segs, {opinionTimer.ElapsedMilliseconds} ms)";
                    }
                    else
                    {
                        secondOpinion = $"{opinion.Outcome}({opinionTimer.ElapsedMilliseconds} ms)";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    secondOpinion = "error";
                    Logger.Warning(ex, "Sherpa second opinion failed — ggml no-speech verdict stands");
                }
            }

            // Same "VAD gate:" prefix (scripts/rel15-tally.ps1 counts it); the gain token makes a
            // conditioned verdict distinguishable from a raw one in support bundles. Numbers only.
            // secondOpinion sits BEFORE the gain token: the UAT plan's prose pins that this line
            // "ends with `input gain N dB`", and the segment count stays the GGML count on a
            // rescued verdict — the token beside it carries the attribution.
            Logger.Information("VAD gate: {Outcome} ({SegmentCount} speech segments, {DurationMs} ms, secondOpinion {SecondOpinion}, input gain {GainDb} dB)",
                isNoSpeech ? "no speech" : "speech detected", segments.Count, detectMs, secondOpinion, (int)global::System.Math.Round(inputGainDb));
            return isNoSpeech ? NoSpeechVerdict.NoSpeech : NoSpeechVerdict.SpeechDetected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "VAD evaluation failed — RMS fallback for this recording");
            return NoSpeechVerdict.Unavailable;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// AUD-28: the production second opinion — parse the conditioned bytes, run
    /// <see cref="SherpaVadSegmenter"/> at its SHIPPED constants (threshold 0.50 — the measured
    /// configuration; the harness sweep is where other thresholds live). The parsed rate feeds
    /// Segment rather than a literal 16000 (its UnsupportedSampleRate guard exists because
    /// mis-sized frames score garbage timings, not an error), and <see cref="_featureEnabled"/>
    /// rides along per Segment's own parameter doc — unreachable difference today (the lever
    /// returns before the lock), honest by construction. Runs under <see cref="_lock"/> on the
    /// block path only; cost is bounded by the model parse (643 KB) + ~31 windows/audio-second
    /// at one thread — the same order as the ggml detect that just ran — and it delays only the
    /// no-speech pill on an already-terminal path. CPU safety is INHERITED, not probed twice:
    /// this runs only after a successful ggml init, whose <see cref="HostSupportsNativeVad"/>
    /// AVX/AVX2/FMA/F16C probe is strictly stronger than sherpa-onnx's SSE2 floor
    /// (<see cref="ParakeetNativeProbe"/>).
    /// </summary>
    private SherpaSecondOpinion RunSherpaSecondOpinion(byte[] conditionedWav, CancellationToken ct)
    {
        var (samples, sampleRate) = WavPcm.ReadMono16(conditionedWav);
        var result = SherpaVadSegmenter.Segment(samples, sampleRate, _sileroVadModelPath, _featureEnabled, ct);
        return new SherpaSecondOpinion(result.Outcome, result.Segments, sampleRate);
    }

    /// <summary>
    /// Caller holds <see cref="_lock"/>. Atomic: builds into locals, disposes partials
    /// on any failure (processor before factory — the factory must outlive the
    /// processor), and assigns the fields only after complete success. The bundled
    /// asset is never deleted (hash-verified, and not ours to quarantine).
    /// </summary>
    private bool TryInitialize()
    {
        WhisperVadFactory? factory = null;
        WhisperVadProcessor? processor = null;
        try
        {
            if (!HostSupportsNativeVad())
            {
                Logger.Information("Host CPU lacks the native runtime's required instruction set — RMS fallback for this session");
                _unavailable = true;
                return false;
            }

            if (!VerifyBundledModel())
            {
                _unavailable = true;
                return false;
            }

            factory = WhisperVadFactory.FromPath(_modelPath);
            processor = factory.CreateBuilder()
                .WithThreshold(VadTuning.Threshold)
                .WithMinSpeechDuration(VadTuning.MinSpeechDuration)
                .WithMinSilenceDuration(VadTuning.MinSilenceDuration)
                .WithSpeechPadding(VadTuning.SpeechPadding)
                // An 885 KB model gains nothing from GPU, and a future GPU runtime
                // package must not silently change the gate. The BUILDER owns the VAD
                // context's GPU setting (not WhisperFactoryOptions).
                .WithUseGpu(false)
                .WithThreads(VadTuning.DetectThreads)
                .Build();

            _factory = factory;
            _processor = processor;
            Logger.Information("Silero VAD initialized: {Path}", _modelPath);

            // TRN-27: this is the FIRST Whisper.net factory on the shipped default path (the gate
            // runs on every fresh recording, whatever engine is selected), so it is what freezes
            // the process-wide native backend — report which one loaded, once per process.
            // NOTE the builder's WithUseGpu(false) above governs this CONTEXT only; the loaded
            // LIBRARY is whatever the [Vulkan, Cpu] pin resolved, so on a GPU machine the gate
            // runs on the Vulkan package's ggml-cpu-whisper.dll — measured different bytes from
            // the CPU package's. Never claim "the gate stays CPU-package code"; recalibrating
            // means re-measuring with tools/vad-gate-tune under the shipped package set.
            WhisperBackendLog.TryLogOnce();
            return true;
        }
        catch (Exception ex)
        {
            processor?.Dispose();
            factory?.Dispose();
            Logger.Warning(ex, "Silero VAD initialization failed — RMS fallback for this session");
            _unavailable = true;
            return false;
        }
    }

    private bool VerifyBundledModel()
    {
        if (!File.Exists(_modelPath))
        {
            Logger.Warning("Bundled VAD model missing at {Path} — RMS fallback for this session", _modelPath);
            return false;
        }

        var length = new FileInfo(_modelPath).Length;
        if (length != ModelSizeBytes)
        {
            Logger.Warning("Bundled VAD model size {Actual} ≠ pinned {Expected} — RMS fallback for this session",
                length, ModelSizeBytes);
            return false;
        }

        using var fs = File.OpenRead(_modelPath);
        var hash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(fs));
        if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warning("Bundled VAD model SHA-256 mismatch — RMS fallback for this session");
            return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; // refuse new evaluations even if the lock wait below times out
        if (!await _lock.WaitAsync(DisposeLockTimeout).ConfigureAwait(false))
        {
            Logger.Warning("VoiceActivityDetectionService.DisposeAsync timed out after {Timeout}s waiting for an " +
                "in-flight evaluation; skipping native disposal (process exit reclaims it)", DisposeLockTimeout.TotalSeconds);
            return;
        }
        try
        {
            if (_processor != null)
            {
                await _processor.DisposeAsync().ConfigureAwait(false);
                _processor = null;
            }
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true; // refuse new evaluations even if the lock wait below times out
        if (!_lock.Wait(DisposeLockTimeout))
        {
            Logger.Warning("VoiceActivityDetectionService.Dispose timed out after {Timeout}s waiting for an " +
                "in-flight evaluation; skipping native disposal (process exit reclaims it)", DisposeLockTimeout.TotalSeconds);
            return;
        }
        try
        {
            _processor?.Dispose();
            _processor = null;
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
