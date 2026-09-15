namespace VoiceWink.Helpers;

/// <summary>
/// PST-7: owns the pre-send ORDER, not just the classification. Codex plan round 2 flagged
/// that a pure `Decide` would test the easy half while the load-bearing part — wait once →
/// obtain the late identity → decide → final checks → synchronous send — lived inline in
/// `ClipboardService`, whose tests deliberately never touch the native paste path. So the
/// sequence lives here behind delegates and is unit-pinned.
///
/// <para><b>The problem being solved.</b> The paste path runs its final liveness/foreground
/// checks and then waits up to ~400 ms for physically-held modifiers to clear. Those checks
/// are HWND-equality only, so a user switching to a DIFFERENT field inside the SAME window
/// during that wait is invisible: the dictation lands in the field they moved to and is
/// reported as success. PST-6/PST-8 closed this for their own paths by moving the wait ahead
/// and dispatching synchronously; PST-7 extends that to every path where an element identity
/// was actually proven.</para>
///
/// <para><b>Deliberately partial.</b> An unreadable late identity PROCEEDS (fail open). PST-7
/// exists to stop a PROVEN wrong-field paste, not to add a refusal class — treating "cannot
/// read" as "drifted" would refuse working pastes on every under-reporting target, which is
/// the wave-2 risk this wave is scoped to avoid. Both gap branches are reported so the
/// coverage is measurable rather than assumed.</para>
/// </summary>
internal static class PreSendIdentityCoordinator
{
    /// <summary>
    /// PST-7 (Codex diff round 1, finding 3): the implication that makes "no await between the
    /// identity decision and the keystroke" true — an attempt with a proven identity is ALWAYS
    /// pre-waited, and only pre-waited attempts take the synchronous
    /// <c>SendCtrlVWithPrefix</c> dispatch instead of the awaiting
    /// <c>SendCtrlVModifierSafeAsync</c>.
    /// <para><b>History:</b> the first PST-7 wave pinned ONLY this boolean implication; the
    /// sequence <c>decision → final checks → synchronous send</c> itself stayed inline in
    /// <c>ClipboardService</c>, correct by inspection and unpinnable by test. That gap is now
    /// CLOSED by <see cref="RunPreWaitedSequenceAsync"/> (the ordering seam, 2026-07-30) —
    /// this predicate remains the single source of the pre-wait flag the call site branches
    /// on.</para>
    /// </summary>
    public static bool RequiresPreWaitedModifiers(
        bool willVerify, bool suppressRestore, bool identityGuardApplicable)
        => willVerify || suppressRestore || identityGuardApplicable;

    /// <summary>
    /// Classify the late identity check. Pure; the ordering is enforced by
    /// <see cref="RunAsync"/>.
    /// </summary>
    public static PreSendIdentityOutcome Decide(int[]? priorIdentity, int[]? lateIdentity)
    {
        if (priorIdentity is not { Length: > 0 })
            return PreSendIdentityOutcome.NoPriorGap;
        if (lateIdentity is not { Length: > 0 })
            return PreSendIdentityOutcome.UnreadableGap;
        return NoEditableFocusGate.RuntimeIdsEqual(priorIdentity, lateIdentity)
            ? PreSendIdentityOutcome.Proven
            : PreSendIdentityOutcome.Blocked;
    }

