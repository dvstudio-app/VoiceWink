namespace VoiceWink.Helpers;

/// <summary>
/// Carries <see cref="PriorityWatchdog"/>'s diagnostics off the rescue thread and onto a
/// normal-priority pump thread that does the actual logging.
///
/// <para><b>Why this exists.</b> The watchdog previously logged on its own thread, dropping
/// out of TIME_CRITICAL first and restoring afterwards. Codex flagged the residual window in
/// three consecutive reviews and was right: Windows can re-demote the process DURING that
/// write, leaving the sole rescue thread at Idle+Normal — base priority 4 — stalled inside a
/// synchronous Serilog write with nothing boosted left to recover it. A small reprise of the
/// exact bug REL-14 fixes. Delayed diagnostics are safe; a stalled rescue thread is not.</para>
///
/// <para><b>The publish side never blocks and never allocates.</b> <see cref="TryPublish"/>
/// uses <c>Monitor.TryEnter</c> with no timeout, so the priority-15 caller either takes an
/// uncontended lock immediately or walks away and retries on its next tick — it can never
/// wait on a lock held by a normal-priority thread, which is the priority inversion the whole
/// design exists to avoid. Signalling is one bounded kernel call.</para>
///
/// <para>Because the publish side is latest-wins, two demotion catches arriving before the
/// pump drains would report only the newer one. Catches are rare and the pump wakes in
/// microseconds, so this is accepted rather than paying for a queue.</para>
/// </summary>
internal sealed class PriorityDiagnosticChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Action<string, object?[]> _logInfo;
    private readonly Action<string, object?[]> _logWarning;

    // Published state, guarded by _gate.
    private int _achievedPriority = PriorityWatchdog.NoPriority;
    private uint _demotedFrom;
    private long _latencyMs = -1;
    private readonly bool[] _failurePending;
    private readonly int[] _failureError;

    internal PriorityDiagnosticChannel(
        Action<string, object?[]> logInfo,
        Action<string, object?[]> logWarning)
    {
        _logInfo = logInfo;
        _logWarning = logWarning;
        _failurePending = new bool[PriorityWatchdog.FailureKindCount];
        _failureError = new int[PriorityWatchdog.FailureKindCount];
    }

    /// <summary>
    /// Publishes a snapshot. Called from the TIME_CRITICAL thread, so it must stay
    /// non-blocking, allocation-free, and free of I/O.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the snapshot was taken and the caller may clear its pending state;
    /// <c>false</c> if the lock was momentarily contended and the caller should retry next
    /// tick. Never throws.
    /// </returns>
    internal bool TryPublish(
        int achievedPriority,
        uint demotedFrom,
        long latencyMs,
        bool[] failurePending,
        int[] failureError)
    {
        if (!Monitor.TryEnter(_gate))
            return false;

        try
        {
            if (achievedPriority != PriorityWatchdog.NoPriority)
                _achievedPriority = achievedPriority;

            if (demotedFrom != 0)
            {
                _demotedFrom = demotedFrom;
                _latencyMs = latencyMs;
            }

            for (var i = 0; i < failurePending.Length && i < _failurePending.Length; i++)
            {
                if (!failurePending[i])
                    continue;

                _failurePending[i] = true;
                _failureError[i] = failureError[i];
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        _signal.Set();
        return true;
    }

    /// <summary>
    /// The pump loop, run on its own normal-priority background thread. Drains on every wake,
    /// and once more when <paramref name="stop"/> is signalled.
    ///
    /// <para>Precisely: everything published BEFORE that final drain is written. The watchdog
    /// shares this stop handle, so one mid-tick publish can still land after the pump has
    /// exited and be dropped. That is deliberate — it can only happen during shutdown, where
    /// the lost line is a routine tick's diagnostics, and closing it would mean a second stop
    /// handle plus an ordering dependency between two joins in a lifecycle that has already
    /// been the source of two bugs here.</para>
    /// </summary>
    internal void Run(WaitHandle stop)
    {
        var handles = new[] { (WaitHandle)_signal, stop };

        while (true)
        {
            int woken;
            try
            {
                woken = WaitHandle.WaitAny(handles);
            }
            catch
            {
                // A disposed handle during teardown — drain what we have and go.
                DrainOnce();
                return;
            }

            DrainOnce();

            if (woken == 1)
                return;
        }
    }

    /// <summary>
    /// Emits everything published so far. Internal so tests can drive publish/drain
    /// deterministically without threads. Returns the number of lines emitted.
    /// </summary>
    internal int DrainOnce()
    {
        int achieved;
        uint demotedFrom;
        long latencyMs;
        bool[] pending;
        int[] errors;

        lock (_gate)
        {
            achieved = _achievedPriority;
            demotedFrom = _demotedFrom;
            latencyMs = _latencyMs;
            pending = (bool[])_failurePending.Clone();
            errors = (int[])_failureError.Clone();

            _achievedPriority = PriorityWatchdog.NoPriority;
            _demotedFrom = 0;
            _latencyMs = -1;
            Array.Clear(_failurePending);
        }

        // Logged OUTSIDE the lock so the publish side can never find it contended for the
        // duration of a disk write. Each write is contained: this runs on a background
        // thread, where an escaping exception would take the process down.
        var emitted = 0;

        if (achieved != PriorityWatchdog.NoPriority)
        {
            emitted += Emit(
                info: true,
                "Priority re-pin watchdog thread at OS priority {Achieved} (TIME_CRITICAL target {Target})",
                new object?[] { achieved, NativeInterop.THREAD_PRIORITY_TIME_CRITICAL });
        }

        if (demotedFrom != 0)
        {
            // Latency is an UPPER BOUND: we know when the demotion was observed, never when
            // Windows applied it. Measured on a suspend-excluding clock.
            emitted += Emit(
                info: true,
                "Priority re-pinned to Normal (Windows demoted us to {DemotedFrom}); caught within {LatencyMs}ms of the last healthy check",
                new object?[]
                {
                    PriorityRepinGate.Describe(demotedFrom),
                    latencyMs < 0 ? "unknown" : latencyMs.ToString(),
                });
        }

        for (var i = 0; i < pending.Length; i++)
        {
            if (!pending[i])
                continue;

            // {Detail} is kind-specific: a Win32 error code for the call that failed, or —
            // for the two *Unverified kinds — the thread priority actually read back.
            emitted += Emit(
                info: false,
                "Priority re-pin watchdog failure: {Failure}, detail={Detail} (logged once per kind; not repeated per tick)",
                new object?[] { (PriorityFailureKind)i, errors[i] });
        }

        return emitted;
    }

    private int Emit(bool info, string template, object?[] args)
    {
        try
        {
            if (info)
                _logInfo(template, args);
            else
                _logWarning(template, args);

            return 1;
        }
        catch
        {
            // A throwing sink must not kill the pump thread, and there is nothing else to
            // report it with.
            return 0;
        }
    }

    public void Dispose() => _signal.Dispose();
}
