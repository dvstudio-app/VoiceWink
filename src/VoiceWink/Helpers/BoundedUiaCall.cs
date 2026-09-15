using System.Threading;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// PST-10: per-item lifecycle for a bounded call on a <c>UiaFocusBridge</c> worker queue.
///
/// <para><b>The defect this closes.</b> Every bounded worker operation used to wait on its
/// TCS with a timeout and, on expiry, log + <c>TrySetCanceled</c> + return — leaving the
/// queued work item alive. A timed-out <c>SetFocus</c> then executed whenever the wedged
/// target finally answered (PST-8 measured a 500 ms bound answering ~17 s later on
/// WhatsApp), moving the user's focus seconds after the paste finished. This type gives
/// each queued item an explicit lifecycle so a timeout can PROVE what the item can still
/// do — and suppress the mutation when it has not yet committed.</para>
///
/// <para><b>State machine</b> (single CAS field; all transitions one-way):</para>
/// <code>
/// Queued  ──TryBeginExecute (worker, at dequeue)──────────────► Started
/// Queued  ──TryAbandon (timeout path)─────────────────────────► Abandoned
/// Started ──TryCommitMutation (worker, LAST instruction before SetFocus)─► MutationCommitted
/// Started ──TryBarMutation (timeout path)─────────────────────► MutationBarred
/// </code>
///
/// <para>Read-only items use only the top half. The commit/bar pair exists because two of
/// the three focus-mutating operations perform cross-process READS before their
/// <c>SetFocus</c> (Codex plan round 1, blocking): a dequeue-time gate alone lets a read
/// stall carry the mutation past the timeout. The commit must be the last instruction
/// before the mutation, so a lost commit proves the mutation never runs.</para>
///
/// <para><b>Busy ownership (Codex plan round 2, blocking).</b> The worker's dequeue path
/// ALWAYS releases the worker's busy flag — after running the body or after skipping an
/// abandoned husk — via the item's idempotent <see cref="BoundedCallItem{T}.Complete"/>.
/// The timeout path NEVER touches it. Round 2's draft released busy at abandonment, which
/// would have REMOVED today's backpressure: behind a wedged release, each new caller would
/// win the freed flag, pay its full bound, and enqueue another husk — unbounded churn where
/// today the first caller waits and later callers fail fast. Worker-owned release preserves
/// that behaviour exactly: busy frees when the husk is skipped, i.e. when the wedge clears.</para>
///
/// <para><b>The invariant downstream binding relies on</b> (stated precisely per round 2:
/// "no item remains capable of calling SetFocus", not "no incomplete item"): an item's
/// mutation can only run between a WON <c>TryCommitMutation</c> and its completion, and
/// busy is held from admission until the item's <see cref="BoundedCallItem{T}.Complete"/> —
/// which every body calls AT OR AFTER its mutation point (publication precedes only RCW
/// cleanup, never the mutation). Therefore: winning the admission CAS proves no prior item
/// can still mutate, and a subsequent abandon/bar proves this item never will. That chain
/// is what makes <c>RestoreFocusResult.Unavailable</c> bind-safe for PST-7 while
/// <c>BusyPending</c> (admission lost — incumbent unknown) and <c>TimedOutPending</c>
/// (own mutation committed, outcome unknown) never are.</para>
///
/// <para>Pure orchestration — no COM, no threads of its own — so the races are exhaustively
/// unit-testable (<c>BoundedUiaCallTests</c>), including the production site compositions
/// in <see cref="MutatingCallBodies"/>.</para>
/// </summary>
internal enum BoundedCallPhase
{
    Queued = 0,
    Started = 1,
    Abandoned = 2,
    MutationCommitted = 3,
    MutationBarred = 4
}

/// <summary>One-way CAS gate over <see cref="BoundedCallPhase"/>. See the file doc.</summary>
internal sealed class BoundedCallGate
{
    private int _phase; // BoundedCallPhase

    public BoundedCallPhase Phase => (BoundedCallPhase)Volatile.Read(ref _phase);

    /// <summary>Worker, at dequeue. False ⇒ the item was abandoned and its body must not run.</summary>
    public bool TryBeginExecute()
        => Interlocked.CompareExchange(ref _phase, (int)BoundedCallPhase.Started, (int)BoundedCallPhase.Queued)
           == (int)BoundedCallPhase.Queued;

