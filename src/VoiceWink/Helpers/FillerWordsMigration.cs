using Serilog;
using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// One-way repair of a stored filler list that is exactly the pre-2026-09-13 default
/// (<see cref="AppDefaults.LegacyDefaultFillerWords"/>, the one that deleted Dutch/German "er",
/// French "eh" and "ah" from every dictation). The list is normally ABSENT from settings — the
/// Settings box persists it only on a change — so most installs pick the new default up by
/// themselves. The exception is an install where the user pressed "Reset to defaults" beside the
/// box: that button wrote the old literal, and it would keep deleting those words forever.
///
/// <para>Presence-and-value based, idempotent, one-way: a stored value EQUAL to the old default
/// is REMOVED, so the table's current default applies (and any later default change reaches the
/// install the same way). A stored value that differs — the user edited the list — is the user's
/// and is never touched, whatever it contains. The <see cref="PromptTraceKeyMigration"/> shape:
/// called eagerly in <c>App.OnLaunched</c>, fail-soft, retried next launch.
/// Pinned by <c>FillerWordsMigrationTests</c>.</para>
/// </summary>
public static class FillerWordsMigration
{
    private static ILogger Logger => Log.ForContext(typeof(FillerWordsMigration));

    /// <summary>Returns true when the stored list was the legacy default and was removed.</summary>
    public static bool Run(SettingsService settings)
    {
        try
        {
            if (!settings.Contains(AppDefaults.FillerWords))
                return false;
            var stored = settings.GetString(AppDefaults.FillerWords, "");
            if (!string.Equals(stored.Trim(), AppDefaults.LegacyDefaultFillerWords, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!settings.Remove(AppDefaults.FillerWords))
                return false;
            Logger.Information(
                "Filler word list was the pre-2026-09-13 default; removed so the current default applies");
            return true;
        }
        catch (Exception ex)
        {
            // Fail-soft: a settings hiccup must never abort launch; the filter still runs on the
            // stored list, and the next launch retries.
            Logger.Warning(ex, "FillerWordsMigration failed; will retry next launch");
            return false;
        }
    }
}
