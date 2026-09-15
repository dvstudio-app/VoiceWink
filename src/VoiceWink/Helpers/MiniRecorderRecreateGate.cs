namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision helper for the MiniRecorder cross-DPI recreate gate.
/// <para>The gate has three lifecycle states that must all serialize requests:
/// (1) <c>queued</c> — <see cref="Microsoft.UI.Dispatching.DispatcherQueue.TryEnqueue"/>
/// returned <c>true</c> but the dispatcher hasn't pumped the queued lambda yet
/// (a 2.25-second gap was observed in production on 2026-05-21);
/// (2) <c>inProgress</c> — the queued lambda has entered <c>RecreateMiniRecorderWindowAfterDisplayChange</c>
/// and the window swap is mid-flight;
/// (3) <c>completed</c> — finally has run; further requests are blocked by the
/// post-completion quiet period (WM_DPICHANGED → XamlRoot pump) and the
/// accepted-cooldown.</para>
/// <para>Extracted as a pure helper so the gating logic is unit-testable
/// without instantiating WinUI <c>AppWindow</c> + <c>DispatcherQueue</c>.</para>
/// </summary>
internal static class MiniRecorderRecreateGate
{
    /// <summary>
    /// Reject reasons. Listed in priority order — first matching condition wins.
    /// Surfaced via Serilog so log-grep proves which guard caught a given retap.
    /// </summary>
    internal enum Decision
    {
        Accept,
        RejectQuitting,
        RejectInProgress,
        RejectQueued,
        RejectPostCompletionQuiet,
        RejectAcceptedCooldown,
    }

    /// <summary>
    /// Quiet window after a swap completes. Spans the WM_DPICHANGED pump +
    /// XamlRoot.RasterizationScale propagation on the new window. Sized at 9×
    /// observed 2-frame settle time (~32ms at 60Hz) to absorb slow systems
    /// while staying well below human retap latency.
    /// </summary>
    internal static readonly TimeSpan PostCompletionQuietPeriod = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Cooldown between accepted cross-DPI recreates. Measured from the LAST
    /// completed swap, not from acceptance, so a slow dispatcher cannot
    /// stale-out the guard.
    /// </summary>
    internal static readonly TimeSpan AcceptedCooldown = TimeSpan.FromSeconds(1);

    internal static Decision Evaluate(
        DateTime now,
        bool isQuitting,
        bool recreateQueued,
        bool recreateInProgress,
        DateTime lastCompletedUtc)
    {
        if (isQuitting) return Decision.RejectQuitting;
        if (recreateInProgress) return Decision.RejectInProgress;
        if (recreateQueued) return Decision.RejectQueued;

        var sinceCompletion = now - lastCompletedUtc;
        if (sinceCompletion < PostCompletionQuietPeriod) return Decision.RejectPostCompletionQuiet;
        if (sinceCompletion < AcceptedCooldown) return Decision.RejectAcceptedCooldown;

        return Decision.Accept;
    }
}
