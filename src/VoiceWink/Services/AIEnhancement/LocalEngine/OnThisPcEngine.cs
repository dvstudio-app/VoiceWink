using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.Clients;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-4: the "On this PC" provider's engine host — the one owner of the bundled llama-server's
/// lifecycle in the app (LAI-2 built the runtime with no caller). Design plan §5.2/§5.3, revised
/// after its LAI-4 plan round.
/// <list type="bullet">
/// <item><b>No cloud fallback, never silent:</b> every failure throws short pill copy
/// (≤ 26 units) into <c>MainViewModel</c>'s existing enhancement catch, which pastes the raw
/// transcript and marks History.</item>
/// <item><b>First-use check before any dictation (Codex plan B1):</b> the first load of a model on
/// this llama build + display driver runs the GPU self-test in the background; until it settles,
/// a dictation for that model pastes raw at once ("AI enhancement warming up") — even while a healthy
/// child exists, so no dictation reaches a GPU that has not passed.</item>
/// <item><b>The check also picks the faster route:</b> after a Pass the same fixed request is timed
/// once on the GPU and once on the CPU, and the model runs where it was faster on THIS PC
/// (<see cref="LlamaSelfTest.ChooseRoute"/>) - per model, stored with the verdict, never a rule
/// about a brand or an architecture. A model whose route is the CPU is acquired in Cpu mode for
/// its own calls; the session is not moved to the CPU for other models.</item>
/// <item><b>Prepare on recording start</b> (fire-and-forget, never on the recording path), then a
/// prompt-cache warm; <b>idle unload</b> after <see cref="DefaultIdleUnload"/>.</item>
/// <item><b>Erasure closes the engine for the session (Codex plan B2):</b> admission closes, the
/// lifetime token cancels, background work is joined, and the verdict store refuses writes under
/// the same lock, so an erased file is never recreated by a late continuation.</item>
/// </list>
/// </summary>
public sealed class OnThisPcEngine : IDisposable
{
    private static ILogger Logger => Log.ForContext<OnThisPcEngine>();

    // Pill copy: MainViewModel.ComposeFallbackFailureStatus reserves 26 UTF-16 units for a reason.
    internal const string NotInstalledMessage = "AI model not installed";
    internal const string GettingReadyMessage = "AI enhancement warming up";
    internal const string UnavailableMessage = "AI enhancement unavailable";
    internal const string TooLongMessage = "Dictation too long";
    internal const string StoppedMessage = "AI enhancement stopped";

    /// <summary>A resident child idle this long is retired, giving its RAM/VRAM back (the 4B held
    /// +6.6 GB of commit idle on the laptop). 30 minutes since 2026-10-01 (owner; 5 before) — the
    /// Local server provider's Ollama <c>keep_alive</c> window, so a dictation after a coffee break
    /// does not pay the 1.3–7 s reload; prepare-on-record hides most of a reload either way.</summary>
    internal static readonly TimeSpan DefaultIdleUnload = TimeSpan.FromMinutes(30);

    /// <summary>Tokens the chat template adds around the system and user turns, beyond the texts'
    /// own tokens — the admission check's margin.</summary>
    internal const int TemplateMarginTokens = 64;

