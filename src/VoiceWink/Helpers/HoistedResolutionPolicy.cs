namespace VoiceWink.Helpers;

/// <summary>
/// Decides whether a device resolution that was started EARLY (overlapped with the recording
/// pre-flight) is still trustworthy enough to use, or must be discarded and resolved fresh.
///
/// <para><b>Why a bound is needed at all.</b> The hoist exists to overlap the ≤250 ms App Mode
/// detect with the ≤1000 ms device resolution (two sequential 500 ms legs when a pinned device
/// is missing). If something slow intervenes instead — a local Whisper model load, a model
/// download, a contended detect — the chosen device can go stale in a way NO exception reveals:
/// Windows changes the default microphone, and the previously-resolved endpoint still activates
/// successfully, so the recording silently captures from the wrong device
/// (<c>ShouldRetryWithDefault</c> never fires because nothing threw).</para>
///
/// <para><b>Why a time bound rather than "did a model load run?".</b> Elapsed time catches every
/// slow cause, including ones not predicted; a branch-specific check only catches the one that
/// was thought of.</para>
///
/// <para><b>Over the bound the behaviour is exactly today's</b> — discard, resolve fresh, pay the
/// same cost the sequential path always paid. So the bound can only cost the optimisation, never
/// correctness.</para>
///
/// <para><b>Owner decision, 2026-08-02.</b> Within the bound a residual window remains: a default
/// microphone changed inside it is missed for that one recording. Accepted on the evidence that
/// the resolve→activation window ALREADY spans 150–3300 ms today (bounded constructor plus
/// <c>StartRecording()</c>), so the hoist widens an existing window by at most the App Mode
/// detect rather than opening a new one. Recorded in the session's 50-decision.md.</para>
/// </summary>
internal static class HoistedResolutionPolicy
{
    /// <summary>Maximum age of a hoisted resolution at the moment ownership transfers.</summary>
    internal static readonly TimeSpan MaxHoistAge = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// True when a resolution started <paramref name="ageSinceResolveStarted"/> ago may still be
    /// used. A negative age (clock adjustment, or a caller stamping out of order) is NOT trusted —
    /// it degrades to a fresh resolve, because the safe direction is always "resolve again".
    /// </summary>
    public static bool ShouldUseHoisted(TimeSpan ageSinceResolveStarted) =>
        ageSinceResolveStarted >= TimeSpan.Zero && ageSinceResolveStarted <= MaxHoistAge;
}
