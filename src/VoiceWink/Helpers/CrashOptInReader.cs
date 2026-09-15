namespace VoiceWink.Helpers;

/// <summary>
/// Reads the crash-reporting opt-in flag directly from <c>settings.json</c> without
/// constructing a <c>SettingsService</c>. Called before Serilog is configured — a real
/// SettingsService would log and start its own debounce timer, both undesirable at that
/// point in startup.
///
/// <para>Fails closed on any IO or parse error: consent must default off so an empty or
/// corrupt settings file never silently activates telemetry.</para>
/// </summary>
internal static class CrashOptInReader
{
    public static bool Read(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath)) return false;
            using var doc = global::System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsFilePath));
            // Guard against a top-level array or primitive — TryGetProperty throws on non-object roots.
            if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty(AppDefaults.CrashReportingOptIn, out var el)
                   && el.ValueKind == global::System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
