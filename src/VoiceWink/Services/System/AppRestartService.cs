using global::System;
using global::System.ComponentModel;
using global::System.Diagnostics;
using global::System.Globalization;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Maintenance;

namespace VoiceWink.Services.System;

/// <summary>Seam over <c>Process.Start</c> so the restart sequence is testable without spawning.</summary>
internal interface IProcessStarter
{
    /// <summary>Start the successor. Throws when the OS refuses or returns no process.</summary>
    void Start(ProcessStartInfo startInfo);
}

internal sealed class ProductionProcessStarter : IProcessStarter
{
    public static readonly ProductionProcessStarter Instance = new();
    private ProductionProcessStarter() { }

    public void Start(ProcessStartInfo startInfo)
    {
        // The handle is not kept: the successor outlives us by design, and the only ordering that
        // matters (it waits for OUR exit) is carried by the environment variable, not by a handle.
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("the operating system returned no process");
    }
}

/// <summary>
/// TRN-59: the PREDECESSOR half of "Restart now" — shut this VoiceWink down gracefully and bring
/// a new one up on the same launcher, so a setting that only applies at start (the GPU-acceleration
/// toggle: the whisper.cpp load order is frozen at the first native load and the Parakeet child's
/// device rides its launch environment) applies now instead of "next time".
///
/// <para><b>The sequence is the update-apply prefix, reused step for step</b>
/// (<c>UpdateService.ApplyAndRestartAsync</c>): refuse while an update apply is in flight → take the
/// exclusive lease → poll the maintenance gate → resolve the launcher → make the settings durable →
/// start the successor → write the UPD-3c visible-restart one-shot → request the graceful quit with
/// the lease still held. Each refusal changes nothing else, and EVERYTHING after the lease runs
/// under a <c>try/finally</c> that releases it unless the restart was initiated: a leaked lease
/// would leave every start path refusing for the rest of the process's life, and
/// <c>LauncherDiscovery.GetLaunch()</c> reads <c>Process.MainModule</c>, which can throw.</para>
///
/// <para><b>UI-THREAD, SYNCHRONOUS — the same invariant <c>ApplyAndRestartAsync</c> documents for
/// its prefix.</b> Start paths read <c>App.IsExclusiveMaintenanceActive()</c> on the UI thread, so
/// the apply-in-flight probe, the lease and the gate poll run on that thread with no await between
/// them; a background caller would reopen the start-vs-restart race. The only caller is the
/// dialog's button handler.</para>
///
/// <para><b>Why the update-apply lease and not a new one.</b> It is THE exclusive-shutdown lease
/// every start path already consults (recording, model download, file transcription, redo, image
/// generation) and it is mutually exclusive with data erasure. A second lease kind would duplicate
/// the flag, every start-path check and the erasure exclusion to change one word on the pill
/// ("Update in progress — please wait"), which on this path shows from the click until the process
/// exits — the length of the graceful shutdown, since the lease is deliberately held through it.
/// The lease is ref-counted, which is exactly why the in-flight probe comes FIRST (Codex plan
/// round): a restart taken while an apply holds its own lease would spawn a successor into
/// Velopack's file swap.</para>
///
/// <para><b>Error detail is PATH-FREE.</b> A failed <c>Process.Start</c> message names the launcher
/// path and the working directory, and a settings write failure names the settings file — both
/// carry <c>C:\Users\&lt;name&gt;\…</c>. The log line and the dialog get the exception type and, for
/// a Win32 failure, its error code (<see cref="DescribeError"/>); the launcher KIND is logged,
/// never the command — the <c>LauncherDiscovery</c> rule.</para>
///
/// <para><b>Accepted failure mode, identical to the update path:</b> if the graceful quit never
/// completes (a wedged UI thread) the lease is held for the rest of the process's life and start
/// paths keep refusing; the successor's exit wait times out, its mutex wait then times out too
/// (<c>RestartHandoff.TryTakeOverMutexAfterTimeout</c>), and it activates this process and exits.</para>
/// </summary>
public sealed class AppRestartService
{
    private static ILogger Logger => Log.ForContext<AppRestartService>();

