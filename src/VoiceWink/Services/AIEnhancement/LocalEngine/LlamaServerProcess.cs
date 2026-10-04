using System.Diagnostics;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-2: the resident llama-server — one child at a time, spawned behind
/// <see cref="LlamaSpawnGate"/>, health-checked on <c>GET /health</c>, killable by generation. A
/// SIBLING of <see cref="ParakeetServerProcess"/>, not a subclass: that manager carries install
/// identity, adapter pinning and warm-up coupling this engine has none of (llama.cpp prefers a
/// discrete GPU itself). What is shared is reused rather than re-typed — the Win32 launcher, the
/// storm bound and retire wait (<see cref="ParakeetServerPolicy"/>), the provenance rule. The
/// lifecycle rules are Parakeet's, each from one of its review rounds:
/// <list type="bullet">
/// <item>One never-disposed gate serialises every state change; a child whose kill is unconfirmed
/// parks in <c>_retiring</c> and no successor spawns until it is seen dead.</item>
/// <item>Every lease carries a generation; reuse mints a new one, so a stale caller's kill can
/// never reach a successor.</item>
/// <item>An involuntary exit is charged to the storm fuse the first time it is observed; an
/// expected stop (model switch, CPU request, kill, refusal) never is. Past
/// <see cref="ParakeetServerPolicy.MaxCrashesPerWindow"/> in the window the engine is unrunnable
/// until app restart.</item>
/// </list>
/// <para><b>CPU fallback:</b> an <c>Auto</c> child that exits before health, OR that dies after
/// health before any request succeeded (<see cref="NotifyServed"/>), is a driver-shaped death —
/// not charged, one CPU respawn, and Cpu is latched for the session. <see cref="RequestCpu"/> is the
/// non-blocking intent the GPU self-test's FAIL uses.</para>
/// <para><b>Thinking is a contract:</b> the command line turns it off, and a child that states
/// <c>thinking = 1</c> anyway is retired and its model refused for the session — a thinking model
/// blows the dictation deadline and pastes its reasoning.</para>
/// <para>No app caller yet (LAI-4 wires it); <c>tools/llama-server-probe</c> drives it end to end.</para>
/// </summary>
internal sealed class LlamaServerProcess
{
    private static ILogger Logger => Log.ForContext<LlamaServerProcess>();

    /// <summary>Spawn → <c>/health</c> 200. Model load plus llama's load-time warm-up run, which is
    /// where the Vulkan shader compile lands (6–30 s measured on first use); a cold load from a
    /// slow disk on the laptop took up to 30 s through Ollama.</summary>
    internal static readonly TimeSpan DefaultHealthBudget = TimeSpan.FromSeconds(120);

    private readonly ILlamaServerLauncher _launcher;
    private readonly Func<Uri, CancellationToken, Task<bool>> _probe;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _healthBudget;
    private readonly Func<string, AuthenticodeSignature.Verdict>? _verifySignature;
    private readonly Func<string, LlamaSpawnGate.Verdict> _spawnGate;
    private readonly string _exePath;
    private readonly SemaphoreSlim _gate = new(1, 1); // never disposed, deliberately.
    private readonly List<DateTime> _involuntaryExitsUtc = [];

    private Resident? _resident;
    private ILlamaServerChild? _retiring;
    private int _generation;
    private bool _stormTripped;
    private LlamaLaunchMode _launchMode;
    private int _cpuRequested;
    private readonly HashSet<string> _thinkingRefusedModels = new(StringComparer.Ordinal);

    /// <summary>How long a healthy child gets to print its <c>thinking = 0|1</c> line when the drain
    /// has not delivered it yet (llama.cpp logs from a worker thread, so the line can trail
    /// <c>/health</c>). Past this the child is served on the command line's word.</summary>
    internal static readonly TimeSpan ThinkingLineWait = TimeSpan.FromSeconds(1);

    /// <summary>What an acquire hands out: where to send, the key to send, which generation a later
    /// kill may target, and whether this child showed GPU evidence.</summary>
    internal readonly record struct LlamaLease(Uri BaseUri, string ApiKey, int Generation, bool OnGpu);

