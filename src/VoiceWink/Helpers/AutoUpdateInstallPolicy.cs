namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision for UPD-4b: when a background scheduler tick reports an available update, may this
/// app apply it automatically (download + restart) without the user clicking "Apply &amp; restart"?
/// </summary>
internal static class AutoUpdateInstallPolicy
{
    /// <summary>
    /// Every operand must be favourable. Two of them are structurally implied at today's only call
    /// site and are still stated, because "install without a check" and "install in a build with
    /// update egress compiled out" must be false BY STATEMENT rather than by luck of the trigger:
    ///
    /// <list type="bullet">
    ///   <item><paramref name="featureEnabled"/> mirrors gate-1 (<c>UPDATE_CHECK_ENABLED</c>). The
    ///   scheduler is inert when it is off, so no tick — and no install — can happen; kept as
    ///   defence-in-depth, deliberately not live logic.</item>
    ///   <item><paramref name="automaticCheckEnabled"/> mirrors gate-2. An install is only ever
    ///   triggered from a scheduler tick and the scheduler is unarmed while checks are off, so this
    ///   too is implied today.</item>
    ///   <item><paramref name="automaticInstallEnabled"/> is the user's opt-out — the only operand
    ///   that genuinely varies at the call site.</item>
    ///   <item><paramref name="applyInFlight"/> avoids a pointless guard-CAS rejection inside
    ///   <c>ApplyAndRestartAsync</c> and the "already being applied" completion event an open
    ///   Updates page would render.</item>
    ///   <item><paramref name="exclusiveMaintenanceActive"/> is the same cheap pre-gate every
    ///   start-path uses. It is an OPTIMISATION, never the authority: the apply path's own
    ///   update-apply lease and blocker pre-check remain the real refusal, and they run under the
    ///   gate's lock where this snapshot cannot.</item>
    /// </list>
    ///
    /// <para>Note what is deliberately NOT an operand: whether a recording / transcription / model
    /// download is in flight. Those are the maintenance gate's to judge, inside
    /// <c>ApplyAndRestartAsync</c>, atomically with acquiring the lease. Duplicating them here would
    /// read them a second time, outside that lock, and invite the two answers to disagree.</para>
    /// </summary>
    internal static bool ShouldInstallAutomatically(
        bool featureEnabled,
        bool automaticCheckEnabled,
        bool automaticInstallEnabled,
        bool applyInFlight,
        bool exclusiveMaintenanceActive) =>
        featureEnabled
        && automaticCheckEnabled
        && automaticInstallEnabled
        && !applyInFlight
        && !exclusiveMaintenanceActive;
}
