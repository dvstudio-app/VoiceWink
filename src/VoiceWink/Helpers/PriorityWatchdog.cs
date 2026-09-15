namespace VoiceWink.Helpers;

/// <summary>
/// Distinct failure kinds the watchdog can hit. Each is logged at most ONCE per
/// process — a persistently failing native call would otherwise warn every 5 s
/// forever. (The pre-REL-14 timer had exactly that hole.)
/// </summary>
internal enum PriorityFailureKind
{
    ReadClass = 0,
    WriteClass = 1,
    ThreadBoost = 2,
    EcoQoS = 3,
    ReadThreadPriority = 4,
    TickException = 5,

    /// <summary>The raise reported success but the readback disagreed — boost not actually held.</summary>
    ThreadBoostUnverified = 6,

    /// <summary>
    /// Failed to DROP the boost on entering a realtime-class process. The most severe
    /// state this watchdog can create — TIME_CRITICAL means absolute 31 there — so it must
    /// never be silent (Kimi diff review 2026-08-05).
    /// </summary>
    BoostShed = 7,

    /// <summary>The unbiased clock read failed, so a catch latency could not be computed.</summary>
    Clock = 8,

    // NOTE: there are deliberately no "boost restore" kinds. Moving diagnostics onto
    // PriorityDiagnosticChannel's pump thread removed the temporary priority drop that used
    // to surround logging, so there is no restore to fail. The boost is acquired once and
    // held until a realtime transition or an unreadable class sheds it.
}

/// <summary>
/// REL-14: the process-priority re-pin loop, running on its own thread raised to
/// <c>THREAD_PRIORITY_TIME_CRITICAL</c>.
///
/// <para><b>Why a dedicated thread.</b> This replaced a <c>System.Threading.Timer</c>,
/// whose callbacks run on the .NET ThreadPool — inside the very process the demotion
/// starves. Two mechanisms made that fail in the field, and a dedicated TIME_CRITICAL
/// thread removes both: the process sitting at <c>IDLE_PRIORITY_CLASS</c> puts every one of
/// its threads at base priority 4 against a logon storm of Normal-class work at 8, and
/// ThreadPool injection is rate-limited while the pool is simultaneously saturated by the
/// app's own async startup. Two field incidents (2026-07-24, 2026-08-05) recorded silent
/// gaps of 4 min 11 s and 2 min 25 s ending at the re-pin, with the whole remaining startup
/// completing in single-digit seconds immediately after. <c>THREAD_PRIORITY_HIGHEST</c>
/// would NOT have worked — inside the Idle class it reaches only 6.</para>
///
/// <para><b>The invariant.</b> The ONLY things that ever execute at TIME_CRITICAL are
/// <c>GetPriorityClass</c>, <c>SetPriorityClass</c>, <c>SetProcessInformation</c>,
/// <c>SetThreadPriority</c>, <c>QueryUnbiasedInterruptTime</c>, one uncontended
/// <c>Monitor.TryEnter</c>/<c>SetEvent</c> publish, and the stop wait. Nothing that
/// allocates, blocks on I/O, or can WAIT on a lock held by a normal-priority thread may be
/// added. <b>This loop does not log.</b> Diagnostics go to
/// <see cref="PriorityDiagnosticChannel"/> and are written by a separate normal-priority
/// pump thread — see that class for why logging from here was unsafe even with a temporary
/// priority drop.</para>
///
/// <para><b>One honest exception to that invariant:</b> the FIRST pass through
/// <see cref="RunTick"/> JIT-compiles it and its callees while already boosted, and the JIT
/// takes its own locks. So there is a brief, bounded inversion window on the first tick that
/// the wording above does not literally cover. Removing it would need pre-JIT or
/// ReadyToRun-style warming that isn't worth the machinery — but do not let the invariant be
/// read as stronger than it is (Kimi diff review 2026-08-05).</para>
///
/// <para>Pinned by <c>PriorityWatchdogTests</c> (fake-driven; no real priority-15 thread in
/// the test host). Design converged over three dual review rounds: Codex supplied the
/// realtime-transition bound, the verified-boost requirement, and the off-thread logging
/// this class now relies on; Kimi the non-monotonic-constant trap, the rate-limiting, and
/// the steady-state-silence regression.</para>
/// </summary>
internal sealed class PriorityWatchdog
{
    internal const int FailureKindCount = 9;

    /// <summary>Sentinel for "no achieved-priority reading is pending".</summary>
    internal const int NoPriority = int.MinValue;

    private readonly IPriorityPlatform _platform;
    private readonly PriorityDiagnosticChannel _channel;
    private readonly Action<Exception, string> _logException;

    /// <summary>Returns <c>true</c> when stop was signalled, <c>false</c> on timeout.</summary>
    private readonly Func<TimeSpan, bool> _waitForStop;

    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _interval;

