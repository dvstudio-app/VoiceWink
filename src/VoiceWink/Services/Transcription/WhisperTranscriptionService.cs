using Serilog;
using VoiceWink.Helpers;
using Whisper.net;
using NAudio.Wave;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Local Whisper transcription via Whisper.NET.
/// Reads WAV, converts Int16→Float, calls Whisper.net.
/// </summary>
public sealed class WhisperTranscriptionService : ITranscriptionService, IDisposable, IAsyncDisposable
{
    private static ILogger Logger => Log.ForContext<WhisperTranscriptionService>();
    private const string AutoLanguage = "auto";

    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly GpuWarmup _warmup;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    /// <summary>TRN-64: the CATALOG name of the loaded model, when the caller knew it
    /// (<c>WhisperLocalRuntime</c> does; a bare path load leaves it null). The per-model
    /// self-test and its gate key on this, never on the file path. Volatile: the gate reads it
    /// WITHOUT the model lock (see <see cref="AwaitGpuSelfTestGateAsync"/>); writes stay under it.</summary>
    private volatile string? _loadedModelName;
    private string _language = AutoLanguage;
    private string? _prompt;

    public WhisperTranscriptionService() : this(GpuWarmup.Instance)
    {
    }

    /// <summary>Test seam: the warm-up coordinator whose gate this service consults.</summary>
    internal WhisperTranscriptionService(GpuWarmup warmup)
    {
        _warmup = warmup;
    }

    /// <summary>Test seam: pretend a catalog model is loaded (the gate needs a name, not a processor).</summary>
    internal void SetLoadedModelForTest(string? modelName) => _loadedModelName = modelName;

    // Upper bound on how long shutdown-time disposal waits for an in-flight transcription to
    // release the model lock. Dispose runs from DI-container teardown at app exit; blocking the
    // shutdown (UI) thread indefinitely on a long/hung native inference hangs the whole app.
    // internal so tests can shorten it. See Dispose/DisposeAsync for the fail-safe on timeout.
    internal TimeSpan DisposeLockTimeout = TimeSpan.FromSeconds(5);

    // Test-only seams to simulate a transcription holding the model lock during a dispose.
    internal bool TryAcquireModelLockForTest() => _modelLock.Wait(0);
    internal void ReleaseModelLockForTest() => _modelLock.Release();

