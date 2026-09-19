using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Data;
using VoiceWink.Services.Maintenance;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Privacy;

/// <summary>Outcome of a data-erasure attempt.</summary>
public sealed record EraseResult(bool Success, string Message);

/// <summary>
/// GDPR right-to-erasure (Art. 17). Deletes the user's personal data and then exits the app.
///
/// <para><b>Allowlist, not recursive root delete.</b> The portable installer also lives in
/// <c>%LOCALAPPDATA%\VoiceWink</c> (the same dir as <see cref="AppPaths.RootDir"/>), so a
/// <c>Directory.Delete(root, recursive)</c> would destroy the running app's own binaries. We
/// instead delete only the specific data artifacts the app owns (settings + DB + the Logs /
/// Recordings / Images / References / Models folders + their stale temp/journal/backup
/// leftovers) and leave everything else under the root untouched.</para>
///
/// <para><b>Ordering matters.</b> The maintenance gate is polled FIRST — if it never clears we
/// return a failure <see cref="EraseResult"/> (<c>Success=false</c>) without touching anything, so an aborted erase never leaves the
/// still-running app with persistence disabled. Only once we are committed do we suppress
/// settings persistence (so DI-disposal-on-exit can't resurrect <c>settings.json</c>), release
/// SQLite handles, stop the local engines' children (REL-39 — they hold the model file under
/// <c>Models</c>; fail-soft, every wait bounded — about 45 s worst case with the coordinator free,
/// up to a minute behind a busy one, with no cancellation; run on the pool so the dialog's thread is
/// never the one blocked), close the log sink, and delete.</para>
///
/// <para><b>A second call after a committed attempt re-runs the post-commit pass (REL-38,
/// 2026-09-19).</b> The exclusive erasure lease is held to process exit once an attempt has
/// committed (F19), so every later <see cref="IMaintenanceGate.TryBeginErasure"/> is refused — and
/// the refusal used to be reported as "an update is being applied", which is what the owner's
/// second "Delete everything" after a partial failure said on a Debug build. The service now owns
/// its in-process attempt state: a call while one is in flight answers "a deletion is already in
/// progress"; a call after a COMMITTED attempt skips the lease, the gate poll and the cleanup
/// quiesce (all still held) and re-runs the idempotent post-commit steps, so a retry frees a
/// handle that has since returned to the pool and fails again BY NAME on a file this process
/// holds. Two facts make the discrimination race-free, and a change to either is a violation:
/// this service is a DI <b>singleton</b> (<c>App.xaml.cs</c>) and it is the <b>only</b> production
/// caller of <c>TryBeginErasure</c> — so once the in-flight slot is ours and no attempt of ours has
/// committed, a gate refusal can only be the update-apply lease: an update being applied, or the
/// TRN-59 "Restart now" that reuses that lease for the seconds of its graceful quit and already
/// reads as an update on every other surface.</para>
/// </summary>
public sealed class DataErasureService
{
    private static ILogger Logger => Log.ForContext<DataErasureService>();

    private const int DefaultGatePollMs = 100;
    private const int DefaultGateTimeoutMs = 10_000;

    private readonly IMaintenanceGate _gate;
    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly SettingsService _settings;
    private readonly string _rootDir;
    private readonly Action _exitAction;
    private readonly Action _closeLogSink;
    private readonly Action _closeSentry;
    private readonly Func<Task<bool>>? _quiesceMediaCleanup;
    private readonly Action? _resumeMediaCleanup;
    private readonly Func<Task<bool>>? _quiesceLocalEngines;
    private readonly int _gatePollMs;
    private readonly int _gateTimeoutMs;

    // REL-38 attempt state (class doc). One DeleteAllAsync at a time in this process — the CAS is
    // what makes "a deletion is already in progress" a fact rather than a guess.
    private int _inFlight;
    // Written on a threadpool continuation (ConfigureAwait(false)), read at entry on the UI
    // thread — volatile so the hand-off is explicit rather than borrowed from task completion.
    // Set right before step 2; never cleared, because the lease it describes is held to exit.
    private volatile bool _committed;

