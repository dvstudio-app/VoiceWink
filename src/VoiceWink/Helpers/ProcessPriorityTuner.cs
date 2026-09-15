using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Startup helper + watchdog: pins this process's priority class back to
/// <see cref="ProcessPriorityClass.Normal"/> and opts out of Windows 11's
/// automatic Efficiency Mode / EcoQoS. <see cref="Start()"/> additionally
/// installs a periodic re-pin watchdog because the startup-only set isn't
/// sufficient — Windows demotes the process to <c>IDLE_PRIORITY_CLASS</c>
/// some seconds AFTER our Apply() call lands (likely after the wscript
/// launcher exits and the dotnet host is left as a long-running background
/// process). EcoQoS opt-out does not block the priority-class demotion.
///
/// <para><b>REL-14 (2026-08-05): the watchdog runs on its own thread, not the
/// ThreadPool.</b> It was a <see cref="Timer"/>, whose callbacks are ThreadPool
/// work — inside the very process the demotion starves. Two field incidents
/// (2026-07-24, 2026-08-05) recorded boot-time launches hanging 4 min 11 s and
/// 2 min 25 s, each ending the instant the re-pin finally landed, with the whole
/// remaining startup completing in single-digit seconds afterwards. The loop now
/// lives in <see cref="PriorityWatchdog"/> on a dedicated thread raised to
/// <c>THREAD_PRIORITY_TIME_CRITICAL</c>; read its class comment before changing
/// anything about it, especially the "nothing but bounded syscalls at priority 15"
/// invariant.</para>
///
/// <para><b>Why this matters.</b> VoiceWink uses a low-level keyboard hook
/// (<c>WH_KEYBOARD_LL</c>) for its global hotkey. Windows synchronously calls
/// every hooked process for every keystroke and requires the callback to
/// return within <c>LowLevelHooksTimeout</c> (registry default 300&#160;ms);
/// if not, it silently removes the hook from the chain. When Windows places
/// the dotnet host into <c>IDLE_PRIORITY_CLASS</c>, any Normal-priority CPU
/// hog (Excel auto-calc, a build, Teams in a call) starves the hook callback
/// thread of scheduling slices — the AboveNormal thread boost in
/// <see cref="VoiceWink.Services.Input.HotkeyService"/> isn't enough because
/// thread priority is relative to process class (Idle+AboveNormal still
/// computes to a lower effective base priority than Normal+Normal). The
/// deadline is missed, the hook gets removed, and the hotkey goes dead until
/// <c>HotkeyService</c>'s watchdog restarts it 60-75&#160;s later.</para>
///
/// <para>Observed live in the 2026-05-23 session log: dotnet host at
/// PriorityClass=Idle, hook silent 60-825&#160;s while Windows kept observing
/// keyboard input, multiple watchdog-driven hook restarts per hour. Live
/// retest after the first round of this fix confirmed Windows re-demotes
/// within ~30&#160;s even with unconditional <c>SetPriorityClass(Normal)</c>
/// at startup, motivating the periodic re-pin.</para>
///
/// <para>The helper is idempotent and safe to call from any thread. It logs
/// the before/after state once and never throws — failures are best-effort
/// warnings (the call site is the entry point of the app; we don't want a
/// startup hiccup to take the whole app down).</para>
/// </summary>
internal static class ProcessPriorityTuner
{
    private static ILogger Logger => Log.ForContext(typeof(ProcessPriorityTuner));