    /// <summary>
    /// Load a Whisper model from disk. Call once before transcribing, or when switching models.
    /// </summary>
    /// <param name="modelName">TRN-64: the catalog name of the model at <paramref name="modelPath"/>,
    /// when the caller knows it. Keyed per model by the GPU self-test; a load with no name keeps
    /// the previous name only while the path is unchanged (a language reload), else clears it.</param>
    public async Task LoadModelAsync(string modelPath, string? language = null, CancellationToken ct = default, string? modelName = null)
    {
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _loadedModelName = modelName ?? (string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) ? _loadedModelName : null);
            try
            {
                await LoadModelCoreAsync(modelPath, language, ct).ConfigureAwait(false);
            }
            catch
            {
                // Kimi diff r1 A1: the name must never describe a model whose processor the failed
                // load just tore down — the gate reads it lock-free and would queue a self-test,
                // and refuse with the Unstable sentence, under a name nothing is loaded for. (It is
                // assigned BEFORE the load on purpose: a dictation arriving mid-load gates on the
                // incoming model.)
                _loadedModelName = null;
                throw;
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>Inner implementation — caller must already hold _modelLock.</summary>
    private async Task LoadModelCoreAsync(string modelPath, string? language, CancellationToken ct)
    {
        await LoadModelCoreAsync(modelPath, language, prompt: null, ct).ConfigureAwait(false);
    }

    /// <summary>Inner implementation — caller must already hold _modelLock.</summary>
    private async Task LoadModelCoreAsync(string modelPath, string? language, string? prompt, CancellationToken ct)
    {
        var lang = NormalizeLanguage(language ?? _language);
        var normalizedPrompt = NormalizePrompt(prompt);
        if (_loadedModelPath == modelPath && _language == lang && _prompt == normalizedPrompt && _processor != null)
        {
            Logger.Information("Model already loaded: {Path}", modelPath);
            // TRN-64 (self-review, fail-open lens): the self-test is re-offered on THIS path too. A
            // run cancelled by a model selection or a language reload records nothing, and the next
            // load of the same model — a re-selection, or the prepare every dictation makes — used
            // to return here without queueing, leaving the model ungated for the session. Idempotent:
            // a run in flight for this model, or anything this session already learned about it on
            // this processor, makes it a no-op.
            _warmup.QueueWhisperWarmup(this, _loadedModelName);
            return;
        }

        // Determine whether the factory itself needs to be rebuilt (model path or language changed)
        // or whether only the processor needs to be rebuilt (prompt-only change, which is inference-time
        // context and does not require re-reading the model file from disk).
        bool factoryStale = _factory == null
            || _loadedModelPath != modelPath
            || _language != lang;

        if (factoryStale)
        {
            Logger.Information("Loading Whisper model: {Path}, language: {Lang}, hasPrompt: {HasPrompt}",
                modelPath, lang, !string.IsNullOrWhiteSpace(normalizedPrompt));

            // Dispose processor first, then factory — processor holds native pointers into factory,
            // so factory must outlive processor. Disposing in this order is safe.
            if (_processor != null)
            {
                await _processor.DisposeAsync().ConfigureAwait(false);
                _processor = null;
            }
            _factory?.Dispose();
            _factory = null;
            _loadedModelPath = null;

            _language = lang;
            _prompt = normalizedPrompt;

            await Task.Run(() =>
            {
                // TRN-68: build on the Vulkan device this process's own enumeration selects. The
                // FIRST factory in a process cannot know the rows (ggml prints them inside this
                // very call), so the helper builds once more when that first build revealed a
                // dedicated adapter behind an integrated one — BEFORE any processor exists, the
                // one moment a WhisperFactory may be disposed. Every later factory builds right
                // first time. A single-GPU machine takes the default ordinal 0, byte-identical to
                // the plain FromPath overload.
                var rebuilt = false;
                _factory = WhisperBackendLog.BuildOnSelectedDevice(
                    device => WhisperFactory.FromPath(modelPath, WhisperFactoryOptions.Default with { GpuDevice = device }),
                    factory => factory.Dispose(),
                    () => WhisperBackendLog.SelectedGpuDevice,
                    out var deviceUsed,
                    (first, selected) =>
                    {
                        rebuilt = true;
                        Logger.Information(
                            "Whisper: rebuilt the model on Vulkan device {GpuDevice} ({GpuName}) - device {FirstGpuDevice} is an integrated adapter (TRN-68)",
                            selected, WhisperBackendLog.ObservedGpuName ?? GpuToggleAvailability.UnnamedGpu, first);
                    });
                if (!rebuilt && deviceUsed != 0)
                {
                    // The rows were already known: say which non-default device this factory took,
                    // once per load — on a single-GPU machine (device 0) the line is silent.
                    Logger.Information("Whisper: model built on Vulkan device {GpuDevice} ({GpuName}) (TRN-68)",
                        deviceUsed, WhisperBackendLog.ObservedGpuName ?? GpuToggleAvailability.UnnamedGpu);
                }
                _processor = BuildProcessor(_factory, lang, normalizedPrompt, modelPath);
            }, ct).ConfigureAwait(false);

            _loadedModelPath = modelPath;
            Logger.Information("Whisper model loaded successfully");

            // TRN-27: report which native backend actually loaded (Vulkan or Cpu) — once per
            // process, from whichever factory site succeeds first. Usually the VAD gate beat us
            // to it and this no-ops; it fires here when a Whisper model is the first native load
            // (e.g. the gate is disabled by the REL-15 lever or unavailable on this CPU).
            WhisperBackendLog.TryLogOnce();

            // TRN-49: hand the freshly loaded model to the warm-up coordinator. Fire-and-forget
            // BY DESIGN — the warm-up is a latency optimisation and nothing about it may delay
            // this load, the caller, or a recording (GpuWarmup owns the once-per-version marker,
            // the Vulkan-backend condition, and the admission-cancel; it no-ops in every other
            // case, including tests, where Configure was never called). TRN-64: a NEW processor
            // re-offers a test that could form no verdict on the old one (a pinned language, no
            // clip, no device) — processorRebuilt says so; a verdict already reached stands.
            _warmup.QueueWhisperWarmup(this, _loadedModelName, processorRebuilt: true);
        }
        else
        {
            // Only the prompt changed — rebuild the processor from the existing factory.
            // This avoids re-reading the model file from disk (2-8 seconds) because the
            // factory already holds the loaded GGML weights in memory.
            Logger.Information("Prompt changed — rebuilding processor (keeping factory): hasPrompt: {HasPrompt}",
                !string.IsNullOrWhiteSpace(normalizedPrompt));

            if (_processor != null)
            {
                await _processor.DisposeAsync().ConfigureAwait(false);
                _processor = null;
            }

            _prompt = normalizedPrompt;

            await Task.Run(() =>
            {
                _processor = BuildProcessor(_factory!, lang, normalizedPrompt, _loadedModelPath ?? modelPath);
            }, ct).ConfigureAwait(false);

            Logger.Information("Processor rebuilt successfully");
            _warmup.QueueWhisperWarmup(this, _loadedModelName, processorRebuilt: true); // TRN-64: a new processor, same rule as above
        }
    }

    /// <summary>
    /// Construct a <see cref="WhisperProcessor"/> from an already-loaded factory.
    /// Extracted so both the full-reload and prompt-only paths share identical builder configuration.
    /// </summary>
    private static WhisperProcessor BuildProcessor(WhisperFactory factory, string lang, string? prompt, string modelPath)
    {
        // Decode configuration lives in Helpers/WhisperDecodeSettings — one reviewable place that
        // says what we send whisper.cpp and what we deliberately leave at its default. Read here,
        // never written: only the AsrBench harness assigns Active, so it can measure an alternative
        // through THIS path instead of a parallel processor that would measure something the app
        // does not do.
        var decode = Helpers.WhisperDecodeSettings.Active;
        var threads = decode.ResolveThreads();
        var builder = factory.CreateBuilder().WithThreads(threads);
        if (decode.NoContext)
        {
            // Upstream VoiceInk sets this; it stops the previous 30-second window's text becoming
            // this window's prompt, which is how a repetition propagates across a boundary.
            builder = builder.WithNoContext();
        }
        if (decode.Temperature is { } t)
        {
            builder = builder.WithTemperature(t);
        }
        if (!IsAutoLanguage(lang))
        {
            builder = builder.WithLanguage(lang);
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder = builder.WithPrompt(prompt);
        }
        // Full config trace (DEBUG-only, toggle-gated) — the whole effective request whisper.net
        // receives. Unconditional (prompt or not). NOTE: fires on each processor (re)build, not
        // per recording — the processor is cached and reused until language/prompt change. Local
        // inference has no API key or wire request, so there is nothing secret to exclude.
        // The model name rides the ENTRY TITLE, matching every cloud client's
        // "$"{provider} transcription · {model}"" form — the local entry was the only one that
        // didn't say which model produced it (owner, 2026-07-25). Taken from the path parameter,
        // NOT _loadedModelPath: that field is assigned only after the first successful build, so on
        // the initial load it would still be null here (Codex plan review round 2).
        // The hint field is labeled "prompt" — the SAME mechanism and label as the Groq
        // Whisper sibling (its literal wire field name; owner uniformity request
        // 2026-07-26, was "bias prompt"). Keyterm providers keep "keyterms": a
        // discriminative keyterm list is not a prompt, and those labels mirror THEIR
        // wire field names.
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.LocalWhisperTranscription,
            new Helpers.TraceMeta(Provider: "LocalWhisper",
                Model: Path.GetFileNameWithoutExtension(modelPath)),
            $"whisper (local) transcription · {Path.GetFileNameWithoutExtension(modelPath)}",
            ("language", IsAutoLanguage(lang) ? "auto (detect)" : lang),
            ("threads", threads.ToString(global::System.Globalization.CultureInfo.InvariantCulture)),
            // The other two decode knobs ride too — "the whole effective request" was this trace's
            // stated contract, and it stopped being true the day the app began setting them
            // (self-review): a trace used to establish which decode config produced a transcript
            // must not omit the two values that changed it.
            ("noContext", decode.NoContext.ToString()),
            ("temperature", decode.Temperature is { } tt
                ? tt.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                : "(whisper.cpp default)"),
            ("prompt", prompt));
        return builder.Build();
    }