    /// <param name="payloadDirectory">The llama directory (<see cref="LlamaPayloadSet.DirectoryFor"/>).</param>
    /// <param name="payload">Whose pins the spawn gate checks that directory against; null = the
    /// x64 set. Production passes <see cref="LlamaServerPayload.Current"/> for both.</param>
    /// <param name="probe">One <c>GET /health</c>; true = 200. Injected so tests open no socket.</param>
    /// <param name="spawnGate">Test seam over <see cref="LlamaSpawnGate.Check"/>; production passes null.</param>
    internal LlamaServerProcess(
        string payloadDirectory,
        ILlamaServerLauncher launcher,
        Func<Uri, CancellationToken, Task<bool>> probe,
        LlamaLaunchMode initialLaunchMode = LlamaLaunchMode.Auto,
        Func<DateTime>? utcNow = null,
        TimeSpan? healthBudget = null,
        Func<string, AuthenticodeSignature.Verdict>? verifySignature = null,
        Func<string, LlamaSpawnGate.Verdict>? spawnGate = null,
        LlamaPayloadSet? payload = null)
    {
        var pins = payload ?? LlamaServerPayload.X64;
        _exePath = Path.Combine(payloadDirectory, LlamaServerPayload.ExeName);
        _launcher = launcher;
        _probe = probe;
        _launchMode = initialLaunchMode;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _healthBudget = healthBudget ?? DefaultHealthBudget;
        _verifySignature = verifySignature;
        _spawnGate = spawnGate ?? (dir => LlamaSpawnGate.Check(dir, pins, _verifySignature));
    }

    /// <summary>The production health probe: <c>GET /health</c> answers 200 once the model is
    /// loaded (503 while loading). It needs no key — the one route the server leaves open.</summary>
    internal static Func<Uri, CancellationToken, Task<bool>> HealthProbe(HttpClient http)
        => async (baseUri, ct) =>
        {
            try
            {
                using var response = await http.GetAsync(new Uri(baseUri, "health"), ct).ConfigureAwait(false);
                return response.StatusCode == global::System.Net.HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                return false; // not listening yet
            }
        };

    /// <summary>Advisory, lock-free: the fuse only ever goes false → true.</summary>
    internal bool IsStormTripped => _stormTripped;

    /// <summary>The mode the NEXT child spawns with; a pending CPU request already reads Cpu.</summary>
    internal LlamaLaunchMode LaunchMode
        => Volatile.Read(ref _cpuRequested) != 0 ? LlamaLaunchMode.Cpu : _launchMode;

    /// <summary>The mode one call runs in. The session's Cpu (the GPU toggle off, a failed GPU
    /// check, a GPU child that died) always wins; otherwise the caller's own choice for THIS call,
    /// else Auto. A per-call Cpu never latches anything: it is how one model runs on the processor
    /// because it measured faster there, while another model in the same session keeps the GPU.</summary>
    internal static LlamaLaunchMode EffectiveMode(LlamaLaunchMode sessionMode, LlamaLaunchMode? requested)
        => sessionMode == LlamaLaunchMode.Cpu ? LlamaLaunchMode.Cpu : requested ?? LlamaLaunchMode.Auto;

    internal int InvoluntaryExitCountForTest => _involuntaryExitsUtc.Count;

    /// <summary>Ask for CPU from the next spawn on — monotonic, non-blocking, never takes the gate
    /// (the self-test lands outside it). The next acquire retires a GPU-served child.</summary>
    internal void RequestCpu(string reason)
    {
        if (Interlocked.Exchange(ref _cpuRequested, 1) == 0)
        {
            Logger.Warning("llama-server: CPU requested for the rest of this session - {Reason}; the next acquire retires a GPU-served child",
                LogValueSanitizer.SingleLine(reason));
        }
    }

