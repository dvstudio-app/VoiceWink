namespace VoiceWink.Helpers;

/// <summary>
/// TRN-29 slice 2: the PURE decisions behind the `parakeet-server` child-process manager —
/// health-wait budget, restart storm bound, port-retry bound, and the cancellation escalation
/// grace (`docs/plans/2026-08-23-trn29-parakeetcpp-swap/14-slice2-server-process.md`). The
/// service executes these; nothing here touches a process, a socket, or a clock — callers pass
/// timestamps in, so every decision is replayable in tests.
/// </summary>
public static class ParakeetServerPolicy
{
    /// <summary>The server's decode thread count — the ONE definition, read by
    /// `NativeParakeetServerLauncher`'s default AND linked into `tools/parakeet-long-audio`'s
    /// server spawn (Codex diff r1: that harness once used ProcessorCount/2, which merely
    /// COINCIDED with this value on the 8-logical-processor measurement machine). Since TRN-47
    /// the AsrBench harness holds a deliberate, validated override seam over this default
    /// (`VOICEWINK_ASRBENCH_PARAKEET_THREADS`) so a thread sweep can be measured at all — the
    /// applied value is printed and recorded in the results file, so a non-default run is
    /// attributable, which is the property the old "structurally cannot differ" wording was
    /// protecting.
    ///
    /// <para><b><c>min(8, max(1, ProcessorCount))</c> since TRN-47 (2026-09-01), on a MEASURED
    /// three-sweep result; it was a hardcoded 4 before</b> — the value TRN-29 G5 accepted with
    /// "server thread count" named as the first unpulled lever on the 2.15× p95 gap. Three
    /// sweeps on the owner's 8-logical machine (4/6/8 arms, 4-thread control first and last,
    /// 100-clip samples): no single control met the whisper-grade bar (−6.6% / +8.5% / +4.8%,
    /// noise in BOTH directions — per-arm noise is roughly ±5–8%, and run 3's discarded
    /// 4-thread warm-up arm even hit 9.008×, within 0.2% of the 8-thread winner — but the ORDERING
    /// never wavered: 8 beat every 4-thread control in all six pairwise comparisons (+5.9…+19%,
    /// median ~+11% aggregate) and posted the best p95 in all three runs (4717 / [run 2,
    /// log-sourced] 4331 / 4208 ms vs the 4-thread arms' 4426–5808), and 8 beat 6 in all three.
    /// STATED PLAINLY (both round-1 reviewers): the predeclared clean-control gate was
    /// RENEGOTIATED, not met — replication excludes a one-run thermal artifact, but every sweep
    /// ran the same 4→6→8→4 arm order, so a repeatable position effect is not excluded, and the
    /// verdict is DIRECTIONAL: 8 is very unlikely to be worse and plausibly +5–11% on aggregate
    /// with a better tail. That is enough for a low-risk, one-line-reversible change that is
    /// byte-identical on 4-logical machines and never measured worse anywhere.
    /// Committed evidence: results/speed-2026-08-31-parakeet-tdt-0.6b-v3-pt*.json (run 1) and
    /// results/speed-2026-09-01-…-pt*.json (run 3, incl. the warm-up discard); run 2 survives
    /// only as log-sourced aggregates — its files were overwritten by run 3 under the same-day/same-label
    /// naming, the trap the sweep script's own notes describe (aggregates in the TRN-47 card).
    /// The bounds: never above the machine's logical count (a fleet constant of 8 would
    /// oversubscribe a 4-core box), floor 1, cap 8 (VoiceInk's ceiling, unmeasured beyond it —
    /// the same stance as the Whisper formula's cap). On a 4-logical machine this is exactly
    /// the old value; on the measured machine it is the measured optimum. Deliberately NOT
    /// Whisper's <c>−2</c> headroom: the server decodes in its OWN process while the app only
    /// awaits the HTTP reply, and 8 measured better than 6 in all three runs. Honest residual:
    /// all three sweeps ran with the app closed; decode-time contention with the live app's UI
    /// threads is unmeasured (the whisper decode already runs 6-of-8 in-process with the UI
    /// alive, so the exposure is not novel).</para></summary>
    public static int ServerDecodeThreads { get; } =
        ResolveServerDecodeThreads(global::System.Environment.ProcessorCount);

    /// <summary>The formula alone, seam-tested exactly like
    /// <c>WhisperDecodeSettings.ResolveThreads(int)</c>: CI runners have 2–4 logical processors,
    /// where the formula and the retired constant nearly coincide, so only a case table can
    /// catch a silent revert.</summary>
    internal static int ResolveServerDecodeThreads(int processorCount) =>
        global::System.Math.Min(8, global::System.Math.Max(1, processorCount));

    /// <summary>Health-wait: total budget for spawn → listening → first probe answered. The
    /// measured warm figure is 1.2 s (evidence/g3-server-smoke.md); the budget covers a cold
    /// 941 MB model read on a slow disk.</summary>
    public static readonly TimeSpan HealthBudget = TimeSpan.FromSeconds(30);

    /// <summary>Health-wait poll interval.</summary>
    public static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Cancellation escalation: after cancelling the HTTP request, how long the server
    /// gets to return before the process is KILLED (native decode may be non-interruptible;
    /// killing the child is the abandon policy, and the next preparation respawns).</summary>
    public static readonly TimeSpan CancelKillGrace = TimeSpan.FromSeconds(2);

