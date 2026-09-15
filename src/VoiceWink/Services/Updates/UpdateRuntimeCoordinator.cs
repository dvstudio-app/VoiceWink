using System;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Owns the update subsystem's RUNTIME WIRING: which of the scheduler's and service's events are
/// subscribed, how they reach the UI thread, and the teardown order (UPD-4b extraction).
///
/// <para><b>Why this type exists.</b> All of this lived inline in <c>App.StartGatedRuntimeServices</c>
/// / <c>App.Cleanup</c>, and UPD-4b would have added a third handler, a third resolved service and
/// another cleanup step to it. `AGENTS.md` forbids exactly that: *"Do not add new responsibilities
/// to the hub classes (`App.xaml.cs`, …). A change that would grow a hub's responsibilities must
/// first extract or isolate the affected responsibility into its own component."* — and *"Do not add
/// new `App.Services` service-locator usages"*. Extracting turns three locator calls into one and
/// moves the wiring somewhere it can be read as a unit (Codex diff review).</para>
///
/// <para><b>What stays in `App`.</b> Only what genuinely belongs to the window: two callbacks that
/// touch <c>MainWindow</c>, a quitting probe, and the UI-thread post. This type never references
/// the window, WinUI or the dispatcher type — which is also what makes the wiring order testable
/// without a UI.</para>
///
/// <para><b>Ordering is load-bearing.</b> <see cref="Start"/> subscribes BEFORE starting the poll
/// so the first tick cannot be missed; <see cref="Stop"/> unsubscribes BEFORE stopping it so a tick
/// already in flight cannot marshal onto a tearing-down dispatcher, and disposes the installer last
/// so its retry loop is cancelled after nothing can arm it again.</para>
/// </summary>
public sealed class UpdateRuntimeCoordinator : IDisposable
{
    private static ILogger Logger => Log.ForContext<UpdateRuntimeCoordinator>();

    private readonly IUpdateScheduler _scheduler;
    private readonly IUpdateService _updates;
    private readonly AutoUpdateInstaller _installer;
    private readonly SettingsService _settings;

    // Held so teardown unsubscribes the SAME delegate instances it registered.
    private Action<string>? _updateAvailableHandler;
    private Action<string>? _readyToInstallHandler;
    private Action<string?>? _pendingVersionHandler;
    private bool _started;

    public UpdateRuntimeCoordinator(
        IUpdateScheduler scheduler, IUpdateService updates, AutoUpdateInstaller installer,
        SettingsService settings)
    {
        _scheduler = scheduler;
        _updates = updates;
        _installer = installer;
        _settings = settings;
    }

