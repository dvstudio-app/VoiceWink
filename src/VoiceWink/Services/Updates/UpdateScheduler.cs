using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Background scheduler that drives the automatic update poll (UPD-1b). See
/// <see cref="IUpdateScheduler"/> for the contract and <see cref="UpdateScheduleDecision"/>
/// for the cadence rules.
///
/// <para><b>Loop shape.</b> A single re-armed <c>Task.Delay</c> loop (not a
/// <c>DispatcherTimer</c>) — testable via an injected delay seam and decoupled from the
/// WinUI dispatcher lifecycle. Each session: wait <c>InitialDelay</c> (~30s after start) →
/// tick → then wait <c>Interval</c> (24h) → tick → … (see <see cref="UpdateScheduleDecision"/>).
/// The first check fires on <i>every</i> launch — there's no cross-restart suppression. The
/// tick is fully wrapped so a thrown check (e.g. a 502 from the CDN) can never tear down the
/// loop or crash a background thread.</para>
///
/// <para><b>Concurrency.</b> <see cref="Start"/> / <see cref="Stop"/> /
/// <see cref="OnAutomaticSettingChanged"/> are serialized under <c>_lifecycleLock</c> and
/// gated by <c>_started</c>, so the toggle can never start polling before the gated
/// <see cref="Start"/> nor spawn a duplicate loop. A monotonic <c>_generation</c> stamp
/// makes a superseded loop exit at its next checkpoint even if cancellation hasn't yet
/// propagated, so at most one loop is ever effectively live.</para>
/// </summary>
public sealed class UpdateScheduler : IUpdateScheduler, IDisposable
{
    private static ILogger Logger => Log.ForContext<UpdateScheduler>();

    private readonly IUpdateService _updates;
    private readonly SettingsService _settings;
    private readonly bool _featureEnabled;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<bool> _applyingProbe;
    private readonly IInstallSourceReporter? _installSource;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private bool _started;
    private int _generation;

    // Last version announced via UpdateAvailableDetected — dedupes so the badge isn't
    // re-raised every 24h for a version the user has already seen.
    private string? _lastNotifiedVersion;

    public event Action<string>? UpdateAvailableDetected;
    public event Action<string>? UpdateReadyToInstall;
    public event Action? UpdateNoLongerAvailable;

    public UpdateScheduler(IUpdateService updates, SettingsService settings, IInstallSourceReporter installSource)
        : this(
            updates,
            settings,
            UpdateCheckFeature.IsEnabled,
            static (d, ct) => Task.Delay(d, ct),
            App.IsExclusiveMaintenanceActive,
            installSource)
    {
    }

    // Internal test seam (InternalsVisibleTo VoiceWink.Tests). Lets tests exercise the
    // enabled-scheduler behavior even though UpdateCheckFeature.IsEnabled is compiled false
    // in test/dev builds, and inject a controllable delay + applying probe.
    internal UpdateScheduler(
        IUpdateService updates,
        SettingsService settings,
        bool featureEnabled,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<bool> applyingProbe,
        IInstallSourceReporter? installSource = null)
    {
        _updates = updates;
        _settings = settings;
        _featureEnabled = featureEnabled;
        _delay = delay;
        _applyingProbe = applyingProbe;
        _installSource = installSource;
    }

