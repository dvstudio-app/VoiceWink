using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-49: pays the once-per-machine GPU shader/pipeline compile in the BACKGROUND so it never
/// lands inside the user's first dictation (owner verdict on the live smoke: a 12.5 s first
/// decode is "not acceptable"). One coordinator for both engines, gated by
/// <see cref="GpuWarmupMarker"/> (once per app version — the compile persists in the driver's
/// on-disk cache across restarts, measured 12.51 s → 0.90 s, so re-warming every launch buys
/// nothing and a warm Parakeet child costs ~1 GB + a second ~900 MB model read).
///
/// <para><b>TRN-50 (2026-09-03): the warm decode is also the GPU SELF-TEST.</b> A driver can load
/// cleanly and decode real speech to NOTHING — the owner's ARM64 laptop (Adreno X1-85, Vulkan):
/// 3/3 dictations empty on the GPU, 3/3 fine on the CPU, while this warm-up had logged
/// "complete" three minutes earlier, because it decoded SILENCE and checked only that the call
/// succeeded — and an empty transcript is the correct answer for silence. With the golden clip
/// bundled (<see cref="GpuSelfTestClip"/>) the short warm shape IS that clip, the words that come
/// back are judged (<see cref="GpuSelfTestVerdict"/>), and a FAIL on a child that POSITIVELY
/// reported a Vulkan device (<see cref="ParakeetGpuEvidence"/> — the launch mode is a request,
/// not proof) is persisted per app version and requests CPU for this session through the
/// non-blocking <see cref="ParakeetServerProcess.RequestCpu"/>. Whisper's half judges the same
/// clip through its own warm decode, records the verdict, and applies it at the NEXT start — the
/// loaded Vulkan runtime is process-frozen and a same-session switch is not a proven CPU boundary
/// (Codex revised-plan check, B5). Without the clip every path here is TRN-49 unchanged.</para>
///
/// <para><b>Instance pattern</b> (the `RecordingStartLatencyProbe.Instance` precedent):
/// `MainViewModel` is the one recording-admission caller and the composition root the one
/// configurer; unconfigured (every test, and every build where the feature has nothing to do)
/// every entry no-ops.</para>
///
/// <para><b>Structural invariants, each pinned by a test:</b></para>
/// <list type="bullet">
/// <item><b>Fully subject to the provenance gate:</b> the Parakeet warm child spawns only after
/// <see cref="ParakeetSpawnGate.Check"/> passes — the laptop session's pre-diff Blocker: escaping
/// the storm fuse must never escape the TRN-34 gate, and the exe is user-writable
/// (per-user Velopack install). A refusal logs once and warms nothing. Since TRN-50 the gate,
/// the health wait and the kill-and-confirm live in <see cref="ParakeetEphemeralChild"/>, which
/// the coordinator's CPU re-decode shares — one spawn path, one set of properties.</item>
/// <item><b>Invisible to the storm fuse and the launch-mode latch:</b> the warm child is spawned
/// through the LAUNCHER seam (job-object kill-on-close, so an app crash cannot orphan it) and
/// never through <see cref="ParakeetServerProcess"/> — its death logs one line and changes
/// nothing: no fuse charge, no launch-mode change, no marker write (the machine stays "will warm
/// next launch"). A latency optimisation must never degrade capability. The one thing the
/// warm-up may now change is the OTHER direction — a self-test FAIL requests CPU — and that goes
/// through the process's own non-blocking intent, never its gate.</item>
/// <item><b>Never ahead of real work:</b> <see cref="Cancel"/> at recording admission — and at
/// every other user-initiated engine action (Audio Transcribe, a Models-page selection, a
/// language reload) plus shutdown — cancels the Whisper warm decode (whisper.cpp abort path) and
/// kills the warm child; the warm-up never re-arms that session, and the one log line names the
/// reason. Whisper's honest bound is documented on
/// <c>WhisperTranscriptionService.WarmUpDecodeAsync</c>.</item>
/// <item><b>Never cancelled by the machinery that guards it (TRN-57):</b> the STARTUP preload's
/// resident spawn reaches <see cref="WaitForParakeetQuiesceAsync"/> milliseconds after the
/// warm-up is queued, and until TRN-57 that quiesce cancelled session-permanently — so on every
/// machine with Parakeet selected (the default engine) the warm-up cancelled itself 3 ms after
/// queuing, logged it as "recording admission", and never wrote the marker (laptop session,
/// reproduced 2/2 on a fresh reboot). The quiesce now takes a GRACE: it lets a queued warm-up
/// finish on its own before cancelling. A recording-triggered prepare passes zero grace and has
/// already cancelled at admission, so a dictation never waits. That grace is also what lands a
/// self-test verdict BEFORE the resident spawns on the normal startup path.</item>
/// <item><b>The resident Auto child never initializes against a DYING warm child:</b>
/// TerminateProcess is asynchronous, and a GPU device init against ~1 GB of dying predecessor
/// fails into the uncharged CPU retry and latches CPU for the session — the "latency
/// optimisation must never degrade capability" violation. The quiesce therefore CONFIRMS the
/// child's exit or reports <see cref="ParakeetQuiesceOutcome.Unconfirmed"/>, on which the
/// coordinator does not spawn; and the warm-up MARKER is written only after a confirmed exit
/// (Codex final check) — an unconfirmed teardown must never suppress the next launch's warm-up.</item>
/// <item><b>An inconclusive self-test pins nothing, and an unconfirmed compute records nothing.</b>
/// A transport failure, a timeout, a cancel, or a child that never printed a Vulkan selection
/// ends in today's skip path — no verdict, no CPU request. Only a decode that RAN on a
/// GPU-confirmed child and produced too little of the clip's text is a FAIL.</item>
/// </list>
/// </summary>
internal sealed class GpuWarmup
{
    private static ILogger Logger => Log.ForContext<GpuWarmup>();

    public static GpuWarmup Instance { get; } = new();

    /// <summary>Everything the warm-up may touch, wired once at the composition root. Null until
    /// then — and in tests forever — which is what makes every public entry a structural no-op
    /// outside the configured app. <paramref name="ResolveClip"/> (TRN-50) yields the golden
    /// clip or null; null keeps every decode the TRN-49 silence shape.</summary>
    public sealed record Configuration(
        GpuWarmupMarker Marker,
        bool WhisperVulkanUsable,
        ParakeetPlan? Parakeet,
        Func<GpuSelfTestClip?>? ResolveClip = null);

    /// <summary>The Parakeet half's ingredients. Null when there is nothing to warm: pcpp
    /// disabled, GPU toggle off, launch mode already CPU, or no system/bundled Vulkan path.
    /// <paramref name="RequestCpu"/> (TRN-50) is <see cref="ParakeetServerProcess.RequestCpu"/>,
    /// injected so a test can observe the request without a process manager.</summary>
    public sealed record ParakeetPlan(
        IParakeetServerLauncher Launcher,
        ParakeetServerTranscriptionClient Client,
        string ExePath,
        string ExpectedExeSha256,
        Func<string?> ResolveGgufPath,
        Action<string>? RequestCpu = null);

    /// <summary>Per-decode bound for the warm shapes. The measured worst first-exposure
    /// compile is 15.5 s (57.7 s clip, cold driver cache, RTX 3080); 120 s is generous headroom
    /// for slower GPUs while staying far under the named client's 5-minute deadline — without
    /// this, a wedged child parks the ~1 GB warm-up for up to 5 minutes per call (the same
    /// per-await-bound lesson the production health loop already carries from Codex diff r3).</summary>
    private static readonly TimeSpan WarmDecodeBudget = TimeSpan.FromSeconds(120);

    /// <summary>The PARAKEET leg's cancellation — session-permanent, every cancel reason (a warm
    /// child must never sit ahead of a user action). The quiesce reads it.</summary>
    private readonly CancellationTokenSource _cts = new();
    private Configuration? _config;
    private int _parakeetQueued;
    private Task _parakeetTask = Task.CompletedTask;

    // ---- TRN-64: the Whisper leg has its OWN scope, re-created per queued run ----
    //
    // The incident: the Whisper self-test shared the Parakeet leg's session-permanent CTS, and
    // recording admission (or a Models-page selection) cancelled it before it ever ran — so a
    // Whisper model selected after the first dictation decoded 13 minutes of garbage on a GPU
    // nothing had checked. Now: a per-run CTS (ModelSelection / LanguageReload / Shutdown cancel
    // it; admission and Audio Transcribe do NOT — the decode they admit is the one the test
    // protects), a per-MODEL queue (the verdict is per model, Kimi r1 K3), a verdict TASK the
    // first real decode awaits (Codex r1 C4: never synchronous inside the load), and an in-memory
    // copy of every verdict so a failed marker write cannot let this session decode on a GPU that
    // just failed (Codex r2 F3). Everything below is read and written under _whisperLock.
    private readonly object _whisperLock = new();
    private CancellationTokenSource? _whisperCts;
    /// <summary>The catalog name the queued run tests, <see cref="UnnamedModel"/> for the
    /// warm-up-only path (a model the catalog does not know), null when nothing is queued.</summary>
    private string? _whisperQueuedModel;
    private Task _whisperTask = Task.CompletedTask;
    private WhisperVerdictSlot? _whisperVerdict;
    private readonly Dictionary<string, GpuSelfTestOutcome> _whisperSessionVerdicts = new(StringComparer.OrdinalIgnoreCase);
    private GpuSelfTestOutcome? _whisperSessionPin;
    private volatile bool _shutdown;
    private const string UnnamedModel = "";