    /// <summary>The live child's lease for <paramref name="modelIdentity"/> without waiting — null
    /// when there is none, or when a gated acquire would not reuse it: exited, another model, a
    /// pending CPU request against a GPU child (a failed self-test must stop the fast path too), or
    /// a child that reported thinking on. A dictation arriving while a child is still loading must
    /// not wait on the gate. Like a gated reuse it MINTS a generation, so a stale holder's kill can
    /// never reach this caller's child; the resident is re-read after the mint, so a lease never
    /// pairs one child's address and key with a successor's generation.</summary>
    internal LlamaLease? TryGetReadyLease(string modelIdentity, LlamaLaunchMode? mode = null)
    {
        var resident = Volatile.Read(ref _resident);
        if (!IsServable(resident, modelIdentity, mode))
        {
            return null;
        }
        var generation = Interlocked.Increment(ref _generation);
        if (!ReferenceEquals(Volatile.Read(ref _resident), resident) || !IsServable(resident, modelIdentity, mode))
        {
            return null;
        }
        return resident!.LeaseFor(generation);
    }

    /// <summary>The reuse rule <see cref="TryAcquireAsync"/> applies under the gate, read lock-free.</summary>
    private bool IsServable(Resident? resident, string modelIdentity, LlamaLaunchMode? mode)
        => resident is not null
           && !resident.Child.HasExited
           && resident.Mode == EffectiveMode(LaunchMode, mode)
           && resident.Child.ObservedThinking != true
           && string.Equals(resident.ModelIdentity, modelIdentity, StringComparison.Ordinal);

    /// <summary>The first request on this child succeeded: from now on its death is a crash, not
    /// a driver-shaped death that earns the CPU fallback.</summary>
    internal void NotifyServed(int generation)
    {
        var resident = Volatile.Read(ref _resident);
        if (resident is not null && generation >= resident.FirstGeneration && generation <= Volatile.Read(ref _generation))
        {
            resident.Served = true;
        }
    }

    /// <summary>The live child's device evidence for <paramref name="generation"/>, or null when that
    /// generation is not the live one. Lock-free; a torn read can only refuse.</summary>
    internal (string? DeviceToken, string? DeviceName)? ObservedFor(int generation)
    {
        var resident = Volatile.Read(ref _resident);
        if (resident is null || generation < resident.FirstGeneration
            || Volatile.Read(ref _generation) < generation || resident.Child.HasExited)
        {
            return null;
        }
        return resident.Child.ObservedDevice is { } device ? (device.Token, device.Name) : (null, null);
    }

    /// <summary>
    /// A healthy resident server for <paramref name="modelPath"/>, or null when the engine cannot
    /// serve this call (payload refused, storm tripped, launch or health failure, a thinking
    /// model). <paramref name="modelIdentity"/> binds the child to the model INSTALL, not its path:
    /// a re-download lands new bytes at the same path. Never throws for lifecycle reasons;
    /// cancellation propagates. <paramref name="requestedMode"/> is this call's own mode
    /// (<see cref="EffectiveMode"/>): it neither sets nor clears the session latch.
    /// </summary>
    internal async Task<LlamaLease?> TryAcquireAsync(
        string modelPath, string modelIdentity, CancellationToken ct, LlamaLaunchMode? requestedMode = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryClearRetiring())
            {
                Logger.Warning("llama-server: predecessor still exiting - not serving this call");
                return null;
            }

            if (Volatile.Read(ref _cpuRequested) != 0 && _launchMode != LlamaLaunchMode.Cpu)
            {
                _launchMode = LlamaLaunchMode.Cpu;
                Logger.Information("llama-server: CPU request consumed - launch mode is Cpu for the rest of this session");
            }

