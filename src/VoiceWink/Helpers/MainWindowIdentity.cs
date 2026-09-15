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
    public const string DebugTitle = "VoiceWink (Debug)";

    /// <summary>The title THIS build's main window carries.</summary>
#if DEBUG
    public const string Title = DebugTitle;
#else
    public const string Title = BaseTitle;
#endif

    /// <summary>
    /// Win32 class name the Windows App SDK registers for WinUI 3 desktop windows.
    /// Checked alongside the title match so an unrelated window that happens to be
    /// titled "VoiceWink…" (e.g. a browser window on a page about VoiceWink) is never
    /// activated by mistake.
    /// </summary>
    public const string WinUIWindowClassName = "WinUIDesktopWin32WindowClass";

    /// <summary>
    /// True when <paramref name="title"/> is a VoiceWink main-window title: either of the
    /// current plain titles, or the pre-2026-07-10 versioned "VoiceWink v…" form — so a
    /// new build's second instance still restores an OLD build's running window.
    /// </summary>
    public static bool MatchesTitle(string? title) =>
        title is BaseTitle or DebugTitle
        || (title?.StartsWith("VoiceWink v", StringComparison.Ordinal) == true);
}
