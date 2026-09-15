using System.Diagnostics;
using Serilog;
using SherpaOnnx;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Local transcription with NVIDIA Parakeet TDT 0.6B v3 via sherpa-onnx (TRN-1 step 3) — the second
/// native inference runtime beside whisper.cpp.
///
/// <para><b>Measured, not assumed:</b> on this project's own dictation corpus this runs 9.0× real
/// time on CPU against the shipped default's 0.6×, and stayed silent on the digital-silence clip
/// where four Whisper models hallucinated text. Full method and limits in
/// <c>docs/model-review/2026-08-02-parakeet-vs-local-whisper-cpu.md</c>. WER is NOT measured.</para>
///
/// <para><b>Recognizer construction costs ~3.1 s</b>, so it is built once and cached. It is rebuilt
/// only when the model path changes — never per recording, which is why this sits behind
/// <c>PrepareAsync</c> rather than being constructed at transcribe time.</para>
///
/// <para><b>One lock across build / rebuild / decode / dispose.</b> File transcription and the
/// recording pipeline share this instance, and sherpa's <c>Decode</c> is synchronous native work
/// against a handle a rebuild would replace underneath it. <c>WhisperTranscriptionService</c> carries
/// the same shape for the same reason.</para>
///
/// <para><b>Long audio is CUT before decoding (TRN-10, 2026-08-05).</b> sherpa-onnx does no internal
/// windowing — whisper.cpp's 30 s windows are why the same file transcribes fine there — and past
/// roughly a minute this model silently drops speech: a measured 108 s recording returned text for
/// only its first ~61 s and reported success. <c>Helpers/LongAudioChunker</c> owns where the cuts go
/// and carries the evidence. A recording that fits yields a one-chunk plan and takes a byte-identical
/// path to the pre-TRN-10 single decode — byte-identical including the decode input only while no
/// TRN-10b gain applies (≥ −30 dBFS active-RMS; the next paragraph).</para>
///
/// <para><b>Quiet audio is GAIN-CONDITIONED before decoding (TRN-10b, 2026-08-05).</b> The same
/// engine also collapses to an EMPTY result on quiet input — all-or-nothing per decode call,
/// worsening with slice length: 3 of 4 chunks of a −37.4 dBFS active-RMS capture decoded to exactly
/// 0 chars (Whisper: 13.76% WER on the same file) and +3 dB flipped it to fully decoding.
/// <see cref="Helpers.DecodeInputGain"/> owns the whole-recording decision (boost below −30
/// active-RMS toward −26; calibration story and residuals live there) and the scale-then-clip
/// application runs per chunk inside <c>DecodeOne</c>; the stored WAV is never touched, so a retry
/// replays the identical decision. An empty chunk on non-silent audio logs a Warning — the tripwire
/// this defect's silence earned.</para>
///
/// <para><b>Hints are not sent, and since TRN-26 (2026-08-23) that is a MEASURED refusal, not a
/// pending follow-up.</b> The full wiring was built (encoder, reconcile-and-rebuild, the transport
/// flip) and then measured on the 20-file owner ground-truth corpus before shipping: sherpa's
/// hotword biasing requires <c>modified_beam_search</c>, and that decode alone — one inert term,
/// biasing uninvolved — scored 214 total errors against greedy's 152, deletions 96→168, including
/// a 10-word recording going 0→10 errors under both beam runs; with the owner's real 51-term
/// vocabulary it scored 196, while term recall stayed ~0. The damage is the decode method, not the biasing strength, so no
/// score retune answers it — it is the same sherpa decode-stack defect TRN-22 proved, one decode
/// mode deeper. The validated encoder (<see cref="Helpers.ParakeetHotwords"/>) and the harness's
/// <c>--hotwords</c> instrument stay committed so ONE command retests any future sherpa release;
/// the echo half PASSED for the record (0/51 terms on silence/noise/no-speech probes). Reopen
/// conditions live on the TRN-26 card. <see cref="HintTransport"/> stays
/// <see cref="HintTransportKind.None"/> — literal, as before.</para>
/// </summary>
public sealed class ParakeetTranscriptionService : ITranscriptionService, IDisposable
{
    private static ILogger Logger => Log.ForContext<ParakeetTranscriptionService>();

    /// <summary>sherpa's model-type discriminator for a NeMo transducer. Wrong value ⇒ a null
    /// handle, not an exception — see the guard in <see cref="LoadModelAsync"/>.</summary>
    private const string NemoTransducerModelType = "nemo_transducer";

    /// <summary>TRN-35 (2026-08-26): the shared tail of both empty-decode warnings, named so the
    /// third cause cannot be dropped by a future reword without failing a test. The rationale —
    /// and why the zeroCut/zeroEdge counters are POINTED AT rather than recomputed here — is the
    /// comment on <c>LogEmptyDecode</c>. Pinned by <c>ParakeetEmptyDecodeMessageTests</c>.</summary>
    internal const string EmptyDecodeCauseTail =
        "a speech-free chunk, or capture-chain damage (TRN-35); zeroCut/zeroEdge on this " +
        "recording's `Audio level:` line is the discriminator, and a healthy activeRms here " +
        "rules out NONE of them";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly bool _featureEnabled;
    private readonly int _numThreads;
    private readonly IParakeetPcppBackend? _pcppBackend;

    private OfflineRecognizer? _recognizer;
    private string? _loadedBundleDir;

    /// <summary>TRN-37 test seam (the <c>DetectSpeechOverride</c> precedent): the unload's
    /// disposal call, injectable so tests can plant a <c>GetUninitializedObject</c> recognizer
    /// without betting CI on sherpa's native destroy tolerating a zero handle — an AV there is
    /// unbounded by the managed catch. Null (production) = the real <c>Dispose()</c>.
    ///
    /// <para>A PROPERTY, not a field, and that is the whole of the precedent it cites: only the
    /// test assembly assigns it, so as a field the app's own compilation raises CS0649
    /// ("never assigned to") — which TRN-37 shipped, because an incremental rebuild does not
    /// re-emit a warning from an unchanged project and the re-run read clean. Auto-properties
    /// are exempt from that analysis, which is why <c>DetectSpeechOverride</c> one file over has
    /// always been one.</para></summary>
    internal Action<OfflineRecognizer>? DisposeRecognizerOverride { get; set; }
    // int rather than bool: Dispose races decode, and Interlocked needs an int. `_disposed` reads
    // through a property so every site sees the same volatile semantics.
    private int _disposedFlag;
    private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;

