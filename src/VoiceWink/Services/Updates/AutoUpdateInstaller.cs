using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// UPD-4b — turns a background-detected update into an applied one, without a user click, when
/// <see cref="AppDefaults.AutomaticUpdateInstallEnabled"/> is on (the shipped default).
///
/// <para><b>It owns the decision and the retry, never the mechanism.</b> Applying is
/// <see cref="IUpdateService.ApplyAndRestartAsync"/> and nothing else — the same maintenance-gated
/// path the "Apply &amp; restart" button uses, with the same update-apply lease, blocker pre-check,
/// stall watchdog and UPD-3c visible-restart flag. There is deliberately no second apply path.</para>
///
/// <para><b>UI-thread affinity is a correctness requirement, not a convention.</b>
/// <c>ApplyAndRestartAsync</c> documents that everything from its guard CAS through
/// <c>TryBeginUpdateApply</c> runs on one thread synchronously before the first await, and that "a
/// future background-thread caller would reopen the start-vs-apply race". This class IS that
/// caller's home — the scheduler raises its events on a thread-pool thread — so every entry point
/// checks <see cref="_isOnUiThread"/> and REFUSES rather than proceeding. The race it prevents
/// (a recording start slipping between the CAS and the lease, with neither side seeing the other)
/// is rare, timing-dependent and would be miserable to diagnose in the field, so the check is
/// fail-closed and logs at Error where Sentry can see it.</para>
///
/// <para><b>Why a retry loop exists</b> (owner decision 2026-08-08, "retry when you go idle"):
/// the first attempt is often refused for a purely transient reason — the user is dictating. A
/// refusal that waits for the next 24 h tick sits badly next to a feature that promises to install
/// right away, so a refusal arms a bounded, NETWORK-FREE re-attempt (<see cref="RetryInterval"/> ×
/// <see cref="MaxRetryAttempts"/>) that fires as soon as the maintenance gate clears. It is bounded
/// because a machine that never goes idle must not wake forever; the scheduler's un-deduped
/// <see cref="IUpdateScheduler.UpdateReadyToInstall"/> is the outer retry once the budget is spent.
/// The loop makes no network call of its own — it re-attempts an apply whose update was already
/// found.</para>
///
/// <para><b>The opt-out also stops an apply already RUNNING.</b> Each automatic apply carries its
/// own cancellation token, and while it is in flight the two preferences are re-read every
/// <see cref="PreferencePollInterval"/>; either going false cancels the apply before the updater
/// hand-off. Without this the toggles governed only future attempts — a user who watched
/// "Downloading update…" and opted out still got the restart. Self-contained by design: no UI
/// surface has to tell this class anything (there is no settings change event to subscribe to),
/// and the manual "Apply &amp; restart" button never passes through here, so it is never
/// cancelled by the automatic path's rules.</para>
/// </summary>
public sealed class AutoUpdateInstaller : IDisposable
{
    private static ILogger Logger => Log.ForContext<AutoUpdateInstaller>();

    /// <summary>Gap between re-attempts after a transient refusal. Deliberately coarse: this is a
    /// retry, not a poll, and each attempt only reads the maintenance gate.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>Attempt budget for ONE armed retry (60 × 60 s ≈ one hour). Bounded so a machine
    /// that is never idle stops waking; the next scheduled check re-arms from scratch.</summary>
    internal const int MaxRetryAttempts = 60;

    /// <summary>How often an IN-FLIGHT automatic apply re-reads the two preferences so an opt-out
    /// can cancel it (see the poll loop in <see cref="TryInstallCoreAsync"/>). Network-free — it
    /// reads settings and, at most, cancels a token. Coarse enough to cost nothing against a
    /// download measured in minutes; fine enough that "I turned it off" wins long before the
    /// updater hand-off.</summary>
    internal static readonly TimeSpan PreferencePollInterval = TimeSpan.FromSeconds(5);

    private readonly IUpdateService _updates;
    private readonly SettingsService _settings;
    private readonly bool _featureEnabled;
    private readonly Func<bool> _exclusiveMaintenanceProbe;
    private readonly Func<bool> _isOnUiThread;
    private readonly Func<Action, bool> _postToUi;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    // Separate from _delay on purpose: the retry-budget tests count _delay invocations to prove
    // the bound, and the in-apply preference poll would pollute that count.
    private readonly Func<TimeSpan, CancellationToken, Task> _pollDelay;