    public DataErasureService(
        IMaintenanceGate gate,
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        SettingsService settings,
        // REQUIRED, deliberately (diff review round 3, High): a %TEMP% default let every
        // test that injected only rootDir silently sweep the REAL %TEMP% — which deleted
        // a real user report bundle on this machine. The compiler now forces every
        // caller to decide; production DI passes Path.GetTempPath() explicitly.
        string legacyTempDir,
        string? rootDir = null,
        Action? exitAction = null,
        Action? closeLogSink = null,
        Action? closeSentry = null,
        Func<Task<bool>>? quiesceMediaCleanup = null,
        Action? resumeMediaCleanup = null,
        int gatePollMs = DefaultGatePollMs,
        int gateTimeoutMs = DefaultGateTimeoutMs,
        // REL-39: stops whatever of ours holds a file under Models (the Parakeet children) so the
        // delete pass can remove it; null = nothing to stop (tests, a build without the pcpp backend
        // still cancels the warm-up in DI). Fail-soft by contract — see step 3b.
        Func<Task<bool>>? quiesceLocalEngines = null)
    {
        _gate = gate;
        _dbFactory = dbFactory;
        _settings = settings;
        LegacyTempDir = legacyTempDir;
        _quiesceMediaCleanup = quiesceMediaCleanup;
        _resumeMediaCleanup = resumeMediaCleanup;
        _quiesceLocalEngines = quiesceLocalEngines;
        _rootDir = rootDir ?? AppPaths.RootDir;
        _exitAction = exitAction ?? (() => Environment.Exit(0));
        _closeLogSink = closeLogSink ?? Log.CloseAndFlush;
        _closeSentry = closeSentry ?? (() => global::VoiceWink.Services.System.SentryInitializer.Shutdown());
        _gatePollMs = gatePollMs;
        _gateTimeoutMs = gateTimeoutMs;
    }

