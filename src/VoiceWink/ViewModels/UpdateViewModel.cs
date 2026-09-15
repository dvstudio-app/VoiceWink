using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;
using VoiceWink.Services.Updates;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for the Settings → Updates page. Wraps <see cref="IUpdateService"/>
/// state into observable properties + a single user-facing command
/// ("Check for updates"). Registered as transient in DI so each
/// navigation re-reads the current LastCheckedUtc + AutomaticUpdate-
/// CheckEnabled values from settings on construction.
///
/// <para>The "Check for updates automatically" toggle is two-way bound
/// to <see cref="AppDefaults.AutomaticUpdateCheckEnabled"/> via the
/// source-generated <c>OnAutomaticUpdateCheckEnabledChanged</c> partial
/// hook. Constructor seeding assigns the backing field directly
/// (<c>_automaticUpdateCheckEnabled = …</c>) so opening the page doesn't
/// fire the partial — matching <see cref="SettingsViewModel"/>'s pattern.
/// This matters because the partial PERSISTS to settings, and persisting
/// on every page navigation would (a) waste disk I/O, and (b) silently
/// clobber any external change to the setting between page opens once
/// the build flag flips and other code paths start writing it.</para>
///
/// <para>The page itself is built in code-behind per the project-wide
/// "All UI in code-behind" constraint — see
/// <see cref="VoiceWink.Views.Pages.UpdatesPage"/>.</para>
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<UpdateViewModel>();

    private readonly IUpdateService _updates;
    private readonly SettingsService _settings;
    private readonly IUpdateScheduler _scheduler;
    // UPD-4b: lets a toggle opt-out cancel a RUNNING automatic apply immediately instead of at
    // the installer's next 5 s poll (Codex diff r4). Optional so existing three-argument test
    // constructions keep compiling; null simply means no immediate path (the poll still covers).
    private readonly Services.Updates.AutoUpdateInstaller? _installer;

    // UPD-2: service apply-state events fire on thread-pool threads; property writes must land
    // on the UI thread. Captured in the ctor (pages construct the VM on the UI thread). Null in
    // unit tests (no WinUI pump) — handlers then run inline, which is what the tests want.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    /// <summary>Version string of the running build (X.Y.Z). Static for the process lifetime.</summary>
    [ObservableProperty] private string _currentVersion = "";

    /// <summary>
    /// Last-attempted check timestamp (UTC), or null if no check has run in
    /// this profile yet. The page renders this via <see cref="LastCheckedDisplay"/>
    /// rather than reading the DateTime directly so the formatting logic
    /// stays in one place.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedDisplay))]
    private DateTime? _lastCheckedUtc;

    /// <summary>True while <see cref="ApplyAndRestartCommand"/> is in flight (download + apply).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAndRestartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isApplying;

    /// <summary>
    /// Apply-specific status line: the maintenance-block reason ("Can't update while recording..."),
    /// an apply error, or "restarting...". Empty until an apply is attempted. Separate from
    /// <see cref="StatusText"/> (the check result) so both can show at once.
    /// </summary>
    [ObservableProperty] private string _applyStatus = "";

    /// <summary>
    /// Two-way-bound to the <see cref="AppDefaults.AutomaticUpdateCheckEnabled"/>
    /// setting. The user-facing label is "Check for updates automatically".
    /// Note this controls ONLY the scheduler path (<see cref="IUpdateScheduler"/>,
    /// 30s-after-launch + 24h timer — UPD-1b) — the "Check for updates" button is
    /// always available regardless of this toggle (per Plan 4D: a manual
    /// button press is itself explicit consent). Changes are forwarded to the
    /// scheduler in <see cref="OnAutomaticUpdateCheckEnabledChanged"/>.
    /// </summary>
    [ObservableProperty] private bool _automaticUpdateCheckEnabled;

    /// <summary>
    /// Two-way-bound to <see cref="AppDefaults.AutomaticUpdateInstallEnabled"/> (UPD-4b). Label:
    /// "Install updates automatically". Gates ONLY the automatic path driven by
    /// <c>AutoUpdateInstaller</c> off a background tick — the "Apply &amp; restart" button below is
    /// never gated by it, exactly as the manual check button is never gated by the check toggle.
    /// Seeded via the backing field for the same reason as the check toggle.
    /// </summary>
    [ObservableProperty] private bool _automaticUpdateInstallEnabled;

    /// <summary>
    /// Non-empty when the last toggle change could NOT be written to disk. The page renders it in
    /// amber under the toggles. See <see cref="ApplyToggle"/> for why this exists at all: a
    /// silently-lost write leaves a switch showing a state the app is not in.
    /// </summary>
    [ObservableProperty] private string _toggleWarning = "";

    // Guards the corrective write-back in ApplyToggle from re-entering persistence. Set only
    // around a property assignment made by this VM itself, never around a user-driven one.
    private bool _suppressTogglePersist;

    /// <summary>
    /// Friendly status line shown under the version: "You're on the latest
    /// version", "Update available: v X.Y.Z", "Couldn't check for updates:
    /// &lt;error&gt;", etc. Empty until the user first runs a check.
    /// </summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// Outcome of the last check, or null if no check has run yet. Drives
    /// any per-state UI affordances the page might want (e.g., coloring
    /// the status text differently for Error vs UpdateAvailable).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAndRestartCommand))]
    private UpdateCheckOutcome? _lastOutcome;

    /// <summary>
    /// Available version string when <see cref="LastOutcome"/> is
    /// <see cref="UpdateCheckOutcome.UpdateAvailable"/>. Drives any
    /// "Download now" UI the page might surface (deferred to UPD-1b
    /// for v1 — currently informational only).
    /// </summary>
    [ObservableProperty] private string? _availableVersion;

    /// <summary>
    /// True while <see cref="CheckForUpdatesCommand"/> is in flight.
    /// Drives the button's enabled state via
    /// <see cref="CommunityToolkit.Mvvm.Input.RelayCommandAttribute.CanExecute"/>;
    /// the page can also bind it to a spinner if it wants. Re-fires
    /// CheckForUpdatesCommand.NotifyCanExecuteChanged on transitions so the
    /// button updates immediately.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAndRestartCommand))]
    private bool _isBusy;

    public UpdateViewModel(IUpdateService updates, SettingsService settings, IUpdateScheduler scheduler,
        Services.Updates.AutoUpdateInstaller? installer = null)
    {
        _updates = updates;
        _settings = settings;
        _scheduler = scheduler;
        _installer = installer;

        // Seed from service + settings so the page can render without
        // waiting for the first user click. Matches LicenseViewModel's
        // constructor-time GetCachedStatus pattern.
        CurrentVersion = _updates.CurrentVersion;
        LastCheckedUtc = _updates.LastCheckedUtc;
        // Direct backing-field assignment — assigning via the property
        // would fire OnAutomaticUpdateCheckEnabledChanged and persist a
        // same-value SetBool on every page open. Codex 2026-05-20 diff
        // review caught this; matches SettingsViewModel's pattern.
        // GetBoolDefaulted (UPD-4b sweep): the shipped default belongs to AppDefaults.Defaults, so
        // every reader resolves a malformed stored value the same way. A literal here could render
        // the toggle ON while the scheduler treated the same value as OFF.
        _automaticUpdateCheckEnabled = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);
        _automaticUpdateInstallEnabled = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled);

        // UPD-2/UPD-3b: apply + pending state are service-owned so a transient VM can
        // re-attach mid-apply and stay truthful while the pending update transitions.
        // Subscribe BEFORE snapshotting so a transition between the two can't be missed
        // (Codex UPD-3b round 2 caught the pending-seed reading before the subscription);
        // UpdatesPage calls Detach() on unload so the transient VM doesn't leak through the
        // singleton's event list.
        _dispatcher = TryGetDispatcher();
        _updates.ApplyProgressChanged += OnServiceApplyProgress;
        _updates.ApplyCompleted += OnServiceApplyCompleted;
        _updates.ApplyInFlightChanged += OnServiceApplyInFlightChanged;
        _updates.ApplyDownloadRetrying += OnServiceApplyDownloadRetrying;
        _updates.PendingUpdateVersionChanged += OnServicePendingVersionChanged;

        // UPD-1b: if a BACKGROUND scheduler tick found an update while this page was
        // closed, the service holds it in PendingUpdateVersion. Seed the page straight
        // into the UpdateAvailable state so "Apply & restart" is enabled on open —
        // otherwise the user clicks the sidebar badge and lands on a page that shows
        // nothing actionable until they press "Check for updates" again. Set the backing
        // fields directly: these run before any binding is attached, and routing through
        // the property setters would only churn command CanExecute re-evaluation.
        var pending = _updates.PendingUpdateVersion;
        if (!string.IsNullOrEmpty(pending))
        {
            _lastOutcome = UpdateCheckOutcome.UpdateAvailable;
            _availableVersion = pending;
            _statusText = $"Update available: v{pending}";
        }

        if (_updates.IsApplyInFlight)
        {
            _isApplying = true;
            _applyStatus = _updates.ApplyProgressPercent is int percent
                ? DownloadingStatus(percent)
                : "Applying update…";
        }
    }

    // The apply flow ends in an automatic restart (ApplyAndRestartAsync) — warn up
    // front in the download status so the restart never surprises mid-dictation
    // (owner request 2026-07-18).
    private static string DownloadingStatus(int percent)
        => $"Downloading update… {percent}% — VoiceWink will restart automatically when ready.";

    /// <summary>
    /// Unsubscribes the service event handlers. Called by <c>UpdatesPage.OnUnloaded</c> — the VM
    /// is transient and the service is a singleton, so without this every page visit would leak
    /// a VM instance through the service's event list.
    /// </summary>
    public void Detach()
    {
        _updates.ApplyProgressChanged -= OnServiceApplyProgress;
        _updates.ApplyCompleted -= OnServiceApplyCompleted;
        _updates.ApplyInFlightChanged -= OnServiceApplyInFlightChanged;
        _updates.ApplyDownloadRetrying -= OnServiceApplyDownloadRetrying;
        _updates.PendingUpdateVersionChanged -= OnServicePendingVersionChanged;
    }

    // UPD-3b: keeps an OPEN Updates page truthful when the pending update transitions
    // underneath it — a background check discovering an update enables "Apply & restart"
    // live, and a withdrawn update drops the stale affordance instead of leaving an Apply
    // button pointing at nothing (the sidebar dot already follows service state; this is
    // the page-level counterpart, replacing the old rebuild-the-whole-page approach).
    // Snapshot-reconciled like every other service-event handler; skipped while a check or
    // apply is in flight — those flows own their state updates.
    private void OnServicePendingVersionChanged(string? _) => OnUiThread(() =>
    {
        if (IsApplying || IsBusy) return;

        var pending = _updates.PendingUpdateVersion;
        if (pending is null)
        {
            if (LastOutcome == UpdateCheckOutcome.UpdateAvailable)
            {
                LastOutcome = UpdateCheckOutcome.UpToDate;
                AvailableVersion = null;
                StatusText = "You're on the latest version.";
            }
        }
        else
        {
            LastOutcome = UpdateCheckOutcome.UpdateAvailable;
            AvailableVersion = pending;
            StatusText = $"Update available: v{pending}";
        }
    });

    // Events are NOTIFICATIONS, not truth: each handler reconciles from the service snapshot
    // instead of trusting the payload, so a stale event — raised just after a release/new-apply
    // race on a thread-pool thread — can't flip the UI the wrong way (Codex diff-review round 2).
    private void OnServiceApplyProgress(int percent) => OnUiThread(() =>
    {
        if (!_updates.IsApplyInFlight) return;
        IsApplying = true;
        ApplyStatus = DownloadingStatus(percent);
    });

    private void OnServiceApplyCompleted(ApplyResult result) => OnUiThread(() => ApplyResultToState(result));

    // UPD-3: a stalled attempt tore down cleanly and the service scheduled an automatic retry.
    // Shown until the next attempt's first progress callback overwrites it with a fresh
    // "Downloading update… N%".
    private void OnServiceApplyDownloadRetrying(int attempt, int maxAttempts) => OnUiThread(() =>
    {
        if (!_updates.IsApplyInFlight) return;
        IsApplying = true;
        ApplyStatus = $"Download stalled — retrying (attempt {attempt} of {maxAttempts})…";
    });

    // Covers the transitions the awaited command can't observe: the abandoned-download state
    // un-wedging (stalled Velopack task finally died → buttons re-enable without navigation),
    // an apply initiated by an EARLIER VM instance releasing — and, since UPD-4b, an AUTOMATIC
    // apply cancelled by an opt-out, which raises no ApplyCompleted at all.
    private void OnServiceApplyInFlightChanged(bool _) => OnUiThread(() =>
    {
        var inFlight = _updates.IsApplyInFlight;
        if (!inFlight && IsApplying)
        {
            // The guard released while this VM still thought an apply was running, i.e. no result
            // has been written yet. Without this, a cancelled automatic apply left "Downloading
            // update… VoiceWink will restart automatically when ready." on screen with the buttons
            // re-enabled (Codex diff r4) — a restart promise nothing was going to keep. Ordering
            // makes this safe for normal completions: the service raises InFlightChanged(false)
            // BEFORE ApplyCompleted, so a real result overwrites the cleared text immediately;
            // ApplyInitiated never releases the guard, so the restart text it wants stays.
            ApplyStatus = "";
        }
        IsApplying = inFlight;
    });

    private void OnUiThread(Action action)
    {
        if (_dispatcher is null) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    // GetForCurrentThread doesn't just return null off the WinUI pump — in a process without
    // the WinAppSDK runtime bootstrapped (the unit-test host runs with
    // WindowsAppSdkBootstrapInitialize=false) the WinRT activation itself throws
    // REGDB_E_CLASSNOTREG. Null means "run handlers inline", which is exactly what tests want.
    private static Microsoft.UI.Dispatching.DispatcherQueue? TryGetDispatcher()
    {
        try { return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// User-facing "Last checked: ..." string. Renders "never" for null;
    /// otherwise the local-time short date + time. The conversion to
    /// local time matters here — the user's mental model is "when did
    /// I last check?" not "what was UTC when I checked?".
    /// </summary>
    public string LastCheckedDisplay => LastCheckedUtc.HasValue
        ? $"Last checked: {LastCheckedUtc.Value.ToLocalTime():g}"
        : "Last checked: never";

    /// <summary>
    /// Source-generated partial hook for <see cref="AutomaticUpdateCheckEnabled"/>
    /// — fires on every change including the constructor's initial set (which the ctor's
    /// backing-field seeding deliberately avoids).
    /// </summary>
    partial void OnAutomaticUpdateCheckEnabledChanged(bool value)
    {
        if (_suppressTogglePersist) return;
        var effective = ApplyToggle(AppDefaults.AutomaticUpdateCheckEnabled, value,
            corrected => AutomaticUpdateCheckEnabled = corrected);
        // UPD-1b: there is no SettingsService change event, so the scheduler can't observe
        // this write on its own. Notify it explicitly so the toggle takes effect in-session
        // (off → stop polling; on → re-arm). The scheduler's own gate makes this a no-op
        // until App has started it behind the LGL-1 legal/onboarding gate, so flipping the
        // toggle during onboarding can never start network egress early. The service-level
        // gate-2 (AutomaticUpdateCheckEnabled re-read per tick) remains the safety net.
        //
        // UPD-4b: notified with the EFFECTIVE value, not the requested one, and exactly once —
        // a failed enable must not arm the poll loop for a preference that is not on disk.
        _scheduler.OnAutomaticSettingChanged(effective);
        // …and the installer, so turning checks off also cancels an apply already downloading
        // (an install is only ever a consequence of a check). Self-checking: no-ops if both
        // preferences are still on.
        _installer?.NotifyPreferencesChanged();
    }

    /// <summary>
    /// Source-generated partial hook for <see cref="AutomaticUpdateInstallEnabled"/> (UPD-4b).
    /// No scheduler notify: the scheduler never reads this key, and
    /// <c>AutoUpdateInstaller</c> re-reads it per attempt, so an opt-out takes effect on the next
    /// attempt with nothing to notify.
    /// </summary>
    partial void OnAutomaticUpdateInstallEnabledChanged(bool value)
    {
        if (_suppressTogglePersist) return;
        ApplyToggle(AppDefaults.AutomaticUpdateInstallEnabled, value,
            corrected => AutomaticUpdateInstallEnabled = corrected);
        // UPD-4b (Codex diff r4): an opt-out must cancel an apply already DOWNLOADING, not only
        // future attempts — this is the surface where a user watches the download happen.
        _installer?.NotifyPreferencesChanged();
    }

    /// <summary>
    /// Durable toggle apply, returning the value actually in effect (UPD-4b).
    ///
    /// <para>Routed through <see cref="DiagnosticsConsent.Apply"/> — the REL-17 helper — rather
    /// than a bare <c>SetBool</c>, because <c>SettingsService</c> writes are debounced and its
    /// <c>Save()</c> SWALLOWS failures, so a plain write reports a success it never had. A toggle
    /// that reads OFF and comes back ON next launch is bad enough for a diagnostics opt-in; for
    /// these two it also means unexpected network egress and an app that restarts itself.</para>
    ///
    /// <para>The helper's contract is fail-OFF in BOTH directions, which is the right direction
    /// here too: a failed opt-out must not leave the behaviour running, and a failed opt-in must
    /// not leave it running with no durable record. Restoring the PREVIOUS value was tried in
    /// REL-17 round 3 and is exactly backwards — it re-enables what the user just turned off.</para>
    ///
    /// <para>On failure the property is corrected through <paramref name="writeBack"/> under
    /// <see cref="_suppressTogglePersist"/>, so the corrective assignment cannot re-enter
    /// persistence or notify a second time; the page follows via <c>PropertyChanged</c>.</para>
    /// </summary>
    private bool ApplyToggle(string key, bool requested, Action<bool> writeBack)
    {
        // The opt-out must reach a RUNNING automatic apply BEFORE the durable write (Codex diff
        // r7): DiagnosticsConsent.Apply flushes synchronously on this (UI) thread, and while that
        // blocks — a slow disk is exactly when it matters — the download can complete on a worker
        // and hand off with an uncancelled token. SetBool is an in-memory cache write, so the
        // installer's own settings re-read sees the new value instantly; Apply's identical
        // SetBool below is a harmless same-value re-write. The hooks' post-Apply notify stays,
        // covering the fail-off direction (a failed opt-IN forces the value off, which must also
        // withdraw a running apply's mandate).
        _settings.SetBool(key, requested);
        _installer?.NotifyPreferencesChanged();

        var result = DiagnosticsConsent.Apply(_settings, key, requested);
        if (result.Persisted)
        {
            ToggleWarning = "";
            return result.Effective;
        }

        Logger.Warning(
            "Could not persist update preference {Key}; effective value forced to {Effective}",
            key, result.Effective);
        if (result.Effective != requested)
        {
            _suppressTogglePersist = true;
            try { writeBack(result.Effective); }
            finally { _suppressTogglePersist = false; }
        }
        ToggleWarning = "Couldn't save this setting. It's turned off for now, and the previous " +
                        "value may come back the next time VoiceWink starts.";
        return result.Effective;
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    public async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _updates.CheckForUpdatesAsync(isManualCheck: true)
                .ConfigureAwait(true); // resume on UI thread so the property writes below are dispatcher-safe
            LastOutcome = result.Outcome;
            AvailableVersion = result.AvailableVersion;
            if (result.CheckedUtc is { } checkedUtc)
                LastCheckedUtc = checkedUtc;
            StatusText = result.Outcome switch
            {
                UpdateCheckOutcome.Disabled =>
                    "Auto-updates aren't enabled in this build yet — you're running a pre-release version.",
                UpdateCheckOutcome.UpToDate =>
                    "You're on the latest version.",
                UpdateCheckOutcome.UpdateAvailable =>
                    $"Update available: v{result.AvailableVersion}",
                UpdateCheckOutcome.Error =>
                    $"Couldn't check for updates: {result.ErrorMessage}",
                _ => "",
            };
        }
        catch (Exception ex)
        {
            // The service is supposed to never throw out of
            // CheckForUpdatesAsync (it catches and reports as Error
            // outcome). This catch is defense-in-depth so a contract
            // violation can't crash the UI.
            Logger.Warning(ex, "Update check unexpectedly threw out of IUpdateService.CheckForUpdatesAsync");
            LastOutcome = UpdateCheckOutcome.Error;
            StatusText = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Also disabled while an apply is in flight: a check overwrites the source's cached UpdateInfo,
    // which the in-flight apply correlates against — letting them overlap could make the apply
    // download/apply a different release than the one it was authorized for.
    private bool CanCheckForUpdates() => !IsBusy && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanApplyAndRestart))]
    public async Task ApplyAndRestartAsync()
    {
        IsApplying = true;
        ApplyStatus = "";
        try
        {
            var result = await _updates.ApplyAndRestartAsync().ConfigureAwait(true);
            ApplyResultToState(result);
        }
        catch (Exception ex)
        {
            // IUpdateService is contracted not to throw out of ApplyAndRestartAsync (it returns
            // an Error outcome). Defense-in-depth so a contract violation can't crash the UI.
            Logger.Warning(ex, "Apply unexpectedly threw out of IUpdateService.ApplyAndRestartAsync");
            ApplyStatus = $"Update failed: {ex.Message}";
        }
        finally
        {
            // Service truth, NOT unconditional false: ApplyInitiated and the abandoned-download
            // state keep IsApplyInFlight true, and the buttons must stay disabled — an
            // unconditional reset here would re-enable the UI while the process is exiting or
            // while an orphaned download still owns the staging dir (Codex round-2 blocker).
            IsApplying = _updates.IsApplyInFlight;
        }
    }

    // Shared outcome→UI mapping for the awaited command result AND the service's ApplyCompleted
    // event (which is how a RE-ATTACHED page — apply started by an earlier VM instance — learns
    // the terminal state). Idempotent: the initiating VM runs it twice with the same result.
    private void ApplyResultToState(ApplyResult result)
    {
        ApplyStatus = result.Outcome switch
        {
            ApplyOutcome.ApplyInitiated => "Update downloaded - restarting...",
            ApplyOutcome.Blocked => $"Can't update right now: {result.Detail}. Finish that, then try again.",
            // Echo the service Detail when present so the two NotPending reasons ("no longer
            // available" — pulled / superseded by a concurrent re-check — vs the generic case) are
            // distinguishable, matching the Blocked/Error arms.
            ApplyOutcome.NotPending => string.IsNullOrEmpty(result.Detail) ? "No update to apply." : result.Detail,
            ApplyOutcome.Error => $"Update failed: {result.Detail}",
            _ => "",
        };

        // NotPending from the Apply button means the update this apply targeted is no longer
        // applicable (pulled, superseded by a concurrent re-check, or already applied).
        // Reconcile against the service instead of assuming nothing is pending (UPD-3b): if a
        // NEWER version superseded the targeted one mid-apply, the service kept it pending —
        // point the affordance at the new version; otherwise drop the stale affordance (the
        // Apply button greys out and the status line stops advertising a dead version).
        if (result.Outcome == ApplyOutcome.NotPending)
        {
            var stillPending = _updates.PendingUpdateVersion;
            if (stillPending is not null)
            {
                LastOutcome = UpdateCheckOutcome.UpdateAvailable;
                AvailableVersion = stillPending;
                StatusText = $"Update available: v{stillPending}";
            }
            else
            {
                LastOutcome = UpdateCheckOutcome.UpToDate;
                StatusText = "";
            }
        }

        IsApplying = _updates.IsApplyInFlight;
    }

    // Apply is offered only when the last check found an update and nothing else is running.
    private bool CanApplyAndRestart() =>
        LastOutcome == UpdateCheckOutcome.UpdateAvailable && !IsApplying && !IsBusy;
}