    /// <summary>
    /// Re-pin period (steady state). Cheap — each tick is one P/Invoke
    /// <c>GetPriorityClass</c> read and (rarely) a <c>SetPriorityClass</c>.
    /// 5s is short enough that even if Windows demotes us, we recover before
    /// <c>HotkeyService</c>'s watchdog (which needs ≥60s of hook silence to
    /// fire) decides the hook is dead.
    /// </summary>
    internal static readonly TimeSpan RepinInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initial delay before the first re-pin check. Windows applies its
    /// background-process demotion ~1-2s after the wscript launcher exits;
    /// firing at T+2s reliably catches that demotion before it has time to
    /// matter. (Live diagnosis 2026-05-23: original 30s due-time meant a
    /// full hook timeout could happen before we recovered the priority,
    /// causing one hook restart per app launch.)
    /// </summary>
    internal static readonly TimeSpan RepinFirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long <see cref="Stop"/> waits for the worker to leave its loop.
    /// </summary>
    internal static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Rooted reference so the worker isn't collected while the app runs.
    /// Internal for the lifetime test to verify <see cref="Start()"/> installed it.
    /// </summary>
    internal static Thread? _repinThread;

    /// <summary>
    /// The normal-priority thread that writes the watchdog's diagnostics. Separate because
    /// the watchdog itself must never perform I/O — see <see cref="PriorityDiagnosticChannel"/>.
    /// </summary>
    private static Thread? _pumpThread;

    /// <summary>
    /// Test-visible view of <see cref="_pumpThread"/>, so the unexpected-exit test can assert
    /// that a later stop really did reclaim the pump rather than reporting success past it.
    /// </summary>
    internal static Thread? _pumpThreadForTests => _pumpThread;

    private static PriorityDiagnosticChannel? _channel;

    private static ManualResetEvent? _stopEvent;

    /// <summary>
    /// Serializes the whole install/teardown transaction. A bare CAS was not enough: it
    /// flipped before <see cref="_repinThread"/> and <see cref="_stopEvent"/> were
    /// published, so a concurrent stop could see a null handle, clear the guard, and let
    /// a later start install a SECOND worker over the first one's fields (Codex diff
    /// review 2026-08-05). Both calls happen on the main thread today, so this closes a
    /// currently-unreachable race — cheap enough that proving it stays unreachable isn't
    /// worth the argument.
    /// </summary>
    private static readonly object _lifecycleLock = new();

    /// <summary>
    /// 1 = a worker is installed. NO-OP-on-second-call, unlike the timer this replaced
    /// (which disposed and replaced itself) — you cannot "replace" a running thread
    /// cleanly, and two workers are never wanted.
    /// </summary>
    private static int _started;

    /// <summary>
    /// Bumped per install so an exiting worker only ever reconciles its OWN generation
    /// and can never clear state belonging to a later one.
    /// </summary>
    private static int _generation;

    /// <summary>
    /// True once a stop was requested. Only affects whether the worker's exit is REPORTED
    /// as unexpected — never who cleans up.
    ///
    /// <para><b>Teardown has exactly one owner: the worker's exit hook.</b> Splitting it
    /// between the worker and <see cref="StopAndWait"/> produced two bugs in a row (a
    /// timed-out stop latching the guard forever, then a race when the worker exited
    /// between the join giving up and the stopper re-taking the lock). Since
    /// <c>Thread.Join</c> returning true means the worker's <c>finally</c> has already
    /// completed, a successful stop needs no cleanup of its own, and a timed-out stop can
    /// simply let the worker reconcile whenever it does finish.</para>
    /// </summary>
    private static bool _stopRequested;

    /// <summary>
    /// One-shot: pins priority to Normal and opts the process out of
    /// Efficiency Mode. Used by tests; production should call <see cref="Start"/>
    /// which additionally installs the periodic re-pin watchdog.
    /// </summary>
    public static void Apply()
    {
        TryRaisePriority();
        TryOptOutOfEfficiencyMode();
    }

    /// <summary>
    /// One-shot Apply() + periodic re-pin every <see cref="RepinInterval"/> on a
    /// dedicated TIME_CRITICAL thread. Call once at startup. Idempotent — a second call
    /// is a NO-OP (it does not replace the running worker).
    /// </summary>
    public static void Start() => Start(new Win32PriorityPlatform());

