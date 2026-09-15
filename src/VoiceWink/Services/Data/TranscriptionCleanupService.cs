using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// Auto-delete old transcription entries — plus, since REL-17, the app's UNIFIED retention
/// pass: an unconditional, off-thread sweep of debug-retained recordings, crash-orphaned
/// root WAVs (ledger-aware hard 7 days), prompt-trace files, and report bundles that runs
/// BEFORE the history-cleanup enable gate, so no diagnostics artifact outlives its bound
/// even with history cleanup disabled or a long-lived tray session.
/// </summary>
public sealed class TranscriptionCleanupService
{
    private static ILogger Logger => Log.ForContext<TranscriptionCleanupService>();

    /// <summary>Hard bound for diagnostics artifacts (debug WAVs, crash-orphaned root
    /// WAVs, trace files, report bundles) — matches the trace sweep's 7 days. Independent
    /// of the user's history retention.</summary>
    internal const int HardRetentionDays = 7;

    private readonly TranscriptionHistoryService _history;
    private readonly SettingsService _settings;
    private readonly RetainedWavLedger _wavLedger;
    private readonly ImageGenerationJobService _imageJob;

    // REL-17 (diff round 5): the sweep roots are ctor-REQUIRED, same rationale as
    // DataErasureService's legacyTempDir — a real-location default let any test calling
    // CleanupAsync sweep real user data. Production DI passes AppPaths/%TEMP% explicitly.
    // HIS-3 extends the same rule to the images root.
    internal string RecordingsRoot { get; }
    internal string ReportsRoot { get; }
    internal string LegacyTempRoot { get; }
    internal string ImagesRoot { get; }

    /// <summary>
    /// HIS-3 crash-orphan backstop: an UNREFERENCED PNG is swept only once it is at least this
    /// old. NOT the concurrency mechanism (that is the job-epoch protocol below) — it exists so
    /// a crashed commit's forever-unreferenced file still reaps, while a file from any brief
    /// non-coordinated window (or a hypothetical future non-job writer) survives long past any
    /// realistic save→row-commit gap.
    /// </summary>
    internal static readonly TimeSpan OrphanImageAgeGrace = TimeSpan.FromHours(1);

    // HIS-3 test seams, the _recycleImageFile convention: internal fields with production
    // defaults, never reassigned outside tests. The sweep tests bind the recycle/clock/
    // timestamp seams AND pass a temp ImagesRoot, so no real user data, directory, or
    // Recycle Bin is ever touched by a test.
    internal Action<string> _recycleFile = RecycleToBin;
    internal Func<string, DateTime> _getCreationTimeUtc = File.GetCreationTimeUtc;
    internal Func<DateTime> _utcNow = () => DateTime.UtcNow;
    // Invoked between the row snapshot and the file enumeration — the deterministic hook the
    // race tests use to interleave a real job lifecycle into the sweep's window. Null in
    // production (explicit initializer: only tests assign it — CS0649 otherwise).
    internal Func<Task>? _afterSnapshotHook = null;