    private readonly object _retryLock = new();
    private CancellationTokenSource? _retryCts;
    private int _retryGeneration;

    // UI-thread only (every mutation happens inside a thread-checked entry point), so a plain
    // field is correct — a lock here would only hide a caller that had escaped the thread check.
    private bool _installInFlight;

    // The live automatic apply's cancellation source, published so NotifyPreferencesChanged can
    // cancel it IMMEDIATELY instead of waiting for the next poll tick (Codex diff r4: an opt-out
    // made seconds before the download finished lost the race to the updater hand-off). Volatile +
    // snapshot-and-catch rather than UI-thread-plain, because the clearing runs in an await
    // continuation that the test host resumes off-thread (no dispatcher context there).
    private CancellationTokenSource? _activeApplyCts;

    public AutoUpdateInstaller(IUpdateService updates, SettingsService settings)
        : this(
            updates,
            settings,
            UpdateCheckFeature.IsEnabled,
            App.IsExclusiveMaintenanceActive,
            new DispatcherSeam(CaptureDispatcher()))
    {
    }

    // Internal test seam (InternalsVisibleTo VoiceWink.Tests). Lets tests exercise the ENABLED
    // path even though UpdateCheckFeature.IsEnabled compiles to false in test builds, and drive
    // the retry loop without sleeping real minutes.
    internal AutoUpdateInstaller(
        IUpdateService updates,
        SettingsService settings,
        bool featureEnabled,
        Func<bool> exclusiveMaintenanceProbe,
        Func<bool> isOnUiThread,
        Func<Action, bool>? postToUi = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
    {
        _updates = updates;
        _settings = settings;
        _featureEnabled = featureEnabled;
        _exclusiveMaintenanceProbe = exclusiveMaintenanceProbe;
        _isOnUiThread = isOnUiThread;
        // Default runs the action inline: a test driving the retry loop wants the attempt to
        // happen, and its isOnUiThread seam is what decides whether that is legal.
        _postToUi = postToUi ?? (a => { a(); return true; });
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _pollDelay = pollDelay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// The two production seams, derived from ONE captured <c>DispatcherQueue</c> so they can never
    /// describe different threads. A class rather than a tuple-returning helper because the public
    /// constructor delegates with <c>: this(...)</c>, where a helper would have to be invoked once
    /// per seam — two calls, two captures, and a latent way for "am I on the UI thread?" and "post
    /// to the UI thread" to disagree.
    ///
    /// <para>A null queue (the unit-test host, or a WinAppSDK activation failure) refuses
    /// everything: no install is serviced and no post succeeds. Fail closed — an install is worth
    /// skipping, the race is not worth risking.</para>
    /// </summary>
    private sealed class DispatcherSeam
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue;
        internal DispatcherSeam(Microsoft.UI.Dispatching.DispatcherQueue? queue) => _queue = queue;
        internal bool IsOnUiThread() => _queue is { } q && q.HasThreadAccess;
        internal bool Post(Action action) => _queue is { } q && q.TryEnqueue(() => action());
    }

    private AutoUpdateInstaller(
        IUpdateService updates,
        SettingsService settings,
        bool featureEnabled,
        Func<bool> exclusiveMaintenanceProbe,
        DispatcherSeam seam)
        : this(updates, settings, featureEnabled, exclusiveMaintenanceProbe, seam.IsOnUiThread, seam.Post)
    {
    }

    // GetForCurrentThread doesn't merely return null off the WinUI pump — in a process without the
    // WinAppSDK runtime bootstrapped (the test host runs with WindowsAppSdkBootstrapInitialize=false)
    // the WinRT activation itself throws REGDB_E_CLASSNOTREG. Same shape as UpdateViewModel's.
    private static Microsoft.UI.Dispatching.DispatcherQueue? CaptureDispatcher()
    {
        try { return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Consider applying the update the scheduler just reported. <b>Must be called on the UI
    /// thread</b> — an off-thread call is refused and logged, never serviced.
    /// </summary>
    public Task TryInstallDetectedUpdateAsync() => TryInstallCoreAsync(isRetry: false);

    /// <summary>
    /// Tell the installer a persisted update preference just changed, so an opt-out cancels a
    /// running automatic apply IMMEDIATELY rather than at the next poll tick (Codex diff r4: the
    /// poll only reads preferences when its 5 s timer wins the race, so an opt-out made seconds
    /// before the download finished could still end in a restart). Reads the settings itself —
    /// the caller cannot hand it a value that disagrees with disk — and no-ops when both are on,
    /// so surfaces call it on every change without branching on direction.
    ///
    /// <para>Called by <c>UpdateViewModel</c>'s toggle hooks (the Settings → Updates surface, the
    /// one place a user can watch a download and opt out). The poll REMAINS as the backstop for
    /// writers that cannot reach this class without a forbidden locator call — onboarding's
    /// relaunch-wizard path — where a residual window of at most one poll interval is the accepted
    /// cost; the service's final token check before the updater hand-off bounds it there.</para>
    /// </summary>
    public void NotifyPreferencesChanged()
    {
        if (!_isOnUiThread())
        {
            // Same fail-closed rule as the install entry point: this cancels a token that the
            // UI-thread state machine owns, so an off-thread caller is a wiring bug, not a caller
            // to accommodate.
            Logger.Error("NotifyPreferencesChanged refused: not on the UI thread.");
            return;
        }

        if (_settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled)
            && _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled))
        {
            return; // both still on — nothing to withdraw
        }

        // Snapshot-and-catch: the apply's finally may be disposing the source concurrently in the
        // test host (off-thread continuation). A cancel that loses that race lost it to an apply
        // that already COMPLETED, which needs no cancelling.
        if (Volatile.Read(ref _activeApplyCts) is { } cts)
        {
            try
            {
                Logger.Information("Automatic update install cancelled: preference turned off");
                cts.Cancel();
            }
            catch (ObjectDisposedException) { /* the apply just finished — nothing to stop */ }
        }
    }

    private async Task TryInstallCoreAsync(bool isRetry)
    {
        if (!_isOnUiThread())
        {
            // Fail closed. Running ApplyAndRestartAsync's synchronous prefix off the UI thread
            // reopens the start-vs-apply race its invariant comment warns about, so a wiring
            // regression must stop here loudly rather than corrupt state rarely.
            Logger.Error(
                "Automatic update install refused: not on the UI thread. ApplyAndRestartAsync's " +
                "guard/lease prefix is UI-thread-affine; servicing this off-thread would reopen " +
                "the start-vs-apply race.");
            return;
        }

        if (_installInFlight) return; // an attempt is already awaiting the service

        var automaticCheck = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);
        var automaticInstall = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled);
        var applyInFlight = _updates.IsApplyInFlight;
        var maintenanceActive = _exclusiveMaintenanceProbe();