    public void Start()
    {
        if (!_featureEnabled) return; // dev/CI: inert — no loop, no timer, no wake.
        lock (_lifecycleLock)
        {
            if (_started) return; // idempotent
            _started = true;
            // Only arm the loop if automatic checks are currently ON. Launching with the
            // toggle OFF must NOT start a loop at all — it's armed later by
            // OnAutomaticSettingChanged(true) when the user enables automatic checks.
            if (AutomaticEnabled()) StartLoopLocked();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_started) return;
            _started = false;
            CancelLoopLocked();
        }
    }

    public void OnAutomaticSettingChanged(bool enabled)
    {
        if (!_featureEnabled) return;
        lock (_lifecycleLock)
        {
            // No-op until the gated Start() has run — a toggle flip during onboarding must
            // not begin network egress ahead of the legal gate.
            if (!_started) return;
            if (enabled)
                // Arm / re-arm: a fresh loop (first check ~30s later, then every 24h).
                StartLoopLocked();
            else
                // Unarm but stay started: cancel the loop entirely so we don't wake at all
                // while automatic checks are off (no initial-delay busy-wait). Re-armed on the next
                // OnAutomaticSettingChanged(true). The per-tick toggle re-read stays as a
                // belt-and-suspenders guard for the brief arm→tick window.
                CancelLoopLocked();
        }
    }

    // GetBoolDefaulted, not GetBool with a literal `true`: the shipped default lives in
    // AppDefaults.Defaults, and a literal here would disagree with the other readers on a corrupt
    // or hand-edited settings file — a malformed value would make the UI render the toggle ON
    // while this method treated it as OFF (UPD-4b sweep).
    private bool AutomaticEnabled() => _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);

    // Must be called under _lifecycleLock. Cancels any running loop, then launches a new
    // one stamped with a fresh generation.
    private void StartLoopLocked()
    {
        CancelLoopLocked();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var generation = ++_generation;
        _ = Task.Run(() => RunLoopAsync(generation, ct), ct);
    }

    // Must be called under _lifecycleLock.
    private void CancelLoopLocked()
    {
        // Bump the generation so any in-flight loop observes it's stale and exits at its
        // next checkpoint, even before the token cancellation propagates.
        _generation++;
        if (_cts is { } cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
            cts.Dispose();
            _cts = null;
        }
    }

    private async Task RunLoopAsync(int generation, CancellationToken ct)
    {
        // First iteration of a session waits InitialDelay (~30s after start — so EVERY launch
        // re-checks); subsequent iterations wait Interval (24h) for long-running sessions.
        var isFirstCheck = true;
        try
        {
            while (!ct.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                var delay = UpdateScheduleDecision.NextDelay(isFirstCheck);
                isFirstCheck = false;
                await _delay(delay, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                    return;
                await TickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on Stop()/re-arm — nothing to do.
        }
        catch (Exception ex)
        {
            // Defense-in-depth: the loop body already swallows tick exceptions, so reaching
            // here means something outside the tick (e.g. the delay seam) faulted. Log and
            // let the loop end rather than crash a background thread.
            Logger.Warning(ex, "Update scheduler loop terminated unexpectedly");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            // Pointless (and slightly risky) to poll while an update is being applied — the
            // process is about to exit for the binary swap. Checked via BOTH surfaces (UPD-2):
            // the service's apply guard is won a few statements before the maintenance-gate
            // flag is attached, so a tick racing that window could otherwise slip through and
            // overwrite the source's cached UpdateInfo mid-apply; the service guard also covers
            // the abandoned-download state.
            if (_applyingProbe() || _updates.IsApplyInFlight) return;

            // Cheap pre-gate so a disabled toggle doesn't even reach the service. The service
            // re-checks AutomaticUpdateCheckEnabled itself (gate-2) as the authoritative guard.
            if (!AutomaticEnabled()) return;

            var result = await _updates.CheckForUpdatesAsync(isManualCheck: false, ct).ConfigureAwait(false);

            if (result.Outcome == UpdateCheckOutcome.UpdateAvailable
                && result.AvailableVersion is { Length: > 0 } version)
            {
                if (version != _lastNotifiedVersion)
                {
                    _lastNotifiedVersion = version;
                    // Contained INDIVIDUALLY (Codex diff r7): the dedupe memo is already set, so
                    // a throwing badge subscriber that escaped to the tick's outer catch would
                    // also skip the install raise below — deferring an automatic install a full
                    // tick because a BADGE handler misbehaved.
                    try { UpdateAvailableDetected?.Invoke(version); }
                    catch (Exception ex) { Logger.Warning(ex, "UpdateAvailableDetected subscriber threw"); }
                }

                // UPD-4b: un-deduped, and raised AFTER the badge channel so the sidebar dot is
                // already correct if the install refuses. Every tick that still sees an available
                // update gives the auto-installer a fresh attempt — a refusal (recording in
                // flight) must never be permanent for the process. Raised outside the dedupe
                // block deliberately; sharing the deduped event was the original design and the
                // defect this fixes. Subscriber exceptions are contained: this runs on the poll
                // loop, and a throwing handler must not kill the loop or the tick.
                try { UpdateReadyToInstall?.Invoke(version); }
                catch (Exception ex) { Logger.Warning(ex, "UpdateReadyToInstall subscriber threw"); }
            }
            else if (result.Outcome == UpdateCheckOutcome.UpToDate)
            {
                // The previously-pending version was applied or pulled. Allow a future
                // re-release of the same version string to re-notify, AND — if we had
                // announced an update — tell the UI so it can clear the now-stale badge
                // without waiting for the user to open the Updates page. Fire only on the
                // announced→cleared transition to avoid redundant clears every tick.
                if (_lastNotifiedVersion is not null)
                {
                    _lastNotifiedVersion = null;
                    UpdateNoLongerAvailable?.Invoke();
                }
            }

            // dvstudio-metrics #85 (Privacy §4.5): the one-time install-source report rides THIS
            // path — an automatic check, so the build flag, the toggle and the LGL-1 gate already
            // hold; a manual check never reaches it. It runs on every automatic tick: it classifies
            // the installation once whatever the check's outcome (the Store log rolls over within
            // days, so an offline first launch must still record it), and sends only when this
            // check just reached the server. Awaited after the update events, and contained, so it
            // can neither delay an install hand-off nor kill the tick.
            if (_installSource is { } installSource)
            {
                var networkProven = result.Outcome is UpdateCheckOutcome.UpToDate or UpdateCheckOutcome.UpdateAvailable;
                try { await installSource.RunAsync(networkProven, ct).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Warning(ex, "Install-source report threw"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate so RunLoopAsync exits cleanly
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Background update check tick failed");
        }
    }

    public void Dispose() => Stop();
}
