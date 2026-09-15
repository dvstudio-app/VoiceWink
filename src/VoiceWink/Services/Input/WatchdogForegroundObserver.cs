using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AppMode;

namespace VoiceWink.Services.Input;

/// <summary>
/// HKY-2: emits ONE diagnostic line per watchdog-triggered hook restart carrying the
/// foreground process name, PID, and elevation, so the next restart episode
/// self-diagnoses — HKY-1 elevated-focus noise (elevated=true on every fire) vs a
/// genuine LowLevelHooksTimeout hook death. Lives outside <see cref="HotkeyService"/>
/// because the hub must not grow responsibilities (AGENTS.md launch-freeze rule);
/// the hub only captures HWND→PID and hands the sample here.
///
/// <para><b>Line contract:</b> every <see cref="Publish"/> emits exactly one line;
/// no failure mode swallows it. PID always logs; a failed elevation probe logs
/// elevated=unknown; a slow/absent name lookup logs foregroundProc="unknown"; a
/// name lookup refused by the single-flight gate logs foregroundProc="skipped".</para>
///
/// <para><b>Worker bound:</b> at most ONE process-name worker is ever alive.
/// <see cref="Helpers.BoundedComCall"/> is deliberately NOT used here — its timeout
/// abandons the wait but lets the worker run on, so gating on the bounded wait would
/// admit one fresh worker per watchdog fire while a wedged
/// <c>Process.GetProcessById</c> accumulates them (Codex R2). Instead this class owns
/// the raw lookup task: a bounded wait only decides when to log "unknown", and the
/// single-flight slot is released exclusively by the raw task's completion
/// continuation (which also observes its fault).</para>
///
/// <para><b>Privacy:</b> the process name is PII-adjacent (an installed-app name,
/// REL-16 precedent) and is logged under the redacted
/// <c>{WatchdogForegroundProcess}</c> property inside the quoted
/// <c>foregroundProc="…"</c> token, so Sentry breadcrumbs, the Support zip, and the
/// GDPR log export all scrub it while the local file keeps it.</para>
/// </summary>
internal sealed class WatchdogForegroundObserver
{
    /// <summary>The documented recommendation for a UI-adjacent process-name lookup
    /// (see <c>AppModeManager.DetectActiveAppModeAsync</c>). Here nothing user-visible
    /// waits on it — the budget only bounds how long the LINE waits for a name.</summary>
    internal static readonly TimeSpan DefaultNameBudget = TimeSpan.FromMilliseconds(250);

    private readonly IActiveWindowService? _nameSource;
    private readonly Func<uint, bool?> _tryGetElevation;
    private readonly TimeSpan _nameBudget;

    /// <summary>1 while a raw name-lookup worker is alive (not merely awaited). Released
    /// ONLY by the worker's completion continuation — never by the bounded wait.</summary>
    private int _nameLookupInFlight;

    public WatchdogForegroundObserver(IActiveWindowService nameSource)
        : this(nameSource, NativeInterop.TryGetProcessElevation, DefaultNameBudget)
    {
    }

    internal WatchdogForegroundObserver(
        IActiveWindowService? nameSource,
        Func<uint, bool?> tryGetElevation,
        TimeSpan nameBudget)
    {
        _nameSource = nameSource;
        _tryGetElevation = tryGetElevation;
        _nameBudget = nameBudget;
    }

    // Dynamic on purpose: ClearAndReinitializeLogs replaces Log.Logger, so a stored
    // ILogger would keep writing to the closed sink (HotkeyService's own pattern).
    private static ILogger Logger => Log.ForContext<WatchdogForegroundObserver>();

    /// <summary>Fire-and-forget entry for the watchdog tick (UI thread). The WHOLE
    /// pipeline is scheduled onto the thread pool — an async method runs inline until
    /// its first incomplete await, and the Pid-0 path has none, so without Task.Run the
    /// log line (and the file sink behind it) would execute synchronously on the
    /// watchdog's UI thread (Codex diff review). Exceptions are observed inside
    /// <see cref="PublishAsync"/>; the returned task can never fault.</summary>
    public void Publish(WatchdogForegroundSample sample)
        => _ = Task.Run(() => PublishAsync(sample));

    /// <summary>Awaitable body — internal so tests can await the pipeline.</summary>
    internal async Task PublishAsync(WatchdogForegroundSample sample)
    {
        try
        {
            await RunAsync(sample).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { Logger.Debug(ex, "Watchdog foreground observer failed"); }
            catch (Exception) { /* observability must never throw into the hook path */ }
        }
    }

    /// <summary>Test seam: true while a raw name-lookup worker is alive.</summary>
    internal bool NameLookupInFlight => Volatile.Read(ref _nameLookupInFlight) != 0;

    private async Task RunAsync(WatchdogForegroundSample sample)
    {
        // Already off the UI thread — Publish schedules the whole pipeline via Task.Run
        // (the test entry PublishAsync runs on the test's own thread, deterministically).
        bool? elevated = null;
        if (sample.Pid != 0)
        {
            try
            {
                elevated = _tryGetElevation(sample.Pid);
            }
            catch (Exception)
            {
                // Probe failure is "unknown", never a lost line — pid still logs below.
            }
        }

        var processDisplay = await ResolveNameDisplayAsync(sample.Pid).ConfigureAwait(false);

        Logger.Information(
            "Watchdog foreground at restart request: foregroundProc=\"{WatchdogForegroundProcess}\" pid={Pid} elevated={Elevated} sampledAt={SampledAtUtc:O}",
            processDisplay, sample.Pid, WatchdogForegroundProbe.DescribeElevation(elevated), sample.SampleUtc);
    }

    private async Task<string> ResolveNameDisplayAsync(uint pid)
    {
        if (pid == 0 || _nameSource == null) return "unknown";

        // Single-flight: a wedged worker keeps the slot; this fire's line still emits.
        if (Interlocked.CompareExchange(ref _nameLookupInFlight, 1, 0) != 0) return "skipped";

        var nameSource = _nameSource;
        var raw = Task.Run(() => nameSource.GetProcessNameById((int)pid));
        _ = raw.ContinueWith(
            t =>
            {
                _ = t.Exception; // observe a late fault so it never reaches the finalizer
                Interlocked.Exchange(ref _nameLookupInFlight, 0);
            },
            TaskScheduler.Default);

        try
        {
            return await raw.WaitAsync(_nameBudget).ConfigureAwait(false) ?? "unknown";
        }
        catch (TimeoutException)
        {
            return "unknown"; // slot stays held until the raw worker actually finishes
        }
        catch (Exception)
        {
            return "unknown"; // raw fault — also observed by the release continuation
        }
    }
}
