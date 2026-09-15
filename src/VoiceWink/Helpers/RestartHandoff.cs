using System.Diagnostics;
using System.Globalization;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>What the successor found when it looked for a predecessor to wait for.</summary>
public enum RestartHandoffKind
{
    /// <summary>The variable was absent: an ordinary launch. Nothing was waited for.</summary>
    NotARestart,
    /// <summary>The predecessor was alive and exited within the bound.</summary>
    PredecessorExited,
    /// <summary>The predecessor had already gone by the time this process looked.</summary>
    PredecessorAlreadyGone,
    /// <summary>The predecessor was still alive when the bound expired. The launch proceeds to the
    /// single-instance check, where <see cref="RestartHandoff.TryTakeOverMutexAfterTimeout(Mutex)"/>
    /// is the second layer: the successor waits on the MUTEX the predecessor will release rather
    /// than activating a window that is closing and exiting — a merely SLOW predecessor must not
    /// lose the user the app. Only a predecessor still holding the mutex after that is treated as
    /// wedged (the successor activates it and exits, and the user keeps the app they had).</summary>
    TimedOut,
    /// <summary>The variable was present but unusable (not a process id, this process's own pid,
    /// or the wait threw). Treated as a launch with nothing to wait for.</summary>
    Invalid,
}

/// <summary>The outcome of one handoff, kept for the single log line <c>App</c> writes once Serilog
/// exists. <see cref="Detail"/> is the reason for <see cref="RestartHandoffKind.Invalid"/>, and for
/// <see cref="RestartHandoffKind.TimedOut"/> what the mutex takeover then did.</summary>
public sealed record RestartHandoffOutcome(RestartHandoffKind Kind, int? PredecessorPid, long ElapsedMs, string? Detail)
{
    public static RestartHandoffOutcome NotARestart { get; } = new(RestartHandoffKind.NotARestart, null, 0, null);
}

/// <summary>
/// TRN-59: the SUCCESSOR half of "Restart now". When a running VoiceWink restarts itself it starts
/// the successor first and then quits gracefully; the successor must not claim the single-instance
/// mutex, the daily log file, the settings file or the capture device while the predecessor still
/// holds them. So the predecessor hands its pid to the child on a CHILD-ONLY environment variable
/// (never a process-global write — the TRN-53 rule for the Parakeet child's launch block), and
/// <see cref="WaitForPredecessorExit()"/>, called from <c>Program.Main</c> before WinUI starts, waits
/// — bounded — for that PROCESS to exit. Process exit, not the mutex: the mutex is released before
/// <c>Log.CloseAndFlush()</c> in <c>App.Cleanup</c>, and the un-shared daily log file would still
/// be held for the few hundred milliseconds between the two. Waiting for exit is the ordering the
/// Velopack update path already relies on (<c>Update.exe</c> waits for our exit before it swaps
/// files); this is the same ordering with the successor doing the waiting.
///
/// <para><b>An environment variable, not an argument</b>, because the portable launcher is a fixed
/// VBS (<c>WshShell.Run """dotnet"" VoiceWink.dll"</c>) that forwards no arguments, while every
/// launcher — VBS, apphost, <c>dotnet … .dll</c> — inherits the environment.</para>
///
/// <para><b>The variable is cleared from this process's environment before anything else can
/// spawn.</b> Every path that saw a value clears it, valid or not: the resident parakeet-server and
/// the warm-up child inherit our environment, and a stale predecessor pid must never reach a
/// process that would read it as its own instruction.</para>
///
/// <para><b>Never throws.</b> A handoff must not stop a launch. Accepted bound, stated: a pid reused
/// by an unrelated process inside the ~1 s between the spawn and this read makes the successor wait
/// on a stranger for at most the timeout, then launch normally — a delay, never a wrong action.</para>
///
/// <para><b>A timeout is NOT "the user keeps the app they had" on its own — that needs the second
/// layer.</b> A predecessor can be merely slow: a Parakeet preload can hold the server gate through
/// its 30 s health budget, and <c>Cleanup</c> waits on that gate unbounded before the 5 s retire.
/// If the successor simply lost the mutex claim after its wait, it would activate a window that is
/// already closing and exit, and the predecessor would then finish exiting: NO VoiceWink running.
/// So on a timed-out handoff the single-instance loser waits on the MUTEX instead
/// (<see cref="TryTakeOverMutexAfterTimeout(Mutex)"/>), which the predecessor releases as the last
/// act of <c>Cleanup</c> — acquired the moment it does, an abandoned mutex (the predecessor died
/// holding it) counting as acquired. The cost, accepted and confined to this degraded path: the
/// daily log can overlap for a moment, and Serilog's rolling file sink then opens a
/// sequence-suffixed file. A predecessor still holding the mutex after that bound is wedged: the
/// successor leaves one line in <c>install.log</c> (Serilog is not up in a loser), activates it
/// and exits. That fully wedged case therefore leaves no Serilog line in either process.</para>
/// </summary>
public static class RestartHandoff
{
    /// <summary>The variable the predecessor sets on the successor's start info.</summary>
    public const string PredecessorPidVariable = "VOICEWINK_RESTART_PREDECESSOR_PID";