            if (_resident is { } current)
            {
                var reusable = !current.Child.HasExited
                    && current.Mode == EffectiveMode(_launchMode, requestedMode)
                    && string.Equals(current.ModelIdentity, modelIdentity, StringComparison.Ordinal)
                    && current.Child.ObservedThinking != true;
                if (reusable)
                {
                    return current.LeaseFor(Interlocked.Increment(ref _generation));
                }
                if (current.Child.ObservedThinking == true)
                {
                    // The line arrived after this child was published: refuse the model now, or
                    // every later call would reload it only to retire it again.
                    _thinkingRefusedModels.Add(current.ModelIdentity);
                    Logger.Error("llama-server: the model reports thinking on despite --reasoning off - refused for this session (a thinking model misses the dictation deadline)");
                }
                if (!RetireResident(current, current.Child.ObservedThinking == true ? "thinking on" : "not reusable"))
                {
                    return null;
                }
            }

            if (_thinkingRefusedModels.Contains(modelIdentity))
            {
                return null; // refused once this session with a logged reason; never respawned.
            }

            if (_stormTripped || !ParakeetServerPolicy.MayRespawn(_involuntaryExitsUtc, _utcNow()))
            {
                if (!_stormTripped)
                {
                    _stormTripped = true;
                    Logger.Error("llama-server: crash storm tripped ({Count} involuntary exits in {Window}) - the local engine is unrunnable until app restart",
                        _involuntaryExitsUtc.Count, ParakeetServerPolicy.StormWindow);
                }
                return null;
            }

            var payloadDir = Path.GetDirectoryName(_exePath)!;
            // Read AFTER the retirement above: a dead unserved Auto child latches the session to Cpu.
            var mode = EffectiveMode(_launchMode, requestedMode);
            var cpuRetried = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Per SPAWN, the CPU retry included: it can come up to a health budget after the
                // first check.
                var verdict = _spawnGate(payloadDir);
                if (!verdict.Spawnable)
                {
                    // Not charged: nothing was spawned. Sanitized - the reason can carry a certificate
                    // subject, which is attacker-authored text on a planted file.
                    Logger.Error("llama-server: refusing to spawn - {Reason}", LogValueSanitizer.SingleLine(verdict.RefusalReason));
                    return null;
                }
                var apiKey = LlamaServerLaunch.NewApiKey();
                var child = _launcher.TryLaunch(_exePath, modelPath, apiKey, mode);
                if (child is null)
                {
                    Logger.Warning("llama-server: launch failed (job setup or process creation, launch mode {Mode})", mode);
                    return null;
                }

                var outcome = await WaitForHealthAsync(child, ct).ConfigureAwait(false);
                if (outcome is { } baseUri)
                {
                    var waited = Stopwatch.StartNew();
                    while (child.ObservedThinking is null && waited.Elapsed < ThinkingLineWait && !child.HasExited)
                    {
                        // Not the caller's token: a cancel here would leave a healthy child that is
                        // neither resident nor retiring - alive, unowned, holding the model file.
                        // The wait is bounded at ThinkingLineWait.
                        await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    }
                    if (child.ObservedThinking == true)
                    {
                        Retire(child);
                        _thinkingRefusedModels.Add(modelIdentity);
                        Logger.Error("llama-server: the model reports thinking on despite --reasoning off - refused for this session (a thinking model misses the dictation deadline)");
                        return null;
                    }
                    if (mode == LlamaLaunchMode.Cpu && cpuRetried)
                    {
                        _launchMode = LlamaLaunchMode.Cpu;
                        Logger.Warning("llama-server: CPU retry healthy after the Auto (GPU) child died before health - latching CPU for this session");
                    }
                    var generation = Interlocked.Increment(ref _generation);
                    var resident = new Resident(child, baseUri, apiKey, modelIdentity, mode, generation);
                    Volatile.Write(ref _resident, resident);
                    Logger.Information("llama-server: generation {Gen} healthy on port {Port} (pid {Pid}, launch mode {Mode}, gpu evidence {OnGpu})",
                        generation, baseUri.Port, child.Pid, mode, resident.OnGpu);
                    return resident.LeaseFor(generation);
                }

