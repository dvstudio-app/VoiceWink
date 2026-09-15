using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// UPD-3c: the one-shot "show the window after an update restart" rule, extracted so the
/// read/consume split is unit-testable without launching WinUI (same pure-helper pattern as
/// <see cref="MiniRecorderRecreateGate"/> / <c>UpdateScheduleDecision</c>).
///
/// <para>An update restart is an attended action, not a cold boot: the apply path persists
/// <see cref="AppDefaults.ShowWindowAfterUpdateRestart"/> right before the graceful
/// quit-for-update, and the NEXT launch overrides the StartMinimized preference once so the
/// user gets visible confirmation (the What's-new dialog). The flag is READ early (to compute
/// launch visibility) but CONSUMED only when the onboarded post-legal path actually proceeds —
/// a legal decline/exit must preserve the one-shot for the next accepted launch.</para>
/// </summary>
internal static class UpdateRestartVisibility
{
    /// <summary>Reads the one-shot WITHOUT consuming it — safe before the legal gate.</summary>
    public static bool ReadShowAfterUpdate(SettingsService settings) =>
        settings.GetBool(AppDefaults.ShowWindowAfterUpdateRestart, false);

    /// <summary>
    /// Effective start-minimized INTENT for this launch: the user's preference, overridden by
    /// the one-shot. Intent, not actual visibility — the tray-not-ready fallback can leave the
    /// window visible independently.
    /// </summary>
    public static bool StartMinimizedIntent(bool startMinimizedSetting, bool showAfterUpdate) =>
        startMinimizedSetting && !showAfterUpdate;

    /// <summary>
    /// Consumes the one-shot. Call ONLY when the onboarded post-legal launch path is
    /// proceeding to the gated runtime services (clear-on-use): clearing any earlier would
    /// burn the confirmation on a launch that never got to show it.
    /// </summary>
    public static void ConsumeShowAfterUpdate(SettingsService settings)
    {
        if (settings.GetBool(AppDefaults.ShowWindowAfterUpdateRestart, false))
            settings.SetBool(AppDefaults.ShowWindowAfterUpdateRestart, false);
    }
}