    private readonly SettingsService _settings;
    private readonly IMaintenanceGate _gate;
    private readonly IAppLifetime _lifetime;
    private readonly LauncherDiscovery _launcher;
    private readonly Func<bool> _isUpdateApplyInFlight;
    private readonly IProcessStarter _starter;
    private readonly int _currentPid;

    /// <param name="isUpdateApplyInFlight">Bound in DI to <c>IUpdateService.IsApplyInFlight</c>
    /// — a probe rather than the whole interface, the <c>MaintenanceSourcesWiring</c> shape.</param>
    public AppRestartService(
        SettingsService settings,
        IMaintenanceGate gate,
        IAppLifetime lifetime,
        LauncherDiscovery launcher,
        Func<bool> isUpdateApplyInFlight)
        : this(settings, gate, lifetime, launcher, isUpdateApplyInFlight, ProductionProcessStarter.Instance, Environment.ProcessId)
    {
    }

    internal AppRestartService(
        SettingsService settings,
        IMaintenanceGate gate,
        IAppLifetime lifetime,
        LauncherDiscovery launcher,
        Func<bool> isUpdateApplyInFlight,
        IProcessStarter starter,
        int currentPid)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _isUpdateApplyInFlight = isUpdateApplyInFlight ?? throw new ArgumentNullException(nameof(isUpdateApplyInFlight));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _currentPid = currentPid;
    }

    /// <summary>The blocker name for an update apply in flight — the same words the pill uses.</summary>
    public const string UpdateInProgress = "Update in progress";

    /// <summary>The blocker name when a data erasure holds the exclusive lease.</summary>
    public const string ErasureInProgress = "Data erasure in progress";

    /// <summary>
    /// Restart now. Synchronous; returns once the successor is started and the graceful quit is
    /// queued (<see cref="AppRestartOutcome.Initiated"/>), or with the refusal and nothing changed.
    /// </summary>
    public AppRestartResult TryRestart(AppRestartReason reason)
    {
        // 1. An update apply owns the exit already (its lease is ref-counted, so the lease below
        //    would NOT refuse it). Restarting under it would put a successor into Velopack's swap.
        if (_isUpdateApplyInFlight())
        {
            Logger.Information("Restart ({Reason}) refused: an update apply is in flight", reason);
            return AppRestartResult.BusyWith([UpdateInProgress]);
        }

        // 2. The exclusive lease, BEFORE the gate poll (closes the check-to-acquire window, F19) —
        //    refused while a data erasure holds its own lease.
        if (!_gate.TryBeginUpdateApply(out var lease) || lease is null)
        {
            Logger.Information("Restart ({Reason}) refused: a data erasure is in progress", reason);
            return AppRestartResult.BusyWith([ErasureInProgress]);
        }

        // From here every exit releases the lease EXCEPT an initiated restart, whose process is
        // exiting with it held. A throw anywhere below is a refusal with the lease released, never
        // a lease leaked into a process that keeps running.
        var initiated = false;
        try
        {
            // 3. Anything in flight that a restart would cut short — named, so the dialog can say so.
            var status = _gate.Check();
            if (!status.CanProceed)
            {
                Logger.Information("Restart ({Reason}) refused: {Blockers}", reason, string.Join(", ", status.ActiveBlockers));
                return AppRestartResult.BusyWith(status.ActiveBlockers);
            }

            // 4. The launcher this process was started with. The KIND is logged, never the command
            //    — it carries C:\Users\<name>\… (the LauncherDiscovery privacy rule). Reading the
            //    process's main module can throw; that is a refusal, not a crash.
            LaunchCommand launch;
            try
            {
                launch = _launcher.GetLaunch();
            }
            catch (Exception ex)
            {
                Logger.Warning("Restart ({Reason}) refused: the launcher could not be resolved ({Error})", reason, DescribeError(ex));
                return AppRestartResult.LauncherUnavailable;
            }
            if (launch.Kind == LauncherKind.Unavailable)
            {
                Logger.Warning("Restart ({Reason}) refused: no launcher could be resolved for this process", reason);
                return AppRestartResult.LauncherUnavailable;
            }

            // 5. The settings become durable NOW. Writes are debounced 250 ms; a successor that raced
            //    the debounce would read the OLD value — the exact defect this feature removes. A
            //    suppressed flush (data-erasure quiesce) returns silently, so its flag is checked too.
            try
            {
                _settings.FlushOrThrow();
                if (_settings.IsPersistenceSuppressed)
                {
                    throw new InvalidOperationException("settings persistence is suspended");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Restart ({Reason}) refused: the settings could not be made durable ({Error})", reason, DescribeError(ex));
                return AppRestartResult.SettingsNotDurable(DescribeError(ex));
            }

            // 6. Start the successor. It inherits our environment plus ONE child-only variable naming
            //    our pid, and waits for us to exit before it claims anything (RestartHandoff).
            var startInfo = BuildStartInfo(launch, _currentPid);
            try
            {
                _starter.Start(startInfo);
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "Restart ({Reason}) refused: the successor could not be started through the {LauncherKind} launcher ({Error})",
                    reason, launch.Kind, DescribeError(ex));
                return AppRestartResult.LaunchFailed(DescribeError(ex));
            }
            Logger.Information(
                "Restart ({Reason}): successor started through the {LauncherKind} launcher; it waits for pid {Pid} to exit, then the graceful quit runs",
                reason, launch.Kind, _currentPid);

            // 7. The UPD-3c one-shot: this restart is attended, so the successor shows its window even
            //    under Start minimized. AFTER the spawn succeeded (a failed spawn must not leak a
            //    spurious visible launch) and best-effort (the restart matters more than the window),
            //    exactly as the update path orders it.
            try
            {
                _settings.SetBool(AppDefaults.ShowWindowAfterUpdateRestart, true);
                _settings.FlushOrThrow();
            }
            catch (Exception ex)
            {
                Logger.Warning("Could not persist the visible-restart flag ({Error}); the relaunch may start minimized", DescribeError(ex));
            }

            // 8. The graceful quit — the same path the tray Quit, the update apply and the uninstaller
            //    take. The lease stays HELD: the process is exiting and start paths must keep refusing.
            _lifetime.RequestQuit(GracefulQuitReason.Restart);
            initiated = true;
            return AppRestartResult.Initiated;
        }
        finally
        {
            if (!initiated) lease.Dispose();
        }
    }

    /// <summary>The successor's start info. Pure, so the test pins the shape: no shell, no console
    /// window (a bare <c>dotnet … .dll</c> would otherwise open one), and the handoff variable set
    /// on the CHILD's block only — never <c>Environment.SetEnvironmentVariable</c> in this process.</summary>
    internal static ProcessStartInfo BuildStartInfo(LaunchCommand launch, int predecessorPid)
    {
        var startInfo = new ProcessStartInfo(launch.FileName, launch.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment[RestartHandoff.PredecessorPidVariable] = predecessorPid.ToString(CultureInfo.InvariantCulture);
        return startInfo;
    }

    /// <summary>A path-free description of a failure: the exception type and, for a Win32 failure,
    /// its error code — never <c>Message</c>, which for <c>Process.Start</c> names the launcher path
    /// and the working directory, and for a settings write names the settings file.</summary>
    internal static string DescribeError(Exception ex)
        => ex is Win32Exception win32
            ? $"{win32.GetType().Name}, Windows error {win32.NativeErrorCode}"
            : ex.GetType().Name;
}
