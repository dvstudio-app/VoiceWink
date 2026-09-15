using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.AppMode;

/// <summary>
/// One-way migration of the pre-rename <c>powerModeConfigs</c> settings key (the feature was
/// called "Power Mode" until 2026-07-08). Presence-based: if the new key exists at all — even as
/// an explicitly stored empty string, e.g. after a Reset-all-settings defaults pass — it wins and
/// the legacy value is never consulted. The legacy key is removed regardless of the decision so
/// it can never linger into settings exports (both <c>ImportExportService</c> and
/// <c>GdprExportService</c> snapshot raw settings.json keys).
/// </summary>
/// <remarks>
/// Idempotent and cheap once the legacy key is gone (a locked dictionary miss). Called eagerly
/// from <c>App.OnLaunched</c> before any window exists, and defensively from every raw-settings
/// consumer: <see cref="AppModeManager.GetConfigs"/> and both export services (there, BEFORE
/// their <c>Flush()</c> — the ordering is load-bearing so the removal is materialized into the
/// very snapshot being exported).
/// </remarks>
public static class AppModeSettingsMigration
{
    private static ILogger Logger => Log.ForContext(typeof(AppModeSettingsMigration));

    public static void Run(SettingsService settings)
    {
        if (!settings.Contains(AppDefaults.AppModeConfigs))
        {
            var legacyJson = settings.GetString(AppDefaults.LegacyPowerModeConfigs, "");
            if (!string.IsNullOrEmpty(legacyJson))
            {
                settings.SetString(AppDefaults.AppModeConfigs, legacyJson);
                Logger.Information("Migrated App Mode configs from legacy settings key ({Chars} chars)",
                    legacyJson.Length);
            }
        }
        settings.Remove(AppDefaults.LegacyPowerModeConfigs);
    }
}
