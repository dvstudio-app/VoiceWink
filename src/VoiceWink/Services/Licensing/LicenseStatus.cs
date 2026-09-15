namespace VoiceWink.Services.Licensing;

/// <summary>
/// The licensing state of the app as observed by <see cref="LicenseService"/>.
/// Drives the startup gate in App.xaml.cs and the Settings/Onboarding UI.
/// </summary>
public enum LicenseStatus
{
    /// <summary>Never activated on this machine.</summary>
    Unlicensed,

    /// <summary>Online-validated within the last validation interval.</summary>
    Activated,

    /// <summary>Offline but within 30 days of last successful validation.</summary>
    OfflineGrace,

    /// <summary>Offline more than 30 days — blocks new recordings; history/export stays accessible.</summary>
    GraceExpired,

    /// <summary>Key revoked, tampered, or not recognized by LemonSqueezy.</summary>
    Invalid,

    /// <summary>
    /// The license server reports the key as disabled — a manual merchant action whose
    /// cause the app cannot know (refund, fraud, chargeback, replacement, or error; LIC-8:
    /// never presented to the user as proof of a refund). App stays open in read-only mode
    /// so the user can export their transcriptions; new recordings are blocked.
    /// </summary>
    DisabledReadOnly,

    /// <summary>
    /// The free trial: a time-limited local window on first install ("Start free trial" in
    /// onboarding — no key, no email, no account). All features work; a persistent banner reminds the
    /// user to activate. Since LIC-21 (owner decision 2026-09-06) it is the ONLY try path — the
    /// Lemon Squeezy trial key was retired; expiring keys still exist (a refunded key arrives as
    /// <c>expired</c>) and activate as <see cref="Activated"/>.
    /// </summary>
    FirstRunGrace,
}
