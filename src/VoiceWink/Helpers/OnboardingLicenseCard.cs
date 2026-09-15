using VoiceWink.Services.Licensing;

namespace VoiceWink.Helpers;

/// <summary>
/// Which variant the onboarding License step renders in its FIRST card slot — the tryout / status
/// card. Every variant occupies the same slot: a state change re-renders the card in place, so
/// coming Back to the step never inserts, removes or reorders anything (2026-09-03 redesign; the
/// research basis is in <c>docs/plans/2026-09-03-onb-license-step-v2/50-decision.md</c>).
/// </summary>
public enum OnboardingLicenseCardVariant
{
    /// <summary>No key, free trial never started: the accent "Start free trial" button.</summary>
    Start,
    /// <summary>In the free-trial window: the countdown title and an accent "Continue".</summary>
    TryoutActive,
    /// <summary>No key, free trial run out: the card says so and Buy becomes the step's accent (LIC-21).</summary>
    TryoutEnded,
    /// <summary>A stored key in a non-activated status: a status line; the key card below carries the actions.</summary>
    StoredKey,
    /// <summary>Activated: the step is a skip screen (no cards).</summary>
    Activated,
}

/// <summary>
/// The onboarding License step's layout decision as a pure function, so it is test-pinned rather
/// than re-derived in page code-behind (which the test host cannot construct).
///
/// <para><b>The key-free start is offered ONLY to <see cref="LicenseStatus.Unlicensed"/>.</b> Every
/// other non-activated status — <c>OfflineGrace</c>, <c>GraceExpired</c>, <c>Invalid</c>,
/// <c>DisabledReadOnly</c>, reachable via Settings → "Relaunch setup wizard" — has a STORED key, and
/// for those <c>StartFirstRunGrace</c> would write the grace timestamp (consuming the one tryout)
/// while <c>GetCachedStatus</c> keeps answering from the key: the click would burn a tryout the user
/// never received and then report it as "already used". The <c>default</c> arm therefore maps to
/// <see cref="OnboardingLicenseCardVariant.StoredKey"/>, so a future <see cref="LicenseStatus"/>
/// member lands on the safe side without an edit here.</para>
/// </summary>
public static class OnboardingLicenseCard
{
    /// <param name="status">The cached licence status the step was entered with.</param>
    /// <param name="tryoutEnded">
    /// <c>LicenseService.HasFirstRunGraceEnded()</c> — the tryout was started on this device and its
    /// window has run out. Only consulted while <paramref name="status"/> is
    /// <see cref="LicenseStatus.Unlicensed"/>; in grace the status already says so.
    /// </param>
    public static OnboardingLicenseCardVariant Resolve(LicenseStatus status, bool tryoutEnded) => status switch
    {
        LicenseStatus.Activated     => OnboardingLicenseCardVariant.Activated,
        LicenseStatus.FirstRunGrace => OnboardingLicenseCardVariant.TryoutActive,
        LicenseStatus.Unlicensed    => tryoutEnded ? OnboardingLicenseCardVariant.TryoutEnded : OnboardingLicenseCardVariant.Start,
        _                           => OnboardingLicenseCardVariant.StoredKey,
    };
}