    /// <summary>
    /// Erase all of the user's data. On success the app exits (via the injected exit action) and
    /// this method does not return; on a busy gate or a delete failure it returns a
    /// <see cref="EraseResult"/> describing the problem (it never throws and never falsely reports
    /// success). A call after a committed attempt re-runs the post-commit pass (class doc).
    /// </summary>
    public async Task<EraseResult> DeleteAllAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return new EraseResult(false,
                "Can't delete your data right now — a deletion is already in progress. Please try again in a moment.");
        }

        try
        {
            var firstPass = !_committed;
            if (firstPass)
            {
                var abort = await BeginAsync(ct).ConfigureAwait(false);
                if (abort != null) return abort;
                _committed = true;
            }
            return await CommitAndDeleteAsync(firstPass, ct).ConfigureAwait(false);
        }
        finally
        {
            // The production exit action never returns; the test no-op does. Reset on every
            // return and every throw so a later call is never told a deletion is in progress
            // when none is.
            Volatile.Write(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// Steps 0–1b: the exclusive lease, the gate poll, the cleanup quiesce. Returns the abort
    /// result (lease released, nothing touched) or null when the attempt may commit — the lease is
    /// then held to process exit. Every exception on this path releases the lease before it
    /// propagates: a leaked lease would make every later attempt read as an update apply.
    /// </summary>
    private async Task<EraseResult?> BeginAsync(CancellationToken ct)
    {
        // 0. Exclusive lease FIRST (F19, 2026-07-14). Acquired atomically BEFORE the gate poll
        //    so an update apply can't slip in during the poll — and so a pending update's
        //    "wait for exit then apply + relaunch" watcher can't turn erasure's own
        //    Environment.Exit into an update relaunch (the reachable contradiction this fixes:
        //    "delete everything" that silently reinstalls and reopens the app). While held,
        //    TryBeginUpdateApply refuses and every start-path sees IsErasing via
        //    App.IsExclusiveMaintenanceActive(). Held through the destructive commit + exit;
        //    released ONLY on a pre-commit abort (below). A refusal here can only be the
        //    update-apply lease: our own attempts are serialised by _inFlight and a committed one
        //    never reaches this method (class doc).
        if (!_gate.TryBeginErasure(out var erasureLease))
        {
            return new EraseResult(false,
                "Can't delete your data right now — an update is being applied. Please try again in a moment.");
        }

        try
        {
            // 1. Gate poll. If it never clears (recording / transcribe / model-download /
            //    paste-restore / an in-flight cleanup pass), abort WITHOUT suppressing
            //    persistence or deleting — and release the exclusive lease.
            var blockers = await WaitForGateAsync(ct).ConfigureAwait(false);
            if (blockers != null)
            {
                erasureLease?.Dispose();
                var why = blockers.Count > 0 ? string.Join(", ", blockers) : "a background task is busy";
                return new EraseResult(false, $"Can't delete your data right now — {why}. Please try again in a moment.");
            }

            // 1b. Quiesce the reference-copy cleanup worker (ENH-6e) BEFORE anything
            //     irreversible: a scheduled release-cleanup mid-query can hold — or, via
            //     SQLite's create-on-open, even RECREATE — voicewink.db after the allowlist
            //     delete below. (Complementary to the F19 cleanup-pass leases above: those
            //     gate the SWEEP + history cleanup passes; this drains the per-release
            //     cleanups ReferencePersistence schedules itself.) On timeout ABORT — never
            //     proceed-and-hope: a parked op resuming after the delete would turn our
            //     reported success into a lie — resume cleanup so the still-running session
            //     behaves normally, and release the lease (still a pre-commit abort).
            if (_quiesceMediaCleanup != null && !await _quiesceMediaCleanup().ConfigureAwait(false))
            {
                _resumeMediaCleanup?.Invoke();
                erasureLease?.Dispose();
                return new EraseResult(false,
                    "Can't delete your data right now — a background cleanup task is busy. Please try again in a moment.");
            }

            return null;
        }
        catch
        {
            // ct cancelled during the poll, or the quiesce threw — release the lease before
            // propagating (the lease is held to exit only past a COMMIT).
            erasureLease?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Steps 2–6, every one idempotent so a call after a committed attempt can run them again
    /// under the lease that attempt still holds. <paramref name="firstPass"/> is false on such a
    /// re-run: the DbContext probe is skipped — it only confirms the factory is healthy and never
    /// released a handle (constructing a context opens no connection; <c>ClearAllPools</c> is what
    /// frees the file), so a re-run has nothing to learn from it.
    /// </summary>
    private async Task<EraseResult> CommitAndDeleteAsync(bool firstPass, CancellationToken ct)
    {
        // 2. Committed — the exclusive lease is now held to process exit (NOT released on the
        //    success path; the process is ending). Quiesce settings persistence so exit can't
        //    rewrite settings.json.
        _settings.SuppressPersistence();

        // 3. Release SQLite file handles. Disposing a context returns its connection to the pool
        //    still holding the file handle, so ClearAllPools() is what actually frees the
        //    .db / -wal / -journal locks; the probe just confirms the factory is healthy first.
        if (firstPass)
        {
            try { await using var _ = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warning(ex, "DbContext probe failed during erasure"); }
        }
        try { SqliteConnection.ClearAllPools(); } catch (Exception ex) { Logger.Warning(ex, "ClearAllPools failed during erasure"); }

        // 3b. REL-39: stop the local engines' children that hold a file under Models — the resident
        //     parakeet-server and the TRN-49 warm-up child hold the GGUF open, the migration download
        //     and the verification hash hold files there too, and an in-flight Whisper self-test
        //     could write gpu-warmup.json back after the pass below removed it. BEFORE the sink
        //     closes, so a refusal is on disk when the erasure then fails. FAIL-SOFT, never an abort:
        //     past the commit the user's personal data is already going, and aborting here would keep
        //     settings, keys and history on disk to protect a model payload — a child that could not
        //     be confirmed dead simply makes Models fail its delete BY NAME below (REL-38). Inside the
        //     committed pass so the REL-38 re-run tries again (idempotent: a retired server has no
        //     child, a cancelled warm-up reports Cancelled). Safe by the time we get here: the gate
        //     poll proved no decode is in flight and IsErasing refuses every new start. On the POOL
        //     (self-review, concurrency lens): on an idle machine nothing above has yielded yet, so
        //     this still runs on the dialog's thread, and the retire's confirm is a blocking
        //     WaitForExit (up to 5 s) — a pool hop keeps that off the UI thread.
        if (_quiesceLocalEngines != null)
        {
            try
            {
                if (!await Task.Run(_quiesceLocalEngines).ConfigureAwait(false))
                    Logger.Warning("Local-engine quiesce did not confirm during erasure - Models may fail its delete by name");
            }
            catch (Exception ex) { Logger.Warning(ex, "Local-engine quiesce failed during erasure"); }
        }

        // 4. Close the Serilog file sink so the active .log handle is released.
        try { _closeLogSink(); } catch (Exception ex) { Logger.Warning(ex, "Closing log sink failed during erasure"); }

        // 4b. Shut Sentry down so its background transport can't write to sentry-cache while we delete
        //     it — otherwise a queued-envelope flush could re-create the dir mid-erase (Codex review).
        try { _closeSentry(); } catch (Exception ex) { Logger.Warning(ex, "Shutting down Sentry failed during erasure"); }

        // 5. Delete the allowlist; collect (don't throw on) failures.
        var failures = new List<string>();
        foreach (var target in BuildTargets(_rootDir))
            DeleteTarget(target, failures);

        // REL-17: report bundles written by pre-Reports-dir builds live in %TEMP% — erase
        // them through the SAME failure-counting path (an erasure must never silently
        // best-effort personal data; Codex plan round 4). Enumeration failure counts as a
        // failure too (diff review High): erasure must not report success while raw
        // legacy reports may remain.
        var failuresBeforeLegacy = failures.Count;
        if (TryBuildLegacyTempReportTargets(LegacyTempDir, out var legacyReportTargets))
        {
            foreach (var target in legacyReportTargets)
                DeleteTarget(target, failures);
        }
        else
        {
            failures.Add("voicewink-report-* (temp folder)");
        }
        var hasLegacyTempFailures = failures.Count > failuresBeforeLegacy;

        if (failures.Count > 0)
        {
            // Persistence is suppressed and the log sink is closed by this point, so the app can't
            // keep running normally — tell the user to close it and finish the cleanup by hand.
            // Remediation must name EVERY leftover location (diff review round 2): the temp
            // reports live outside the VoiceWink folder the base message points at, so they get
            // their own sentence and never appear in the folder's list (REL-38, Gemini plan round).
            // The names are NAMED (REL-38): the sink is dead, so this sentence is the only place the
            // cause of a leftover can surface — and "remain" claims nothing about why (an ACL
            // failure and a held handle read the same from here).
            var rootLeftovers = failures.Take(failuresBeforeLegacy).ToList();
            var message = rootLeftovers.Count > 0
                ? $"Most of your data was deleted, but these remain: {string.Join(", ", rootLeftovers)}. " +
                  $"Please close VoiceWink and delete this folder manually: {_rootDir}"
                : "Most of your data was deleted.";
            if (hasLegacyTempFailures)
            {
                message += rootLeftovers.Count > 0
                    ? " Also delete any files named voicewink-report-*.zip from your Windows temp folder (%TEMP%)."
                    : " Please close VoiceWink and delete any files named voicewink-report-*.zip from your Windows temp folder (%TEMP%).";
            }
            return new EraseResult(false, message);
        }

        // 6. Done — exit (prod: Environment.Exit, never returns; tests: injected no-op).
        _exitAction();
        return new EraseResult(true, "All your data has been deleted.");
    }

    /// <summary>
    /// The concrete set of data paths erasure deletes — files and directories the app owns under
    /// <paramref name="root"/>, never <paramref name="root"/> itself. Internal so a test can pin
    /// that the set covers the <see cref="AppPaths"/> data members and excludes app binaries.
    /// </summary>
    internal static IReadOnlyList<string> BuildTargets(string root)
    {
        var targets = new List<string>
        {
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "settings.json.bak"),
            // SettingsService's fixed-name quarantine of a corrupt primary — holds the same
            // personal data as settings.json itself, so erasure must cover it (the original
            // timestamped quarantine was dropped for exactly this gap, CR-1 Codex R2).
            Path.Combine(root, "settings.json.corrupt"),
            Path.Combine(root, "voicewink.db"),
            Path.Combine(root, "voicewink.db-journal"),
            Path.Combine(root, "voicewink.db-wal"),
            Path.Combine(root, "voicewink.db-shm"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Recordings"),
            Path.Combine(root, "Images"),
            Path.Combine(root, "References"),
            Path.Combine(root, "Models"),
            Path.Combine(root, "sentry-cache"),
            // REL-17: problem-report bundles (raw-mode bundles hold verbatim prompts +
            // voice audio — first-class personal data).
            Path.Combine(root, "Reports"),
            // TRN-49: the GPU warm-up marker — not personal data, but erasure's contract is
            // every app-owned artifact under RootDir, and a survivor would suppress the
            // re-warm a fresh start should get (self-review, regression lens).
            Path.Combine(root, "gpu-warmup.json"),
            Path.Combine(root, "gpu-warmup.json.tmp"),
        };

        if (Directory.Exists(root))
        {
            // Stale atomic-write temp files + schema-repair backups (see SettingsService.SaveOrThrow
            // and App.TryBackupDatabase).
            targets.AddRange(Directory.EnumerateFiles(root, "settings.json.*.tmp"));
            targets.AddRange(Directory.EnumerateFiles(root, "voicewink.db.schema-backup-*"));
        }

        return targets;
    }

    /// <summary>The legacy %TEMP% report location — ctor-injected (required), see the
    /// constructor note.</summary>
    internal string LegacyTempDir { get; }

    /// <summary>Report bundles the pre-REL-17 dialog wrote to %TEMP% (and any staged
    /// leftovers). Returns false when the enumeration itself failed — an absent temp dir
    /// is success-with-nothing, but a dir we could not LIST may still hold reports, and
    /// the caller must count that as an erasure failure (diff review High). Internal so
    /// tests can pin the pattern set and the failure contract.</summary>
    internal static bool TryBuildLegacyTempReportTargets(string tempDir, out IReadOnlyList<string> targets)
    {
        var found = new List<string>();
        targets = found;
        try
        {
            // Enumerate-and-catch, never Exists-probe (diff round 5): Directory.Exists
            // answers false for some ACCESS failures, which would read as "nothing
            // there" and let erasure claim success over unreachable raw reports. Only a
            // genuine not-found is success-with-nothing.
            found.AddRange(Directory.EnumerateFiles(tempDir, "voicewink-report-*.zip"));
            found.AddRange(Directory.EnumerateFiles(tempDir, "voicewink-report-*.zip.tmp"));
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true; // genuinely absent — nothing to erase
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Legacy temp report enumeration failed during erasure");
            return false;
        }
    }

    private static void DeleteTarget(string path, List<string> failures)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to delete a data target during erasure");
            failures.Add(Path.GetFileName(path));
        }
    }

    /// <summary>Polls the gate up to the timeout. Returns null when clear, or the blocker list on timeout.</summary>
    private async Task<IReadOnlyList<string>?> WaitForGateAsync(CancellationToken ct)
    {
        var elapsed = 0;
        while (true)
        {
            var status = _gate.Check();
            if (status.CanProceed) return null;
            if (elapsed >= _gateTimeoutMs) return status.ActiveBlockers;
            await Task.Delay(_gatePollMs, ct).ConfigureAwait(false);
            elapsed += _gatePollMs;
        }
    }
}
