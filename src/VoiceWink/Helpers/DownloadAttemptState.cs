namespace VoiceWink.Helpers;

/// <summary>
/// Tracks WHY a cancellable download's token was cancelled, so a late completion can tell a
/// deliberate user cancel (keep the result) apart from the screen being abandoned — Back, page
/// Unload, step reconstruction (discard the result). UI-3, Codex diff review r6.
///
/// <para><b>Provenance lives on the ATTEMPT, not the page.</b> A page-level bool could not
/// distinguish "this attempt's cancel" from an earlier attempt's stale flag, and nothing reset it
/// when Back/Unload/reconstruction cancelled the SAME token afterwards — so a completion arriving
/// after Cancel-then-Back still read "the user wants to keep this," writing settings and forcing
/// navigation for a screen the user had already left. One instance per attempt, checked via the
/// LOCAL variable the attempt's own closure captured — never a re-read of a page field a newer
/// attempt could have replaced — is what makes this attempt-scoped rather than page-global.</para>
///
/// <para>Extracted to a pure, WinUI-free type for the same reason
/// <c>Helpers.ActionToggleCore</c> was: the page cannot be constructed in the test host, so the
/// decision has to live somewhere a test can actually drive it.</para>
/// </summary>
internal sealed class DownloadAttemptState
{
    /// <summary>Set by the explicit Cancel action. Necessary but not sufficient for "keep it".</summary>
    internal bool CancelledByUser { get; set; }

    /// <summary>
    /// Set by anything meaning the screen's outcome no longer matters. ALWAYS overrides
    /// <see cref="CancelledByUser"/> — see <see cref="ShouldFinalize"/>.
    /// </summary>
    internal bool Abandoned { get; set; }

    /// <summary>
    /// Whether a completion should write its result (persist settings, navigate) given the token's
    /// own <paramref name="cancellationRequested"/> state.
    ///
    /// <para><b><see cref="Abandoned"/> is checked FIRST and unconditionally</b>, so the predicate
    /// says what the doc above says. The earlier form — <c>!cancellationRequested || (CancelledByUser
    /// &amp;&amp; !Abandoned)</c> — finalized an ABANDONED attempt whenever the token had not been
    /// cancelled, i.e. it codified a fail-OPEN state while claiming abandonment always wins (Codex
    /// diff review r7). That combination is unreachable today only because
    /// <c>AbandonCurrentDownload</c> sets the flag and cancels the token in the same synchronous
    /// block; a truth table that depends on two callers staying in lockstep is not a guarantee, and
    /// the failure it would allow is the exact one this type exists to prevent — settings written and
    /// navigation forced for a screen the user has already left.</para>
    ///
    /// <para>The second clause is deliberately NOT <c>!Abandoned</c> alone either. A cancellation
    /// whose source set NEITHER flag — also unreachable today — must fail CLOSED rather than default
    /// to "finalize", which is why finalizing on a cancelled token requires an explicit
    /// <see cref="CancelledByUser"/>.</para>
    /// </summary>
    internal bool ShouldFinalize(bool cancellationRequested)
        => !Abandoned && (!cancellationRequested || CancelledByUser);
}
