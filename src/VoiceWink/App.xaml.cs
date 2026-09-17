using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sentry;
using Serilog;
using Microsoft.EntityFrameworkCore;
using VoiceWink.Services.Audio;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AIEnhancement.Providers;
using VoiceWink.Services.Context;
using VoiceWink.Services.Data;
using VoiceWink.Services.Http;
using VoiceWink.Services.Licensing;
using VoiceWink.Services.Maintenance;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.Input;
using VoiceWink.Services.System;
using VoiceWink.Services.TextProcessing;
using VoiceWink.Services.Transcription;
using VoiceWink.Models.Enums;
using Microsoft.Data.Sqlite;
using VoiceWink.Helpers;
using VoiceWink.ViewModels;
using CommunityToolkit.Mvvm.Input;
using VoiceWink.Views;
using System.ComponentModel;

namespace VoiceWink;

/// <summary>
/// Application entry point with DI container.
/// Sets up Serilog logging, DI services, system tray icon, and creates windows.
/// </summary>
public partial class App : Application, Services.IAppLifetime
{
    private static Mutex? _singleInstanceMutex;
    // INS-4: kept alive for the process lifetime so the uninstall hook can ask us to quit
    // GRACEFULLY. Disposing it would silently close the channel and send every uninstall
    // back to killing the app, which is exactly what that card exists to stop.
    private static EventWaitHandle? _uninstallQuitSignal;
    private const string MutexName = "VoiceWink-SingleInstance-9F3A7B2C";

    private VoiceWink.Views.MainWindow? _mainWindow;
    private MiniRecorderWindow? _miniRecorderWindow;
    private MainViewModel? _miniRecorderViewModel;
    private DispatcherTimer? _miniRecorderDisplayDebounceTimer;
    private EventHandler? _miniRecorderDisplaySettingsChangedHandler;
    // DSP-1: WM_DISPLAYCHANGE listener window — the reliable second signal source
    // (SystemEvents.DisplaySettingsChanged never fires on some hardware/app
    // combos; proven 2026-07-06). Created/disposed with the display-change handling.
    private DisplayChangeListener? _displayChangeListener;
    // ERR-PERSIST controller refactor (2026-07-21): the single-owner presentation controller replaces
    // the App-held pill-state mirror (active error/download/image-job + last-rehydration-kind + epochs).
    // App now only forwards VM/domain events to Publish*/Clear*/Arm* and routes the window's
    // handle-carrying dismiss/action/stop events back — the controller owns precedence, timing, render.
    private MiniRecorderPresentationController? _pillController;
    private Action<PillHandle>? _pillDismissHandler, _pillActionHandler, _pillStopHandler, _pillHideHandler, _pillMessageActionHandler;
    // Previous affordance-availability so a PropertyChanged can tell an ARM (false→true) from a CLEAR
    // (true→false) and drive the controller's two-context ArmAffordance/RetireAffordance protocol.
    private bool _prevRedoAvailable, _prevRetryAvailable;
    private Services.AIEnhancement.ImageGenerationJobService? _imageJobService;
    private bool _miniRecorderDisplayDirty;
    private bool _miniRecorderRecreateInProgress;
    // Set to true after a cross-DPI recreate request is successfully enqueued on
    // the dispatcher but BEFORE the queued lambda has started running. Closes
    // the 2.25-second enqueue-to-start gap observed in the 2026-05-21 crash log:
    // a retap during that gap would otherwise sneak past `_miniRecorderRecreateInProgress`
    // (set only inside the queued lambda) and pile a second recreate onto the queue.
    private bool _miniRecorderRecreateQueued;
    // Timestamp of the LAST completed cross-DPI recreate swap. Replaces the earlier
    // enqueue-stamped cooldown — a slow dispatcher gap (the 2.25s spike) defeated
    // that variant because the cooldown aged out before the actual recreate ran.
    // Read by MiniRecorderRecreateGate.Evaluate alongside the queued/inProgress
    // flags so the gate covers the full lifecycle (queued → inProgress → quiet → cooldown).
    private DateTime _lastRecreateCompletionUtc = DateTime.MinValue;
    private MiniRecorderWindow.MonitorPlacement? _pendingMiniRecorderTarget;
    private Func<MiniRecorderWindow.MonitorPlacement, string, bool>? _miniRecorderCrossDpiHandler;
    // NOTE (2026-07-06 hotfix): the "proactive idle re-parking" poll that briefly
    // lived here recreated the hidden window toward the foreground monitor's DPI.
    // Live logs disproved its premise the same day: Windows does NOT re-assign a
    // fully off-screen window's DPI (XamlRoot.RasterizationScale never converges
    // to a non-primary monitor while parked, on EITHER side of the monitor), so
    // the mismatch never cleared and the poll recreated the window every ~4 s.
    // Removed. A real pre-association needs the window ON the target monitor —
    // DWMWA_CLOAK parking is the candidate follow-up (backlog PERF entry).
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    // True only after SetupTrayIcon completes ForceCreate() successfully (LGL-1 / Codex
    // round-2 diff-review challenge). Distinguishes "tray icon was constructed" from
    // "tray icon is actually visible in the system tray and can restore the window."
    // Used by the close, minimize, and start-minimized hide paths.
    private bool _trayIconReady;
    // DCT-1: what InitializeDatabase learned, for the Dictionary seed that runs after it — the
    // factory it already resolved (no second service-locator lookup for the same singleton;
    // AGENTS.md) and whether InitializeDatabaseCore CREATED the database this launch (a fresh
    // file, or the schema-repair recreate), in which case the seed offers every default again
    // instead of trusting a record written for a database that no longer exists.
    private IDbContextFactory<VoiceWinkDbContext>? _dbFactoryForSeed;
    private bool _databaseCreatedThisLaunch;
    private HotkeyService? _hotkeyService;
    // AUD-6: retained at gated-services wiring (the same seam _hotkeyService uses) so the
    // suspend/resume/unlock/warm-up/cleanup sites don't each add an App.Services lookup
    // (AGENTS.md standing rule). Null until StartGatedRuntimeServices runs — every consumer
    // sits behind that gate, and a quit before it means the service was never created.
    private VoiceWink.Services.Audio.StandingCaptureService? _standingCaptureService;
    // TRN-29 slice 4 (Kimi diff r1, K4): stashed at startup so exit-time shutdown never
    // CONSTRUCTS the pcpp stack — GetService on a never-materialized singleton builds the
    // whole coordinator (EnsureModels side effect included) at process exit. Null in every
    // flag-off build AND when startup never reached the stash.
    private Services.Transcription.IParakeetPcppBackend? _pcppBackend;
    // UPD-1b background poll + UPD-3b sidebar dot + UPD-4b auto-install, all behind the LGL-1 gate.
    // The handler/subscription bookkeeping that used to live here as five fields moved into the
    // coordinator (UPD-4b) — see StartGatedRuntimeServices.
    private Services.Updates.UpdateRuntimeCoordinator? _updateRuntime;
    // LIC-23: the daily in-session forced licence check, behind the LGL-1 gate like the update
    // poll above; the loop, the tick and its error handling live in the type — App only starts
    // it (StartGatedRuntimeServices) and stops it (Cleanup). Null until the gated services run.
    private LicenseRevalidationScheduler? _licenseRevalidation;
    private bool _isQuitting;
    // UPD-3c: effective start-minimized INTENT for this launch (raw preference AND NOT the
    // update-restart one-shot). Intent, not actual visibility — the tray-not-ready fallback
    // can leave the window visible independently. Consumed by the post-legal hide path and
    // the What's-new guard so both agree on the same launch-visibility decision.
    private bool _startMinimizedIntentThisLaunch;
    private DispatcherTimer? _transcriptionCleanupTimer;
    // Serializes hook restarts triggered by system power/session events so that
    // PowerModeChanged.Resume and SessionSwitch.SessionUnlock firing close together
    // (modern standby + lock-screen wake) don't race two concurrent restarts.
    private readonly SemaphoreSlim _hookRestartLock = new(1, 1);
    // Coalesce near-miss restarts. The semaphore prevents *concurrent* restarts but if
    // one finishes before the second event's delayed lambda fires WaitAsync, both would
    // run sequentially. A second restart within this window is suppressed entirely.
    private DateTime _lastSystemEventRestartUtc = DateTime.MinValue;
    private static readonly TimeSpan SystemEventRestartCooldown = TimeSpan.FromSeconds(10);


    public static IServiceProvider Services { get; private set; } = null!;
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Typed alias for <see cref="MainWindow"/> (LGL-1) so external callers
    /// (Settings → Legal row Tapped, Legal "Review now" button) can invoke
    /// <see cref="VoiceWink.Views.MainWindow.NavigateToLegalPage"/> without a cast.
    /// </summary>
    public static VoiceWink.Views.MainWindow? MainWindowInstance { get; private set; }

    /// <summary>
    /// Set once after <see cref="StartGatedRuntimeServices"/> runs so a
    /// subsequent re-trigger (e.g. theme rebuild) doesn't double-arm the
    /// hotkey hook or tray Record action. LGL-1.
    /// </summary>
    private int _gatedRuntimeStarted;

    internal static string LogDirectory => Helpers.AppPaths.LogsDir;

    /// <summary>Default location for the opt-in probe — see <see cref="Helpers.CrashOptInReader"/>.</summary>
    private static string DefaultSettingsPath => Helpers.AppPaths.SettingsFile;

