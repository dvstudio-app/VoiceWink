namespace VoiceWink;

using Velopack;
using global::Microsoft.Windows.ApplicationModel.DynamicDependency;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

/// <summary>
/// Custom entry point for the unpackaged WinUI 3 app. Replaces the WinUI
/// generator's static <c>Program.Main</c> (suppressed by the
/// <c>DISABLE_XAML_GENERATED_MAIN</c> define) so the Velopack lifecycle hook,
/// the Update.exe self-heal, the WindowsAppRuntime prereq install, and the
/// <see cref="Bootstrap.TryInitialize"/> activation all run BEFORE WinUI ever
/// starts.
///
/// The four stages of <see cref="Main"/> are the production shape validated by
/// the <c>spike/velopack-day1</c> spike on a clean Hyper-V VM with signed
/// artifacts (7/7 acceptance criteria green). See
/// <c>docs/plans/2026-05-05-2103-installer-signing-autoupdate-plan/50-decision.md</c>
/// override items 6–10 for the failure modes each invariant guards.
/// </summary>
public static class Program
{
    // WindowsAppSDK 1.6.250602001 runtime minimum (Runtime.Version from the
    // restored package's WindowsAppSDK-VersionInfo.json), matching the pinned
    // package reference + installer/portable/VoiceWinkSetup.ps1's $minVersion.
    private const uint WAR_MAJOR_MINOR_VERSION = 0x00010006; // 1.6
    private const string WAR_VERSION_TAG = "";
    private static readonly global::Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion WAR_MIN_VERSION
        = new(6000, 519, 329, 0);

