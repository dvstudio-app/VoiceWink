namespace VoiceWink.Helpers;

/// <summary>
/// Single source of truth for the main window's title AND the second-instance window
/// matcher (<c>App.ActivateExistingInstance</c>). The two must never drift: dropping the
/// version from the title (2026-07-10) would have broken second-instance restore because
/// the matcher still looked for the old "VoiceWink v" prefix (caught in Codex review) —
/// relaunching VoiceWink while the first instance sat in the tray would silently exit
/// without restoring the window.
/// </summary>
public static class MainWindowIdentity
{
    public const string BaseTitle = "VoiceWink";

    /// <summary>
    /// The "VoiceWink (Debug)" title Debug builds carried until 2026-09-22 (owner: the
    /// marker is noise on the development machine, where every build is a Debug build).
    /// Kept as a MATCHER input only — never set on a window any more — so a new build's
    /// second instance still restores an OLD Debug build's running window, exactly as the
    /// legacy "VoiceWink v…" prefix below does.
    /// </summary>
    public const string LegacyDebugTitle = "VoiceWink (Debug)";

    /// <summary>The title THIS build's main window carries — the same in every configuration.</summary>
    public const string Title = BaseTitle;

    /// <summary>
    /// Win32 class name the Windows App SDK registers for WinUI 3 desktop windows.
    /// Checked alongside the title match so an unrelated window that happens to be
    /// titled "VoiceWink…" (e.g. a browser window on a page about VoiceWink) is never
    /// activated by mistake.
    /// </summary>
    public const string WinUIWindowClassName = "WinUIDesktopWin32WindowClass";

    /// <summary>
    /// True when <paramref name="title"/> is a VoiceWink main-window title: the current
    /// plain title, the pre-2026-09-22 <see cref="LegacyDebugTitle"/>, or the
    /// pre-2026-07-10 versioned "VoiceWink v…" form — so a new build's second instance
    /// still restores an OLD build's running window.
    /// </summary>
    public static bool MatchesTitle(string? title) =>
        title is BaseTitle or LegacyDebugTitle
        || (title?.StartsWith("VoiceWink v", StringComparison.Ordinal) == true);
}