    /// <summary>The self-test's own bound (it is one short, fixed request).</summary>
    internal static readonly TimeSpan SelfTestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A first-use check that ends without a verdict (the child was retired under it, the
    /// request timed out, the engine would not start) is tried again; after this many the engine
    /// moves to the CPU for the session instead of serving an unchecked GPU.</summary>
    internal const int MaxInconclusiveWarmups = 2;

    /// <summary>How long erasure waits for background work before retiring the child anyway.</summary>
    internal static readonly TimeSpan ErasureJoinBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long a delete waits for the engine's one user slot. On expiry nothing is
    /// retired and nothing is deleted.</summary>
    internal static readonly TimeSpan DeleteGateWait = TimeSpan.FromSeconds(10);

    private readonly LlamaServerProcess _process;
    private readonly Func<string, string?> _installedModelPath;
    private readonly HttpClient _http;
    private readonly LlamaGpuCheckStore _checks;
    private readonly Func<HttpClient, LlamaServerProcess.LlamaLease, TimeSpan, CancellationToken, Task<(LlamaSelfTestVerdict Verdict, int Facts, TimeSpan Elapsed)>> _selfTest;
    private readonly Func<HttpClient, LlamaServerProcess.LlamaLease, TimeSpan, CancellationToken, Task<LlamaTimedRun>> _timedRun;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _idleUnload;
    private readonly TimeSpan _erasureJoinBudget;
    private readonly TimeSpan _deleteGateWait;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _state = new();
    /// <summary>One user of the child at a time, held from the acquire to the last response byte.
    /// The process hands out a lease and releases its own gate, so without this a first-use check
    /// for a model that just finished downloading would retire the child another model's
    /// dictation is still using (Codex diff r1 B2). The server runs one slot anyway.</summary>
    private readonly SemaphoreSlim _useGate = new(1, 1);
    private readonly Dictionary<string, Task> _warmups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warmupsDone = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _inconclusiveWarmups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LlamaGpuCheckStore.Entry?> _verdicts = new(StringComparer.Ordinal);
    /// <summary>One cancel per running first-use check, so a delete of that model stops the check's
    /// spawn instead of waiting out a health budget.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _warmupCancels = new(StringComparer.Ordinal);
    private bool _unloading;
    private Task? _prepare;
    private bool _closed;
    private int _inFlight;
    private DateTime? _lastUseUtc;
    private Timer? _idleTimer;

    /// <summary>Production: the payload next to the app, the native launcher, the verdict store in
    /// the app folder keyed by this build and display driver.</summary>
    internal OnThisPcEngine(LocalModelStore store, IHttpClientFactory httpFactory, LlamaLaunchMode initialMode)
        : this(
            new LlamaServerProcess(
                LlamaServerPayload.Current.DirectoryFor(AppContext.BaseDirectory),
                new NativeLlamaServerLauncher(),
                LlamaServerProcess.HealthProbe(httpFactory.CreateClient(Http.VoiceWinkHttpClients.LlamaLocal)),
                initialMode,
                payload: LlamaServerPayload.Current),
            store.InstalledModelPath,
            httpFactory.CreateClient(Http.VoiceWinkHttpClients.LlamaLocal),
            new LlamaGpuCheckStore(LlamaGpuCheckStore.DefaultPath, LlamaServerPayload.Current.VerdictBuild, DisplayDriverSignature.Read()))
    {
    }

    /// <summary>Test seam: every collaborator injected; no socket, no child process.</summary>
    internal OnThisPcEngine(
        LlamaServerProcess process,
        Func<string, string?> installedModelPath,
        HttpClient http,
        LlamaGpuCheckStore checks,
        Func<HttpClient, LlamaServerProcess.LlamaLease, TimeSpan, CancellationToken, Task<(LlamaSelfTestVerdict, int, TimeSpan)>>? selfTest = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? idleUnload = null,
        TimeSpan? erasureJoinBudget = null,
        Func<HttpClient, LlamaServerProcess.LlamaLease, TimeSpan, CancellationToken, Task<LlamaTimedRun>>? timedRun = null,
        TimeSpan? deleteGateWait = null)
    {
        _timedRun = timedRun ?? LlamaSelfTest.RunTimedAsync;
        _deleteGateWait = deleteGateWait ?? DeleteGateWait;
        _erasureJoinBudget = erasureJoinBudget ?? ErasureJoinBudget;
        _process = process;
        _installedModelPath = installedModelPath;
        _http = http;
        _checks = checks;
        _selfTest = selfTest ?? LlamaSelfTest.RunAsync;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _idleUnload = idleUnload ?? DefaultIdleUnload;
    }

    internal LlamaServerProcess Process => _process;

    /// <summary>Test seam: how many first-use checks are running right now.</summary>
    internal int WarmupsInFlightForTest
    {
        get
        {
            lock (_state)
            {
                return _warmups.Count;
            }
        }
    }

    /// <summary>The catalog models installed on this PC, in catalog order — the provider's model list.</summary>
    internal IReadOnlyList<string> InstalledModelIds()
        => LocalModelCatalog.All.Where(e => _installedModelPath(e.Id) is not null).Select(e => e.Id).ToList();

    /// <summary>Binds a child to the catalog revision it was spawned for, not the file's path.</summary>
    internal static string IdentityOf(LocalModelEntry entry)
        => entry.Id + ":" + entry.Model.Sha256[..Math.Min(16, entry.Model.Sha256.Length)];

    /// <summary>True while a first-use check for this model is owed or running.</summary>
    internal bool IsWarmupPending(string modelId)
    {
        var entry = LocalModelCatalog.Find(modelId);
        if (entry is null)
            return false;
        var identity = IdentityOf(entry);
        lock (_state)
        {
            return _warmups.ContainsKey(identity) || IsWarmupOwedLocked(identity);
        }
    }

    /// <summary>The model's speed measured on this PC by its first-use check — the timed run of the
    /// route it uses — or null when nothing was measured (no check yet, no GPU to check, a failed GPU)
    /// or when the session runs on the processor and the measurement was the GPU's.</summary>
    internal int? MeasuredSpeedMs(string modelId)
    {
        var entry = LocalModelCatalog.Find(modelId);
        if (entry is null)
            return null;
        lock (_state)
        {
            var verdict = VerdictLocked(IdentityOf(entry));
            if (_process.LaunchMode == LlamaLaunchMode.Cpu && verdict is { Route: LlamaRoute.Gpu })
                return null;
            return verdict is { Verdict: LlamaSelfTestVerdict.Pass, RouteMs: { } ms } ? ms : null;
        }
    }

    /// <summary>The running first-use check's completion, or null when none is RUNNING (one merely
    /// owed has nothing to wait on — answering a completed task there made the rows rebuild forever).</summary>
    internal Task? WhenFirstUseCheckSettledAsync(string modelId)
    {
        var entry = LocalModelCatalog.Find(modelId);
        if (entry is null)
            return null;
        lock (_state)
        {
            return _warmups.TryGetValue(IdentityOf(entry), out var running) ? running : null;
        }
    }

    // ── Enhancement ─────────────────────────────────────────────────────────

    /// <summary>
    /// One enhancement on the bundled engine: warm-up gate, lease, token budget and admission, then
    /// the shared OpenAI-compatible client against the child's loopback port and per-child key.
    /// </summary>
    internal async Task<string> EnhanceAsync(AIProviderConfig config, string systemPrompt, string userText, CancellationToken ct)
    {
        var entry = LocalModelCatalog.Find(config.ModelName);
        var path = entry is null ? null : _installedModelPath(entry.Id);
        if (entry is null || path is null)
            throw new InvalidOperationException(NotInstalledMessage);
        var identity = IdentityOf(entry);

        LlamaLaunchMode? route;
        lock (_state)
        {
            if (_closed)
                throw new InvalidOperationException(UnavailableMessage);
            if (_deleting.Contains(entry.Id))
                throw new InvalidOperationException(NotInstalledMessage);
            ApplyStoredVerdictLocked(identity);
            if (StartWarmupIfOwedLocked(entry, path, identity) || _warmups.ContainsKey(identity))
            {
                Logger.Information("On this PC: {LocalModelId} is still on its first-use check - the transcription is used unchanged", entry.Id);
                throw new InvalidOperationException(GettingReadyMessage);
            }
            route = RouteModeLocked(identity);
            _inFlight++;
        }

        CancellationTokenSource? linked = null;
        var ownsUseGate = false;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            await _useGate.WaitAsync(linked.Token).ConfigureAwait(false);
            ownsUseGate = true;
            // Asked again under the slot: a delete that ran while this waited has removed the model.
            if (!StillAdmitted(entry.Id))
                throw new InvalidOperationException(NotInstalledMessage);
            var lease = ReadyLeaseUnlessUnloading(identity, route)
                        ?? await _process.TryAcquireAsync(path, identity, linked.Token, route).ConfigureAwait(false);
            if (lease is not { } l)
                throw new InvalidOperationException(UnavailableMessage);
            StartIdleTimer();

            var hints = config.LocalRequest;
            var promptClass = hints?.PromptClass ?? LocalPromptClass.Custom;
            var inputTokens = await TokenizeAsync(l, hints?.SourceText ?? userText, linked.Token).ConfigureAwait(false);
            var promptTokens = await TokenizeAsync(l, systemPrompt, linked.Token).ConfigureAwait(false)
                               + await TokenizeAsync(l, userText, linked.Token).ConfigureAwait(false);
            var cap = LocalPromptClassifier.CapFor(promptClass, inputTokens);
            if (!Fits(promptTokens, cap))
            {
                Logger.Warning("On this PC: refused - {PromptTokens} prompt tokens + a {Cap}-token reply do not fit the {Context}-token context",
                    promptTokens, cap, LlamaServerLaunch.ContextSize);
                throw new InvalidOperationException(TooLongMessage);
            }

            var derived = new AIProviderConfig
            {
                Provider = AIProvider.OnThisPc,
                ModelName = LlamaServerLaunch.ModelAlias,
                ApiKey = l.ApiKey,
                BaseUrl = new Uri(l.BaseUri, "v1").ToString().TrimEnd('/'),
                MaxTokens = cap,
                Temperature = config.Temperature,
            };
            string reply;
            try
            {
                reply = await new OpenAICompatibleClient(_http, derived).EnhanceAsync(systemPrompt, userText, linked.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && !linked.Token.IsCancellationRequested)
            {
                // The child died between the lease and the request; the next dictation respawns
                // (the storm fuse bounds that). The socket error stays on InnerException.
                throw new HttpRequestException(StoppedMessage, ex);
            }
            _process.NotifyServed(l.Generation);
            return reply;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(UnavailableMessage);
        }
        finally
        {
            if (ownsUseGate)
                _useGate.Release();
            linked?.Dispose();
            lock (_state)
            {
                _inFlight--;
                _lastUseUtc = _utcNow();
            }
        }
    }

    /// <summary>The lock-free ready lease - skipped while an idle unload is retiring the resident,
    /// which stays readable until its kill is confirmed; the gated acquire then waits the retire out.</summary>
    private LlamaServerProcess.LlamaLease? ReadyLeaseUnlessUnloading(string identity, LlamaLaunchMode? route)
    {
        lock (_state)
        {
            if (_unloading)
                return null;
        }
        return _process.TryGetReadyLease(identity, route);
    }

    /// <summary>Asked after taking <see cref="_useGate"/>, before any acquire: the engine is open,
    /// the model is not being deleted and its file is still installed. A delete holds the slot
    /// through its retire and its file removal, so whoever gets the slot afterwards must look
    /// again - or it would load, and map, a model the user was just told is gone.</summary>
    private bool StillAdmitted(string modelId)
    {
        lock (_state)
        {
            if (_closed || _deleting.Contains(modelId))
                return false;
        }
        return _installedModelPath(modelId) is not null;
    }

    /// <summary>The mode this model's own calls run in: Cpu when its check found the CPU faster,
    /// otherwise the process's. Never the session latch.</summary>
    private LlamaLaunchMode? RouteModeLocked(string identity)
        => VerdictLocked(identity) is { Verdict: LlamaSelfTestVerdict.Pass, Route: LlamaRoute.Cpu } ? LlamaLaunchMode.Cpu : null;

    /// <summary>The admission rule: the rendered prompt plus the reply's cap must fit the context.</summary>
    internal static bool Fits(int promptTokens, int cap)
        => (long)promptTokens + cap + TemplateMarginTokens <= LlamaServerLaunch.ContextSize;

    /// <summary><c>POST /tokenize</c>: the child's own token count for <paramref name="text"/>.</summary>
    private async Task<int> TokenizeAsync(LlamaServerProcess.LlamaLease lease, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.BaseUri, "tokenize"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { content = text }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("On this PC: /tokenize answered HTTP {Status}", (int)response.StatusCode);
                throw new InvalidOperationException(UnavailableMessage);
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                return tokens.GetArrayLength();
            Logger.Warning("On this PC: /tokenize answered without a tokens array");
            throw new InvalidOperationException(UnavailableMessage);
        }
        catch (JsonException)
        {
            Logger.Warning("On this PC: /tokenize answered with malformed JSON");
            throw new InvalidOperationException(UnavailableMessage);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(StoppedMessage, ex);
        }
    }