    /// <summary>Timeout path. True ⇒ the body will provably never run.</summary>
    public bool TryAbandon()
        => Interlocked.CompareExchange(ref _phase, (int)BoundedCallPhase.Abandoned, (int)BoundedCallPhase.Queued)
           == (int)BoundedCallPhase.Queued;

    /// <summary>
    /// Worker, as the LAST instruction before the focus mutation. False ⇒ the timeout path
    /// barred the mutation while the body was still in its pre-mutation reads — the body
    /// must skip the mutation (and everything that only makes sense after it).
    /// </summary>
    public bool TryCommitMutation()
        => Interlocked.CompareExchange(ref _phase, (int)BoundedCallPhase.MutationCommitted, (int)BoundedCallPhase.Started)
           == (int)BoundedCallPhase.Started;

    /// <summary>Timeout path, after a lost <see cref="TryAbandon"/> on a mutating item.
    /// True ⇒ the mutation will provably never run (the body's commit will lose).</summary>
    public bool TryBarMutation()
        => Interlocked.CompareExchange(ref _phase, (int)BoundedCallPhase.MutationBarred, (int)BoundedCallPhase.Started)
           == (int)BoundedCallPhase.Started;
}

/// <summary>How a timed-out bounded call classifies once the gate has spoken.</summary>
internal enum BoundedTimeoutClass
{
    /// <summary>Abandon won: the body never ran and never will. Bind-safe.</summary>
    AbandonedBeforeStart,
    /// <summary>Bar won: pre-mutation reads may still be running (and keep the worker
    /// legitimately busy), but the mutation is provably suppressed. Bind-safe.</summary>
    MutationSuppressed,
    /// <summary>The item completed in the race window between the wait expiring and
    /// classification — the caller gets the REAL result instead of a refusal.</summary>
    CompletedLate,
    /// <summary>The mutation is committed/executing (or a read is executing) with an
    /// unknown outcome. Never bind-safe; a late-completion log line is armed.</summary>
    StillPending
}

internal readonly record struct BoundedTimeoutResult<T>(BoundedTimeoutClass Class, T? LateResult);

/// <summary>
/// One queued bounded call: gate + TCS + the worker's busy-release, with idempotent
/// completion so publication order stays per-site (a body calls <see cref="Complete"/> at
/// its publication point and only THEN does RCW cleanup — the shipped ordering that keeps a
/// slow cross-process Release from turning a successful call into a caller-side timeout).
/// </summary>
internal sealed class BoundedCallItem<T>
{
    private static ILogger Logger => Log.ForContext(typeof(BoundedCallItem<T>));

    private readonly TaskCompletionSource<T> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _releaseBusy;
    private readonly string _opName;
    private readonly DateTime _enqueuedUtc;
    private int _completed; // 0 = open, 1 = Complete ran

    public BoundedCallItem(string opName, Action releaseBusy, DateTime enqueuedUtc)
    {
        _opName = opName;
        _releaseBusy = releaseBusy;
        _enqueuedUtc = enqueuedUtc;
    }

    public BoundedCallGate Gate { get; } = new();
    public Task<T> Task => _tcs.Task;