    /// <summary>
    /// Platform-injecting overload. Production calls <see cref="Start()"/>; tests pass a
    /// fake so the wiring assertions don't start a real TIME_CRITICAL thread in the
    /// test host.
    /// </summary>
    internal static void Start(IPriorityPlatform platform)
    {
        Apply();

        lock (_lifecycleLock)
        {
            if (_started != 0)
                return; // already installed — no-op

            // Reclaim a RETIRED generation before installing over it. After an unexpected
            // watchdog death the exit hook makes the tuner restartable, but that generation's
            // pump and handles are still owned — overwriting the fields here would leak them
            // and let a concurrent StopAndWait report success against a replacement worker
            // (Codex diff review round 5). Safe to join under the lock: the pump never takes
            // _lifecycleLock, and this generation's watchdog is already gone.
            ReclaimRetiredGeneration();

            _started = 1;
            _stopRequested = false;
            var generation = ++_generation;

            try
            {
                var stop = new ManualResetEvent(false);

                // The channel carries diagnostics to the pump thread; the watchdog itself
                // never logs. ManualResetEvent, deliberately, NOT ManualResetEventSlim:
                // Slim spins before falling back to a kernel wait, and spinning at priority
                // 15 burns exactly the CPU the starved process needs.
                var channel = new PriorityDiagnosticChannel(
                    (template, args) => Logger.Information(template, args),
                    (template, args) => Logger.Warning(template, args));

                var watchdog = new PriorityWatchdog(
                    platform,
                    channel,
                    (ex, message) => Logger.Warning(ex, message),
                    stop.WaitOne,
                    RepinFirstDelay,
                    RepinInterval);

                var pump = new Thread(() => channel.Run(stop))
                {
                    IsBackground = true,
                    Name = "VoiceWink.PriorityDiagnostics",
                };

                var thread = new Thread(() =>
                {
                    try
                    {
                        watchdog.Run();
                    }
                    finally
                    {
                        // Reconcile on ANY exit. Without this an unexpected termination
                        // left _started latched at 1: no watchdog, and every later Start()
                        // silently a no-op, with nothing saying so (Codex diff review).
                        OnWorkerExited(generation, watchdog.LeftAtNormalPriority);
                    }
                })
                {
                    IsBackground = true,
                    Name = "VoiceWink.PriorityWatchdog",
                };

                _stopEvent = stop;
                _channel = channel;
                _repinThread = thread;
                _pumpThread = pump;

                // Logged BEFORE either Start() deliberately: if the sink throws, the rollback
                // below must be able to dispose the stop event, which is only safe while no
                // thread can be waiting on it (Kimi diff review round 3).
                Logger.Information(
                    "Priority re-pin watchdog installed on a dedicated TIME_CRITICAL thread " +
                    "(first check in {First}s, then every {Interval}s); diagnostics on a " +
                    "separate normal-priority thread",
                    (int)RepinFirstDelay.TotalSeconds, (int)RepinInterval.TotalSeconds);

                pump.Start();
                thread.Start();
            }
            catch (Exception ex)
            {
                // Roll back so a later Start() can retry, rather than the app running for
                // its whole lifetime with no watchdog and no way to install one.
                _stopEvent?.Dispose();
                _channel?.Dispose();
                _stopEvent = null;
                _channel = null;
                _repinThread = null;
                _pumpThread = null;
                _started = 0;

                Logger.Warning(ex, "Failed to start the priority re-pin watchdog thread");
            }
        }
    }