    /// <summary>
    /// Build the canonical Serilog configuration used by the app: file sink under
    /// <see cref="Helpers.AppPaths.LogsDir"/>, a Debug sink, and the optional Sentry
    /// sink when <see cref="SentryInitializer.IsInitialized"/>.
    /// Both <see cref="ClearAndReinitializeLogs"/> and the startup configurer use this
    /// so a sink added in one place can never silently desync from the other.
    ///
    /// <para>The <see cref="Services.System.LogRedactionEnricher"/> is scoped to the
    /// Sentry sub-logger only. The local file sink and Debug sink see the full
    /// un-redacted event so on-device troubleshooting (e.g. inspecting an image-gen
    /// failure body) isn't crippled by privacy scrubbing intended for telemetry.
    /// Redaction for the user-triggered Support-zip bundling path (REL-3) happens
    /// at bundle time via <c>LogRedactionEnricher.RedactString</c>.</para>
    ///
    /// <para>The <see cref="Services.System.ModelIdEnricher"/> is on the ROOT (2026-09-13) and
    /// is not redaction: it gates the SHAPE of the one user-typed field that reaches log lines
    /// (the editable model box) so every sink — file, Debug, the Sentry sub-logger — renders an
    /// id or "(not an id, N chars)", never the text. Ahead of every sink by construction: a
    /// root enricher runs before dispatch.</para>
    /// </summary>
    private static LoggerConfiguration BuildLoggerConfiguration()
    {
        var logDir = Helpers.AppPaths.EnsureLogs();
        var cfg = new LoggerConfiguration()
            // The level is a property of the ONE process-global switch, never a literal here:
            // this configuration is built three times (startup, the Log Viewer's Clear, the
            // crash-consent sink attach) and a literal would silently reset the Log Viewer's
            // "Verbose debug logging" choice on each rebuild. Information until the toggle (or the
            // pre-boot VerboseLoggingReader) lowers it to Debug.
            .MinimumLevel.ControlledBy(Helpers.LogLevelControl.Switch)
            // The {Model} identifier gate, on the root so the file sink sees it too — the Sentry
            // sub-logger below is the wrong scope for it (ModelIdEnricherTests pins the placement).
            .Enrich.With<Services.System.ModelIdEnricher>()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "voicewink-.log"),
                rollingInterval: RollingInterval.Day,
                // 14, not 7 (owner decision 2026-08-20, for PST-14). This is a FILE COUNT, and
                // with RollingInterval.Day a file exists only for a day the app actually logged —
                // so it retains the last 14 DAYS OF USE, not 14 calendar days. That distinction is
                // why 7 was too few to investigate with: the PST-13 investigation found only three
                // log files under the old limit and could compare three days, on a defect whose
                // rate needed more. Retention cannot be applied retroactively, so raising it is
                // only ever useful BEFORE the window you want to measure.
                //
                // Scope: this covers the application log ONLY. Prompt traces (PromptTraceLog),
                // debug recordings and report bundles each keep their own independent 7-day sweep
                // and are deliberately untouched — those hold dictation audio and prompt text,
                // where a longer window is a privacy cost with no analytical benefit. The shipped
                // privacy policy's §7 retention list enumerates those shorter-lived items by name
                // and does NOT mention this application log at all, so this number contradicts no
                // published claim — verified against the promoted privacy-v5 before the change,
                // and worth re-checking if that list is ever revised. Erasure covers this log
                // either way (DataErasureService closes the sink and deletes the Logs directory).
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        if (SentryInitializer.IsInitialized)
        {
            cfg = cfg.WriteTo.Logger(lc => lc
                .Enrich.With<Services.System.LogRedactionEnricher>()
                .WriteTo.Sentry(o =>
                {
                    o.MinimumBreadcrumbLevel = Serilog.Events.LogEventLevel.Information;
                    o.MinimumEventLevel = Serilog.Events.LogEventLevel.Error;
                    o.InitializeSdk = false; // SentryInitializer already owns SDK init.
                }));
        }
        return cfg;
    }

    /// <summary>
    /// Close the current logger, delete all log files, and reinitialize.
    /// Called from the Log Viewer's "Clear Logs" button — and since the launch defaults audit
    /// (2026-09-13) from nowhere else: History → Delete all used to call it too, which destroyed
    /// the evidence for exactly the support case a user raises right after clearing history.
    /// </summary>
    internal static void ClearAndReinitializeLogs()
    {
        Log.CloseAndFlush();

        if (Directory.Exists(LogDirectory))
        {
            foreach (var file in Directory.GetFiles(LogDirectory, "*.log"))
            {
                try { File.Delete(file); }
                catch { /* best effort */ }
            }
        }

        Log.Logger = BuildLoggerConfiguration().CreateLogger();
    }

    /// <summary>
    /// Rebuild the logger WITHOUT touching the log files, so a sink whose availability just
    /// changed is attached (or detached) now rather than at the next start. The one caller is
    /// the crash-reporting consent (the wizard's step and the Settings toggle): the Sentry sink
    /// is added only when <see cref="SentryInitializer.IsInitialized"/> was true at build time,
    /// so before this existed a user who opted in kept a logger with no Sentry sink until a
    /// restart — and the Settings row had to say "Takes effect after a restart." Same
    /// configuration as startup by construction (<see cref="BuildLoggerConfiguration"/>), so
    /// the level switch and every other sink survive the swap.
    /// </summary>
    internal static void ReattachLogSinks()
    {
        Log.CloseAndFlush();
        Log.Logger = BuildLoggerConfiguration().CreateLogger();
    }

    public App()
    {
        // Single-instance check: if another VoiceWink is already running,
        // bring its window to the foreground and exit this instance.
        //
        // TRN-59: unless this launch is a self-restart whose wait for the predecessor's EXIT timed
        // out. Then the predecessor is still shutting down (or wedged), and the successor waits on
        // the MUTEX it releases as the last act of Cleanup — activating a window that is closing and
        // exiting would leave NO VoiceWink running. Bounded; a wedged predecessor still wins, and an
        // ordinary second launch (no handoff) never waits at all.
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew && !Helpers.RestartHandoff.TryTakeOverMutexAfterTimeout(_singleInstanceMutex))
        {
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        // Force Per-Monitor V2 DPI awareness before any UI is created.
        // Critical when launching via "dotnet VoiceWink.dll" — the host
        // process (dotnet.exe) may not be DPI-aware, causing blurry text.
        try { Helpers.NativeInterop.SetProcessDpiAwarenessContext(
            Helpers.NativeInterop.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* Already set or not supported — safe to ignore */ }

        // Pin a stable AppUserModelID before any window is created. Same root cause as
        // the DPI call above — the dotnet.exe host leaves us sharing identity with every
        // other .NET app, and the shell uses AUMID as the taskbar-icon cache key.
        //
        // The value MUST equal the AUMID Velopack stamps onto the shortcuts it creates, or the
        // shell treats the pinned shortcut and this process as two different apps and shows two
        // taskbar buttons (UI-16). It lives in Helpers/AppIdentity.cs — read the evidence there
        // before touching it, and note the value is "velopack." + --packId, not the pack id
        // verbatim. scripts/check-app-identity.ps1 pins this call site as well as the constant.
        try { Helpers.NativeInterop.SetCurrentProcessExplicitAppUserModelID(Helpers.AppIdentity.AppUserModelId); }
        catch { /* COM not yet initialized in a weird host — safe to skip; icon will still set */ }

        // Set native menu theme (dark/light) to match app theme
        ApplyNativeMenuTheme();
        AppTheme.ThemeChanged += ApplyNativeMenuTheme;

        // App.xaml is intentionally empty — XamlControlsResources was removed
        // from the XAML because LoadComponent("ms-appx:///App.xaml") with
        // resource dictionaries causes native crashes in CLI builds
        // (dotnet VoiceWink.dll). Resources are loaded in code in OnLaunched.
        InitializeComponent();

        // Init crash reporting BEFORE Serilog so the Sentry sink can attach. No-op until
        // the user opts in AND a DSN is configured; safe to call on every launch.
        var crashOptIn = CrashOptInReader.Read(DefaultSettingsPath);
        SentryInitializer.TryInit(crashOptIn);

        // Pre-create the writable directories the app uses at runtime so the recording
        // hot path (and other downstream callers) doesn't pay a per-action syscall.
        Helpers.AppPaths.EnsureLogs();
        Helpers.AppPaths.EnsureRecordings();

        // Configure Serilog (shared with ClearAndReinitializeLogs / ReattachLogSinks). The
        // minimum level is read from settings.json BEFORE the logger exists (the CrashOptInReader
        // shape), so a user who turned on verbose logging gets Debug detail from the very first
        // line — a slow or failing START is the case that detail is needed for most.
        Helpers.LogLevelControl.Apply(Helpers.VerboseLoggingReader.Read(DefaultSettingsPath));
        Log.Logger = BuildLoggerConfiguration().CreateLogger();

        // INS-4: registered AFTER the logger is configured, deliberately. A Listen failure logs a
        // Warning, and above this line Serilog's default sink is silent — so the one diagnostic
        // telling us the graceful channel is unavailable would be thrown away. Safe to sit here:
        // the single-instance LOSER has already exited above, so only the winner ever reaches this,
        // and two listeners on one latch would race to quit.
        //
        // It routes into RequestQuit (the Uninstall reason) — the SAME path the tray Quit, the
        // update-apply quit and the TRN-59 self-restart use, and the only one that sets _isQuitting
        // before Close() and therefore the only one that reaches Cleanup(). An out-of-process
        // WM_CLOSE cannot: the Closed handler intercepts it and hides to tray.
        //
        // The delegate binds THIS instance rather than resolving Application.Current inside the
        // callback: Current is UI-thread affine in WinUI 3, and this callback arrives on a
        // thread-pool thread by construction. RequestQuit marshals onto the dispatcher itself and
        // is documented safe to call from a background path.
        _uninstallQuitSignal = global::VoiceWink.Services.System.UninstallQuitSignal.Listen(
            () =>
            {
                Log.Information("INS-4: graceful quit requested by the uninstaller");
                RequestQuit(global::VoiceWink.Services.GracefulQuitReason.Uninstall);
            });

        Log.Information("VoiceWink starting up");
        // The session banner: version, OS, architecture (emulated or not), runtime, channel and
        // the handful of preferences that change what the pipeline does — the facts a support
        // case asks for first, on the first line of every log so no reply has to ask for them.
        // Allowlisted keys only (DiagnosticSnapshot): never a key, a path or dictated text.
        Log.Information("Session {Environment}",
            Helpers.DiagnosticSnapshot.RenderLine(Helpers.DiagnosticSnapshot.Collect(DefaultSettingsPath)));
        // TRN-59: if this process was started by a VoiceWink restarting itself, stage 3b of
        // Program.Main waited for the predecessor to exit before WinUI started; this is the
        // earliest point Serilog exists, so the outcome is logged here. Silent on an ordinary
        // launch.
        Helpers.RestartHandoff.LogLastOutcome(Log.Logger);

        // Pin process priority back to Normal + opt out of Windows 11 Efficiency Mode,
        // and install a periodic re-pin watchdog. Live diagnosis showed Windows
        // demotes the dotnet host to IDLE_PRIORITY_CLASS some seconds AFTER our
        // startup Apply() call lands, so a one-shot is not sufficient. The watchdog
        // re-checks every 5s (first check at T+2s) from a dedicated TIME_CRITICAL
        // thread — REL-14: as a ThreadPool timer it was starved by the very demotion
        // it exists to undo, and boot-time launches hung for minutes. Paired with
        // ProcessPriorityTuner.Stop() in Cleanup. See ProcessPriorityTuner and
        // PriorityWatchdog for full context.
        Helpers.ProcessPriorityTuner.Start();

        // Global exception handlers for diagnostics. For fatal paths we SentrySdk.Flush
        // before closing Serilog — the process is about to die and Sentry's HTTP send is
        // async; without an explicit flush the highest-value events (the crashes that
        // motivated opt-in) get lost racing process termination.
        UnhandledException += (sender, e) =>
        {
            Log.Fatal(e.Exception, "WinUI unhandled exception: {Message}", e.Message);
            if (SentryInitializer.IsInitialized)
            {
                SentrySdk.CaptureException(e.Exception);
                try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort during crash */ }
            }
            Log.CloseAndFlush();
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "AppDomain unhandled exception");
                if (SentryInitializer.IsInitialized)
                {
                    SentrySdk.CaptureException(ex);
                    try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); }
                    catch { /* best-effort during crash */ }
                }
                Log.CloseAndFlush();
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            if (SentryInitializer.IsInitialized)
                SentrySdk.CaptureException(e.Exception);
        };

        // Configure DI container
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // TRN-49: wire + kick the GPU warm-up AFTER the container exists (it needs the shared
        // HTTP client). Every input was decided in ConfigureServices; the warm-up itself is
        // background, once per app version (marker), fully behind the TRN-34 spawn gate, and
        // invisible to the storm fuse — see GpuWarmup's class doc for the pinned invariants.
        ConfigureGpuWarmup();
    }

    private void ConfigureGpuWarmup()
    {
        // TRN-63: the same three keys the pre-boot pin used (ConfigureServices adopted any driver
        // change there, so this instance reads the fresh state).
        var marker = new GpuWarmupMarker(
            GpuWarmupMarker.DefaultPath, GpuWarmupMarker.CurrentAppVersion(), GpuWarmupMarker.CurrentDriverSignature());

        // TRN-50: the golden clip the warm decodes judge. Loaded ONCE, lazily, on the first warm-up
        // that asks (a 120 KB read off the startup path); absent → one Information line and every
        // warm decode is the TRN-49 silence shape, verdict-free. The asset's presence is the
        // owner's decision, so the app must run either way.
        var selfTestClip = new Lazy<Helpers.GpuSelfTestClip?>(() =>
        {
            var clip = Helpers.GpuSelfTestClip.TryLoadBundled(out var reason);
            if (clip is null)
            {
                global::Serilog.Log.ForContext<App>().Information(
                    "GPU self-test: {Reason} - the warm-up decodes silence only (TRN-50)", reason);
            }
            return clip;
        });

        // Hoisted const read (the VadFeature CS0162 idiom).
        var pcppEnabled = Helpers.PcppFeature.IsEnabled;
        GpuWarmup.ParakeetPlan? parakeetPlan = null;
        if (pcppEnabled && s_parakeetLaunchMode == ParakeetLaunchMode.Auto)
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "parakeet-server.exe");
            parakeetPlan = new GpuWarmup.ParakeetPlan(
                new NativeParakeetServerLauncher(),
                Services!.GetRequiredService<ParakeetServerTranscriptionClient>(),
                exePath,
                ParakeetBackendCoordinator.ServerExeSha256,
                ResolveGgufPath: () =>
                {
                    try
                    {
                        var manager = Services!.GetRequiredService<ModelDownloadManager>();
                        var location = manager.TryGetInstalledLocation(Models.ParakeetCatalog.GgufDescriptor);
                        if (location?.Path is not { } dir || !Directory.Exists(dir)) return null;
                        return Directory.EnumerateFiles(dir, "*.gguf").FirstOrDefault();
                    }
                    catch
                    {
                        return null; // best effort: no model, no warm-up, no noise.
                    }
                },
                // TRN-50: a self-test FAIL requests CPU through the process manager's non-blocking
                // intent (consumed by the next acquire — never a gate wait from the warm-up).
                RequestCpu: reason => Services!.GetService<ParakeetServerProcess>()?.RequestCpu(reason));
        }

        GpuWarmup.Instance.Configure(new GpuWarmup.Configuration(
            marker,
            WhisperVulkanUsable: s_whisperVulkanUsable,
            Parakeet: parakeetPlan,
            ResolveClip: () => selfTestClip.Value));

        // TRN-52: what the Models page rates SPEED for. Installed here because the container now
        // exists, and written as a provider rather than a value because two inputs move during a
        // session: the Whisper backend becomes KNOWN at the first native load (before that it is
        // the pinned order's first entry), and the Parakeet launch mode can latch Auto→Cpu when a
        // GPU child died before health. Resolving the ParakeetServerProcess singleton spawns
        // nothing; under PcppEnabled=false it is not registered and the CPU-only sherpa engine
        // reads Cpu, which is also the only speed set that row carries.
        Helpers.LocalComputeSnapshot.Configure(() =>
        {
            // UI-14 (Codex verification-round Blocker): BOTH Parakeet facts from ONE captured child
            // reference. Read as two properties they were two live reads, and a child swap between
            // them paired Gpu with a null name — the page then fell back to the persisted warm-up
            // adapter and named it live.
            var parakeetEvidence = Services!.GetService<ParakeetServerProcess>()?.GetLiveGpuEvidence()
                ?? (IsGpu: false, Name: (string?)null);
            return new Helpers.LocalComputeSnapshot(
            Whisper: Helpers.WhisperBackendLog.ResolvedCompute(),
            // TRN-64 PR 2 (Codex diff r1 Blocker): the child's OWN device evidence, not its launch
            // mode. Auto is a request — a loader-present machine with no usable adapter serves CPU
            // under it — and PR 2 turned this snapshot into an affirmative "running on your
            // graphics card" sentence, so intent is no longer a good enough basis. It also makes
            // the speed stars honest on the same machine, and both now fail toward CPU while no
            // child is live, which is the direction LocalComputeSnapshot documents.
            Parakeet: parakeetEvidence.IsGpu
                ? Helpers.LocalCompute.Gpu
                : Helpers.LocalCompute.Cpu,
            // UI-14 / TRN-68: the LIVE adapter names the Pass tick renders — the row Whisper builds
            // on, and the resident child's own resolved row — never the warm-up child's persisted name
            // alone (the two children pick independently since TRN-68).
            WhisperGpuName: Helpers.WhisperBackendLog.ObservedGpuName,
            ParakeetGpuName: parakeetEvidence.Name);
        });

        // The Parakeet kick itself lives in StartGatedRuntimeServices, NOT here: this runs in
        // the App constructor, BEFORE the LGL-1 legal gate, and spawning a ~1 GB child that
        // reads 900 MB of model on a machine whose user has not accepted the current legal
        // bundle belongs behind the same gate as every other runtime service (self-review,
        // regression lens). The Whisper half needs no equivalent — it queues itself from a
        // successful model load, and model loads are themselves gated.
    }

    /// <summary>TRN-51/53: ConfigureServices' pre-boot decisions, surfaced for the ctor's
    /// warm-up wiring (ConfigureServices is static and runs before the provider exists).</summary>
    private static ParakeetLaunchMode s_parakeetLaunchMode = ParakeetLaunchMode.Auto;

    private static bool s_whisperVulkanUsable;

    private static void ConfigureServices(IServiceCollection services)
    {
        // TRN-27: decide + pin the whisper.cpp native load order BEFORE any service that could
        // build a WhisperFactory/WhisperVadFactory is even registered — [Vulkan, Cpu] when the
        // helper's pre-flight probe proves the Vulkan chain loadable, [Cpu] otherwise (the
        // probe exists because Whisper.net's own vulkan→cpu fall-through measurably ABORTS the
        // process on a partial payload; see WhisperBackendLog). RuntimeOptions is process-wide
        // and frozen at the first native load; the first factory on the shipped default path is
        // the no-speech gate's WhisperVadFactory (it runs on every fresh recording, whatever
        // engine is selected), and that gate is lazy — so registration time here is comfortably
        // early. The helper logs an Error if something loaded first.
        //
        // TRN-53: the GPU-acceleration toggle rides the same pre-boot read pattern as the crash
        // opt-in (no service exists yet). OFF pins [Cpu] without probing, and forces the
        // Parakeet child onto PARAKEET_DEVICE=cpu below. A flip applies at NEXT app start — the
        // pin is frozen at the first native load — and the Settings copy says so.
        var gpuAccelerationEnabled = Helpers.GpuAccelerationReader.Read(DefaultSettingsPath);

        // TRN-50: the persisted GPU SELF-TEST verdicts, read pre-boot exactly like the toggle —
        // the marker needs no service, and a FAIL has to take effect BEFORE the first native
        // load (Whisper's order is frozen there) and before the Parakeet child's launch mode is
        // decided. A FAIL for this app version treats that engine as the toggle-OFF path: the
        // probe-free CPU pin for Whisper, Cpu for the child. Unknown/Pass change nothing. The
        // user re-arms with the toggle; a new app version re-tests on its own, and so does a new
        // display driver.
        // TRN-63: the display driver — the marker keys on its signature too, and this
        // pre-boot read is where the change is adopted (persisted once, logged once) BEFORE the
        // verdicts below are read, so a driver that fixes a FAIL lifts the pin at this start.
        var selfTestMarker = new GpuWarmupMarker(
            GpuWarmupMarker.DefaultPath, GpuWarmupMarker.CurrentAppVersion(), GpuWarmupMarker.CurrentDriverSignature());
        if (selfTestMarker.TryAdoptDriverChange())
        {
            // The signature is device-class data (adapter name + driver version, the same class
            // as the ggml device line) and DisplayDriverSignature.Clean already flattened it to
            // one bounded line, so it may sit mid-line; if it ever becomes a redacted property,
            // the SEC-3 rule wants it LAST on the line.
            // Stated as the fact (the stored results no longer apply), not as a promise: with the
            // toggle OFF or no Vulkan loader neither engine tests anything this start, and each
            // engine's own self-test line is the record of a test that ran (self-review).
            global::Serilog.Log.ForContext<App>().Information(
                "Display driver changed since the GPU self-test last ran (now {DriverSignature}) - the stored self-test results no longer apply; each engine re-tests when GPU acceleration lets it (TRN-63)",
                GpuWarmupMarker.CurrentDriverSignature());
        }
        var whisperSelfTest = selfTestMarker.ReadVerdict(Helpers.GpuSelfTestEngine.Whisper);
        var parakeetSelfTest = selfTestMarker.ReadVerdict(Helpers.GpuSelfTestEngine.Parakeet);
        // TRN-64: the predicate is PinsCpu — Fail, Inconclusive (a self-test that never finished on
        // a GPU-engaged process) or, since PR 2, Slower — at all five read sites here (Kimi r2 B3).
        if (gpuAccelerationEnabled && whisperSelfTest.PinsCpu)
        {
            global::Serilog.Log.ForContext<App>().Information(
                "Whisper GPU self-test {Outcome} for this app version on {GpuName} - pinning CPU-only load order until the next update, a display-driver change, or a toggle re-arm (TRN-50/63/64)",
                whisperSelfTest.Outcome,
                whisperSelfTest.GpuName ?? Helpers.GpuToggleAvailability.UnnamedGpu);
        }
        var backendPin = Helpers.WhisperBackendLog.PinLoadOrder(gpuAccelerationEnabled && !whisperSelfTest.PinsCpu);
        // TRN-50: the Settings advisory says "Whisper runs on the CPU" rather than "from the next
        // start" once THIS process has booted on the verdict (Grok diff r1).
        Helpers.GpuToggleAvailability.RecordWhisperSelfTestApplied(gpuAccelerationEnabled && whisperSelfTest.PinsCpu);

        // TRN-51: the Parakeet child's launch mode. Cpu when the toggle is off, and also when
        // the SYSTEM has no Vulkan loader at all (the probe's TryLoad is deliberately blind to
        // the loader bundled beside parakeet-server, so LoaderMissing = no GPU driver anywhere)
        // — byte-identical to the child's measured zero-ICD auto-fallback, one fewer moving
        // part. Every other outcome leaves Auto: the child carries its own bundled loader and
        // falls back to CPU on its own when no device enumerates (measured, both dev machines).
        // TRN-50: and Cpu when this app version's self-test recorded a pinning verdict for the
        // child (a FAIL, or since TRN-64 PR 2 a Slower).
        var parakeetLaunchMode =
            !gpuAccelerationEnabled
            || backendPin.Probe == Helpers.VulkanSupport.LoaderMissing
            || parakeetSelfTest.PinsCpu
                ? ParakeetLaunchMode.Cpu
                : ParakeetLaunchMode.Auto;
        if (gpuAccelerationEnabled && parakeetSelfTest.PinsCpu)
        {
            global::Serilog.Log.ForContext<App>().Information(
                "Parakeet GPU self-test {Outcome} for this app version on {GpuName} - the parakeet-server child starts on CPU until the next update, a display-driver change, or a toggle re-arm (TRN-50/63/64)",
                parakeetSelfTest.Outcome,
                parakeetSelfTest.GpuName ?? Helpers.GpuToggleAvailability.UnnamedGpu);
        }
        s_parakeetLaunchMode = parakeetLaunchMode;
        s_whisperVulkanUsable = backendPin.Probe == Helpers.VulkanSupport.Available;
        // TRN-61: the Models page row reads the same verdict — disabled and shown OFF only on
        // LoaderMissing, the one outcome under which the toggle changes nothing for either
        // engine above. Never written back to the setting.
        Helpers.GpuToggleAvailability.Record(backendPin.Probe);

        // System services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Services.Legal.LegalAcceptanceService>();
        services.AddSingleton<DebugHeartbeat>();
        services.AddSingleton<PasteDeliveryVerifier>();
        // Factory (not AddSingleton<ClipboardService>()) because the verifier seam is on
        // an INTERNAL constructor — PasteDeliveryVerifier is internal, so it cannot ride
        // the public signature the DI container would otherwise select (PST-6).
        services.AddSingleton(sp => new ClipboardService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<Services.Input.HotkeyService>(),
            sp.GetRequiredService<PasteDeliveryVerifier>()));
        services.AddSingleton<Helpers.LauncherDiscovery>();
        services.AddSingleton<AutostartRegistrationService>();

        // Auto-update (UPD-1). The build-flag default ships this as a no-op
        // (UpdateCheckFeature.IsEnabled == false), so the singleton exists
        // for DI consumers but short-circuits before any network call.
        // UpdateViewModel is transient so each navigation to the Updates page
        // re-reads the persisted LastUpdateCheckUtc + AutomaticUpdateCheck-
        // Enabled values from settings on construction. Same shape as
        // LicenseViewModel above (transient → fresh state per nav).
        services.AddSingleton<Services.Updates.IUpdateService, Services.Updates.UpdateService>();
        // UPD-1b: the automatic background poll. Singleton (it owns a single long-lived loop),
        // inert when UpdateCheckFeature.IsEnabled == false, and only Start()'d from behind the
        // LGL-1 gate in StartGatedRuntimeServices.
        services.AddSingleton<Services.Updates.IUpdateScheduler, Services.Updates.UpdateScheduler>();
        // UPD-4b: turns a background-detected update into an applied one when the user hasn't
        // opted out. Singleton (it owns one bounded retry loop). Resolved from
        // StartGatedRuntimeServices — i.e. ON THE UI THREAD, which is load-bearing: its ctor
        // captures the DispatcherQueue there, and a null capture makes it refuse every install.
        services.AddSingleton<Services.Updates.AutoUpdateInstaller>();
        // UPD-4b: owns the update subsystem's runtime wiring (which events, how they reach the UI
        // thread, teardown order) so App doesn't. Constructor-injected, so resolving this ONE
        // service replaces three App.Services calls rather than adding a fourth.
        services.AddSingleton<Services.Updates.UpdateRuntimeCoordinator>();
        // App implements IAppLifetime; resolve the live Application instance (set post-construction).
        services.AddSingleton<Services.IAppLifetime>(_ => (App)Current);
        services.AddTransient<UpdateViewModel>();

        // Maintenance gate: pull-model aggregator over subsystem "am I busy?"
        // sources. Consumers (Update apply, Reset-all-data) call Check() to
        // decide whether the destructive operation can proceed. Subsystems
        // call Register() to contribute status. See IMaintenanceGate.
        services.AddSingleton<Services.Maintenance.IMaintenanceGate, Services.Maintenance.MaintenanceGate>();

        // TRN-59: "Restart now" behind the GPU-acceleration toggle — the update-apply prefix
        // (lease → gate poll → durable settings → successor → graceful quit) reused for a
        // user-requested restart. The in-flight probe is a Func bound here rather than the whole
        // IUpdateService (the MaintenanceSourcesWiring probe shape): the lease it takes is
        // ref-counted, so an apply already holding its own lease has to be refused up front, or a
        // successor would start into Velopack's file swap.
        services.AddSingleton(sp => new AppRestartService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<Services.Maintenance.IMaintenanceGate>(),
            sp.GetRequiredService<Services.IAppLifetime>(),
            sp.GetRequiredService<Helpers.LauncherDiscovery>(),
            () => sp.GetRequiredService<Services.Updates.IUpdateService>().IsApplyInFlight));

        // Maintenance status sources — each constructor calls gate.Register and
        // holds the disposable for app lifetime. Routed through
        // MaintenanceSourcesWiring so the registration is unit-testable without
        // launching WinUI; OnLaunched calls the matching Resolve() to trigger
        // construction + registration (DI singletons are lazy by default).
        // Probes resolve the live singletons lazily at Check() time (post-launch) via the static
        // App.Services — NOT injected into the sources (which would invert construction order).
        // GetService (not GetRequiredService) + null guards keep IsBlocking non-throwing per the
        // IMaintenanceStatusSource contract.
        MaintenanceSourcesWiring.Register(
            services,
            audioTranscribeProbe: () => Views.Pages.AudioTranscribePage.IsTranscribing,
            recorderProbe: () =>
            {
                IServiceProvider? sp = Services;
                // IsRecordingPipelineBusy is a volatile mirror of RecordingState != Idle — safe to
                // read off the UI thread, which the maintenance gate's Check() may do.
                return sp?.GetService<MainViewModel>()?.IsRecordingPipelineBusy == true;
            },
            modelDownloadProbe: () =>
            {
                IServiceProvider? sp = Services;
                return sp?.GetService<ModelDownloadManager>()?.IsDownloading == true;
            },
            pasteRestoreProbe: () =>
            {
                IServiceProvider? sp = Services;
                return sp?.GetService<ClipboardService>()?.IsRestorePending == true;
            },
            imageGenerationProbe: () =>
            {
                IServiceProvider? sp = Services;
                // IsRunning is lock-guarded on the job service — safe off the UI thread.
                return sp?.GetService<Services.AIEnhancement.ImageGenerationJobService>()?.IsRunning == true;
            });

        // Audio services
        services.AddSingleton<AudioDeviceManager>();
        // AUD-1: recording-device selection + fallback policy (hub rule keeps it out of MainViewModel)
        services.AddSingleton<Services.Audio.RecordingDeviceSelectionService>();
        // AUD-6: standing warm capture — instant recording start. The gate closure re-reads the
        // setting + license per (re)start; onboarding comes from the same flag that gates
        // StartGatedRuntimeServices, whose warm-up path is the only startup caller — an
        // un-onboarded machine never opens a standing microphone.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var license = sp.GetRequiredService<VoiceWink.Services.Licensing.LicenseService>();
            return new VoiceWink.Services.Audio.StandingCaptureService(
                sp.GetRequiredService<VoiceWink.Services.Audio.RecordingDeviceSelectionService>(),
                startGate: () =>
                {
                    bool licenseAllows;
                    try
                    {
                        licenseAllows = !VoiceWink.Services.Licensing.LicenseService.IsRecordingBlocked(
                            license.GetCachedStatus());
                    }
                    catch
                    {
                        // Fail closed like the recording gate itself: an unreadable license
                        // state must not light a standing microphone.
                        licenseAllows = false;
                    }
                    return Helpers.StandingCapturePolicy.ShouldStart(
                        settingEnabled: settings.GetBool(AppDefaults.InstantRecordingEnabled, true),
                        onboardingComplete: settings.GetBool(AppDefaults.HasCompletedOnboarding),
                        // Structural: every caller of EnsureStartedAsync/EnsureHealthyAsync sits
                        // behind the LGL-1 gate (StartGatedRuntimeServices → warm-up / resume /
                        // unlock), so acceptance is already proven by control flow reaching it.
                        legalAccepted: true,
                        licenseAllowsRecording: licenseAllows);
                });
        });
        services.AddSingleton<AudioRecorderService>();
        // AUD-2: raw-gate-then-normalize coordinator (soft-speech gain; hub rule keeps it out of MainViewModel)
        services.AddSingleton<Services.Audio.RecordingAudioPreparation>();
        services.AddSingleton<SoundFeedbackService>();
        services.AddSingleton<SystemAudioMuteService>();
        services.AddSingleton<AudioFileProcessor>();

        // Input services
        // HKY-2: explicit factories — the observer isolates the watchdog's foreground
        // diagnostic (hub rule: HotkeyService must not own the async lookup), and the
        // explicit HotkeyService factory keeps the wiring visible rather than relying
        // on the container resolving an optional parameter.
        services.AddSingleton(sp => new WatchdogForegroundObserver(
            sp.GetRequiredService<IActiveWindowService>()));
        services.AddSingleton(sp => new HotkeyService(
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<WatchdogForegroundObserver>()));

        // System services (cont.)
        services.AddSingleton<ImportExportService>();

        // API Key management
        services.AddSingleton<ApiKeyManager>();

        // HTTP clients: IHttpClientFactory with named clients + pooled handlers.
        // Per-client Timeout / ConnectTimeout / retry policy lives in
        // VoiceWinkHttpClients (extracted for testability — ENH-3).
        VoiceWinkHttpClients.Register(services);

        // Licensing — LemonSqueezy activate/validate/deactivate via the "licensing" client.
        // Uses the same DPAPI SettingsService for encrypted key storage.
        //
        // Pass a delegate rather than a captured HttpClient: a Singleton holding one
        // HttpClient would pin the factory's pooled SocketsHttpHandler forever, defeating
        // handler rotation (default 2-minute HandlerLifetime) and any DNS refresh. The
        // factory call is cheap (each CreateClient returns a thin wrapper), so creating
        // a fresh wrapper per API call keeps the service singleton (LastKnown-cache-ready
        // for M2's hotkey-path optimisation) while preserving factory-managed lifetime.
        services.AddSingleton<LicenseService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var settings = sp.GetRequiredService<SettingsService>();
            return new LicenseService(
                () => factory.CreateClient("licensing"),
                settings,
                clock: null,
                fingerprintProvider: null,
                baseUrl: null,
                identityManifest: null,
                // LIC-21 PR A: the free-trial window's durable leg — the registry stamp that
                // survives a folder wipe (test-pinned) and, by design pending the VM uninstall +
                // reinstall row, a reinstall. Only this production path wires it (the
                // internal constructor); tests and the LS harness take the public shape, which
                // passes none, so they never touch a real HKCU.
                trialStampStore: new RegistryTrialStampStore());
        });
        // LIC-23: the daily in-session forced licence check. Singleton (it owns one long-lived
        // loop), constructor-injected, and only Start()'d from behind the LGL-1 gate in
        // StartGatedRuntimeServices — the same shape as the update poll.
        services.AddSingleton<LicenseRevalidationScheduler>();

        // Transcription services
        services.AddSingleton<WhisperTranscriptionService>();
        // Silero-VAD no-speech gate over the BUNDLED Assets/Models model (fail-open to
        // the RMS gate when unavailable); disposed with the container like the Whisper
        // service (bounded native teardown).
        services.AddSingleton<VoiceActivityDetectionService>();
        // Explicit factory rather than AddSingleton<T>(): the manager's models root is a REQUIRED
        // constructor argument, so no test can construct it against the user's live Models
        // directory by simply omitting one. This line is the single place that resolves the real
        // path.
        services.AddSingleton(sp => new ModelDownloadManager(
            sp.GetRequiredService<IHttpClientFactory>(), Helpers.AppPaths.EnsureModels()));
        // Local-runtime seam: every local model goes through LocalModelPreparer, which is the ONE
        // authority on which engine serves a model name — the five call sites that used to reach
        // WhisperTranscriptionService directly now ask the preparer instead.
        //
        // WhisperLocalRuntime holds the Whisper service but never disposes it (its WhisperFactory
        // must outlive the processors that hold native pointers into it).
        services.AddSingleton<ILocalTranscriptionRuntime, WhisperLocalRuntime>();

        // The second engine (TRN-1 step 3). Registered UNCONDITIONALLY — even when the build lever
        // is off or the CPU is unsupported — because ownership must not depend on runtime health:
        // an unregistered runtime makes its catalog rows UnknownModel, so a Parakeet selection would
        // be reported as a name nothing recognises instead of a model this build cannot run. It
        // claims its rows and answers PrepareOutcome.Unavailable instead.
        //
        // Registration is NOT what stops the Models page offering a 670 MB download on a build that
        // cannot run it — this comment used to say that, and both diff reviewers caught it. Prepare
        // runs after the download. The download gate reads ILocalTranscriptionRuntime.IsAvailable in
        // ModelManagementViewModel.DownloadModelAsync.
        //
        // ParakeetFeature.IsEnabled is read HERE, at the composition root, and injected — a const
        // consumed inside an `if` makes the tail unreachable (CS0162) and the disabled path
        // untestable in an ordinary build.
        // TRN-29: PcppFeature.IsEnabled is read HERE and only here — the flag's own
        // documented pattern. ON (every ordinary build since the G6 flip) registers the
        // resident-server stack; OFF (the -p:PcppEnabled=false kill-switch rebuild) registers
        // nothing, the seam params below resolve null, and the sherpa-only era returns
        // byte-identical. The server exe rides the app payload (absent = the
        // coordinator derives PcppRunnable=false and sherpa serves — fail-soft by state, not
        // by exception). Hoisted through a local, NOT branched on the const directly — the
        // VadFeature/_featureEnabled idiom: a const in an `if` is CS0162 in the OFF build,
        // and the repo gates on zero warnings.
        var pcppEnabled = Helpers.PcppFeature.IsEnabled;
        if (pcppEnabled)
        {
            services.AddSingleton(sp => new ParakeetServerTranscriptionClient(
                sp.GetRequiredService<IHttpClientFactory>()));
            services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<ParakeetServerTranscriptionClient>();
                return new ParakeetServerProcess(
                    new NativeParakeetServerLauncher(),
                    (baseUri, ct) => client.ProbeAsync(baseUri, ct),
                    initialLaunchMode: parakeetLaunchMode);
            });
            services.AddSingleton<IParakeetPcppBackend>(sp => new ParakeetBackendCoordinator(
                sp.GetRequiredService<ModelDownloadManager>(),
                sp.GetRequiredService<ParakeetServerProcess>(),
                sp.GetRequiredService<ParakeetServerTranscriptionClient>(),
                Helpers.AppPaths.EnsureModels(),
                // Inside runtimes\win-x64\ beside the VC++ DLLs the exe's import table
                // needs - a spawned child resolves imports from its own directory, and the
                // .NET probing path means nothing to the Win32 loader (G6 payload row).
                Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "parakeet-server.exe")));
        }

        services.AddSingleton(sp => new ParakeetTranscriptionService(
            Helpers.ParakeetFeature.IsEnabled,
            pcppBackend: sp.GetService<IParakeetPcppBackend>()));
        services.AddSingleton<ILocalTranscriptionRuntime>(sp => new ParakeetLocalRuntime(
            sp.GetRequiredService<ParakeetTranscriptionService>(),
            name => sp.GetRequiredService<ModelDownloadManager>()
                      .TryGetInstalledLocation(
                          Models.PredefinedModels.Models.First(m =>
                              string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))?.Path,
            sp.GetService<IParakeetPcppBackend>()));

        services.AddSingleton<LocalModelPreparer>();
        services.AddSingleton<TranscriptionServiceRegistry>();

        // Database — must specify file path here so EF Core uses the correct
        // SQLite file. Without this, the options constructor sets _dbPath=""
        // and SQLite opens a transient in-memory database, wiped between ops.
        services.AddDbContextFactory<VoiceWinkDbContext>(
            options => options.UseSqlite($"Data Source={Helpers.AppPaths.DatabaseFile}"));

        // History & data services
        // ENH-6b: the ONE reference-retention policy owner — registered ahead of
        // TranscriptionHistoryService, which takes it as a constructor dependency so
        // row deletion and retention can never apply divergent rules. ENH-6g: the
        // dedup key provider closes over the SENSITIVE-classified settings value
        // (get-or-create; ReferencePersistence resolves it once under its own lock).
        services.AddSingleton(sp => new ReferencePersistence(
            sp.GetRequiredService<IDbContextFactory<VoiceWinkDbContext>>(),
            AppPaths.ReferencesDir,
            () => Helpers.ReferenceDedupKey.GetOrCreate(sp.GetRequiredService<SettingsService>())));
        services.AddSingleton<TranscriptionHistoryService>();
        services.AddSingleton<LifetimeMetricsService>();
        services.AddSingleton<CsvExportService>();
        // REL-17: sweep roots are ctor-REQUIRED (a real-location default let tests sweep
        // real user data — the DataErasureService lesson, applied here too).
        services.AddSingleton(sp => new TranscriptionCleanupService(
            sp.GetRequiredService<TranscriptionHistoryService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<Helpers.RetainedWavLedger>(),
            sp.GetRequiredService<Services.AIEnhancement.ImageGenerationJobService>(),
            recordingsRoot: Helpers.AppPaths.RecordingsDir,
            reportsRoot: Helpers.AppPaths.ReportsDir,
            legacyTempRoot: global::System.IO.Path.GetTempPath(),
            imagesRoot: Helpers.AppPaths.ImagesDir));
        services.AddSingleton<CustomVocabularyService>();

        // Privacy / GDPR data-subject rights (LGL-2): export (Art. 15/20) + erasure (Art. 17)
        services.AddSingleton<Services.Privacy.GdprExportService>();
        // ENH-6e: erasure quiesces the reference-cleanup worker (bounded) before
        // releasing SQLite handles — a late background cleanup could otherwise hold
        // or recreate voicewink.db after the allowlist delete. Timeout aborts erasure.
        services.AddSingleton(sp => new Services.Privacy.DataErasureService(
            sp.GetRequiredService<Services.Maintenance.IMaintenanceGate>(),
            sp.GetRequiredService<IDbContextFactory<VoiceWinkDbContext>>(),
            sp.GetRequiredService<SettingsService>(),
            // REL-17: the legacy %TEMP% report location is ctor-REQUIRED (tests must
            // isolate it — a default here once swept the real %TEMP%).
            legacyTempDir: Path.GetTempPath(),
            quiesceMediaCleanup: () => sp.GetRequiredService<ReferencePersistence>()
                .QuiesceAsync(TimeSpan.FromSeconds(5)),
            resumeMediaCleanup: () => sp.GetRequiredService<ReferencePersistence>().ResumeCleanup()));

        // Changelog / What's-new dialog (REL-4)
        services.AddSingleton<Services.Support.ChangelogParser>();

        // AI Enhancement
        services.AddSingleton(AIProviderRegistry.CreateDefault());
        services.AddSingleton<AIEnhancementService>();
        // IMG-BG: single-slot coordinator for the background image generation job.
        services.AddSingleton<Services.AIEnhancement.ImageGenerationJobService>();
        services.AddSingleton<PromptDetectionService>();

        // App Mode + Context
        services.AddSingleton<ActiveWindowService>();
        // Map the interface to the same singleton so AppModeManager (which now
        // depends on IActiveWindowService for testability) resolves to the same
        // instance as any code path that still asks for the concrete.
        services.AddSingleton<IActiveWindowService>(sp => sp.GetRequiredService<ActiveWindowService>());
        services.AddSingleton<AppModeManager>();
        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<SelectedTextService>();

        // Text processing pipeline
        services.AddSingleton<TranscriptionOutputFilter>();
        services.AddSingleton<FillerWordManager>();
        services.AddSingleton<TranscriptionTextFormatter>();
        services.AddSingleton<WordReplacementService>();
        services.AddSingleton<TextPipelineRunner>();
        services.AddSingleton<TranscriptionHistoryWriter>();
        services.AddSingleton<RedoCoordinator>();
        // REL-12: tracks failure-retained recording WAVs until deletion is confirmed;
        // drained at shutdown (Cleanup + the forced-exit flush).
        services.AddSingleton<Helpers.RetainedWavLedger>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ModelManagementViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<EnhancementViewModel>();
        services.AddSingleton<DictionaryViewModel>();
        // Composes an App-Mode add with its default-enhancement resolve/add (AppModeManager +
        // EnhancementViewModel, both singletons above); used by the App-Mode page.
        services.AddSingleton<AppModeSetupCoordinator>();
        // LicenseViewModel is transient: each page navigation re-queries CheckAsync and
        // rebuilds local state on construction, so a fresh instance is the right scope.
        services.AddTransient<LicenseViewModel>();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Information("Application launched");

        // Load WinUI theme resources in code. App.xaml is empty to avoid
        // native crashes from LoadComponent in CLI builds.
        try
        {
            Resources ??= new ResourceDictionary();
            Resources.MergedDictionaries.Add(
                new Microsoft.UI.Xaml.Controls.XamlControlsResources());
            // Use Windows 11's sharper variable font for all controls
            Resources["ContentControlThemeFontFamily"] = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Text");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load XamlControlsResources");
        }

        try
        {
            // Eagerly resolve maintenance status sources so each ctor runs and
            // self-registers with IMaintenanceGate. DI singletons are lazy by
            // default — without this resolve, gate.Check() would see an empty
            // source list and incorrectly return CanProceed=true while a file
            // transcription is in flight. Pinned by MaintenanceSourcesWiringTests.
            // Returns the StartupMaintenance handle too — held, not started, until after the model
            // migration below (see the Start() call there).
            var startupMaintenance = MaintenanceSourcesWiring.Resolve(Services);

            // Database initialization runs on a background thread to avoid blocking
            // the UI thread on startup. Awaited to ensure schema is ready before
            // any page queries the database.
            await Task.Run(InitializeDatabase);

            // ENH-6b: reference-copy orphan sweep — the crash-window backstop (a copy
            // written but its row never committed). MUST run after the migration above
            // added the ReferenceImagePath column; fire-and-forget because the sweep is
            // internally fail-contained and startup must not wait on file IO. The 48 h
            // grace keeps it clear of any in-flight generation's uncommitted copy.
            // F19: gated by the same cleanup lease as history cleanup — the lease is held
            // INSIDE the Task.Run body across the awaited sweep (disposing around the
            // fire-and-forget launch would reopen the erase-vs-sweep race, Codex R4), and
            // skipped entirely if an erasure/apply already holds an exclusive lease.
            _ = Task.Run(async () =>
            {
                var refGate = Services.GetService(typeof(Services.Maintenance.IMaintenanceGate))
                    as Services.Maintenance.IMaintenanceGate;
                IDisposable? sweepLease = null;
                if (refGate is not null && !refGate.TryBeginCleanupPass(out sweepLease))
                    return; // exclusive maintenance in progress — skip the backstop sweep this launch
                try
                {
                    await Services.GetRequiredService<ReferencePersistence>()
                        .SweepOrphansAsync(TimeSpan.FromHours(48));
                }
                finally
                {
                    sweepLease?.Dispose();
                }
            });

            // Initialize theme before creating any windows
            var settingsSvc = Services.GetRequiredService<SettingsService>();

            // One-way legacy-key migration (PowerMode → AppMode rename, 2026-07-08). Eager —
            // runs before any window exists so no user-triggered settings export can snapshot
            // the pre-rename key. Raw-settings consumers also call this defensively.
            AppModeSettingsMigration.Run(settingsSvc);

            // TRN-6 (2026-08-03): the local catalogue became q8-only, and later the same day dropped
            // its English-only rows — so a persisted name from EITHER generation can resolve to
            // nothing. Rewrite those names to their successors.
            // Same eager placement and reasoning as the migration above: before any window, before
            // any service caches settings, and before an export could snapshot a dead name.
            //
            // This covers BOTH selection surfaces. The global model is the obvious one; App Mode
            // per-app overrides are the one a first pass missed (caught at review) — an override is
            // neither the global selection nor necessarily installed, so nothing else would have
            // touched it and that user's App Mode would have silently stopped resolving.
            LocalModelMigration.RunOnSettings(settingsSvc);

            // Once-per-launch background housekeeping — today, deleting the on-disk files of the
            // models the migration above just retired. Fire-and-forget; nothing below waits on it,
            // and every failure inside is handled there. Started off the handle the composition
            // call already returned, so this adds no App.Services lookup (AGENTS.md).
            startupMaintenance.Start();

            // UPD-4: re-seed the shipped default prompts + App-Mode configs when the SOURCE
            // defaults have changed (owner: overwrite defaults, only if changed). AFTER the
            // migrations above (reads their post-migration state) and BEFORE any service reads
            // prompts/configs — the AIEnhancementService/AppModeManager caches don't exist yet,
            // so this operates purely on SettingsService. Fail-soft internally; never aborts launch.
            DefaultPromptsReseed.Run(settingsSvc);

            // DCT-1: offer the shipped Dictionary defaults (VoiceWink's own names + three
            // replacement rules) once each — AFTER the awaited database init above (which hands
            // over its factory and whether it created the file), BEFORE the hotkey is wired so the
            // first transcription's replacement cache reads the seeded rows. Off the UI thread
            // like the init itself (SQLite IO). Fail-soft internally; a failed init (null factory)
            // simply skips the seed — there is no database to seed.
            if (_dbFactoryForSeed is { } seedFactory)
            {
                var createdThisLaunch = _databaseCreatedThisLaunch;
                await Task.Run(() => DefaultDictionarySeed.Run(seedFactory, settingsSvc, createdThisLaunch));
            }

            // REL-17: drop the pre-lift DEBUG-only trace key so a stale Debug-era opt-in
            // can never silently activate tracing now that the feature ships in all builds
            // (rollout hazard, Codex plan round 3). Presence-based, one-way, idempotent.
            PromptTraceKeyMigration.Run(settingsSvc);

            // 2026-09-13: an install whose "Reset to defaults" wrote the old filler list (the one
            // that deleted Dutch/German "er") drops it so the corrected default applies. Same
            // shape as the line above: presence-and-value based, one-way, idempotent, fail-soft.
            FillerWordsMigration.Run(settingsSvc);

            // Prompt/output trace (all builds since REL-17; owner request 2026-07-18):
            // wire the static gates once. Opt-in, default OFF. The startup sweep enforces
            // the 7-day retention Serilog doesn't cover; the hourly cleanup pass re-runs
            // it so week-long tray sessions stay bounded.
            // REL-21: image prompts ride a second gate under the same opt-in, default OFF.
            PromptTraceLog.Enabled = () => settingsSvc.GetBool(AppDefaults.PromptTraceLoggingOptIn, false);
            PromptTraceLog.ImagePromptsEnabled =
                () => settingsSvc.GetBool(AppDefaults.PromptTraceIncludeImagePrompts, false);
            PromptTraceLog.Directory = AppPaths.LogsDir;
            PromptTraceLog.SweepOldTraces();

            var themeName = settingsSvc.GetString(AppDefaults.ThemeMode, "System");
            if (Enum.TryParse<ThemeMode>(themeName, out var themeMode))
            {
                AppTheme.SetTheme(themeMode);
            }
            else
            {
                Log.Warning("Setting '{Key}'='{Value}' failed to parse as ThemeMode, defaulting to {Default}",
                    AppDefaults.ThemeMode, themeName, ThemeMode.Dark);
            }

            // Create and show main window. LGL-1: legal route is deferred so the
            // license-route + runtime services can't fire before acceptance.
            _mainWindow = new MainWindow(deferLicenseRoute: true);
            MainWindow = _mainWindow;
            MainWindowInstance = _mainWindow;
            // Wire OnboardingCompleted so the wizard's last step triggers gated
            // runtime startup. The handler is idempotent — guarded by the
            // Interlocked flag inside StartGatedRuntimeServices.
            //
            // UPD-4b: the completion also reconciles the update scheduler with the wizard's
            // check-updates choice. This exists for the Settings → "Relaunch setup wizard" path,
            // where the scheduler is ALREADY RUNNING and the gated start below is a no-op — a
            // user who launched with checks off and enabled them in the re-run would otherwise
            // get no polling until the next launch. On a FIRST run it is a harmless re-derive
            // (Start() just armed from the same setting). Wizard choices apply at Done, like
            // every other onboarding decision — not per toggle-flip mid-wizard.
            _mainWindow.OnboardingCompleted += () =>
            {
                StartGatedRuntimeServices();
                _updateRuntime?.ReconcileCheckSetting();
            };
            _mainWindow.Closed += (s, e) =>
            {
                // Hide to tray instead of closing (unless quit was requested via tray menu).
                // LGL-1 / Codex diff-review challenge: only hide-to-tray when the tray icon
                // actually exists. SetupTrayIcon now runs inside StartGatedRuntimeServices,
                // so during onboarding or a stale-acceptance modal the tray icon doesn't
                // exist yet — closing would otherwise strand the app hidden with no way to
                // restore the window.
                if (!_isQuitting && _trayIconReady)
                {
                    e.Handled = true;
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                    Helpers.NativeInterop.ShowWindow(hwnd, Helpers.NativeInterop.SW_HIDE);
                    Log.Debug("Main window close intercepted — hidden to tray");
                    return;
                }

                Log.Information("Main window closed (tray ready: {TrayReady}, quitting: {Quitting})",
                    _trayIconReady, _isQuitting);
                Cleanup();
                Environment.Exit(0);
            };
            _mainWindow.Activate();

            // LGL-1: defer the StartMinimized hide until after the legal modal
            // dismisses — a hidden owner can't host a ContentDialog. New users
            // never hide; their wizard-occupied window stays visible.
            // UPD-3c: the apply path's one-shot flag overrides StartMinimized for THIS
            // launch (an update restart is attended — the user expects visible
            // confirmation). READ without consuming here: the flag is cleared only when
            // the onboarded post-legal path proceeds, so a legal decline preserves it.
            var startupSettings = Services.GetRequiredService<SettingsService>();
            var showAfterUpdate = Helpers.UpdateRestartVisibility.ReadShowAfterUpdate(startupSettings);
            var startMinimized = Helpers.UpdateRestartVisibility.StartMinimizedIntent(
                startupSettings.GetBool(AppDefaults.StartMinimized, true), showAfterUpdate);
            _startMinimizedIntentThisLaunch = startMinimized;
            if (showAfterUpdate)
                Log.Information("Update-restart one-shot present — starting visible for confirmation");

            // Minimize to system tray (tray icon) — no window-visibility side effect.
            SetupMinimizeToTray();

            // Apply user's theme preference and update on theme change
            if (_mainWindow.Content is FrameworkElement root)
            {
                root.RequestedTheme = AppTheme.ElementTheme;
                AppTheme.ThemeChanged += () =>
                {
                    _mainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_mainWindow.Content is FrameworkElement r)
                            r.RequestedTheme = AppTheme.ElementTheme;
                    });
                };
            }

            var viewModel = Services.GetRequiredService<MainViewModel>();
            InitializeMiniRecorder(viewModel);
            // TRN-64: the restart-offer host below needs the restart service; resolved ONCE here, beside
            // the ViewModel it hosts for, rather than per offer (AGENTS.md: no new App.Services
            // service-locator usages — Codex diff r1).
            var restartService = Services.GetRequiredService<global::VoiceWink.Services.System.AppRestartService>();

            // Image options picker dialog — shown when an image generation prompt has AskImageSize.
            // Reuses the same rich dialog as the redo/regenerate flow: an editable input box
            // pre-filled with the transcribed text, plus provider / model / aspect / size / quality.
            viewModel.ShowImageOptionsPickerRequested += async (pickerContext, ct) =>
            {
                try
                {
                    // UI-7: before RestoreMainWindow, which is what makes VoiceWink the foreground.
                    CapturePickerForeground();
                    // UI-7: the scope opens BEFORE RestoreMainWindow and closes when the dialog
                    // does. Arming it around ShowAsync alone left the 200 ms below unguarded —
                    // VoiceWink already held the foreground there while the flag was still clear,
                    // so a record hotkey in that window captured our own dialog and was not
                    // suppressed (Kimi round 4). Arming this early costs nothing: suppression also
                    // requires the foreground to BE ours, so nothing is suppressed until
                    // RestoreMainWindow has actually taken it.
                    using (viewModel.BeginPickerDialogVisible())
                    {
                        // Don't hide MiniRecorder — user should see recording is active
                        RestoreMainWindow();
                        await Task.Delay(200); // Let window restore complete before showing dialog
                        // Re-force foreground after delay — the first call can lose focus
                        // if the OS processes other window messages during the delay.
                        if (_mainWindow != null)
                        {
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                            Helpers.NativeInterop.SetForegroundWindow(hwnd);
                        }
                        var mainWindow = App.MainWindow;
                        if (mainWindow?.Content is not FrameworkElement root)
                            return null;

                        // No prompt-title suffix for image generation: the image description is
                        // already shown in the dialog body, and an image redo can fall back to the
                        // active TEXT prompt ("Improve Transcription"), meaningless here.
                        return await ShowEnhancementOptionsDialogAsync(
                            root, isImageGeneration: true, pickerContext, "Generate Image", "Generate", ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to show image options picker");
                    return null;
                }
                finally
                {
                    ReleasePickerForeground();
                }
            };

            // TRN-64: the restart offer after a REFUSED Whisper decode (its GPU failed, or never
            // finished, its self-test in this process). Thin routing, the retry picker's exact
            // shape: the decision (once per session, after the retry pill) stays in the ViewModel;
            // the App only hosts the dialog. Returns whether it was shown, so the ViewModel can
            // release its one-shot when it was not.
            viewModel.ShowGpuSelfTestRestartRequested += async refusal =>
            {
                try
                {
                    CapturePickerForeground();
                    using (viewModel.BeginPickerDialogVisible())
                    {
                        RestoreMainWindow();
                        await Task.Delay(100); // Let window activate before showing dialog

                        if (App.MainWindow?.Content is not FrameworkElement root || root.XamlRoot is null)
                        {
                            Log.Warning("GPU self-test restart offer skipped — no XamlRoot available");
                            return false;
                        }
                        var dialog = new Views.Dialogs.RestartToApplyDialog(
                            restartService,
                            Views.Dialogs.RestartToApplyDialog.GpuSelfTestBody(refusal),
                            global::VoiceWink.Services.System.AppRestartReason.GpuSelfTestFailed)
                        {
                            XamlRoot = root.XamlRoot,
                        };
                        await dialog.ShowAsync();
                        ReleasePickerForeground();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to show the GPU self-test restart offer");
                    return false;
                }
                finally
                {
                    ReleasePickerForeground();
                }
            };

            // TRN-17: transcription-retry picker (model + recognition language, this run only).
            // Thin routing by design — the dialog is a real class in Views/Dialogs, and the
            // decisions stay in the ViewModel because REL-12's guard order belongs there.
            viewModel.ShowTranscriptionRetryPickerRequested += async request =>
            {
                try
                {
                    if (viewModel.IsPickerDialogVisible)
                    {
                        // TRN-64 (self-review): the restart offer can be on screen when the pill's
                        // Retry is tapped — WinUI allows one ContentDialog per XamlRoot, and the
                        // second ShowAsync throws (an Error, so a Sentry event). Null reads as
                        // cancel: the retry stays armed for after the dialog closes.
                        Log.Warning("Retry picker skipped — another dialog is open");
                        return null;
                    }
                    // UI-7: before RestoreMainWindow, which is what makes VoiceWink the foreground.
                    CapturePickerForeground();
                    // UI-7: the scope opens BEFORE RestoreMainWindow — see the image handler above
                    // for why the 100 ms below cannot be left outside it.
                    ContentDialogResult result;
                    using (viewModel.BeginPickerDialogVisible())
                    {
                        // The pill can be tapped with the main window minimized to tray; a
                        // ContentDialog needs a XamlRoot to show on.
                        RestoreMainWindow();
                        await Task.Delay(100); // Let window activate before showing dialog

                        if (App.MainWindow?.Content is not FrameworkElement root || root.XamlRoot is null)
                        {
                            // The VM reads null as a cancel, so the retry stays armed and the
                            // recording survives — the right failure for "we could not show it".
                            Log.Warning("Retry picker skipped — no XamlRoot available");
                            return null;
                        }

                        var dialog = new Views.Dialogs.TranscriptionRetryOptionsDialog(request)
                        {
                            XamlRoot = root.XamlRoot,
                        };
                        result = await dialog.ShowAsync();
                        // Released here rather than only in the finally so the foreground goes back
                        // the moment the dialog closes.
                        ReleasePickerForeground();
                        return result == ContentDialogResult.Primary ? dialog.Selection : null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to show the transcription retry picker");
                    return null;
                }
                finally
                {
                    ReleasePickerForeground();
                }
            };

            // Model picker dialog for redo — shown on main window which has XamlRoot
            viewModel.ShowModelPickerRequested += async (isImageGeneration, capturedContext) =>
            {
                try
                {
                    // UI-7: before RestoreMainWindow, which is what makes VoiceWink the foreground.
                    CapturePickerForeground();

                    // UI-7: the scope opens BEFORE RestoreMainWindow and closes as soon as the
                    // dialog returns — both ends matter, for different reasons.
                    //
                    // EARLY, because RestoreMainWindow foregrounds VoiceWink and the 100 ms below
                    // was outside the scope: a record hotkey landing there captured our own dialog
                    // unsuppressed (Kimi round 4). Arming early is free — suppression also requires
                    // the foreground to BE ours, so nothing is suppressed until it actually is.
                    //
                    // CLOSED EARLY, because this handler goes on to await RedoEnhancementAsync, a
                    // multi-second cloud call. Releasing only in the outer finally left VoiceWink in
                    // front for the whole of it and the flag set long after the dialog was gone
                    // (Codex round 2).
                    MainViewModel.EnhancementDialogSelection? selection;
                    using (viewModel.BeginPickerDialogVisible())
                    {
                        // Restore/activate main window so the dialog is visible
                        // (main window may be minimized to tray when redo is clicked)
                        RestoreMainWindow();
                        await Task.Delay(100); // Let window activate before showing dialog

                        var mainWindow = App.MainWindow;
                        if (mainWindow?.Content is not FrameworkElement root)
                        {
                            viewModel.ClearRedoStatePublic();
                            return;
                        }

                        // IsNewGeneration marks the IMG-1 text-first entry (new generation,
                        // no lineage) — "Regenerate" would be wrong there.
                        // Image titles carry NO prompt-title suffix: the image description is in the
                        // dialog body, and an image redo (esp. from History) can fall back to the
                        // active TEXT prompt ("Improve Transcription"), misleading for an image.
                        // Text re-enhance keeps the prompt name — there it's the real prompt.
                        var title = isImageGeneration
                            ? (capturedContext.IsNewGeneration ? "Generate Image" : "Regenerate Image")
                            : $"Re-enhance • {capturedContext.Prompt?.Title ?? "Default"}";

                        var primaryText = isImageGeneration && capturedContext.IsNewGeneration
                            ? "Generate"
                            : "Run";
                        selection = await ShowEnhancementOptionsDialogAsync(
                            root, isImageGeneration, capturedContext, title, primaryText);
                    }
                    ReleasePickerForeground();

                    if (selection is null || string.IsNullOrWhiteSpace(selection.Model))
                    {
                        // User cancelled, or no model available — clear redo state
                        // (the dismiss timer was stopped in RequestModelPicker).
                        viewModel.ClearRedoStatePublic();
                        return;
                    }

                    var selectedModel = selection.Model!;
                    var selectedProvider = selection.Provider;

                    // Pass edited text and image size through the context
                    var editedText = selection.EditedText;
                    var ctx = string.Equals(editedText, capturedContext.RawText, StringComparison.Ordinal)
                        ? capturedContext
                        : capturedContext with { RawText = editedText };

                    // ENH-6: the dialog is authoritative for the references on confirm —
                    // it seeded from the context, re-validated per item, and the user
                    // may have added/removed some. Image contexts only; text contexts
                    // never carry references. IMG-3: the confirmed Versions count rides
                    // the same way (DispatchImageRedoAsync clamps + consumes it).
                    if (isImageGeneration)
                        ctx = ctx with { References = selection.References, PreviousImageCount = selection.Count };

                    // Apply selected aspect / size / quality — clone prompt to avoid mutating the cached original
                    if (isImageGeneration)
                    {
                        var selectedAspect = selection.Aspect;
                        var selectedSizeTier = selection.SizeTier;
                        var selectedQuality = selection.Quality;

                        if (ctx.Prompt != null)
                        {
                            var clonedPrompt = ctx.Prompt.Clone();
                            clonedPrompt.ImageAspect = selectedAspect;
                            clonedPrompt.ImageSizeTier = selectedSizeTier;
                            clonedPrompt.ImageQuality = selectedQuality;
                            ctx = ctx with
                            {
                                Prompt = clonedPrompt,
                                PreviousImageAspect = selectedAspect,
                                PreviousImageSizeTier = selectedSizeTier,
                                PreviousImageQuality = selectedQuality,
                            };
                        }
                        else
                        {
                            // Prompt is null (IMG-1 text-first entry, or a failed redo
                            // chain). The Previous* fields alone are NOT enough:
                            // GenerateImageWithModelAsync reads aspect/size/quality from
                            // the PROMPT, so a null prompt silently generated with
                            // defaults and the success-history row lost the choices
                            // (Codex IMG-1 review). Synthesize a minimal image prompt
                            // carrying the dialog selections through the existing flow.
                            ctx = ctx with
                            {
                                Prompt = new Models.CustomPrompt
                                {
                                    Title = "Generate Image",
                                    IsImageGeneration = true,
                                    ImageAspect = selectedAspect,
                                    ImageSizeTier = selectedSizeTier,
                                    ImageQuality = selectedQuality,
                                },
                                PreviousImageAspect = selectedAspect,
                                PreviousImageSizeTier = selectedSizeTier,
                                PreviousImageQuality = selectedQuality,
                            };
                        }
                    }

                    await viewModel.RedoEnhancementAsync(selectedModel, selectedProvider, ctx);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to show model picker for redo");
                    viewModel.ClearRedoStatePublic();
                }
                finally
                {
                    ReleasePickerForeground();
                }
            };

            // LGL-1: legal-acceptance gate. For onboarded users, this awaits
            // the modal until they accept (or exits via Decline). For new
            // users in onboarding, this returns true immediately and the
            // gated runtime services start later from OnboardingCompleted.
            if (!await EnsureLegalAcceptanceAsync())
            {
                Log.Warning("Legal acceptance was not satisfied; aborting startup");
                return;
            }

            // For onboarded users only — license route + gated runtime services.
            // New users land here only after OnboardingCompleted re-enters this
            // path; double-arming is prevented by the Interlocked flag inside
            // StartGatedRuntimeServices.
            var settingsForRoute = Services.GetRequiredService<SettingsService>();
            if (settingsForRoute.GetBool(AppDefaults.HasCompletedOnboarding))
            {
                _mainWindow.ApplyLicenseStartupRouteIfReady();

                // Autostart reconciliation: rewrite HKCU\Run to match the current channel's
                // launcher command. Idempotent — for users whose Run entry already matches,
                // it's a no-op. For portable→Velopack migrators, it rewrites stale VBS entries
                // to the EXE path. Runs only on the onboarded path; new users go through the
                // wizard's OnboardingCompleted handler which calls AutostartRegistrationService
                // directly.
                Services.GetRequiredService<AutostartRegistrationService>()
                    .Apply(settingsForRoute.GetBool(AppDefaults.LaunchAtLogin, true));

                // UPD-3c: the onboarded post-legal launch is proceeding — consume the
                // update-restart one-shot NOW (clear-on-use). Any earlier and a legal
                // decline would burn the visible-restart confirmation without showing it.
                Helpers.UpdateRestartVisibility.ConsumeShowAfterUpdate(settingsForRoute);

                StartGatedRuntimeServices();

                // Defer the StartMinimized hide until after the (possible) modal
                // dismisses. New users never reach here pre-Done, so the wizard
                // window stays visible until OnboardingCompleted.
                // LGL-1 / Codex round-2: also gate on tray-icon readiness — a
                // tray-create failure mid-StartGatedRuntimeServices must not
                // strand the user with no way back to the window.
                if (startMinimized && _trayIconReady)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                    Helpers.NativeInterop.ShowWindow(hwnd, Helpers.NativeInterop.SW_HIDE);
                    Log.Information("Started minimized to tray (post-legal-gate)");
                }
                else if (startMinimized)
                {
                    Log.Warning("StartMinimized requested but tray icon not ready — leaving window visible");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to launch application");
            throw;
        }
    }

    /// <summary>
    /// LGL-1 legal-acceptance gate. Returns <c>true</c> when it is safe for the
    /// caller to proceed with runtime startup; returns <c>false</c> after exiting
    /// the process (decline path). For onboarded users with stale or absent
    /// acceptance, shows the modal and re-prompts on persistence failure (the
    /// dialog itself owns the retry loop via <c>args.Cancel</c>). For new users
    /// (<c>HasCompletedOnboarding == false</c>) returns <c>true</c> immediately —
    /// their acceptance is captured by the wizard's Legal step at index 1.
    /// Bundle corruption fail-closes via <see cref="LegalBundleCorruptedDialog"/>.
    /// </summary>
    private async Task<bool> EnsureLegalAcceptanceAsync()
    {
        var settings = Services.GetRequiredService<SettingsService>();
        var legal = Services.GetRequiredService<Services.Legal.LegalAcceptanceService>();

        if (!settings.GetBool(AppDefaults.HasCompletedOnboarding))
        {
            // New user — acceptance flows through the wizard, not this gate.
            return true;
        }

        // Flush-then-exit for the legal gate's fail-closed paths (F21). The bare
        // Environment.Exit(0) calls here skipped the Sentry/Serilog flush the crash handlers
        // above treat as mandatory — the Log.Error explaining WHY the app refused to start
        // (corrupt bundle, dead XamlRoot, dialog failure) died with the process. Same
        // best-effort shape as the handlers: a flush failure must never prevent the exit.
        static void ExitAfterFlush()
        {
            try
            {
                if (SentryInitializer.IsInitialized)
                {
                    try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); }
                    catch { /* best-effort on the way out */ }
                }
                try { Log.CloseAndFlush(); }
                catch { /* a throwing sink must not block the exit (Codex R1) */ }
            }
            finally
            {
                Environment.Exit(0); // the fail-closed exit is unconditional
            }
        }

        if (!legal.BundleHealthy)
        {
            // Fail-closed for the gate path; LegalPage degrades softly elsewhere.
            await WaitForXamlRootAsync();
            try
            {
                if (_mainWindow?.Content is FrameworkElement root && root.XamlRoot is not null)
                {
                    var corrupted = new Views.Dialogs.LegalBundleCorruptedDialog
                    { XamlRoot = root.XamlRoot };
                    await corrupted.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to show legal-bundle-corrupted dialog");
            }
            ExitAfterFlush();
            return false;
        }

        if (!legal.RequiresPromptAtStartup()) return true;

        // ContentDialog requires a live XamlRoot. After Activate() returns,
        // _mainWindow.Content.XamlRoot is briefly null until the first frame
        // renders — attempting ShowAsync() too early throws "This element does
        // not have a XamlRoot." Wait for the first Activated event so the
        // root is wired up.
        await WaitForXamlRootAsync();

        if (_mainWindow?.Content is not FrameworkElement winRoot || winRoot.XamlRoot is null)
        {
            // Genuinely unable to show the modal — fail-closed.
            Log.Error("Legal gate: XamlRoot still null after first activation; failing closed and exiting");
            ExitAfterFlush();
            return false;
        }

        try
        {
            var dialog = new Views.Dialogs.LegalAcceptanceDialog(legal) { XamlRoot = winRoot.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return true;
            ExitAfterFlush(); // user declined — flush the decline log line too
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Legal acceptance dialog failed; failing closed and exiting");
            ExitAfterFlush();
            return false;
        }
    }

    private void InitializeMiniRecorder(MainViewModel viewModel)
    {
        _miniRecorderViewModel = viewModel;
        _pillController = new MiniRecorderPresentationController(
            new DispatcherPillScheduler(_mainWindow!.DispatcherQueue));
        _miniRecorderWindow = CreateMiniRecorderWindow(viewModel);
        AttachMiniRecorderWindow(_miniRecorderWindow);
        // Initial attach — no DPI concern on first show; renders the (empty) current presentation,
        // i.e. keeps the pill hidden until a slot is published.
        _pillController.Attach(_miniRecorderWindow);

        viewModel.PropertyChanged += OnMiniRecorderViewModelPropertyChanged;
        viewModel.ShowMiniRecorderError += OnMiniRecorderErrorRequested;
        viewModel.DownloadProgressUpdated += OnMiniRecorderDownloadProgressUpdated;
        viewModel.AffordanceArmed += OnAffordanceArmed;
        viewModel.OpenLicensePageRequested += OnOpenLicensePageRequested;

        StartMiniRecorderDisplayChangeHandling();
    }

    private MiniRecorderWindow CreateMiniRecorderWindow(MainViewModel viewModel)
        => new(viewModel, _pendingMiniRecorderTarget);

    private void AttachMiniRecorderWindow(MiniRecorderWindow window)
    {
        _pillDismissHandler ??= OnPillDismissRequested;
        window.DismissRequested += _pillDismissHandler;
        _pillActionHandler ??= OnPillActionRequested;
        window.ActionRequested += _pillActionHandler;
        _pillStopHandler ??= OnPillStopRequested;
        window.StopRequested += _pillStopHandler;
        _pillHideHandler ??= OnPillHideRequested;
        window.HideRequested += _pillHideHandler;
        _pillMessageActionHandler ??= OnPillMessageActionRequested;
        window.MessageActionRequested += _pillMessageActionHandler;
        _miniRecorderCrossDpiHandler ??= OnMiniRecorderCrossDpiRecreateRequested;
        window.CrossDpiRecreateRequested = _miniRecorderCrossDpiHandler;
    }

    private void DetachMiniRecorderWindow(MiniRecorderWindow window)
    {
        if (_pillDismissHandler != null)
            window.DismissRequested -= _pillDismissHandler;
        if (_pillActionHandler != null)
            window.ActionRequested -= _pillActionHandler;
        if (_pillStopHandler != null)
            window.StopRequested -= _pillStopHandler;
        if (_pillHideHandler != null)
            window.HideRequested -= _pillHideHandler;
        if (_pillMessageActionHandler != null)
            window.MessageActionRequested -= _pillMessageActionHandler;
        window.CrossDpiRecreateRequested = null;
    }

    /// <summary>
    /// Re-attach the presentation controller to the current window on the NEXT dispatcher tick rather
    /// than synchronously, so the Win32 messages queued by the new window's constructor (notably
    /// WM_DPICHANGED from MoveAndResize) are pumped before Attach's render runs — the new window's
    /// XamlRoot reaches target DPI first. Attach clears the recreate render-suspension. Falls back to
    /// synchronous if the dispatcher is unavailable.
    /// </summary>
    private void DeferControllerReattach()
    {
        void Reattach()
        {
            if (_isQuitting) return;
            if (_pillController != null && _miniRecorderWindow != null)
                _pillController.Attach(_miniRecorderWindow);
        }

        var disp = _mainWindow?.DispatcherQueue;
        if (disp == null || !disp.TryEnqueue(Reattach))
            Reattach();
    }

    /// <summary>
    /// Called by MiniRecorderWindow.Show() when it detects a cross-DPI mismatch
    /// that warrants a full window recreate. Returns true if the recreate was
    /// accepted and queued — Show() will abort its in-place flow. Returns false
    /// if a recreate is already queued / in flight / inside the post-completion
    /// quiet period / inside the accepted-cooldown — Show() falls through to its
    /// in-place reconciliation, which is robust to stale RasterizationScale.
    ///
    /// <para>Gating is delegated to <see cref="MiniRecorderRecreateGate"/> so the
    /// decision is unit-testable and so the four reject reasons can be logged
    /// distinctly (helpful when triaging future crash reports).</para>
    /// </summary>
    private bool OnMiniRecorderCrossDpiRecreateRequested(MiniRecorderWindow.MonitorPlacement target, string trigger)
        => RequestMiniRecorderRecreate(target, trigger);

    /// <summary>
    /// Recreate-request path for the show-time cross-DPI callback (the only
    /// remaining trigger — recreates are lazy/show-time only since the idle
    /// repark's removal): evaluates <see cref="MiniRecorderRecreateGate"/>,
    /// queues the recreate to the next dispatcher tick, and preserves the
    /// queued-stamp discipline. Returns true when the recreate was accepted and
    /// queued.
    /// </summary>
    private bool RequestMiniRecorderRecreate(MiniRecorderWindow.MonitorPlacement target, string trigger)
    {
        var decision = MiniRecorderRecreateGate.Evaluate(
            DateTime.UtcNow,
            _isQuitting,
            _miniRecorderRecreateQueued,
            _miniRecorderRecreateInProgress,
            _lastRecreateCompletionUtc);

        if (decision != MiniRecorderRecreateGate.Decision.Accept)
        {
            Log.Information(
                "MiniRecorder recreate ({Trigger}) rejected: {Reason} (target scale={Scale})",
                trigger, decision, target.Scale);
            return false;
        }

        var dispatcher = _mainWindow?.DispatcherQueue;
        if (dispatcher == null) return false;

        // Queue the recreate to the next dispatcher tick so a calling Show() can
        // return cleanly before we close the old window. Reentrant teardown of the
        // current window from inside its Show() call stack is unsafe.
        var enqueued = dispatcher.TryEnqueue(() =>
        {
            // Clear queued the moment the lambda starts running — subsequent gate
            // evaluations during the body see recreateInProgress=true instead.
            _miniRecorderRecreateQueued = false;
            if (_isQuitting) return;
            try
            {
                _pendingMiniRecorderTarget = target;
                RecreateMiniRecorderWindowAfterDisplayChange(trigger);
            }
            finally
            {
                _pendingMiniRecorderTarget = null;
            }
        });

        if (!enqueued) return false;

        // Stamp queued ONLY after enqueue confirmed — a failed enqueue must not
        // consume the queued-state slot.
        _miniRecorderRecreateQueued = true;
        // Suspend controller renders NOW, before the (up-to-2.25 s) enqueue-to-start gap, so a VM
        // property change during the gap can't paint the pill on the wrong-DPI window that Show()
        // just flagged for recreate. The recreate body re-suspends idempotently and DeferControllerReattach
        // clears it on the new window.
        _pillController?.SuspendRendering();
        // Latency probe: tag an in-flight hotkey→pill measurement as
        // recreate-routed, only now that the recreate is committed. No-ops
        // when no measurement is armed (display-change-triggered recreates).
        MiniRecorderShowLatencyProbe.Instance.MarkRecreate();
        Log.Information(
            "MiniRecorder recreate ({Trigger}) accepted (target scale={Scale})", trigger, target.Scale);
        return true;
    }

    private void StartMiniRecorderDisplayChangeHandling()
    {
        if (_miniRecorderDisplayDebounceTimer != null)
            return;

        _miniRecorderDisplayDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _miniRecorderDisplayDebounceTimer.Tick += OnMiniRecorderDisplayDebounceTick;

        _miniRecorderDisplaySettingsChangedHandler = OnMiniRecorderDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _miniRecorderDisplaySettingsChangedHandler;

        // Second, reliable signal source (DSP-1): SystemEvents' hidden-window
        // infrastructure demonstrably never fired on this hardware during live
        // monitor unplug/replug (2026-07-06 UAT — zero receipts), so a WS-level
        // listener window on OUR pumping UI thread receives WM_DISPLAYCHANGE
        // directly. Fail-soft: null keeps the SystemEvents-only behavior.
        _displayChangeListener ??= DisplayChangeListener.TryCreate(
            () => ScheduleMiniRecorderDisplayRecreate("listener"));
    }

    private void StopMiniRecorderDisplayChangeHandling()
    {
        if (_miniRecorderDisplaySettingsChangedHandler != null)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _miniRecorderDisplaySettingsChangedHandler;
            _miniRecorderDisplaySettingsChangedHandler = null;
        }

        _displayChangeListener?.Dispose();
        _displayChangeListener = null;

        if (_miniRecorderDisplayDebounceTimer != null)
        {
            _miniRecorderDisplayDebounceTimer.Tick -= OnMiniRecorderDisplayDebounceTick;
            _miniRecorderDisplayDebounceTimer.Stop();
            _miniRecorderDisplayDebounceTimer = null;
        }

        _miniRecorderDisplayDirty = false;
    }

    private void OnMiniRecorderDisplaySettingsChanged(object? sender, EventArgs e)
        => ScheduleMiniRecorderDisplayRecreate("SystemEvents");

    /// <summary>
    /// Shared debounce entry for BOTH display-change signal sources (SystemEvents
    /// + the WM_DISPLAYCHANGE listener window). The 500 ms debounce restart
    /// coalesces double delivery when both sources fire for one topology change.
    /// </summary>
    private void ScheduleMiniRecorderDisplayRecreate(string source)
    {
        // Log BEFORE the dispatcher marshal so we can prove the event reached us
        // even if dispatcher work later drops or short-circuits — receipt-proof
        // per source (SystemEvents was observed never firing on this hardware).
        Log.Information("Display change received (source={Source})", source);
        RunOnMiniRecorderDispatcher(() =>
        {
            _miniRecorderDisplayDirty = true;

            if (_miniRecorderRecreateInProgress)
            {
                Log.Information("MiniRecorder display settings changed: recreate in progress, skipping debounce schedule");
                return;
            }

            _miniRecorderDisplayDebounceTimer?.Stop();
            _miniRecorderDisplayDebounceTimer?.Start();
            Log.Information("MiniRecorder display settings changed; recreation scheduled");
        });
    }

    private void OnMiniRecorderDisplayDebounceTick(object? sender, object e)
    {
        _miniRecorderDisplayDebounceTimer?.Stop();
        Log.Information("MiniRecorder display debounce tick fired (dirty={Dirty})", _miniRecorderDisplayDirty);

        if (!_miniRecorderDisplayDirty)
            return;

        RecreateMiniRecorderWindowAfterDisplayChange("display settings changed");
    }

    private void OnMiniRecorderViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.RecordingState)
            or nameof(MainViewModel.IsMiniRecorderVisible)
            or nameof(MainViewModel.CanSkipCurrentEnhancement)
            or nameof(MainViewModel.IsRedoAvailable)
            or nameof(MainViewModel.IsRetryAvailable)))
        {
            return;
        }

        RunOnMiniRecorderDispatcher(() =>
        {
            var vm = _miniRecorderViewModel;
            var c = _pillController;
            if (vm == null || c == null) return;

            switch (e.PropertyName)
            {
                case nameof(MainViewModel.RecordingState):
                    // The controller renders the pipeline pill from RecordingState. Idle = pipeline
                    // done → ClearPipeline lets a message / affordance / image-job win; any non-Idle
                    // state publishes the pipeline pill (the elapsed timer shows only while Recording).
                    if (vm.RecordingState == RecordingState.Idle)
                        c.ClearPipeline();
                    else
                        c.PublishPipeline(
                            vm.RecordingState,
                            vm.RecordingState == RecordingState.Recording ? vm.RecordingStartedAtUtc : null,
                            // Only the main pipeline installs the skip CTS (before flipping to
                            // Enhancing, so this reads true here); redo / image-options Enhancing is
                            // non-skippable, so its stop fully cancels. The pill does not SAY which
                            // it is (PILL-5) — this drives the controller's stop-class handle only.
                            canSkipEnhancement: vm.CanSkipCurrentEnhancement);
                    break;
                case nameof(MainViewModel.CanSkipCurrentEnhancement):
                    // Skip capability BEGAN or ENDED mid-Enhancing (the CTS was installed / cleared).
                    // Re-publish so the controller rotates the stop handle (Skip↔Cancel class) — a stop
                    // press captured under the old semantics is then rejected, and Stop no longer means
                    // "skip" once the CTS is gone (Codex r2 #2). Since PILL-5 the handle rotation is the
                    // ONLY consumer: nothing on the pill changes visibly at this transition.
                    if (vm.RecordingState == RecordingState.Enhancing)
                        c.PublishPipeline(vm.RecordingState, null, canSkipEnhancement: vm.CanSkipCurrentEnhancement);
                    break;
                case nameof(MainViewModel.IsMiniRecorderVisible):
                    // The VM hides the pill before pasting (focus + occlusion) and on cancel/abort. Map
                    // that to SuppressPipeline — hides the pipeline pill (its Stop stops being actionable
                    // through the uncancellable paste/history tail; Codex r2 #2 / finding 8) WITHOUT
                    // withdrawing the pipeline as the busy owner, so a latent background image job can't
                    // surface its own Stop mid-tail and cancel the wrong operation (Codex r3 #1). The real
                    // Idle transition (ClearPipeline) is what finally exposes the lower slots. A show
                    // (=true) needs no action — RecordingState / AffordanceArmed drive it.
                    if (!vm.IsMiniRecorderVisible)
                        c.SuppressPipeline();
                    break;
                case nameof(MainViewModel.IsRedoAvailable):
                    SyncAffordanceClear(c, vm, AffordanceKind.Redo, vm.IsRedoAvailable, ref _prevRedoAvailable);
                    break;
                case nameof(MainViewModel.IsRetryAvailable):
                    SyncAffordanceClear(c, vm, AffordanceKind.Retry, vm.IsRetryAvailable, ref _prevRetryAvailable);
                    break;
            }
        });
    }

    /// <summary>
    /// A genuine redo/retry ARM (ArmRedo/ArmRetry) — including a re-arm while already available, which
    /// no availability transition signals (Codex r2 #1). Publish the recency winner (ArmedAffordance)
    /// with its fresh content/tone; the controller mints a new handle + resets lifetime. The genuine-arm
    /// path (ArmAffordance) DOES supersede a stale message/download — that is correct here.
    /// </summary>
    private void OnAffordanceArmed()
        => RunOnMiniRecorderDispatcher(() =>
        {
            var vm = _miniRecorderViewModel;
            var c = _pillController;
            if (vm == null || c == null) return;
            switch (vm.ArmedAffordance)
            {
                case Models.Enums.ArmedAffordance.Redo:
                    c.ArmAffordance(AffordanceKind.Redo, vm.RedoStatusText ?? vm.StatusText, vm.RedoTone);
                    break;
                case Models.Enums.ArmedAffordance.Retry:
                    c.ArmAffordance(AffordanceKind.Retry, vm.RetryStatusText ?? vm.StatusText, vm.RetryTone);
                    break;
            }
            _prevRedoAvailable = vm.IsRedoAvailable;
            _prevRetryAvailable = vm.IsRetryAvailable;
        });

    /// <summary>
    /// Handle only the CLEAR direction of an affordance-availability change (true→false). Arming is
    /// handled by <see cref="OnAffordanceArmed"/>, so a false→true transition is a no-op here (its
    /// prev-flag is still updated). On clear, retire it — exposing the OTHER affordance as the fallback
    /// if it is still armed; the controller no-ops if it already swapped away (a stale clear can't
    /// resurrect a fallback). Fallback exposure goes through RetireAffordance, which — unlike a genuine
    /// arm — does NOT clear a live message/download (Codex r1 #3).
    /// </summary>
    private static void SyncAffordanceClear(
        MiniRecorderPresentationController c, MainViewModel vm,
        AffordanceKind kind, bool nowAvailable, ref bool prev)
    {
        var wasAvailable = prev;
        prev = nowAvailable;
        if (nowAvailable || !wasAvailable)
            return; // arm (false→true) is handled by OnAffordanceArmed; no transition → nothing
        (AffordanceKind Kind, string Text, MiniRecorderTone Tone)? fallback = null;
        if (kind == AffordanceKind.Redo && vm.IsRetryAvailable)
            fallback = (AffordanceKind.Retry, vm.RetryStatusText ?? vm.StatusText, vm.RetryTone);
        else if (kind == AffordanceKind.Retry && vm.IsRedoAvailable)
            fallback = (AffordanceKind.Redo, vm.RedoStatusText ?? vm.StatusText, vm.RedoTone);
        c.RetireAffordance(kind, fallback);
    }

    private void OnMiniRecorderErrorRequested(string message, MiniRecorderTone tone, PillMessageAction action)
        => RunOnMiniRecorderDispatcher(() => _pillController?.PublishMessage(message, tone, action));

    /// <summary>
    /// LNC-11: the once-per-session automatic navigation the recording gate requests on the first
    /// blocked press after the free trial ended — the same destination the refusal pill's click
    /// opens, so both go through <see cref="OpenLicensePageInMainWindow"/>.
    /// </summary>
    private void OnOpenLicensePageRequested()
        // The refusal pill is deliberately NOT dismissed on this path (the click path dismisses it):
        // the window jumps under the user's hands, and the pill is what says why.
        => OpenLicensePageInMainWindow("trial ended — first blocked press this session");

    /// <summary>
    /// Bring the main window to the front (restoring it from the tray or a minimized state — the
    /// user pressed the hotkey in ANOTHER app, so the window is usually behind or hidden) and land
    /// on the License page, whose Buy-primary panel carries the transaction. Marshalled to the
    /// main window's UI thread; a refused navigation (onboarding, or an unknown tag — impossible
    /// here) is logged, never thrown. <paramref name="afterNavigation"/> runs ONLY when the
    /// navigation landed — the click path hands in the pill's dismissal, which must not happen
    /// for a navigation the window refused.
    /// </summary>
    private void OpenLicensePageInMainWindow(string reason, Action? afterNavigation = null)
    {
        var window = _mainWindow;
        if (window == null || _isQuitting) return;
        var queued = window.DispatcherQueue.TryEnqueue(() =>
        {
            // Quit may have started between the request and this tick (the tray Quit drains an
            // in-flight transcription first): never restore a window the app is closing.
            if (_isQuitting) return;
            try
            {
                RestoreMainWindow();
                var landed = window.NavigateToPage("license");
                Log.Information("License page opened from the pill/gate ({Reason}): navigated={Landed}", reason, landed);
                if (landed)
                    afterNavigation?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Opening the License page ({Reason}) failed", reason);
            }
        });
        if (!queued)
            Log.Warning("Opening the License page ({Reason}) could not be queued — dispatcher shutting down", reason);
    }

    private void OnMiniRecorderDownloadProgressUpdated(double progress, string modelName)
        => RunOnMiniRecorderDispatcher(() => _pillController?.PublishDownload(progress, modelName));

    // ── Window (renderer) → controller/VM routing (handle-gated) ──────────────────
    // The window captures the rendered PillHandle at pointer-DOWN and carries it on the Tapped event,
    // so a press that spans a pill swap resolves against the ORIGINAL presentation (ABA-safe): the
    // controller/gate rejects the request unless the handle is still current.

    /// <summary>LNC-11 message-body click: the controller resolves the handle to the message's own
    /// action (None for a plain message, a stale handle or a hidden pill — the ABA guard every other
    /// pill press takes); an actionable one opens its page and the pill is dismissed ONLY once the
    /// navigation landed (Codex diff round 1): a click while a relaunched setup wizard owns the
    /// window is refused by <c>NavigateToPage</c>, and dismissing anyway would take away the one
    /// visible path to the License page with nothing opened.</summary>
    private void OnPillMessageActionRequested(PillHandle handle)
        => RunOnMiniRecorderDispatcher(() =>
        {
            var controller = _pillController;
            if (controller == null) return;
            switch (controller.MessageActionFor(handle))
            {
                case PillMessageAction.OpenLicensePage:
                    OpenLicensePageInMainWindow("pill clicked", afterNavigation: () => controller.Dismiss(handle));
                    break;
                default:
                    break; // a click on a plain message body: the drag handle's inert click
            }
        });

    /// <summary>Corner × tap: the controller dismisses only if the handle still matches the current
    /// dismissable presentation.</summary>
    private void OnPillDismissRequested(PillHandle handle)
        => RunOnMiniRecorderDispatcher(() => _pillController?.Dismiss(handle));

    /// <summary>Redo/retry action tap: route to the VM's recency-first redo-or-retry entry (which the
    /// pill's affordance mirrors) only if the handle still matches the current pill.</summary>
    private void OnPillActionRequested(PillHandle handle)
        => RunOnMiniRecorderDispatcher(() =>
        {
            var vm = _miniRecorderViewModel;
            if (vm == null || _pillController?.IsInteractiveCurrent(handle) != true) return;
            _ = vm.RedoOrRetryLastAsync();
        });

    /// <summary>Right-click hide: the controller withholds the pill while its owner (image job,
    /// enhancement, download) keeps running. Handle-gated, so a press spanning a pill swap can't hide
    /// the newer surface; refused with no tray icon (nothing to restore from) or during shutdown.</summary>
    private void OnPillHideRequested(PillHandle handle)
        => RunOnMiniRecorderDispatcher(() =>
        {
            if (!PillHidePolicy.ShouldAcceptHide(_trayIconReady, _isQuitting))
            {
                Log.Debug("MiniRecorder pill hide refused (tray ready: {TrayReady}, quitting: {Quitting})",
                    _trayIconReady, _isQuitting);
                return;
            }
            if (_pillController?.HideCurrent(handle) == true)
                Log.Information("MiniRecorder pill hidden by user (right-click)");
        });

    /// <summary>Stop-button tap: route by live pipeline / image-job state, only if the handle still
    /// matches the current pill — so a press spanning a swap can't act on the wrong surface (e.g. start
    /// a phantom recording right after an image-job cancel, the 2026-07-18 incident).</summary>
    private void OnPillStopRequested(PillHandle handle)
        => RunOnMiniRecorderDispatcher(() =>
        {
            var vm = _miniRecorderViewModel;
            var c = _pillController;
            if (vm == null || c == null || !c.IsInteractiveCurrent(handle)) return;
            // Route by the CURRENT pill's CONTENT (what the user saw + pressed), handle-validated — NOT
            // solely live RecordingState (Codex r3 #1). An image-job pill's Stop must cancel the IMAGE
            // job even while a text pipeline is concurrently mid-tail; a pipeline pill's Stop routes by
            // its own state (Toggle vs Cancel/Skip, the skip-vs-full refinement stays ClassifyStopTap).
            switch (c.Current?.Content)
            {
                case PillContent.ImageJob:
                    vm.CancelImageJob();
                    break;
                case PillContent.Download:
                    // The model-download pill shows only while Starting; its Stop cancels the in-progress
                    // start (which cancels the download) — the same action as a Starting pipeline stop.
                    // Without this branch the Download pill's Stop was inert (Codex r4 #1).
                    _ = vm.ToggleRecordAsync();
                    break;
                case PillContent.Pipeline p:
                    switch (MiniRecorderStopRouting.Decide(p.State, isImageJobRunning: false))
                    {
                        case MiniRecorderStopAction.CancelPipeline: vm.CancelPipeline(); break;
                        case MiniRecorderStopAction.ToggleRecording: _ = vm.ToggleRecordAsync(); break;
                        default: break;
                    }
                    break;
            }
        });

    /// <summary>IMG-BG: forward the background job's running state to the controller — it owns the
    /// GeneratingImage pill (precedence, the stop button, and since REL-22 the job-start stamp that
    /// supersedes a persistent message and masks a persistent affordance older than the job).
    /// Raised on the UI thread (the job runs UI-thread-affine); marshaled defensively regardless.</summary>
    private void OnImageJobServiceStateChanged()
        => RunOnMiniRecorderDispatcher(() =>
        {
            if (_imageJobService?.IsRunning == true)
                _pillController?.PublishImageJob();
            else
                _pillController?.ClearImageJob();
        });

    /// <summary>IMG-3: forward the batch progress ("Generating image 2 of 4…") to the live job
    /// pill. The controller no-ops when no job slot exists — slot creation stays with the
    /// job-state event, never a progress straggler.</summary>
    private void OnImageJobProgressChanged(string? progress)
        => RunOnMiniRecorderDispatcher(() => _pillController?.SetImageJobProgress(progress));

    /// <summary>IMG-BG: a job-scoped notice ("An image is already generating") renders INSIDE the
    /// GeneratingImage pill (stop button stays visible), reverting to the plain job pill when it
    /// expires — the controller owns that revert timer. If the job already ended, fall back to a plain
    /// warning message so the notice isn't lost.</summary>
    private void OnImageJobNoticeRequested(string notice)
        => RunOnMiniRecorderDispatcher(() =>
        {
            var c = _pillController;
            if (c == null) return;
            if (_imageJobService?.IsRunning == true)
                c.SetImageJobNotice(notice, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(MiniRecorderTimings.AttentionSeconds));
            else
                c.PublishMessage(notice, MiniRecorderTone.Warning);
        });

    /// <summary>AUD-1: the mic-fallback notice renders INSIDE the pipeline pill (stop button
    /// stays reachable; the controller owns the revert). If the pipeline already ended (a
    /// sub-second recording edge), fall back to a plain warning message so it isn't lost —
    /// same shape as the image-job notice forwarder above.</summary>
    private void OnPipelineNoticeRequested(string notice)
        => RunOnMiniRecorderDispatcher(() =>
        {
            var c = _pillController;
            if (c == null) return;
            if (!c.SetPipelineNotice(notice, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(MiniRecorderTimings.PipelineNoticeSeconds)))
                c.PublishMessage(notice, MiniRecorderTone.Warning);
        });

    private void RecreateMiniRecorderWindowAfterDisplayChange(string reason)
    {
        if (_isQuitting) return;

        var viewModel = _miniRecorderViewModel;
        var oldWindow = _miniRecorderWindow;
        if (viewModel == null || oldWindow == null)
            return;

        if (_miniRecorderRecreateInProgress)
        {
            _miniRecorderDisplayDirty = true;
            return;
        }

        _miniRecorderRecreateInProgress = true;
        _miniRecorderDisplayDirty = false;
        // Suspend controller renders for the whole swap — nothing must paint on the about-to-be-orphaned
        // old window (nor on the new one before its DPI settles). DeferControllerReattach re-attaches +
        // un-suspends onto the surviving window on the DPI-settled turn (success AND failure).
        _pillController?.SuspendRendering();

        MiniRecorderWindow? oldWindowToClose = null;
        MiniRecorderWindow? newWindow = null;
        var oldWindowDetached = false;
        var swappedWindow = false;
        try
        {
            var oldHwnd = WinRT.Interop.WindowNative.GetWindowHandle(oldWindow);
            newWindow = CreateMiniRecorderWindow(viewModel);

            DetachMiniRecorderWindow(oldWindow);
            oldWindowDetached = true;
            // Drop the controller's renderer reference to the old (soon-closed) window; the deferred
            // reattach re-points it to the new one. Renders are already suspended, so this delivers none.
            _pillController?.Detach();
            AttachMiniRecorderWindow(newWindow);
            _miniRecorderWindow = newWindow;
            swappedWindow = true;
            oldWindowToClose = oldWindow;

            var newHwnd = WinRT.Interop.WindowNative.GetWindowHandle(newWindow);
            Log.Information(
                "MiniRecorder recreated after {Reason}: oldHwnd={OldHwnd} newHwnd={NewHwnd} state={State} redo={Redo}",
                reason,
                oldHwnd.ToInt64(),
                newHwnd.ToInt64(),
                viewModel.RecordingState,
                viewModel.IsRedoAvailable);

            // WarmUp BEFORE the deferred reattach. Triggers compositor first paint at the parked
            // position above the target monitor's work area (cross-DPI) or at -10000,-10000 (normal),
            // giving WinUI a chance to start committing XamlRoot at target DPI.
            newWindow.WarmUp();
        }
        catch (Exception ex)
        {
            if (!swappedWindow)
            {
                try { newWindow?.CloseForReplacement(); }
                catch (Exception closeEx)
                {
                    Log.Debug(closeEx, "Failed to close unused MiniRecorder replacement window");
                }
            }

            if (!swappedWindow && _miniRecorderWindow == oldWindow && oldWindowDetached)
                AttachMiniRecorderWindow(oldWindow);

            Log.Warning(ex, "Failed to recreate MiniRecorder after {Reason}", reason);
        }
        finally
        {
            oldWindowToClose?.CloseForReplacement();
            _miniRecorderRecreateInProgress = false;
            // Arm the gate's cooldown after EVERY attempt — success AND failure (Codex r2 #3). A failed
            // construction leaves the old window, whose reattach Show() re-detects the cross-DPI mismatch;
            // without a completion stamp the gate would accept an immediate re-recreate and loop. The
            // cooldown throttles retries to the gate's window instead of spinning.
            _lastRecreateCompletionUtc = DateTime.UtcNow;

            // Re-attach the controller to the SURVIVING window (new on success, old on failure) on the
            // next dispatcher tick — critical for cross-DPI: the WM_DPICHANGED queued by the ctor's
            // MoveAndResize is pumped by the dispatcher, not synchronously inside SetWindowPos, so the
            // new window's XamlRoot reaches target DPI before Attach's render runs. Attach clears the
            // render-suspension set above.
            if (_miniRecorderViewModel != null)
                DeferControllerReattach();

            if (_miniRecorderDisplayDirty)
            {
                _miniRecorderDisplayDebounceTimer?.Stop();
                _miniRecorderDisplayDebounceTimer?.Start();
            }
        }
    }

    private void ShutdownMiniRecorder()
    {
        StopMiniRecorderDisplayChangeHandling();

        if (_miniRecorderViewModel != null)
        {
            _miniRecorderViewModel.PropertyChanged -= OnMiniRecorderViewModelPropertyChanged;
            _miniRecorderViewModel.ShowMiniRecorderError -= OnMiniRecorderErrorRequested;
            _miniRecorderViewModel.DownloadProgressUpdated -= OnMiniRecorderDownloadProgressUpdated;
            _miniRecorderViewModel.AffordanceArmed -= OnAffordanceArmed;
            _miniRecorderViewModel.OpenLicensePageRequested -= OnOpenLicensePageRequested;
        }

        if (_miniRecorderWindow != null)
        {
            DetachMiniRecorderWindow(_miniRecorderWindow);
            // Shutdown path: app is exiting, no later dispatcher ticks are
            // guaranteed. Use synchronous close (deferClose:false) — the
            // close-during-active-work race only manifests when the SAME window
            // had a recent Content swap + composition work in flight from a
            // still-active Show()/ShowError. By the time ShutdownMiniRecorder
            // runs, the recording loop has already been torn down and no
            // concurrent in-flight Show is possible.
            _miniRecorderWindow.CloseForReplacement(deferClose: false);
            _miniRecorderWindow = null;
        }

        _pillController?.Dispose();
        _pillController = null;
        _miniRecorderViewModel = null;
        _pillDismissHandler = null;
        _pillActionHandler = null;
        _pillStopHandler = null;
        _prevRedoAvailable = false;
        _prevRetryAvailable = false;
        _miniRecorderRecreateInProgress = false;
        _miniRecorderRecreateQueued = false;
    }

    private void RunOnMiniRecorderDispatcher(Action action)
    {
        var queue = _mainWindow?.DispatcherQueue;
        if (queue == null)
            return;

        if (queue.HasThreadAccess)
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Warning(ex, "MiniRecorder dispatcher action failed");
            }
            return;
        }

        queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Warning(ex, "MiniRecorder dispatcher action failed");
            }
        });
    }

    /// <summary>
    /// Wait until the main window's content tree is loaded AND the first
    /// post-activation DispatcherQueue cycle has run. ContentDialog needs both:
    /// XamlRoot must be non-null (Loaded guarantees this) AND the host window's
    /// activation messages must have flushed (DispatcherQueue yield ensures this),
    /// otherwise the dialog can render behind the main window on first launch
    /// until the user toggles window visibility.
    /// </summary>
    // Bound for either wait below (F22): Loaded/first-paint should land within a frame or
    // two — if the EVENT never fires (content torn down mid-startup, activation aborted), an
    // unbounded await here hung the ENTIRE gated startup (legal gate → license route → tray →
    // hotkey). On timeout we return normally: the callers' existing XamlRoot-null checks then
    // fail closed on their own. SCOPE (Codex R1): the timeout continuations resume through
    // the same UI context, so this bounds only the missing-event case while the dispatcher
    // keeps pumping — a fully wedged dispatcher can't run the fail-closed checks either, and
    // guarding THAT would take an out-of-band watchdog, deliberately out of scope here.
    private static readonly TimeSpan XamlRootWaitTimeout = TimeSpan.FromSeconds(10);

    private async Task WaitForXamlRootAsync()
    {
        if (_mainWindow?.Content is not FrameworkElement el) return;

        if (!el.IsLoaded)
        {
            var loadedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                el.Loaded -= handler;
                loadedTcs.TrySetResult(true);
            };
            el.Loaded += handler;
            if (await Task.WhenAny(loadedTcs.Task, Task.Delay(XamlRootWaitTimeout)) != loadedTcs.Task)
            {
                el.Loaded -= handler;
                Log.Error("WaitForXamlRootAsync timed out waiting for Loaded — startup gate proceeding to its fail-closed checks");
                return;
            }
        }

        // Yield via DispatcherQueue so the post-Loaded message-pump cycle
        // (activation completion + first paint) finishes before the modal opens.
        var yieldTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mainWindow.DispatcherQueue.TryEnqueue(() => yieldTcs.TrySetResult(true)))
            return;
        if (await Task.WhenAny(yieldTcs.Task, Task.Delay(XamlRootWaitTimeout)) != yieldTcs.Task)
            Log.Error("WaitForXamlRootAsync timed out waiting for the dispatcher yield — startup gate proceeding to its fail-closed checks");
    }

    /// <summary>
    /// REL-4: after an update, show the "What's new" dialog once. Called from
    /// <see cref="StartGatedRuntimeServices"/> so it runs behind the LGL-1 legal/onboarding gate.
    /// Seeds the last-seen marker on a fresh install (no dialog) and suppresses the auto-show when
    /// starting minimized (the Settings → About "What's new" row remains available). Fire-and-forget
    /// and fully guarded so it can never crash startup.
    /// </summary>
    private async Task MaybeShowWhatsNewAsync()
    {
        try
        {
            if (_isQuitting) return;

            var settings = Services.GetRequiredService<global::VoiceWink.Services.System.SettingsService>();
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            var lastSeen = settings.GetString(AppDefaults.LastSeenChangelogVersion, "");

            // Fresh install: seed so the dialog only ever appears AFTER a future update.
            if (string.IsNullOrWhiteSpace(lastSeen))
            {
                settings.SetString(AppDefaults.LastSeenChangelogVersion, current);
                return;
            }

            if (!global::VoiceWink.Services.Support.ChangelogVersion.IsNewer(current, lastSeen)) return;

            // Don't pop a modal over a hidden / tray start — the user sees it on the next
            // visible launch (deliberate deferral: the marker below is NOT advanced) or from
            // the About page. Guards on the launch's EFFECTIVE intent (UPD-3c), not the raw
            // StartMinimized setting — a post-update visible restart passes and gets the
            // dialog as its confirmation.
            if (_startMinimizedIntentThisLaunch) return;

            var parser = Services.GetRequiredService<global::VoiceWink.Services.Support.ChangelogParser>();
            // treatUnreleasedAs: an un-rolled [Unreleased] block in the bundled changelog IS
            // this build's changes — without the mapping the post-update dialog rendered
            // nothing (every 1.30.30x release shipped with everything under Unreleased).
            var entries = parser.EntriesNewerThan(lastSeen, treatUnreleasedAs: current);
            if (entries.Count == 0)
            {
                // Nothing to render (unbundled changelog / no matching entries) — still advance the
                // marker so we don't re-evaluate every launch.
                settings.SetString(AppDefaults.LastSeenChangelogVersion, current);
                return;
            }

            await WaitForXamlRootAsync();
            if (_isQuitting) return;
            if (_mainWindow?.Content is not FrameworkElement root || root.XamlRoot is null) return;

            var dialog = new Views.Dialogs.WhatsNewDialog(entries, $"VoiceWink was updated to v{current}.")
            {
                XamlRoot = root.XamlRoot,
            };
            // Advance the marker regardless of how the dialog is dismissed — the user has seen it.
            settings.SetString(AppDefaults.LastSeenChangelogVersion, current);
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "What's-new dialog failed (non-fatal)");
        }
    }

    /// <summary>
    /// LGL-1 gated runtime services — the call sites that capture user input
    /// or persist user data. Idempotent: a second invocation (e.g. from
    /// OnboardingCompleted firing on a wizard restart) is a no-op via the
    /// <see cref="_gatedRuntimeStarted"/> Interlocked flag.
    /// </summary>
    private void StartGatedRuntimeServices()
    {
        if (Interlocked.Exchange(ref _gatedRuntimeStarted, 1) != 0) return;
        if (_mainWindow == null) return;

        var viewModel = Services.GetRequiredService<MainViewModel>();

        // Set up system tray (Record menu calls viewModel.ToggleRecordAsync — gated).
        SetupTrayIcon(viewModel);

        // AUD-6: resolved once here (the _hotkeyService seam) — the warm-up, suspend/resume,
        // unlock, and cleanup sites all use the field, per the no-new-service-locator rule.
        _standingCaptureService = Services.GetRequiredService<VoiceWink.Services.Audio.StandingCaptureService>();
        _pcppBackend = Services.GetService<Services.Transcription.IParakeetPcppBackend>();

        // TRN-49: kick the Parakeet GPU warm-up — HERE, behind the LGL-1 gate, because it spawns
        // a ~1 GB child and reads 900 MB of model (configuration happened in the ctor's
        // ConfigureGpuWarmup). Called SYNCHRONOUSLY, on purpose: the queue itself is a config
        // read, a small once-per-version marker-file read (fail-soft, on this thread) and the
        // latch, and it backgrounds the model read and the spawn on its own Task.Run — so by the
        // time the startup preload below reaches the TRN-57 quiesce the warm-up is either QUEUED
        // or provably never queuable this session (no plan, already warmed, cancelled), never
        // "not yet". Posting the queue through a Task.Run of its own made that order thread-pool
        // FIFO rather than a fact: on a starved pool the preload could find nothing queued, spawn
        // the resident, and then run the warm child beside it (Kimi, PR #730).
        GpuWarmup.Instance.QueueParakeetWarmup();

        // Wire up global hotkey
        _hotkeyService = Services.GetRequiredService<HotkeyService>();
        _hotkeyService.ToggleRecordingRequested += async () =>
        {
            try
            {
                if (viewModel.CanProcessHotkeyAction)
                    await viewModel.ToggleRecordAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling hotkey toggle");
            }
        };
        _hotkeyService.PasteLastRequested += async () =>
        {
            try
            {
                await viewModel.PasteLastTranscriptionAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling paste-last hotkey");
            }
        };
        _hotkeyService.RedoLastRequested += async () =>
        {
            try
            {
                // Recency-routed (REL-12): retry when a failed transcription's retry
                // armed last, redo picker otherwise. Async so a retry pipeline failure
                // after the first await still lands in this catch, mirroring the
                // paste-last handler above.
                await viewModel.RedoOrRetryLastAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling redo-last hotkey");
            }
        };
        _hotkeyService.GenerateImageRequested += () =>
        {
            try
            {
                // IMG-1: text-first image generation — opens the new-image dialog.
                // All picker guards (pipeline-busy, one-dialog reentrancy) apply inside.
                viewModel.RequestNewImageGeneration();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling generate-image hotkey");
            }
        };
        _hotkeyService.PromptHotkeyPressed += promptId =>
        {
            viewModel.SetPromptOverride(promptId);
        };
        _hotkeyService.CancelPromptOverride += () =>
        {
            viewModel.ClearPromptOverride();
        };
        viewModel.ResetHotkeyState += () => _hotkeyService.ResetState();

        // Start the periodic retention timer immediately, but seed lifetime metrics
        // before the first cleanup run so upgraded users keep pre-existing totals.
        StartTranscriptionCleanupTimer();
        _ = InitializeLifetimeMetricsAndRunStartupCleanupAsync();

        // Keep Home's "Last Transcription" in sync if the history-backed record is removed.
        // Marshaled: HistoryChanged fires on a threadpool thread (SaveAsync resumes on
        // ConfigureAwait(false) before raising) and the sync mutates observable properties —
        // a pre-existing race whose window IMG-BG's overlapping flows widen (Codex round 2).
        var historyService = Services.GetRequiredService<TranscriptionHistoryService>();
        historyService.HistoryChanged += () => _mainWindow?.DispatcherQueue.TryEnqueue(
            () => _ = viewModel.SyncLastTranscriptionWithHistoryAsync());

        // A user-initiated BULK delete committed: retire an armed redo whose rows just
        // vanished and hand back the reference copies it was pinning, so the service
        // deletes them in the SAME pass ("Delete all" used to leave the most recent
        // run's reference images behind — owner, 2026-07-29). Raised on a POOL thread
        // (same ConfigureAwait(false) reason as HistoryChanged above) while the redo
        // state is UI-thread-only, so hop across and AWAIT the result: the service is
        // waiting on these paths.
        historyService.BulkDeleteCommitted += scope =>
        {
            // The wipe stamp advances HERE, on the raising thread, inside the delete's
            // awaited settlement (diff r9) — guaranteed once the delete commits, so a
            // UI freeze that expires the bounded retirement callback below can never
            // leave the stamp behind (a later completion would have passed the apply
            // gate and re-armed deleted rows' claims). Thread-safe by design.
            if (MainViewModel.BulkDeleteScopeMatchesArmedRedo(scope, armedWasImageGeneration: true))
                viewModel.NoteImageHistoryWipeCommitted();

            var done = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Bounded handshake with a CAS expiry gate (diff r5+r7): TryEnqueue can
            // ACCEPT a callback the dispatcher then parks past the deadline (shutdown,
            // stall) — an unbounded await would hang the committed deletion, and a
            // parked callback RUNNING LATE would retire a NEWER redo after the delete
            // already returned. Exactly one side wins state 0: 1 = the callback (may
            // mutate the VM), 2 = expiry (completes empty; a late callback sees 2 and
            // touches NOTHING; UI-state retirement is best-effort under a frozen
            // dispatcher — recorded residual, the stamp above carries the invariant).
            var state = new int[1];
            var queue = _mainWindow?.DispatcherQueue;
            if (queue == null || !queue.TryEnqueue(() =>
                {
                    if (global::System.Threading.Interlocked.CompareExchange(ref state[0], 1, 0) != 0)
                        return; // expired while parked — stale retirement must not run
                    try { done.TrySetResult(viewModel.RetireRedoForBulkHistoryDelete(scope)); }
                    catch (Exception ex)
                    {
                        Log.Warning("Redo retirement after bulk delete failed: {ErrorType} (HResult=0x{HResult:X8})",
                            ex.GetType().Name, ex.HResult);
                        done.TrySetResult(Array.Empty<string>());
                    }
                }))
            {
                // No dispatcher (shutting down / window gone) ⇒ complete empty rather
                // than hang the deletion waiting for a UI turn that will never run.
                state[0] = 2;
                done.TrySetResult(Array.Empty<string>());
                return done.Task;
            }
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                if (global::System.Threading.Interlocked.CompareExchange(ref state[0], 2, 0) == 0)
                {
                    Log.Warning("Redo retirement after bulk delete timed out — proceeding without retired paths");
                    done.TrySetResult(Array.Empty<string>());
                }
            }, TaskScheduler.Default);
            return done.Task;
        };

        // IMG-BG: mirror the background image job into the pill state + route job-scoped
        // notices; both raised on the UI thread, marshaled defensively inside the handlers.
        _imageJobService = Services.GetRequiredService<Services.AIEnhancement.ImageGenerationJobService>();
        _imageJobService.StateChanged += OnImageJobServiceStateChanged;
        viewModel.ImageJobNoticeRequested += OnImageJobNoticeRequested;
        viewModel.ImageJobProgressChanged += OnImageJobProgressChanged;
        viewModel.PipelineNoticeRequested += OnPipelineNoticeRequested;

        // Register prompt hotkeys
        RegisterPromptHotkeys();

        // Pre-load the selected Whisper model so the first recording starts instantly.
        // Fire-and-forget: failures are logged inside PreloadModelAsync.
        _ = viewModel.PreloadModelAsync();

        // Start debug heartbeat (only runs when verbose logging is enabled in Settings)
        Services.GetRequiredService<DebugHeartbeat>().Start(_mainWindow.DispatcherQueue);

        _ = _hotkeyService.StartAsync(_mainWindow.DispatcherQueue).ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Error(t.Exception?.InnerException, "Failed to start global hotkey hook");
        }, TaskScheduler.Default);

        // UPD-1b: start the automatic update poll here — behind the LGL-1 legal/onboarding gate,
        // alongside the other gated runtime services — so a background network check never runs
        // ahead of consent. Inert in default builds (UpdateCheckFeature off).
        //
        // UPD-4b: the wiring itself (which events, how they reach the UI thread, teardown order)
        // moved into Services/Updates/UpdateRuntimeCoordinator.cs. It used to be inline here and
        // this change would have added a third handler, a third resolved service and another
        // cleanup step to a hub AGENTS.md says must not grow. What stays is only what genuinely
        // belongs to the window: two callbacks, the quitting probe, and the UI-thread post.
        _updateRuntime = Services.GetRequiredService<Services.Updates.UpdateRuntimeCoordinator>();
        _updateRuntime.Start(
            isQuitting: () => _isQuitting,
            postToUi: action => _mainWindow?.DispatcherQueue.TryEnqueue(() => action()) ?? false,
            onUpdateAvailableDetected: version => _mainWindow?.OnUpdateAvailableDetected(version),
            onPendingVersionChanged: pending => _mainWindow?.OnPendingUpdateVersionChanged(pending));

        // LIC-23: arm the daily in-session forced licence check here, behind the same LGL-1 gate —
        // its first tick is a full day after this point (on an onboarded start the launch reconcile
        // has just run; on a first run the wizard has just activated a key or started the trial),
        // and a tick can never precede consent or setup completion, which is what keeps the privacy
        // policy's "after setup is complete … and about once a day while VoiceWink keeps running"
        // true.
        _licenseRevalidation = Services.GetRequiredService<LicenseRevalidationScheduler>();
        _licenseRevalidation.Start();

        // REL-4: show the "What's new" dialog after an update (fire-and-forget; self-guarded).
        _ = MaybeShowWhatsNewAsync();

        // Defer COM-heavy initialization to avoid blocking the UI thread at startup.
        // Both MMDeviceEnumerator pre-warm and SystemEvents subscription involve COM
        // apartment initialization that can stall for 30-50s after a reboot.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000); // Let the UI fully render first
            if (_isQuitting) return;

            // Subscribe to sleep/wake events BEFORE the audio warm-up below. If the
            // warm-up subsequently hangs (wedged WASAPI / driver bug), these
            // registrations are already in place so sleep/wake recovery still works.
            // SystemEvents creates a hidden COM window on first access — deferring
            // (the 3s delay above) prevents contention with WinUI's startup.
            //
            // We subscribe to BOTH PowerModeChanged AND SessionSwitch because on some
            // hardware (notably Snapdragon/ARM64 Windows 11 with modern standby S0)
            // PowerModeChanged.Resume does not fire reliably when the laptop wakes —
            // the system never enters classic S3 suspend. SessionSwitch.SessionUnlock
            // fires on the lock-screen wake path and is the primary recovery signal
            // there. The watchdog in HotkeyService is the third line of defense.
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
                Log.Information("Sleep/wake event subscriptions registered");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to subscribe to sleep/wake events; warm-up will still run");
            }

            if (_isQuitting) return;

            // Fire-and-forget: bounded with 5s wait + in-flight guard so a wedged
            // audio service can't lock out future warm-ups (notably, resume events).
            RunStartupWarmUpFireAndForget();
        });
    }

    /// <summary>
    /// Ensure database is created with current schema. EnsureCreated() is a no-op if the
    /// file exists, so we verify all required tables are present. Only recreates the
    /// database on schema errors (e.g., missing tables), not on transient I/O failures.
    /// </summary>
    private void InitializeDatabase()
    {
        try
        {
            var dbFactory = Services.GetRequiredService<IDbContextFactory<VoiceWinkDbContext>>();
            var created = InitializeDatabaseCore(dbFactory, Helpers.AppPaths.DatabaseFile);
            // Published only AFTER the init succeeded: a throw above leaves the factory null,
            // which is what makes the seed's "a failed init skips the seed" true (Gemini diff r1).
            _dbFactoryForSeed = dbFactory;
            _databaseCreatedThisLaunch = created;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Database initialization failed");
        }
    }

    /// <returns>True when the database was CREATED by this call — a fresh file, or the
    /// schema-repair recreate below — so the caller knows its tables started empty this launch
    /// (DCT-1: the Dictionary defaults are offered again to a fresh database).</returns>
    internal static bool InitializeDatabaseCore(
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        string? databasePath)
    {
        using var db = dbFactory.CreateDbContext();
        var created = db.Database.EnsureCreated();

        try
        {
            // Safe column additions BEFORE schema validation, so an old DB with only
            // missing additive columns is upgraded in place instead of being recreated.
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN EnhancementModelName TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ImageFilePath TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ImageQuality TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ImageAspect TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ImageSizeTier TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ReferenceImagePath TEXT");
            AddColumnIfMissing(db, "ALTER TABLE TranscriptionRecords ADD COLUMN ImageVersionCount INTEGER");

            _ = db.WordReplacements.Any();
            _ = db.TranscriptionRecords.Any();
            _ = db.VocabularyWords.Any();
            return created;
        }
        catch (SqliteException ex) when (IsSchemaMismatch(ex))
        {
            Log.Warning(ex, "Database schema is incompatible — backing up before recreating");
            if (!TryBackupDatabase(databasePath, out var backupPath))
            {
                Log.Error("Database schema repair skipped because backup failed for {Path}", databasePath);
                return created;
            }

            db.Database.EnsureDeleted();
            db.Database.EnsureCreated();
            Log.Warning("Database recreated after schema mismatch; previous database backed up to {BackupPath}", backupPath);
            return true;
        }
    }

    private static void AddColumnIfMissing(VoiceWinkDbContext db, string sql)
    {
        try
        {
            db.Database.ExecuteSqlRaw(sql);
        }
        catch (SqliteException ex) when (IsDuplicateColumn(ex))
        {
            // Already migrated.
        }
    }

    private static bool IsDuplicateColumn(SqliteException ex) =>
        ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);

    private static bool IsSchemaMismatch(SqliteException ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

    private static bool TryBackupDatabase(string? databasePath, out string? backupPath)
    {
        backupPath = null;
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return true;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            backupPath = $"{databasePath}.schema-backup-{timestamp}";
            File.Copy(databasePath, backupPath, overwrite: false);

            var walPath = databasePath + "-wal";
            if (File.Exists(walPath))
                File.Copy(walPath, backupPath + "-wal", overwrite: false);

            var shmPath = databasePath + "-shm";
            if (File.Exists(shmPath))
                File.Copy(shmPath, backupPath + "-shm", overwrite: false);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to back up database before schema repair");
            backupPath = null;
            return false;
        }
    }

    /// <summary>
    /// Wires minimize handling for the main window: when it's minimized AND the "Minimize to Tray"
    /// preference is on (and the tray icon is ready), hide it from the taskbar so the user restores
    /// it from the system tray. Otherwise minimize stays a normal taskbar minimize.
    /// </summary>
    private void SetupMinimizeToTray()
    {
        if (_mainWindow == null) return;

        var presenter = _mainWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter == null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        // Resolve the settings singleton once; the enabled flag is read LIVE per minimize below so
        // the "Minimize to Tray" toggle takes effect without a restart (in-memory cache read — no IO).
        var settings = Services.GetRequiredService<SettingsService>();

        // Event-driven minimize detection — no polling timer, no COM calls on tick.
        // AppWindow.Changed fires on presenter state changes (minimize/restore/maximize).
        _mainWindow.AppWindow.Changed += (_, args) =>
        {
            if (!args.DidPresenterChange) return;
            try
            {
                if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                {
                    // Explicit flow: confirmed Minimized → read the live "Minimize to Tray"
                    // preference → require BOTH the user's opt-in AND a ready tray icon. The
                    // _trayIconReady operand is MANDATORY (LGL-1 / Codex round-2): during
                    // onboarding or a stale-acceptance modal the tray icon isn't registered yet,
                    // so hiding would strand the window with no way to restore it — leave it
                    // minimized to the taskbar. When the preference is OFF, minimize also stays a
                    // normal taskbar minimize (owner request 2026-07-22).
                    var minimizeToTray = settings.GetBool(AppDefaults.MinimizeToTray, true);
                    if (!Helpers.WindowMinimizePolicy.ShouldHideToTray(_trayIconReady, minimizeToTray))
                        return;

                    Helpers.NativeInterop.ShowWindow(hwnd, Helpers.NativeInterop.SW_HIDE);
                    Log.Debug("Main window minimized to tray");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Minimize-to-tray handler failed");
            }
        };
    }

    /// <summary>
    /// Touch the three cold paths that dominate first-use latency after a fresh start
    /// or system resume: the MMDevice COM class factory, the default-capture-endpoint
    /// fetch that the recording hot path actually calls, and the MiniRecorder's WinUI
    /// compositor. Runs on a background thread for COM work; marshals the MiniRecorder
    /// warm to the UI thread. Best-effort — individual failures are logged and ignored.
    ///
    /// <para><b>Bounded under contention.</b> The whole core is awaited with a 5-second
    /// overall budget. Under heavy system CPU contention the underlying COM calls have
    /// been observed to take minutes (logged 585s startup-warm-up on a ComfyUI-installer
    /// + VM + Excel scenario). If the budget expires we abandon the wait but let the
    /// core task continue running in the background — the kernel-side audio service
    /// caching it primes is still valuable even if we no longer block on it.</para>
    ///
    /// <para>The in-flight guard (<see cref="_warmUpInFlight"/>) prevents a resume
    /// event from kicking off a second warm-up while a previous one is still
    /// grinding in the background — the guard clears via a continuation on the
    /// CORE task, not on the wait wrapper, so it stays set as long as the core
    /// is actually running.</para>
    /// </summary>
    private const int WarmUpBudgetSeconds = 5;
    private int _warmUpInFlight; // 0 = idle, 1 = core task running

    private void RunStartupWarmUpFireAndForget()
    {
        if (Interlocked.CompareExchange(ref _warmUpInFlight, 1, 0) != 0)
        {
            Log.Information("Startup warm-up skipped: previous warm-up still in flight");
            return;
        }

        var totalSw = global::System.Diagnostics.Stopwatch.StartNew();
        var coreTask = Task.Run(RunStartupWarmUpCoreAsync);

        // Clear the in-flight guard only when the CORE task actually finishes,
        // no matter how long that takes. This is what prevents a resume event
        // from doubling up while an abandoned-wait warm-up is still running.
        _ = coreTask.ContinueWith(t =>
        {
            Interlocked.Exchange(ref _warmUpInFlight, 0);
            if (t.IsFaulted)
            {
                Log.Information("Startup warm-up background task faulted: {ExType}",
                    t.Exception?.InnerException?.GetType().Name ?? "Unknown");
            }
        }, TaskScheduler.Default);

        // Bounded wait. After the budget expires we return; the core may still
        // be running.
        _ = Task.Run(async () =>
        {
            try
            {
                await coreTask.WaitAsync(TimeSpan.FromSeconds(WarmUpBudgetSeconds))
                    .ConfigureAwait(false);
                Log.Information("Startup warm-up completed in {Elapsed}ms", totalSw.ElapsedMilliseconds);
            }
            catch (TimeoutException)
            {
                Log.Warning(
                    "Startup warm-up timed out at {Budget}s after {Elapsed}ms — core task "
                    + "continues in background. First recording may pay an additional cold-"
                    + "start cost; the in-flight guard clears when the core finally completes.",
                    WarmUpBudgetSeconds, totalSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Information("Startup warm-up wait threw: {ExType}", ex.GetType().Name);
            }
        });
    }

    private async Task RunStartupWarmUpCoreAsync()
    {
        // Generic MMDeviceEnumerator creation — primes the COM class-factory cache for
        // the MTA apartment so later enumerator creations skip the 30-50s cold path.
        try { using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator(); }
        catch (Exception ex) { Log.Debug(ex, "Warm-up: MMDeviceEnumerator creation failed"); }

        if (_isQuitting) return;

        // AUD-6: when the standing warm capture may run, ITS start IS the audio warm-up — and
        // unlike the throwaway below it STAYS warm, which is the whole feature. EnsureHealthyAsync
        // is the wake-shared entry: healthy-running ⇒ no-op (a resume whose stream survived),
        // stale-running ⇒ rebuild, stopped ⇒ start behind the gate. Only when the standing
        // capture did NOT come up (setting off, BT endpoint, no license, start failure) does the
        // legacy throwaway warm-up run — never both.
        var standingUp = false;
        try
        {
            var standing = _standingCaptureService;
            if (standing != null)
            {
                await standing.EnsureHealthyAsync("warmup").ConfigureAwait(false);
                standingUp = standing.IsRunning;
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Warm-up: standing capture start failed"); }

        if (_isQuitting) return;

        if (!standingUp)
        {
            // Prime the recording device — the AUD-1 selection when pinned, else the system
            // default (what WasapiCapture's setup path resolves). The selection service owns
            // the rules + bounds; warm-up stays orchestration-only (launch-freeze hub rule).
            NAudio.CoreAudioApi.MMDevice? warmUpDevice = null;
            try
            {
                var deviceSelection = Services.GetRequiredService<Services.Audio.RecordingDeviceSelectionService>();
                warmUpDevice = await deviceSelection.ResolveForWarmUpAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Debug(ex, "Warm-up: device resolution failed"); }

            if (_isQuitting) { warmUpDevice?.Dispose(); return; }

            // Pre-warm the WASAPI capture session — opens, starts, and stops a throwaway
            // capture so the first user-initiated recording skips the ~300-500ms cold
            // start. Trade-off: mic-in-use indicator flickers briefly at app launch
            // (and on resume — this method runs from OnPowerModeChanged too).
            try
            {
                await VoiceWink.Services.Audio.AudioRecorderService.WarmUpAsync(warmUpDevice).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Debug(ex, "Warm-up: AudioRecorderService warm-up failed"); }
            finally
            {
                try { warmUpDevice?.Dispose(); }
                catch (Exception ex) { Log.Debug(ex, "Warm-up: warmUpDevice dispose failed"); }
            }
        }

        if (_isQuitting) return;

        // MiniRecorder compositor warm — must run on the UI thread. Does an off-screen
        // SWP_SHOWWINDOW so WinUI 3's first-paint flash happens where the user can't see
        // it; later real Show() calls then reuse the composited surface.
        var mainWindow = _mainWindow;
        if (mainWindow != null && _miniRecorderWindow != null)
        {
            // RunContinuationsAsynchronously so the await below resumes on the thread pool
            // instead of inlining onto the UI thread (which would make the final log line
            // and any post-warmup code run on the UI thread).
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueued = mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_isQuitting) return;

                    var currentRecorder = _miniRecorderWindow;
                    currentRecorder?.WarmUp();
                }
                catch (Exception ex) { Log.Debug(ex, "Warm-up: MiniRecorder WarmUp failed"); }
                finally { tcs.TrySetResult(); }
            });
            // If the dispatcher is shutting down TryEnqueue returns false and the lambda
            // never runs — complete the TCS ourselves so we don't await forever.
            if (!enqueued) tcs.TrySetResult();
            await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handle system sleep/wake events. After sleep, COM objects behind WinUI presenters
    /// can stall, and Windows may silently remove low-level keyboard hooks. On resume,
    /// pause timers briefly to let the OS stabilize, then restart the hotkey hook.
    /// </summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            Log.Information("System suspending — pausing timers");
            // AUD-6: release the standing capture's device before sleep. Latched until claim
            // detach if a recording is mid-drain (B5) — never yanks a live recording's source.
            try { _standingCaptureService?.NotifySuspend(); }
            catch (Exception ex) { Log.Debug(ex, "Standing capture suspend notify failed"); }
            _mainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                _transcriptionCleanupTimer?.Stop();
            });
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            Log.Information("System resumed — restarting timers and hotkey hook");
            _mainWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // Brief delay to let the OS stabilize post-wake
                    await Task.Delay(2000);

                    _transcriptionCleanupTimer?.Start();

                    await TryRestartHotkeyHookAsync("system resume");

                    // Re-run warm-up — post-wake AV/disk/CPU contention reintroduces
                    // the same cold-path penalty the startup warm-up addresses (logged
                    // 43s first-hotkey latency on one resume). The in-flight guard
                    // inside the fire-and-forget entry skips this if a previous warm-up
                    // is still grinding away (e.g. wedged audio service).
                    RunStartupWarmUpFireAndForget();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to restore state after resume");
                }
            });
        }
    }

    /// <summary>
    /// Handle session-lock/unlock events. On modern-standby hardware (Snapdragon/ARM64
    /// Windows 11) <see cref="OnPowerModeChanged"/> often does not fire on wake — the
    /// system never enters classic S3 suspend — but the lock screen always engages on
    /// modern-standby entry, so SessionUnlock is the reliable wake signal there.
    /// We also cover ConsoleConnect (fast-user-switch return) and RemoteConnect (RDP
    /// reconnect) because both leave the prior session's hooks in an indeterminate state.
    /// </summary>
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        var shouldRestart =
            e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
            e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
            e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect;
        if (!shouldRestart) return;
        Log.Information("Session event {Reason} — scheduling hotkey hook restart", e.Reason);
        _mainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // Shorter than PowerModeChanged.Resume's 2s — unlock is a UI event, the
                // OS is already settled, we just need a brief beat for any lock-screen
                // teardown to finish.
                await Task.Delay(1500);
                await TryRestartHotkeyHookAsync("session unlock");

                // AUD-6: NEW behavior on unlock, stated as such (final check R3 — this handler
                // previously only restarted the hook). On ARM64 modern standby
                // PowerModeChanged.Resume never fires (the comment on OnSessionSwitch is the
                // evidence), so unlock is the only wake signal that can heal a standing capture
                // whose stream died across the sleep. Deliberately the STANDING entry only —
                // never the legacy throwaway warm-up, so a lock/unlock with the feature off
                // causes no per-unlock mic flicker; a healthy surviving stream makes this a
                // no-op (EnsureHealthyAsync's last-data check).
                try
                {
                    if (_standingCaptureService is { } standing)
                        await standing.EnsureHealthyAsync("session-unlock");
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Standing capture unlock check failed");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restart hotkey hook after session unlock");
            }
        });
    }

    /// <summary>
    /// Tear down and restart the global hotkey hook. Serialized via <see cref="_hookRestartLock"/>
    /// so PowerModeChanged.Resume and SessionSwitch.SessionUnlock firing close together
    /// won't race two concurrent restarts. Caller should already be on the UI dispatcher.
    /// </summary>
    private async Task TryRestartHotkeyHookAsync(string trigger)
    {
        if (!await _hookRestartLock.WaitAsync(0))
        {
            Log.Information("Hotkey hook restart skipped ({Trigger}) — another restart in progress", trigger);
            return;
        }
        try
        {
            if (_isQuitting || _hotkeyService == null || _mainWindow == null) return;
            // Coalesce near-miss events (resume + unlock arriving seconds apart on a
            // single wake). The semaphore alone only blocks concurrent overlap; this
            // window also collapses serial repeats within a short cooldown.
            var sinceLast = DateTime.UtcNow - _lastSystemEventRestartUtc;
            if (sinceLast < SystemEventRestartCooldown)
            {
                Log.Information("Hotkey hook restart coalesced ({Trigger}) — last restart {Elapsed}s ago",
                    trigger, sinceLast.TotalSeconds);
                return;
            }
            // Funnel through HotkeyService.RestartAsync so the service's _restartLock
            // serializes with watchdog + hook-task-continuation restart paths. The
            // hook-only reset inside preserves _isHandsFreeMode, so the user's
            // hands-free recording survives the wake/unlock recovery.
            var restarted = await _hotkeyService.RestartAsync(trigger);
            if (restarted)
            {
                _lastSystemEventRestartUtc = DateTime.UtcNow;
                Log.Information("Hotkey hook restarted after {Trigger}", trigger);
            }
            else
            {
                Log.Information("Hotkey hook restart deferred to in-flight call after {Trigger}", trigger);
            }
        }
        finally
        {
            _hookRestartLock.Release();
        }
    }

    /// <summary>
    /// Find the existing VoiceWink window and bring it to the foreground.
    /// Called from the second instance before it exits.
    /// </summary>
    private static void ActivateExistingInstance()
    {
        // Search ALL windows (including hidden/tray) — VoiceWink may be minimized to tray
        IntPtr found = IntPtr.Zero;
        // Pin the delegate to prevent GC during native callback
        Helpers.NativeInterop.EnumWindowsProc callback = (hWnd, _) =>
        {
            var len = Helpers.NativeInterop.GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            var sb = new global::System.Text.StringBuilder(len + 1);
            Helpers.NativeInterop.GetWindowText(hWnd, sb, sb.Capacity);
            // Title AND window-class must both match: the title alone could hit an
            // unrelated window (e.g. a browser page about VoiceWink) now that the
            // title is the plain "VoiceWink" (MainWindowIdentity keeps this matcher
            // and MainWindow's Title in lockstep).
            if (Helpers.MainWindowIdentity.MatchesTitle(sb.ToString()) && HasWinUIWindowClass(hWnd))
            {
                found = hWnd;
                return false; // stop enumeration
            }
            return true;
        };
        Helpers.NativeInterop.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (found != IntPtr.Zero)
        {
            if (Helpers.NativeInterop.IsIconic(found))
                Helpers.NativeInterop.ShowWindow(found, Helpers.NativeInterop.SW_RESTORE);
            else
                Helpers.NativeInterop.ShowWindow(found, Helpers.NativeInterop.SW_SHOW);
            ForceWindowToForeground(found);
        }
    }

    private static bool HasWinUIWindowClass(IntPtr hWnd)
    {
        var cls = new global::System.Text.StringBuilder(64);
        return Helpers.NativeInterop.GetClassName(hWnd, cls, cls.Capacity) > 0
            && cls.ToString() == Helpers.MainWindowIdentity.WinUIWindowClassName;
    }

    /// <summary>
    /// Build and show the shared enhancement-options dialog: an editable input-text box
    /// (pre-filled from <paramref name="capturedContext"/>.RawText), a provider dropdown, a
    /// model dropdown (async-loaded + filtered), and — when <paramref name="isImageGeneration"/>
    /// is true — aspect / size / quality dropdowns gated to the resolved provider+model.
    /// Used by BOTH the redo/regenerate flow and the initial image-generation options picker.
    /// Returns the user's selections, or null if the user cancelled (or no provider is available).
    /// Assumes the main window is already restored/foregrounded by the caller.
    /// </summary>
    /// <param name="ct">Optional external dismissal token (F20): the voice-flow AskImageSize
    /// entry passes the PIPELINE token so a Stop tap during "Waiting for image options..."
    /// hides the open dialog (ShowAsync then returns None → callers cancel). The redo /
    /// text-first entries keep the default token — their dialogs have no pipeline to stop.</param>
    private async Task<MainViewModel.EnhancementDialogSelection?> ShowEnhancementOptionsDialogAsync(
        FrameworkElement root,
        bool isImageGeneration,
        MainViewModel.RedoContext capturedContext,
        string title,
        string primaryButtonText,
        CancellationToken ct = default)
    {
        var enhancement = Services.GetRequiredService<AIEnhancementService>();

        // Provider dropdown
        var providerCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Populate providers: image-capable only for image generation, all for text
        var providers = Enum.GetValues<AIProvider>()
            .Where(p => !isImageGeneration || enhancement.IsImageCapableProvider(p))
            .ToList();
        if (providers.Count == 0)
        {
            Log.Warning("No providers available for enhancement dialog (isImage={IsImage})", isImageGeneration);
            return null;
        }
        foreach (var p in providers)
            providerCombo.Items.Add(p.ToString());

        // Pre-select: previously-used provider if available, else current selection
        var defaultProvider = capturedContext.PreviousProvider
            ?? (isImageGeneration ? enhancement.SelectedImageProvider : enhancement.SelectedProvider);
        var currentProviderIdx = providers.IndexOf(defaultProvider);
        providerCombo.SelectedIndex = currentProviderIdx >= 0 ? currentProviderIdx : 0;

        // Model dropdown
        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Loading models..."
        };

        // Helper: refresh model list for selected provider.
        // Generation counter prevents stale results when user switches providers rapidly.
        int refreshGen = 0;
        ContentDialog? dialogRef = null; // set after dialog creation for button state management
        // Lifecycle fence (PR C): a model fetch can complete AFTER the dialog closes
        // (cancel / Stop-token Hide mid-fetch). Set in dialog.Closed; the post-await
        // populate bails on it so nothing touches the combo of a dismissed dialog.
        var dialogClosed = false;
        // Confirm is enabled iff models loaded AND a model is selected/typed AND the input
        // text is non-empty (IMG-1: the text-first entry starts with an empty prompt —
        // Generate must stay disabled until the user types; UX-1: no model may be silently
        // committed when the pre-select found no known-good candidate). RefreshModelsAsync
        // owns modelsAvailable; the composed rule lives in updateConfirmEnabled, assigned
        // after inputBox/dialog exist (same deferred-wiring pattern as dialogRef).
        var modelsAvailable = false;
        Action? updateConfirmEnabled = null;
        // IMG-5: same late-binding shape as updateConfirmEnabled — the model fetch runs before the
        // option controls exist, so it cannot call RefreshImageOptionGating directly (the compiler
        // enforces this). Assigned once those controls are built.
        Action? regateImageOptions = null;
        // IMG-6: the reference advisory lives inside the isImageGeneration block (it needs the
        // strip's controls), but the thing that changes it — a provider/model switch — is handled
        // by RefreshImageOptionGating at method scope. Same late-binding shape, opposite direction.
        Action? refreshReferenceAdvisory = null;
        // IMG-6 (owner UAT 2026-08-02): the advisory is PINNED OUTSIDE the dialog's ScrollViewer,
        // so this handle has to reach the dialog construction below. A ContentDialog cannot exceed
        // its host window (it renders inside that XamlRoot), and the max-height override already
        // claims all of it bar 48px — so on a short window this form scrolls no matter what, and an
        // advisory inside the scroller scrolls out of sight. Pinning it costs one Grid row and works
        // at every window size, including when the content fits but the user has scrolled up.
        TextBlock? referenceAdvisory = null;
        // UX-1 persist-on-confirm intent tracking: userAdjustedModel is true only when the
        // USER changed the model since the last refresh settled — programmatic mutations run
        // under suppressModelTracking, the flag resets on every refresh (intent is scoped to
        // the current provider view: adjust-on-A then switch-to-B never persists B's
        // pre-fill), and the combo is disabled while a fetch is unsettled so a mid-fetch
        // edit can't be overwritten by the arriving pre-fill yet still count as intent.
        var suppressModelTracking = false;
        var userAdjustedModel = false;
        async Task RefreshModelsAsync()
        {
            var gen = ++refreshGen;
            userAdjustedModel = false;
            modelCombo.IsEnabled = false;
            var providerName = providerCombo.SelectedItem as string ?? "";
            if (!Enum.TryParse<AIProvider>(providerName, out var selProvider))
            {
                suppressModelTracking = true;
                try
                {
                    // Single ItemsSource swap, never Items.Clear()+Add loop: churning a
                    // live IsEditable combo's item collection corrupts native combo/popup
                    // state (the PR #163 0x80070490 class). ItemsSource=null empties it in
                    // one step.
                    modelCombo.ItemsSource = null;
                    modelCombo.Text = "";
                }
                finally { suppressModelTracking = false; }
                modelCombo.PlaceholderText = "Invalid provider";
                modelCombo.IsEnabled = true;
                modelsAvailable = false;
                updateConfirmEnabled?.Invoke();
                return;
            }

            // Clear the editable Text too: free text typed for the PRIOR provider must not
            // survive a provider switch (cross-provider model ids 404 — the same bug class
            // PromptModelOverridePolicy fixed in the Configure dialog) nor satisfy the
            // confirm gate while the new provider's list is still loading.
            suppressModelTracking = true;
            try
            {
                modelCombo.ItemsSource = null; // single swap (see above)
                modelCombo.Text = "";
            }
            finally { suppressModelTracking = false; }
            modelCombo.PlaceholderText = "Loading models...";
            // Disable confirm while a provider's models load so the user can't commit a stale
            // selection mid-switch; re-enabled below once the new model list arrives.
            modelsAvailable = false;
            updateConfirmEnabled?.Invoke();

            var query = isImageGeneration
                ? global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Image
                : global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Text;

            // ONE settled-path body for both the cache-hit and fetched branches, so the two
            // cannot drift (IMG-10b). Everything here is the pre-existing settled path verbatim:
            // single ItemsSource swap, UX-1 pre-select, confirm gate, IMG-5 re-gate.
            void BindModels(IReadOnlyList<string> filtered)
            {
                suppressModelTracking = true;
                try
                {
                    // Single ItemsSource swap (see the clear branches) — one container
                    // rebuild, not N incremental Adds into the live editable combo.
                    modelCombo.ItemsSource = filtered;

                    // Pre-select (UX-1): redo-chain recency, then the per-provider persisted
                    // memory, then the provider default — for ANY selected provider, not just
                    // the one the dialog opened with (the old defaultProvider gate left
                    // live-switched providers on the meaningless alphabetical list[0]).
                    // Per-provider, never the global selection (F17). No candidate offered =
                    // no selection: the confirm gate keeps Generate disabled until a pick.
                    var next = ProviderModelMemoryPolicy.NextDialogModel(
                        selProvider,
                        capturedContext.PreviousProvider,
                        capturedContext.PreviousModel,
                        enhancement.PersistedModelFor(selProvider, isImageGeneration),
                        isImageGeneration
                            ? ImageOptions.DefaultModelFor(selProvider)
                            : TextModelDefaults.DefaultModelFor(selProvider),
                        filtered);
                    if (next != null)
                        modelCombo.SelectedItem = next;
                }
                finally { suppressModelTracking = false; }

                modelCombo.PlaceholderText = filtered.Count == 0
                    ? "No models available"
                    : "Select a model...";
                modelCombo.IsEnabled = true;
                modelsAvailable = filtered.Count > 0;
                // AFTER items + pre-selection are final, so button state is computed from
                // the settled combo.
                updateConfirmEnabled?.Invoke();
                // IMG-5: the list fetch is what populates the capability cache, so re-gate against
                // it. The SelectionChanged handler only fires when the pre-selection actually
                // CHANGES the pick — on first run (empty cache, then a fetch that re-selects the
                // same model) the options would otherwise stay gated on the pre-fetch answer until
                // the user touched a combo. On the cache-hit branch this gates against the current
                // persisted snapshot — correct by construction: a hit proves a fetch succeeded
                // this session, and that fetch is what populated the snapshot.
                regateImageOptions?.Invoke();
            }

            try
            {
                // IMG-10b stale-while-revalidate: a list this session already fetched for this
                // exact (provider, modality) binds INSTANTLY — no spinner, no disabled combo —
                // and the fetch still runs in the BACKGROUND so IMG-5 capability discovery stays
                // live and a model published between opens appears on the NEXT open. The
                // background completion deliberately touches nothing in this dialog: rebinding
                // the list or re-gating the option combos under a live popup is the
                // 0x800F1000/AUD-7 crash surface. Inside the SAME try as the miss path (both
                // reviewers, independently): a WinUI fault while binding the cached list must
                // reach the same visible failure state, not escape to the caller's logger with
                // the combo stranded on "Loading models...".
                var cached = enhancement.TryGetCachedModels(query, showAll: false, selProvider);
                if (cached != null)
                {
                    BindModels(cached);
                    _ = enhancement.RefreshModelListInBackgroundAsync(query, showAll: false, selProvider);
                    return;
                }

                // The service returns a DISPLAY-READY list: it picks the modality catalog, applies
                // provider curation where the provider publishes capability metadata, and runs the
                // shared preview/date policy. Re-filtering here by id pattern would undo curation —
                // for OpenRouter's image catalog it dropped 33 of 40.
                var filtered = await enhancement.FetchAvailableModelsAsync(
                    query, showAll: false, providerOverride: selProvider);

                if (gen != refreshGen || dialogClosed) return; // stale refresh, or dialog dismissed mid-fetch

                BindModels(filtered);
            }
            catch (Exception ex)
            {
                if (gen != refreshGen || dialogClosed) return; // stale, or dialog dismissed mid-fetch (a delayed 401/403 lands here too)
                // LOG-1: exception-bearing ON PURPOSE — provider failures are handled
                // (and message-only-logged) inside TryFetchAvailableModelsAsync; what
                // reaches THIS catch is auth rethrow or post-fetch UI/collection
                // faults, which need their stacks (Codex LOG-1 review).
                Log.Warning(ex, "Failed to fetch models for provider {Provider}", providerName);
                modelCombo.PlaceholderText = "Failed to load models";
                modelCombo.IsEnabled = true;
                modelsAvailable = false;
                updateConfirmEnabled?.Invoke();
            }
        }

        // UX-1 intent + confirm-gate wiring. SelectionChanged marks user intent only outside
        // programmatic mutations; it ALWAYS recomputes the confirm gate so programmatic
        // selection updates button state too. TextSubmitted fires on Enter or focus move for
        // typed text; LostFocus is the belt for typed-text-then-click-Generate (the first
        // click lands on a still-disabled button, focus loss enables it).
        modelCombo.SelectionChanged += (_, _) =>
        {
            if (!suppressModelTracking)
                userAdjustedModel = true;
            updateConfirmEnabled?.Invoke();
        };
        modelCombo.TextSubmitted += (_, _) =>
        {
            userAdjustedModel = true;
            updateConfirmEnabled?.Invoke();
            // A typed model raises no SelectionChanged, so the option gating must re-run here too
            // (IMG-5). Folded into this handler rather than a second TextSubmitted registration —
            // one "model committed" event deserves one reaction (Kimi diff r4). Late-bound because
            // the option controls do not exist yet at this point in the builder.
            regateImageOptions?.Invoke();
        };
        modelCombo.LostFocus += (_, _) => updateConfirmEnabled?.Invoke();

        // Wire provider change → refresh models
        providerCombo.SelectionChanged += async (_, _) =>
        {
            try { await RefreshModelsAsync(); }
            catch (Exception ex) { Log.Warning(ex, "Provider change model refresh failed"); }
        };

        // The initial model load happens AFTER dialog.ShowAsync() below — it's a network
        // fetch and must not gate the dialog's appearance.

        // Editable input text
        var inputLabel = new TextBlock
        {
            Text = "Input text:",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 13
        };
        var inputBox = new TextBox
        {
            Text = capturedContext.RawText,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13
        };
        var scrollViewer = new ScrollViewer
        {
            Content = inputBox,
            MaxHeight = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Image Aspect / Size / Quality dropdowns (image generation only).
        // Filtered + visibility-toggled based on the resolved provider+model.
        ComboBox? aspectCombo = null;
        ComboBox? sizeCombo = null;
        ComboBox? qualityCombo = null;
        ComboBox? versionsCombo = null;
        TextBlock? batchBudgetNote = null;
        // IMG-12: the quality row's REQUEST, tracked apart from the tag in the combo. The combo only
        // ever holds the value clamped to the CURRENT model, so using it as the memory made every
        // clamp permanent — see QualitySelectionTracker for the defects that produced.
        var qualityAsk = new QualitySelectionTracker(
            capturedContext.PreviousImageQuality ?? capturedContext.Prompt?.ImageQuality);
        if (isImageGeneration)
        {
            aspectCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            sizeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            qualityCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            versionsCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

            // The ONLY signal that sees an intermediate pick. An end-state comparison cannot: choose
            // Standard, change your mind back to the displayed Enhanced, and the combo ends where it
            // started while the ask genuinely changed (Codex diff r2). Guarded by the tracker's
            // populate scope, because our own populate raises this same event.
            qualityCombo.SelectionChanged += (_, _) =>
            {
                if (qualityAsk.IsPopulating) return;
                qualityAsk.NoteUserPick(AppTheme.SelectedIndicatorTag(qualityCombo));
            };
        }

        // Dialog layout
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(inputLabel);
        panel.Children.Add(scrollViewer);
        panel.Children.Add(new TextBlock
        {
            Text = "Provider:",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(providerCombo);
        panel.Children.Add(new TextBlock
        {
            Text = isImageGeneration ? "Image model:" : "Model:",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(modelCombo);
        // Aspect / Size / Quality labels — kept as references so we can toggle visibility
        // dynamically when the user changes provider or model in the dropdowns above.
        TextBlock? aspectLabel = null;
        TextBlock? sizeLabel = null;
        TextBlock? qualityLabel = null;
        if (isImageGeneration && aspectCombo != null && sizeCombo != null && qualityCombo != null)
        {
            aspectLabel = new TextBlock { Text = "Aspect ratio:", Foreground = AppTheme.Brush(AppTheme.TextPrimary), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
            sizeLabel = new TextBlock { Text = "Size:", Foreground = AppTheme.Brush(AppTheme.TextPrimary), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
            qualityLabel = new TextBlock { Text = "Quality:", Foreground = AppTheme.Brush(AppTheme.TextPrimary), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(aspectLabel);
            panel.Children.Add(aspectCombo);
            panel.Children.Add(sizeLabel);
            panel.Children.Add(sizeCombo);
            panel.Children.Add(qualityLabel);
            panel.Children.Add(qualityCombo);
        }

        // IMG-3: Versions picker (image generation only) — a per-run spend decision,
        // default 1, seeded from the redo chain; NOT provider/model-gated (the batch
        // loop is app-side). With History off it locks at 1: a batch's only output
        // surface is History (N>1 never pastes), so there is nowhere to put versions.
        if (isImageGeneration && versionsCombo != null)
        {
            for (var v = 1; v <= ImageBatchPolicy.MaxVersions; v++)
                versionsCombo.Items.Add(new ComboBoxItem { Content = v.ToString(), Tag = v });
            var historyOnAtBuild = Services.GetRequiredService<SettingsService>()
                .GetBool(AppDefaults.IsHistoryEnabled, true);
            var seededCount = historyOnAtBuild
                ? ImageBatchPolicy.SeedCount(capturedContext.PreviousImageCount)
                : 1;
            versionsCombo.SelectedIndex = seededCount - 1;
            versionsCombo.IsEnabled = historyOnAtBuild;

            panel.Children.Add(new TextBlock
            {
                Text = "Versions:",
                Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(versionsCombo);
            if (!historyOnAtBuild)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Multiple versions need History (Settings)",
                    Foreground = AppTheme.Brush(AppTheme.SubtleText),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // IMG-4: shown by the confirm gate when count × references exceed the batch
            // budget — the dialog stays open with the actionable, count-specific limit.
            batchBudgetNote = new TextBlock
            {
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            panel.Children.Add(batchBudgetNote);
        }

        // IMG-5/IMG-6: the provider+model this run will ACTUALLY use. Extracted so the option
        // gating (which dropdowns to show) and the reference limits (how many may be added, and
        // whether to advise) resolve the target ONCE, the same way. They previously could not
        // disagree because only one of them existed; now that both read it, a second copy of this
        // resolution is exactly how they would drift apart.
        (AIProvider Provider, string? Model) ResolveImageGatingTarget()
        {
            var providerName = (providerCombo.SelectedItem as string)
                ?? capturedContext.PreviousProvider?.ToString();
            var provider = (!string.IsNullOrEmpty(providerName)
                && Enum.TryParse<AIProvider>(providerName, out var p))
                ? p
                : (capturedContext.PreviousProvider ?? AIProvider.OpenAI);
            // The previous code passed a null model for "(Default)", which ImageOptions resolves to
            // the provider's HARDCODED default — while the runtime resolves the provider's
            // PERSISTED model. On OpenRouter that let the dialog describe gpt-image-2 while the
            // generation ran krea.
            var model = ImageOptionGating.ResolveGatingModel(
                modelCombo.SelectedItem as string ?? modelCombo.Text,
                capturedContext.PreviousModel,
                enhancement.ResolveEffectiveImageModel(capturedContext.Prompt, provider));
            return (provider, model);
        }

        ImageModelCapabilities? CurrentImageCapabilities()
        {
            var (provider, model) = ResolveImageGatingTarget();
            return enhancement.ImageCapabilitiesFor(provider, model);
        }

        // IMG-6: the add bound for the currently-selected model. Only OpenRouter publishes a
        // catalog, so without the second source "NO CAP" would hold for one provider out of three —
        // OpenAI-direct documents 16 for its edits route and was still pinned at 6 (Codex diff
        // review, blocking).
        int CurrentMaxReferences()
        {
            var (provider, model) = ResolveImageGatingTarget();
            return ReferenceImagePolicy.MaxReferenceCountFor(
                enhancement.ImageCapabilitiesFor(provider, model),
                ReferenceImagePolicy.PublishedMaxForDirectProvider(provider, model));
        }

        // ENH-6/6f: reference image strip (image generation only). The dialog owns a
        // LOCAL pending list — seeded from the captured context after per-item
        // re-validation (a stale armed reference drops with a note, never a dead
        // path) and returned as an immutable SNAPSHOT on confirm: the dialog is
        // authoritative for the run it confirms, and its working list must never
        // escape (Codex plan round 1). Validation here is UX-only;
        // AIEnhancementService re-validates (containment, type, size, count,
        // aggregate budget) at generation time regardless.
        var pendingReferences = new List<Models.ReferenceImageSelection>();
        if (isImageGeneration)
        {
            var referenceNote = new TextBlock
            {
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            // IMG-6: a SEPARATE line from referenceNote, deliberately. referenceNote carries
            // transient EVENT text (a refused add, a dropped seed); this carries a STANDING
            // property of (attached count × selected model). One shared TextBlock would make each
            // wipe the other — switching model would erase "Image already added.", and a refused
            // click would erase the limit advice — so they are never both true at once, which is
            // wrong: they are independent facts.
            var advisoryBlock = new TextBlock
            {
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                // It sits in the dialog's pinned row, outside the scroller's padded wrapper, so it
                // carries its own spacing — SYMMETRIC so the text is optically centred in the band
                // between the scrolled content and the button row (owner UAT 2026-08-02: it read as
                // top-aligned with a gap beneath). VerticalAlignment.Center makes that hold if the
                // row is ever given more height than the text needs.
                Margin = new Thickness(0, 12, 0, 12),
                VerticalAlignment = VerticalAlignment.Center
            };
            referenceAdvisory = advisoryBlock;
            // The ENH-6b in-dialog retention notice was REMOVED at the owner's request
            // (ENH-6f UAT, 2026-07-15) — retention is disclosed in the privacy policy
            // (shipped in privacy-v5 §3; legal addenda A4), the README data table, and
            // the GDPR export README instead of dialog chrome.
            var useLastButton = new Button
            {
                Content = "Use last image",
                MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var browseButton = new Button
            {
                Content = "Browse",
                MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // The thumbnail STRIP: one 64×64 slot per selected reference (each with a
            // corner remove button), wrapping via FlowPanel when they exceed the
            // dialog width; ONE empty placeholder slot while the list is empty so the
            // row never collapses to zero height. Add buttons live on their own row
            // below (equal star columns at MinHeight 32 — the WinUI ComboBox default,
            // so they line up with the dropdowns above; owner request 2026-07-11).
            var referenceStrip = new Controls.FlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            var referenceButtonsRow = new Grid { ColumnSpacing = 8 };
            referenceButtonsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            referenceButtonsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(useLastButton, 0);
            Grid.SetColumn(browseButton, 1);
            referenceButtonsRow.Children.Add(useLastButton);
            referenceButtonsRow.Children.Add(browseButton);

            // NOTE-only updates never touch the strip (Codex diff review r1: a refused
            // duplicate/over-cap click must not clear + re-decode every thumbnail —
            // repeated refusals would otherwise ACCUMULATE blocking cloud/network
            // opens; the single-reference dialog had the same-selection guard for
            // exactly this). Strip rebuilds happen ONLY on an actual add/remove/seed.
            void ShowReferenceNote(string? note)
            {
                referenceNote.Text = note ?? "";
                referenceNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            }

            // IMG-6: ADVISORY ONLY — this never gates, drops, or reorders a reference. It states
            // what the selected model publishes and leaves the choice with the user (the AUD-4
            // Bluetooth-microphone posture). Two independent inputs move it, so it is re-evaluated
            // from BOTH: the attached count (every strip render) and the model's published limit
            // (every provider/model change, via refreshReferenceAdvisory).
            void RefreshReferenceAdvisory()
            {
                var advice = ReferenceImagePolicy.DescribeReferenceOverage(
                    pendingReferences.Count, CurrentImageCapabilities());
                advisoryBlock.Text = advice ?? "";
                advisoryBlock.Visibility = string.IsNullOrEmpty(advice)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            refreshReferenceAdvisory = RefreshReferenceAdvisory;

            // Retires superseded thumbnail work: a decode only publishes into the
            // CURRENT render's controls (stale generations return without touching
            // the tree; their slots are orphaned anyway).
            var stripRenderGeneration = 0;

            // Rebuild the strip from the pending list — cheap at the 6-item cap; each
            // slot's thumbnail decodes async and paints ITS OWN Image control, fenced
            // by generation + dialogClosed (Codex final check r2 + diff review r1).
            void RenderReferenceStrip(string? note)
            {
                ShowReferenceNote(note);
                // Before the empty-list early return below: emptying the strip must CLEAR a
                // standing advisory, not leave the last one on screen.
                RefreshReferenceAdvisory();
                stripRenderGeneration++;
                referenceStrip.Children.Clear();
                if (pendingReferences.Count == 0)
                {
                    referenceStrip.Children.Add(new Border
                    {
                        Width = 64,
                        Height = 64,
                        CornerRadius = new CornerRadius(4),
                        Background = AppTheme.Brush(AppTheme.RowBg)
                    });
                    return;
                }
                foreach (var selection in pendingReferences)
                    referenceStrip.Children.Add(BuildReferenceSlot(selection));
            }

            FrameworkElement BuildReferenceSlot(Models.ReferenceImageSelection selection)
            {
                var thumb = new Image
                {
                    Width = 64,
                    Height = 64,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                };
                var slot = new Grid { Width = 64, Height = 64 };
                slot.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = AppTheme.Brush(AppTheme.RowBg),
                    Child = thumb
                });
                var removeButton = new Button
                {
                    Content = "", // Segoe Fluent Cancel glyph
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 10,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top
                };
                ToolTipService.SetToolTip(removeButton, "Remove");
                removeButton.Click += (_, _) =>
                {
                    pendingReferences.Remove(selection);
                    RenderReferenceStrip(null);
                };
                slot.Children.Add(removeButton);
                // No filename display (owner request) — it rides the slot tooltip.
                ToolTipService.SetToolTip(slot, global::System.IO.Path.GetFileName(selection.Path));
                _ = LoadThumbnailIntoSlotAsync(selection, thumb, stripRenderGeneration);
                return slot;
            }

            // Thumbnail decode via BitmapDecoder → BGRA8 + sRGB color management +
            // EXIF orientation → SoftwareBitmapSource. A plain BitmapImage renders
            // CMYK/color-managed JPEGs as a solid BLACK box (live incident 2026-07-11:
            // a browse-picked 1.7 MB jpeg uploaded and generated fine but showed
            // black) and ignores EXIF rotation; the decoder path normalizes anything
            // WIC can decode. Fail-soft: empty slot + tooltip — the generation-time
            // validation is the gate, and the provider decodes the bytes server-side.
            async Task LoadThumbnailIntoSlotAsync(Models.ReferenceImageSelection selection, Image thumb, int generation)
            {
                var source = await TryDecodeThumbnailAsync(selection.Path);
                // Retirement fences (Codex final check r2 + diff review r1): a decode
                // must not publish into a dismissed dialog's tree, and a decode whose
                // render was superseded by a newer rebuild returns without touching
                // anything (its slot is orphaned; publishing would be dead work).
                if (dialogClosed || generation != stripRenderGeneration) return;
                thumb.Source = source;
            }

            static async Task<Microsoft.UI.Xaml.Media.ImageSource?> TryDecodeThumbnailAsync(string path)
            {
                try
                {
                    // The open is the blocking step for cloud-placeholder/network-backed
                    // files — off the UI thread (ENH-6f: up to six slots decode after
                    // seeding, so a per-open stall would multiply; Codex final check).
                    using var stream = await Task.Run(() => global::System.IO.File.OpenRead(path));
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                        stream.AsRandomAccessStream());

                    var transform = new Windows.Graphics.Imaging.BitmapTransform
                    {
                        InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                    };
                    // Decode at thumbnail size (64 DIP slot → 128 px covers 200% scale).
                    var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                    if (longest > 128)
                    {
                        var scale = 128.0 / longest;
                        transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                        transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                    }

                    var bitmap = await decoder.GetSoftwareBitmapAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        transform,
                        Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                        Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

                    var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                    await source.SetBitmapAsync(bitmap);
                    return source;
                }
                catch (Exception ex)
                {
                    // Information, not Debug — the file sink is Information+ and the
                    // 2026-07-11 black-thumbnail report was undiagnosable from the log.
                    // PATH-FREE by the ENH-6 rule: the exception object would carry the
                    // user-picked file path (FileStream embeds it in Message) into the
                    // Sentry breadcrumb trail; type + HResult diagnose codec/IO failures
                    // without it (Codex polish review 2026-07-11).
                    Log.Information("Reference thumbnail decode failed: {ErrorType} (HResult=0x{HResult:X8})",
                        ex.GetType().Name, ex.HResult);
                    return null;
                }
            }

            // UX pre-check shared by Browse / Use-last / context seeding: type +
            // existence + size cap, so a doomed selection is refused HERE with a note
            // instead of failing the eventual generation. Outs the file length for the
            // ENH-6f aggregate-budget check (0 on failure).
            static bool TryDescribeUsable(string path, out string? problem, out long length)
            {
                problem = null;
                length = 0;
                if (ReferenceImagePolicy.MimeFromExtension(path) == null)
                {
                    problem = ReferenceImagePolicy.UnsupportedReferenceTypeMessage + ".";
                    return false;
                }
                try
                {
                    var info = new global::System.IO.FileInfo(path);
                    if (!info.Exists) { problem = "File not found."; return false; }
                    if (info.Length == 0) { problem = "File is empty."; return false; }
                    if (info.Length > ReferenceImagePolicy.MaxBytes)
                    {
                        // Derived from the constant so a future cap change can't
                        // leave this message stale (review 2026-07-13).
                        problem = $"Image is too large (max {ReferenceImagePolicy.MaxBytes / (1024 * 1024)} MB).";
                        return false;
                    }
                    length = info.Length;
                }
                catch (Exception)
                {
                    problem = "File could not be read.";
                    return false;
                }
                return true;
            }

            // The ONE add gate (ENH-6f) shared by Browse / Use-last: count cap →
            // duplicate (canonical, case-insensitive) → per-file usability → aggregate
            // byte budget (best-effort UX sums; the generation-time gate is
            // authoritative). Returns false with the refusal note in
            // <paramref name="problem"/>; the caller renders once per interaction.
            // isSeed distinguishes RESTORING references the run already had from ADDING a new one
            // (Codex diff review r2, blocking). They are different questions and sharing one bound
            // silently destroyed data: 16 references attached under gpt-image, model switched to a
            // low-limit one, generation refused by the provider — failure sanitation correctly
            // retains all 16, then reopening seeded only the first 6 and told the user the other 10
            // were "no longer available". They were fine. A seed is bounded only by the read gate's
            // sanity ceiling; the per-model figure is guidance for what to ADD next, and the
            // advisory already says the selected model may not take them all.
            bool TryAddReference(string path, Models.ReferenceImageOrigin origin, out string? problem,
                bool isSeed = false)
            {
                problem = null;
                // IMG-6 (owner: "NO CAP", 2026-08-02): the add bound is the MODEL's published
                // figure wherever we have one — gpt-image 16 (catalog AND OpenAI's documented edits
                // route), gemini-3 14, riverflow 10 — and it only ever RAISES, so a model publishing
                // 1 never drops the bound to 1. That case belongs to the advisory, because the model
                // is switchable after the photos are chosen.
                //
                // THIS is where per-model policy lives. The generation-side read gate deliberately
                // does NOT re-apply it: doing so meant that attaching 16 under a 16-capable model
                // and then switching to krea made the app reject the list before the provider saw
                // it, contradicting the advisory (Codex diff review, blocking). Guidance here,
                // authority at the provider.
                var maxReferences = ReferenceImagePolicy.AdmissionBound(isSeed, CurrentMaxReferences());
                if (pendingReferences.Count >= maxReferences)
                {
                    // A model switch can leave MORE attached than the new model's bound — they were
                    // added under a higher-limit model and are deliberately never dropped. Naming a
                    // maximum below the visible thumbnail count reads as a bug, so the over-cap case
                    // says the one thing that is both true and actionable.
                    problem = pendingReferences.Count > maxReferences
                        ? "Remove a reference image before adding another."
                        : $"Maximum {maxReferences} reference images.";
                    return false;
                }
                if (pendingReferences.Any(r => PathsEqual(r.Path, path)))
                {
                    problem = "Image already added.";
                    return false;
                }
                if (!TryDescribeUsable(path, out problem, out var length))
                    return false;
                long total = length;
                foreach (var existing in pendingReferences)
                {
                    if (TryDescribeUsable(existing.Path, out _, out var existingLength))
                        total += existingLength;
                }
                if (total > ReferenceImagePolicy.MaxTotalBytes)
                {
                    problem = $"Reference images exceed {ReferenceImagePolicy.MaxTotalBytes / (1024 * 1024)} MB total.";
                    return false;
                }
                pendingReferences.Add(new Models.ReferenceImageSelection(path, origin));
                return true;
            }

            static bool PathsEqual(string a, string b)
            {
                try
                {
                    return string.Equals(
                        global::System.IO.Path.GetFullPath(a),
                        global::System.IO.Path.GetFullPath(b),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                }
            }

            useLastButton.Click += async (_, _) =>
            {
                try
                {
                    var history = Services.GetRequiredService<TranscriptionHistoryService>();
                    var lastPath = await history.TryGetLatestUsableImagePathAsync();
                    if (lastPath == null)
                    {
                        ShowReferenceNote("No previous generated image found.");
                        return;
                    }
                    // Belt-and-braces: the scan is already reference-usable-aware, but
                    // the file can change between the scan and this click. A refusal is
                    // note-only — the strip (and its decodes) rebuild only on a real add.
                    if (TryAddReference(lastPath, Models.ReferenceImageOrigin.AppImages, out var lastProblem))
                        RenderReferenceStrip(null);
                    else
                        ShowReferenceNote(lastProblem);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Use-last-image lookup failed");
                    ShowReferenceNote("Could not look up the last image.");
                }
            };

            browseButton.Click += async (_, _) =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker
                    {
                        SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
                    };
                    // Driven by the policy, never hand-listed here: the picker must offer exactly
                    // what the read gate accepts. Hand-listing drifted the moment AVIF was added —
                    // the gate took it and the picker kept showing four types.
                    //
                    // The set is capability-aware, so .avif appears only where this machine can
                    // decode it (the conversion to PNG needs the HEIF + AV1 extensions: present on
                    // most Win11 installs, a Store download on the Win10 19041 baseline).
                    foreach (var ext in ReferenceImagePolicy.SupportedReferenceExtensions)
                        picker.FileTypeFilter.Add(ext);
                    if (_mainWindow == null) return;
                    WinRT.Interop.InitializeWithWindow.Initialize(
                        picker, WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow));

                    // ENH-6f: multi-pick — each file runs the same add gate; the FIRST
                    // refusal's note wins (later successes still add). The strip
                    // rebuilds only when something actually changed.
                    var files = await picker.PickMultipleFilesAsync();
                    if (files == null || files.Count == 0) return; // picker cancelled — keep the current strip
                    string? firstProblem = null;
                    var addedAny = false;
                    foreach (var file in files)
                    {
                        if (TryAddReference(file.Path, Models.ReferenceImageOrigin.UserPicked, out var problem))
                            addedAny = true;
                        else
                            firstProblem ??= problem;
                    }
                    if (addedAny)
                        RenderReferenceStrip(firstProblem);
                    else
                        ShowReferenceNote(firstProblem);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Reference image browse failed");
                    ShowReferenceNote("Could not open the file picker.");
                }
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Reference images (optional):",
                Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(referenceStrip);
            panel.Children.Add(referenceButtonsRow);
            panel.Children.Add(referenceNote);
            // referenceAdvisory is deliberately NOT added here — it is pinned outside the
            // scroller at the dialog's Content assignment (see its declaration).

            // Seed from the captured context, re-validated per origin PER ITEM: an
            // armed redo may carry references whose files were since deleted from
            // History — dead items drop with ONE aggregate note (the dialog-side
            // mirror of the per-item re-arm sanitation). FAIL-CLOSED origin switch
            // (ENH-6b): app-owned origins re-validate containment against THEIR root;
            // an unknown future origin seeds nothing.
            var seededAnyDropped = false;
            if (capturedContext.References is { Count: > 0 } seededReferences)
            {
                foreach (var seeded in seededReferences)
                {
                    var originOk = seeded.Origin switch
                    {
                        Models.ReferenceImageOrigin.AppImages =>
                            ReferenceImagePolicy.IsUsableAppImagePath(seeded.Path, AppPaths.ImagesDir, out _),
                        Models.ReferenceImageOrigin.AppReferences =>
                            ReferenceImagePolicy.IsUsableAppImagePath(seeded.Path, AppPaths.ReferencesDir, out _),
                        Models.ReferenceImageOrigin.UserPicked =>
                            global::System.IO.File.Exists(seeded.Path),
                        _ => false
                    };
                    // isSeed: restoring what the run already had, so the per-model ADD bound must
                    // not apply — see TryAddReference. A seeded item drops only when it is genuinely
                    // unusable (missing file, failed containment), which is what the note claims.
                    if (!originOk || !TryAddReference(seeded.Path, seeded.Origin, out _, isSeed: true))
                        seededAnyDropped = true;
                }
            }
            RenderReferenceStrip(seededAnyDropped
                ? "Some previous reference images are no longer available."
                : null);
        }

        // Refresh aspect / size / quality dropdowns + visibility for the current selection.
        // Called on initial show and on every provider/model change so the user only sees
        // options the resolved model actually accepts.
        void RefreshImageOptionGating()
        {
            if (!isImageGeneration || aspectCombo == null || sizeCombo == null || qualityCombo == null
                || aspectLabel == null || sizeLabel == null || qualityLabel == null) return;

            // IMG-5: gate against the model the run will ACTUALLY use, and against what THAT model
            // publishes. `enhancement` is already resolved at the top of this method — no second
            // lookup; the target resolution itself lives in ResolveImageGatingTarget so the
            // reference limits below gate against the same model these dropdowns describe.
            var (provider, model) = ResolveImageGatingTarget();
            var gating = ImageOptionGating.Decide(
                provider, model, enhancement.ImageCapabilitiesFor(provider, model));
            var supportedTiers = gating.Tiers;

            // Aspect row: hide AND skip repopulation when the model publishes none.
            // PopulateIndicatorCombo atomically replaces the rows, preserving a supported tag and
            // otherwise falling back to Auto — and a hidden row's tag is never among the new rows —
            // so populating a row we then hide would silently erase the user's saved aspect on the
            // next Save. Mirrors the tier path immediately below, which has always worked this way.
            var aspectVis = gating.ShowAspect ? Visibility.Visible : Visibility.Collapsed;
            aspectLabel.Visibility = aspectVis;
            aspectCombo.Visibility = aspectVis;
            if (gating.ShowAspect)
            {
                AppTheme.PopulateImageAspectComboWithIndicators(
                    aspectCombo,
                    AppTheme.SelectedIndicatorTag(aspectCombo)
                        ?? capturedContext.PreviousImageAspect
                        ?? capturedContext.Prompt?.ImageAspect,
                    gating.Aspects);
            }

            var tierVis = supportedTiers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            sizeLabel.Visibility = tierVis;
            sizeCombo.Visibility = tierVis;
            if (supportedTiers.Count > 0)
            {
                AppTheme.PopulateImageSizeTierComboWithIndicators(
                    sizeCombo,
                    AppTheme.SelectedIndicatorTag(sizeCombo)
                        ?? capturedContext.PreviousImageSizeTier
                        ?? capturedContext.Prompt?.ImageSizeTier,
                    supportedTiers);
            }

            // IMG-12: the quality row repopulates per model like the two rows above it. It used to
            // populate ONCE (`Items.Count == 0`), which was fine while the rows were the same three
            // for every model — but the offered tiers are now per-model, so a stale row set would
            // keep offering Maximum after a switch to a model that tops out at Enhanced. Same
            // hide-AND-skip rule as the aspect row: a hidden row is left untouched so Save cannot
            // erase a tag the user's model simply does not expose. The selection is CLAMPED rather
            // than dropped, so the dialog pre-selects exactly what NormalizeForModel would send.
            var qVis = gating.ShowQuality ? Visibility.Visible : Visibility.Collapsed;
            qualityLabel.Visibility = qVis;
            qualityCombo.Visibility = qVis;
            if (gating.ShowQuality)
            {
                // Display = the ask, clamped to THIS model. The ask itself is never overwritten here
                // — only the user's own SelectionChanged moves it — so returning to a capable model
                // restores the original tier instead of keeping the narrow model's ceiling.
                using (qualityAsk.BeginPopulate())
                {
                    AppTheme.PopulateImageQualityComboWithIndicators(
                        qualityCombo,
                        ImageOptions.ClampQuality(qualityAsk.Requested, gating.Qualities),
                        gating.Qualities);
                }
            }

            // IMG-6: the model just changed, so the reference limit may have too — an advisory
            // armed against the previous model would now be describing the wrong one. Null when
            // this dialog has no reference strip.
            refreshReferenceAdvisory?.Invoke();
        }
        RefreshImageOptionGating();
        // Late-bound now that the option controls exist — see the declaration.
        regateImageOptions = RefreshImageOptionGating;
        providerCombo.SelectionChanged += (_, _) => RefreshImageOptionGating();
        modelCombo.SelectionChanged += (_, _) => RefreshImageOptionGating();
        // A TYPED model raises no SelectionChanged (IMG-5, Codex plan review). Folded into the
        // existing confirm-gate TextSubmitted handler above rather than registered separately —
        // two subscriptions reacting to one "model committed" event is a readability trap
        // (Kimi diff r4).

        // Height-flexible scroller: the form's natural height varies (reference thumbnail,
        // per-model option gating) and a bare StackPanel CLIPPED the button row when it exceeded
        // the ContentDialog's max height (2026-07-11).
        FrameworkElement dialogContent = AppTheme.CreateDialogScroller(panel);
        if (referenceAdvisory != null)
        {
            // IMG-6 (owner UAT 2026-08-02): pin the reference advisory BELOW the scroller so it is
            // visible at every scroll position. The dialog can be taller than the window allows —
            // ContentDialogMaxHeight below already takes the whole window bar 48px, and a
            // ContentDialog cannot exceed its host window at all — so on a short window this form
            // scrolls, and an advisory inside the scroller scrolls out of sight. That made the one
            // message whose whole job is to be noticed the easiest one to miss.
            var contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(dialogContent, 0);
            Grid.SetRow(referenceAdvisory, 1);
            contentGrid.Children.Add(dialogContent);
            contentGrid.Children.Add(referenceAdvisory);
            dialogContent = contentGrid;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = dialogContent,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme
        };
        // ContentDialog's template caps content at the ContentDialogMaxHeight resource
        // (756 epx default) — shorter than this form's natural height, so the reference
        // row's Browse button needed scrolling on a large monitor (owner, 2026-07-15).
        // Let the dialog use the window height minus breathing room; never SHRINK below
        // the default (small windows keep today's behavior — the scroller still guards).
        var rootHeight = root.XamlRoot?.Size.Height ?? 0;
        if (rootHeight > 0)
            dialog.Resources["ContentDialogMaxHeight"] = global::System.Math.Max(756.0, rootHeight - 48.0);
        dialogRef = dialog; // enable RefreshModelsAsync to manage button state
        dialog.Closed += (_, _) => dialogClosed = true; // lifecycle fence for in-flight fetches
        // UI-18: re-gate once the dialog is actually on screen. Every populate above runs while
        // this dialog is still detached from the visual tree, and an ItemsSource SWAP in that state
        // leaves the CLOSED combo blank — the row is selected and every reader returns it, only the
        // selection box renders nothing. WinUI rendering behaviour, owner-observed 2026-09-17;
        // nothing in this repo can see it from a test, which is how it shipped.
        //
        // What kept it hidden: the LAST populate usually lands post-show. On an IMG-10b cache MISS
        // the model fetch awaits the network, so BindModels' re-gate runs after the dialog is up.
        // On a cache HIT — every open after the first — that bind is synchronous and the last
        // populate is pre-show too. NOT a clean first-open/later-open split, though: a provider
        // with no key, and an offline throw, both return synchronously on the miss path as well.
        //
        // Safe HERE specifically — not idempotent in general. Opened fires before any user
        // interaction, so every row re-derives the same tag from the same expression it used
        // pre-show, and the quality row reads the untouched ask. SelectedIndicatorTag answers null
        // for BOTH "Auto" and "nothing selected" (see its doc comment), so a re-gate placed AFTER
        // a user pick is a different question — see IMG-17.
        dialog.Opened += (_, _) => RefreshImageOptionGating();
        // Composed confirm rule: models loaded AND non-empty input text (IMG-1 — the
        // text-first entry starts empty; a redo's pre-filled text satisfies it as before).
        bool HasUsableModelSelection() =>
            !string.IsNullOrWhiteSpace(modelCombo.SelectedItem as string ?? modelCombo.Text);
        updateConfirmEnabled = () =>
        {
            if (dialogRef != null)
                dialogRef.IsPrimaryButtonEnabled = modelsAvailable
                    && HasUsableModelSelection()
                    && !string.IsNullOrWhiteSpace(inputBox.Text);
        };
        inputBox.TextChanged += (_, _) => updateConfirmEnabled?.Invoke();
        // Show the dialog IMMEDIATELY; load models in the background. The initial fetch
        // is a network call — on a degraded connection it burns the 10 s ConnectTimeout
        // × 3 retries (~30-40 s), and awaiting it before ShowAsync kept the dialog
        // invisible that whole time after a History-retry click (2026-07-10 16:37).
        // RefreshModelsAsync + updateConfirmEnabled own button state from here.
        dialog.IsPrimaryButtonEnabled = false;
        var initialModelLoad = RefreshModelsAsync();
        // LOG-1: exception-bearing ON PURPOSE — RefreshModelsAsync handles provider
        // failures internally; a fault reaching this continuation is an unexpected
        // defect that needs its stack (Codex LOG-1 review).
        _ = initialModelLoad.ContinueWith(
            t => Log.Warning(t.Exception, "Initial model load for enhancement dialog failed"),
            TaskContinuationOptions.OnlyOnFaulted);

        // F20: dismiss the open dialog when the external token fires (Stop tap). Marshaled —
        // Cancel() may run off the UI thread; Hide() is thread-affine. Registration disposes
        // with method scope, i.e. after ShowAsync completes. Hide() on an already-closed
        // dialog throws harmlessly into the catch.
        using var ctReg = ct.CanBeCanceled
            ? ct.Register(() => dialog.DispatcherQueue.TryEnqueue(() => { try { dialog.Hide(); } catch { } }))
            : default;

        // IMG-4: batch reference-budget gate. N concurrent payload builds multiply the
        // transient allocation, so a batch's aggregate reference bytes must fit
        // MaxBatchTotalBytes(count) = 150 MB / count. Enforced at CONFIRM — args.Cancel
        // keeps the dialog open with the actionable, count-specific limit shown, before
        // any spend — with the shared read's count-scoped budget as the backstop.
        // Unreadable sizes are skipped fail-soft (the backstop still enforces).
        if (isImageGeneration)
        {
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (batchBudgetNote != null)
                    batchBudgetNote.Visibility = Visibility.Collapsed;
                var count = ImageBatchPolicy.ClampCount(
                    (versionsCombo?.SelectedItem as ComboBoxItem)?.Tag as int? ?? 1);
                if (count <= 1 || pendingReferences.Count == 0)
                    return;
                long totalBytes = 0;
                foreach (var reference in pendingReferences)
                {
                    try { totalBytes += new global::System.IO.FileInfo(reference.Path).Length; }
                    catch { /* size unavailable — the shared-read backstop still enforces */ }
                }
                var budget = Helpers.ReferenceImagePolicy.MaxBatchTotalBytes(count);
                if (totalBytes <= budget)
                    return;
                args.Cancel = true;
                if (batchBudgetNote != null)
                {
                    batchBudgetNote.Text =
                        $"References exceed {budget / (1024 * 1024)} MB for {count} versions — remove references or lower Versions";
                    batchBudgetNote.Visibility = Visibility.Visible;
                }
            };
        }

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null; // user cancelled

        var selectedModel = modelCombo.SelectedItem as string ?? modelCombo.Text;
        if (string.IsNullOrWhiteSpace(selectedModel))
            return null; // no usable model (combo cleared / none loaded) — treat as cancel so
                         // callers never proceed with a provider/model mismatch

        var providerStr = providerCombo.SelectedItem as string ?? "";
        AIProvider? selectedProvider = Enum.TryParse<AIProvider>(providerStr, out var sp) ? sp : null;

        // UX-1: remember the confirmed model per provider so the next dialog (and the AI
        // Enhancement page / main pipeline, which share this memory) restore it. Gated on
        // real user intent + an actual change vs the RAW persisted value, so an untouched
        // pre-fill never writes and never pins the implicit default.
        if (selectedProvider is { } chosenProvider
            && ProviderModelMemoryPolicy.ShouldPersistOnConfirm(
                userAdjustedModel, selectedModel,
                enhancement.PersistedModelFor(chosenProvider, isImageGeneration)))
        {
            enhancement.RememberModelFor(chosenProvider, selectedModel, isImageGeneration);
        }

        var editedText = inputBox.Text?.Trim() ?? capturedContext.RawText;
        var selectedAspect = AppTheme.SelectedIndicatorTag(aspectCombo);
        var selectedSizeTier = AppTheme.SelectedIndicatorTag(sizeCombo);
        // IMG-12: the standing REQUEST, not the combo's tag — a hidden row still holds whatever an
        // earlier model's clamp left in it, and a visible one holds this model's ceiling. The wire
        // clamps per run and History records what actually rendered, so carrying the ask forward
        // costs nothing and stops one narrow model from rewriting the redo chain.
        var selectedQuality = qualityAsk.Requested;
        // IMG-3: the Versions pick, clamped defensively (text dialogs have no combo → 1).
        var selectedCount = ImageBatchPolicy.ClampCount(
            (versionsCombo?.SelectedItem as ComboBoxItem)?.Tag as int? ?? 1);

        return new MainViewModel.EnhancementDialogSelection(
            editedText, selectedProvider, selectedModel,
            selectedAspect, selectedSizeTier, selectedQuality,
            // SNAPSHOT, never the working list — the carrier must be immutable
            // (ENH-6f, Codex plan round 1). Empty = no references.
            pendingReferences.Count == 0 ? null : pendingReferences.ToArray(),
            selectedCount);
    }

    /// <summary>UI-7: the window a picker dialog took the foreground from, so the dialog can give it
    /// back. Owned by App because App is the only consumer — the recording path deliberately does
    /// NOT read it (see <see cref="Helpers.PickerForegroundHolder"/> for why a stored handle must
    /// never become a paste target).</summary>
    private readonly Helpers.PickerForegroundHolder _pickerForeground = new();

    /// <summary>
    /// UI-7: remember where the user actually was, immediately before a picker dialog takes the
    /// foreground. Pairs with <see cref="ReleasePickerForeground"/> in the same handler's
    /// <c>finally</c> — never call one without the other.
    ///
    /// <para>Must run BEFORE <see cref="RestoreMainWindow"/>: that is the call which makes VoiceWink
    /// the foreground, and after it there is nothing left to remember.</para>
    /// </summary>
    private void CapturePickerForeground()
    {
        var foreground = Helpers.NativeInterop.GetForegroundWindow();
        _pickerForeground.CapturePreDialog(
            foreground, Helpers.ForegroundOwnership.IsOurOwnProcess(foreground));
    }

    /// <summary>
    /// UI-7: hand the foreground back to whatever had it before the dialog, and clear the hold.
    ///
    /// <para>Uses <see cref="ForceWindowToForeground"/> — the same three-step ladder
    /// <see cref="RestoreMainWindow"/> used to TAKE the foreground, so giving it back is symmetric
    /// with taking it rather than a weaker bare <c>SetForegroundWindow</c> that the OS may refuse.
    /// A failure is not worth reporting: the user is looking at VoiceWink, which is a nuisance they
    /// can see and fix, not a wrong result.</para>
    /// </summary>
    private void ReleasePickerForeground()
    {
        if (_pickerForeground.TryTakeForRestore(out var previous))
            ForceWindowToForeground(previous);
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow == null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);

        // The window is hidden (SW_HIDE) when sent to tray. Restore a minimized window to its
        // prior state; otherwise SW_SHOW shows it in place — an unconditional SW_RESTORE would
        // un-maximize a window the user had maximized before hiding to tray.
        if (Helpers.NativeInterop.IsIconic(hwnd))
            Helpers.NativeInterop.ShowWindow(hwnd, Helpers.NativeInterop.SW_RESTORE);
        else
            Helpers.NativeInterop.ShowWindow(hwnd, Helpers.NativeInterop.SW_SHOW);

        ForceWindowToForeground(hwnd);
    }

    /// <summary>
    /// Reliably brings <paramref name="hwnd"/> to the very front with keyboard focus.
    ///
    /// <para>A bare <c>SetForegroundWindow</c> is unreliable from a tray-icon click: the
    /// H.NotifyIcon click commands run asynchronously on the WinUI dispatcher, not inside the
    /// click's window message, so by the time we run our process often no longer holds the
    /// "received the last input event" right Windows requires to grant a foreground change.
    /// Windows then refuses the steal and merely flashes/highlights the taskbar button — the
    /// "selected but not in front" symptom.</para>
    ///
    /// <para>This combines the techniques that together defeat the foreground lock:
    /// (1) <c>AttachThreadInput</c> to the current foreground thread so it "agrees" to yield
    /// focus; (2) a <c>HWND_TOPMOST → HWND_NOTOPMOST</c> toggle plus <c>BringWindowToTop</c>
    /// that force z-order to the very front even if the focus steal is denied;
    /// (3) <c>SetForegroundWindow</c> for the actual activation + keyboard focus.</para>
    /// </summary>
    private static void ForceWindowToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var foreground = Helpers.NativeInterop.GetForegroundWindow();
        if (foreground == hwnd) return; // already in front

        var foregroundThread = Helpers.NativeInterop.GetWindowThreadProcessId(foreground, out _);
        var currentThread = Helpers.NativeInterop.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = Helpers.NativeInterop.AttachThreadInput(currentThread, foregroundThread, true);
            }

            // Force z-order to the top even when the focus steal is denied. SWP_NOMOVE/NOSIZE
            // keep the window's position and size; the topmost flag is immediately removed so the
            // window doesn't stay pinned above everything else.
            const uint zOrderFlags = Helpers.NativeInterop.SWP_NOMOVE
                | Helpers.NativeInterop.SWP_NOSIZE
                | Helpers.NativeInterop.SWP_SHOWWINDOW;
            Helpers.NativeInterop.SetWindowPos(hwnd, Helpers.NativeInterop.HWND_TOPMOST, 0, 0, 0, 0, zOrderFlags);
            Helpers.NativeInterop.SetWindowPos(hwnd, Helpers.NativeInterop.HWND_NOTOPMOST, 0, 0, 0, 0, zOrderFlags);

            Helpers.NativeInterop.BringWindowToTop(hwnd);
            Helpers.NativeInterop.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Helpers.NativeInterop.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private void StartTranscriptionCleanupTimer()
    {
        StopTranscriptionCleanupTimer();

        _transcriptionCleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        _transcriptionCleanupTimer.Tick += OnTranscriptionCleanupTimerTick;
        _transcriptionCleanupTimer.Start();
    }

    private void StopTranscriptionCleanupTimer()
    {
        if (_transcriptionCleanupTimer == null)
            return;

        _transcriptionCleanupTimer.Tick -= OnTranscriptionCleanupTimerTick;
        _transcriptionCleanupTimer.Stop();
        _transcriptionCleanupTimer = null;
    }

    private async void OnTranscriptionCleanupTimerTick(object? sender, object e)
    {
        await RunTranscriptionCleanupAsync("Periodic");
    }

    private async Task RunTranscriptionCleanupAsync(string source)
    {
        // F19: a cleanup pass mutates the same SQLite/media an erasure deletes and an update
        // relaunch expects. Acquire the gate's cleanup lease FIRST — refused while an erasure or
        // update apply holds its exclusive lease, and held (inside this async body, across the
        // await) until the pass completes so an erasure's poll drains it. Skip-and-log if refused.
        var gate = Services.GetService(typeof(Services.Maintenance.IMaintenanceGate))
            as Services.Maintenance.IMaintenanceGate;
        IDisposable? lease = null;
        if (gate is not null && !gate.TryBeginCleanupPass(out lease))
        {
            Log.Information("{Source} transcription cleanup skipped — exclusive maintenance in progress", source);
            return;
        }
        try
        {
            await Services.GetRequiredService<TranscriptionCleanupService>().CleanupAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Source} transcription cleanup failed", source);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task InitializeLifetimeMetricsAndRunStartupCleanupAsync()
    {
        try
        {
            await Services.GetRequiredService<LifetimeMetricsService>().InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lifetime metrics initialization failed");
        }

        await RunTranscriptionCleanupAsync("Startup");
    }

    /// <summary>
    /// Load prompt hotkey bindings from saved prompts and register them with HotkeyService.
    /// </summary>
    internal void RegisterPromptHotkeys()
    {
        if (_hotkeyService == null) return;
        var enhancement = Services.GetRequiredService<AIEnhancementService>();
        var bindings = enhancement.GetPrompts()
            .Where(p => !string.IsNullOrEmpty(p.Hotkey))
            .Select(p => (p.Id, p.Hotkey!));
        _hotkeyService.RegisterPromptHotkeys(bindings);
    }

    // Quit-during-processing guard: tray Quit while a transcription/enhancement is in flight
    // would silently discard the user's dictation — Cleanup()'s bounded Whisper dispose (F5)
    // gives up after 5 s and the Closed handler's Environment.Exit(0) then kills the native
    // inference mid-run, so nothing is ever pasted or written to history. Pre-F5 the dispose
    // waited indefinitely (removed because a blocking wait on the UI thread can deadlock);
    // awaiting HERE, before Close(), preserves the result without that risk — the pump keeps
    // running, the pipeline finishes and pastes, then quit proceeds. Bounded so a wedged
    // pipeline can never make Quit hang; active RECORDING is deliberately not waited on
    // (quitting mid-recording is the user abandoning it); a second Quit click skips the wait.
    private static readonly TimeSpan QuitTranscriptionWaitBound = TimeSpan.FromSeconds(60);
    private bool _quitWaitInProgress;

    // IMG-BG: bound for cancelling a job that is still in its CANCELLABLE section
    // (generation aborts in ms; this is a safety net). An IN-FLIGHT COMMIT is waited out
    // with no automatic cap (IMG-4b: via the per-commit signal — post-latch no new commit
    // can begin, so the uncapped wait covers exactly one persistence; N=1 keeps its whole
    // persist+paste tail inside that wait), after which the residual cancellable drain
    // gets a FRESH copy of this bound. Only the user's explicit second Quit click
    // abandons a commit; the accepted consequence of that skip is a saved image without
    // its History row, identical to the pre-existing crash window.
    private static readonly TimeSpan QuitImageJobGenerationBound = TimeSpan.FromSeconds(5);
    private TaskCompletionSource? _quitImageJobSkip;

    /// <summary>
    /// Latch + cancel + drain the background image job for quit. Returns true when this call
    /// owns the quit flow (drain finished or was abandoned at the generation bound); false on
    /// a SECOND Quit click, which only signals the skip — the first flow proceeds to Close.
    /// Runs on the dispatcher (tray handler), so the skip TCS needs no synchronization.
    /// </summary>
    private async Task<bool> DrainImageJobBeforeQuitAsync()
    {
        var jobService = _imageJobService;
        if (jobService == null)
            return true;

        if (_quitImageJobSkip != null)
        {
            // Second Quit click while the first drain waits: the explicit abandon signal.
            _quitImageJobSkip.TrySetResult();
            return false;
        }

        // Always latch — even with no job running, admission must seal so a new image job
        // can't START during the transcription wait below.
        _quitImageJobSkip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = jobService.ShutdownAsync(QuitImageJobGenerationBound);
        if (!drain.IsCompleted)
            Log.Information("Quit deferred: cancelling background image generation");

        var winner = await Task.WhenAny(drain, _quitImageJobSkip.Task);
        if (winner != drain)
            Log.Warning("Quit abandoned the image-job drain on second click — a saved image may lack its History row until reconciled");
        else if (!await drain)
            Log.Warning("Image-job drain abandoned at the generation bound (job unresponsive to cancel)");

        // Reset so LATER Quit clicks behave as fresh quits and still reach the transcription
        // wait's own second-click skip — a lingering TCS would swallow every subsequent click
        // (Codex diff review round 1). The admission latch is permanent regardless.
        _quitImageJobSkip = null;
        return true;
    }

    private async Task WaitForTranscriptionBeforeQuitAsync(MainViewModel viewModel)
    {
        if (_quitWaitInProgress)
        {
            // Second Quit click while the first is waiting: the user wants out now.
            _quitWaitInProgress = false;
            return;
        }
        if (viewModel.RecordingState is not (RecordingState.Transcribing or RecordingState.Enhancing))
            return;

        _quitWaitInProgress = true;
        Log.Information("Quit deferred: transcription in flight — waiting up to {Bound}s for it to finish",
            QuitTranscriptionWaitBound.TotalSeconds);
        var deadline = DateTimeOffset.UtcNow + QuitTranscriptionWaitBound;
        // Both Quit handlers run on the dispatcher, so the flag needs no synchronization: a
        // second click clears it between our awaits and the next condition check exits the loop.
        while (_quitWaitInProgress
               && viewModel.RecordingState is RecordingState.Transcribing or RecordingState.Enhancing
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        if (viewModel.RecordingState is RecordingState.Transcribing or RecordingState.Enhancing)
            Log.Warning("Quit proceeding with the pipeline still busy (bound reached or second click)");
        _quitWaitInProgress = false;
    }

    /// System tray icon setup.
    /// Uses H.NotifyIcon.WinUI with PopupActivation mode (not SecondWindow — known buggy).
    /// </summary>
    private void SetupTrayIcon(MainViewModel viewModel)
    {
        try
        {
            _trayIcon = new H.NotifyIcon.TaskbarIcon();
            _trayIcon.ToolTipText = "VoiceWink";

            // Load tray icon from Assets
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "trayicon.ico");
            if (File.Exists(iconPath))
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                Log.Debug("Tray icon loaded from {Path}", iconPath);
            }

            // Click or double-click tray icon to restore window
            _trayIcon.LeftClickCommand = new RelayCommand(RestoreMainWindow);
            _trayIcon.DoubleClickCommand = new RelayCommand(RestoreMainWindow);

            // Menu IDs for native popup menu
            const nuint ID_SHOW = 1;
            const nuint ID_RECORD = 2;
            const nuint ID_QUIT = 3;
            const nuint ID_GENERATE_IMAGE = 4;
            const nuint ID_CANCEL_IMAGE = 5;
            const nuint ID_SHOW_PILL = 6;

            // Right-click shows a native Win32 popup menu at the cursor.
            // This avoids WinUI flyout issues with multi-monitor setups — the native menu
            // takes screen coordinates directly and doesn't depend on the main window position.
            _trayIcon.RightClickCommand = new RelayCommand(() =>
            {
                if (!VoiceWink.Helpers.NativeInterop.GetCursorPos(out var pt))
                    return;

                var hMenu = VoiceWink.Helpers.NativeInterop.CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                try
                {
                    VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_SHOW, "Show");
                    VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_RECORD, "Speak");
                    VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_GENERATE_IMAGE, "New image");
                    // IMG-BG: the menu is rebuilt on every right-click, so the conditional item
                    // reads the live job state — no dynamic-menu plumbing needed.
                    if (_imageJobService?.IsRunning == true)
                        VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_CANCEL_IMAGE, "Cancel image");
                    // Right-click hide (2026-07-30): the restore path for a pill the user hid while a
                    // long generation/enhancement finishes. Same rebuilt-per-click liveness as above.
                    if (_pillController?.IsUserHidden == true)
                        VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_SHOW_PILL, "Show mini recorder");
                    VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_SEPARATOR, 0, null);
                    VoiceWink.Helpers.NativeInterop.AppendMenu(hMenu, Helpers.NativeInterop.MF_STRING, ID_QUIT, "Quit");

                    // SetForegroundWindow is required for TrackPopupMenuEx to dismiss on click-away
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                    VoiceWink.Helpers.NativeInterop.SetForegroundWindow(hwnd);

                    var cmd = VoiceWink.Helpers.NativeInterop.TrackPopupMenuEx(
                        hMenu,
                        Helpers.NativeInterop.TPM_RETURNCMD | Helpers.NativeInterop.TPM_NONOTIFY | Helpers.NativeInterop.TPM_BOTTOMALIGN,
                        pt.X, pt.Y, hwnd, IntPtr.Zero);

                    // Dispatch the selected action on the UI thread
                    if (cmd > 0)
                    {
                        _mainWindow?.DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                switch ((nuint)cmd)
                                {
                                    case ID_SHOW:
                                        RestoreMainWindow();
                                        break;
                                    case ID_RECORD:
                                        await viewModel.ToggleRecordAsync();
                                        break;
                                    case ID_GENERATE_IMAGE:
                                        // IMG-1: text-first image generation from the tray
                                        viewModel.RequestNewImageGeneration();
                                        break;
                                    case ID_CANCEL_IMAGE:
                                        viewModel.CancelImageJob();
                                        break;
                                    case ID_SHOW_PILL:
                                        if (_pillController?.ShowHidden() == true)
                                            Log.Information("MiniRecorder pill restored from tray");
                                        break;
                                    case ID_QUIT:
                                        Log.Information("Quit requested via tray");
                                        // IMG-BG: latch + cancel + drain the background image
                                        // job FIRST. A second Quit click is the explicit skip
                                        // signal; the FIRST flow then owns the Close.
                                        if (!await DrainImageJobBeforeQuitAsync())
                                            break;
                                        await WaitForTranscriptionBeforeQuitAsync(viewModel);
                                        _isQuitting = true;
                                        _mainWindow?.Close();
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error handling tray menu action");
                            }
                        });
                    }
                }
                finally
                {
                    VoiceWink.Helpers.NativeInterop.DestroyMenu(hMenu);
                }
            });

            // ForceCreate registers the icon with the Windows shell notification area.
            // Without this call the icon never appears in the system tray.
            _trayIcon.ForceCreate();
            // Only flip "ready" AFTER ForceCreate succeeds — otherwise a thrown
            // ForceCreate would leave _trayIcon non-null pointing at an icon that
            // was never registered, and hide-to-tray would strand the app.
            _trayIconReady = true;

            Log.Information("System tray icon set up");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set up tray icon");
            // Tray creation failed — discard the half-built icon so the readiness
            // check stays definitively false. The app continues without a tray
            // icon; the user can close the window via Alt+F4 or the title bar X
            // (which now exits cleanly because _trayIconReady is false).
            try { _trayIcon?.Dispose(); } catch { /* best-effort */ }
            _trayIcon = null;
            _trayIconReady = false;
        }
    }

    private static void ApplyNativeMenuTheme()
    {
        var mode = AppTheme.IsDark
            ? Helpers.NativeInterop.AppMode_ForceDark
            : Helpers.NativeInterop.AppMode_ForceLight;
        Helpers.NativeInterop.TrySetPreferredAppMode(mode);
        Helpers.NativeInterop.TryFlushMenuThemes();
    }

    /// <summary>
    /// <see cref="Services.IAppLifetime.RequestQuitForUpdate"/> — the update-apply spelling of
    /// <see cref="RequestQuit"/>. Kept so the update path is untouched by TRN-59.
    /// </summary>
    public void RequestQuitForUpdate() => RequestQuit(global::VoiceWink.Services.GracefulQuitReason.UpdateApply);

    /// <summary>
    /// <see cref="Services.IAppLifetime.RequestQuit"/> — marshal a graceful quit onto the UI
    /// dispatcher and reuse the normal quit sequence, so a staged Velopack update applies on exit —
    /// or, for a TRN-59 self-restart, so the successor waiting on this process's exit finds every
    /// process-scoped resource released. ONE sequence for every reason; only the log line names it.
    /// Idempotent (the <c>_isQuitting</c> guard) and safe to call from a background path.
    /// </summary>
    public void RequestQuit(global::VoiceWink.Services.GracefulQuitReason reason)
    {
        var win = _mainWindow;
        if (win is null)
        {
            // No main window (every caller is user-triggered post-launch, so this shouldn't happen).
            Log.Information("Graceful quit ({Reason}) requested before main window — flushing durable state then exiting", reason);
            FlushDurableStateBeforeForcedExit();
            Environment.Exit(0);
            return;
        }

        if (!win.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isQuitting) return; // a quit is already underway — idempotent
            Log.Information("Graceful quit requested ({Reason})", reason);
            _isQuitting = true;
            win.Close(); // → Closed handler → Cleanup() → Environment.Exit(0)
        }))
        {
            // Dispatcher is dead/shutting down, so the normal Closed → Cleanup() path can't run.
            Log.Warning("Graceful quit ({Reason}): dispatcher enqueue failed — flushing durable state then exiting", reason);
            FlushDurableStateBeforeForcedExit();
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Narrow, best-effort flush for the two degenerate <see cref="RequestQuitForUpdate"/> paths (no
    /// main window / dead dispatcher) where the normal <c>Closed → Cleanup()</c> path can't run before
    /// a Velopack binary swap. The COMMON apply path is unaffected: when the UI thread is alive, the
    /// enqueued <c>win.Close()</c> runs full <see cref="Cleanup"/> on the UI thread (intentional — that
    /// is the graceful shutdown). This helper deliberately does NOT call <see cref="Cleanup"/>: hotkey
    /// unhook, tray-icon dispose and MiniRecorder teardown are UI-thread-affine and could throw — or
    /// worse, hang (a hang wouldn't be caught here) — when invoked off the UI thread or against a dead
    /// dispatcher.
    ///
    /// <para>What actually needs flushing here is small: settings persist on every write, and in the
    /// app's default SQLite journal mode (DELETE — nothing ever sets <c>PRAGMA journal_mode=WAL</c>)
    /// committed rows are already in the main db file. So the meaningful work is the telemetry flush
    /// (Sentry + Serilog); the WAL checkpoint below is a no-op today, kept only as cheap insurance if
    /// WAL is ever enabled. Each step is wrapped in its own try/catch so a THROWING step can't stop the
    /// others or the exit — but this is best-effort, not time-bounded: a hung Sentry/Serilog flush
    /// would still stall here (Velopack's ≤60s exit timeout is the ultimate backstop).</para>
    ///
    /// <para>One residual gap, accepted: if the UI thread is alive but WEDGED (a long synchronous
    /// Win32/COM call), <c>TryEnqueue</c> returns true yet the queued <c>Close()</c> never pumps, so
    /// neither this flush nor <see cref="Cleanup"/> runs. Velopack's <c>WaitExitThenApplyUpdates</c>
    /// (≤60s wait for this process to exit) is the backstop for that case.</para>
    /// </summary>
    private static void FlushDurableStateBeforeForcedExit()
    {
        try
        {
            if (Services?.GetService(typeof(IDbContextFactory<VoiceWinkDbContext>))
                is IDbContextFactory<VoiceWinkDbContext> dbFactory)
            {
                using var db = dbFactory.CreateDbContext();
                // No-op in the app's default DELETE journal mode (nothing sets PRAGMA journal_mode=WAL),
                // where committed rows already live in the main db file — kept as cheap, non-blocking
                // insurance if WAL is ever enabled. PASSIVE never waits on a busy writer, so even then
                // it returns immediately and can't deadlock this forced-exit path.
                db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(PASSIVE);");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FlushDurableStateBeforeForcedExit: WAL checkpoint failed (continuing to exit)");
        }

        // REL-12: drain failure-retained retry audio directly via the ledger — non-UI
        // (pure file IO, no observables, no DispatcherTimer), so it is safe on these
        // degenerate off-UI-thread exit paths where full Cleanup() can't run. REL-17:
        // the drain honors the keep-recordings setting (move to Debug, not delete).
        try
        {
            (Services?.GetService(typeof(Helpers.RetainedWavLedger))
                as Helpers.RetainedWavLedger)?.DrainBestEffort(
                    ReadKeepRecordingsForDebugSafe(), AppPaths.RecordingsDebugDir);
        }
        catch { /* best-effort */ }

        try { SentryInitializer.Shutdown(); } catch { /* best-effort */ }
        try { Log.CloseAndFlush(); } catch { /* best-effort */ }
    }

    /// <summary>REL-17: read the keep-recordings debug setting for the exit drains —
    /// null-safe and fail-soft (both drain sites run on degenerate exit paths where DI
    /// may be partially torn down; absence means today's delete behavior).</summary>
    private static bool ReadKeepRecordingsForDebugSafe()
    {
        try
        {
            return (Services?.GetService(typeof(SettingsService)) as SettingsService)
                ?.GetBool(AppDefaults.KeepRecordingsForDebug, false) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True while an EXCLUSIVE maintenance operation — an update apply OR a data erasure (F19,
    /// 2026-07-14) — is in progress. Start-paths (recording, model download, file transcription,
    /// redo / new-image generation) call this to refuse new work during either, so nothing starts
    /// between the operation's final gate re-check and process exit. Resolves the gate lazily +
    /// null-safely so any subsystem can call it without DI plumbing.
    /// </summary>
    internal static bool IsExclusiveMaintenanceActive()
    {
        var gate = Services?.GetService(typeof(Services.Maintenance.IMaintenanceGate))
            as Services.Maintenance.IMaintenanceGate;
        return gate is { IsApplyingUpdate: true } || gate is { IsErasing: true };
    }

    private void Cleanup()
    {
        Log.Information("VoiceWink shutting down");

        // IMG-BG belt-and-braces: the tray Quit path already latched + drained the job
        // (DrainImageJobBeforeQuitAsync); other exit paths (window close, update quit) at
        // least cancel it here — DI container dispose order across ~50 singletons is
        // unspecified, so don't rely on it for the cancel.
        try { _imageJobService?.Cancel(); } catch { /* best-effort on exit */ }

        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        // AUD-6: close the standing microphone explicitly and EARLY — DI dispose order across
        // ~50 singletons is unspecified (same reasoning as the image-job cancel above), and the
        // mic-in-use indicator should drop the moment quit begins, not whenever the container
        // gets around to it. Idempotent; a recording mid-drain has already ended (or the second
        // Quit click abandoned it) by the time Cleanup runs. Null before the gated services
        // started ⇒ the singleton was never created ⇒ nothing to close.
        try { _standingCaptureService?.Dispose(); }
        catch (Exception ex) { Log.Debug(ex, "Standing capture dispose failed during cleanup"); }
        ShutdownMiniRecorder();
        StopTranscriptionCleanupTimer();
        // UPD-1b/3b/4b: unsubscribe every update handler, stop the background poll, and cancel the
        // auto-install retry loop — in that order — before the container is disposed. The order is
        // the coordinator's own invariant now, not something each caller has to remember.
        _updateRuntime?.Stop();
        // LIC-23: stop the daily licence check before the container is disposed — a tick landing
        // on a disposed LicenseService would only log, but the loop must not outlive the app.
        _licenseRevalidation?.Stop();
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();

        // TRN-49: stop the GPU warm-up before the engines it warms are torn down — a warm
        // Whisper decode otherwise holds the model lock into WhisperTranscriptionService.Dispose
        // (a 5 s stall + skipped native disposal on an exit-during-first-launch), and the warm
        // Parakeet child's teardown kills-and-confirms on cancellation (self-review, concurrency
        // lens). Same cancel as admission: session-permanent, exception-contained, reason logged.
        GpuWarmup.Instance.Cancel(GpuWarmupCancelReason.Shutdown);

        // TRN-29 slice 4: explicit kill of the resident parakeet-server (the kill-on-close job
        // object is the app-DEATH backstop, not the primary path). Null in every flag-off build.
        // Sync-over-async is fine on this exit path — Cleanup already performs bounded waits.
        //
        // TRN-39 + TRN-67: EVERY wait on this call is now bounded, and that took two cards. The
        // coordinator's state capture takes ParakeetBackendCoordinator.ShutdownStateWait, the
        // server manager's gate takes ParakeetServerPolicy.ShutdownGateWait, and the child kill
        // takes ParakeetServerPolicy.RetireWait. Before TRN-39 this comment read "Bounded by the
        // retire wait inside", which named only the LAST of the three; TRN-39 bounded the first
        // and said the server gate was still untimed; TRN-67 closed that one. A miss at either
        // gate skips work, never blocks: the job object still terminates the child at process
        // exit, which is the same class of kill Kill() performs.
        try
        {
            _pcppBackend?.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { Log.Debug(ex, "parakeet-server shutdown failed during cleanup"); }

        // REL-12: deterministically settle failure-retained retry audio on normal exit
        // (armed slot + any unconfirmed pending deletes — the ledger tracks them all).
        // Resolved from DI directly: _miniRecorderViewModel is already null here
        // (ShutdownMiniRecorder above clears it), and the drain is pure file IO — no
        // observables, no timers. Best-effort — a WAV held open by an in-flight retry
        // falls to the 7-day recordings sweep. REL-17: honors the keep-recordings
        // setting (move to Debug, not delete — the 7-day promise survives exit).
        try
        {
            (Services?.GetService(typeof(Helpers.RetainedWavLedger))
                as Helpers.RetainedWavLedger)?.DrainBestEffort(
                    ReadKeepRecordingsForDebugSafe(), AppPaths.RecordingsDebugDir);
        }
        catch (Exception ex) { Log.Warning(ex, "Retained retry audio drain failed at shutdown"); }

        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstanceMutex?.Dispose();

        // Teardown symmetry for the Start() above: stop the re-pin watchdog before the
        // log sink closes, so its diagnostic flush can't race CloseAndFlush. Bounded
        // join; the thread is IsBackground, so a timeout is survivable either way.
        try { Helpers.ProcessPriorityTuner.Stop(); } catch { /* best-effort */ }

        SentryInitializer.Shutdown();
        Log.CloseAndFlush();
    }
}