    /// <summary>
    /// Publish the result and release the worker's busy flag, exactly once (idempotent —
    /// the dequeue wrapper's finally may call it again as throw-safety and must no-op).
    /// Order is load-bearing and mirrors the shipped bodies: busy release, then result
    /// publication; RCW cleanup stays AFTER this call, inside the body.
    /// </summary>
    public void Complete(T result)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _releaseBusy();
        _tcs.TrySetResult(result);
    }

    /// <summary>
    /// The worker-side wrapper run at dequeue. Skips an abandoned husk (logging its queued
    /// age — visibility for the wedged-release corner), runs the body otherwise, and
    /// guarantees completion + busy release even when the body throws before publishing.
    /// The BODY owns its internal ordering; this wrapper only backstops it.
    /// </summary>
    public void RunAtDequeue(Action<BoundedCallItem<T>> body, T fallback, Func<DateTime> utcNow)
    {
        if (!Gate.TryBeginExecute())
        {
            var queuedMs = (int)(utcNow() - _enqueuedUtc).TotalMilliseconds;
            Logger.Information(
                "UIA {Op}: abandoned item skipped {Ms}ms after enqueue (mutation never ran)",
                _opName, queuedMs);
            Complete(fallback);
            return;
        }

        try
        {
            body(this);
        }
        catch (Exception ex)
        {
            Logger.Information("UIA {Op} item threw: {ExType}", _opName, ex.GetType().Name);
        }
        finally
        {
            Complete(fallback);
        }
    }

    /// <summary>
    /// The timeout-side classification, run after the caller's wait expired. Order is the
    /// design: abandon (never ran) → bar (mutation suppressed; mutating items only) →
    /// completed-in-race (return the REAL result) → still pending (arm ONE late-completion
    /// log through <paramref name="describe"/> — a REQUIRED content-free projection,
    /// because a generic <c>T.ToString()</c> would leak focused-field text from readback
    /// results in violation of the bridge's privacy contract).
    /// </summary>
    public BoundedTimeoutResult<T> ClassifyTimeout(bool hasMutationGate, int timeoutMs, Func<T, string> describe, Func<DateTime> utcNow)
    {
        if (Gate.TryAbandon())
            return new BoundedTimeoutResult<T>(BoundedTimeoutClass.AbandonedBeforeStart, default);

        if (hasMutationGate && Gate.TryBarMutation())
        {
            // Codex diff round 1 (non-blocking race): a body can complete WITHOUT ever
            // committing — verified-refocus prechecks failing ("different-element",
            // "not-editable") publish a result while the phase stays Started, so the bar
            // above can win against an already-FINISHED call. Mutation safety is
            // unaffected either way; this recheck is about honesty — return the real
            // result instead of mislabelling it "mutation-barred".
            if (_tcs.Task.IsCompletedSuccessfully)
                return new BoundedTimeoutResult<T>(BoundedTimeoutClass.CompletedLate, _tcs.Task.Result);
            ArmLateCompletionLog(describe, utcNow);
            return new BoundedTimeoutResult<T>(BoundedTimeoutClass.MutationSuppressed, default);
        }

        if (_tcs.Task.IsCompletedSuccessfully)
            return new BoundedTimeoutResult<T>(BoundedTimeoutClass.CompletedLate, _tcs.Task.Result);

        Logger.Information(
            "UIA {Op} timed out after {Ms}ms with the call still running (phase={Phase})",
            _opName, timeoutMs, Gate.Phase);
        ArmLateCompletionLog(describe, utcNow);
        return new BoundedTimeoutResult<T>(BoundedTimeoutClass.StillPending, default);
    }

    private void ArmLateCompletionLog(Func<T, string> describe, Func<DateTime> utcNow)
    {
        _tcs.Task.ContinueWith(
            t =>
            {
                var totalMs = (int)(utcNow() - _enqueuedUtc).TotalMilliseconds;
                Logger.Information(
                    "UIA {Op} straggler settled {Ms}ms after enqueue (phase={Phase}, outcome={Outcome})",
                    _opName, totalMs, Gate.Phase,
                    t.Status == TaskStatus.RanToCompletion ? describe(t.Result) : t.Status.ToString());
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>
/// The three production focus-mutating call skeletons, extracted with delegate seams so the
/// GATE PLACEMENT — commit as the last instruction before SetFocus — is the exact code the
/// worker runs AND the code the tests drive (Codex round 2: a generic barred-row test does
/// not prove the three real placements; these shared skeletons make the proof structural).
/// The delegates carry the COM steps; no COM type appears here.
/// </summary>
internal static class MutatingCallBodies
{
    /// <summary>`TryRestoreFocusTyped`: SetFocus is the body's first and only COM call.</summary>
    public static RestoreFocusResult DirectRestore(
        BoundedCallGate gate,
        Func<RestoreFocusResult> setFocusAndClassify)
    {
        return gate.TryCommitMutation()
            ? setFocusAndClassify()
            : RestoreFocusResult.Unavailable; // barred — mutation provably suppressed
    }

    /// <summary>
    /// `RefocusCurrentElement`: acquire the live element (cross-process READ — the stall
    /// that motivated the second gate), then commit, then SetFocus.
    /// </summary>
    public static bool CurrentRefocus(
        BoundedCallGate gate,
        Func<bool> acquireTarget,
        Func<bool> setFocus)
    {
        if (!acquireTarget())
            return false;
        if (!gate.TryCommitMutation())
            return false; // barred during the read
        return setFocus();
    }

}
