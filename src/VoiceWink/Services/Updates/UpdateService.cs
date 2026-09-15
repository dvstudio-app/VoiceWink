using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Maintenance;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// In-process update-check orchestrator. Sits between the user-facing
/// "Check for updates" button (Settings → Updates page,
/// <see cref="VoiceWink.Views.Pages.UpdatesPage"/>) and Velopack's
/// <c>UpdateManager</c> (via <see cref="IVelopackUpdateSource"/>).
/// Owns the two gating rules (UPDATE_CHECK_ENABLED build flag +
/// <see cref="AppDefaults.AutomaticUpdateCheckEnabled"/> user toggle)
/// and the persisted "last checked" timestamp.
///
/// <para>See <see cref="IUpdateService"/> for the contract; this file
/// just implements it.</para>
///
/// <para><b>Why split gating into two layers.</b> The build flag is the
/// kill-switch that ships disabled — egress can't happen until DV
/// Studio flips the csproj property AND a corresponding privacy
/// disclosure ships. The user toggle gates the scheduler path
/// (<see cref="IUpdateScheduler"/> — 30s after launch + 24h timer, UPD-1b);
/// once the build flag is on, users can still opt out of automatic checks
/// while keeping the manual button. Two layers because they answer different questions
/// ("can this build talk to the update server at all?" vs "should we
/// poll without the user asking?").</para>
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private static ILogger Logger => Log.ForContext<UpdateService>();

    private readonly SettingsService _settings;
    private readonly IVelopackUpdateSource _source;
    // Build-flag value, injected through the internal constructor so unit
    // tests can exercise the source-call branches without a separate
    // UPDATE_CHECK_ENABLED build configuration. Production constructs with
    // `UpdateCheckFeature.IsEnabled` (compile-time constant), so the
    // runtime behavior of release builds is identical to a hard-wired
    // check.
    private readonly bool _featureEnabled;

    // Apply-path collaborators (Phase 3). Null in unit tests that exercise only the check path. The
    // maintenance gate decides whether an apply may proceed + marks the app as applying an update so
    // start-paths refuse new work; IAppLifetime performs the graceful restart.
    private readonly IMaintenanceGate? _maintenanceGate;
    private readonly IAppLifetime? _appLifetime;

    // Memoized RemoteUpdateInfo from the last UpdateAvailable check — the "is an update queued?"
    // marker the apply path consults. The real Velopack UpdateInfo lives inside the source.
    private readonly object _pendingLock = new();
    private RemoteUpdateInfo? _pendingUpdate;

    // UPD-3b: serializes CheckForUpdatesAsync so an overlapping manual click + scheduler tick
    // can't interleave their source calls and pending-marker writes — a later-completing STALE
    // UpToDate result could otherwise clear a newer pending update another check just found
    // (visible now that the sidebar dot trusts service state). Checks are short; last STARTED
    // check wins, which is the correct semantic.
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    // UPD-2: the single in-flight apply. The CAS on this reference IS the apply guard — null
    // means no apply owns the path; non-null means that ApplyContext owns guard release, the
    // BeginUpdateApply gate scope, the progress fields, and the abandoned flag. A second
    // ApplyAndRestartAsync while this is non-null is rejected without side effects, which is
    // what stops a second Apply click from starting a parallel download into the same Velopack
    // staging dir (2026-07-03 tester-VM incident).
    private ApplyContext? _activeApply;

    // UPD-2 stall-watchdog tuning. Production uses the defaults; the internal test-seam ctor
    // overrides with ms-scale values so stall tests don't sleep real minutes. The watchdog is
    // the AUTHORITATIVE download bound: the incident proved Velopack's HTTP timeout does not
    // cover the package-body read (a dead connection sat at a 0-byte partial for 30+ minutes).
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DefaultStallPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultStallGrace = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _stallPollInterval;
    private readonly TimeSpan _stallGrace;

    // UPD-3: bounded automatic retry for stalled downloads. A stall that tears down cleanly
    // (the task acknowledged cancellation within grace) retries up to MaxDownloadAttempts total
    // attempts with a short backoff, all inside the SAME ApplyContext (guard + gate scope held
    // throughout — no new race surface). Velopack skips already-completed packages on retry and
    // adjacent-version updates are deltas, so retries are cheap. ABANDONED downloads never
    // retry — the orphaned task may still be writing to the staging dir. Byte-level resume is
    // deliberately out of scope (Velopack 0.0.1298 has no Range support and deletes partials);
    // revisit only if field evidence shows delta retries can't complete between drops.
    internal const int MaxDownloadAttempts = 3;
    private static readonly TimeSpan DefaultStallRetryBackoff = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _stallRetryBackoff;

    public event Action<int>? ApplyProgressChanged;
    public event Action<ApplyResult>? ApplyCompleted;
    public event Action<bool>? ApplyInFlightChanged;
    public event Action<int, int>? ApplyDownloadRetrying;
    public event Action<string?>? PendingUpdateVersionChanged;

    public UpdateService(SettingsService settings, IMaintenanceGate maintenanceGate, IAppLifetime appLifetime)
        : this(settings, new VelopackUpdateSource(), UpdateCheckFeature.IsEnabled, maintenanceGate, appLifetime)
    {
    }

    // Internal test seam — VoiceWink.Tests project sees this via InternalsVisibleTo (granted in
    // csproj). Gate/lifetime default null so check-path tests don't need to supply them; the
    // stall knobs default to production values so only stall tests need to pass them.
    internal UpdateService(
        SettingsService settings,
        IVelopackUpdateSource source,
        bool featureEnabled,
        IMaintenanceGate? maintenanceGate = null,
        IAppLifetime? appLifetime = null,
        TimeSpan? stallTimeout = null,
        TimeSpan? stallPollInterval = null,
        TimeSpan? stallGrace = null,
        TimeSpan? stallRetryBackoff = null)
    {
        _settings = settings;
        _source = source;
        _featureEnabled = featureEnabled;
        _maintenanceGate = maintenanceGate;
        _appLifetime = appLifetime;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _stallPollInterval = stallPollInterval ?? DefaultStallPollInterval;
        _stallGrace = stallGrace ?? DefaultStallGrace;
        _stallRetryBackoff = stallRetryBackoff ?? DefaultStallRetryBackoff;
    }

    public string CurrentVersion => CurrentVersionCached.Value;

    public DateTime? LastCheckedUtc
    {
        get
        {
            var raw = _settings.GetString(AppDefaults.LastUpdateCheckUtc, "");
            if (string.IsNullOrEmpty(raw)) return null;
            // Round-trip with the same "O" format we wrote. The format
            // always emits the timezone offset (Z for UTC), so
            // RoundtripKind alone restores Kind=Utc. If the value got
            // hand-edited or corrupted, treat as "no signal" rather
            // than throwing — surface state should never sticky-break.
            // Note: RoundtripKind can't be OR'd with AssumeUniversal /
            // AssumeLocal / AdjustToUniversal (they're mutually
            // exclusive style flags — DateTime.TryParse throws on
            // combination).
            return DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
    }

    public string? PendingUpdateVersion
    {
        // Reads the same memoized marker the apply path consults, under the same lock,
        // so the UI surface and ApplyAndRestartAsync can never disagree about what is
        // pending. Cleared to null on UpToDate / applied (see CheckForUpdatesAsync /
        // ApplyAndRestartAsync), so a stale version is never advertised.
        get { lock (_pendingLock) { return _pendingUpdate?.Version; } }
    }

    public bool IsApplyInFlight => Volatile.Read(ref _activeApply) is not null;

    public int? ApplyProgressPercent
    {
        get
        {
            var ctx = Volatile.Read(ref _activeApply);
            var percent = ctx?.ProgressSnapshot() ?? -1;
            return percent >= 0 ? percent : null;
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool isManualCheck,
        CancellationToken ct = default)
    {
        // Gate 1 — compile-time kill-switch. Until the csproj property
        // flips, no egress, no settings write, no logs at info level.
        if (!_featureEnabled)
        {
            Logger.Debug("Update check skipped: UPDATE_CHECK_ENABLED build flag is off (manual={IsManual})", isManualCheck);
            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.Disabled };
        }

        // Gate 2 — user-facing toggle. Enforced only on scheduler-triggered
        // checks; a manual button press is itself explicit consent.
        // GetBoolDefaulted (UPD-4b sweep): the shipped default is AppDefaults.Defaults', so a
        // malformed stored value resolves the same way here as in the scheduler and the UI.
        if (!isManualCheck && !_settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled))
        {
            Logger.Debug("Scheduled update check skipped: AutomaticUpdateCheckEnabled setting is off");
            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.Disabled };
        }

        // Record the attempt timestamp BEFORE the source call. Both
        // success and error update "Last checked" because the user sees
        // it as "when did we last try", not "when did we last succeed".
        var checkedUtc = DateTime.UtcNow;
        _settings.SetString(
            AppDefaults.LastUpdateCheckUtc,
            checkedUtc.ToString("O", CultureInfo.InvariantCulture));

        await _checkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await _source.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                string? clearedFrom;
                lock (_pendingLock) { clearedFrom = _pendingUpdate?.Version; _pendingUpdate = null; }
                // Transition-only, raised OUTSIDE the lock (UPD-3b): the previously
                // advertised update was pulled/resolved — the state-driven sidebar dot
                // consumes this.
                if (clearedFrom is not null) RaisePendingUpdateVersionChanged(null);
                Logger.Information("Update check: up to date (manual={IsManual})", isManualCheck);
                return new UpdateCheckResult
                {
                    Outcome = UpdateCheckOutcome.UpToDate,
                    CheckedUtc = checkedUtc,
                };
            }
            string? previousPending;
            lock (_pendingLock) { previousPending = _pendingUpdate?.Version; _pendingUpdate = info; }
            if (!string.Equals(previousPending, info.Version, StringComparison.Ordinal))
                RaisePendingUpdateVersionChanged(info.Version);
            Logger.Information(
                "Update check: newer version available (current={Current}, available={Available}, manual={IsManual})",
                CurrentVersion, info.Version, isManualCheck);
            return new UpdateCheckResult
            {
                Outcome = UpdateCheckOutcome.UpdateAvailable,
                AvailableVersion = info.Version,
                CheckedUtc = checkedUtc,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is normal control flow — propagate so the
            // caller can await/cancel composition naturally. Don't
            // misreport as Error (the user didn't fail; they cancelled).
            throw;
        }
        catch (Exception ex)
        {
            // Never let an exception escape this method — a background
            // scheduler tick that crashed the dispatcher loop because
            // updates.voicewink.app returned a 502 would be a very poor
            // user experience.
            // LOG-1 (owner report 2026-07-15): THIS is the production network log
            // site for update checks — a CDN/network failure logs type + message
            // only (the stack added nothing and repeated every 24 h tick during
            // outages); unexpected types (Velopack contract, settings faults) keep
            // the full exception. A non-ct TaskCanceledException here is the
            // HttpClient timeout, i.e. network-shaped.
            if (ex is HttpRequestException or TimeoutException or OperationCanceledException)
                Logger.Warning("Update check failed (manual={IsManual}): {ErrorType}: {ErrorMessage}",
                    isManualCheck, ex.GetType().Name, ex.Message);
            else
                Logger.Warning(ex, "Update check failed (manual={IsManual})", isManualCheck);
            return new UpdateCheckResult
            {
                Outcome = UpdateCheckOutcome.Error,
                ErrorMessage = ex.Message,
                CheckedUtc = checkedUtc,
            };
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async Task<ApplyResult> ApplyAndRestartAsync(CancellationToken ct = default)
    {
        if (!_featureEnabled)
        {
            return new ApplyResult { Outcome = ApplyOutcome.NotPending, Detail = "Updates are not enabled in this build." };
        }

        // ---- Synchronous prefix ----------------------------------------------------------------
        // INVARIANT: everything from the guard CAS through TryBeginUpdateApply below runs on ONE
        // thread, synchronously, before the first await. Today that holds because the only caller
        // (UpdateViewModel.ApplyAndRestartAsync) invokes this method from the UI thread BEFORE its
        // own first await. A future background-thread caller would reopen the start-vs-apply race.

        // Apply guard (UPD-2): winning the CAS makes this context the single owner of the apply
        // path. A concurrent second call is rejected here — fast, no events, no state change —
        // which is the fix for "navigate away → re-opened page shows an enabled Apply button →
        // second click starts a parallel download into the same staging dir".
        var ctx = new ApplyContext(Environment.TickCount64);
        if (Interlocked.CompareExchange(ref _activeApply, ctx, null) is not null)
        {
            return Blocked(new[] { "an update is already being applied" });
        }
        RaiseApplyInFlightChanged(true);

        RemoteUpdateInfo? pending;
        lock (_pendingLock) { pending = _pendingUpdate; }
        if (pending is null)
        {
            return CompleteApply(ctx, new ApplyResult { Outcome = ApplyOutcome.NotPending, Detail = "No update is available to apply." });
        }

        // Mark the app as applying an update NOW — synchronous prefix, BEFORE the maintenance
        // pre-check and BEFORE the download await yields. Start paths (recording / model download
        // / file transcription / redo / new-image) see App.IsExclusiveMaintenanceActive()==true
        // and refuse for the WHOLE apply. This deliberately REVERSES the plan's earlier "allow
        // recording during the download" intent (50-decision note): apply is user-triggered, so
        // blocking new work for the download they initiated is the correct, safe trade.
        //
        // F19 (round-3 amendment): acquire ATOMICALLY and BEFORE the pre-check's Check(), closing
        // the check-to-acquire window and making apply mutually exclusive with data erasure. If an
        // erasure holds its lease, refuse here. Attached to ctx so EVERY exit (pre-check blocked,
        // download error, cancel, success) unwinds it via CompleteApply→ReleaseApply→DisposeGateScope.
        // A null gate (tests / feature-off) is treated as "acquired" so behavior is unchanged.
        // Held on the success path (the app is exiting) AND in the abandoned-download state.
        if (_maintenanceGate != null)
        {
            if (!_maintenanceGate.TryBeginUpdateApply(out var applyScope))
                return CompleteApply(ctx, Blocked(new[] { "data erasure in progress" }));
            ctx.AttachGateScope(applyScope);
        }

        // Pre-check: don't proceed if a task is already in flight. (Guard-winner ⇒ this exit
        // raises ApplyCompleted, unlike the CAS rejection above.) The apply lease is already
        // held, so ReleaseApply on this blocked exit disposes it.
        if (TryGetBlockers(out var preBlockers))
        {
            return CompleteApply(ctx, Blocked(preBlockers));
        }

        // True once the download task has been abandoned (stall/cancel that Velopack didn't
        // acknowledge). The catch filters below EXCLUDE that case so the guard + gate scope stay
        // held — the orphaned task may still be writing to the staging dir, and the observer
        // continuation owns the eventual release.
        var abandonedHold = false;
        // ---- end of synchronous prefix -----------------------------------------------------
        try
        {
            ct.ThrowIfCancellationRequested();

            Logger.Information("Downloading update v{Version}…", pending.Version);

            // UPD-3 attempt loop: stalls that tear down CLEANLY retry automatically (up to
            // MaxDownloadAttempts total) inside this same ApplyContext — the guard and gate
            // scope never release between attempts, ApplyInFlightChanged fires no intermediate
            // transitions, and intermediate failed attempts raise ApplyDownloadRetrying, never
            // ApplyCompleted (which stays exactly-once-per-guard-winner).
            Task<object?> completedDownload;
            var state = ctx.BeginAttempt(1, Environment.TickCount64);
            for (var attempt = 1; ; attempt++)
            {
                var (outcome, downloadTask) = await RunDownloadAttemptAsync(ctx, state, pending, ct).ConfigureAwait(false);

                if (outcome == AttemptOutcome.StalledAbandoned)
                {
                    // Velopack ignored cancellation — the orphaned task may still be writing to
                    // the staging dir, so retrying (or handing the guard to anyone) would
                    // recreate the concurrent-write hazard. Enter the ABANDONED state: guard +
                    // gate scope stay held (start paths and the scheduler stay paused), and the
                    // observer releases them when the task finally completes — or a process
                    // restart clears everything. Never retried, regardless of attempt number.
                    abandonedHold = true;
                    ObserveDownloadOutcome(downloadTask, ctx, pending.Version, abandoned: true);
                    Logger.Warning(
                        "Update download did not acknowledge cancellation within {Seconds:F0}s — abandoned; apply stays locked until it completes (v{Version}, attempt {Attempt}/{Max})",
                        _stallGrace.TotalSeconds, pending.Version, attempt, MaxDownloadAttempts);

                    if (ct.IsCancellationRequested)
                    {
                        // Contract: caller cancellation throws. No ApplyCompleted (there is no
                        // result); ApplyInFlightChanged(false) fires when the observer releases.
                        throw new OperationCanceledException(ct);
                    }

                    var abandonedResult = new ApplyResult
                    {
                        Outcome = ApplyOutcome.Error,
                        Detail = "Update download stalled and could not be cancelled — restart VoiceWink to try again.",
                    };
                    RaiseApplyCompleted(abandonedResult); // guard deliberately NOT released
                    return abandonedResult;
                }

                if (outcome == AttemptOutcome.CallerCancelled)
                {
                    // Caller cancellation WINS over whatever the task did: observe its outcome
                    // (an unrelated concurrent fault is logged, never surfaced — letting it
                    // escape here would convert the cancellation into an Error result +
                    // ApplyCompleted, violating the contract; adversarial-workflow finding),
                    // a completed-successfully race is discarded, then throw OCE
                    // deterministically into the catch below (release + rethrow).
                    try { await downloadTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* the expected teardown shape */ }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Update download faulted while caller cancellation was pending; surfacing the cancellation");
                    }
                    throw new OperationCanceledException(ct);
                }

                if (outcome == AttemptOutcome.StalledAcknowledged)
                {
                    // Clean teardown — the task acknowledged the stall-cancel within grace.
                    // Observe it (logs a fault instead of leaving it unobserved; never releases
                    // for acknowledged stalls — see ObserveDownloadOutcome).
                    ObserveDownloadOutcome(downloadTask, ctx, pending.Version, abandoned: false);

                    if (attempt < MaxDownloadAttempts)
                    {
                        var next = attempt + 1;
                        // Swap in the NEXT attempt's state BEFORE the backoff so
                        // ApplyProgressPercent reads null for the whole backoff window — a
                        // re-attached page shows the retrying text, never a stale percentage.
                        state = ctx.BeginAttempt(next, Environment.TickCount64);
                        Logger.Warning(
                            "Update download attempt {Attempt}/{Max} stalled; retrying in {Seconds:F0}s (v{Version})",
                            attempt, MaxDownloadAttempts, _stallRetryBackoff.TotalSeconds, pending.Version);
                        RaiseApplyDownloadRetrying(next, MaxDownloadAttempts);
                        // ct-aware: a caller cancel during backoff throws OCE into the catch
                        // below (release + rethrow) instead of being swallowed by the loop.
                        await Task.Delay(_stallRetryBackoff, ct).ConfigureAwait(false);
                        continue;
                    }

                    return CompleteApply(ctx, new ApplyResult
                    {
                        Outcome = ApplyOutcome.Error,
                        Detail = $"Update download stalled after {MaxDownloadAttempts} attempts — no data received for {_stallTimeout.TotalMinutes:0.#} minute(s) at a time. Automatic retries were exhausted; check your connection and try again.",
                    });
                }

                // Succeeded: the task completed — success, or a fault (stall-adjacent or not)
                // that surfaces at the await below and flows to the generic catch: Error, no
                // retry, unchanged UPD-2 semantics.
                completedDownload = downloadTask;
                break;
            }

            var token = await completedDownload.ConfigureAwait(false);
            if (token is null)
            {
                // Clear only OUR pending marker here — and only if it still holds the version
                // THIS apply was authorized for. A check that slipped in before the apply won
                // the guard can have replaced it with a NEWER release (that's typically WHY the
                // source's version-match guard returned null); blindly clearing would wipe a
                // real, actionable update and its sidebar dot (Codex UPD-3b diff round 2). The
                // source's own cached UpdateInfo is left as-is either way: it is only ever
                // consulted behind the version-match guard, and the next check overwrites it.
                var clearedAuthorized = false;
                lock (_pendingLock)
                {
                    if (string.Equals(_pendingUpdate?.Version, pending.Version, StringComparison.Ordinal))
                    {
                        _pendingUpdate = null;
                        clearedAuthorized = true;
                    }
                }
                if (clearedAuthorized) RaisePendingUpdateVersionChanged(null);
                return CompleteApply(ctx, new ApplyResult { Outcome = ApplyOutcome.NotPending, Detail = "The update is no longer available." });
            }

            // Defense-in-depth re-check. With the flag held since before the download, no NEW work can
            // have started; this still catches a maintenance source that isn't gated by the flag.
            if (TryGetBlockers(out var raceBlockers))
            {
                return CompleteApply(ctx, Blocked(raceBlockers));
            }

            // LINEARIZATION POINT for the UPD-4b automatic opt-out: the LAST cancellation
            // observation before the irreversible Velopack hand-off, with deliberately NOTHING
            // between the check and the call. It used to sit ABOVE the blocker re-check, which put
            // a whole maintenance-gate poll inside the window — an opt-out landing during that
            // poll went unobserved and the restart happened anyway (Codex diff r6). A cancel that
            // flips after this line loses by definition: WaitExitThenApplyUpdates cannot be
            // un-called, which is the documented "too late by nature" boundary. Pinned by
            // Apply_CancelDuringPostDownloadGatePoll_NeverReachesTheHandoff, whose gate source
            // cancels the token from INSIDE the second poll.
            ct.ThrowIfCancellationRequested();
            _source.ApplyDownloadedUpdate(token);
            // UPD-3c: one-shot visible-restart flag — the relaunched build shows its window
            // (and the What's-new dialog) even when StartMinimized is on; an update restart is
            // an attended action, not a cold boot. Written only AFTER the updater hand-off
            // succeeded (a failed hand-off must not leak a spurious visible launch), flushed
            // explicitly (settings writes are debounced 250 ms), and best-effort — the update
            // matters more than the confirmation.
            try
            {
                _settings.SetBool(AppDefaults.ShowWindowAfterUpdateRestart, true);
                _settings.FlushOrThrow();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not persist the visible-restart flag; the relaunch may start minimized");
            }
            _appLifetime?.RequestQuitForUpdate();
            Logger.Information("Update apply initiated; graceful shutdown requested.");
            var initiated = new ApplyResult { Outcome = ApplyOutcome.ApplyInitiated };
            // Guard + gate scope stay held: the process is exiting and start paths must keep
            // refusing new work. IsApplyInFlight stays true; ApplyCompleted still fires (guard
            // winner) so a re-attached page can render "restarting…".
            RaiseApplyCompleted(initiated);
            return initiated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !abandonedHold)
        {
            ReleaseApply(ctx);
            Logger.Information("Update apply cancelled by caller.");
            throw;
        }
        catch (Exception ex) when (!abandonedHold)
        {
            Logger.Warning(ex, "Update apply failed.");
            return CompleteApply(ctx, new ApplyResult { Outcome = ApplyOutcome.Error, Detail = ex.Message });
        }
    }

    // True (with the blocker descriptions) when the maintenance gate reports a task in flight.
    // No gate (unit tests) ⇒ nothing blocking.
    private bool TryGetBlockers(out IReadOnlyList<string> blockers)
    {
        var status = _maintenanceGate?.Check();
        if (status is { CanProceed: false })
        {
            blockers = status.ActiveBlockers;
            return true;
        }

        blockers = Array.Empty<string>();
        return false;
    }

    private static ApplyResult Blocked(IReadOnlyList<string> blockers) => new()
    {
        Outcome = ApplyOutcome.Blocked,
        Detail = blockers.Count > 0 ? string.Join(", ", blockers) : "A task is in progress.",
        ActiveBlockers = blockers,
    };

    // ---- UPD-2 apply-state plumbing ------------------------------------------------------------

    // Releases the apply guard IF ctx is still the active apply: swaps the reference out,
    // disposes the gate scope, and raises ApplyInFlightChanged(false). Idempotent AND
    // generation-safe — the CAS fails for a stale context, so the late abandoned-download
    // observer can never release a NEWER apply's guard (Codex round-2 blocker).
    private void ReleaseApply(ApplyContext ctx)
    {
        if (Interlocked.CompareExchange(ref _activeApply, null, ctx) == ctx)
        {
            ctx.DisposeGateScope();
            RaiseApplyInFlightChanged(false);
        }
    }

    // Terminal path for guard-winning exits that release: release + raise ApplyCompleted
    // (exactly once per guard winner — see the IUpdateService contract).
    private ApplyResult CompleteApply(ApplyContext ctx, ApplyResult result)
    {
        ReleaseApply(ctx);
        RaiseApplyCompleted(result);
        return result;
    }

    // How a single download attempt ended (UPD-3). "Succeeded" means the task COMPLETED —
    // success vs fault is resolved by awaiting it afterwards, so a non-stall fault flows to the
    // existing catch (Error, no retry) exactly as before the retry loop existed.
    private enum AttemptOutcome { Succeeded, CallerCancelled, StalledAcknowledged, StalledAbandoned }

    // One download attempt under the stall watchdog (extracted from the UPD-2 inline block).
    // Progress callbacks are byte-driven, so "no callback for _stallTimeout" means a dead
    // connection; Velopack's own HTTP timeout demonstrably does not bound the body read
    // (2026-07-03 incident: 0-byte partial, 30+ min, no fault). Polling WhenAny (not a bare
    // await) also guarantees this method returns even if Velopack ignores cancellation.
    private async Task<(AttemptOutcome Outcome, Task<object?> DownloadTask)> RunDownloadAttemptAsync(
        ApplyContext ctx, AttemptState state, RemoteUpdateInfo pending, CancellationToken ct)
    {
        // Liveness starts at download start — backoff time never counts toward the stall window.
        state.Touch(Environment.TickCount64);
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var downloadTask = _source.DownloadPendingUpdateAsync(
            pending, p => OnDownloadProgress(ctx, state, pending.Version, p), stallCts.Token);

        var stalled = false;
        while (!downloadTask.IsCompleted)
        {
            var first = await Task.WhenAny(
                downloadTask, Task.Delay(_stallPollInterval, CancellationToken.None)).ConfigureAwait(false);
            if (first == downloadTask) break;

            if (ct.IsCancellationRequested)
            {
                // Caller cancelled: the linked stallCts already carries it to the download;
                // fall through to the shared grace wait below.
                break;
            }

            if (Environment.TickCount64 - state.LastCallbackTicksSnapshot() > _stallTimeout.TotalMilliseconds)
            {
                Logger.Warning(
                    "Update download stalled: no data received for {Seconds:F0}s — cancelling (v{Version}, attempt {Attempt}/{Max})",
                    _stallTimeout.TotalSeconds, pending.Version, state.AttemptNumber, MaxDownloadAttempts);
                stallCts.Cancel();
                stalled = true;
                break;
            }
        }

        if (!downloadTask.IsCompleted)
        {
            // Grace window: give the (cancelled) download a moment to acknowledge.
            await Task.WhenAny(downloadTask, Task.Delay(_stallGrace, CancellationToken.None)).ConfigureAwait(false);
        }

        if (!downloadTask.IsCompleted) return (AttemptOutcome.StalledAbandoned, downloadTask);
        // Caller cancellation OUTRANKS the stall classification: if the caller cancelled while
        // a watchdog-stalled attempt was acknowledging, this must surface as OCE — on the final
        // attempt a StalledAcknowledged classification would swallow it into the exhausted
        // Error result, violating the interface contract (Codex diff round 1).
        if (ct.IsCancellationRequested) return (AttemptOutcome.CallerCancelled, downloadTask);
        // Acknowledged stall = the task ended CANCELED in response to the watchdog — the only
        // teardown shape that is safely retryable. A task that RAN TO COMPLETION despite the
        // stall flag (completion raced the watchdog) is a success — the staged package is
        // valid. A task that FAULTED after the stall-cancel classifies as Succeeded so the
        // outer await surfaces the fault to the existing catch: Error, NO retry — faults are
        // never retried, stall-adjacent or not.
        if (stalled && downloadTask.IsCanceled) return (AttemptOutcome.StalledAcknowledged, downloadTask);
        return (AttemptOutcome.Succeeded, downloadTask);
    }

    // Download progress callback for ONE attempt. Writes land in THIS attempt's state object
    // unconditionally — if the attempt (or the whole apply) has been superseded, the object is
    // orphaned and no reader consults it, so a stale callback can't corrupt a newer attempt's
    // progress or liveness (UPD-3; an int stamp checked before the write still had a TOCTOU).
    // Event/log emission additionally requires that this apply still owns the guard AND this
    // attempt is still current, checked immediately before the raise; subscribers reconcile
    // against snapshots on top (events are notifications, not truth).
    private void OnDownloadProgress(ApplyContext ctx, AttemptState state, string version, int percent)
    {
        var previous = state.ExchangeProgress(percent, Environment.TickCount64);
        if (percent == previous) return;

        if (!ReferenceEquals(Volatile.Read(ref _activeApply), ctx)) return;
        if (!ReferenceEquals(ctx.CurrentAttempt, state)) return;

        // One log line per 10%-bucket crossing (plus the first callback): enough for a support
        // log to distinguish slow from wedged without a line per percent.
        if (previous < 0 || percent / 10 != previous / 10)
        {
            Logger.Information("Update download progress: {Percent}% (v{Version}, attempt {Attempt})",
                percent, version, state.AttemptNumber);
        }

        RaiseApplyProgressChanged(percent);
    }

    // Observes a download task the apply path no longer awaits: logs its eventual outcome
    // (also preventing an unobserved-task fault). ONLY the abandoned observer releases the
    // guard (the un-wedge path that re-enables an open Updates page without a restart) — for an
    // acknowledged stall the main path owns the lifecycle, and under UPD-3 it may be RETRYING
    // inside the same context; a release here would hand the guard to a concurrent apply
    // mid-retry.
    private void ObserveDownloadOutcome(Task<object?> downloadTask, ApplyContext ctx, string version, bool abandoned)
    {
        _ = downloadTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Logger.Warning(t.Exception?.GetBaseException(),
                    "{Kind} update download (v{Version}) finished with an error",
                    abandoned ? "Abandoned" : "Stalled", version);
            }
            else
            {
                Logger.Information("{Kind} update download (v{Version}) finished (status={Status})",
                    abandoned ? "Abandoned" : "Stalled", version, t.Status);
            }
            if (abandoned) ReleaseApply(ctx);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Event raisers: subscriber exceptions are contained (a UI handler bug must not corrupt the
    // apply path), and events may fire on thread-pool threads — subscribers marshal.
    private void RaiseApplyProgressChanged(int percent)
    {
        try { ApplyProgressChanged?.Invoke(percent); }
        catch (Exception ex) { Logger.Warning(ex, "ApplyProgressChanged subscriber threw"); }
    }

    private void RaiseApplyCompleted(ApplyResult result)
    {
        try { ApplyCompleted?.Invoke(result); }
        catch (Exception ex) { Logger.Warning(ex, "ApplyCompleted subscriber threw"); }
    }

    private void RaiseApplyInFlightChanged(bool inFlight)
    {
        try { ApplyInFlightChanged?.Invoke(inFlight); }
        catch (Exception ex) { Logger.Warning(ex, "ApplyInFlightChanged subscriber threw"); }
    }

    private void RaiseApplyDownloadRetrying(int attempt, int maxAttempts)
    {
        try { ApplyDownloadRetrying?.Invoke(attempt, maxAttempts); }
        catch (Exception ex) { Logger.Warning(ex, "ApplyDownloadRetrying subscriber threw"); }
    }

    private void RaisePendingUpdateVersionChanged(string? version)
    {
        try { PendingUpdateVersionChanged?.Invoke(version); }
        catch (Exception ex) { Logger.Warning(ex, "PendingUpdateVersionChanged subscriber threw"); }
    }

    /// <summary>
    /// Per-apply ownership context (UPD-2). The instance identity is the generation: guard
    /// release and the abandoned-download observer key off "am I still the active context"
    /// (reference CAS in <see cref="ReleaseApply"/>), so a late continuation from an abandoned
    /// download can never mutate a newer apply's state. Progress/liveness live in per-attempt
    /// <see cref="AttemptState"/> objects (UPD-3): <see cref="BeginAttempt"/> swaps in a fresh
    /// one for each download attempt, and all snapshots read only the CURRENT one — a stale
    /// callback writes into an orphaned object nobody reads.
    /// </summary>
    private sealed class ApplyContext
    {
        private IDisposable? _gateScope;
        private AttemptState? _currentAttempt;

        public ApplyContext(long nowTicks) => _currentAttempt = new AttemptState(1, nowTicks);

        /// <summary>Swaps in a fresh attempt state (progress -1 → <c>ApplyProgressPercent</c>
        /// reads null until the new attempt's first callback) and returns it.</summary>
        public AttemptState BeginAttempt(int attemptNumber, long nowTicks)
        {
            var state = new AttemptState(attemptNumber, nowTicks);
            Volatile.Write(ref _currentAttempt, state);
            return state;
        }

        public AttemptState? CurrentAttempt => Volatile.Read(ref _currentAttempt);

        // Synchronous-prefix only (single thread) — no interlocking needed for the write; the
        // exchange in DisposeGateScope makes disposal idempotent across main-path/observer races.
        public void AttachGateScope(IDisposable? scope) => _gateScope = scope;

        public void DisposeGateScope() => Interlocked.Exchange(ref _gateScope, null)?.Dispose();

        public int ProgressSnapshot() => Volatile.Read(ref _currentAttempt)?.ProgressSnapshot() ?? -1;
    }

    /// <summary>
    /// Mutable state of ONE download attempt (UPD-3). Callbacks capture their attempt's
    /// instance and write into it unconditionally; only the context's current instance is ever
    /// read, which closes the check-then-write race an attempt counter alone would leave open.
    /// </summary>
    private sealed class AttemptState
    {
        private int _progressPercent = -1;   // -1 = no callback yet (surfaces as null)
        private long _lastCallbackTicks;     // Environment.TickCount64 domain

        public AttemptState(int attemptNumber, long nowTicks)
        {
            AttemptNumber = attemptNumber;
            _lastCallbackTicks = nowTicks;
        }

        public int AttemptNumber { get; }

        /// <summary>Records a progress callback: refreshes liveness FIRST, then swaps the percent
        /// in, returning the previous value (for change/bucket detection).</summary>
        public int ExchangeProgress(int percent, long nowTicks)
        {
            Volatile.Write(ref _lastCallbackTicks, nowTicks);
            return Interlocked.Exchange(ref _progressPercent, percent);
        }

        public void Touch(long nowTicks) => Volatile.Write(ref _lastCallbackTicks, nowTicks);

        public int ProgressSnapshot() => Volatile.Read(ref _progressPercent);

        public long LastCallbackTicksSnapshot() => Volatile.Read(ref _lastCallbackTicks);
    }

    // Computed once per process. Reading the entry assembly's Version
    // is cheap but not free, and the value can't change at runtime —
    // it's baked at build time from the csproj <Version>.
    private static readonly Lazy<string> CurrentVersionCached = new(() =>
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    });
}