    /// <summary>TRN-64: one queued run's verdict — the task the gate awaits plus the CLAIM that
    /// decides who records it. The watchdog and the run each claim BEFORE writing anything, and
    /// the loser writes nothing (self-review, concurrency lens: both used to test
    /// <c>IsCompleted</c> and then record, so a decode returning inside the watchdog's write
    /// window recorded a Pass beside the watchdog's Inconclusive). Completed CANCELLED when the
    /// run decoded nothing — a cancel, or no processor — so the gate can tell "nothing learned"
    /// from "ran and could conclude nothing" (fail-open lens).</summary>
    internal sealed class WhisperVerdictSlot
    {
        private readonly TaskCompletionSource<GpuSelfTestOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;

        internal Task<GpuSelfTestOutcome> Task => _tcs.Task;

        /// <summary>True exactly once, for whoever gets here first.</summary>
        internal bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

        internal void SetResult(GpuSelfTestOutcome outcome) => _tcs.TrySetResult(outcome);

        internal void SetCanceled() => _tcs.TrySetCanceled();
    }

    /// <summary>TRN-64: how long the Whisper self-test may run from the moment its decode CALL
    /// starts (never from queueing or the model read) before the watchdog records
    /// <see cref="GpuSelfTestOutcome.Inconclusive"/>. Same value as <see cref="WarmDecodeBudget"/>
    /// — the measured worst first-exposure compile is 15.5 s, so this is ~8× that.</summary>
    internal static readonly TimeSpan WhisperSelfTestBudget = TimeSpan.FromSeconds(120);

    /// <summary>Test seam: the budget is 120 s wall-clock and the expiry branch is untestable at
    /// that cost. Production never sets it.</summary>
    internal TimeSpan? WhisperSelfTestBudgetOverride { get; set; }

    /// <summary>TRN-64 PR 2 test seam: the speed floor is 2× the audio the engine ACTUALLY decodes
    /// — 8 s for Parakeet's 4 s shape, 60 s for Whisper's 30 s encoder window, NEVER the clip's own
    /// 5.7 s, which is the basis the self-review reversed (Delta 3) — and the Slower branch is
    /// untestable at that cost. Production never sets it.</summary>
    internal TimeSpan? SpeedFloorOverride { get; set; }

    private TimeSpan SpeedFloorFor(int audioSamples) => SpeedFloorOverride ?? GpuSpeedFloor.FloorFor(audioSamples);

    /// <summary>TRN-64 PR 2: Whisper's speed floor — over whisper.cpp's 30 s ENCODER WINDOW, never
    /// the 2.85 s clip, because the encoder pads every input to that window and does the same work
    /// either way (self-review, fail-open lens). Named rather than inlined so the basis is PINNED:
    /// the timing method itself needs a real processor and no test reaches it, so a silent change
    /// back to the clip's length would otherwise kill no row (mutation-verified gap).</summary>
    internal TimeSpan WhisperSpeedFloor => SpeedFloorFor(GpuSpeedFloor.WhisperWindowSamples);

    /// <summary>TRN-57: a warm child whose exit was NOT confirmed within the run's own wait, kept
    /// (never disposed) so a later quiesce can observe it, plus the marker to write once it is
    /// observed gone — null when the decodes did not succeed. Read and written only under
    /// <see cref="_parakeetLock"/>.</summary>
    private LingeringChild? _lingering;

    private sealed record LingeringChild(IParakeetServerChild Child, GpuWarmupMarker? MarkOnConfirm);

    private enum LingeringState { None, Observed, StillAlive }

    /// <summary>How long a quiesce waits, after the run has ended, for an unconfirmed warm child
    /// to be observed exited — the run's own kill-and-confirm wait
    /// (<see cref="ParakeetServerPolicy.RetireWait"/>) plus signal latency. Beyond it the quiesce
    /// reports <see cref="ParakeetQuiesceOutcome.Unconfirmed"/> and the resident spawn waits for
    /// the next preparation.</summary>
    internal static readonly TimeSpan QuiesceConfirmBudget = ParakeetServerPolicy.RetireWait + TimeSpan.FromSeconds(1);

    /// <summary>Publishes <c>_parakeetQueued</c> and <c>_parakeetTask</c> atomically: a
    /// quiesce landing between "flag set" and "task assigned" would await the stale completed
    /// task and return while this run's kill-confirm is still pending — the exact spawn race
    /// the quiesce exists to close (kimi diff r1 A2).</summary>
    private readonly object _parakeetLock = new();

    internal GpuWarmup()
    {
    }

    public void Configure(Configuration config) => _config = config;

    /// <summary>Test seam: back to unconfigured with no session state. The suite runs
    /// single-threaded; a test that configures <see cref="Instance"/> restores it in a finally.</summary>
    internal void ResetForTests()
    {
        _config = null;
        lock (_whisperLock)
        {
            _whisperCts = null;
            _whisperQueuedModel = null;
            _whisperTask = Task.CompletedTask;
            _whisperVerdict = null;
            _whisperSessionVerdicts.Clear();
            _whisperSessionPin = null;
        }
        _shutdown = false;
        WhisperSelfTestBudgetOverride = null;
        SpeedFloorOverride = null;
    }

    /// <summary>Stop competing, permanently for this session, naming why. Recording admission is
    /// the canonical caller; Audio Transcribe, a Models-page selection, a language reload and
    /// shutdown take the same path (a user-initiated engine action must never wait behind the
    /// warm-up), and the quiesce itself calls it when a grace expires. The real decode compiles
    /// whatever the warm-up had not reached, the marker stays unwritten, and the next launch
    /// warms instead — fail-soft in the only direction that matters. The reason is LOGGED
    /// (TRN-57): until then every cancel read "by recording admission", which is how the startup
    /// self-cancel hid for a day.</summary>
    public void Cancel(GpuWarmupCancelReason reason)
    {
        // TRN-64: two scopes. The PARAKEET leg keeps every cancel (below, unchanged). The WHISPER
        // leg is cancelled only when the model itself is going away (a selection or a language
        // reload — the next load queues a test for its model) or the app is. NEVER at recording
        // admission or Audio Transcribe: the decode those admit is the one the self-test exists to
        // protect, and cancelling it there is what produced the 13-minute incident (Kimi r1 K6's
        // suggested split was rejected on exactly this point).
        var cancelsWhisper = reason is GpuWarmupCancelReason.ModelSelection
            or GpuWarmupCancelReason.LanguageReload
            or GpuWarmupCancelReason.Shutdown;
        if (!_cts.IsCancellationRequested && _parakeetQueued != 0)
        {
            Logger.Information("GPU warm-up cancelled: {Reason}", reason);
        }
        TryCancel(_cts, "GPU warm-up cancellation callback failed");
        if (cancelsWhisper)
        {
            CancellationTokenSource? whisper;
            lock (_whisperLock)
            {
                if (reason == GpuWarmupCancelReason.Shutdown)
                {
                    // Under the lock: QueueWhisperWarmup re-reads it there, so a load racing this
                    // cancel cannot publish a run nothing will ever cancel (self-review, concurrency lens).
                    _shutdown = true;
                }
                whisper = _whisperCts;
            }
            if (whisper is not null && !whisper.IsCancellationRequested)
            {
                Logger.Information("Whisper GPU self-test cancelled: {Reason}", reason);
                TryCancel(whisper, "Whisper GPU self-test cancellation callback failed");
            }
        }
    }

    /// <summary>Cancel() runs registered callbacks synchronously on THIS thread — the hotkey/UI
    /// thread at recording admission — and rethrows their failures. A callback failure (an HTTP
    /// abort, whisper.cpp's abort registration) must never escape into admission: it would skip
    /// the license/update gates and latch the hotkey state machine (self-review, concurrency lens).</summary>
    private static void TryCancel(CancellationTokenSource cts, string failureMessage)
    {
        try
        {
            cts.Cancel();
        }
        catch (AggregateException ex)
        {
            Logger.Warning(ex, failureMessage);
        }
    }

    // ---- TRN-50: the verdict surface the rest of the app reads and writes ----

    /// <summary>The coordinator's CPU re-decode recorded text where the GPU produced none: persist
    /// the Parakeet verdict. No-op unconfigured (tests, harnesses).
    /// <para>Returns whether the verdict is DURABLE — REL-30's gate on reporting the failure to
    /// Sentry. Unconfigured reads as false: nothing was persisted, so nothing may be reported as
    /// a once-per-version fact.</para></summary>
    public bool RecordParakeetVerdict(GpuSelfTestOutcome outcome, string? gpuName)
    {
        if (_config is null) return false;
        var durable = _config.Marker.RecordVerdict(GpuSelfTestEngine.Parakeet, outcome, gpuName);
        NotifyChanged(); // UI-12: the row re-reads the verdict whether or not the write was durable
        return durable;
    }
    /// <summary>UI-12: raised after anything that changes what the Models page's GPU row should
    /// say — a verdict, a warmed mark, a re-arm, or a run ending for any reason (so "Checking…"
    /// never outlives the check). Raised on whatever thread wrote the change; subscribers marshal.
    /// Subscriber exceptions are swallowed here: the warm-up must never fail because a page did.</summary>
    public event Action? SelfTestChanged;

