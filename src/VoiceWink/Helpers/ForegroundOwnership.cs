using System;

namespace VoiceWink.Helpers;

/// <summary>
/// Is the foreground window OURS? One implementation, because two would drift.
///
/// <para>Extracted from <c>MainViewModel</c> by UI-7, which needs the same test in
/// <c>App</c>'s picker handlers — the pre-dialog foreground must not be captured when it is
/// already VoiceWink. The <c>MainViewModel</c> copy now delegates here rather than duplicating
/// four lines whose failure mode is a private dictation pasted into the wrong window.</para>
/// </summary>
internal static class ForegroundOwnership
{
    /// <summary>
    /// True when the foreground window belongs to THIS process — VoiceWink's main window, a dialog,
    /// or the pill (TRN-17).
    ///
    /// <para>Exists because the retry picker foregrounds the main window to show itself, so after a
    /// CANCEL the foreground is ours and a fresh "paste wherever the user is now" capture would
    /// name VoiceWink. A zero/unreadable pid answers FALSE — the fallback it guards discards a
    /// fresh capture, and doing that on an unreadable answer would silently pin every retry to a
    /// possibly-stale handle.</para>
    ///
    /// <para>The retry caller reads the foreground a SECOND time when this answers false (through
    /// <c>CaptureFreshPasteTarget</c>), and that race is deliberately not closed: the two reads are
    /// microseconds apart, and if the user did switch windows between them the second read is the
    /// more correct answer to "where is the user now" — which is the contract. Passing the handle
    /// through would freeze a staler one to remove a race whose only outcome is being right.</para>
    ///
    /// <para><b>UI-7's caller wants the handle too</b>, and takes the same view: it reads the
    /// foreground itself and calls <see cref="IsOurOwnProcess"/> on that exact handle, so the
    /// capture and the ownership test cannot disagree about which window they described.</para>
    /// </summary>
    internal static bool IsForegroundOurs() => IsOurOwnProcess(NativeInterop.GetForegroundWindow());

    /// <summary>The same test against a handle the caller has already read. A zero handle, or one
    /// whose pid cannot be read, answers FALSE — "not provably ours", which every caller then
    /// treats conservatively on its own terms.</summary>
    internal static bool IsOurOwnProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        NativeInterop.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }
}