                var exitedOnItsOwn = child.HasExited;
                Retire(child);
                if (exitedOnItsOwn && mode == LlamaLaunchMode.Auto && !cpuRetried)
                {
                    Logger.Warning("llama-server: Auto (GPU) child exited before health - retrying once on CPU (not charged to the storm fuse)");
                    if (_retiring is not null)
                    {
                        Logger.Warning("llama-server: predecessor exit unconfirmed - CPU retry deferred to the next call");
                        return null;
                    }
                    mode = LlamaLaunchMode.Cpu;
                    cpuRetried = true;
                    continue;
                }
                if (exitedOnItsOwn)
                {
                    RecordInvoluntaryExit();
                }
                Logger.Warning("llama-server: not healthy within {Budget} (childExited={Exited}, launch mode {Mode})",
                    _healthBudget, exitedOnItsOwn, mode);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Kill the live child, but only if <paramref name="generation"/> is still the live
    /// one. An expected stop — charged only if the child had already died on its own. A bounded
    /// <paramref name="gateWait"/> that expires skips the kill: the holder is an acquire, which
    /// mints a newer generation anyway.</summary>
    internal async Task KillGenerationAsync(int generation, TimeSpan? gateWait = null)
    {
        if (gateWait is { } bound)
        {
            if (!await _gate.WaitAsync(bound).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        try
        {
            if (_resident is { } resident && Volatile.Read(ref _generation) == generation)
            {
                RetireResident(resident, "killed by generation");
                Logger.Information("llama-server: generation {Gen} killed", generation);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A child is loaded right now (read without the gate - a hint; under the gate it is exact).</summary>
    internal bool HasResident => Volatile.Read(ref _resident) is not null;

    /// <summary>Mid-session retire (a model delete, the GPU toggle): confirm-or-park, bounded gate
    /// wait. False = could not retire inside the bound, or the kill is unconfirmed. With
    /// <paramref name="onlyModelIdentity"/>, a resident serving ANOTHER model is left alone.</summary>
    internal async Task<bool> TryRetireResidentAsync(TimeSpan gateWait, string? onlyModelIdentity = null, Func<bool>? onlyIf = null)
    {
        if (!await _gate.WaitAsync(gateWait).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            // Asked UNDER the gate: a caller that took a lease while this waited is seen here, so
            // an idle unload never kills a child a request has just been handed.
            if (onlyIf is not null && !onlyIf())
            {
                return false;
            }
            if (!TryClearRetiring())
            {
                return false;
            }
            return _resident is not { } resident
                   || (onlyModelIdentity is not null && !string.Equals(resident.ModelIdentity, onlyModelIdentity, StringComparison.Ordinal))
                   || RetireResident(resident, "retired");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>App exit: explicit kill with the same bounded gate wait as Parakeet's
    /// (<see cref="ParakeetServerPolicy.ShutdownGateWait"/>). On a miss the kill-on-close job
    /// terminates the child at process exit.</summary>
    internal async Task ShutdownAsync()
    {
        if (!await _gate.WaitAsync(ParakeetServerPolicy.ShutdownGateWait).ConfigureAwait(false))
        {
            Logger.Warning("llama-server: shutdown gate busy - skipping the explicit kill; the kill-on-close job terminates the child at process exit");
            return;
        }
        try
        {
            if (_resident is { } resident)
            {
                resident.Child.Kill();
                _ = resident.Child.WaitForExit(ParakeetServerPolicy.RetireWait);
                resident.Child.Dispose();
                Volatile.Write(ref _resident, null);
            }
            if (_retiring is { } retiring)
            {
                retiring.Dispose();
                _retiring = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Poll until <c>/health</c> answers, the child dies, or the budget ends. Returns the
    /// base URI on health. The health budget is bounded in real time and each probe await is
    /// bounded too, so a probe that never answers cannot hold the gate. User cancellation retires
    /// the candidate and propagates.</summary>
    private async Task<Uri?> WaitForHealthAsync(ILlamaServerChild child, CancellationToken ct)
    {
        var spawnedUtc = _utcNow();
        Uri? baseUri = null;
        using var health = CancellationTokenSource.CreateLinkedTokenSource(ct);
        health.CancelAfter(_healthBudget);
        try
        {
            while (_utcNow() - spawnedUtc <= _healthBudget)
            {
                health.Token.ThrowIfCancellationRequested();
                if (child.HasExited)
                {
                    return null;
                }
                if (baseUri is null && child.TryReadListeningPort() is { } port)
                {
                    baseUri = new Uri($"http://127.0.0.1:{port}/");
                }
                if (baseUri is not null)
                {
                    var probeTask = _probe(baseUri, health.Token);
                    bool healthy;
                    try
                    {
                        healthy = await probeTask.WaitAsync(health.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = probeTask.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        throw;
                    }
                    if (healthy)
                    {
                        return baseUri;
                    }
                }
                await Task.Delay(ParakeetServerPolicy.HealthPollInterval, health.Token).ConfigureAwait(false);
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Retire(child);
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // the budget fired during an await: a health failure, not a cancel.
        }
        catch (Exception ex)
        {
            // A throwing probe is a health failure; the child did not exit on its own.
            Logger.Warning(ex, "llama-server: health probe threw");
            return null;
        }
    }

    /// <summary>Retire the resident. A child that died on its own is charged — unless it was an
    /// Auto child that never served a request, which is the driver-shaped death: not charged, and
    /// Cpu is latched so the next spawn uses it. False when the kill is unconfirmed.</summary>
    private bool RetireResident(Resident resident, string why)
    {
        if (resident.Child.HasExited)
        {
            if (resident.Mode == LlamaLaunchMode.Auto && !resident.Served && _launchMode == LlamaLaunchMode.Auto)
            {
                _launchMode = LlamaLaunchMode.Cpu;
                Logger.Warning("llama-server: the Auto (GPU) child died before serving a request - latching CPU for this session (not charged to the storm fuse)");
            }
            else
            {
                RecordInvoluntaryExit();
            }
        }
        Retire(resident.Child);
        Volatile.Write(ref _resident, null);
        Logger.Information("llama-server: resident child {Why}", why);
        return _retiring is null;
    }

    private bool TryClearRetiring()
    {
        if (_retiring is not { } retiring)
        {
            return true;
        }
        if (!retiring.HasExited)
        {
            return false;
        }
        retiring.Dispose();
        _retiring = null;
        return true;
    }

    /// <summary>Kill and CONFIRM; an unconfirmed exit parks the child in <c>_retiring</c>.</summary>
    private void Retire(ILlamaServerChild child)
    {
        child.Kill();
        if (child.WaitForExit(ParakeetServerPolicy.RetireWait))
        {
            child.Dispose();
            return;
        }
        Logger.Warning("llama-server: kill not confirmed within {Wait} - parking in retiring state", ParakeetServerPolicy.RetireWait);
        _retiring = child;
    }

    private void RecordInvoluntaryExit()
    {
        var now = _utcNow();
        _involuntaryExitsUtc.Add(now);
        _involuntaryExitsUtc.RemoveAll(t => t <= now - ParakeetServerPolicy.StormWindow);
        Logger.Warning("llama-server: involuntary exit recorded ({Count} in window)", _involuntaryExitsUtc.Count);
    }

    /// <summary>The live child and what it was spawned for. <see cref="Served"/> is written by
    /// <see cref="NotifyServed"/> off the gate, hence volatile.</summary>
    private sealed class Resident(
        ILlamaServerChild child, Uri baseUri, string apiKey, string modelIdentity, LlamaLaunchMode mode, int firstGeneration)
    {
        private volatile bool _served;

        public ILlamaServerChild Child { get; } = child;
        public string ModelIdentity { get; } = modelIdentity;
        public LlamaLaunchMode Mode { get; } = mode;
        public int FirstGeneration { get; } = firstGeneration;
        public bool OnGpu => LlamaServerLogLine.IsGpuDevice(Child.ObservedDevice?.Token);

        public bool Served
        {
            get => _served;
            set => _served = value;
        }

        public LlamaLease LeaseFor(int generation) => new(baseUri, apiKey, generation, OnGpu);
    }
}