        if (!AutoUpdateInstallPolicy.ShouldInstallAutomatically(
                _featureEnabled, automaticCheck, automaticInstall, applyInFlight, maintenanceActive))
        {
            // Split the refusal by whether waiting could plausibly change the answer. "Busy" is
            // transient and earns a retry; a disabled setting or feature is not, and arming a
            // wake loop for it would be waking up to re-read a preference.
            var transient = automaticInstall && automaticCheck && _featureEnabled
                            && (applyInFlight || maintenanceActive);
            if (transient)
            {
                Logger.Debug(
                    "Automatic update install deferred (applyInFlight={ApplyInFlight}, maintenance={Maintenance}); retrying while idle",
                    applyInFlight, maintenanceActive);
                ArmRetry();
            }
            else
            {
                Logger.Debug(
                    "Automatic update install skipped (feature={Feature}, check={Check}, install={Install})",
                    _featureEnabled, automaticCheck, automaticInstall);
                DisarmRetry();
            }
            return;
        }

        _installInFlight = true;
        try
        {
            Logger.Information("Automatic update install starting{Retry}", isRetry ? " (retry)" : "");

            // The apply gets ITS OWN cancellation token, and an opt-out during the apply cancels
            // it (Codex diff r3). Without this, the toggles only governed FUTURE attempts: a user
            // who saw "Downloading update…" and turned automatic installation off still got the
            // restart, because the download had already been authorised. The manual "Apply &
            // restart" button is untouched by construction — it never goes through this method,
            // so this token never wraps it.
            //
            // The watcher is the same shape as the retry: network-free (it reads two settings),
            // coarse (5 s against a download measured in minutes), and it needs no wiring from
            // any UI surface — the toggles just persist, and the watcher notices. The service
            // re-checks the token one final time after the download, BEFORE the updater hand-off,
            // so a cancel that lands during the download always stops the restart; a cancel after
            // the hand-off is too late by nature (the process is already exiting).
            //
            // It runs on the THREAD POOL, deliberately independent of the UI pump (Codex diff
            // r8). The first version was a WhenAny loop inside this method resuming with
            // ConfigureAwait(true) — so any long UI-thread block suspended it, and the worst
            // offender is the opt-out gesture itself: onboarding's toggle flushes settings
            // synchronously ON the UI thread, which parked the very poll that was supposed to
            // observe the opt-out while the download completed on a worker and handed off. The
            // "at most one poll interval" bound only holds if nothing on the UI thread can delay
            // the watcher; everything it does (settings reads, a CTS cancel) is thread-safe.
            var applyCts = new CancellationTokenSource();
            Volatile.Write(ref _activeApplyCts, applyCts);   // published: NotifyPreferencesChanged can now cancel
            var applyTask = _updates.ApplyAndRestartAsync(applyCts.Token);
            _ = Task.Run(() => WatchPreferencesWhileApplyRunsAsync(applyTask, applyCts));

            ApplyResult result;
            try { result = await applyTask.ConfigureAwait(true); }
            finally
            {
                // Unpublish BEFORE disposing, so a NotifyPreferencesChanged snapshot can never
                // observe a source that is already disposed-and-nulled; one that raced the
                // unpublish itself hits the Cancel try/catch — as does the watcher's.
                Volatile.Write(ref _activeApplyCts, null);
                applyCts.Dispose();
            }
            switch (result.Outcome)
            {
                case ApplyOutcome.ApplyInitiated:
                    // The process is exiting for the binary swap; nothing further to arm.
                    DisarmRetry();
                    Logger.Information("Automatic update install initiated; restarting.");
                    break;
                case ApplyOutcome.Blocked:
                    // The gate refused inside the lease acquisition — the authoritative answer,
                    // and a transient one (recording / transcribing / erasure). Wait for idle.
                    Logger.Information("Automatic update install blocked ({Detail}); retrying while idle", result.Detail);
                    ArmRetry();
                    break;
                case ApplyOutcome.NotPending:
                    // Pulled, superseded, or already applied — nothing to wait for.
                    DisarmRetry();
                    break;
                default:
                    // Error: deliberately NOT retried on the 60 s loop. The service already owns
                    // download-level retry (UPD-3), and turning a network failure into a
                    // once-a-minute re-attempt would hammer the CDN for an hour. The scheduler's
                    // next tick is the right granularity for this class.
                    DisarmRetry();
                    Logger.Warning("Automatic update install failed: {Detail}", result.Detail);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Our own opt-out cancel — the ONE exception ApplyAndRestartAsync's contract lets
            // propagate. The service released its guard and handed nothing to the updater; the
            // user said stop, so nothing is re-armed.
            DisarmRetry();
        }
        catch (Exception ex)
        {
            // IUpdateService is contracted not to throw out of ApplyAndRestartAsync. This is a
            // fire-and-forget call site, so a contract violation must not become an unobserved
            // task exception.
            Logger.Warning(ex, "Automatic update install threw out of IUpdateService.ApplyAndRestartAsync");
            DisarmRetry();
        }
        finally
        {
            _installInFlight = false;
        }
    }