    /// <summary>Race row 2's one-shot escalation probe (slice 4): the answer window before a
    /// cancelled-under generation is judged wedged and killed. Deliberately short — the request
    /// was already cancelled and its grace already waited; this asks only "is the process still
    /// answering ANYTHING", and holding the manager's gate longer just extends the queued
    /// caller's wait.</summary>
    public static readonly TimeSpan EscalationProbeBudget = TimeSpan.FromSeconds(2);

    /// <summary>Retirement: after Kill(), how long to wait for CONFIRMED process exit before
    /// entering the retiring state (no successor spawns while a predecessor may still hold its
    /// ~1 GB - Codex diff r2). TerminateProcess is asynchronous; confirmation is the invariant.</summary>
    public static readonly TimeSpan RetireWait = TimeSpan.FromSeconds(5);

    /// <summary>TRN-67: how long APP EXIT may wait for the manager's gate before giving up on the
    /// explicit kill. <c>App.Cleanup</c> runs <c>ShutdownAsync</c> as
    /// <c>.GetAwaiter().GetResult()</c> from the <c>Closed</c> handler — the UI thread — and the
    /// gate's long holder is <see cref="HealthBudget"/>'s spawn-and-health wait, so an untimed
    /// acquire parked the exiting process for up to 30 s with the window already gone.
    ///
    /// <para><b>Short by design, and the shortness is the argument.</b> The bound only bites when
    /// the gate is CONTENDED, and the holders that contend for long run in SECONDS —
    /// <see cref="HealthBudget"/>'s spawn-and-health wait, <see cref="RetireWait"/>'s
    /// kill-and-confirm, Auto's one CPU retry, which pays a second spawn (Codex plan round
    /// corrected an earlier draft naming only the spawn), TRN-68's pinned respawn on a two-GPU
    /// PC (a third spawn in the worst case, Auto → pinned → Cpu), and an escalation probe against an
    /// UNRESPONSIVE child, which holds the gate for the probe's whole
    /// <see cref="EscalationProbeBudget"/> (Kimi diff round — an earlier draft filed "a probe"
    /// under the ordinary holders, which is true only of the healthy-child case and false for
    /// exactly the probe most likely to be in flight at a bad moment). Waiting longer cannot
    /// rescue any of them. What the 250 ms does buy is the ORDINARY holder — a probe against a
    /// HEALTHY child, sub-millisecond, or a retire already past its kill — which finishes inside
    /// it and still hands us the confirmed kill. Same UI-thread reasoning, and the same value, as
    /// <c>ParakeetBackendCoordinator.ShutdownStateWait</c> one layer up.</para>
    ///
    /// <para><b>What a miss costs is nearly nothing, which is what makes the bound safe.</b> The
    /// child is placed in a <c>KILL_ON_JOB_CLOSE</c> job AT CREATION
    /// (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, no assign-after-start window) and closing that
    /// handle IS the kill, so the child dies either way. **Said precisely, because the loose
    /// version overstates it** (Codex plan round): the job kill lands when the PROCESS EXITS, not
    /// at the moment of the timeout — the rest of <c>App.Cleanup</c> still runs first — so what a
    /// miss really gives up is the kill's ORDERING plus <see cref="RetireWait"/>'s confirmation.
    /// Nothing after the shutdown call in <c>App.Cleanup</c> observes either. Do NOT read the word
    /// "graceful" in <c>ShutdownAsync</c>'s own doc as "polite": <c>Kill()</c> is
    /// <c>TerminateProcess</c>, the same class of kill the job performs.</para>
    /// </summary>
    public static readonly TimeSpan ShutdownGateWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Involuntary-exit storm bound: more than this many crashes inside
    /// <see cref="StormWindow"/> marks the backend Unrunnable until app restart.</summary>
    public const int MaxCrashesPerWindow = 3;

    /// <summary>The sliding window the storm bound counts over.</summary>
    public static readonly TimeSpan StormWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The storm decision: given the UTC times of past INVOLUNTARY exits (order irrelevant) and
    /// now, may the manager respawn? False once more than <see cref="MaxCrashesPerWindow"/>
    /// exits fall inside the trailing window — the "more than" reading is deliberate: the
    /// third crash still respawns (three strikes get three retries); the fourth inside the
    /// window is the storm.
    /// </summary>
    public static bool MayRespawn(IReadOnlyCollection<DateTime> involuntaryExitsUtc, DateTime nowUtc)
    {
        var cutoff = nowUtc - StormWindow;
        var recent = 0;
        foreach (var exit in involuntaryExitsUtc)
        {
            if (exit > cutoff) recent++;
        }
        return recent <= MaxCrashesPerWindow;
    }

    /// <summary>Health-wait verdict for one poll tick: keep waiting, give up, or (the caller's
    /// own observation) healthy. Pure arithmetic over the elapsed time so the loop's exit
    /// condition is testable without a clock.</summary>
    public static bool WithinHealthBudget(DateTime spawnedUtc, DateTime nowUtc)
        => nowUtc - spawnedUtc <= HealthBudget;
}
