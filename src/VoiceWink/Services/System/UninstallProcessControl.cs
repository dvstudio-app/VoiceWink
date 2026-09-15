using System.Diagnostics;

namespace VoiceWink.Services.System;

/// <summary>
/// INS-4 production side effects for <see cref="UninstallRunningInstanceStop"/>, kept separate so
/// that type stays a pure decision plus an orchestration loop.
///
/// <para><b>Every operation goes through the handle opened at ENUMERATION, never through the pid.</b>
/// That is the whole design, and it exists because a pid recheck cannot close the reuse race:
/// <c>Process.GetProcessById(pid).Kill()</c> resolves whoever owns that pid AT KILL TIME, so a
/// VoiceWink that quits between the identity check and the kill — the exact window the graceful wait
/// makes likely — hands the kill to an unrelated new process. Measured, not assumed: an
/// <c>InstallEpoch</c>-style probe confirmed (a) a handle opened during enumeration still permits
/// <c>StartTime</c> and <c>Kill</c>, and (b) killing through a retained handle after the process has
/// already exited is INERT — it cannot reach a later occupant of the pid, because the call never
/// resolves a pid at all.</para>
///
/// <para><b>Pre-bootstrap constraint:</b> everything here runs inside Velopack's uninstall fast
/// callback, so it is pure managed with NO <c>Microsoft.UI.Xaml</c> in scope — the same rule
/// <c>Program.cs</c> states for that whole stage.</para>
/// </summary>
internal static class RealProcessControl
{
    /// <summary>pid → the <see cref="Process"/> whose handle was opened during enumeration. The pid
    /// is only ever a LOOKUP KEY here; no operation below re-resolves it against the OS.</summary>
    private static readonly Dictionary<int, Process> Held = new();

    /// <summary>Retain a candidate and force its handle open. Returns false when the handle cannot be
    /// opened (an elevated instance), which the caller must treat as "not a candidate" — without a
    /// handle there is no safe way to kill it later.</summary>
    internal static bool TryHold(Process p)
    {
        try
        {
            _ = p.SafeHandle;   // opens and caches the handle; Kill() reuses it
            Held[p.Id] = p;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Release every retained handle. Called once at the end of the pass — until then the
    /// handles are what make the kills identity-safe.</summary>
    internal static void ReleaseAll()
    {
        foreach (var p in Held.Values)
        {
            try { p.Dispose(); } catch { /* best effort */ }
        }
        Held.Clear();
    }

    /// <summary>True when the retained process is gone. An unheld pid reads as exited: we never
    /// enumerated it, so there is nothing of ours to stop.</summary>
    internal static bool HasExited(int pid)
    {
        if (!Held.TryGetValue(pid, out var p)) return true;
        try { return p.HasExited; }
        catch { return true; }
    }

    /// <summary>Start time via the retained handle, or <see cref="DateTime.MinValue"/> when it cannot
    /// be read. <c>MinValue</c> is refused by the caller rather than compared, so an unreadable
    /// identity can never satisfy the PID-reuse guard by matching itself.</summary>
    internal static DateTime StartTimeOrDefault(int pid)
    {
        if (!Held.TryGetValue(pid, out var p)) return DateTime.MinValue;
        try { return p.StartTime; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Terminate through the RETAINED handle. Refuses outright for an unheld pid rather than
    /// falling back to <c>GetProcessById</c> — that fallback is precisely the race this type exists
    /// to remove, and a silent reintroduction of it would be invisible.</summary>
    internal static void Kill(int pid)
    {
        if (!Held.TryGetValue(pid, out var p))
            throw new InvalidOperationException("no retained handle for this pid; refusing a pid-resolved kill");
        p.Kill();
    }
}

/// <summary>
/// INS-4: asks a running VoiceWink to quit GRACEFULLY.
///
/// <para><b>A window message cannot do this, and that finding is the reason this type exists.</b>
/// The obvious signal — <c>PostMessage(WM_CLOSE)</c>, which is also exactly what
/// <c>Process.CloseMainWindow()</c> sends — is what the title-bar X sends, and the app deliberately
/// INTERCEPTS it: the main window's <c>Closed</c> handler hides to tray and returns early unless
/// <c>_isQuitting</c> is already set. So a WM_CLOSE from another process hides the window, the
/// process survives the wait, and the kill fallback becomes the NORMAL path — the precise outcome
/// the graceful step exists to prevent. Both in-process quit paths (tray Quit, update-apply quit)
/// set that flag first, and no out-of-process message can.</para>
///
/// <para>So the signal is <see cref="UninstallQuitSignal"/>, a session-scoped named event the app
/// listens on, which routes into <c>RequestQuit</c> (the Uninstall reason) — the same implementation
/// the update path and the TRN-59 self-restart use, so the routes can never drift.</para>
/// </summary>
internal static class RealGracefulQuit
{
    /// <summary>Returns true only when a graceful quit was actually REQUESTED of something that was
    /// listening. False means no attempt happened — the caller logs that honestly rather than
    /// claiming the app was asked to close, which an earlier revision did unconditionally.
    /// <para>The <paramref name="pid"/> is accepted for symmetry with the caller's per-candidate
    /// loop and deliberately unused: the channel is session-scoped and the single-instance mutex
    /// means at most one of our instances is listening in THIS session, so there is nothing to
    /// address.</para>
    /// <para><b>One corner where the caller's log line overclaims, stated rather than hidden:</b> the
    /// mutex is session-local too, so the same user CAN have one instance per session (RDP, Fast User
    /// Switching). This signals only our own session's event, yet the runner logs "asked to quit
    /// gracefully" against every accepted pid — including another session's, which was never asked and
    /// is then killed through its retained handle. The OUTCOME is right (that instance holds the same
    /// install's files open and must go); only the line describing it is generous. Rare on client
    /// Windows, and not worth a per-pid channel to fix.</para></summary>
    internal static bool RequestGracefulQuit(int pid) => UninstallQuitSignal.RequestQuit();
}