    /// <summary>
    /// The SYNCHRONOUS identity decision, factored out of <see cref="RunAsync"/> for the
    /// ordering seam: once the awaited preparation is done, everything up to the keystroke
    /// can be — and now is — synchronous. The early return must stay FIRST so an
    /// inapplicable guard never invokes either identity delegate (pinned by the existing
    /// not-applicable test).
    /// <para><paramref name="usableBaselineIdentity"/> must return null when no usable
    /// post-wait baseline exists. When it returns an id, <paramref name="probeIdentity"/> is
    /// NOT called — including when that id turns out to be a MISMATCH, which blocks without
    /// paying for a probe (pinned by a call-count test).</para>
    /// </summary>
    public static PreSendIdentityResult Resolve(
        bool guardApplicable,
        int[]? priorIdentity,
        Func<int[]?> usableBaselineIdentity,
        Func<int[]?> probeIdentity)
    {
        if (!guardApplicable || priorIdentity is not { Length: > 0 })
            return new PreSendIdentityResult(PreSendIdentityOutcome.NoPriorGap, IdentitySource.None);

        var baseline = usableBaselineIdentity();
        var source = baseline is { Length: > 0 } ? IdentitySource.Baseline : IdentitySource.Probe;
        var late = baseline is { Length: > 0 } ? baseline : probeIdentity();

        return new PreSendIdentityResult(Decide(priorIdentity, late), source);
    }

    /// <summary>
    /// Run the identity decision behind the caller's awaited wait. Retained for the PLAIN
    /// (non-pre-waited) path, where the wait closure is a no-op and only the decision + its
    /// log line matter (every attempt reports exactly one identity outcome — the
    /// four-outcomes-sum-to-total UAT metric). Pre-waited attempts use
    /// <see cref="RunPreWaitedSequenceAsync"/> instead, which owns the full ordering.
    /// </summary>
    public static async Task<PreSendIdentityResult> RunAsync(
        bool guardApplicable,
        int[]? priorIdentity,
        Func<Task> waitModifiers,
        Func<int[]?> usableBaselineIdentity,
        Func<int[]?> probeIdentity)
    {
        await waitModifiers().ConfigureAwait(false);
        return Resolve(guardApplicable, priorIdentity, usableBaselineIdentity, probeIdentity);
    }

    /// <summary>
    /// PST-7 ordering seam (Codex PST-7 diff round 2's open gap, closed here): the ENTIRE
    /// pre-waited send sequence — <c>prepare → identity decision → log → block? → fallback
    /// baseline → target alive → foreground → synchronous send</c> — behind one awaited
    /// preparation step and one NON-ASYNC tail.
    ///
    /// <para><b>What is structurally guaranteed:</b> <see cref="RunPreWaitedTail"/> is a
    /// non-async method, so the compiler rejects any <c>await</c> between the identity
    /// decision and the keystroke; reintroducing one requires editing the tail into an async
    /// method — a compile-visible act. The single statement between the last await and the
    /// tail call below is the whole remaining ordering surface.</para>
    ///
    /// <para><b>What is honestly NOT guaranteed by types</b> (Codex seam plan round 2):
    /// the three check parameters share a delegate type, so their ORDER at the call site is
    /// enforced by named arguments + diff review (omission is a compile error; reordering is
    /// not). A closure could still block internally (<c>.GetAwaiter().GetResult()</c> before
    /// its native call would recreate the drift window without any signature change) — the
    /// production call site therefore passes single-expression closures over the real
    /// operations, and their directness is review-pinned. Production ROUTING (all three
    /// pre-wait reasons taking this sequence; the plain path logging exactly once) is a
    /// diff-review/UAT responsibility — coordinator tests cannot see the call site.</para>
    ///
    /// <para><paramref name="prepare"/> returns the modifier-release prefix so the prefix
    /// reaches <paramref name="send"/> by VALUE through the seam rather than via a captured
    /// mutable local; it also performs the delivery-baseline read on verification-eligible
    /// attempts (the baseline id then reaches the decision through
    /// <paramref name="usableBaselineIdentity"/>).</para>
    /// </summary>
    public static async Task<PreSendSequenceResult> RunPreWaitedSequenceAsync(
        Func<Task<NativeInterop.INPUT[]>> prepare,
        bool guardApplicable,
        int[]? priorIdentity,
        Func<int[]?> usableBaselineIdentity,
        Func<int[]?> probeIdentity,
        Action<PreSendIdentityResult> logOutcome,
        Func<PasteResult> blockedResult,
        Func<PasteResult?> checkFallbackBaseline,
        Func<PasteResult?> checkTargetAlive,
        Func<PasteResult?> checkForeground,
        Func<NativeInterop.INPUT[], bool> send,
        Func<PasteResult> sendFailedResult)
    {
        var modifierPrefix = await prepare().ConfigureAwait(false);
        return RunPreWaitedTail(
            modifierPrefix, guardApplicable, priorIdentity, usableBaselineIdentity,
            probeIdentity, logOutcome, blockedResult, checkFallbackBaseline,
            checkTargetAlive, checkForeground, send, sendFailedResult);
    }