    // --- Pending diagnostics awaiting publication. Fixed size, preallocated,
    // single-threaded access (the worker is the only reader AND writer), so no allocation
    // and no synchronization on the critical path.
    private readonly bool[] _failureReported = new bool[FailureKindCount];
    private readonly bool[] _failurePending = new bool[FailureKindCount];
    private readonly int[] _failureError = new int[FailureKindCount];
    private uint _pendingDemotedFrom;
    private long _pendingLatencyMs = -1;
    private int _pendingAchievedPriority = NoPriority;

    /// <summary>
    /// True while we hold a VERIFIED TIME_CRITICAL boost. Drives "do I need to (re-)raise?".
    /// </summary>
    private bool _boostVerified;

    /// <summary>
    /// True while this thread MIGHT be above normal priority — so it must be normalized
    /// before a realtime tick proceeds and before <see cref="Run"/> returns.
    ///
    /// <para>Separate from <see cref="_boostVerified"/> because the two genuinely diverge: a
    /// raise that succeeds but reads back wrong leaves the boost unverified while the thread
    /// may still be elevated. Collapsing them meant a FAILED normalization was recorded as
    /// "not boosted", which <see cref="Run"/> then read as proof the thread was safe — so the
    /// terminal log could run at priority 15 and a realtime tick would not retry the shed
    /// (Codex diff review round 4).</para>
    /// </summary>
    private bool _mayHoldElevated;

    private long _lastHealthyMs;
    private bool _haveHealthyStamp;

    internal PriorityWatchdog(
        IPriorityPlatform platform,
        PriorityDiagnosticChannel channel,
        Action<Exception, string> logException,
        Func<TimeSpan, bool> waitForStop,
        TimeSpan firstDelay,
        TimeSpan interval)
    {
        _platform = platform;
        _channel = channel;
        _logException = logException;
        _waitForStop = waitForStop;
        _firstDelay = firstDelay;
        _interval = interval;
    }

    /// <summary>Exposed for tests: do we hold a verified boost?</summary>
    internal bool IsBoosted => _boostVerified;

    /// <summary>Exposed for tests: might this thread still be above normal priority?</summary>
    internal bool MayHoldElevatedPriority => _mayHoldElevated;

    /// <summary>
    /// Whether the worker had verifiably left TIME_CRITICAL by the time <see cref="Run"/>
    /// returned. The tuner's exit hook consults it before logging: its state reconciliation
    /// is a few field writes under an uncontended lock and is always safe, but a Serilog
    /// write at priority 15 is not.
    /// </summary>
    internal bool LeftAtNormalPriority { get; private set; }

    /// <summary>
    /// The loop. Returns when stop is signalled — or, on a should-never-happen exception,
    /// quietly, because an escape here would kill the app:
    /// <c>ProcessPriorityTuner.Start()</c> is called from the <c>App</c> constructor BEFORE
    /// the unhandled-exception handlers are installed a few lines later, so there would be
    /// nothing watching. (Deliberately no line numbers — they drift.)
    ///
    /// <para>NEVER returns while still holding TIME_CRITICAL: the tuner's exit hook takes a
    /// lock and may log, which must not happen at priority 15.</para>
    /// </summary>
    internal void Run()
    {
        try
        {
            try
            {
                PrimeBoost();

                // Stamp before the first wait so a demotion caught on the first tick
                // reports elapsed-since-start rather than a garbage or zero latency.
                StampHealthy();

                if (_waitForStop(_firstDelay))
                    return;

                while (true)
                {
                    try
                    {
                        RunTick();
                    }
                    catch (Exception ex)
                    {
                        // A single bad tick must not end the loop — the watchdog is the
                        // recovery path. Note it (rate-limited) and keep going.
                        NoteFailure(PriorityFailureKind.TickException, ex.HResult);
                    }

                    if (_waitForStop(_interval))
                        return;
                }
            }
            finally
            {
                // Every exit path, ordinary or exceptional. Keyed on "might be elevated",
                // NOT on "holds a verified boost": a failed normalization earlier leaves the
                // thread elevated with no verified boost, and that must still be downgraded
                // here rather than assumed safe.
                LeftAtNormalPriority =
                    !_mayHoldElevated
                    || _platform.TrySetCurrentThreadPriority(
                        NativeInterop.THREAD_PRIORITY_NORMAL, out _);

                if (LeftAtNormalPriority)
                    _mayHoldElevated = false;

                _boostVerified = false;
            }
        }
        catch (Exception ex)
        {
            // Only write if the downgrade actually succeeded — otherwise there is nowhere
            // safe to write and the line is dropped. Contained either way: a throwing sink
            // on a background thread would take the whole process with it. This is the one
            // log site left on this thread, and it is terminal — by the time it runs there
            // is no rescue left for a slow write to delay.
            if (!LeftAtNormalPriority)
                return;

            try
            {
                _logException(ex, "Priority re-pin watchdog thread exited unexpectedly");
            }
            catch
            {
                // Nothing left to report it with.
            }
        }
    }