    /// <summary>
    /// <paramref name="featureEnabled"/> is injected rather than read from
    /// <c>ParakeetFeature.IsEnabled</c> here, so the disabled behaviour is testable in an ordinary
    /// build (a const in an <c>if</c> also makes the tail unreachable — CS0162).
    ///
    /// <para><paramref name="numThreads"/> defaults to <c>ProcessorCount / 2</c> — the formula
    /// the Whisper service ALSO used when the benchmark measured this engine (Whisper moved to
    /// VoiceInk's <c>min(8, PC − 2)</c> in TRN-44/46; this sherpa-era value is deliberately
    /// untouched, since re-measuring it is TRN-47's business, not a drive-by). It is
    /// deliberately NOT the VAD's 1-thread pin: that exists to dodge an OpenMP barrier in
    /// whisper.cpp, and onnxruntime does not use OpenMP, so the reasoning does not transfer.</para>
    /// </summary>
    /// <remarks><paramref name="pcppBackend"/> (TRN-29 slice 4) is the parakeet.cpp seam — null
    /// in every ordinary build (the sherpa-only era, byte-inert). The composition root registers
    /// the real coordinator only under <c>PcppFeature.IsEnabled</c>; tests inject fakes, which is
    /// what makes the routing and the cancellation races runnable in ordinary CI at all.</remarks>
    public ParakeetTranscriptionService(bool featureEnabled, int? numThreads = null,
        IParakeetPcppBackend? pcppBackend = null)
    {
        _featureEnabled = featureEnabled;
        _numThreads = numThreads ?? Math.Max(1, Environment.ProcessorCount / 2);
        _pcppBackend = pcppBackend;
    }

    public bool IsReady => Volatile.Read(ref _recognizer) is not null;

    /// <summary>See the type remarks: literal, not a placeholder.</summary>
    public HintTransportKind HintTransport => HintTransportKind.None;

    /// <summary>Not a generative model — nothing to stamp a vocabulary against.</summary>
    public string? GenerativeModelId => null;

    /// <summary>True when this build and machine can actually run the engine.</summary>
    public bool IsAvailable => _featureEnabled && ParakeetNativeProbe.IsSupported;

    /// <summary>
    /// Build (or reuse) the recognizer for a bundle directory. Cheap when already loaded — this sits
    /// behind the recording-start hot path, whose hotkey→pill latency is probe-pinned.
    /// </summary>
    internal async Task LoadModelAsync(string bundleDir, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked INSIDE the lock. The check before WaitAsync is only a fast path: a caller
            // parked on the gate resumes after Dispose has released it, and would then build a fresh
            // native recognizer into a disposed service — one nothing will ever dispose, because
            // Dispose already ran. A diff reviewer caught exactly this.
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recognizer is not null &&
                string.Equals(_loadedBundleDir, bundleDir, StringComparison.OrdinalIgnoreCase))
            {
                return;     // already loaded, and 3.1 s says do not rebuild it
            }

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Transducer.Encoder = Path.Combine(bundleDir, "encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = Path.Combine(bundleDir, "decoder.int8.onnx");
            config.ModelConfig.Transducer.Joiner = Path.Combine(bundleDir, "joiner.int8.onnx");
            config.ModelConfig.Tokens = Path.Combine(bundleDir, "tokens.txt");
            config.ModelConfig.ModelType = NemoTransducerModelType;
            config.ModelConfig.NumThreads = _numThreads;

            // Verified BEFORE construction, because a missing or empty file is the realistic cause of
            // the null native handle below, and catching it here yields a message that names the
            // file instead of a generic "could not be created".
            foreach (var required in new[]
                     {
                         config.ModelConfig.Transducer.Encoder, config.ModelConfig.Transducer.Decoder,
                         config.ModelConfig.Transducer.Joiner, config.ModelConfig.Tokens,
                     })
            {
                var info = new FileInfo(required);
                if (!info.Exists || info.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"The Parakeet model is incomplete: '{Path.GetFileName(required)}' is missing or empty. " +
                        "Delete the model on the Models page and download it again.");
                }
            }

            var sw = Stopwatch.StartNew();

            // Construction is multi-second native work; off the UI thread or it freezes the app.
            var built = await Task.Run(() => new OfflineRecognizer(config), ct).ConfigureAwait(false);

            // `built is null` would NEVER fire — an earlier version of this guard checked exactly
            // that and was useless. sherpa's C API returns a NULL POINTER for an invalid config
            // (c-api.cc: SherpaOnnxCreateOfflineRecognizer returns nullptr), but the managed wrapper
            // still hands back an object WRAPPING that null. The failure then surfaces at the first
            // CreateStream, far from the cause, and possibly as a native fault rather than an
            // exception.
            //
            // So the pointer itself is inspected. Reflection is warranted here: the package version
            // is pinned in the csproj, the field is the only expression of "did the factory
            // succeed", and there is no public surface for it. FAIL OPEN if the shape ever changes —
            // a package update must not brick the engine over a diagnostic.
            if (HasNullNativeHandle(built))
            {
                built.Dispose();
                throw new InvalidOperationException(
                    "The Parakeet recognizer could not be created from the installed model files. " +
                    "They may be corrupt — delete the model on the Models page and download it again.");
            }

