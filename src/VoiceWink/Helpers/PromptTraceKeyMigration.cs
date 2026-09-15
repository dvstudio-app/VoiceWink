using Serilog;
using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// One-way removal of the pre-REL-17 DEBUG-only prompt-trace toggle key
/// (<see cref="AppDefaults.LegacyPromptTraceLoggingEnabled"/>). The trace now ships in ALL
/// builds behind the NEW <see cref="AppDefaults.PromptTraceLoggingOptIn"/> key, and a stale
/// Debug-era <c>true</c> must never silently activate Release tracing (Codex plan round 3) —
/// so the legacy key is deleted, never aliased: the value does NOT carry over, and re-enabling
/// is a fresh explicit opt-in in Settings -> Diagnostics. Presence-based and idempotent (the
/// <see cref="AppModeSettingsMigration"/> shape); called eagerly in <c>App.OnLaunched</c>
/// before the trace gates are wired. ImportExportService additionally skips both keys so an
/// old exported profile can't resurrect the legacy key between launches.
/// Pinned by <c>PromptTraceKeyMigrationTests</c>.
/// </summary>
public static class PromptTraceKeyMigration
{
    private static ILogger Logger => Log.ForContext(typeof(PromptTraceKeyMigration));

    public static void Run(SettingsService settings)
    {
        try
        {
            if (settings.Remove(AppDefaults.LegacyPromptTraceLoggingEnabled))
            {
                Logger.Information(
                    "Removed legacy Debug-only prompt-trace key '{Key}' (REL-17: tracing is a fresh opt-in)",
                    AppDefaults.LegacyPromptTraceLoggingEnabled);
            }
        }
        catch (Exception ex)
        {
            // Fail-soft: a settings hiccup must never abort launch; the key is inert either
            // way (nothing reads it) and the next launch retries.
            Logger.Warning(ex, "PromptTraceKeyMigration failed; will retry next launch");
        }
    }
}