    // Arms the bounded re-attempt loop. Already-armed is a NO-OP rather than a restart: each
    // scheduler tick would otherwise refill the attempt budget, quietly turning a bounded loop
    // into an unbounded one.
    private void ArmRetry()
    {
        lock (_retryLock)
        {
            if (_retryCts is not null) return;
            var cts = new CancellationTokenSource();
            _retryCts = cts;
            var generation = ++_retryGeneration;
            _ = Task.Run(() => RetryLoopAsync(generation, cts.Token), CancellationToken.None);
        }
    }

    private void DisarmRetry()
    {
        lock (_retryLock)
        {
            _retryGeneration++;
            if (_retryCts is not { } cts) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
            cts.Dispose();
            _retryCts = null;
        }
    }

    private async Task RetryLoopAsync(int generation, CancellationToken ct)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                await _delay(RetryInterval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _retryGeneration)) return;

                // Back to the UI thread for the attempt — the whole reason this class exists.
                // A failed post means the dispatcher is gone (shutting down): stop.
                //
                // The loop AWAITS the attempt UNTIL ITS TASK SETTLES, and both halves of that are
                // correctness fixes, not tidiness. Fire-and-forget made the LAST attempt race its
                // own loop teardown: the `for` falls out, the `finally` clears the retry slot, and
                // the attempt's ArmRetry then saw a free slot and armed a FRESH FULL BUDGET — an
                // unbounded loop, exactly what ArmRetry's already-armed no-op exists to prevent.
                // And completing at the attempt's FIRST await (the first fix's shape, Codex diff
                // r2) only moved the same race one layer deeper: an attempt that reaches
                // ApplyAndRestartAsync yields at that await, and the service's own post-download
                // maintenance re-check can return Blocked MINUTES later — its ArmRetry then hit a
                // slot the exhausted loop had long since retired. Holding the slot until the task
                // settles means every ArmRetry an attempt can ever make happens while this loop
                // still owns the slot, so exhaustion ALWAYS hands off bounded, to the next
                // scheduler tick. A side benefit: the loop cannot burn attempts while an apply is
                // downloading — there is nothing useful a tick could do then anyway.
                //
                // The generation re-check inside the posted action closes the shutdown window:
                // Dispose/DisarmRetry bumps the generation, so an item already sitting in the
                // dispatcher queue when teardown began does not start an apply on the way out.
                var attemptRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_postToUi(() =>
                {
                    // An async method never throws synchronously — a fault surfaces on the task —
                    // so ContinueWith observes every completion shape, and the stale-generation
                    // branch contributes an already-completed task.
                    var attemptTask = generation != Volatile.Read(ref _retryGeneration)
                        ? Task.CompletedTask
                        : TryInstallCoreAsync(isRetry: true);
                    attemptTask.ContinueWith(
                        static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
                        attemptRan, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                })) return;
                // Not ct-linked on purpose: abandoning the wait would reintroduce the very race
                // this await removes. The attempt always settles (TryInstallCoreAsync never
                // throws out), and a cancel is observed at the next loop checkpoint.
                await attemptRan.Task.ConfigureAwait(false);
            }

            Logger.Information(
                "Automatic update install: retry budget exhausted after {Attempts} attempts; the next scheduled check will re-arm.",
                MaxRetryAttempts);
        }
        catch (OperationCanceledException)
        {
            // Normal on disarm.
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Automatic update install retry loop terminated unexpectedly");
        }
        finally
        {
            // Covers every exit this loop has OTHER than the exhausted-budget one, which already
            // retired its slot before dispatching its last attempt: cancellation, a fault, a
            // stale-generation return, and a failed post. Idempotent.
            RetireSlot(generation);
        }
    }

    /// <summary>
    /// Watches the two preferences while ONE automatic apply is in flight and cancels it the
    /// moment either goes false (UPD-4b, Codex diff r8). Runs on the thread pool with
    /// <c>ConfigureAwait(false)</c> throughout — its independence from the UI pump is the whole
    /// point: the opt-out gesture itself can block the UI thread in a synchronous settings flush,
    /// which is precisely when a pump-bound poll could not act.
    /// </summary>
    private async Task WatchPreferencesWhileApplyRunsAsync(Task applyTask, CancellationTokenSource applyCts)
    {
        try
        {
            while (!applyTask.IsCompleted)
            {
                await _pollDelay(PreferencePollInterval, applyCts.Token).ConfigureAwait(false);
                if (applyTask.IsCompleted) return;
                if (!_settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled)
                    || !_settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled))
                {
                    Logger.Information("Automatic update install cancelled: the preference was turned off during the apply");
                    // The apply can complete-and-dispose concurrently; a cancel that loses that
                    // race lost it to an apply that already finished, which needs no cancelling.
                    try { applyCts.Cancel(); }
                    catch (ObjectDisposedException) { /* the apply just finished */ }
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The apply was cancelled by another path (NotifyPreferencesChanged) or completed and
            // tore the token down mid-delay — either way there is nothing left to watch.
        }
        catch (ObjectDisposedException)
        {
            // The delay raced the CTS disposal after the apply settled. Same conclusion.
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Automatic update preference watcher terminated unexpectedly");
        }
    }

    // Frees the retry slot IF this generation still owns it, so a newer arm is never clobbered.
    // Does not bump the generation: callers that need to invalidate in-flight work call
    // DisarmRetry instead.
    private void RetireSlot(int generation)
    {
        lock (_retryLock)
        {
            if (generation != _retryGeneration) return;
            if (_retryCts is not { } cts) return;
            cts.Dispose();
            _retryCts = null;
        }
    }

    public void Dispose() => DisarmRetry();
}