    /// <summary>
    /// Change transcription language. Forces model reload on next transcription.
    /// </summary>
    /// <summary>
    /// TRN-49: one decode to pay the once-per-machine Vulkan shader/pipeline compile OFF the
    /// user's first dictation. Runs under the model lock like any decode — codex's plan round
    /// prohibited an uncancellable warm decode ahead of real work, so the token is wired through
    /// to whisper.cpp's abort path and <see cref="GpuWarmup"/> cancels it at recording admission.
    /// The honest bound (stated in the plan, for the diff reviewers): if admission lands
    /// mid-compile, the real decode waits for the abort and then pays the compile itself —
    /// exactly the pre-TRN-49 status quo; every other case removes the compile from the
    /// dictation entirely. Clip length does not matter to the compile because whisper.cpp pads
    /// every input into fixed 30 s windows, so the encoder pipeline shapes do not vary with it
    /// (the cache-cleared acceptance measurement is what validates this, per the plan).
    ///
    /// <para><b>TRN-50: the caller chooses the samples and gets the TEXT back — with the LANGUAGE
    /// the processor decoded in.</b> With the golden clip bundled the warm decode doubles as the
    /// GPU self-test — the caller judges the returned words (<c>GpuSelfTestVerdict</c>), but only
    /// when that language is auto or English: the processor is built <c>WithLanguage</c> at load,
    /// so a Dutch-pinned one decodes the English clip AS Dutch by construction and a low recall
    /// there proves the pin, never the GPU (Kimi diff r1 Blocker). The language is captured under
    /// the model lock, beside the processor it describes, so a concurrent
    /// <see cref="SetLanguageAsync"/> cannot misreport it. Without a clip the caller passes two
    /// seconds of silence and the decode is the TRN-49 warm-up unchanged. Null means nothing was
    /// decoded (the processor was gone), and the caller must not mark or judge.</para>
    /// </summary>
    /// <param name="onDecodeStarting">TRN-64: invoked under the model lock immediately before the
    /// native decode call — the self-test's watchdog starts its budget here, never at queueing.</param>
    internal async Task<(string Text, string Language)?> WarmUpDecodeAsync(float[] samples, CancellationToken ct, Action? onNoProcessor = null, Action? onDecodeStarting = null)
    {
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_processor is null)
            {
                // Model unloaded between queue and run (SetLanguageAsync nulls it mid-session):
                // NOTHING was warmed, and the caller must not mark — a false mark sticks for the
                // whole app version and re-exposes the first-dictation compile (kimi diff r1 A1).
                // The caller's latch-release runs HERE, while this lock is still held: the sole
                // re-queue caller (a reload's post-load QueueWhisperWarmup) also serializes on
                // this lock, so a clear after release could land AFTER the reload's queue check
                // observed the stale latch — and nothing would ever re-queue (codex verify r2).
                onNoProcessor?.Invoke();
                return null;
            }
            var language = _language; // the language THIS processor was built with (same lock)
            onDecodeStarting?.Invoke();
            var text = new global::System.Text.StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }
            return (text.ToString(), language);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>TRN-64: wait for the loaded model's GPU self-test verdict (bounded) and refuse a
    /// decode the verdict forbids. Returns the model name that was gated, or null when nothing
    /// applies (no catalog name, not GPU-engaged, no verdict pending or recorded).
    ///
    /// <para><b>The name is read WITHOUT the model lock</b> (self-review, concurrency lens): the
    /// self-test holds that lock while it decodes, and a wedged native decode never releases it —
    /// taking the lock here parked the decode behind the very hang whose <c>Inconclusive</c> this
    /// gate exists to refuse. The value is load-bearing only at the re-validation the caller
    /// performs once it holds the lock.</para>
    ///
    /// <para><b>A test that is OWED and not running is queued here</b> (self-review, fail-open
    /// lens): the load that should have queued it may have been cancelled — a selection or a
    /// reload, nothing recorded — and a decode must never run on a GPU nothing checked. A run
    /// CANCELLED under this wait refuses as <see cref="GpuSelfTestRefusedException.Reason.Unstable"/>
    /// and records nothing: the retry's own load re-offers the test. The bound is the self-test's
    /// budget plus 30 s of slack for the watchdog to land — if even that passes with no verdict,
    /// this decode is refused as inconclusive on its own (session-local; nothing persisted).</para></summary>
    private async Task<string?> AwaitGpuSelfTestGateAsync(CancellationToken ct)
    {
        var model = _loadedModelName; // volatile read; never the lock (see the summary)
        if (model is null)
        {
            return null;
        }

        var pending = _warmup.WhisperVerdictTask(model);
        if (pending is null && _warmup.WhisperSelfTestOwed(model))
        {
            Logger.Information("Whisper GPU self-test for {Model} is owed and not running - queued by the decode that needs it (TRN-64)", model);
            // The queue hands back the run's OWN verdict task. Re-reading WhisperVerdictTask here
            // lost a run that had already ended — a no-processor run ends in microseconds — and
            // read "nothing pending" as "nothing to wait for" (the gate-queues row flaked there).
            pending = _warmup.QueueWhisperWarmupCore(this, model, processorRebuilt: false);
        }
        if (pending is not null)
        {
            // TRN-64 PR 2 (Kimi diff r1 B1): the run continues into the SPEED-FLOOR phase after the
            // compile decode — its own deadline, up to PhaseBudget more — and this decode rides
            // inside it, so the ceiling must span BOTH. Sized for PR 1's run shape it expired at
            // 150 s on exactly the slow hardware the floor exists to catch: the decode was refused
            // Inconclusive instead of proceeding on the Slower verdict the design promises it.
            // The override stands in for each term as it does in the run (GpuWarmup's floorBudget).
            var ceiling = (_warmup.WhisperSelfTestBudgetOverride ?? GpuWarmup.WhisperSelfTestBudget)
                + (_warmup.WhisperSelfTestBudgetOverride ?? GpuSpeedFloor.PhaseBudget(_warmup.WhisperSpeedFloor))
                + TimeSpan.FromSeconds(30);
            try
            {
                await pending.WaitAsync(ceiling, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warning("Whisper decode refused: no GPU self-test verdict within {Seconds:F0}s and the watchdog did not land (TRN-64)", ceiling.TotalSeconds);
                throw new GpuSelfTestRefusedException(GpuSelfTestRefusedException.Reason.Inconclusive, WhisperBackendLog.ObservedGpuName);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The RUN was cancelled (a selection, a reload, shutdown), not this decode: nothing
                // was learned about the GPU, so nothing is recorded and nothing proceeds on it.
                Logger.Warning("Whisper decode refused: the GPU self-test for {Model} was cancelled under it - nothing learned, nothing recorded (TRN-64)", model);
                throw new GpuSelfTestRefusedException(GpuSelfTestRefusedException.Reason.Unstable, null);
            }
        }

        var outcome = _warmup.WhisperGateOutcome(model);
        if (GpuWarmupMarker.RefusesDecode(outcome)) // Fail | Inconclusive — never Slower, whose text is right
        {
            var kind = outcome == GpuSelfTestOutcome.Fail
                ? GpuSelfTestRefusedException.Reason.Fail
                : GpuSelfTestRefusedException.Reason.Inconclusive;
            Logger.Warning("Whisper decode refused: the GPU self-test for {Model} is {Outcome} on this process (TRN-64)", model, outcome);
            throw new GpuSelfTestRefusedException(kind, WhisperBackendLog.ObservedGpuName);
        }
        return model;
    }

    public async Task SetLanguageAsync(string language)
    {
        await _modelLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var normalized = NormalizeLanguage(language);
            if (_language == normalized) return;
            _language = normalized;
            // Force reload next time — dispose processor first, then factory
            if (_processor != null)
            {
                await _processor.DisposeAsync().ConfigureAwait(false);
                _processor = null;
            }
            _factory?.Dispose();
            _factory = null;
            _loadedModelPath = null;
            _loadedModelName = null; // Kimi diff r1 A1: the next load re-passes the catalog name
            _prompt = null;
            Logger.Information("Language changed to: {Lang}", language);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Transcribe a WAV file to text.
    /// </summary>
    public async Task<string> TranscribeAsync(string wavFilePath, string? language = null, CancellationToken ct = default)
    {
        return await TranscribeAsync(wavFilePath, language, hints: null, diarize: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Transcribe a WAV file. <paramref name="hints"/> is accepted and IGNORED — this
    /// service reports <see cref="HintTransportKind.None"/> since TRN-11; the initial
    /// prompt is always empty. The parameter stays because the interface defines it for
    /// every transport.
    /// </summary>
    public async Task<string> TranscribeAsync(string wavFilePath, string? language, Models.TranscriptionHints? hints, bool diarize = false, CancellationToken ct = default)
    {
        // TRN-64: the GPU self-test gate, BEFORE the lock (the test itself holds the lock while it
        // decodes; awaiting it under the lock would deadlock — Codex r1 C4). Generation-safe: the
        // model is read without the lock (volatile), its verdict awaited without it, and re-validated once
        // the lock is held — a concurrent load that swapped the model in between gets one more
        // gate; a second swap refuses as Unstable, which records nothing (Codex r2 F2, Kimi r2 C2).
        var gatedModel = await AwaitGpuSelfTestGateAsync(ct).ConfigureAwait(false);
        // Hold the model lock for the entire transcription to prevent SetLanguageAsync
        // or LoadModelAsync from disposing _processor mid-transcription.
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        if (gatedModel is not null && !string.Equals(_loadedModelName, gatedModel, StringComparison.OrdinalIgnoreCase))
        {
            _modelLock.Release();
            gatedModel = await AwaitGpuSelfTestGateAsync(ct).ConfigureAwait(false);
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            if (gatedModel is not null && !string.Equals(_loadedModelName, gatedModel, StringComparison.OrdinalIgnoreCase))
            {
                _modelLock.Release();
                Logger.Warning("Whisper decode refused: the loaded model changed twice while its GPU self-test was awaited (TRN-64)");
                throw new GpuSelfTestRefusedException(GpuSelfTestRefusedException.Reason.Unstable, null);
            }
        }
        try
        {
            var requestedLanguage = NormalizeLanguage(language ?? _language);
            // TRN-11 (2026-08-11): the initial prompt is ALWAYS empty — `hints` is accepted
            // and ignored, matching HintTransport == None below.
            //
            // whisper.cpp's `initial_prompt` is the same mechanism measured to destroy
            // transcripts on the Groq path (evidence on GroqClient.HintTransport), reached
            // through a different binding. Originally fixed by MECHANISM alone; a LOCAL A/B
            // now exists and lands on the same side (TRN-25, 2026-08-23): a one-sentence
            // language-anchor prompt regressed the owner ground-truth corpus 15.04% -> 17.05%
            // no-filler WER and made the no-speech recording hallucinate 8 words where the
            // unprompted decode emitted nothing. The risk shape was never in doubt — an echo here corrupts the
            // recognizer's own output, where the user cannot tell invented words from
            // dictated ones, and no vocabulary benefit is worth that. Deliberately routed
            // through NormalizePrompt rather than a bare null, so the value is identical to
            // what empty hints always produced and the reload comparison below cannot
            // oscillate between two spellings of "no prompt".
            var requestedPrompt = NormalizePrompt(null);
            if (_loadedModelPath != null && (_language != requestedLanguage || _prompt != requestedPrompt))
            {
                // Already hold the lock — call the inner implementation directly
                await LoadModelCoreAsync(_loadedModelPath, requestedLanguage, requestedPrompt, ct).ConfigureAwait(false);
            }

            if (_processor == null)
            {
                throw new InvalidOperationException("No Whisper model loaded. Call LoadModelAsync first.");
            }

            Logger.Information("Transcribing: {Path}", wavFilePath);

            // Read WAV and convert to float[]
            var samples = await Task.Run(() => ReadWavAsFloat(wavFilePath), ct).ConfigureAwait(false);

            if (samples.Length == 0)
            {
                Logger.Warning("Empty audio file: {Path}", wavFilePath);
                return string.Empty;
            }

            // Process through Whisper
            var segments = new List<string>();
            var noSpeechProbabilities = new List<float>();
            string? detectedLanguage = null;
            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                segments.Add(segment.Text);
                noSpeechProbabilities.Add(segment.NoSpeechProbability);
                detectedLanguage ??= segment.Language;
            }

            var result = string.Join(" ", segments).Trim();
            Logger.Information("Transcription complete: {Length} chars", result.Length);
            // Observability only (numbers, never text): real-world data for a future
            // decoder-side no-speech corroboration decision. Deliberately NOT a gate —
            // confident hallucinations carry deceptively low values (2026-07-23 research).
            var noSpeechStats = Helpers.WhisperSegmentStats.Format(noSpeechProbabilities);
            if (noSpeechStats != null)
            {
                Logger.Information("Local no-speech probability: {Stats}", noSpeechStats);
            }
            // Detected-language observability (owner request 2026-07-26), auto mode only —
            // with a pinned language the segment value just echoes the request, and
            // labeling it "detected" would mislead (same rule as the cloud clients).
            if (IsAutoLanguage(requestedLanguage) && !string.IsNullOrEmpty(detectedLanguage))
            {
                Logger.Information("Local Whisper detected language: {DetectedLanguage}", detectedLanguage);
                Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.DetectedLanguage,
                    new Helpers.TraceMeta(Provider: "LocalWhisper",
                        Model: _loadedModelPath is null ? null : Path.GetFileNameWithoutExtension(_loadedModelPath),
                        Language: detectedLanguage),
                    $"whisper (local) detected language · {(_loadedModelPath is null ? "unknown" : Path.GetFileNameWithoutExtension(_loadedModelPath))}",
                    detectedLanguage);
            }
            return result;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Read a 16kHz mono 16-bit PCM WAV file and convert to float[] in [-1.0, 1.0].
    /// Read WAV audio samples — stride from byte 44, Int16 to Float.
    /// </summary>
    private static float[] ReadWavAsFloat(string filePath)
    {
        using var reader = new WaveFileReader(filePath);
        var format = reader.WaveFormat;

        Logger.Debug("WAV format: {Rate}Hz, {Channels}ch, {Bits}bit, {Encoding}",
            format.SampleRate, format.Channels, format.BitsPerSample, format.Encoding);

        // Read all bytes
        var bytes = new byte[reader.Length];
        int bytesRead = reader.Read(bytes, 0, bytes.Length);

        // Convert based on format
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sampleCount = bytesRead / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                var shortVal = BitConverter.ToInt16(bytes, i * 2);
                samples[i] = Math.Clamp(shortVal / 32767f, -1f, 1f);
            }

            // If stereo, downmix to mono
            if (format.Channels > 1)
            {
                return DownmixToMono(samples, format.Channels);
            }

            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var sampleCount = bytesRead / 4;
            var samples = new float[sampleCount];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytesRead);

            if (format.Channels > 1)
            {
                return DownmixToMono(samples, format.Channels);
            }

            return samples;
        }

        throw new NotSupportedException(
            $"Unsupported WAV format: {format.Encoding}, {format.BitsPerSample}-bit");
    }

    private static float[] DownmixToMono(float[] samples, int channels)
    {
        var frameCount = samples.Length / channels;
        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += samples[i * channels + ch];
            }
            mono[i] = sum / channels;
        }
        return mono;
    }

    public bool IsModelLoaded => _processor != null;
    public bool IsReady => _processor != null;

    // TRN-11: NO hints — the initial prompt is always empty (see TranscribeAsync).
    // GenerativeModelId is deliberately not overridden: the interface default is null,
    // which is what the contract specifies for a non-generative transport, and nothing
    // needs the loaded ggml path once no prompt is budgeted against a tokenizer.
    public HintTransportKind HintTransport => HintTransportKind.None;

    private static string NormalizeLanguage(string? language)
    {
        return string.IsNullOrWhiteSpace(language) || language.Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase)
            ? AutoLanguage
            : language.Trim();
    }

    private static string? NormalizePrompt(string? prompt)
    {
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
    }

    private static bool IsAutoLanguage(string language)
        => language.Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        // Wait (bounded) for the model lock so we cannot dispose the processor or factory while a
        // transcription is in progress (TranscribeAsync holds the lock for its full duration).
        if (!await _modelLock.WaitAsync(DisposeLockTimeout).ConfigureAwait(false))
        {
            // A transcription is still running past the timeout. Disposing the native processor /
            // factory concurrently with in-flight native inference can crash the process, so skip
            // it: Dispose only runs at app shutdown, and process exit reclaims the native memory.
            Logger.Warning("WhisperTranscriptionService.DisposeAsync timed out after {Timeout}s waiting for an " +
                "in-flight model operation; skipping native disposal (process exit reclaims it)", DisposeLockTimeout.TotalSeconds);
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
            _loadedModelPath = null;
            _loadedModelName = null; // Kimi diff r1 A1
            _prompt = null;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void Dispose()
    {
        // Bounded wait — mirrors DisposeAsync. Dispose runs from DI-container teardown at app exit;
        // an UNBOUNDED _modelLock.Wait() blocks the shutdown thread indefinitely on a long or hung
        // native inference, hanging the whole app. On timeout we deliberately skip native disposal
        // (touching it mid-inference can crash) and let process exit reclaim the native memory.
        if (!_modelLock.Wait(DisposeLockTimeout))
        {
            Logger.Warning("WhisperTranscriptionService.Dispose timed out after {Timeout}s waiting for an " +
                "in-flight model operation; skipping native disposal (process exit reclaims it)", DisposeLockTimeout.TotalSeconds);
            return;
        }
        try
        {
            _processor?.Dispose();
            _processor = null;
            _factory?.Dispose();
            _factory = null;
            _loadedModelPath = null;
            _loadedModelName = null; // Kimi diff r1 A1
            _prompt = null;
        }
        finally
        {
            _modelLock.Release();
        }
    }
}