    private void NotifyChanged()
    {
        // Per subscriber, not one Invoke: a multicast delegate stops at the first throw, which
        // would let one failing page silence every other subscriber (pinned by test).
        if (SelfTestChanged is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A GPU self-test change subscriber threw");
            }
        }
    }

    /// <summary>UI-12: is that engine's warm-up/self-test in flight right now? Whisper: the queued
    /// run's task; Parakeet: the queued task under its lock. False unconfigured.</summary>
    internal bool IsSelfTestRunning(GpuSelfTestEngine engine)
    {
        if (_config is null) return false;
        if (engine == GpuSelfTestEngine.Whisper)
        {
            lock (_whisperLock)
            {
                return _whisperQueuedModel is not null && !_whisperTask.IsCompleted;
            }
        }
        lock (_parakeetLock)
        {
            return _parakeetQueued != 0 && !_parakeetTask.IsCompleted;
        }
    }

    /// <summary>UI-12: what the row may say about an engine that has NO verdict yet. Running while
    /// its run is in flight; Awaiting when the marker still owes it a warm-up (no verdict, not
    /// warmed — the check happens at a start where the toggle lets it); None otherwise (a verdict
    /// exists, or the engine was warmed without one — nothing pending, nothing to announce).</summary>
    private GpuSelfTestPhase PhaseFor(GpuSelfTestEngine engine, GpuWarmupMarker marker, GpuSelfTestRecord record)
    {
        if (IsSelfTestRunning(engine)) return GpuSelfTestPhase.Running;
        var owed = engine == GpuSelfTestEngine.Whisper ? marker.WhisperNeedsWarmup() : marker.ParakeetNeedsWarmup();
        return owed && record.Outcome == GpuSelfTestOutcome.Unknown ? GpuSelfTestPhase.Awaiting : GpuSelfTestPhase.None;
    }

    /// <summary>The GPU toggle's re-arm: forget both engines' verdicts, names AND warmed flags in
    /// one write, so the next start re-runs the self-test instead of skipping it on a stale
    /// "warmed" (Codex plan round, Blocker 1). No-op unconfigured.</summary>
    public void RearmSelfTest()
    {
        if (_config is { } config)
        {
            config.Marker.RearmAll();
            Logger.Information("GPU self-test verdicts cleared - both engines test the GPU again at the next start of VoiceWink");
            NotifyChanged();
        }
    }

    /// <summary>What the Models page row shows — the LIVE marker, so a re-arm is visible on the next
    /// page build without a restart. Null unconfigured.</summary>
    /// <param name="whisperModel">TRN-64 PR 2 (self-review): the SELECTED Whisper model's catalog
    /// name, or null. Fills <see cref="GpuSelfTestSummary.WhisperSelectedModelOutcome"/> from
    /// <see cref="WhisperGateOutcome"/> — the model's OWN verdict in a GPU-engaged process — so the
    /// row's positive tick can never speak for a model nobody judged, nor for a process where ggml
    /// enumerated no device.</param>
    public GpuSelfTestSummary? ReadSelfTestSummary(string? whisperModel = null)
    {
        if (_config is not { } config) return null;
        var whisper = config.Marker.ReadVerdict(GpuSelfTestEngine.Whisper);
        var parakeet = config.Marker.ReadVerdict(GpuSelfTestEngine.Parakeet);
        lock (_whisperLock)
        {
            // The in-memory session pin outranks a marker the REL-30 path could not write: a
            // process that refuses every Whisper decode must never show a stale Pass (self-review).
            // RefusesDecode, not PinsCpu (Kimi verification r2): a persisted Slower is still a
            // marker this session has OVERTAKEN when its own verdict refuses — the row would read
            // "too slow … from the next start" while every Whisper decode is being refused NOW.
            // A session Slower over a persisted Pass keeps working: Slower does not refuse.
            if (_whisperSessionPin is { } pin && !whisper.RefusesDecode)
            {
                whisper = new GpuSelfTestRecord(pin, WhisperBackendLog.ObservedGpuName);
            }
        }
        return new GpuSelfTestSummary(
            whisper, parakeet,
            PhaseFor(GpuSelfTestEngine.Whisper, config.Marker, whisper),
            PhaseFor(GpuSelfTestEngine.Parakeet, config.Marker, parakeet),
            whisperModel is null ? GpuSelfTestOutcome.Unknown : WhisperGateOutcome(whisperModel));
    }

    /// <summary>
    /// Wait (bounded) until no warm Parakeet child can be alive, and say whether that is
    /// CONFIRMED. The resident spawn path calls this before creating its own child:
    /// TerminateProcess is asynchronous, so without confirmation the resident Auto child can
    /// initialize its GPU device against ~1 GB of dying warm child, fail, and take the uncharged
    /// CPU retry — a session-permanent CPU latch caused by the warm-up, the exact "latency
    /// optimisation must never degrade capability" violation this class forbids (self-review,
    /// concurrency lens).
    ///
    /// <para><paramref name="grace"/> (TRN-57): how long to let a queued warm-up FINISH on its own
    /// before cancelling it. The startup preload passes the coordinator's spawn grace, because
    /// the warm-up is what makes its first dictation fast — and because the pre-TRN-57 quiesce
    /// cancelled immediately, which on every Parakeet-default machine killed the warm-up 3 ms
    /// after it was queued. Every path that already cancelled (recording admission, Audio
    /// Transcribe) or must not wait (model delete, the live transcription acquire) passes
    /// <see cref="TimeSpan.Zero"/>: the cancel-first behaviour, unchanged. A cancel that arrives
    /// from elsewhere during the grace completes the task and releases this wait at once.</para>
    ///
    /// <para>Outcome contract (Codex plan round): <see cref="ParakeetQuiesceOutcome.Unconfirmed"/>
    /// means a warm child MAY still be alive — the run did not end within its bounds after
    /// cancellation, or its exit was not observed within <see cref="QuiesceConfirmBudget"/> — and
    /// the caller must not spawn a resident Auto child; the handle is retained and the next
    /// quiesce re-observes it. Every other outcome means no warm child is alive.</para>
    /// </summary>
    public async Task<ParakeetQuiesceOutcome> WaitForParakeetQuiesceAsync(TimeSpan grace, GpuWarmupCancelReason reasonIfCancelled)
    {
        Task? task;
        lock (_parakeetLock)
        {
            task = _parakeetQueued == 0 ? null : _parakeetTask;
        }

        if (task is not null)
        {
            if (!task.IsCompleted && grace > TimeSpan.Zero)
            {
                var started = DateTime.UtcNow;
                await Task.WhenAny(task, Task.Delay(grace)).ConfigureAwait(false);
                if (task.IsCompleted && !_cts.IsCancellationRequested)
                {
                    // Visible on purpose: a preparation that waited here is the startup preload
                    // (or a future prepare site that forgot to cancel first — bounded either way).
                    Logger.Information("GPU warm-up: resident spawn waited {Seconds:F1}s for the warm-up to finish",
                        (DateTime.UtcNow - started).TotalSeconds);
                }
            }

            if (!task.IsCompleted)
            {
                Cancel(reasonIfCancelled);
                if (await Task.WhenAny(task, Task.Delay(QuiesceConfirmBudget)).ConfigureAwait(false) != task)
                {
                    // The run is wedged past its own bounds: its finally has not run, so the child
                    // may be alive. Report it; never spawn against it.
                    Logger.Warning("GPU warm-up: the warm-up task did not end within {Seconds:F0}s of cancellation - reporting unconfirmed",
                        QuiesceConfirmBudget.TotalSeconds);
                    return ParakeetQuiesceOutcome.Unconfirmed;
                }
            }
        }

        // The run has ended (or nothing was queued). Its finally either confirmed the child's exit
        // or retained the child; observe a retained one, bounded.
        var lingering = await ObserveLingeringAsync().ConfigureAwait(false);
        if (lingering == LingeringState.StillAlive)
        {
            return ParakeetQuiesceOutcome.Unconfirmed;
        }
        if (task is null)
        {
            return lingering == LingeringState.Observed ? ParakeetQuiesceOutcome.Completed : ParakeetQuiesceOutcome.Idle;
        }
        // Cancelled by ANYONE — this quiesce's grace expiry, or admission/shutdown arriving during
        // the grace — reads Cancelled; only an untouched run that ended on its own reads Completed.
        return _cts.IsCancellationRequested ? ParakeetQuiesceOutcome.Cancelled : ParakeetQuiesceOutcome.Completed;
    }

    /// <summary>Observe a retained (unconfirmed) warm child, bounded by
    /// <see cref="QuiesceConfirmBudget"/>. Exactly one observer disposes it and writes the deferred
    /// marker: the clear happens under the lock, and only the caller that cleared marks.</summary>
    private async Task<LingeringState> ObserveLingeringAsync()
    {
        LingeringChild? lingering;
        lock (_parakeetLock)
        {
            lingering = _lingering;
        }
        if (lingering is null)
        {
            return LingeringState.None;
        }

        var deadline = DateTime.UtcNow + QuiesceConfirmBudget;
        while (!lingering.Child.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(ParakeetServerPolicy.HealthPollInterval).ConfigureAwait(false);
        }
        if (!lingering.Child.HasExited)
        {
            Logger.Warning("GPU warm-up: the warm child's exit is still unconfirmed - the resident spawn waits for the next preparation");
            return LingeringState.StillAlive;
        }

        var mine = false;
        lock (_parakeetLock)
        {
            if (ReferenceEquals(_lingering, lingering))
            {
                _lingering = null;
                mine = true;
            }
        }
        if (mine)
        {
            lingering.Child.Dispose();
            if (lingering.MarkOnConfirm is { } marker)
            {
                marker.MarkParakeetWarmed();
                Logger.Information("Parakeet GPU warm-up complete (exit confirmed late)");
                NotifyChanged();
            }
        }
        return LingeringState.Observed;
    }

    /// <summary>Called by <c>WhisperTranscriptionService</c> after every successful model load,
    /// with the catalog name of the model it loaded (null for a model the catalog does not know).
    /// Runs when the engine still needs its once-per-version warm-up OR the model still needs its
    /// self-test (TRN-64: per model — a Pass on Small says nothing about Large V3 Turbo, the model
    /// that decoded the incident's garbage). Only when configured and Vulkan loaded-or-loadable.
    /// With the golden clip bundled the warm decode is the Whisper GPU self-test (TRN-50): judged
    /// only when ggml enumerated at least one Vulkan device for this process — the positive
    /// evidence, since a loaded Vulkan library with zero devices computes on CPU. A FAIL (or a
    /// budget expiry, <see cref="GpuSelfTestOutcome.Inconclusive"/>) refuses this session's GPU
    /// decodes of Whisper through <see cref="WhisperGateOutcome"/> and pins the CPU at the next
    /// start (the loaded runtime cannot be switched in-process; see the class doc).
    ///
    /// <para>A run in flight for the SAME model is left alone; one for a DIFFERENT model is
    /// cancelled (its processor is the one this load just disposed) and the new model's run
    /// replaces it. <see cref="SelfTestChanged"/> fires at publish and at the run's end.</para>
    ///
    /// <para>TRN-64 (self-review, fail-open lens): EVERY load path calls this — the already-loaded
    /// path too — because a run cancelled by a selection or a reload records nothing and the next
    /// load used to be the only thing that could re-queue it. So a run that decoded the clip but
    /// could conclude nothing (a pinned language, no clip, no device) is REMEMBERED for the
    /// session (<see cref="RecordWhisperSessionRan"/>) and not re-run before every dictation;
    /// <paramref name="processorRebuilt"/> — the factory or its language changed — re-offers it,
    /// since a new processor is a new thing to test. A verdict already reached always stands.
    /// A PINNED engine — the session's pin or the marker's — never queues anything: the warm-up
    /// would re-exercise the GPU that just failed (Codex diff r1 Blocker).</para>
    ///
    /// <para>Fire-and-forget by design for every load-path caller; the gate calls
    /// <see cref="QueueWhisperWarmupCore"/> for the run's own verdict task.</para>
    /// </summary>
    public void QueueWhisperWarmup(WhisperTranscriptionService service, string? modelName = null, bool processorRebuilt = false)
        => _ = QueueWhisperWarmupCore(service, modelName, processorRebuilt);

    /// <summary>The queue itself. Returns the verdict task of the run now in flight for this model —
    /// the one just queued, or the one already running — and null when nothing is queued or the run
    /// tests nothing. The gate awaits THIS rather than re-reading <see cref="WhisperVerdictTask"/>:
    /// a run that has already ended (a no-processor run ends in microseconds) reads as "nothing
    /// pending" there, and "nothing pending" must never become "nothing to wait for".</summary>
    internal Task<GpuSelfTestOutcome>? QueueWhisperWarmupCore(WhisperTranscriptionService service, string? modelName, bool processorRebuilt)
    {
        var config = _config;
        if (config is null || !config.WhisperVulkanUsable || _shutdown)
        {
            return null;
        }
        var model = GpuWarmupMarker.ValidModelName(modelName);
        var needsWarm = config.Marker.WhisperNeedsWarmup();
        var markerNeedsTest = model is not null && config.Marker.WhisperNeedsTest(model);
        var markerPin = config.Marker.ReadVerdict(GpuSelfTestEngine.Whisper);

        var key = model ?? UnnamedModel;
        WhisperVerdictSlot? verdict;
        lock (_whisperLock)
        {
            if (_shutdown)
            {
                return null; // re-read under the lock — Cancel(Shutdown) sets it there (self-review, concurrency lens)
            }
            var pin = _whisperSessionPin ?? (markerPin.PinsCpu ? markerPin.Outcome : (GpuSelfTestOutcome?)null);
            if (pin is { } refusing && GpuWarmupMarker.RefusesDecode(refusing))
            {
                // Codex diff r1 Blocker: a pinning verdict CLEARS WhisperWarmed, so needsWarm read true
                // and the next load queued a verdict-less warm decode — no watchdog — on the GPU that
                // had just failed; a wedged driver would then hold the model lock for the session.
                // A REFUSING pin means this process issues no further Whisper GPU decode of any kind:
                // the gate refuses the real ones, and the next start loads CPU-only.
                return null;
            }
            if (pin == GpuSelfTestOutcome.Slower)
            {
                // TRN-64 PR 2 (self-review, concurrency lens): a Slower pin ends the WARM-UPS — no
                // more latency work on a card the next start abandons — but never the correctness
                // TESTS of models this session has not judged: the gate lets Slower through, so an
                // untested second model would otherwise decode on the GPU the incident is about.
                needsWarm = false;
            }
            if (processorRebuilt && model is not null
                && _whisperSessionVerdicts.TryGetValue(model, out var ran) && ran == GpuSelfTestOutcome.Unknown)
            {
                // A NEW processor for this model: a run that could form no verdict on the old one is
                // owed again. Pass/Fail/Inconclusive stand — only a re-arm, an update or a driver
                // change clears those, and none of them passes through here.
                _whisperSessionVerdicts.Remove(model);
            }
            var needsTest = markerNeedsTest && !SessionKnowsLocked(model!);
            if (!needsWarm && !needsTest)
            {
                return null;
            }
            if (_whisperQueuedModel is not null && !_whisperTask.IsCompleted)
            {
                if (string.Equals(_whisperQueuedModel, key, StringComparison.OrdinalIgnoreCase))
                {
                    return _whisperVerdict?.Task; // already in flight for this model: its own verdict, if it has one
                }
                // A different model was loaded under a run that has not ended — that run is
                // parked on the model lock this load holds, and its processor is gone. End it.
                if (_whisperCts is { } stale)
                {
                    TryCancel(stale, "Whisper GPU self-test cancellation callback failed");
                }
            }
            var cts = new CancellationTokenSource();
            _whisperCts = cts;
            _whisperQueuedModel = key;
            verdict = needsTest ? new WhisperVerdictSlot() : null;
            _whisperVerdict = verdict;
            _whisperTask = Task.Run(() => RunWhisperSelfTestAsync(service, config, model, cts, verdict));
        }
        // UI-12: "Checking…" BEGINS with the run — a page built before this load queued the test
        // would otherwise show "will be checked when VoiceWink restarts" for the whole run.
        NotifyChanged();
        return verdict?.Task;
    }

    private bool SessionKnows(string model)
    {
        lock (_whisperLock)
        {
            return SessionKnowsLocked(model);
        }
    }

    /// <summary>Caller holds <see cref="_whisperLock"/>. True once this session has a REFUSING pin
    /// (a Slower pin does not stop other models' tests — PR 2), or ANY entry for the model —
    /// including the Unknown <see cref="RecordWhisperSessionRan"/> writes when a run decoded but
    /// could conclude nothing.</summary>
    private bool SessionKnowsLocked(string model)
        => (_whisperSessionPin is { } pin && GpuWarmupMarker.RefusesDecode(pin)) || _whisperSessionVerdicts.ContainsKey(model);

    /// <summary>The self-test body. <paramref name="verdict"/> is completed EXACTLY once — by the
    /// watchdog (Inconclusive) or by this run's end — and RECORDED exactly once: whoever wins the
    /// slot's claim writes, the other writes nothing (self-review, concurrency lens). A run that
    /// decoded nothing — cancelled, or the processor was gone — completes the slot CANCELLED,
    /// never with a result: nothing was learned, and the gate refuses rather than proceeds on it
    /// (self-review, fail-open lens).</summary>
    private async Task RunWhisperSelfTestAsync(
        WhisperTranscriptionService service, Configuration config, string? model,
        CancellationTokenSource cts, WhisperVerdictSlot? verdict)
    {
        var outcome = GpuSelfTestOutcome.Unknown;
        var claimed = false;        // this run owns the verdict (the watchdog did not get there first)
        var decodedNothing = false; // cancelled, or no processor: the GPU was never exercised
        CancellationTokenSource? watchdog = null;
        try
        {
            var clip = config.ResolveClip?.Invoke();
            // 16 kHz is the app's canonical rate (every capture and conversion lands there);
            // two seconds of silence is the TRN-49 shape when no clip is bundled.
            var samples = clip?.Samples ?? new float[16000 * 2];
            var decoded = await service.WarmUpDecodeAsync(
                    samples,
                    cts.Token,
                    onNoProcessor: () => ReleaseWhisperQueue(cts),
                    onDecodeStarting: () =>
                    {
                        // The budget starts at the decode CALL — never at queueing or during the
                        // 900 MB model read, so a slow disk cannot expire it (Kimi r2 minor).
                        if (verdict is not null && clip is not null)
                        {
                            watchdog = StartWhisperWatchdog(config, model, cts, verdict);
                        }
                    })
                .ConfigureAwait(false);
            if (decoded is null)
            {
                // Nothing decoded (model unloaded between queue and run): no mark, no verdict, and
                // the queue was released by the callback WHILE the service still held its model
                // lock — clearing here, after the lock released, could land after a reload's
                // queue check observed the stale run, and nothing would ever re-queue (kimi diff
                // r1 A1 + codex verify r2).
                decodedNothing = true;
                return;
            }
            if (verdict is not null && !verdict.TryClaim())
            {
                // The watchdog already decided (Inconclusive): this late result changes nothing —
                // a decode that takes longer than the budget IS the finding.
                Logger.Information("Whisper GPU self-test finished after the watchdog had already recorded Inconclusive - late result ignored (TRN-64)");
                return;
            }
            claimed = true;
            // The decode RAN on this processor: whatever it concludes below, this session does not
            // run it again for this model until the processor is rebuilt — a load's re-offer would
            // otherwise re-run a clip-less or language-pinned test before every dictation.
            RecordWhisperSessionRan(model);
            var (text, language) = decoded.Value;

            // TRN-50: the correctness judgement — only with a clip, a Vulkan device observed, and a
            // language the English clip can be judged in. TRN-64 PR 2: a Pass is RECORDED only after
            // the speed floor below, so a correct-but-slow GPU records Slower, never Pass.
            GpuSelfTestJudgement? judgement = null;
            var gpuEvidenced = clip is not null && WhisperBackendLog.ObservedVulkanDevices >= 1;
            if (clip is not null && !GpuSelfTestVerdict.LanguageAllowsJudgement(language))
            {
                // The clip is ENGLISH and the processor decodes in whatever language the user
                // pinned (WithLanguage at load), so a Dutch- or German-pinned processor decodes
                // the clip AS that language by construction and the low recall proves the pin,
                // never a broken GPU — judging it would persist a GPU-confirmed FAIL on a
                // healthy machine, with a re-arm that fails identically (Kimi diff r1 Blocker).
                // Warmed and marked exactly as TRN-49; no correctness verdict either way — the
                // speed floor below still runs (time needs no language, Kimi r2 C4).
                Logger.Information("Whisper GPU self-test skipped: the decode language is pinned to {Language} and the golden clip is English - warmed, no verdict (TRN-50)",
                    LogValueSanitizer.SingleLine(language));
            }
            else if (clip is not null)
            {
                var judged = GpuSelfTestVerdict.Judge(text, clip.ExpectedWords);
                if (!gpuEvidenced)
                {
                    Logger.Information("Whisper GPU self-test decoded with {GpuCount} Vulkan device(s) observed: not a GPU verdict ({Judgement})",
                        WhisperBackendLog.ObservedVulkanDevices, GpuSelfTestVerdict.Describe(judged));
                }
                else if (judged.Outcome == GpuSelfTestOutcome.Fail)
                {
                    var gpuName = WhisperBackendLog.ObservedGpuName;
                    var persisted = RecordWhisperVerdict(config, model, GpuSelfTestOutcome.Fail, gpuName);
                    // REL-30: Error is what reaches Sentry (the sink's MinimumEventLevel), and a
                    // driver that reports success and decodes speech to nothing is contract
                    // drift, not handled noise. Reported ONLY once the verdict is durable: an
                    // unwritable marker cannot pin CPU, so the failure recurs at every launch
                    // and reporting it each time would be one event per start per user.
                    Logger.Write(GpuSelfTestReport.LevelFor(persisted),
                        "Whisper GPU self-test FAILED on {GpuName}: {Judgement} - Whisper refuses the graphics card for this session and runs on the CPU from the next start of VoiceWink (TRN-50/64) [display drivers on this PC: {DriverSignature}]",
                        gpuName ?? GpuToggleAvailability.UnnamedGpu,
                        GpuSelfTestVerdict.Describe(judged),
                        GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
                    outcome = GpuSelfTestOutcome.Fail;
                    return; // nothing was warmed on the path the app will use next.
                }
                else
                {
                    judgement = judged;
                }
            }

            if (gpuEvidenced)
            {
                // TRN-64 PR 2: the speed floor — two more timed clip decodes, each capped at the
                // floor. A Slower verdict ends the run without marking, like a Fail; the current
                // session keeps decoding on the GPU (the text is right), the pin applies next start.
                var gpuName = WhisperBackendLog.ObservedGpuName;
                // The floor phase has its OWN deadline (self-review, concurrency lens): the slot is
                // already claimed, so the watchdog can no longer record Inconclusive for a sample
                // that wedges — this bound does, and the orphaned decode keeps the model lock exactly
                // as a wedged compile decode would (the restart the refusal offers is the way out).
                var floorTask = TimeWhisperFloorAsync(service, clip!, cts);
                var floorBudget = WhisperSelfTestBudgetOverride ?? GpuSpeedFloor.PhaseBudget(WhisperSpeedFloor);
                if (await Task.WhenAny(floorTask, Task.Delay(floorBudget, cts.Token)).ConfigureAwait(false) != floorTask)
                {
                    cts.Token.ThrowIfCancellationRequested(); // the run was cancelled, not wedged
                    var persisted = RecordWhisperVerdict(config, model, GpuSelfTestOutcome.Inconclusive, gpuName);
                    Logger.Write(GpuSelfTestReport.LevelFor(persisted),
                        "Whisper GPU self-test: the speed floor phase did not finish within {Seconds:F0}s on {GpuName} - Whisper refuses the graphics card for this session and starts on the CPU next time (TRN-64) [display drivers on this PC: {DriverSignature}]",
                        floorBudget.TotalSeconds,
                        gpuName ?? GpuToggleAvailability.UnnamedGpu,
                        GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
                    TryCancel(cts, "Whisper GPU self-test abort callback failed");
                    outcome = GpuSelfTestOutcome.Inconclusive;
                    return;
                }
                var floorVerdict = await floorTask.ConfigureAwait(false);
                if (floorVerdict == GpuSpeedFloorVerdict.NotMeasured)
                {
                    // The processor vanished mid-sample (a reload is under way): nothing to record and
                    // nothing to mark — a Pass judged before an UNMEASURED floor is never persisted
                    // (the design's "Pass only after the floor"; self-review A3).
                    decodedNothing = true;
                    return;
                }
                if (floorVerdict == GpuSpeedFloorVerdict.Slower)
                {
                    RecordWhisperVerdict(config, model, GpuSelfTestOutcome.Slower, gpuName);
                    // Warning, never Error: a correct decode that is merely slow is not the driver
                    // contract drift REL-30 reports; the pin and the row's sentence are the response.
                    Logger.Warning("Whisper GPU self-test: SLOWER than the speed floor on {GpuName} - Whisper uses the processor from the next start of VoiceWink (TRN-64) [display drivers on this PC: {DriverSignature}]",
                        gpuName ?? GpuToggleAvailability.UnnamedGpu,
                        GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
                    outcome = GpuSelfTestOutcome.Slower;
                    return; // nothing was warmed on the path the app will use next.
                }
                if (judgement is { Outcome: GpuSelfTestOutcome.Pass } passed)
                {
                    RecordWhisperVerdict(config, model, GpuSelfTestOutcome.Pass, gpuName);
                    Logger.Information("Whisper GPU self-test passed on {GpuName}: {Judgement}",
                        gpuName ?? GpuToggleAvailability.UnnamedGpu, GpuSelfTestVerdict.Describe(passed));
                    outcome = GpuSelfTestOutcome.Pass;
                }
            }

            config.Marker.MarkWhisperWarmed();
            Logger.Information("Whisper GPU warm-up complete");
        }
        catch (OperationCanceledException)
        {
            // A selection, a reload, shutdown, or the watchdog's abort — by design. A verdict the
            // watchdog recorded stands; otherwise nothing was learned and the slot is CANCELLED.
            decodedNothing = true;
        }
        catch (Exception ex)
        {
            // Fail-soft: a failed warm-up must never surface anywhere but this line. It RAN and
            // threw — the real decode reports its own failure through REL-12 — so it is not re-run.
            Logger.Warning(ex, "Whisper GPU warm-up failed - first GPU dictation will pay the compile instead");
            RecordWhisperSessionRan(model);
        }
        finally
        {
            watchdog?.Cancel();
            watchdog?.Dispose();
            if (verdict is not null)
            {
                if (!claimed)
                {
                    claimed = verdict.TryClaim(); // false: the watchdog owns it and its Inconclusive stands
                }
                if (claimed)
                {
                    if (decodedNothing)
                    {
                        verdict.SetCanceled();
                    }
                    else
                    {
                        verdict.SetResult(outcome);
                    }
                }
            }
            NotifyChanged(); // UI-12: the row's "Checking…" ends with the run, whatever ended it
        }
    }

    /// <summary>TRN-64 PR 2: the two timed clip decodes behind <see cref="GpuSpeedFloor"/>. Each is
    /// timed from the decode CALL — the stopwatch starts in <c>onDecodeStarting</c>, under the model
    /// lock, so a concurrent load's lock wait never inflates a sample — and CAPPED at the floor by a
    /// token armed at the same instant; a capped sample reads <see cref="GpuSpeedFloor.Capped"/> and counts as over the floor. The
    /// run's own cancellation propagates. The floor is over whisper.cpp's 30 s encoder window
    /// (<see cref="GpuSpeedFloor.WhisperWindowSamples"/>) — the work the encoder actually does for
    /// ANY input, the clip included (self-review, fail-open lens). Every sample is logged with its
    /// rate: that line is the re-open trigger for the dropped GPU-vs-CPU A/B (the TRN-64 card).</summary>
    private async Task<GpuSpeedFloorVerdict> TimeWhisperFloorAsync(WhisperTranscriptionService service, GpuSelfTestClip clip, CancellationTokenSource runCts)
    {
        var run = runCts.Token;
        var work = GpuSpeedFloor.WhisperWindowSamples;
        var floor = WhisperSpeedFloor;
        var samples = new TimeSpan[GpuSpeedFloor.SampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            using var cap = CancellationTokenSource.CreateLinkedTokenSource(run);
            var stopwatch = new global::System.Diagnostics.Stopwatch();
            try
            {
                var decoded = await service.WarmUpDecodeAsync(
                        clip.Samples,
                        cap.Token,
                        onNoProcessor: () => ReleaseWhisperQueue(runCts), // same latch rule as the compile decode (self-review A4)
                        onDecodeStarting: () =>
                        {
                            stopwatch.Start();
                            cap.CancelAfter(floor);
                        })
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (decoded is null)
                {
                    return GpuSpeedFloorVerdict.NotMeasured; // the processor is gone: nothing to time
                }
                samples[i] = stopwatch.Elapsed;
            }
            catch (OperationCanceledException) when (!run.IsCancellationRequested)
            {
                samples[i] = GpuSpeedFloor.Capped; // cut at the floor: counts as over it
            }
            catch (Exception ex)
            {
                // Kimi diff r1 A1: any OTHER throw used to fault the run into the generic handler,
                // which completes the slot Unknown — the gate PROCEEDS and the session is marked as
                // having run. The Parakeet leg treats a failed sample as NotMeasured (no verdict,
                // the next attempt re-tests) and GpuSpeedFloorVerdict documents that contract; the
                // two legs now agree.
                Logger.Information(ex, "Whisper GPU speed floor: sample decode failed - no verdict");
                return GpuSpeedFloorVerdict.NotMeasured;
            }
            Logger.Information("Whisper GPU speed floor sample {Index}/{Count}: {Sample} - floor {FloorMs} ms for whisper.cpp's {WindowSeconds:F0} s window (the clip is {ClipSeconds:F2} s) (TRN-64)",
                i + 1, samples.Length, GpuSpeedFloor.Describe(samples[i], work, floor),
                (int)floor.TotalMilliseconds, (double)work / GpuSelfTestClip.SampleRate, (double)clip.Samples.Length / GpuSelfTestClip.SampleRate);
        }
        return GpuSpeedFloor.Judge(samples, floor);
    }

    /// <summary>The queued run found no processor: release ITS queue slot (only if it is still
    /// the current run) so the reload that removed the processor can queue again.</summary>
    private void ReleaseWhisperQueue(CancellationTokenSource run)
    {
        lock (_whisperLock)
        {
            if (ReferenceEquals(_whisperCts, run))
            {
                _whisperQueuedModel = null;
            }
        }
    }

    /// <summary>TRN-64: the deadline supervisor (Codex r2 F3, Kimi r2 C5) — independent of the
    /// decode task, so a native decode that never returns still produces a verdict. It CLAIMS the
    /// slot before writing anything — a run returning inside its write window loses and records
    /// nothing (self-review, concurrency lens). On expiry
    /// with a Vulkan device observed it records <see cref="GpuSelfTestOutcome.Inconclusive"/> (in
    /// memory first, then the marker's map AND engine pin), completes the gate, and asks
    /// whisper.cpp's abort path to stop the decode — which a wedged driver may not honour; the
    /// hung decode keeps the model lock, the pre-existing hung-driver shape, and the restart the
    /// refusal offers is the way out. With no device observed the process computes on the CPU, so
    /// the expiry is not a GPU fact: the gate is released with Unknown and nothing is pinned.</summary>
    internal CancellationTokenSource StartWhisperWatchdog(
        Configuration config, string? model, CancellationTokenSource run, WhisperVerdictSlot verdict)
    {
        var budget = WhisperSelfTestBudgetOverride ?? WhisperSelfTestBudget;
        var watchdog = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(budget, watchdog.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // the run ended first
            }
            if (!verdict.TryClaim())
            {
                return; // the run finished first and owns the verdict
            }
            if (WhisperBackendLog.ObservedVulkanDevices < 1)
            {
                Logger.Warning("Whisper GPU self-test did not finish within {Seconds:F0}s with no Vulkan device observed - not a GPU verdict, nothing pinned (TRN-64)",
                    budget.TotalSeconds);
                RecordWhisperSessionRan(model); // it ran (on the CPU) past the budget: not re-run before every dictation
                verdict.SetResult(GpuSelfTestOutcome.Unknown);
            }
            else
            {
                var gpuName = WhisperBackendLog.ObservedGpuName;
                var persisted = RecordWhisperVerdict(config, model, GpuSelfTestOutcome.Inconclusive, gpuName);
                Logger.Write(GpuSelfTestReport.LevelFor(persisted),
                    "Whisper GPU self-test did not finish within {Seconds:F0}s on {GpuName} - Whisper refuses the graphics card for this session and starts on the CPU next time (TRN-64) [display drivers on this PC: {DriverSignature}]",
                    budget.TotalSeconds,
                    gpuName ?? GpuToggleAvailability.UnnamedGpu,
                    GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
                verdict.SetResult(GpuSelfTestOutcome.Inconclusive);
            }
            TryCancel(run, "Whisper GPU self-test abort callback failed");
            NotifyChanged();
        });
        return watchdog;
    }

    /// <summary>Record a Whisper verdict: in memory FIRST (authoritative for this session — a
    /// failed marker write must not let this process decode on a GPU that just failed, Codex r2
    /// F3), then the marker (per model + the sticky engine pin). Returns the marker's durability.</summary>
    /// <summary>Test seam over <see cref="RecordWhisperVerdict"/>: the session pin is otherwise
    /// only reachable through a real run, so the row's overlay rules had no way to be exercised
    /// (Kimi verification r2). Never called outside tests.</summary>
    internal bool RecordWhisperVerdictForTest(string? model, GpuSelfTestOutcome outcome, string? gpuName)
        => _config is { } config && RecordWhisperVerdict(config, model, outcome, gpuName);

    private bool RecordWhisperVerdict(Configuration config, string? model, GpuSelfTestOutcome outcome, string? gpuName)
    {
        lock (_whisperLock)
        {
            if (model is not null)
            {
                _whisperSessionVerdicts[model] = outcome;
            }
            if (GpuWarmupMarker.PinsCpu(outcome)
                && !(outcome == GpuSelfTestOutcome.Slower && _whisperSessionPin is { } held && GpuWarmupMarker.RefusesDecode(held)))
            {
                _whisperSessionPin = outcome; // a Slower never downgrades a refusing pin (PR 2)
            }
        }
        return config.Marker.RecordWhisperVerdict(model, outcome, gpuName);
    }

    /// <summary>TRN-64 (self-review, fail-open lens): this session DECODED the clip for
    /// <paramref name="model"/> on its current processor. Kept as Unknown when nothing stronger is
    /// known, so the model is not re-tested before every dictation in a build with no clip, on a
    /// pinned language, or with no device; a verdict already recorded stands. Never persisted —
    /// the marker learns nothing — and cleared only by a rebuilt processor (see
    /// <see cref="QueueWhisperWarmup"/>). Internal for the test that pins it.</summary>
    internal void RecordWhisperSessionRan(string? model)
    {
        if (model is null)
        {
            return;
        }
        lock (_whisperLock)
        {
            _whisperSessionVerdicts.TryAdd(model, GpuSelfTestOutcome.Unknown);
        }
    }

    // ---- TRN-64: the gate the first real Whisper decode consults ----

    /// <summary>TRN-64 (self-review, fail-open lens): is a self-test for <paramref name="model"/>
    /// still owed on a GPU-engaged process, with nothing this session has learned about it? The
    /// gate queues one when this is true and no run is pending — a load's queue can be cancelled
    /// and nothing then re-queues it, and a decode must never run on a GPU nothing checked. False
    /// when not GPU-engaged (nothing to test), when the marker holds a verdict or the pin, or once
    /// this session ran the test for this model, whatever it could conclude.</summary>
    internal bool WhisperSelfTestOwed(string model)
    {
        if (_config is not { } config || !config.WhisperVulkanUsable || WhisperBackendLog.ObservedVulkanDevices < 1)
        {
            return false;
        }
        if (GpuWarmupMarker.ValidModelName(model) is null || SessionKnows(model))
        {
            return false;
        }
        return config.Marker.WhisperNeedsTest(model);
    }

    /// <summary>The verdict task a decode on <paramref name="model"/> must await before it may run,
    /// or null when nothing is pending for that model (no run queued, a run for another model, or
    /// a verdict already reached — read it through <see cref="WhisperGateOutcome"/>).</summary>
    internal Task<GpuSelfTestOutcome>? WhisperVerdictTask(string model)
    {
        lock (_whisperLock)
        {
            return _whisperVerdict is { } verdict
                   && !verdict.Task.IsCompleted
                   && string.Equals(_whisperQueuedModel, model, StringComparison.OrdinalIgnoreCase)
                ? verdict.Task
                : null;
        }
    }

    /// <summary>What a decode on <paramref name="model"/> must respect right now. Unknown when the
    /// process is not GPU-engaged for Whisper (unconfigured, Vulkan unusable, or ggml enumerated no
    /// device — a CPU decode needs no gate) or nothing is known. Precedence (PR 2, self-review): a
    /// REFUSING session pin covers every model; then the MODEL's own verdict (session, then marker);
    /// then a refusing marker pin; then a Slower pin, which the gate reads as proceed — the model's
    /// own Fail always outranks another model's Slower. <see cref="GpuSelfTestOutcome.Fail"/> and
    /// <see cref="GpuSelfTestOutcome.Inconclusive"/> mean REFUSE; Pass, Slower and Unknown mean proceed.</summary>
    internal GpuSelfTestOutcome WhisperGateOutcome(string model)
    {
        if (_config is not { } config || !config.WhisperVulkanUsable)
        {
            return GpuSelfTestOutcome.Unknown;
        }
        if (WhisperBackendLog.ObservedVulkanDevices < 1)
        {
            return GpuSelfTestOutcome.Unknown;
        }
        GpuSelfTestOutcome? slowerPin = null;
        lock (_whisperLock)
        {
            if (_whisperSessionPin is { } pin)
            {
                if (GpuWarmupMarker.RefusesDecode(pin))
                {
                    return pin;
                }
                slowerPin = pin; // Slower: the model's OWN verdict decides first
            }
            if (_whisperSessionVerdicts.TryGetValue(model, out var known) && known != GpuSelfTestOutcome.Unknown)
            {
                return known; // Unknown here means "ran, nothing concluded" — the marker decides
            }
        }
        var engine = config.Marker.ReadVerdict(GpuSelfTestEngine.Whisper);
        if (engine.RefusesDecode)
        {
            return engine.Outcome;
        }
        var recorded = config.Marker.WhisperModelVerdict(model);
        if (recorded != GpuSelfTestOutcome.Unknown)
        {
            return recorded;
        }
        return slowerPin ?? (engine.PinsCpu ? engine.Outcome : GpuSelfTestOutcome.Unknown);
    }


    /// <summary>Kick the Parakeet warm-up (composition root, after startup settles). Spawns a
    /// THROWAWAY server child on its own ephemeral port — never the resident one, whose queue a
    /// real dictation may need (codex plan round, Blocker 1) — decodes two shapes through it (the
    /// short 4 s shape, which is the golden clip padded to 4 s when bundled and 4 s of silence
    /// otherwise, then the 35 s chunk cap: the compile is per pipeline SHAPE, measured — a 27.5 s
    /// clip cost 5,964 ms on first exposure after shorter clips were warm), judges the clip's
    /// words, and kills it. Called SYNCHRONOUSLY on the startup path
    /// (<c>App.StartGatedRuntimeServices</c>) so the latch precedes the startup preload's quiesce
    /// by construction — it must therefore never throw and never block: the marker read is
    /// fail-soft, and the run itself goes to its own <c>Task.Run</c>.</summary>
    public void QueueParakeetWarmup()
    {
        var config = _config;
        if (config?.Parakeet is not { } plan || _cts.IsCancellationRequested)
        {
            return;
        }
        if (!config.Marker.ParakeetNeedsWarmup())
        {
            return;
        }
        lock (_parakeetLock)
        {
            if (_parakeetQueued != 0)
            {
                return;
            }
            var resolveClip = config.ResolveClip;
            _parakeetTask = Task.Run(() => RunParakeetWarmupAsync(
                config.Marker, plan, _cts.Token, clip: resolveClip?.Invoke()));
            _parakeetQueued = 1;
        }
        // UI-12: "Checking…" BEGINS with the run — a Models page already open at startup would
        // otherwise miss the whole Parakeet run and only see its end (Kimi diff r2). Outside the
        // lock: subscribers marshal and may read IsSelfTestRunning, which takes it.
        NotifyChanged();
    }

    /// <param name="healthBudgetOverride">Test seam only: the health budget is 30 s wall-clock
    /// (<see cref="ParakeetServerPolicy.HealthBudget"/>), and the never-healthy skip branch is
    /// untestable at that cost. Production callers pass nothing.</param>
    /// <param name="clip">The golden clip, or null for the TRN-49 silence-only warm-up.</param>
    internal async Task RunParakeetWarmupAsync(
        GpuWarmupMarker marker, ParakeetPlan plan, CancellationToken ct,
        TimeSpan? healthBudgetOverride = null, GpuSelfTestClip? clip = null)
    {
        try
        {
            await RunParakeetWarmupCoreAsync(marker, plan, ct, healthBudgetOverride, clip).ConfigureAwait(false);
        }
        finally
        {
            // UI-12: the row's "Checking…" ends with the run, whatever ended it. Note the queued
            // task completes only after this returns, so a subscriber reading IsSelfTestRunning
            // synchronously inside the event still sees Running; the page marshals to its
            // dispatcher first, which is after.
            NotifyChanged();
        }
    }

    private async Task RunParakeetWarmupCoreAsync(
        GpuWarmupMarker marker, ParakeetPlan plan, CancellationToken ct,
        TimeSpan? healthBudgetOverride, GpuSelfTestClip? clip)
    {
        var ggufPath = plan.ResolveGgufPath();
        if (ggufPath is null)
        {
            return; // no eligible model installed — nothing to warm, nothing to log.
        }

        var result = await ParakeetEphemeralChild.RunAsync(
            "parakeet GPU warm-up",
            plan.Launcher, plan.Client, plan.ExePath, plan.ExpectedExeSha256, ggufPath,
            ParakeetLaunchMode.Auto,
            (child, baseUri, token) => DecodeWarmShapesAsync(marker, plan, clip, child, baseUri, token),
            ct,
            healthBudgetOverride).ConfigureAwait(false);

        if (result.UnconfirmedChild is { } lingering)
        {
            // TRN-57: exit NOT confirmed. Keep the handle — and the mark, if the decodes earned it
            // — for a quiesce to observe; dispose nothing, mark nothing. The quiesce reports
            // Unconfirmed until it sees the exit, and the resident spawn waits for the next
            // preparation.
            lock (_parakeetLock)
            {
                _lingering = new LingeringChild(lingering, result.BodySucceeded ? marker : null);
            }
            Logger.Warning("parakeet GPU warm-up: retained the unconfirmed warm child for a later observation; marker deferred");
            return;
        }

        // NOT marked before the CONFIRMED exit (Codex final check, TRN-57) — an unconfirmed
        // teardown must never suppress the next launch's warm-up. The helper confirmed it.
        if (result.BodySucceeded)
        {
            marker.MarkParakeetWarmed();
            Logger.Information("Parakeet GPU warm-up complete");
        }
    }

    /// <summary>The warm-up's body against a healthy throwaway child: the short shape, the
    /// correctness verdict, the speed floor (TRN-64 PR 2), then the 35 s shape. Returns whether the
    /// warm-up EARNED its mark — every decode succeeded and no GPU-confirmed FAIL or SLOWER was
    /// recorded.</summary>
    private async Task<bool> DecodeWarmShapesAsync(
        GpuWarmupMarker marker, ParakeetPlan plan, GpuSelfTestClip? clip,
        IParakeetServerChild child, Uri baseUri, CancellationToken ct)
    {
        // Short shape first (cheap, covers the common dictation) — the golden clip zero-padded to
        // EXACTLY the 4 s shape, so the judged decode compiles the same pipeline the first short
        // dictation will use (Codex plan round, Blocker 3) — then the 35 s chunk-cap shape. The
        // verdicts are taken BETWEEN the two: a FAIL or a SLOWER ends the run before the second
        // compile, which would warm a device the app is about to stop using.
        var shortShape = clip?.PaddedForParakeet() ?? new float[16000 * 4];
        var clipText = await DecodeOneShapeAsync(plan, baseUri, shortShape, ct).ConfigureAwait(false);
        if (clipText is null)
        {
            return false;
        }

        if (clip is not null)
        {
            var judged = JudgeClip(marker, plan, clip, child, clipText);
            if (judged is { Outcome: GpuSelfTestOutcome.Fail })
            {
                return false;
            }
            if (judged is { } evidenced)
            {
                // TRN-64 PR 2: the speed floor — on GPU evidence only (a CPU-served child's pace is
                // not a GPU fact), BEFORE the 35 s shape and before the Pass is recorded.
                var gpuName = child.ObservedGpuName;
                var floorVerdict = await TimeParakeetFloorAsync(plan, baseUri, shortShape, ct).ConfigureAwait(false);
                switch (floorVerdict)
                {
                    case GpuSpeedFloorVerdict.Slower:
                        marker.RecordVerdict(GpuSelfTestEngine.Parakeet, GpuSelfTestOutcome.Slower, gpuName);
                        // Warning, never Error: a correct decode that is merely slow is not the
                        // driver contract drift REL-30 reports; the CPU request and the row's
                        // sentence are the response.
                        Logger.Warning("Parakeet GPU self-test: SLOWER than the speed floor on {GpuName} - requesting CPU for this session and app version (TRN-64) [display drivers on this PC: {DriverSignature}]",
                            gpuName ?? GpuToggleAvailability.UnnamedGpu,
                            GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
                        plan.RequestCpu?.Invoke("GPU self-test: slower than the speed floor on the golden clip (TRN-64)");
                        return false;
                    case GpuSpeedFloorVerdict.NotMeasured:
                        return false; // a sample decode failed (already logged): no verdict, no mark
                }
                if (evidenced.Outcome == GpuSelfTestOutcome.Pass)
                {
                    marker.RecordVerdict(GpuSelfTestEngine.Parakeet, GpuSelfTestOutcome.Pass, gpuName);
                    Logger.Information("Parakeet GPU self-test passed on {GpuName}: {Judgement}",
                        gpuName ?? GpuToggleAvailability.UnnamedGpu, GpuSelfTestVerdict.Describe(evidenced));
                }
            }
        }

        return await DecodeOneShapeAsync(plan, baseUri, new float[16000 * 35], ct).ConfigureAwait(false) is not null;
    }

    /// <summary>One bounded warm decode; the transcript on success, null on any skip (already
    /// logged). Null, not empty: an empty transcript is a SUCCESSFUL decode of silence.</summary>
    private static async Task<string?> DecodeOneShapeAsync(
        ParakeetPlan plan, Uri baseUri, float[] samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ParakeetServerDecodeResult result;
        using (var decode = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            decode.CancelAfter(WarmDecodeBudget);
            try
            {
                result = await plan.Client.DecodeAsync(baseUri, samples, 16000, decode.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Logger.Information("parakeet GPU warm-up: decode exceeded {Seconds}s - skipped",
                    (int)WarmDecodeBudget.TotalSeconds);
                return null;
            }
        }
        if (!result.Success)
        {
            Logger.Information("parakeet GPU warm-up: decode failed ({Class}) - skipped", result.FailureClass);
            return null;
        }
        return result.Text;
    }

    /// <summary>TRN-64 PR 2: the two timed short-shape decodes behind <see cref="GpuSpeedFloor"/>
    /// against the throwaway child. Each is timed around the whole call (a loopback round trip adds
    /// nothing a floor of seconds would notice) and CAPPED at the floor by a linked token; a capped
    /// sample reads <see cref="GpuSpeedFloor.Capped"/> and counts as over the floor. A failed sample decode is NotMeasured — no
    /// verdict, like any skipped warm decode. Every sample is logged with its rate (the dropped
    /// A/B's re-open trigger, TRN-64 card).</summary>
    private async Task<GpuSpeedFloorVerdict> TimeParakeetFloorAsync(ParakeetPlan plan, Uri baseUri, float[] shape, CancellationToken ct)
    {
        var floor = SpeedFloorFor(shape.Length);
        var samples = new TimeSpan[GpuSpeedFloor.SampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cap.CancelAfter(floor);
            var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await plan.Client.DecodeAsync(baseUri, shape, 16000, cap.Token).ConfigureAwait(false);
                stopwatch.Stop();
                if (!result.Success)
                {
                    Logger.Information("Parakeet GPU speed floor: sample decode failed ({Class}) - no verdict", result.FailureClass);
                    return GpuSpeedFloorVerdict.NotMeasured;
                }
                samples[i] = stopwatch.Elapsed;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                samples[i] = GpuSpeedFloor.Capped; // cut at the floor: counts as over it
            }
            Logger.Information("Parakeet GPU speed floor sample {Index}/{Count}: {Sample} - floor {FloorMs} ms for {AudioSeconds:F2} s of audio (TRN-64)",
                i + 1, samples.Length, GpuSpeedFloor.Describe(samples[i], shape.Length, floor),
                (int)floor.TotalMilliseconds, (double)shape.Length / 16000);
        }
        return GpuSpeedFloor.Judge(samples, floor);
    }

    /// <summary>TRN-50: the correctness judgement — on POSITIVE GPU evidence only (the launch mode
    /// was Auto, which is a request; the child's own selection line is the fact). Null when the
    /// child did not prove a GPU: nothing is recorded and the warm-up keeps its TRN-49 meaning. A
    /// FAIL is persisted, reported and CPU-requested HERE; a Pass is handed back UNRECORDED — the
    /// caller records it only after the speed floor (TRN-64 PR 2), so a correct-but-slow GPU never
    /// records Pass. The decoded words are never logged.</summary>
    private static GpuSelfTestJudgement? JudgeClip(
        GpuWarmupMarker marker, ParakeetPlan plan, GpuSelfTestClip clip, IParakeetServerChild child, string clipText)
    {
        var judgement = GpuSelfTestVerdict.Judge(clipText, clip.ExpectedWords);
        var backend = child.ObservedBackend;
        if (!ParakeetGpuEvidence.IsGpu(backend))
        {
            Logger.Information("Parakeet GPU self-test decoded on {Backend}: not a GPU verdict ({Judgement})",
                LogValueSanitizer.SingleLine(backend ?? "no device line"), GpuSelfTestVerdict.Describe(judgement));
            return null;
        }
        if (judgement.Outcome == GpuSelfTestOutcome.Fail)
        {
            // Persist first (a FAIL also clears the warmed flag), then request CPU — a
            // non-blocking intent the next resident acquire consumes; this session's
            // remaining spawns are Cpu, and the next start of this app version pins it
            // pre-boot.
            var gpuName = child.ObservedGpuName;
            var persisted = marker.RecordVerdict(GpuSelfTestEngine.Parakeet, GpuSelfTestOutcome.Fail, gpuName);
            // REL-30: Error only once the verdict is durable — see GpuSelfTestReport.
            Logger.Write(GpuSelfTestReport.LevelFor(persisted),
                "Parakeet GPU self-test FAILED on {GpuName}: {Judgement} - requesting CPU for this session and app version (TRN-50) [display drivers on this PC: {DriverSignature}]",
                gpuName ?? GpuToggleAvailability.UnnamedGpu,
                GpuSelfTestVerdict.Describe(judgement),
                GpuSelfTestReport.DriverOrUnknown(GpuWarmupMarker.CurrentDriverSignature()));
            plan.RequestCpu?.Invoke("GPU self-test failed on the golden clip (TRN-50)");
        }
        return judgement;
    }
}

/// <summary>Why a warm-up was cancelled — the one word the log line carries (TRN-57). Until it
/// existed every cancel read "by recording admission", which is how a quiesce cancelling the
/// warm-up 3 ms after startup hid for a day.</summary>
internal enum GpuWarmupCancelReason
{
    RecordingAdmission,
    AudioTranscribe,
    ModelSelection,
    LanguageReload,
    Shutdown,
    /// <summary>The resident-spawn quiesce let the warm-up run for its grace and then cancelled.</summary>
    ResidentSpawnGraceExpired,
    /// <summary>The live transcription acquire's zero-grace quiesce (admission normally cancelled first).</summary>
    TranscriptionAcquire,
    ModelDelete,
}

/// <summary>What <see cref="GpuWarmup.WaitForParakeetQuiesceAsync"/> found. Callers branch on
/// <see cref="Unconfirmed"/> only — the one outcome under which a warm child may still be alive
/// and a resident Auto child must not be spawned.</summary>
internal enum ParakeetQuiesceOutcome
{
    /// <summary>Nothing was queued and nothing lingers.</summary>
    Idle,
    /// <summary>The warm-up ran to its end on its own (its decodes may or may not have succeeded)
    /// and the child's exit is confirmed.</summary>
    Completed,
    /// <summary>The warm-up was cancelled (here or earlier) and the child's exit is confirmed.</summary>
    Cancelled,
    /// <summary>A warm child may still be alive: do not spawn; the next quiesce re-observes.</summary>
    Unconfirmed,
}