    /// <summary>How long the successor waits for the predecessor to EXIT. A graceful quit is
    /// normally over in a few seconds; the longest bounded step in <c>App.Cleanup</c> is the
    /// parakeet-server retire wait, and the unbounded one (the server gate held by a preload's
    /// health wait) is what the mutex layer below exists for.</summary>
    public const int PredecessorExitTimeoutMs = 30_000;

    /// <summary>How long a successor whose exit wait TIMED OUT keeps waiting on the single-instance
    /// mutex before treating the predecessor as wedged. Velopack's own wait-for-exit is 60 s; a
    /// predecessor that has not released the mutex after the two bounds together is not exiting.</summary>
    public const int MutexTakeoverTimeoutMs = 60_000;

    /// <summary>The outcome of this process's handoff, set by <c>Program.Main</c> (stage 3b), amended
    /// by the mutex takeover, and read once by <c>App</c> for its startup log line. <c>null</c>
    /// until stage 3b ran.</summary>
    public static RestartHandoffOutcome? LastOutcome { get; internal set; }

    /// <summary>Production entry: the real process table, this process's pid, the default bound.</summary>
    public static RestartHandoffOutcome WaitForPredecessorExit()
        => WaitForPredecessorExit(ProductionProcessRunner.Instance, Environment.ProcessId, PredecessorExitTimeoutMs);

    /// <summary>The decision. Pure over its seam except for the environment read + clear.</summary>
    internal static RestartHandoffOutcome WaitForPredecessorExit(IProcessRunner processes, int currentPid, int timeoutMs)
    {
        // Detail carries the exception TYPE, never its message — the same path-free discipline as
        // AppRestartService.DescribeError (Kimi diff r1): the reachable messages here are system
        // strings today, and one rule for restart diagnostics is cheaper than two.
        string? raw;
        try { raw = Environment.GetEnvironmentVariable(PredecessorPidVariable); }
        catch (Exception ex) { return new RestartHandoffOutcome(RestartHandoffKind.Invalid, null, 0, ex.GetType().Name); }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return RestartHandoffOutcome.NotARestart;
        }

