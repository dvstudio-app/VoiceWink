namespace VoiceWink.Helpers;

/// <summary>
/// Reads the "Verbose debug logging" flag directly from <c>settings.json</c> before any service
/// exists — the <see cref="CrashOptInReader"/> / <see cref="GpuAccelerationReader"/> shape — so the
/// logger's minimum level (<see cref="LogLevelControl"/>) is right from the first line, including
/// the startup lines a support case about a slow or failing start needs most.
///
/// <para>Fails toward the shipped default (OFF, Information): a missing, corrupt or non-object
/// file never turns Debug logging on by itself.</para>
/// </summary>
internal static class VerboseLoggingReader
{
    public static bool Read(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath)) return false;
            using var doc = global::System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsFilePath));
            if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty(AppDefaults.VerboseLoggingEnabled, out var el)
                   && el.ValueKind == global::System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