            // Checked AGAIN, after construction. Holding the lock does not exclude Dispose here:
            // Dispose is BOUNDED at 5 s, and construction measures ~3.1 s and is native work that
            // can run long on a cold disk. A timed-out Dispose returns having set the flag WITHOUT
            // the lock, so without this the multi-second build lands in a disposed service and the
            // recognizer leaks with nothing left to release it.
            if (_disposed)
            {
                built.Dispose();
                throw new ObjectDisposedException(nameof(ParakeetTranscriptionService));
            }

            var previous = _recognizer;
            _recognizer = built;
            _loadedBundleDir = bundleDir;
            previous?.Dispose();     // only after the replacement is in place

            Logger.Information("Parakeet recognizer built in {ElapsedMs}ms ({Threads} threads)",
                sw.ElapsedMilliseconds, _numThreads);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null,
        Models.TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // `language` is accepted and ignored BY DESIGN — Parakeet v3 auto-detects and exposes no
        // language input. The pipeline already knows: EffectiveTranscriptionLanguage reports
        // EngineAutoDetectsOnly for this runtime, so history records the effective mode rather than
        // a pin the engine never honoured. Ignoring it silently here is what that helper prevents.
        //
        // `hints` is ignored because HintTransport is None — see the type remarks.
        // `diarize` is ignored: no diarization support, matching every local runtime.

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked inside the lock, for the same reason as LoadModelAsync — and it has to be
            // here TOO, which the round-3 fix missed: a decode holding the gate, a second one
            // queued behind it, and a Dispose that TIMES OUT (5 s bound) sets the flag and returns
            // without ever taking the gate. The queued call would then decode on a service whose
            // disposal has already completed. It would also report "No Parakeet model is loaded" —
            // true but misleading — whenever Dispose won the gate first.
            ObjectDisposedException.ThrowIf(_disposed, this);

            // TRN-29 slice 4: which engine serves THIS call — resolved under the lock so the
            // whole call runs one backend (never per-chunk mixed engines). Null backend = the
            // sherpa-only era, and the lines below are byte-identical to it.
            PcppAcquire pcppLease = default;
            var usePcpp = false;
            if (_pcppBackend is not null)
            {
                var acquire = await _pcppBackend.TryAcquireForTranscriptionAsync(ct).ConfigureAwait(false);
                if (acquire.Decision == PcppServe.Pcpp)
                {
                    pcppLease = acquire;
                    usePcpp = true;
                }
                else if (acquire.Decision == PcppServe.FailCall)
                {
                    // Challenge 2's specified outcome: a GGUF-only install whose server acquire
                    // failed is a NORMAL transcription failure (REL-12 retains the WAV, amber
                    // Retry) — never the misleading "No Parakeet model is loaded" a sherpa
                    // branch with no model would throw.
                    throw new InvalidOperationException(
                        "Local transcription is unavailable right now: the Parakeet engine could not start " +
                        "and no legacy engine remains. Try again, or re-download the model.");
                }
                else if (_recognizer is null)
                {
                    // PcppServe.Sherpa with NO recognizer built (self-review, concurrency F1):
                    // preparation resolved to pcpp (so LoadModelAsync never ran), and the
                    // backend flipped to sherpa between prepare and stop — the child died, the
                    // storm tripped, or a predecessor is still confirming its exit. The sherpa
                    // BUNDLE may well be on disk, but building its recognizer here means ~3.1 s
                    // of native construction through a load path that takes this same lock.
                    // Fail THIS call honestly instead: REL-12 retains the WAV, and the Retry
                    // re-runs preparation, which loads sherpa (or re-spawns the server) and
                    // succeeds. What must NOT happen is falling through to the legacy throw —
                    // "No Parakeet model is loaded" is false and points the user at nothing.
                    throw new InvalidOperationException(
                        "Local transcription hit an engine restart. Press Retry to transcribe this recording.");
                }
                // PcppServe.Sherpa with a built recognizer falls through to the unchanged path.
            }

            var recognizer = usePcpp
                ? null
                : _recognizer ?? throw new InvalidOperationException("No Parakeet model is loaded.");

