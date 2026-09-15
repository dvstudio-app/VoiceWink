namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision for the MiniRecorder pill's right-click hide (owner request 2026-07-30): may a hide
/// be accepted right now?
/// </summary>
internal static class PillHidePolicy
{
    /// <summary>
    /// Accept a hide iff the tray icon is actually ready AND the app is not quitting. The
    /// <paramref name="trayReady"/> operand is MANDATORY for the same reason as
    /// <see cref="WindowMinimizePolicy.ShouldHideToTray"/>'s: the tray "Show mini recorder" item is the
    /// user's deliberate restore path, and hiding without a registered tray icon would leave a long
    /// generation's pill recoverable only by accident (whenever the next presentation change happens to
    /// release the latch). Refusing keeps the pill on screen — in the way, but never unrecoverable.
    /// <paramref name="isQuitting"/> keeps a shutdown from spending work on a pill that is about to go.
    /// </summary>
    internal static bool ShouldAcceptHide(bool trayReady, bool isQuitting) => trayReady && !isQuitting;
}