        // Clear FIRST — before parsing, before waiting — so no descendant of this process can
        // inherit the variable whatever happens below.
        try { Environment.SetEnvironmentVariable(PredecessorPidVariable, null); }
        catch { /* best-effort; the value is still consumed below */ }

        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return new RestartHandoffOutcome(RestartHandoffKind.Invalid, null, 0, "not a process id");
        }
        if (pid == currentPid)
        {
            return new RestartHandoffOutcome(RestartHandoffKind.Invalid, pid, 0, "names this process");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var predecessor = processes.GetProcessById(pid);
            if (predecessor is null)
            {
                return new RestartHandoffOutcome(RestartHandoffKind.PredecessorAlreadyGone, pid, 0, null);
            }
            return predecessor.WaitForExit(timeoutMs)
                ? new RestartHandoffOutcome(RestartHandoffKind.PredecessorExited, pid, stopwatch.ElapsedMilliseconds, null)
                : new RestartHandoffOutcome(RestartHandoffKind.TimedOut, pid, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new RestartHandoffOutcome(RestartHandoffKind.Invalid, pid, stopwatch.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The second layer, called by <c>App</c>'s constructor when the single-instance claim LOST:
    /// true means this process now OWNS the mutex and the launch continues as the winner; false
    /// means the usual loser path (activate the running instance, exit). Does nothing — and waits
    /// for nothing — unless this launch's handoff <see cref="RestartHandoffKind.TimedOut"/>: an
    /// ordinary second launch must keep activating the running instance immediately.
    /// </summary>
    public static bool TryTakeOverMutexAfterTimeout(Mutex mutex)
        => TryTakeOverMutexAfterTimeout(mutex, MutexTakeoverTimeoutMs, PrereqInstaller.LogPublic);

    /// <param name="giveUpLog">Where the give-up line goes — <c>install.log</c> in production
    /// (Serilog is not configured in a loser). Null in tests.</param>
    internal static bool TryTakeOverMutexAfterTimeout(Mutex mutex, int timeoutMs, Action<string>? giveUpLog)
    {
        var outcome = LastOutcome;
        if (outcome is null || outcome.Kind != RestartHandoffKind.TimedOut)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // The predecessor died holding it. The wait DID acquire it, and there is no state to
            // inherit from a mutex — it is ours now.
            acquired = true;
        }
        catch (Exception ex)
        {
            LastOutcome = outcome with { Detail = $"mutex wait failed: {ex.GetType().Name}" };
            return false;
        }

        if (acquired)
        {
            LastOutcome = outcome with
            {
                Detail = $"single-instance mutex taken over after a further {stopwatch.ElapsedMilliseconds} ms",
            };
            return true;
        }

        LastOutcome = outcome with { Detail = $"mutex still held after a further {stopwatch.ElapsedMilliseconds} ms" };
        try
        {
            giveUpLog?.Invoke(
                $"TRN-59 restart handoff: predecessor pid {outcome.PredecessorPid} still alive after " +
                $"{outcome.ElapsedMs} ms + {stopwatch.ElapsedMilliseconds} ms; activating it and exiting");
        }
        catch { /* a diagnostic must not change the decision */ }
        return false;
    }

    /// <summary>The ONE log line, written by <c>App</c> right after "VoiceWink starting up" — the
    /// earliest point Serilog exists. Silent for an ordinary launch and before stage 3b ran; a
    /// restart, a taken-over timeout and an unusable handoff each leave a line a support bundle can
    /// show.</summary>
    public static void LogLastOutcome(ILogger logger)
    {
        var outcome = LastOutcome;
        if (outcome is null || outcome.Kind == RestartHandoffKind.NotARestart) return;

        switch (outcome.Kind)
        {
            case RestartHandoffKind.PredecessorExited:
                logger.Information(
                    "Restarted by request (TRN-59): predecessor pid {PredecessorPid} exited after {ElapsedMs} ms",
                    outcome.PredecessorPid, outcome.ElapsedMs);
                break;
            case RestartHandoffKind.PredecessorAlreadyGone:
                logger.Information(
                    "Restarted by request (TRN-59): predecessor pid {PredecessorPid} had already exited",
                    outcome.PredecessorPid);
                break;
            case RestartHandoffKind.TimedOut:
                logger.Warning(
                    "Restarted by request (TRN-59): predecessor pid {PredecessorPid} did not exit within {ElapsedMs} ms; {Detail}",
                    outcome.PredecessorPid, outcome.ElapsedMs, outcome.Detail ?? "no mutex takeover ran");
                break;
            default:
                logger.Warning(
                    "Restart handoff variable was present but unusable ({Detail}; pid {PredecessorPid}) - treated as an ordinary launch",
                    outcome.Detail, outcome.PredecessorPid);
                break;
        }
    }
}
