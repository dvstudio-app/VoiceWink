using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// REL-17 (diff review round 4): the DURABLE apply for the two diagnostics consent
/// toggles (raw prompt tracing + audio retention). The contract is asymmetric and
/// fail-PRIVACY-SAFE: whatever was requested, a persistence failure always ends with the
/// effective in-memory value OFF — disabling sticks even when the disk write fails
/// (collection stops NOW; the next launch may resurrect the stale on-disk value, which
/// the caller surfaces), and enabling never stays active without a durable write (the
/// round-3 revert restored the PREVIOUS value, which re-enabled tracing after a failed
/// opt-out — exactly backwards). Pure over the injected <see cref="SettingsService"/>;
/// pinned by <c>DiagnosticsConsentTests</c> via the <c>_writeFile</c> seam.
/// </summary>
public static class DiagnosticsConsent
{
    /// <summary>Outcome of one toggle apply: the value now in effect, and whether it is
    /// durable on disk.</summary>
    public readonly record struct ConsentApplyResult(bool Effective, bool Persisted);

    public static ConsentApplyResult Apply(SettingsService settings, string key, bool requested)
    {
        settings.SetBool(key, requested);
        try
        {
            settings.FlushOrThrow();
            // The ABSENCE of an exception is not proof of a write. FlushOrThrow returns SILENTLY
            // when persistence is suppressed (the data-erasure quiesce, which a failed erasure
            // pass can leave engaged with settings.json still on disk), so a durability-required
            // caller must check the flag explicitly — the same rule LicenseService follows for
            // ClearLocalLicenseState, and `IsPersistenceSuppressed`'s own doc states it. Without
            // this, an opt-out could clear its warning while the old on-disk `true` came back
            // next launch (Codex diff review, UPD-4b).
            if (settings.IsPersistenceSuppressed) return FailOff(settings, key);
            return new ConsentApplyResult(requested, true);
        }
        catch
        {
            return FailOff(settings, key);
        }
    }

    // Fail privacy-safe: OFF in memory regardless of the requested direction.
    private static ConsentApplyResult FailOff(SettingsService settings, string key)
    {
        settings.SetBool(key, false);
        return new ConsentApplyResult(false, false);
    }
}
