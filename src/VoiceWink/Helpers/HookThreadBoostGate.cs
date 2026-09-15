namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision helper for "should I bump the priority of the SharpHook callback
/// thread that just delivered an event?". Split out from
/// <see cref="VoiceWink.Services.Input.HotkeyService"/> so the per-hook-lifecycle
/// behavior (re-boost on every new callback thread, even after a watchdog-driven
/// hook restart) can be unit-tested without spinning up libuiohook.
///
/// <para><b>Why this matters.</b> Windows' <c>LowLevelHooksTimeout</c> (~300ms)
/// kills any LL-hook that doesn't respond in time. Under CPU contention the
/// hook callback thread can miss that deadline, Windows silently removes the
/// hook, and our watchdog rebuilds it on a brand-new thread. If we only boost
/// once per process, the recovery path runs at default priority — which is
/// exactly the condition the boost is meant to prevent. The gate ensures we
/// re-boost on every fresh thread.</para>
///
/// <para>Pinned by <c>HookThreadBoostGateTests</c>. Codex 2026-05-23 review
/// caught the original process-wide flag and motivated this split.</para>
/// </summary>
internal static class HookThreadBoostGate
{
    /// <summary>
    /// Returns true iff the caller should run the priority-boost native call
    /// for the current callback thread. The caller is responsible for updating
    /// <paramref name="lastBoostedThreadId"/> after the boost attempt (success
    /// or terminal failure) — the gate is stateless.
    /// </summary>
    /// <param name="lastBoostedThreadId">
    /// Managed thread id captured the last time we attempted a boost, or
    /// <c>0</c> if no attempt has been made yet.
    /// </param>
    /// <param name="currentThreadId">
    /// Managed thread id of the callback thread that just delivered an event
    /// (<c>Environment.CurrentManagedThreadId</c> at the call site).
    /// </param>
    public static bool ShouldBoost(int lastBoostedThreadId, int currentThreadId)
    {
        // currentThreadId == 0 is impossible for a live managed thread, but
        // guard against it so a stray 0/0 comparison never short-circuits a
        // legitimate first-event boost.
        if (currentThreadId == 0)
            return false;
        return lastBoostedThreadId != currentThreadId;
    }
}