    // ── Prepare, first-use check, cache warm ─────────────────────────────────

    /// <summary>
    /// Recording started: load <paramref name="modelId"/> while the user speaks, then prefill
    /// <paramref name="warmSystemPrompt"/> so the dictation pays only for its own words. Never
    /// throws, never blocks the caller, single-flight; a model owed its first-use check starts that
    /// check instead.
    /// </summary>
    internal void Prepare(string modelId, string? warmSystemPrompt)
    {
        try
        {
            var entry = LocalModelCatalog.Find(modelId);
            var path = entry is null ? null : _installedModelPath(entry.Id);
            if (entry is null || path is null)
                return;
            var identity = IdentityOf(entry);
            lock (_state)
            {
                if (_closed || _deleting.Contains(entry.Id))
                    return;
                ApplyStoredVerdictLocked(identity);
                if (StartWarmupIfOwedLocked(entry, path, identity) || _warmups.ContainsKey(identity))
                    return;
                if (_prepare is { IsCompleted: false })
                    return;
                var route = RouteModeLocked(identity);
                _prepare = Task.Run(() => PrepareCoreAsync(entry.Id, path, identity, route, warmSystemPrompt));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("On this PC: prepare not started: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task PrepareCoreAsync(string modelId, string path, string identity, LlamaLaunchMode? route, string? warmSystemPrompt)
    {
        var ownsUseGate = false;
        try
        {
            await _useGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            ownsUseGate = true;
            if (!StillAdmitted(modelId))
                return;
            var lease = ReadyLeaseUnlessUnloading(identity, route)
                        ?? await _process.TryAcquireAsync(path, identity, _lifetime.Token, route).ConfigureAwait(false);
            if (lease is not { } l)
                return;
            StartIdleTimer();
            lock (_state)
            {
                _lastUseUtc = _utcNow();
            }
            if (!string.IsNullOrEmpty(warmSystemPrompt))
            {
                await WarmPromptCacheAsync(l, warmSystemPrompt, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug("On this PC: prepare failed: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            if (ownsUseGate)
                _useGate.Release();
        }
    }

    /// <summary>One request with the system message and a 1-token reply, so llama-server's prompt
    /// cache holds the ~1,000-token envelope before the dictation arrives.</summary>
    private async Task WarmPromptCacheAsync(LlamaServerProcess.LlamaLease lease, string systemPrompt, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = LlamaServerLaunch.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = "" },
            },
            max_tokens = 1,
            temperature = 0,
            stream = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.BaseUri, "v1/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.ApiKey);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            _process.NotifyServed(lease.Generation);
            Logger.Debug("On this PC: prompt cache warmed");
        }
    }

    /// <summary>After a model download: run its first-use check now rather than on the first dictation.</summary>
    internal void StartFirstUseCheck(string modelId)
    {
        var entry = LocalModelCatalog.Find(modelId);
        var path = entry is null ? null : _installedModelPath(entry.Id);
        if (entry is null || path is null)
            return;
        lock (_state)
        {
            if (_closed || _deleting.Contains(entry.Id))
                return;
            ApplyStoredVerdictLocked(IdentityOf(entry));
            StartWarmupIfOwedLocked(entry, path, IdentityOf(entry));
        }
    }

    /// <summary>A stored Fail for this model moves the engine to the CPU before its first acquire.</summary>
    private void ApplyStoredVerdictLocked(string identity)
    {
        if (_process.LaunchMode == LlamaLaunchMode.Cpu)
            return;
        if (VerdictLocked(identity)?.Verdict == LlamaSelfTestVerdict.Fail)
            _process.RequestCpu("this model failed its GPU check earlier");
    }

    /// <summary>The model's verdict: the store's, read once per session (the file read can retry
    /// for 150 ms, and this runs on the dictation path under the lock), then this session's own.</summary>
    private LlamaGpuCheckStore.Entry? VerdictLocked(string identity)
    {
        if (!_verdicts.TryGetValue(identity, out var verdict))
        {
            verdict = _checks.Get(identity);
            _verdicts[identity] = verdict;
        }
        return verdict;
    }

    /// <summary>Owed: GPU mode, no verdict for this build + driver, not settled this session.</summary>
    private bool IsWarmupOwedLocked(string identity)
        => _process.LaunchMode == LlamaLaunchMode.Auto
           && !_warmupsDone.Contains(identity)
           && VerdictLocked(identity) is null;

    /// <summary>Starts the first-use check when owed. The flag is published BEFORE the acquire and
    /// cleared only after the self-test's CPU decision, so no dictation can reach the GPU between.</summary>
    private bool StartWarmupIfOwedLocked(LocalModelEntry entry, string path, string identity)
    {
        if (_warmups.ContainsKey(identity) || !IsWarmupOwedLocked(identity))
            return false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _warmups[identity] = gate.Task;
        _warmupCancels[identity] = cancel;
        _ = Task.Run(async () =>
        {
            var outcome = WarmupOutcome.Inconclusive;
            try
            {
                outcome = await RunWarmupAsync(entry, path, identity, cancel.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_state)
                {
                    // Only a settled check opens the gate. An inconclusive one is owed again; after
                    // MaxInconclusiveWarmups the engine moves to the CPU for this session rather
                    // than serve a GPU nothing has checked (nothing is stored: the next session
                    // tests again). An ABANDONED one (the model was deleted under it) counts as
                    // neither: it says nothing about the GPU.
                    if (outcome == WarmupOutcome.Settled)
                    {
                        _warmupsDone.Add(identity);
                    }
                    else if (outcome == WarmupOutcome.Inconclusive && !_closed)
                    {
                        _inconclusiveWarmups.TryGetValue(identity, out var attempts);
                        _inconclusiveWarmups[identity] = ++attempts;
                        if (attempts >= MaxInconclusiveWarmups)
                        {
                            _process.RequestCpu("the GPU check did not complete");
                            _warmupsDone.Add(identity);
                        }
                    }
                    _warmups.Remove(identity);
                    _warmupCancels.Remove(identity);
                }
                cancel.Dispose();
                gate.TrySetResult();
            }
        });
        Logger.Information("On this PC: first-use check started for {LocalModelId}", entry.Id);
        return true;
    }

    private enum WarmupOutcome
    {
        /// <summary>A verdict exists (or there was nothing to check): the gate opens.</summary>
        Settled,
        /// <summary>No verdict: owed again, counted toward <see cref="MaxInconclusiveWarmups"/>.</summary>
        Inconclusive,
        /// <summary>The model was deleted under the check: owed again if it returns, not counted.</summary>
        Abandoned,
    }

    /// <summary>
    /// The first-use check. Settled: a Fail, a Pass with its route chosen, or a child with no GPU
    /// evidence (nothing to check). Inconclusive: no lease, an <c>Unknown</c> verdict, a GPU timed
    /// run that did not complete, an engine shutdown, an exception.
    /// <para>After a Pass the same request is timed on the GPU (its second request there, so the
    /// backend's first-run compilation is already paid), then on a CPU child with the GPU's time as
    /// its deadline. <see cref="LlamaSelfTest.ChooseRoute"/> decides; the winning route's child is
    /// left loaded. The slot (<see cref="_useGate"/>) is held throughout.</para>
    /// </summary>
    private async Task<WarmupOutcome> RunWarmupAsync(LocalModelEntry entry, string path, string identity, CancellationToken ct)
    {
        var ownsUseGate = false;
        try
        {
            await _useGate.WaitAsync(ct).ConfigureAwait(false);
            ownsUseGate = true;
            if (!StillAdmitted(entry.Id))
                return WarmupOutcome.Abandoned;
            var lease = await _process.TryAcquireAsync(path, identity, ct).ConfigureAwait(false);
            if (lease is not { } l)
            {
                Logger.Warning("On this PC: first-use check for {LocalModelId} could not start the engine", entry.Id);
                return WarmupOutcome.Inconclusive;
            }
            StartIdleTimer();
            lock (_state)
            {
                _lastUseUtc = _utcNow();
            }
            if (!l.OnGpu)
            {
                Logger.Information("On this PC: {LocalModelId} loaded without GPU evidence - no GPU check to run", entry.Id);
                return WarmupOutcome.Settled;
            }
            var (verdict, facts, elapsed) = await _selfTest(_http, l, SelfTestTimeout, ct).ConfigureAwait(false);
            if (verdict == LlamaSelfTestVerdict.Unknown)
            {
                Logger.Warning("On this PC: GPU check for {LocalModelId} did not complete", entry.Id);
                return WarmupOutcome.Inconclusive;
            }
            var adapter = _process.ObservedFor(l.Generation)?.DeviceName;
            if (verdict == LlamaSelfTestVerdict.Fail)
                return SettleFail(entry, identity, adapter, facts);

            _process.NotifyServed(l.Generation);
            Logger.Information("On this PC: GPU check passed for {LocalModelId} ({Facts}/{Total} facts, {Ms:F0} ms)",
                entry.Id, facts, LlamaSelfTest.FactGroups.Count, elapsed.TotalMilliseconds);

            // ── Which route is faster here? ──
            var gpu = await _timedRun(_http, l, SelfTestTimeout, ct).ConfigureAwait(false);
            if (!gpu.Completed)
            {
                Logger.Warning("On this PC: the timed GPU run for {LocalModelId} did not complete", entry.Id);
                return WarmupOutcome.Inconclusive;
            }
            if (!gpu.Correct)
                return SettleFail(entry, identity, adapter, facts: 0);   // the GPU's second reply was wrong: not a GPU to use

            if (!StillAdmitted(entry.Id))
                return WarmupOutcome.Abandoned;
            LlamaTimedRun? cpu = null;
            var cpuLease = await _process.TryAcquireAsync(path, identity, ct, LlamaLaunchMode.Cpu).ConfigureAwait(false);
            if (cpuLease is { } c)
            {
                // The deadline IS the GPU's time: a CPU that has not answered by then has lost.
                cpu = await _timedRun(_http, c, gpu.Elapsed, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            var route = LlamaSelfTest.ChooseRoute(gpu.Elapsed, cpu);
            lock (_state)
            {
                // Erasure closes this under the same lock: a verdict that settles after the file was
                // deleted is dropped, never written back (Codex plan B2).
                var gpuMs = (int)gpu.Elapsed.TotalMilliseconds;
                int? cpuMs = cpu is { Completed: true } done ? (int)done.Elapsed.TotalMilliseconds : null;
                var entryNow = new LlamaGpuCheckStore.Entry(LlamaSelfTestVerdict.Pass, adapter, route,
                    route == LlamaRoute.Cpu ? cpuMs : gpuMs);
                if (!_closed)
                {
                    _checks.Record(identity, LlamaSelfTestVerdict.Pass, adapter, route, gpuMs, cpuMs);
                }
                _verdicts[identity] = entryNow;
            }
            Logger.Information("On this PC: {LocalModelId} runs on the {Route} here (GPU {GpuMs:F0} ms, CPU {Cpu})",
                entry.Id, route == LlamaRoute.Cpu ? "processor" : "graphics card", gpu.Elapsed.TotalMilliseconds,
                cpu is null ? "did not start"
                : !cpu.Value.Completed ? "not done within the GPU's time"
                : !cpu.Value.Correct ? "wrong text"
                : $"{cpu.Value.Elapsed.TotalMilliseconds:F0} ms");

            if (route == LlamaRoute.Cpu)
            {
                _process.NotifyServed(cpuLease!.Value.Generation);
            }
            else if (StillAdmitted(entry.Id))
            {
                // Leave the winner loaded, so the first dictation does not pay the reload. Best
                // effort: the verdict is stored either way.
                _ = await _process.TryAcquireAsync(path, identity, ct).ConfigureAwait(false);
            }
            return WarmupOutcome.Settled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return _lifetime.IsCancellationRequested ? WarmupOutcome.Inconclusive : WarmupOutcome.Abandoned;
        }
        catch (Exception ex)
        {
            Logger.Warning("On this PC: first-use check for {LocalModelId} failed: {ErrorType}", entry.Id, ex.GetType().Name);
            return WarmupOutcome.Inconclusive;
        }
        finally
        {
            if (ownsUseGate)
                _useGate.Release();
        }
    }

    /// <summary>A GPU that returned wrong text: stored, and the engine runs on the CPU from now on.</summary>
    private WarmupOutcome SettleFail(LocalModelEntry entry, string identity, string? adapter, int facts)
    {
        bool persisted;
        lock (_state)
        {
            persisted = !_closed && _checks.Record(identity, LlamaSelfTestVerdict.Fail, adapter);
            _verdicts[identity] = new LlamaGpuCheckStore.Entry(LlamaSelfTestVerdict.Fail, adapter);
        }
        _process.RequestCpu("the GPU check failed");
        Logger.Write(GpuSelfTestReport.LevelFor(persisted),
            "On this PC: GPU check FAILED for {LocalModelId} on {Adapter} ({Facts}/{Total} facts) - the engine runs on the CPU from now on",
            entry.Id, adapter ?? "unknown adapter", facts, LlamaSelfTest.FactGroups.Count);
        return WarmupOutcome.Settled;
    }

    // ── Idle unload ─────────────────────────────────────────────────────────

    private void StartIdleTimer()
    {
        lock (_state)
        {
            if (_closed || _idleTimer is not null)
                return;
            var period = TimeSpan.FromSeconds(30);
            _idleTimer = new Timer(_ => _ = CheckIdleAsync(), null, period, period);
        }
    }

    /// <summary>Retires a resident child idle for longer than the unload period with nothing in flight.</summary>
    internal async Task<bool> CheckIdleAsync()
    {
        lock (_state)
        {
            if (_closed || _inFlight > 0 || _warmups.Count > 0 || _prepare is { IsCompleted: false })
                return false;
            if (_lastUseUtc is not { } last || _utcNow() - last < _idleUnload)
                return false;
            _lastUseUtc = null;
            _unloading = true;   // from here a dictation takes the gated acquire, never the dying child
        }
        var retired = false;
        try
        {
            // A dictation or prepare that arrived after the check above has already counted itself
            // (_inFlight / _prepare) before it reaches the process gate; asked again under that
            // gate, the unload stands down instead of killing the child it was just handed.
            retired = await _process.TryRetireResidentAsync(TimeSpan.FromSeconds(1), onlyIf: () =>
            {
                lock (_state)
                {
                    return _inFlight == 0 && _warmups.Count == 0 && _prepare is not { IsCompleted: false };
                }
            }).ConfigureAwait(false);
            if (retired)
                Logger.Information("On this PC: engine unloaded after {Minutes:F0} min idle", _idleUnload.TotalMinutes);
            return retired;
        }
        finally
        {
            lock (_state)
            {
                _unloading = false;
                // Not unloaded (gate busy, or work arrived): the idle clock restarts, so the
                // timer tries again instead of never unloading this child.
                if (!retired && !_closed)
                    _lastUseUtc ??= _utcNow();
            }
        }
    }

    // ── Delete, erasure, exit ───────────────────────────────────────────────

    /// <summary>
    /// Deletes a model with admission for it closed across the whole operation: the child is
    /// retired first (Windows refuses to delete a memory-mapped file), no prepare or dictation can
    /// map it again in between, then <paramref name="delete"/> runs. False when the child could not
    /// be retired — nothing is deleted then.
    /// </summary>
    internal async Task<bool> DeleteModelAsync(string modelId, Func<bool> delete)
    {
        lock (_state)
        {
            if (!_deleting.Add(modelId))
                return false;
        }
        var ownsUseGate = false;
        try
        {
            // Only a child serving THIS model is stopped - deleting one model never fails another
            // model's dictation.
            var entry = LocalModelCatalog.Find(modelId);
            // A dictation already running finishes first: it holds the slot this waits for.
            // The delete takes the engine's one user slot and keeps it through the retire AND the
            // file removal (Codex plan round, LAI-11 B1): a first-use check between two of its
            // own acquires, a queued prepare or a queued dictation would otherwise load - and map
            // - the model again between "the child is stopped" and "the file is gone". This
            // model's running check is cancelled first so its spawn does not hold the slot for a
            // health budget; whoever gets the slot afterwards asks StillAdmitted again.
            if (entry is not null)
            {
                lock (_state)
                {
                    if (_warmupCancels.TryGetValue(IdentityOf(entry), out var cancel))
                        cancel.Cancel();
                }
            }
            if (!await _useGate.WaitAsync(_deleteGateWait).ConfigureAwait(false))
            {
                Logger.Warning("On this PC: the engine is busy - {LocalModelId} was not deleted", modelId);
                return false;
            }
            ownsUseGate = true;
            if (!await _process.TryRetireResidentAsync(TimeSpan.FromSeconds(5),
                    entry is null ? null : IdentityOf(entry)).ConfigureAwait(false))
            {
                Logger.Warning("On this PC: could not stop the engine before deleting {LocalModelId}", modelId);
                return false;
            }
            return delete();
        }
        finally
        {
            if (ownsUseGate)
                _useGate.Release();
            lock (_state)
            {
                _deleting.Remove(modelId);
            }
        }
    }

    /// <summary>
    /// GDPR erasure: close admission for the rest of the session, cancel and join background work,
    /// then retire and confirm the child. True only when everything stopped in time.
    /// </summary>
    internal async Task<bool> TryShutdownForErasureAsync()
    {
        Task[] pending;
        lock (_state)
        {
            _closed = true;
            pending = _warmups.Values.Concat(_prepare is { } p ? [p] : []).ToArray();
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        _lifetime.Cancel();
        var joined = true;
        try
        {
            await Task.WhenAll(pending).WaitAsync(_erasureJoinBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            joined = false;
            Logger.Warning("On this PC: background work did not end within {Seconds:F0}s of erasure - its writes stay fenced", _erasureJoinBudget.TotalSeconds);
        }
        catch (Exception)
        {
            // A faulted task has ended; that is all the join needs.
        }
        var retired = await _process.TryRetireResidentAsync(ParakeetServerPolicy.ShutdownGateWait).ConfigureAwait(false);
        return joined && retired;
    }

    /// <summary>App exit: close, cancel, bounded kill (the job object is the backstop).</summary>
    internal async Task ShutdownAsync()
    {
        lock (_state)
        {
            _closed = true;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        _lifetime.Cancel();
        await _process.ShutdownAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_state)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }
}