    /// <summary>NON-ASYNC by design — see <see cref="RunPreWaitedSequenceAsync"/>. Every
    /// stage short-circuits: nothing runs after the first failure.</summary>
    private static PreSendSequenceResult RunPreWaitedTail(
        NativeInterop.INPUT[] modifierPrefix,
        bool guardApplicable,
        int[]? priorIdentity,
        Func<int[]?> usableBaselineIdentity,
        Func<int[]?> probeIdentity,
        Action<PreSendIdentityResult> logOutcome,
        Func<PasteResult> blockedResult,
        Func<PasteResult?> checkFallbackBaseline,
        Func<PasteResult?> checkTargetAlive,
        Func<PasteResult?> checkForeground,
        Func<NativeInterop.INPUT[], bool> send,
        Func<PasteResult> sendFailedResult)
    {
        var identity = Resolve(guardApplicable, priorIdentity, usableBaselineIdentity, probeIdentity);
        logOutcome(identity);

        if (identity.ShouldBlock)
            return new PreSendSequenceResult(PreSendSequenceStage.Blocked, blockedResult(), identity);
        if (checkFallbackBaseline() is { } baselineFail)
            return new PreSendSequenceResult(PreSendSequenceStage.FallbackBaselineUnusable, baselineFail, identity);
        if (checkTargetAlive() is { } targetFail)
            return new PreSendSequenceResult(PreSendSequenceStage.TargetLost, targetFail, identity);
        if (checkForeground() is { } foregroundFail)
            return new PreSendSequenceResult(PreSendSequenceStage.ForegroundLost, foregroundFail, identity);
        if (!send(modifierPrefix))
            return new PreSendSequenceResult(PreSendSequenceStage.SendFailed, sendFailedResult(), identity);

        return new PreSendSequenceResult(PreSendSequenceStage.Dispatched, null, identity);
    }
}

/// <summary>Which stage of the pre-waited sequence settled the attempt. The five failure
/// stages map 1:1 onto the shipped outcomes (verified per-mapping in diff review):
/// Blocked → FocusMovedInTarget, FallbackBaselineUnusable → NoEditableFocused,
/// TargetLost → TargetGone, ForegroundLost → LostForegroundPostUia,
/// SendFailed → SendInputFailed.</summary>
internal enum PreSendSequenceStage
{
    Blocked,
    FallbackBaselineUnusable,
    TargetLost,
    ForegroundLost,
    SendFailed,
    Dispatched
}

/// <summary><see cref="Failure"/> is null exactly when <see cref="Stage"/> is
/// <see cref="PreSendSequenceStage.Dispatched"/>; the caller returns it as-is, preserving
/// the shipped outcome/summary-line pattern.</summary>
internal readonly record struct PreSendSequenceResult(
    PreSendSequenceStage Stage, PasteResult? Failure, PreSendIdentityResult Identity);

/// <summary>Where the late identity came from — carried for the diagnostic line only.</summary>
internal enum IdentitySource
{
    None,
    /// <summary>Reused the post-wait delivery baseline's runtime id (no extra UIA call).</summary>
    Baseline,
    /// <summary>Issued one bounded identity probe.</summary>
    Probe
}

internal readonly record struct PreSendIdentityResult(PreSendIdentityOutcome Outcome, IdentitySource Source)
{
    /// <summary>Only a proven MISMATCH refuses the paste; both gap branches proceed.</summary>
    public bool ShouldBlock => Outcome == PreSendIdentityOutcome.Blocked;
}