    /// <summary>
    /// The initial raise. Reads the class first: in <c>REALTIME_PRIORITY_CLASS</c>,
    /// TIME_CRITICAL means priority 31, which can starve kernel threads.
    /// </summary>
    internal void PrimeBoost()
    {
        if (!_platform.TryGetProcessPriorityClass(out var observed, out var error))
        {
            NoteFailure(PriorityFailureKind.ReadClass, error);
            return;
        }

        if (PriorityRepinGate.Decide(observed) == PriorityRepinAction.ShedBoost)
            return;

        TryBoost();

        // Publish immediately rather than waiting for the first tick: the achieved-priority
        // line is the field gate's evidence, and a stop signalled during the initial delay
        // would otherwise strand it — along with any failure noted while priming.
        PublishDiagnostics();
    }

    /// <summary>
    /// One check. Internal so tests can drive ticks deterministically instead of racing a
    /// real thread.
    /// </summary>
    internal void RunTick()
    {
        // The class read comes FIRST, every wake. A realtime transition after the initial
        // check would otherwise leave us holding priority 31.
        if (!_platform.TryGetProcessPriorityClass(out var observed, out var readError))
        {
            NoteFailure(PriorityFailureKind.ReadClass, readError);

            // Fail SAFE, not fail-open: an unreadable class cannot rule out a realtime
            // transition, where our boost means absolute 31. Shed it and stay unboosted
            // until a successful non-realtime read permits re-arming — otherwise a run of
            // failed reads would hold 31 indefinitely, breaking the "re-detected every
            // wake" bound this loop claims (Codex diff review round 3).
            ShedBoostIfHeld();
            PublishDiagnostics();
            return;
        }

        var action = PriorityRepinGate.Decide(observed);

        if (action == PriorityRepinAction.ShedBoost)
        {
            // Realtime-class process: shed the boost and do nothing else. Such a process is
            // not the starvation case this loop exists for.
            //
            // Residual race, accepted: between this read and an external change (Task
            // Manager), the boost can mean 31 for at most one interval. Closing it would
            // require polling faster than the work we are trying not to do.
            ShedBoostIfHeld();

            // Publish on this path too. Since diagnostics no longer require a priority drop
            // or a recovered class, there is no reason to withhold them — and a shed FAILURE
            // reported here is the most urgent line this class can produce, so leaving it
            // pending for the duration of a realtime window would be the worst place to
            // withhold it.
            PublishDiagnostics();
            return;
        }

        // Re-arm after a realtime window ended, or if a previous raise failed or read back
        // wrong. Keyed on the VERIFIED flag, so an unverified boost is genuinely retried.
        if (!_boostVerified)
            TryBoost();

        switch (action)
        {
            case PriorityRepinAction.Repin:
                if (_platform.TrySetProcessPriorityClassNormal(out var writeError))
                {
                    _pendingDemotedFrom = observed;
                    _pendingLatencyMs = ElapsedSinceHealthy();
                    StampHealthy();
                }
                else
                {
                    NoteFailure(PriorityFailureKind.WriteClass, writeError);
                }

                break;

            case PriorityRepinAction.Skip:
                StampHealthy();
                break;
        }

        // Re-applied every tick: the startup opt-out is one-shot, and nothing else undoes a
        // later re-throttle by Windows. Silent on success.
        if (!_platform.TryReassertEcoQoSOptOut(out var ecoError))
            NoteFailure(PriorityFailureKind.EcoQoS, ecoError);

        PublishDiagnostics();
    }

    private void ShedBoostIfHeld()
    {
        // Keyed on "might be elevated", so a thread left high by a failed normalization is
        // still retried here — not just one holding a verified boost.
        if (!_mayHoldElevated)
            return;

        if (_platform.TrySetCurrentThreadPriority(
                NativeInterop.THREAD_PRIORITY_NORMAL, out var shedError))
        {
            _mayHoldElevated = false;
            _boostVerified = false;
        }
        else
        {
            // Never silent: we may be holding absolute priority 31 in a realtime-class
            // process. _mayHoldElevated stays true, so the next tick retries the shed.
            NoteFailure(PriorityFailureKind.BoostShed, shedError);
        }
    }

