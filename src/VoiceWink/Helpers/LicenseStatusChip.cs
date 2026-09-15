using VoiceWink.Services.Licensing;

namespace VoiceWink.Helpers;

/// <summary>
/// The License page's status chip — the one-word state beside the page title (LIC-25, owner UAT
/// 2026-09-07). Pure and tested, for the same reason <see cref="LicensePanelPlan"/> is: the WinUI
/// page cannot be constructed in the test host, and this switch used to live inside it, where the
/// <see cref="LicenseStatus.DisabledReadOnly"/> arm printed "READ-ONLY (DISABLED)" — the enum's own
/// name leaking to the user. "Read-only" is the app's internal name for the mode (recording paused,
/// History export and Dictionary still open); the panel body already explains what still works, so
/// the chip says only what the licence IS.
/// </summary>
public static class LicenseStatusChip
{
    /// <param name="status">The cached licence status (<c>LicenseViewModel.Status</c>).</param>
    /// <param name="storedKeyExpired">
    /// <c>LicenseService.IsStoredKeyExpired()</c>. An expired key is a KIND of Invalid and the panel
    /// already says so; the chip agreeing costs one input and removes a same-screen contradiction
    /// (LIC-13).
    /// </param>
    public static string Label(LicenseStatus status, bool storedKeyExpired) => status switch
    {
        LicenseStatus.Invalid when storedKeyExpired => "EXPIRED",
        LicenseStatus.Unlicensed        => "NOT ACTIVATED",
        // "TRIAL" since LIC-21: the local window is the only try path, so there is no trial KEY left
        // for the word to be confused with (it read TRYOUT from 2026-09-02 to 2026-09-06).
        LicenseStatus.FirstRunGrace     => "TRIAL",
        LicenseStatus.Activated         => "ACTIVE",
        LicenseStatus.OfflineGrace      => "OFFLINE GRACE",
        LicenseStatus.GraceExpired      => "GRACE EXPIRED",
        LicenseStatus.Invalid           => "INVALID",
        LicenseStatus.DisabledReadOnly  => "DISABLED",
        // A member added to the enum without a decision here still renders SOMETHING legible rather
        // than an empty chip; LicenseStatusChipTests' sweep is what makes the omission visible.
        _                               => status.ToString().ToUpperInvariant(),
    };
}
