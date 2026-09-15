using System.Diagnostics;

namespace VoiceWink.Services.System;

/// <summary>
/// INS-4 (owner report, 2026-08-18): stop a running VoiceWink before Velopack removes the install.
///
/// <para><b>What went wrong.</b> The owner uninstalled through Windows with VoiceWink running. The
/// uninstaller never mentioned the running instance, <b>reported success</b>, and the app kept
/// running — and the installer then offered to <i>repair</i> rather than install, so the uninstall
/// had not completed while telling the user it had. Files a live process holds open cannot be
/// deleted; that is the mechanism.</para>
///
/// <para><b>What this can and cannot fix.</b> It makes the common case genuinely succeed. It does
/// NOT make Velopack's reporting honest: fast callbacks are <c>void</c>, throwing is contractually
/// non-disruptive, and the hook's exit code is not a veto Update.exe reads — so there is no supported
/// way to fail an uninstall loudly. Recorded as a Velopack limitation rather than papered over.
/// Worse, on the one path that matters (a survivor locks <c>current\VoiceWink.exe</c>) the
/// <c>install.log</c> we log to is itself very likely deleted while the locked binary survives — so
/// "log loudly" is weakest exactly when it is most needed. Accepted; a marker-file channel would be
/// building machinery for a case the user already sees.</para>
///
/// <para><b>Three things make this dangerous, and each has a specific answer:</b></para>
/// <list type="number">
/// <item><b>THIS CALLBACK IS ITSELF A <c>VoiceWink.exe</c> PROCESS.</b> Velopack invokes the app
/// binary with uninstall args, so a naive name sweep includes us and the hook would kill itself
/// mid-uninstall. The self-pid exclusion is the correctness core — and note the path guard cannot
/// help here, because the running app and this process have the SAME executable path. Only the pid
/// separates them.</item>
/// <item><b>A dev or portable build must not be collateral.</b> The ownership guard is the same
/// <c>\VoiceWinkApp\current\</c> segment <see cref="VelopackUninstallCleanup"/> uses, so only the
/// Velopack install is ever touched. An unreadable path is treated as NOT ours (fail-closed): an
/// elevated instance makes <c>MainModule</c> throw, and "could not read the path" must never
/// degrade into "stop it anyway", which would make the guard name-only.</item>
/// <item><b>A hard kill can corrupt user data.</b> Settings writes are debounced 250 ms, and
/// Velopack's uninstall does not remove <c>%LOCALAPPDATA%\VoiceWink</c> (that is INS-2's husk), so a
/// killed process can lose a write it had already accepted. Graceful first, kill only as fallback.</item>
/// </list>
///
/// <para><b>The graceful signal is a NAMED EVENT, not a window message, and that is the finding a
/// self-review produced after the first design shipped the wrong one.</b> <c>WM_CLOSE</c> — which is
/// also exactly what <c>Process.CloseMainWindow()</c> sends — is what the title-bar X sends, and the
/// app deliberately INTERCEPTS it: the main window's <c>Closed</c> handler hides to tray and returns
/// early unless <c>_isQuitting</c> is already set. So a window message from another process hides the
/// window, the process survives the wait, and the kill becomes the NORMAL path while the code reads
/// as though killing were a fallback. Both in-process quit paths (tray Quit, update-apply quit) set
/// that flag first, and no out-of-process message can. <see cref="UninstallQuitSignal"/> is a
/// session-scoped event the app listens on, routed into <c>RequestQuit</c> (the Uninstall reason) —
/// the SAME implementation the update path and the TRN-59 self-restart use, so the uninstall route
/// cannot drift from it.</para>
/// <para><b>Assumption, pinned so it fails loudly if it changes:</b> the Velopack pack is
/// self-contained, so production always runs as <c>VoiceWink.exe</c>. A <c>dotnet VoiceWink.dll</c>
/// host (dev/dogfood) is invisible to the name filter AND independently excluded by the path guard.
/// If a framework-dependent pack ever ships, this silently stops finding the running app.</para>
///
/// <para><b>Logging carries no paths.</b> A candidate's executable path contains
/// <c>C:\Users\&lt;name&gt;</c>, and <c>install.log</c> is a support-visible artifact — so lines
/// classify (pid + verdict) and never quote the path, the same posture
/// <see cref="VelopackUninstallCleanup"/> takes.</para>
/// </summary>
internal static class UninstallRunningInstanceStop
{
    /// <summary>The Run-value cleanup's own constant, REFERENCED rather than repeated — slashes on
    /// both sides so a sibling directory (<c>VoiceWinkAppBackup</c>, <c>OldVoiceWinkApp</c>) cannot
    /// match. It was a second identical copy until a reviewer pointed out that three doc comments
    /// claimed the two "cannot disagree" while nothing stopped them.</summary>
    internal const string VelopackPathSegment = VelopackUninstallCleanup.VelopackPathSegment;

    /// <summary>Process name Velopack packs ship as. See the self-contained assumption above.</summary>
    internal const string ProcessName = "VoiceWink";