    /// <summary>
    /// Acquires a VERIFIED boost: the raise must succeed AND read back as the priority we
    /// asked for. Called only when <see cref="_boostVerified"/> is false — so the achieved-priority
    /// diagnostic is queued once per acquisition and cannot re-pend itself. (An earlier
    /// design logged from this thread behind a temporary drop, and its restore re-queued the
    /// line every tick — one Information line every 5 s for the process lifetime. Kimi caught
    /// it in round 3; moving diagnostics off-thread removed the restore that caused it.)
    /// </summary>
    private void TryBoost()
    {
        if (!_platform.TrySetCurrentThreadPriority(
                NativeInterop.THREAD_PRIORITY_TIME_CRITICAL, out var error))
        {
            // Still strictly better than the old pool timer: a dedicated thread exists
            // regardless of its priority. Note once, never retry-log — and leave the verified flag
            // false so the next tick retries the raise.
            // The raise failed, so it changed nothing — _mayHoldElevated keeps whatever it
            // already was.
            NoteFailure(PriorityFailureKind.ThreadBoost, error);
            _boostVerified = false;
            return;
        }

        // The raise SUCCEEDED, so from here on this thread may be elevated regardless of what
        // the readback says next.
        _mayHoldElevated = true;

        // Read back what the OS actually granted, so the field log carries an OBSERVED
        // priority instead of documented arithmetic. This is the evidence that stands in
        // for a CPU-saturation harness.
        if (_platform.TryGetCurrentThreadPriority(out var achieved, out var readError))
        {
            if (achieved != NativeInterop.THREAD_PRIORITY_TIME_CRITICAL)
            {
                // The set claimed success but the thread is not where we asked. Believing
                // an unheld boost is the failure mode this whole class exists to prevent.
                // Normalize to a known priority and stay retryable — and do NOT queue the
                // achieved-priority line, which would report an unheld boost as observed
                // fact in the very log the field gate reads.
                NoteFailure(PriorityFailureKind.ThreadBoostUnverified, achieved);
                _boostVerified = false;

                // Normalize — and HONOR THE RESULT. Discarding it and clearing the elevated
                // flag anyway was Codex's round-4 blocker: Run() would then treat the thread
                // as provably safe and log terminally at whatever priority it was left at.
                if (_platform.TrySetCurrentThreadPriority(
                        NativeInterop.THREAD_PRIORITY_NORMAL, out var normalizeError))
                {
                    _mayHoldElevated = false;
                }
                else
                {
                    NoteFailure(PriorityFailureKind.BoostShed, normalizeError);
                }

                return;
            }

            _pendingAchievedPriority = achieved;
        }
        else
        {
            // The readback is corroboration; the SET is the authoritative signal. A failed
            // read costs us the diagnostic line, not the boost.
            NoteFailure(PriorityFailureKind.ReadThreadPriority, readError);
        }

        _boostVerified = true;
    }

    /// <summary>
    /// Hands pending diagnostics to the pump thread. No priority juggling, no I/O, no
    /// waiting: if the channel is momentarily contended the notes stay pending and go out
    /// on the next tick.
    /// </summary>
    private void PublishDiagnostics()
    {
        if (!HasPending())
            return;

        if (!_channel.TryPublish(
                _pendingAchievedPriority,
                _pendingDemotedFrom,
                _pendingLatencyMs,
                _failurePending,
                _failureError))
        {
            return;
        }

        _pendingAchievedPriority = NoPriority;
        _pendingDemotedFrom = 0;
        _pendingLatencyMs = -1;
        Array.Clear(_failurePending);
    }

    private bool HasPending()
    {
        if (_pendingAchievedPriority != NoPriority || _pendingDemotedFrom != 0)
            return true;

        for (var i = 0; i < FailureKindCount; i++)
        {
            if (_failurePending[i])
                return true;
        }

        return false;
    }

    private void NoteFailure(PriorityFailureKind kind, int error)
    {
        var index = (int)kind;
        if (_failureReported[index])
            return; // rate limit: one line per kind per process, ever

        _failureReported[index] = true;
        _failurePending[index] = true;
        _failureError[index] = error;
    }

    private void StampHealthy()
    {
        if (_platform.TryGetUnbiasedMs(out var now, out var error))
        {
            _lastHealthyMs = now;
            _haveHealthyStamp = true;
        }
        else
        {
            // Not silent: a dead clock is why a later catch reports "unknown" latency, and
            // the latency line is the field gate's evidence.
            NoteFailure(PriorityFailureKind.Clock, error);
        }
    }

    /// <summary>Milliseconds since the last healthy observation, or -1 if unknown.</summary>
    private long ElapsedSinceHealthy()
    {
        if (!_haveHealthyStamp)
            return -1;

        if (!_platform.TryGetUnbiasedMs(out var now, out var error))
        {
            NoteFailure(PriorityFailureKind.Clock, error);
            return -1;
        }

        var elapsed = now - _lastHealthyMs;
        return elapsed < 0 ? -1 : elapsed;
    }
}
