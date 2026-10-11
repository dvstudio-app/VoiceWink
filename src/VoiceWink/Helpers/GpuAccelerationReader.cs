namespace VoiceWink.Helpers;

/// <summary>
/// TRN-53: reads the GPU-acceleration toggle directly from <c>settings.json</c> without
/// constructing a <c>SettingsService</c> (through <see cref="PreBootSettingReader"/>) — the
/// `CrashOptInReader` pattern, needed for the same reason: the consumer
/// (`WhisperBackendLog.PinLoadOrder` + the Parakeet launch-mode decision) runs at the very top of
/// `App.ConfigureServices`, before any service exists, because `RuntimeOptions` is process-wide and
/// frozen at the first native load.
///
/// <para><b>Fails OPEN to enabled — the deliberate opposite of `CrashOptInReader`.</b> That
/// reader guards CONSENT, so an unreadable file must never activate telemetry; this one guards a
/// PERFORMANCE PREFERENCE whose default is ON, so an absent key, a fresh install, or a corrupt
/// file gets the default experience (GPU where the probes prove it usable) rather than a silent
/// downgrade to CPU. The setting takes effect at next app start by construction — the pin is
/// frozen at first native load — and the Settings copy says so.</para>
/// </summary>
internal static class GpuAccelerationReader
{
    public static bool Read(string settingsFilePath) =>
        PreBootSettingReader.ReadBool(settingsFilePath, AppDefaults.GpuAccelerationEnabled, whenAbsent: true, whenUnreadable: true);
}