    /// <summary>
    /// Retires the leftovers of a generation whose watchdog died unexpectedly: its pump is
    /// still waiting and its handles are still owned. Must be called under
    /// <see cref="_lifecycleLock"/> before publishing a new generation's fields.
    ///
    /// <para>A wedged pump is not allowed to block a fresh install: if it misses its bounded
    /// join, its handles are abandoned to finalization and the new generation proceeds. That
    /// is strictly better than refusing to re-arm the watchdog.</para>
    /// </summary>
    private static void ReclaimRetiredGeneration()
    {
        var pump = _pumpThread;
        var stop = _stopEvent;
        var channel = _channel;

        if (pump is null && stop is null && channel is null)
            return;

        try { stop?.Set(); }
        catch { /* already disposed */ }

        var pumpStopped = pump is null || pump.Join(StopJoinTimeout);

        _pumpThread = null;
        _stopEvent = null;
        _channel = null;

        if (!pumpStopped)
            return; // abandon the handles rather than dispose under a live waiter

        try { stop?.Dispose(); }
        catch { /* best-effort */ }

        try { channel?.Dispose(); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Runs on the worker as it leaves <see cref="PriorityWatchdog.Run"/> — the SINGLE
    /// owner of teardown. Reconciles its own generation on every exit path, orderly or not,
    /// so the tuner is always left re-startable.
    /// </summary>
    private static void OnWorkerExited(int generation, bool safeToLog)
    {
        var wasUnexpected = false;

        lock (_lifecycleLock)
        {
            if (generation != _generation)
                return; // superseded by a later install; not ours to reconcile

            wasUnexpected = !_stopRequested;

            // STATE is reconciled here (single owner, so a timed-out stop can never latch
            // the tuner un-startable). HANDLES are not: the pump thread may still be waiting
            // on the stop event, so only a stopper that has joined BOTH threads can prove
            // disposal is safe. If the watchdog died unexpectedly, signal the stop event so
            // the pump drains and exits too rather than idling forever.
            //
            // _pumpThread / _stopEvent / _channel are deliberately LEFT SET so a later
            // StopAndWait can still join the pump and dispose them — it keys its
            // nothing-installed check on both threads being absent, not just the watchdog.
            _repinThread = null;
            _started = 0;
            _stopRequested = false;

            if (wasUnexpected)
            {
                try { _stopEvent?.Set(); }
                catch { /* disposed during a concurrent teardown */ }
            }
        }

        // Logged OUTSIDE the lock, and contained: this runs on the worker as it dies, and a
        // throwing sink here would escape a background thread and take the process with it.
        // Skipped entirely unless the worker verifiably left TIME_CRITICAL — a Serilog write
        // at priority 15 is the one thing this design never does.
        if (!wasUnexpected || !safeToLog)
            return;

        try
        {
            Logger.Warning(
                "Priority re-pin watchdog terminated unexpectedly — the process is no longer " +
                "protected against priority demotion");
        }
        catch
        {
            // Nothing left to report it with.
        }
    }

    /// <summary>
    /// Stops the watchdog. Called from <c>App.Cleanup</c> so the worker cannot be
    /// mid-write while logging shuts down; the thread is also <c>IsBackground</c>, so
    /// this is teardown symmetry rather than a correctness requirement.
    /// </summary>
    public static void Stop() => StopAndWait(StopJoinTimeout);

    /// <summary>
    /// Signals the worker and waits up to <paramref name="joinTimeout"/> for it to exit.
    /// Returns whether it actually stopped.
    /// </summary>
    internal static bool StopAndWait(TimeSpan joinTimeout)
    {
        Thread? thread;
        Thread? pump;
        ManualResetEvent? stop;
        PriorityDiagnosticChannel? channel;

        lock (_lifecycleLock)
        {
            thread = _repinThread;
            pump = _pumpThread;
            stop = _stopEvent;
            channel = _channel;

            if (thread is null && pump is null)
            {
                // Nothing installed (or a failed Start already rolled back). Make sure the
                // guard is clear so a later Start() isn't silently a no-op.
                _started = 0;
                return true;
            }

            // NOTE the `&&` above, not `||`. After an unexpected watchdog death the exit hook
            // clears _repinThread but the PUMP is still alive, and returning success here left
            // App.Cleanup racing Log.CloseAndFlush() against a pump still draining — plus the
            // handles undisposed and the generation replaceable (Codex diff review round 4).
            _stopRequested = true;
            try { stop?.Set(); }
            catch { /* already disposed by a concurrent teardown */ }
        }

        // Join OUTSIDE the lock. OnWorkerExited takes the same lock, so joining while
        // holding it would deadlock the worker against its own teardown.
        //
        // State reconciliation belongs to the worker's exit hook either way. A timeout means
        // it will run whenever the worker finishes, and until then ownership stays claimed so
        // no rival worker can be installed.
        var watchdogStopped = thread is null || thread.Join(joinTimeout);
        var pumpStopped = pump is null || pump.Join(joinTimeout);

        if (!watchdogStopped || !pumpStopped)
        {
            // Do NOT log when the PUMP is what failed to stop. The likeliest reason a pump
            // misses its join is that it is wedged inside a synchronous Serilog write — and
            // logging here would queue behind that same sink, so the one warning we'd most
            // want is the one guaranteed to block App.Cleanup on shutdown (Codex diff review
            // round 5). The return value is the signal in that case; the process can abandon
            // the background sink and exit.
            if (pumpStopped)
            {
                Logger.Warning(
                    "Priority re-pin watchdog did not stop within {TimeoutMs}ms; leaving it running",
                    (int)joinTimeout.TotalMilliseconds);
            }

            return false;
        }

        // Both threads are provably gone, which is the ONLY point at which disposing the
        // shared handles is safe — hence disposal lives here and not in the exit hook. This
        // also reclaims the handles left behind by an unexpected watchdog death.
        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(_stopEvent, stop))
                return true; // a later install already owns these fields

            _stopEvent = null;
            _channel = null;
            _pumpThread = null;
            _repinThread = null;
            _started = 0;
            _stopRequested = false;
        }

        stop?.Dispose();
        channel?.Dispose();
        return true;
    }

