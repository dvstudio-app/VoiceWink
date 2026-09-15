namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision for the main window's minimize behaviour (owner request 2026-07-22): should a
/// minimize HIDE the window to the system tray, or leave it as a normal taskbar minimize?
/// </summary>
internal static class WindowMinimizePolicy
{
    /// <summary>
    /// Hide-to-tray on minimize iff the tray icon is actually ready AND the user opted in
    /// (<see cref="AppDefaults.MinimizeToTray"/>). The <paramref name="trayReady"/> operand is
    /// MANDATORY: without a registered tray icon (onboarding / stale-acceptance modal), hiding
    /// would strand the window with no way to restore it — so a not-ready tray always means a
    /// normal taskbar minimize regardless of the preference.
    /// </summary>
    internal static bool ShouldHideToTray(bool trayReady, bool minimizeToTrayEnabled) =>
        trayReady && minimizeToTrayEnabled;
}