    public TranscriptionCleanupService(
        TranscriptionHistoryService history,
        SettingsService settings,
        RetainedWavLedger wavLedger,
        ImageGenerationJobService imageJob,
        string recordingsRoot,
        string reportsRoot,
        string legacyTempRoot,
        string imagesRoot)
    {
        _history = history;
        _settings = settings;
        _wavLedger = wavLedger;
        _imageJob = imageJob;
        RecordingsRoot = recordingsRoot;
        ReportsRoot = reportsRoot;
        LegacyTempRoot = legacyTempRoot;
        ImagesRoot = imagesRoot;
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        // REL-17 unified retention pass — UNCONDITIONAL (before the enable gate: these
        // bounds are the app's promises, not user preferences) and OFF-THREAD (the caller
        // is the UI dispatcher's hourly timer; these are synchronous filesystem walks —
        // Codex plan round 3). Each sweep is individually fail-soft so one failure never
        // starves the others.
        await Task.Run(() =>
        {
            SweepDebugRecordings();
            SweepOrphanedRootRecordings();
            PromptTraceLog.SweepOldTraces();
            try { Support.SupportBundle.SweepOldReports(ReportsRoot, LegacyTempRoot); }
            catch (Exception ex) { Logger.Warning(ex, "Report bundle sweep failed"); }
        }, ct).ConfigureAwait(false);

        // The table, never a literal (2026-09-13, when the default went OFF): a present-but-
        // malformed stored value falls back to the caller's literal, and `true` here would have
        // run a deletion the table says is off — the two-literal hazard AppDefaults.BoolDefault
        // documents, on the one setting that deletes user data.
        var enabled = _settings.GetBoolDefaulted(AppDefaults.IsTranscriptionCleanupEnabled);
        if (!enabled) return;

        var retentionMinutes = _settings.GetInt(AppDefaults.TranscriptionRetentionMinutes, 10080);
        var cutoff = DateTime.UtcNow.AddMinutes(-retentionMinutes);

        var deletedCount = await _history.DeleteOlderThanAsync(cutoff, ct).ConfigureAwait(false);
        if (deletedCount > 0)
        {
            Logger.Information("Cleaned up {Count} old transcriptions (retention: {Minutes}m)", deletedCount, retentionMinutes);
        }

        // Clean up old audio recording files using the same retention period
        CleanupRecordingFiles(cutoff);

        // Clean up orphaned image files (not referenced by any history entry)
        await CleanupOrphanedImageFilesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// REL-17: debug-retained WAVs age out after a HARD 7 days — independent of the
    /// keep-recordings toggle (so turning it off can never orphan files) and of history
    /// cleanup. The folder is skipped when it is a reparse point, and each file's
    /// kernel-resolved final path must land under the recordings root before deletion —
    /// destructive sweeps never trust lexical paths (Codex plan round 4).
    /// </summary>
    private void SweepDebugRecordings()
    {
        try
        {
            var debugDir = Path.Combine(RecordingsRoot, "Debug");
            if (!Directory.Exists(debugDir) || VerifiedFileAccess.IsReparsePointOrUnreadable(debugDir))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-HardRetentionDays);
            var deleted = 0;
            foreach (var file in Directory.GetFiles(debugDir, "*.wav"))
            {
                try
                {
                    if (File.GetCreationTimeUtc(file) >= cutoff) continue;
                    if (!VerifiedFileAccess.IsVerifiedUnder(file, RecordingsRoot)) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch { /* file in use, skip */ }
            }

            if (deleted > 0)
                Logger.Information("Cleaned up {Count} debug-retained recording(s) older than {Days} days", deleted, HardRetentionDays);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Debug recordings sweep failed");
        }
    }

    /// <summary>
    /// REL-17: the documented "7-day crash backstop" made REAL — the legacy root sweep
    /// runs only when history cleanup is enabled and uses the user's history retention
    /// (up to 30 days), so crash-leaked WAVs could previously sit forever. This pass is
    /// unconditional at the hard bound and EXCLUDES ledger-tracked paths: an armed Retry
    /// on a week-old tray session must never lose its WAV underneath the pill (Codex plan
    /// round 4). Non-recursive — Debug\ has its own sweep.
    /// </summary>
    private void SweepOrphanedRootRecordings()
    {
        try
        {
            if (!Directory.Exists(RecordingsRoot) || VerifiedFileAccess.IsReparsePointOrUnreadable(RecordingsRoot))
                return;

            var tracked = new HashSet<string>(_wavLedger.Snapshot(), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow.AddDays(-HardRetentionDays);
            var deleted = 0;
            foreach (var file in Directory.GetFiles(RecordingsRoot, "*.wav"))
            {
                try
                {
                    if (tracked.Contains(file)) continue;
                    if (File.GetCreationTimeUtc(file) >= cutoff) continue;
                    if (!VerifiedFileAccess.IsVerifiedUnder(file, RecordingsRoot)) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch { /* file in use, skip */ }
            }

            if (deleted > 0)
                Logger.Information("Cleaned up {Count} orphaned recording(s) older than {Days} days", deleted, HardRetentionDays);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Orphaned recordings sweep failed");
        }
    }

    private void CleanupRecordingFiles(DateTime cutoff)
    {
        try
        {
            var recordingsDir = RecordingsRoot;
            if (!Directory.Exists(recordingsDir)) return;
            // REL-17 diff review (High): this user-retention sweep is exactly as
            // destructive as the hard sweeps — same reparse-point + final-path guards,
            // or a junctioned Recordings root deletes external WAVs.
            if (VerifiedFileAccess.IsReparsePointOrUnreadable(recordingsDir)) return;

            // REL-17: the user-retention sweep now also spares ledger-tracked paths — a
            // short retention (e.g. 1 day) could otherwise delete an armed Retry's WAV
            // out from under the pill (same rule as the hard orphan sweep).
            var tracked = new HashSet<string>(_wavLedger.Snapshot(), StringComparer.OrdinalIgnoreCase);
            var deleted = 0;
            foreach (var file in Directory.GetFiles(recordingsDir, "*.wav"))
            {
                if (tracked.Contains(file)) continue;
                if (File.GetCreationTimeUtc(file) < cutoff)
                {
                    try
                    {
                        if (!VerifiedFileAccess.IsVerifiedUnder(file, recordingsDir)) continue;
                        File.Delete(file);
                        deleted++;
                    }
                    catch { /* file in use, skip */ }
                }
            }

            if (deleted > 0)
                Logger.Information("Cleaned up {Count} old recording files", deleted);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Recording file cleanup failed");
        }
    }

    /// <summary>
    /// Delete image files that are no longer referenced by any history entry. Unlike recording
    /// WAVs (always temp files), images delete only when their parent record is gone.
    /// <para><b>HIS-3 coordination (the correctness layer):</b> the pre-fix sweep snapshotted
    /// the referenced rows and then recycled every unreferenced PNG — a file saved in a job's
    /// save→row-commit window (or whose row committed after a stale snapshot) was recycled out
    /// from under its committed row. The sweep now runs only across a PROVEN idle window: it
    /// atomically captures the job slot's idle epoch before the snapshot (a live job skips the
    /// pass — hourly cadence catches up) and re-checks idle-at-the-same-epoch after
    /// materializing the enumeration, ABORTING if any job phase ran in between. A job starting
    /// after that check cannot own an enumerated path (full-GUID CreateNew filenames) and only
    /// ever references its own new files, so the delete loop needs no further checks. The
    /// <see cref="OrphanImageAgeGrace"/> then keeps even genuinely unreferenced files for an
    /// hour as the crash-orphan backstop. Internal for direct tests — <see cref="CleanupAsync"/>
    /// is the only production caller. The dir-level reparse guard mirrors the REL-17 rule for
    /// the recording sweeps (a junctioned Images root must not recycle external files).</para>
    /// </summary>
    internal async Task CleanupOrphanedImageFilesAsync(CancellationToken ct)
    {
        try
        {
            var imagesDir = ImagesRoot;
            if (!Directory.Exists(imagesDir) || VerifiedFileAccess.IsReparsePointOrUnreadable(imagesDir))
                return;

            if (!_imageJob.TryCaptureIdleEpoch(out var idleEpoch))
                return; // a job is live — it may commit an image at any moment; skip this pass

            // Get all image paths still referenced in the database
            var referencedPaths = await _history.GetAllImagePathsAsync(ct).ConfigureAwait(false);
            var referencedSet = new HashSet<string>(referencedPaths, StringComparer.OrdinalIgnoreCase);

            if (_afterSnapshotHook is { } hook)
                await hook().ConfigureAwait(false);

            // EVERY extension the saver can emit, from the one shared table. A glob narrower than
            // what GeneratedImageSaver writes does not fail loudly — it leaks those files forever,
            // silently. That is why the set is shared rather than restated here.
            var files = ImageBytesFormat.PersistedExtensions
                .SelectMany(ext => Directory.GetFiles(imagesDir, "*" + ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!_imageJob.IsIdleAtEpoch(idleEpoch))
            {
                Logger.Information("Orphan image sweep aborted — an image job ran during the pass");
                return; // the snapshot may be stale relative to an enumerated file's row
            }

            var cutoffUtc = _utcNow(); // ONE cutoff per pass — a slow pass must not age files into eligibility
            var deleted = 0;
            foreach (var file in files)
            {
                if (IsSweepableOrphan(referencedSet.Contains(file), TryGetCreationTimeUtc(file), cutoffUtc))
                {
                    try
                    {
                        _recycleFile(file);
                        deleted++;
                    }
                    catch { /* file in use, skip */ }
                }
            }

            if (deleted > 0)
                Logger.Information("Cleaned up {Count} orphaned image files", deleted);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Image file cleanup failed");
        }
    }

    /// <summary>
    /// The pure per-file sweep decision (HIS-3, test-pinned): only an UNREFERENCED file at least
    /// <see cref="OrphanImageAgeGrace"/> old sweeps; an unreadable creation time (null) is
    /// treated as YOUNG — never delete on unknown age.
    /// </summary>
    internal static bool IsSweepableOrphan(bool referenced, DateTime? createdUtc, DateTime nowUtc)
        => !referenced && createdUtc is { } created && nowUtc - created >= OrphanImageAgeGrace;

    private DateTime? TryGetCreationTimeUtc(string file)
    {
        try { return _getCreationTimeUtc(file); }
        catch { return null; }
    }

    private static void RecycleToBin(string file)
        => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