            var (samples, sampleRate) = await Task.Run(
                () => Helpers.WavPcm.ReadMono16(audioFilePath), ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // TRN-10: long audio is CUT before decoding. sherpa-onnx does no internal windowing (the
            // whole reason whisper.cpp is unaffected by the same file), and past roughly a minute this
            // model silently drops speech — see Helpers/LongAudioChunker for the measurements. A
            // recording that fits produces a one-chunk plan, so the loop below IS the pre-TRN-10 code
            // in that case (modulo TRN-10b's input gain when the recording is quiet); there is
            // deliberately no separate short-circuit branch to keep in sync.
            //
            // TRN-22's investigation showed the drop ALSO occurs inside ordinary 15-35 s dictation
            // (content-dependent, sometimes mid-recording where TailRescue cannot see it), and that
            // the fault lives in sherpa's decode stack — the same weights decode those files
            // completely under another runtime. A VAD-segmented default plan was built, measured
            // against owner ground truth under a pre-registered gate, and REJECTED: net-negative on
            // the corpus, and the gated-rescue variant missed eligibility on a sample-size artifact.
            // The chunker therefore remains the plan; the segmenter, the planner and the candidate
            // arms live on in tools/parakeet-long-audio pending the batch-2 gate, and the full trail
            // is docs/plans/2026-08-20-trn22-vad-segmented-decode/50-decision.md (deltas 7-14).
            var plan = Helpers.LongAudioChunker.Plan(samples, sampleRate);

            // Whether the plan covers the whole recording. CollapseRecovery's retry decodes the
            // WHOLE raw buffer, so it gates on coverage — not on `plan.Count == 1`, which a
            // single-segment partial plan would satisfy while excluding audio (the TRN-22 latent
            // bug: the count gate would let the "recovery" swap a whole-buffer decode in over a
            // plan that deliberately excluded some of it). LongAudioChunker covers by contract, so
            // today this is always true; it is COMPUTED so the gate stays honest for any future
            // plan shape.
            var planCoversWholeRecording =
                plan.Count > 0 && plan[0].Start == 0
                && plan[^1].Start + plan[^1].Count == samples.Length
                && plan.Sum(c => c.Count) == samples.Length;

            // A zero-length chunk is legitimate ONLY as the sole chunk (an empty recording). One
            // appearing inside a multi-chunk plan would mean the planner had broken total coverage —
            // `ChunkedDecode` would skip it and the join would silently swallow the audio it stood for,
            // which is TRN-10's own defect reappearing one layer down. `Plan` is test-pinned against it
            // (every multi-chunk row bounds Count at 1..ceiling), so this is the runtime tripwire for a
            // future regression that slips past the suite.
            //
            // Deliberately checked HERE rather than inside `ChunkedDecode`: the anomaly is a property of
            // the PLAN, and the service is where a logger already exists. Putting it in the helper would
            // mean either a Serilog dependency in a file `tools/parakeet-long-audio` links, or a callback
            // threaded through the shipped signature for one diagnostic. A `Debug.Assert` there was tried
            // first and reverted — it contradicted the helper's own defensive test and hung the suite.
            var emptyChunks = plan.Count(c => c.Count == 0);
            if (plan.Count > 1 && emptyChunks > 0)
            {
                Logger.Warning(
                    "Parakeet chunk plan contains {EmptyChunks} zero-length chunk(s) of {ChunkCount} — " +
                    "audio may be dropped at a join; this should be unreachable",
                    emptyChunks, plan.Count);
            }

            var sw = Stopwatch.StartNew();

            var gain = default(Helpers.DecodeGainDecision);
            var chunkChars = new List<int>(plan.Count);
            var clippedTotal = 0;
            var recoveredChunks = 0;

            // TRN-15 tail-rescue capture: which raw slice ended the recording, which attempt's
            // decode produced its text, and that attempt's token timing. Reset per chunk inside
            // DecodeOne so an earlier chunk's timing can never leak across a chunk boundary; the
            // gain is per-ATTEMPT because the TRN-10c zero-gain retry means the text is not always
            // the whole-recording gain's product.
            float[]? rescueSlice = null;
            float[]? rescueTimestamps = null;
            double rescueGainDb = 0;
            // Initialized rather than `default` so Text is never null — today it is only read
            // inside the `rescueSlice is not null` branch, but that guard is the sole thing
            // keeping a default struct's null Text out of the returned transcript.
            var rescueOutcome = Helpers.TailRescueOutcome.NotFired(string.Empty);

            // Decode is synchronous native work — off the UI thread for the same reason as
            // construction, and inside the lock so a rebuild cannot swap the handle underneath it.
            // The TRN-10b gain decision rides the same Task.Run: it is a full pass over the
            // samples, decided ONCE for the whole recording (the measured lever was uniform gain;
            // per-chunk adaptation was reviewed and rejected as an unmeasured divergence).
            //
            // `ct` stays on Task.Run. Dropping it while extracting the loop was behaviour-preserving
            // (the check at the top of the read above already rejects an already-cancelled token, and
            // ChunkedDecode re-checks before the first chunk) — but it is a needless deviation from the
            // pre-TRN-10 call, and a reviewer had to spend a round establishing the equivalence.
            //
            // TRN-29 race row 2's in-flight marker: set strictly before the pcpp HTTP call and
            // cleared only on its SUCCESS, so an exception unwinding leaves it TRUE — which is
            // exactly what the escalation catch below reads. Cancellation during the WAV read,
            // planning, or between slices finds it FALSE and does not escalate (rows 3/4 — the
            // plan round's unnecessary-stall catch). Single-threaded per call under _lock, so a
            // plain captured local is enough.
            var pcppRequestInFlight = false;

            string text;
            try
            {
                text = await Task.Run(() =>
            {
                gain = Helpers.DecodeInputGain.Decide(samples, sampleRate);
                var joined = Helpers.ChunkedDecode.Run(plan, samples, DecodeOne, ct);

                // TRN-15: one bounded extra decode of the tail, only when the final chunk's token
                // timing proves the main decode stopped before the speech did. Inside the same
                // Task.Run — native work, same lock scope, and the rescue must finish before the
                // pipeline sees the text. TailRescue owns the whole decision (trigger, slice, gain
                // re-application, anchor-gated append-only merge); the closure is a plain
                // fresh-stream decode, deliberately NOT DecodeOne — the rescue is not a chunk, and
                // must not touch chunkChars or the TRN-10b/c tripwires.
                // TRN-29: the tail rescue and the collapse recovery below are SHERPA-fault
                // mechanisms gated off the pcpp branch — their triggers read sherpa token
                // timestamps this transport does not return, and whether parakeet.cpp exhibits
                // the faults they repair is G4's measurement, not an assumption either way.
                // (EmptyDecodeRecovery stays live on both branches: its retry re-calls the same
                // decode closure, engine-agnostically.)
                if (!usePcpp && rescueSlice is not null)
                {
                    rescueOutcome = Helpers.TailRescue.TryAppend(
                        rescueSlice, sampleRate, rescueGainDb, joined, rescueTimestamps,
                        input =>
                        {
                            using var stream = recognizer!.CreateStream();
                            stream.AcceptWaveform(sampleRate, input);
                            recognizer.Decode(stream);
                            return stream.Result.Text?.Trim() ?? string.Empty;
                        },
                        ct);
                    joined = rescueOutcome.Text;
                }

                // TRN-19: a NON-EMPTY collapse — the decode returned confident text covering almost
                // none of the speech (measured: 5 chars for a 3.12 s sentence under +12.2 dB while
                // the raw decode returned all 49). TailRescue's gap is the detector and it FIRED on
                // the incident in production; what was missing was the denominator and a recovery
                // its append-only merge cannot perform. One zero-gain re-decode (TRN-10c's
                // conditioning, reached by a trigger that can see non-empty), accepted only on the
                // retry's own TIME coverage — never text length (TRN-21: the engine can invent
                // words, so longer proves nothing). Same Task.Run: native work, same lock scope,
                // and the swap must land before the pipeline sees the text. Fresh stream,
                // deliberately NOT DecodeOne (no chunkChars/tripwire pollution — the TailRescue
                // closure's rule). Corpus-calibrated to fire once in 630 recordings; the replay
                // over that corpus is the change's validation evidence.
                if (!usePcpp && Helpers.CollapseRecovery.ShouldAttempt(
                        planCoversWholeRecording, joined, rescueGainDb, rescueOutcome.GapSeconds,
                        samples.Length / (double)sampleRate,
                        tailRescueAppended: rescueOutcome.AppendedWordCount > 0))
                {
                    ct.ThrowIfCancellationRequested();
                    using var retryStream = recognizer!.CreateStream();
                    retryStream.AcceptWaveform(sampleRate, samples);
                    recognizer.Decode(retryStream);
                    var retryResult = retryStream.Result;
                    var retryText = retryResult.Text?.Trim() ?? string.Empty;

                    var duration = samples.Length / (double)sampleRate;
                    var lastActive = Helpers.TailRescue.LastActiveTime(samples, sampleRate);
                    if (Helpers.CollapseRecovery.ShouldSwap(
                            joined, rescueOutcome.GapSeconds, retryText, retryResult.Timestamps, lastActive, duration))
                    {
                        // Counts only, never content — the TRN-10b instrument's discipline.
                        Logger.Information(
                            "Parakeet collapse recovery (TRN-19): decode ended {GapSeconds:F2}s before the last speech of a {DurationSeconds:F2}s recording; zero-gain re-decode covers the speech — swapped ({FromChars} -> {ToChars} chars)",
                            rescueOutcome.GapSeconds, duration, joined.Length, retryText.Length);
                        joined = retryText;
                    }
                    else
                    {
                        // The honest failure: the original text stands (a retry without coverage
                        // evidence must never replace user-visible text), and the Warning is the
                        // detector's surface — the same contract as the TRN-10b tripwire. No pill
                        // notice yet, deliberately: zero unrecovered instances exist in 630
                        // corpus recordings, so a user surface has no measured false-positive
                        // budget to be designed against (the TRN-18 surface earned its copy from
                        // three field instances).
                        Logger.Warning(
                            "Parakeet collapse suspected (TRN-19): decode stopped {GapSeconds:F2}s before the last speech of a {DurationSeconds:F2}s recording and the zero-gain re-decode did not improve coverage — transcript may be incomplete",
                            rescueOutcome.GapSeconds, duration);
                    }
                }

                return joined;
            }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (usePcpp && pcppRequestInFlight)
            {
                // Race row 2: cancellation interrupted an ACTUAL in-flight server request. The
                // escalation (grace → one probe → kill-if-unresponsive) runs while _lock is
                // still held, which is what makes a queued second transcription unable to
                // observe — or lease — a half-killed child. Then the cancellation propagates
                // exactly as before.
                await _pcppBackend!.OnCancelledInFlightAsync(pcppLease.Generation).ConfigureAwait(false);
                throw;
            }
            catch (PcppDecodeException ex)
            {
                // The whole-call failure policy: the FIRST failed slice aborted the call
                // (ChunkedDecode propagates), and the generation is retired so a Retry acquires
                // a FRESH child instead of replaying a wedged one (plan round, challenge 5c).
                // Surfaces as the normal transcription failure — REL-12 retains the WAV.
                //
                // The restart question is asked AFTER the failure is charged, so the failure
                // that LATCHES the tripwire already gets the honest message: once the backend
                // is unrunnable for this process and no sherpa bundle remains, "Retry will
                // restart the engine" is a promise the user loops on forever (G6 plan round,
                // the futile-copy fix).
                await _pcppBackend!.OnWholeCallFailedAsync(pcppLease.Generation).ConfigureAwait(false);
                var advice = _pcppBackend.RetryRequiresAppRestart()
                    ? "Restart VoiceWink to try again."
                    : "Retry will restart the engine.";
                throw new InvalidOperationException(
                    $"Local transcription failed ({ex.FailureClass}). {advice}", ex);
            }

            // TRN-50: the safety net for a GPU driver that decodes real speech to NOTHING (the
            // owner's ARM64 laptop, 2026-09-03: 3/3 dictations empty on the Adreno's Vulkan path,
            // 3/3 fine on the CPU). A pcpp whole-call that came back EMPTY on audio above the
            // silence floor is re-decoded ONCE through a throwaway CPU child — same plan, same
            // gain, the device the only variable — and text from it replaces the empty result.
            // The backend decides whether to attempt at all (positive GPU evidence from the
            // child's own device line, once per session) and what a recovery means for the rest
            // of the session; here the call is bounded by that child's health budget and the
            // client deadline, honours ct, and never fires on a typed failure (thrown above) or
            // on silence (empty is the correct answer there). The sherpa branch keeps its own
            // recoveries; this one is pcpp-only by construction.
            if (usePcpp && text.Length == 0 && gain.ActiveRmsDbfs > Helpers.DecodeInputGain.SilenceFloorDbfs)
            {
                var cpuText = await _pcppBackend!.TryDecodeOnCpuFallbackAsync(
                    pcppLease.Generation, plan, samples, sampleRate, gain.GainDb, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(cpuText))
                {
                    text = cpuText;
                }
            }

            // NotifyServed deliberately does NOT fire here (Codex plan round, 2026-08-24): since
            // it became the AUTO-CLEANUP trigger, it must gate on the whole call SUCCEEDING — a
            // decode whose result cancellation is about to discard must not delete the legacy
            // fallback for a dictation the user never received. It fires after the final
            // cancellation check, immediately before the successful return.

            // A fresh stream per chunk is mandatory, not hygiene — a stream ACCUMULATES the waveform
            // it is given, so reuse would re-decode everything before it.
            string DecodeOne(float[] slice)
            {
                // TRN-10c: the empty-result retry lives in Helpers/EmptyDecodeRecovery rather than
                // inline here, because a real decode needs the sherpa runtime and a 670 MB model —
                // inline, the retry rule would be reachable only by owner UAT, which is the exact
                // gap ChunkedDecode was extracted to close for the aggregation loop.
                var attempt = 0;
                var firstClipped = 0;

                // TRN-15: this chunk is now the rescue's subject; timing captured from any earlier
                // chunk is dead. Only a non-empty attempt below repopulates the timestamps.
                rescueSlice = slice;
                rescueTimestamps = null;

                var part = Helpers.EmptyDecodeRecovery.DecodeWithRecovery(
                    slice, sampleRate, gain.GainDb,
                    // The injection gate: a retry may only run when this slice IS the whole
                    // recording. On a multi-chunk plan its text would be joined into whatever the
                    // other chunks produced, and a speech-free chunk decodes empty CORRECTLY.
                    // (The helper documents a TRN-22 widening for VAD-derived plans — per-slice
                    // speech evidence; its only live caller is the harness's r3 candidate arm,
                    // because the app plans with LongAudioChunker.)
                    sliceIsWholeRecording: plan.Count == 1,
                    (toDecode, gainDb) =>
                    {
                        // Copy-on-gain, never in place: `toDecode` may BE the original recording
                        // array (ChunkedDecode.SliceFor's single-chunk hand-over). Zero gain returns
                        // it unchanged, keeping the healthy path allocation-identical to pre-TRN-10b
                        // — and the retry, which is always zero gain, allocation-free too.
                        var input = Helpers.DecodeInputGain.Apply(toDecode, gainDb, out var clipped);
                        if (attempt++ == 0) firstClipped = clipped;

                        if (usePcpp)
                        {
                            // The marker brackets ONLY the HTTP call; cleared on SUCCESS alone,
                            // so an exception (cancel or typed failure) unwinds with it set —
                            // see its declaration for the race table this implements. No
                            // timestamps come back on this transport; the sherpa-specific tail
                            // machinery is gated off above.
                            pcppRequestInFlight = true;
                            var decodedRemote = _pcppBackend!.DecodeSlice(
                                pcppLease.BaseUri!, input, sampleRate, ct);
                            pcppRequestInFlight = false;
                            return decodedRemote;
                        }

                        using var stream = recognizer!.CreateStream();
                        stream.AcceptWaveform(sampleRate, input);
                        recognizer.Decode(stream);
                        var result = stream.Result;
                        var decoded = result.Text?.Trim() ?? string.Empty;
                        if (decoded.Length > 0)
                        {
                            // TRN-15: the attempt whose text SURVIVES is the one whose token timing
                            // and gain the tail rescue must reason about — after a TRN-10c zero-gain
                            // recovery that is the retry, not the whole-recording gain decision.
                            rescueTimestamps = result.Timestamps;
                            rescueGainDb = gainDb;
                        }

                        return decoded;
                    },
                    outcome =>
                    {
                        if (outcome.Recovered) recoveredChunks++;
                        LogEmptyDecode(slice, outcome);
                    },
                    ct);

                // The first attempt's clipping IS all the clipping: TRN-10c's retry runs at zero
                // gain, and DecodeInputGain.Apply clips nothing there by construction. This stays a
                // first-attempt snapshot rather than a running total so it can never disagree with
                // the `gain=` it is printed beside, which is also the first attempt's.
                clippedTotal += firstClipped;

                chunkChars.Add(part.Length);
                return part;
            }

            // The TRN-10b tripwire, now also reporting what the TRN-10c retry made of it. This
            // engine's failure mode is an EMPTY result that reports success, and the reopened card
            // existed because nothing made that visible — so a SILENT recovery would recreate the
            // same invisibility one layer further in. Index counts DECODED chunks (a zero-length
            // chunk never reaches the closure). Counts, durations and levels only; never content.
            //
            // TRN-35 (2026-08-26): the cause list names THREE causes, because naming two sent a
            // real investigation at the engine for half a session. The 2026-08-25 corpus is the
            // counter-example that forced it — same passage, same mic, same train: a −34.4 dBFS
            // chunk decoded 0 chars while a −67.1 dBFS take, 32 dB quieter and far below TRN-10b's
            // ≈ −35.5 cliff, decoded 557 clean characters at +0.0 dB. Level was not the variable;
            // the failed recording carried zeroEdge=6 (an OS noise-suppression gate chopping word
            // boundaries at 100% input). So `activeRms` printed on this very line is NOT evidence
            // of the cause in either direction, and the message now says so.
            //
            // The counters are NOT computed here, deliberately. `ZeroRunClassifier` is documented
            // to judge PRE-gain audio: `slice` is pre-DECODE-gain but the file it was read from is
            // already post-AUD-2 DISK gain, and that layer's own doc records the bias — a boost
            // lifts quiet side-windows over the −55 dBFS activity floor and converts ordinary
            // pauses into "cuts". Classifying here would therefore over-count on exactly the quiet
            // recordings this warning fires for, i.e. invent capture damage to explain a chunk
            // that had none. The recording layer already emits the counters on its own line from
            // the correct domain; pointing at that line is the honest fix, and a number that can
            // be wrong is worse than a pointer that cannot.
            void LogEmptyDecode(float[] slice, Helpers.EmptyDecodeOutcome outcome)
            {
                var seconds = slice.Length / (double)sampleRate;

                if (outcome is { Retried: true, Recovered: true })
                {
                    Logger.Information(
                        "Parakeet chunk {ChunkIndex} ({ChunkSeconds:F1}s, activeRms={ActiveRms:F1}dBFS) decoded empty at +{GainDb:F1}dB and RECOVERED on a zero-gain re-decode (TRN-10c) — the gain, not the audio, was what failed",
                        chunkChars.Count, seconds, outcome.ActiveRmsDbfs, outcome.FirstGainDb);
                    return;
                }

                if (outcome.Retried)
                {
                    Logger.Warning(
                        "Parakeet chunk {ChunkIndex} ({ChunkSeconds:F1}s, activeRms={ActiveRms:F1}dBFS) decoded empty at +{GainDb:F1}dB AND on a zero-gain re-decode — quiet-audio collapse (TRN-10b/c), " + EmptyDecodeCauseTail,
                        chunkChars.Count, seconds, outcome.ActiveRmsDbfs, outcome.FirstGainDb);
                    return;
                }

                // No retry: the chunk was already unconditioned (nothing to flip), the level was
                // unmeasurable, cancellation landed, or it is silence. Silence is the expected,
                // CORRECT empty — staying quiet there is why the pre-TRN-10c guard existed, and it
                // is preserved verbatim.
                if (outcome.ActiveRmsDbfs > Helpers.DecodeInputGain.SilenceFloorDbfs)
                {
                    Logger.Warning(
                        "Parakeet chunk {ChunkIndex} ({ChunkSeconds:F1}s, activeRms={ActiveRms:F1}dBFS, gain=+{GainDb:F1}dB) decoded empty on non-silent audio with no alternative conditioning to try — quiet-audio collapse (TRN-10b), " + EmptyDecodeCauseTail,
                        chunkChars.Count, seconds, outcome.ActiveRmsDbfs, outcome.FirstGainDb);
                }
            }

            // Only when the recording was actually cut — the single-chunk CHUNKING path is
            // unchanged, and its decode input is byte-identical too whenever no gain applies
            // (≥ −30 dBFS active-RMS; a quiet single chunk gets the line below instead).
            // Counts, durations and levels only; never transcript content.
            // TRN-10c: both lines below name `gain=`, which is the FIRST attempt's. A recovered
            // chunk's text did not come from that transform — it came from a zero-gain re-decode —
            // so without this the line reads as a complete account of how the transcript was made
            // and quietly is not. The per-chunk Information line carries the detail; this is the
            // pointer that stops the summary contradicting it.
            var recovery = recoveredChunks > 0 ? $", recoveredAtZeroGain={recoveredChunks}" : string.Empty;

            if (plan.Count > 1)
            {
                Logger.Information(
                    "Parakeet decoded {ChunkCount} chunks in {ElapsedMs}ms ({AudioSeconds:F1}s audio, gain=+{GainDb:F1}dB, activeRms={ActiveRms:F1}dBFS, clipped={ClippedSamples}{Recovery}): {ChunkChars}",
                    plan.Count, sw.ElapsedMilliseconds, samples.Length / (double)sampleRate,
                    gain.GainDb, gain.ActiveRmsDbfs, clippedTotal, recovery,
                    string.Join(" ", chunkChars.Select(c => $"{c}ch")));
            }
            else if (gain.AppliesGain)
            {
                // The single-chunk sibling of AUD-2's one-line instrument: emitted only when the
                // input was actually conditioned, so healthy short recordings stay log-identical.
                Logger.Information(
                    "Parakeet input gain: +{GainDb:F1}dB (activeRms={ActiveRms:F1}dBFS, {AudioSeconds:F1}s audio, clipped={ClippedSamples}{Recovery})",
                    gain.GainDb, gain.ActiveRmsDbfs, samples.Length / (double)sampleRate, clippedTotal, recovery);
            }

            // TRN-15: the rescue's own instrument — this defect is invisible in the output (the
            // model punctuates whatever it emits), so a silent rescue would recreate the same
            // invisibility one layer further in. Fires on both outcomes: an append IS the recovery,
            // and "fired, appended 0" is the anchor guard refusing a disagreeing tail decode.
            // Counts and durations only; never transcript content.
            if (rescueOutcome.Fired)
            {
                Logger.Information(
                    "Parakeet tail rescue (TRN-15): decode ended {GapSeconds:F2}s before the last speech; appended {AppendedWords} word(s)",
                    rescueOutcome.GapSeconds, rescueOutcome.AppendedWordCount);
            }
            else if (rescueOutcome.GapSeconds > 0)
            {
                // The beyond-window skip: the decode stopped further before the speech end than the
                // rescue can reach. Correct to skip (the anchor cannot be in the slice) — and the
                // LARGEST loss the trigger can see, so it warns rather than passing as healthy.
                Logger.Warning(
                    "Parakeet tail rescue (TRN-15): decode ended {GapSeconds:F2}s before the last speech — beyond the {WindowSeconds:F0}s rescue window, speech may be missing from the end",
                    rescueOutcome.GapSeconds, Helpers.TailRescue.RescueWindowSeconds);
            }

            // The token cannot INTERRUPT native decode — once the delegate starts, `Task.Run`'s
            // token is only a pre-start check. Without this, a cancelled recording carried its
            // result onward into formatting and paste whenever inference eventually returned.
            ct.ThrowIfCancellationRequested();
            if (usePcpp)
            {
                // Protocol-success bookkeeping — strictly after the final cancellation check, so
                // a result cancellation discards never arms anything (Codex plan round). This
                // resets the failure tripwire and ARMS the proof; the proof itself is committed
                // by the ViewModel's NotifyUsableTranscription only once the machine-owned text
                // pipeline yields non-empty text (Codex diff r1+r2 — raw-empty AND
                // pipeline-emptied outcomes must never count as pcpp proving itself).
                _pcppBackend!.NotifyWholeCallSucceeded();

                // TRN-37: pcpp just served, so a resident sherpa recognizer is ~770 MB doing
                // nothing for the rest of the process (the migration-window population: sherpa
                // served earlier in this session, then the GGUF verified). Unload it — inside
                // _lock, so it is exclusive with every recognizer toucher (build/rebuild/decode/
                // Dispose's lock path; Dispose's 5 s-timeout abandon never touches the field).
                // Strictly BEFORE the ViewModel's NotifyUsableTranscription can fire the legacy
                // bundle's auto-cleanup delete — belt-and-braces only: the coordinator's measured
                // note (TryDeleteLegacyCore, 2026-08-24) is that a live recognizer holds NO bundle
                // files open, so the ordering protects nothing that measurement hasn't already
                // excluded; the ~770 MB release is this block's real payoff (Kimi diff round).
                // FAIL-SOFT: the unload is an optimization on a call that
                // already succeeded — a throwing Dispose logs and still nulls the reference.
                // Honest cost (Kimi plan round): a later mid-session latch-back to sherpa
                // THROWS the honest "engine restart" failure in-call; the ~3.1 s rebuild
                // happens at the Retry's prepare — or at an ordinary recording start whose
                // prepare resolves ServeSherpa while the bundle still exists (the probe-pinned
                // hot path pays it there). The null-out on a FAILED dispose is deliberate: if
                // Dispose threw after the native destroy, a retained reference would hand a
                // later latch-back a dangling handle (an AV); dropping it costs at most the
                // memory this block exists to reclaim.
                if (_recognizer is not null)
                {
                    var beforeMb = Environment.WorkingSet / (1024 * 1024);
                    try
                    {
                        (DisposeRecognizerOverride ?? (r => r.Dispose()))(_recognizer);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Parakeet sherpa recognizer unload failed - reference dropped anyway");
                    }
                    _recognizer = null;
                    _loadedBundleDir = null;
                    // Working-set delta is DIRECTIONAL (GC/arena timing) — the permanent field
                    // instrument for the ~770 MB claim, counts-only.
                    Logger.Information(
                        "Parakeet sherpa recognizer unloaded after pcpp serve (working set {BeforeMb} -> {AfterMb} MB)",
                        beforeMb, Environment.WorkingSet / (1024 * 1024));
                }
            }
            return text;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The reflection this project's null-handle guard runs on. Returns the raw pointer, or null
    /// when the wrapper's shape is not the one this pinned package version has.
    ///
    /// <para><b>Internal so a test can pin the shape.</b> Fail-open reflection that stops matching is
    /// SILENT — the exact trap <c>CaptureEndpointClassifier</c> shipped dead through, where a
    /// <c>is int</c> pattern never matched a boxed <c>uint</c> and every endpoint classified Unknown
    /// with nothing to show for it. This one had the same defect in its first draft: the field is
    /// <see cref="HandleRef"/>, and an <c>is IntPtr</c> pattern does not unbox across types. A test
    /// asserts the field is found, so a package update fails the build instead of quietly turning
    /// the guard off.</para>
    /// </summary>
    internal static IntPtr? TryReadNativeHandle(OfflineRecognizer recognizer)
    {
        try
        {
            var field = typeof(OfflineRecognizer).GetField("_handle",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic);

            // HandleRef, NOT IntPtr — verified against org.k2fsa.sherpa.onnx 1.13.4. Fully
            // qualified because an unqualified `HandleRef` binds to CsWinRT's generic
            // HandleRef<THandle> from this namespace (CLAUDE.md's Services.System shadowing rule).
            return field?.GetValue(recognizer) is global::System.Runtime.InteropServices.HandleRef href
                ? href.Handle
                : null;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not inspect the sherpa recognizer handle; skipping the null-handle check");
            return null;
        }
    }

    /// <summary>
    /// The DECISION the guard makes, split from the reflection that feeds it so it is testable
    /// without native code.
    ///
    /// <para>A diff reviewer asked to test <see cref="TryReadNativeHandle"/> itself rather than only
    /// the package's field shape. The first answer here was that it could not be tested — a real
    /// <c>OfflineRecognizer</c> needs the native library and the 670 MB model. That was wrong, and
    /// the same reviewer supplied the way round it: <c>GetUninitializedObject</c> allocates one
    /// WITHOUT running the constructor, so the field lookup, the <c>HandleRef</c> unbox and the
    /// value read all execute with no native code, against the zero handle a failed create leaves.
    /// <see cref="ParakeetRuntimeTests"/> does exactly that.</para>
    ///
    /// <para>The split below still earns its keep: it isolates the three-way BRANCH — null pointer,
    /// live pointer, unreadable — from the reflection, so the "unreadable ⇒ fail open" case is
    /// testable without contriving a wrapper whose shape has changed.</para>
    ///
    /// <para>Unreadable answers <c>false</c>, i.e. "looks fine". A package update must degrade the
    /// diagnostic, never brick the engine.</para>
    /// </summary>
    internal static bool IsNullHandle(IntPtr? handle) => handle is { } h && h == IntPtr.Zero;

    /// <summary>
    /// Whether the wrapper is holding a NULL native pointer. See the call site for why reflection is
    /// the right instrument here.
    /// </summary>
    private static bool HasNullNativeHandle(OfflineRecognizer recognizer)
    {
        var handle = TryReadNativeHandle(recognizer);
        if (handle is null)
        {
            Logger.Warning("Could not inspect the sherpa recognizer handle; skipping the null-handle check");
            return false;
        }

        return IsNullHandle(handle);
    }

    public void Dispose()
    {
        // Volatile + exchange: Dispose can race a decode, and a plain bool read gives the other
        // thread no ordering guarantee about the writes that precede it.
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        // Bounded: a wedged native call must not hang shutdown. Unbounded would freeze quit behind
        // a frozen recognizer, which is worse than leaking one.
        if (_lock.Wait(TimeSpan.FromSeconds(5)))
        {
            try
            {
                _recognizer?.Dispose();
                _recognizer = null;
            }
            finally { _lock.Release(); }

            // The semaphore is deliberately NOT disposed — on either path. WhisperTranscriptionService
            // does the same with its own lock, for the reason that applies here too: nothing proves
            // this is the last toucher. On the timeout path an in-flight decode still owns the gate
            // and WILL Release() when it finishes; on THIS path a concurrent caller may already be
            // parked in WaitAsync and takes the gate the moment it is released. Either one then meets
            // a disposed semaphore and throws ObjectDisposedException on a threadpool thread during
            // shutdown. A SemaphoreSlim with no registered wait handle holds no unmanaged resource,
            // so leaking it costs nothing the process is not about to reclaim anyway.
        }
        else
        {
            Logger.Warning(
                "Parakeet dispose timed out waiting for an in-flight operation; abandoning the recognizer and leaving the gate alive for it");
        }
    }
}