    /// <summary>How long the graceful quit request is given before the kill. With the confirm wait below
    /// this totals ~11 s, well inside Velopack's 30 s ceiling for the install/uninstall hooks — and
    /// this callback does almost nothing else.</summary>
    internal static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(8);

    /// <summary>How long a killed process is given to actually disappear before we log a survivor.</summary>
    internal static readonly TimeSpan KillConfirmWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// THE decision: should this candidate be stopped?
    /// <para>Fail-CLOSED on every uncertainty. A null or unreadable path is "not ours" — an elevated
    /// instance makes <c>MainModule</c> throw, and treating that as "stop anyway" would reduce the
    /// ownership guard to a name match and put dev builds in range.</para>
    /// <para><b>The path test is EXACT EQUALITY against the one executable this uninstall owns</b>
    /// (<c>%LOCALAPPDATA%\VoiceWinkApp\current\VoiceWink.exe</c>), not a segment match. Two earlier
    /// shapes were each too wide, and the second was caught in review: "contains
    /// <c>\VoiceWinkApp\current\</c>" matches every profile on the box, and adding "starts under MY
    /// <c>%LOCALAPPDATA%</c>" still accepts a nested copy such as
    /// <c>%LOCALAPPDATA%\scratch\VoiceWinkApp\current\VoiceWink.exe</c> — a portable or dev copy the
    /// uninstaller does not own, which this card explicitly promises not to touch. Only one path can
    /// be the installed binary, so only one path is accepted.</para>
    /// <para>Anchoring to the uninstalling user's own root is what bounds the blast radius to ONE
    /// user: <c>Process.GetProcessesByName</c> enumerates every session, and on an ELEVATED uninstall
    /// <c>MainModule</c> reads across users (unelevated it throws and fails closed), so a
    /// user-agnostic test would have had user A's uninstall killing user B's running app
    /// mid-sentence.</para>
    /// <para>Every residual points the same, safe way — nothing is stopped, i.e. the pre-INS-4
    /// behaviour, never collateral damage: an unreadable own-root, an uninstall elevated as a
    /// DIFFERENT account, or a path the OS reports in a form that is not character-equal (an 8.3
    /// short name, a substituted drive).</para>
    /// </summary>
    internal static bool ShouldStop(int ownPid, int candidatePid, string? candidateExePath, string? ownLocalAppData)
    {
        if (candidatePid == ownPid) return false;                     // never ourselves
        if (string.IsNullOrEmpty(candidateExePath)) return false;      // unreadable ⇒ not ours
        if (string.IsNullOrEmpty(ownLocalAppData)) return false;       // cannot bound it ⇒ stop nothing

        return string.Equals(candidateExePath, ExpectedInstalledExePath(ownLocalAppData),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ONE executable this uninstall owns. Built from the same segment constant the Run
    /// cleanup uses, so the two cannot disagree about what "the install" means.</summary>
    internal static string ExpectedInstalledExePath(string ownLocalAppData)
        => ownLocalAppData.TrimEnd('\\', '/') + VelopackPathSegment + ProcessName + ".exe";

    /// <summary>A candidate pinned at enumeration time.
    /// <para><b>The start time is a CHEAP CROSS-CHECK, not the actual defence.</b> A pid recheck
    /// cannot close the reuse race on its own — the kill would still resolve the pid afterwards, and
    /// resolve whoever owns it by then. What makes the kill identity-safe is that
    /// <see cref="RealProcessControl"/> keeps the OS handle opened during enumeration and terminates
    /// through THAT, so the call never names a pid at all. Measured: killing through a retained
    /// handle after the process has already exited is inert.</para>
    /// <para><see cref="DateTime.MinValue"/> means the identity could not be read; the runner refuses
    /// such a candidate outright rather than comparing it, because the later re-read returns MinValue
    /// too and the guard would pass by matching itself.</para></summary>
    internal readonly record struct Candidate(int Pid, DateTime StartTime);

    /// <summary>Production entry. Wired into <c>Program.Main</c>'s Velopack
    /// <c>OnBeforeUninstallFastCallback</c>, AFTER the Run-value cleanup — see the ordering note
    /// there. Never throws.</summary>
    internal static void StopVelopackOwnedInstances()
    {
        try
        {
            StopVelopackOwnedInstances(
                enumerate: EnumerateRealCandidates,
                requestQuit: RealGracefulQuit.RequestGracefulQuit,
                kill: RealProcessControl.Kill,
                hasExited: RealProcessControl.HasExited,
                startTimeOf: RealProcessControl.StartTimeOrDefault,
                sleep: global::System.Threading.Thread.Sleep,
                log: Helpers.PrereqInstaller.LogPublic);
        }
        finally
        {
            // The handles are what keep every kill identity-safe, so they are held for the WHOLE
            // pass and released exactly once, here — never per candidate inside the loop.
            RealProcessControl.ReleaseAll();
        }
    }

    /// <summary>
    /// Testable core. Every side effect is a delegate, so the whole matrix runs without starting a
    /// process or creating a window.
    /// <para><b>Per-candidate isolation is deliberate:</b> one unreadable or un-killable candidate
    /// must not abort the loop, or a single elevated instance would leave the original defect
    /// shipped for everyone else.</para>
    /// </summary>
    internal static void StopVelopackOwnedInstances(
        Func<IReadOnlyList<(Candidate Candidate, string? ExePath)>> enumerate,
        Func<int, bool> requestQuit,
        Action<int> kill,
        Func<int, bool> hasExited,
        Func<int, DateTime> startTimeOf,
        Action<TimeSpan> sleep,
        Action<string> log,
        string? ownLocalAppData = null)
    {
        try
        {
            var ownPid = global::System.Environment.ProcessId;
            ownLocalAppData ??= OwnLocalAppDataOrNull();
            var accepted = new List<Candidate>();

            foreach (var (candidate, exePath) in enumerate())
            {
                try
                {
                    if (!ShouldStop(ownPid, candidate.Pid, exePath, ownLocalAppData))
                    {
                        log($"INS-4: pid {candidate.Pid} not stopped (not this install, or self)");
                        continue;
                    }
                    if (candidate.StartTime == DateTime.MinValue)
                    {
                        // No readable identity ⇒ no PID-reuse guard is possible for it. MinValue is
                        // also what the later re-read returns on failure, so comparing it would make
                        // an unreadable process match ITSELF and pass a guard that checked nothing.
                        log($"INS-4: pid {candidate.Pid} not stopped (process identity unreadable)");
                        continue;
                    }
                    accepted.Add(candidate);
                    // Log what ACTUALLY happened. An earlier revision logged "asked to close"
                    // unconditionally, so an operator reading install.log after a failed uninstall
                    // saw "asked to close" then "did not exit; killed" and concluded the app had
                    // ignored a request that was never made.
                    log(requestQuit(candidate.Pid)
                        ? $"INS-4: pid {candidate.Pid} asked to quit gracefully"
                        : $"INS-4: pid {candidate.Pid} has no quit listener; will kill after the wait");
                }
                catch (Exception ex)
                {
                    // Per candidate: one failure must not stop the others.
                    log($"INS-4: pid {candidate.Pid} close request failed ({ex.GetType().Name})");
                }
            }

            if (accepted.Count == 0)
            {
                log("INS-4: no running instance of this install");
                return;
            }

            // ONE shared wait rather than a wait per process — the requests were already broadcast.
            sleep(GracefulWait);

            foreach (var candidate in accepted)
            {
                try
                {
                    if (hasExited(candidate.Pid))
                    {
                        log($"INS-4: pid {candidate.Pid} exited gracefully");
                        continue;
                    }
                    // PID-REUSE GUARD: the pid may now be an unrelated process.
                    if (startTimeOf(candidate.Pid) != candidate.StartTime)
                    {
                        log($"INS-4: pid {candidate.Pid} exited during the wait (pid since reused)");
                        continue;
                    }
                    kill(candidate.Pid);
                    log($"INS-4: pid {candidate.Pid} did not exit; killed");
                }
                catch (Exception ex)
                {
                    log($"INS-4: pid {candidate.Pid} kill failed ({ex.GetType().Name})");
                }
            }

            sleep(KillConfirmWait);

            foreach (var candidate in accepted)
            {
                try
                {
                    if (!hasExited(candidate.Pid) && startTimeOf(candidate.Pid) == candidate.StartTime)
                        log($"INS-4: WARNING pid {candidate.Pid} SURVIVED — uninstall will be partial");
                }
                catch (Exception ex)
                {
                    log($"INS-4: pid {candidate.Pid} final check failed ({ex.GetType().Name})");
                }
            }
        }
        catch (Exception ex)
        {
            // Velopack contract: a hook exception must not disrupt the uninstall.
            log($"INS-4: stop pass failed ({ex.GetType().Name})");
        }
    }

    // ── Production side effects ───────────────────────────────────────────────

    /// <summary>The uninstalling user's own <c>%LOCALAPPDATA%</c>, or null when it cannot be read
    /// — which <see cref="ShouldStop"/> treats as "stop nothing".</summary>
    private static string? OwnLocalAppDataOrNull()
    {
        try
        {
            var p = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch { return null; }
    }

    private static IReadOnlyList<(Candidate, string?)> EnumerateRealCandidates()
    {
        var list = new List<(Candidate, string?)>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            var retained = false;
            try
            {
                // Open and RETAIN the handle first. Everything after this — path, start time, exit
                // check, kill — goes through it, so no later step re-resolves the pid. Without a
                // handle there is no identity-safe way to kill this process, so it is not a
                // candidate at all; it is also almost certainly elevated, which the path read below
                // would independently refuse.
                if (!RealProcessControl.TryHold(p)) continue;
                retained = true;

                // MainModule throws for an elevated process; ShouldStop treats null as NOT ours.
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* unreadable ⇒ fail-closed */ }

                DateTime started;
                try { started = p.StartTime; } catch { started = DateTime.MinValue; }
                list.Add((new Candidate(p.Id, started), path));
            }
            catch { /* per-process; keep enumerating */ }
            finally { if (!retained) p.Dispose(); }   // retained ones are released by ReleaseAll
        }
        return list;
    }
}