    [global::System.STAThread]
    public static void Main(string[] args)
    {
        // ─────────────────────────────────────────────────────────────────────
        // Stage 1 — Velopack lifecycle. PURE MANAGED CODE, NO Microsoft.UI.Xaml
        // references in this scope. The JIT compiling Main must NOT eager-load
        // Microsoft.UI.Xaml.dll, because WindowsAppRuntime may not be present
        // on a clean VM. The csproj's WindowsAppSdkBootstrapInitialize=false
        // disables the module initializer that would otherwise auto-fire at
        // VoiceWink.dll load time.
        //
        // Inside the OnAfterInstall fast callback:
        //   1. UpdateExeSelfHeal.EnsureDeployed() — workaround for the
        //      Velopack 0.0.1298 signed-install bug where Update.exe sometimes
        //      fails to extract at the install root.
        //   2. PrereqInstaller.InstallAlwaysWithBudget(25_000) — WindowsApp-
        //      Runtime install with a tight budget so we stay well under
        //      Velopack's 30s fast-callback ceiling, leaving headroom after
        //      the self-heal step.
        // ─────────────────────────────────────────────────────────────────────
        VelopackApp.Build()
                   .SetArgs(args)
                   .SetAutoApplyOnStartup(false)
                   .OnAfterInstallFastCallback(_ =>
                   {
                       UpdateExeSelfHeal.EnsureDeployed();
                       PrereqInstaller.InstallAlwaysWithBudget(25_000);
                   })
                   .OnBeforeUninstallFastCallback(_ =>
                   {
                       // Pre-bootstrap-safe HKCU\Run cleanup. The helper has
                       // its own try/catch so this never throws out of the
                       // uninstall fast-callback (Velopack contract: hooks
                       // must not disrupt uninstall on exception). Logs go
                       // to install.log via PrereqInstaller.LogPublic —
                       // alongside the install-side log so an operator
                       // inspecting "what touched this install" sees one
                       // timeline. The ownership guard inside the helper
                       // only deletes the Run value when it references
                       // the Velopack `\VoiceWinkApp\current\` path
                       // segment, preserving non-Velopack Run entries
                       // (legacy Inno, portable, parallel installs).
                       VelopackUninstallCleanup.RemoveAutostartIfVelopackOwned();

                       // INS-4 (owner report 2026-08-18): stop a RUNNING VoiceWink so Velopack can
                       // actually delete the install. Uninstalling with the app running reported
                       // SUCCESS, left the app running, and the installer then offered to *repair*
                       // — a partial uninstall the user was told had completed.
                       //
                       // ORDER: cleanup FIRST, stop second. Not because the app rewrites autostart
                       // on exit — it does not (AutostartRegistrationService.Apply runs from
                       // settings changes and onboarding, never from Cleanup) — but on asymmetric
                       // certainty: the Run-value delete is instant and certain, the stop is slow
                       // and probabilistic. If this hook were ever budget-killed mid-stop, doing the
                       // cleanup first means we do not also strand the Run value, which is the very
                       // defect the cleanup helper exists to fix.
                       //
                       // Has its own try/catch and never throws, same contract as the cleanup above.
                       UninstallRunningInstanceStop.StopVelopackOwnedInstances();
                   })
                   // Re-armed for the 1.6.250602001 bump (backlog TCH-3): machines
                   // updating from an older VoiceWink may hold a runtime below the
                   // raised WAR_MIN_VERSION, so the update hook re-runs the bundled
                   // installer. Microsoft's installer no-ops fast when the runtime
                   // is already at target. NOTE: OnAfterUpdate's hard kill is 15s
                   // (Velopack 0.0.1298 XML docs — the 30s ceiling applies to the
                   // install/uninstall hooks, NOT this one), so the 13_000 ms wait
                   // leaves ~2s for spawn + logging. If the wait — or the 15s kill —
                   // expires mid-install, the spawned installer keeps running as its
                   // own process (mutex + PID sentinel serialize the handoff) and
                   // stage 3 (120s) remains the belt-and-braces fallback.
                   .OnAfterUpdateFastCallback(_ =>
                   {
                       PrereqInstaller.InstallAlwaysWithBudget(13_000);
                   })
                   .Run();

        // ─────────────────────────────────────────────────────────────────────
        // Stage 2 — Normal-launch self-heal. Belt-and-braces fallback for the
        // OnAfterInstall path: if the install-time call somehow missed (e.g.
        // Velopack's hook didn't fire for an in-place upgrade), re-extract
        // Update.exe before UpdateManager construction in the running app.
        // Pure managed / pure file-IO — no WinUI dependency, safe pre-bootstrap.
        // ─────────────────────────────────────────────────────────────────────
        UpdateExeSelfHeal.EnsureDeployed();

        // ─────────────────────────────────────────────────────────────────────
        // Stage 3 — Normal-launch runtime guard. Bootstrap.TryInitialize is the
        // authoritative check for unpackaged WinUI 3 apps. If it succeeds, all
        // required WinAppRuntime packages are present at compatible versions
        // AND the runtime is now active in this process. If false, install +
        // poll with bounded total budget. Stage 3 has its own (longer) budget
        // because it is not under Velopack's fast-callback ceiling.
        // ─────────────────────────────────────────────────────────────────────
        if (!Bootstrap.TryInitialize(WAR_MAJOR_MINOR_VERSION, WAR_VERSION_TAG, WAR_MIN_VERSION, out var bootstrapHr1))
        {
            PrereqInstaller.LogPublic($"normal-launch detect failed: hr=0x{bootstrapHr1:X8}");

            // Per round-4 override item 1: total budget IS enforced via
            // absolute deadline; per-attempt wait is min(perAttempt, remaining).
            var totalDeadline = global::System.DateTime.UtcNow.AddMilliseconds(120_000);

            while (global::System.DateTime.UtcNow < totalDeadline)
            {
                var remainingMs = (int)(totalDeadline - global::System.DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) break;
                var thisWaitMs = global::System.Math.Min(90_000, remainingMs);

                PrereqInstaller.InstallWithMutexAndRecheckBudget(
                    waitMs: thisWaitMs,
                    recheck: () => Bootstrap.TryInitialize(
                        WAR_MAJOR_MINOR_VERSION, WAR_VERSION_TAG, WAR_MIN_VERSION, out _));

                if (Bootstrap.TryInitialize(WAR_MAJOR_MINOR_VERSION, WAR_VERSION_TAG, WAR_MIN_VERSION, out _))
                {
                    break;
                }

                var sleepMs = global::System.Math.Min(5_000,
                    (int)(totalDeadline - global::System.DateTime.UtcNow).TotalMilliseconds);
                if (sleepMs > 0) global::System.Threading.Thread.Sleep(sleepMs);
            }

            if (!Bootstrap.TryInitialize(WAR_MAJOR_MINOR_VERSION, WAR_VERSION_TAG, WAR_MIN_VERSION, out var finalHr))
            {
                PrereqInstaller.ShowFatalRuntimeMissingDialog(finalHr);
                global::System.Environment.Exit(1);
                return;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Stage 3b — TRN-59 restart handoff. When this process was started by a
        // VoiceWink that is restarting itself ("Restart now" behind the GPU
        // toggle), the predecessor's pid rides a child-only environment
        // variable. Wait — bounded — for that PROCESS to exit before WinUI
        // starts, so the single-instance mutex, the daily log file and every
        // other process-scoped resource are free by the time App's constructor
        // claims them. Pure managed code, no WinUI type touched (the stage 1–3
        // invariant holds). Placed AFTER stage 3 so the runtime detection
        // overlaps the predecessor's shutdown instead of following it. Never
        // throws; a timeout proceeds to the normal single-instance check, which
        // then decides. The outcome is logged by App once Serilog exists.
        // ─────────────────────────────────────────────────────────────────────
        RestartHandoff.LastOutcome = RestartHandoff.WaitForPredecessorExit();

        // ─────────────────────────────────────────────────────────────────────
        // Stage 4 — WinUI start, isolated in a separate static class so
        // Microsoft.UI.Xaml type references are JIT-resolved only when that
        // class is first touched. By here, Bootstrap.TryInitialize has
        // guaranteed runtime presence + activation.
        // ─────────────────────────────────────────────────────────────────────
        WinUIBootstrap.Run();
    }
}