    private static void TryRaisePriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = process.PriorityClass;

            // If a developer (or Task Manager → Set priority) deliberately put us
            // ABOVE Normal, leave it alone — bumping down would be a regression.
            if (before == ProcessPriorityClass.AboveNormal ||
                before == ProcessPriorityClass.High ||
                before == ProcessPriorityClass.RealTime)
            {
                Logger.Debug("Process priority already {Priority}; no change", before);
                return;
            }

            // Pin to Normal unconditionally even when the snapshot reads Normal.
            // Live observation: at Apply() time during startup the read can show
            // Normal but Windows still demotes the process to Idle a moment
            // later (likely after wscript host exits and the dotnet host is left
            // as a long-running background process). The set itself is cheap
            // and idempotent, so set even if we'd otherwise skip.
            process.PriorityClass = ProcessPriorityClass.Normal;
            Logger.Information(
                "Process priority pinned to Normal (was {Before}; Efficiency Mode workaround)",
                before);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to raise process priority class");
        }
    }

    private static void TryOptOutOfEfficiencyMode()
    {
        try
        {
            // ControlMask says "I'm setting EXECUTION_SPEED"; StateMask=0 says
            // "the value is OFF" — i.e. don't throttle this process. This is
            // documented as the explicit opt-out from EcoQoS:
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
            var state = new NativeInterop.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeInterop.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = NativeInterop.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0,
            };

            var size = (uint)Marshal.SizeOf<NativeInterop.PROCESS_POWER_THROTTLING_STATE>();
            var ok = NativeInterop.SetProcessInformation(
                NativeInterop.GetCurrentProcess(),
                NativeInterop.ProcessInformationClass.ProcessPowerThrottling,
                ref state,
                size);

            if (ok)
            {
                Logger.Information(
                    "Opted out of Efficiency Mode (PROCESS_POWER_THROTTLING_EXECUTION_SPEED disabled)");
            }
            else
            {
                Logger.Warning(
                    "SetProcessInformation(ProcessPowerThrottling) returned false; LastError={Err}",
                    Marshal.GetLastWin32Error());
            }
        }
        catch (EntryPointNotFoundException)
        {
            // SetProcessInformation arrived in Win10 1709 / Server 2016. VoiceWink
            // targets Win10.0.22621.0 so this should never fire in production, but
            // tolerating it costs nothing.
            Logger.Debug("SetProcessInformation not available on this OS; skipping EcoQoS opt-out");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to opt out of Efficiency Mode");
        }
    }
}