    /// <summary>
    /// Re-derive the scheduler's armed state from the PERSISTED check preference (UPD-4b).
    ///
    /// <para>Called by <c>App</c> at onboarding COMPLETION — the seam that covers the
    /// Settings → "Relaunch setup wizard" path, where the scheduler is already running,
    /// <c>StartGatedRuntimeServices</c> is an idempotent no-op, and a wizard that turned checks
    /// on would otherwise arm nothing until the next launch. On a first run this is a harmless
    /// re-derive of what <c>Start()</c> just did. Deliberately reads the setting rather than
    /// taking a parameter, so the caller cannot hand it a value that disagrees with disk.</para>
    ///
    /// <para>Why the wizard page does not notify per toggle-flip instead: that needed a new
    /// <c>App.Services</c> locator call in <c>OnboardingPage</c>, which AGENTS.md forbids — and
    /// completion-boundary semantics are the wizard's own convention anyway (every other choice
    /// applies at Done). The scheduler's <c>_started</c> gate keeps this safe pre-consent: before
    /// the LGL-1 gated start it is a no-op.</para>
    /// </summary>
    public void ReconcileCheckSetting()
        => _scheduler.OnAutomaticSettingChanged(
            _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled));

    /// <summary>
    /// Wire the events and start the background poll. Idempotent. Call from behind the LGL-1
    /// legal/onboarding gate — the poll must never run ahead of consent.
    /// </summary>
    /// <param name="isQuitting">True once shutdown has begun. Checked on BOTH sides of the UI post:
    /// the outer check keeps a late tick off a tearing-down dispatcher, the inner one covers a post
    /// that had already queued when the quit started.</param>
    /// <param name="postToUi">Marshal to the UI thread; false when the dispatcher is gone.</param>
    /// <param name="onUpdateAvailableDetected">Runs on the UI thread — the deduped badge/page channel.</param>
    /// <param name="onPendingVersionChanged">Runs on the UI thread with the CURRENT pending version
    /// re-read from the service, not the event payload (snapshot-reconcile: events are
    /// notifications, not truth, so out-of-order delivery still converges).</param>
    public void Start(
        Func<bool> isQuitting,
        Func<Action, bool> postToUi,
        Action<string> onUpdateAvailableDetected,
        Action<string?> onPendingVersionChanged)
    {
        if (_started) return;
        _started = true;

        _updateAvailableHandler = version =>
        {
            if (isQuitting()) return;
            postToUi(() =>
            {
                // Inner check too — the post may have queued behind a quit (the contract in the
                // Start doc: BOTH sides of every enqueue, and until Codex diff r5 only the
                // install callback honoured it).
                if (isQuitting()) return;
                onUpdateAvailableDetected(version);
            });
        };
        _scheduler.UpdateAvailableDetected += _updateAvailableHandler;

        // UPD-4b: the AUTO-INSTALL channel — a second, deliberately un-deduped event rather than a
        // line inside the handler above, because a refusal (the user is dictating) must get a fresh
        // chance on every tick and the badge channel's dedupe would make the first refusal
        // permanent for the process. The parameter is deliberately NOT named `_`: the body needs a
        // discard for the fire-and-forget Task, and a `_` parameter would capture that assignment.
        _readyToInstallHandler = version =>
        {
            if (isQuitting()) return;
            postToUi(() =>
            {
                if (isQuitting()) return;
                _ = _installer.TryInstallDetectedUpdateAsync();
            });
        };
        _scheduler.UpdateReadyToInstall += _readyToInstallHandler;

        // UPD-3b: the sidebar dot is STATE-driven — visible iff PendingUpdateVersion != null, on
        // every page. This event is only a wake-up. The scheduler's UpdateNoLongerAvailable is
        // deliberately NOT wired: a blind badge-clear could race a newer pending version.
        _pendingVersionHandler = _ =>
        {
            if (isQuitting()) return;
            postToUi(() =>
            {
                if (isQuitting()) return; // both sides — see the Start doc's contract
                onPendingVersionChanged(_updates.PendingUpdateVersion);
            });
        };
        _updates.PendingUpdateVersionChanged += _pendingVersionHandler;

        _scheduler.Start();
    }

    /// <summary>Unsubscribe, stop the poll, cancel the installer's retry loop. Idempotent.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;

        // Unsubscribe FIRST, then stop: the reverse order leaves a window where an in-flight tick
        // can still raise into handlers that post onto a dispatcher being torn down.
        if (_updateAvailableHandler is { } a) _scheduler.UpdateAvailableDetected -= a;
        if (_readyToInstallHandler is { } r) _scheduler.UpdateReadyToInstall -= r;
        if (_pendingVersionHandler is { } p) _updates.PendingUpdateVersionChanged -= p;
        _updateAvailableHandler = null;
        _readyToInstallHandler = null;
        _pendingVersionHandler = null;

        try { _scheduler.Stop(); }
        catch (Exception ex) { Logger.Debug(ex, "Update scheduler stop failed during teardown"); }

        // Last: nothing can arm the retry loop any more, so cancelling it here is final.
        try { _installer.Dispose(); }
        catch (Exception ex) { Logger.Debug(ex, "Auto-update installer dispose failed during teardown"); }
    }

    public void Dispose() => Stop();
}
