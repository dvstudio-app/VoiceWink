namespace VoiceWink.Helpers;

/// <summary>
/// Reads the crash-reporting opt-in flag directly from <c>settings.json</c> without
/// constructing a <c>SettingsService</c> (through <see cref="PreBootSettingReader"/>). Called before
/// Serilog is configured — a real SettingsService would log and start its own debounce timer, both
/// undesirable at that point in startup.
///
/// <para>Fails closed on any IO or parse error: consent must default off so an empty or
/// corrupt settings file never silently activates telemetry.</para>
/// </summary>
internal static class CrashOptInReader
{
    public static bool Read(string settingsFilePath) =>
        PreBootSettingReader.ReadBool(settingsFilePath, AppDefaults.CrashReportingOptIn, whenAbsent: false, whenUnreadable: false);
}
