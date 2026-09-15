namespace VoiceWink.Helpers;

/// <summary>
/// Pure lifecycle decisions for the MiniRecorder's cloak-based parking (CLK-1).
///
/// <para><b>The three window states</b> (tracked in MiniRecorderWindow as
/// <c>_isOffScreen</c> + <c>_isCloaked</c>; consumer semantics of
/// <c>_isOffScreen</c> are UNCHANGED from the pre-cloak code):</para>
/// <list type="bullet">
/// <item><b>Parked</b> — <c>_isOffScreen == true</c>; cloak path: cloaked at real
/// on-monitor coordinates; legacy path: uncloaked at -10000,-10000.</item>
/// <item><b>RevealPending</b> — <c>_isOffScreen == false</c> ∧ cloaked: a show is
/// in flight (visual updates + DPI reconciliation run; the terminal step is a
/// verified uncloak). The exact analog of the legacy <c>SWP_HIDEWINDOW</c>
/// pending-reveal window.</item>
/// <item><b>Visible</b> — <c>_isOffScreen == false</c> ∧ fully uncloaked
/// (<see cref="VisibleRequires"/>).</item>
/// </list>
/// </summary>
internal static class MiniRecorderShowTransition
{
    /// <summary>How Show() gets the pill on screen for the resolved placement.</summary>
    internal enum ShowPlan
    {
        /// <summary>Cloak path, XamlRoot scale already matches: geometry assert + verified uncloak.</summary>
        UncloakInPlace,
        /// <summary>Cloak path, scale mismatch (from hidden OR already-visible): cloak
        /// unconditionally (mirrors the legacy unconditional SWP_HIDEWINDOW), move
        /// cloaked onto the target monitor, let reconciliation converge, then
        /// verified uncloak. Show-time recreate is deliberately NOT requested on
        /// this plan — a cloaked on-monitor window reconciles for real; the
        /// reconciliation timeout escalates to a recreate instead.</summary>
        CloakedMoveReconcile,
        /// <summary>Legacy path, scale matches: today's MoveAndResize + SWP_SHOWWINDOW.</summary>
        LegacyShow,
        /// <summary>Legacy path, scale mismatch: today's behavior byte-for-byte —
        /// recreate request first ("cross-dpi-show"), else hidden-show reconcile.</summary>
        LegacyRecreateThenInPlace
    }

    /// <summary>How a not-user-visible window is parked (ctor post-probe, WarmUp, HideWindow).</summary>
    internal enum ParkAction
    {
        /// <summary>Cloak at the current real coordinates — keeps the DPI association.</summary>
        CloakInPlace,
        /// <summary>Legacy: move to -10000,-10000 (WS_VISIBLE stays set for surface warmth).</summary>
        MoveOffScreen
    }

    public static ShowPlan Decide(bool cloakAvailable, bool scaleMatches)
        => cloakAvailable
            ? (scaleMatches ? ShowPlan.UncloakInPlace : ShowPlan.CloakedMoveReconcile)
            : (scaleMatches ? ShowPlan.LegacyShow : ShowPlan.LegacyRecreateThenInPlace);

    public static ParkAction Park(bool cloakAvailable)
        => cloakAvailable ? ParkAction.CloakInPlace : ParkAction.MoveOffScreen;

    /// <summary>
    /// A runtime `Cloak(true)` failure means the window is verifiably NOT
    /// cloaked, so the in-flight operation must complete on the corresponding
    /// LEGACY plan (safe precisely because the window is uncloaked). Callers
    /// also flip cloak availability off one-way.
    /// </summary>
    public static ShowPlan FallbackAfterCloakFailure(ShowPlan plan) => plan switch
    {
        ShowPlan.UncloakInPlace => ShowPlan.LegacyShow,
        ShowPlan.CloakedMoveReconcile => ShowPlan.LegacyRecreateThenInPlace,
        _ => plan
    };

    /// <summary>
    /// The Visible state requires the FULL DWMWA_CLOAKED mask to be zero. The
    /// APP bit only attributes our own cloak; SHELL/INHERITED bits (e.g. the
    /// window living on another virtual desktop) still mean "not visible", so
    /// the latency probe must not fire and reveal must not be recorded.
    /// </summary>
    public static bool VisibleRequires(uint cloakedMask) => cloakedMask == 0;

    /// <summary>
    /// Fail-CLOSED verification of "the user can see this window": the
    /// DWMWA_CLOAKED read-back must have SUCCEEDED and report a zero mask.
    /// A failed read is NEVER treated as uncloaked — that would let a reveal
    /// (and the latency probe) be recorded on a window that may still be
    /// invisible.
    /// </summary>
    public static bool VerifiedVisible(bool maskReadSucceeded, uint cloakedMask)
        => maskReadSucceeded && cloakedMask == 0;

    /// <summary>
    /// Fail-CLOSED verification of OUR OWN cloak: read-back succeeded and the
    /// APP bit is set. Shell/inherited bits do NOT count — a shell-cloaked
    /// window (other virtual desktop) is not under our control and can become
    /// visible when the shell cloak lifts, so replacement parking must not be
    /// skipped on their account.
    /// </summary>
    public static bool VerifiedAppCloak(bool maskReadSucceeded, uint cloakedMask)
        => maskReadSucceeded && (cloakedMask & NativeInterop.DWM_CLOAKED_APP) != 0;

    /// <summary>
    /// Reconciliation work (including the pending reveal) is dropped when the
    /// window was hidden between show and reconcile — identical rule to the
    /// pre-cloak code's `_isOffScreen` early-return.
    /// </summary>
    public static bool ShouldDropReveal(bool isOffScreen) => isOffScreen;

    /// <summary>
    /// After a runtime cloak failure the window is verifiably uncloaked — it must be re-parked
    /// at -10000 iff the CALL SITE was in the hidden/parked state when it started (F24). Takes
    /// the pre-flip value as a parameter because Show() mutates <c>_isOffScreen</c> to false
    /// near its top: reading the field inside the fallback would always skip the re-park on
    /// the Show path, leaving a not-yet-presentable pill uncloaked at its target coordinates.
    /// A visible pill (error/redo re-show) must NOT re-park — that would yank it off-screen.
    /// </summary>
    public static bool ShouldReParkAfterCloakFailure(bool wasOffScreenAtCallSite)
        => wasOffScreenAtCallSite;
}
