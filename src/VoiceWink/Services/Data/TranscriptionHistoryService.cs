using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;

namespace VoiceWink.Services.Data;

/// <summary>
/// CRUD + search for transcription history.
/// </summary>
public sealed class TranscriptionHistoryService
{
    private static ILogger Logger => Log.ForContext<TranscriptionHistoryService>();

    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly ReferencePersistence _referencePersistence;

    // Test seam mirroring the SettingsService/_writeFile convention: lets a test observe that
    // media-file deletion runs ONLY after a successful row commit (F10). Production binds the
    // real recycle-to-bin path; never reassigned outside tests.
    internal Action<string> _recycleImageFile = RecycleImageFile;

    /// <summary>Raised when transcription history content changes.</summary>
    public event Action? HistoryChanged;

    /// <summary>Deterministic seam for the snapshot-race test: fires immediately after
    /// <see cref="DeleteAllAsync"/> materializes its row snapshot, so a test can insert a
    /// row into that exact window and prove it survives with its media. Null in production.</summary>
    internal Action? _afterDeleteAllSnapshotForTests = null;

    /// <summary>Which bulk deletion happened — the scope a subscriber matches against.</summary>
    internal enum BulkDeleteScope { All, Images, Text }

    /// <summary>
    /// Raised after a USER-initiated bulk deletion has COMMITTED, and before this class
    /// deletes the associated media. Handlers retire UI state that pins reference copies
    /// (the armed redo's ENH-6e live claims) and RETURN the paths that state was holding,
    /// which are folded into the same cleanup pass below — that is what makes the files
    /// gone by the time the delete returns, instead of riding fire-and-forget release
    /// cleanup (Codex plan r2).
    ///
    /// <para>Awaited, and raised on the caller's thread — which is a POOL thread, since
    /// these methods resume after <c>ConfigureAwait(false)</c>. A subscriber touching UI
    /// state must marshal itself and complete before returning.</para>
    ///
    /// <para>NOT raised by <c>DeleteOlderThanAsync</c> (automatic retention), by
    /// single-row <c>DeleteAsync</c>, or when the DB mutation threw. Deliberately
    /// separate from <see cref="HistoryChanged"/>.</para>
    /// </summary>
    internal event Func<BulkDeleteScope, Task<IReadOnlyList<string>>>? BulkDeleteCommitted;

    /// <summary>
    /// Raise <see cref="BulkDeleteCommitted"/> per subscriber, awaiting each and
    /// aggregating the reference paths they released. Each call is isolated: a throwing
    /// or hanging-to-failure handler must never turn a COMMITTED deletion into a
    /// reported failure. Type + HResult only in the log — handler messages can carry
    /// paths or personal content.
    /// </summary>
    private async Task<IReadOnlyList<string>> RaiseBulkDeleteCommittedAsync(BulkDeleteScope scope)
    {
        var handlers = BulkDeleteCommitted;
        if (handlers == null) return Array.Empty<string>();
        List<string>? released = null;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                var paths = await ((Func<BulkDeleteScope, Task<IReadOnlyList<string>>>)handler)(scope)
                    .ConfigureAwait(false);
                if (paths is { Count: > 0 })
                    (released ??= new List<string>()).AddRange(paths);
            }
            catch (Exception ex)
            {
                Logger.Warning("BulkDeleteCommitted subscriber threw: {ErrorType} (HResult=0x{HResult:X8})",
                    ex.GetType().Name, ex.HResult);
            }
        }
        return (IReadOnlyList<string>?)released ?? Array.Empty<string>();
    }

    /// <summary>Rows counted as image generations by the scoped deletes.</summary>
    private static readonly global::System.Linq.Expressions.Expression<Func<TranscriptionRecord, bool>> IsImageRow =
        r => r.WasEnhanced && (r.EnhancedText == TranscriptionRecord.ImageGeneratedMarker
                            || r.EnhancedText == TranscriptionRecord.ImageFailedMarker);

    /// <summary>Complement of <see cref="IsImageRow"/> — kept as its own expression
    /// because EF cannot invoke/negate a compiled expression inside a query.</summary>
    private static readonly global::System.Linq.Expressions.Expression<Func<TranscriptionRecord, bool>> IsTextRow =
        r => !r.WasEnhanced || (r.EnhancedText != TranscriptionRecord.ImageGeneratedMarker
                             && r.EnhancedText != TranscriptionRecord.ImageFailedMarker);

    /// <summary>Union of row-derived reference paths and the paths retired UI state was
    /// pinning — the single input to this operation's one cleanup pass.</summary>
    private static IReadOnlyList<string?> Combine(IReadOnlyList<string?> fromRows, IReadOnlyList<string> retired)
    {
        if (retired.Count == 0) return fromRows;
        var all = new List<string?>(fromRows.Count + retired.Count);
        all.AddRange(fromRows);
        all.AddRange(retired);
        return all;
    }

    /// <summary>
    /// Raise <see cref="HistoryChanged"/> per subscriber via GetInvocationList, each
    /// isolated — a throwing subscriber must neither convert a COMMITTED mutation into
    /// a reported failure (the ENH-6b reconcile bug class) nor starve later observers.
    /// The ONE notification helper for every history mutation.
    /// </summary>
    private void RaiseHistoryChanged()
    {
        var handlers = HistoryChanged;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                // Type + HResult only — subscriber messages can carry paths/personal
                // content into local logs and Sentry breadcrumbs (Codex diff R1).
                Logger.Warning("HistoryChanged subscriber threw: {ErrorType} (HResult=0x{HResult:X8})",
                    ex.GetType().Name, ex.HResult);
            }
        }
    }

    /// <summary>
    /// Delete an image file to the recycle bin instead of permanently.
    /// Falls back to permanent delete if recycle bin is unavailable.
    /// </summary>
    private static void RecycleImageFile(string path)
    {
        // Containment guard: the persisted ImageFilePath is a free-form string (could be corrupt,
        // legacy, or imported). Only ever touch files that canonicalize to inside the app's
        // Images directory, so a bad DB value can't delete an arbitrary file on disk.
        if (!MediaPathPolicy.TryResolveWithin(path, AppPaths.ImagesDir, out var safePath))
        {
            Logger.Warning("Skipping image delete: path is null/outside the Images directory");
            return;
        }

        try
        {
            FileSystem.DeleteFile(safePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch
        {
            try { File.Delete(safePath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Delete an audio recording file, guarded to the Recordings directory (the persisted
    /// AudioFilePath is a free-form string). Best-effort; permanent delete (not recycled).
    /// </summary>
    private static void DeleteRecordingFile(string path)
    {
        if (!MediaPathPolicy.TryResolveWithin(path, AppPaths.RecordingsDir, out var safePath))
        {
            Logger.Warning("Skipping recording delete: path is null/outside the Recordings directory");
            return;
        }
        try { File.Delete(safePath); } catch { /* best effort */ }
    }

    /// <summary>
    /// <paramref name="referencePersistence"/> is the SINGLE owner of reference-copy
    /// deletion policy (ENH-6b) — injected so this service and the coordinator can
    /// never apply divergent containment/remaining-reference rules. Null (legacy
    /// tests) lazily binds one to the production references root; such tests never
    /// carry ReferenceImagePath rows, so no reference IO occurs.
    /// </summary>
    public TranscriptionHistoryService(
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        ReferencePersistence? referencePersistence = null)
    {
        _dbFactory = dbFactory;
        _referencePersistence = referencePersistence
            ?? new ReferencePersistence(dbFactory, AppPaths.ReferencesDir);
    }

    public async Task SaveAsync(TranscriptionRecord transcription, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        db.TranscriptionRecords.Add(transcription);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Logger.Debug("Saved transcription #{Id}", transcription.Id);
        RaiseHistoryChanged();
    }

    public async Task UpdateAsync(TranscriptionRecord transcription, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        db.TranscriptionRecords.Update(transcription);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Logger.Debug("Updated transcription #{Id}", transcription.Id);
        RaiseHistoryChanged();
    }

    public Task<List<TranscriptionRecord>> GetPageAsync(int page, int pageSize = 50, CancellationToken ct = default)
        => GetPageAsync(page, pageSize, null, ct);

    /// <param name="imagesOnly">null = all, true = images only, false = text only</param>
    public async Task<List<TranscriptionRecord>> GetPageAsync(int page, int pageSize, bool? imagesOnly, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        IQueryable<TranscriptionRecord> query = db.TranscriptionRecords.AsNoTracking();
        query = ApplyTypeFilter(query, imagesOnly);
        return await query
            .OrderByDescending(t => t.Timestamp)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <param name="imagesOnly">null = all, true = images only, false = text only</param>
    public async Task<int> GetCountAsync(bool? imagesOnly, CancellationToken ct = default)
    {
        if (imagesOnly == null) return await GetCountAsync(ct).ConfigureAwait(false);
        return imagesOnly.Value
            ? await GetImageCountAsync(ct).ConfigureAwait(false)
            : await GetTextCountAsync(ct).ConfigureAwait(false);
    }

    private static IQueryable<TranscriptionRecord> ApplyTypeFilter(IQueryable<TranscriptionRecord> query, bool? imagesOnly)
    {
        if (imagesOnly == true)
            return query.Where(r => r.WasEnhanced && (r.EnhancedText == TranscriptionRecord.ImageGeneratedMarker || r.EnhancedText == TranscriptionRecord.ImageFailedMarker));
        if (imagesOnly == false)
            return query.Where(r => !r.WasEnhanced || (r.EnhancedText != TranscriptionRecord.ImageGeneratedMarker && r.EnhancedText != TranscriptionRecord.ImageFailedMarker));
        return query;
    }

    /// <summary>
    /// ENH-6: newest generated image whose file is still usable as a reference —
    /// SUCCESSFUL image rows only (never the failed marker), non-null path, and the
    /// shared usable-app-image predicate (MediaPathPolicy containment + File.Exists).
    /// Scan capped at the 25 newest image rows so a folder of deleted files doesn't
    /// walk the whole table. Returns the canonical full path, or null.
    /// </summary>
    public Task<string?> TryGetLatestUsableImagePathAsync(CancellationToken ct = default)
        => TryGetLatestUsableImagePathAsync(Helpers.AppPaths.ImagesDir, ct);

    // imagesDir seam for tests (the public overload pins the real Images folder).
    internal async Task<string?> TryGetLatestUsableImagePathAsync(string imagesDir, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var candidates = await db.TranscriptionRecords
            .Where(r => r.WasEnhanced
                && r.EnhancedText == TranscriptionRecord.ImageGeneratedMarker
                && r.ImageFilePath != null && r.ImageFilePath != "")
            .OrderByDescending(r => r.Timestamp)
            .Take(25)
            .Select(r => r.ImageFilePath!)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            // Reference-usable, not merely existing: an image the generation-time
            // validation would reject (oversized, foreign extension) is skipped so
            // Use-last falls through to the next-newest usable one (Codex 2026-07-11).
            if (Helpers.ReferenceImagePolicy.IsReferenceUsableAppImagePath(candidate, imagesDir, out var fullPath))
                return fullPath;
        }
        return null;
    }

    public async Task<List<TranscriptionRecord>> SearchAsync(string query, int page = 0, int pageSize = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetPageAsync(page, pageSize, ct).ConfigureAwait(false);

        using var db = _dbFactory.CreateDbContext();
        var lower = query.ToLowerInvariant();
        return await db.TranscriptionRecords
            .AsNoTracking()
            .Where(t => t.Text.ToLower().Contains(lower) ||
                        (t.EnhancedText != null && t.EnhancedText.ToLower().Contains(lower)))
            .OrderByDescending(t => t.Timestamp)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> SearchCountAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetCountAsync(ct).ConfigureAwait(false);

        using var db = _dbFactory.CreateDbContext();
        var lower = query.ToLowerInvariant();
        return await db.TranscriptionRecords
            .CountAsync(t => t.Text.ToLower().Contains(lower) ||
                        (t.EnhancedText != null && t.EnhancedText.ToLower().Contains(lower)), ct).ConfigureAwait(false);
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.TranscriptionRecords.CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> GetImageCountAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.TranscriptionRecords.CountAsync(
            r => r.WasEnhanced && (r.EnhancedText == TranscriptionRecord.ImageGeneratedMarker || r.EnhancedText == TranscriptionRecord.ImageFailedMarker), ct).ConfigureAwait(false);
    }

    public async Task<int> GetTextCountAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.TranscriptionRecords.CountAsync(
            r => !r.WasEnhanced || (r.EnhancedText != TranscriptionRecord.ImageGeneratedMarker && r.EnhancedText != TranscriptionRecord.ImageFailedMarker), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ENH-6f: fan a set of rows' ENCODED reference values out to their individual
    /// copy paths for ONE batch delete call (each row may encode several paths).
    /// </summary>
    private static IReadOnlyList<string> DecodeAll(IEnumerable<string?> encodedValues)
        => encodedValues.SelectMany(Helpers.ReferencePathList.Decode).ToList();

    // HIS-4 (Codex diff r1): ONE gate serializes every destructive history mutation's
    // snapshot→commit section (single-row delete, the three user bulk deletes, and the
    // hourly retention pass). Without it, retention can delete a row BETWEEN a bulk
    // delete's materialization and its SaveChanges — EF reports a tracked delete that
    // affected zero rows as DbUpdateConcurrencyException, and the user's confirmed
    // "Delete history" fails after the fact, skipping its media cleanup entirely.
    // SaveAsync (inserts) deliberately stays OUTSIDE the gate: rows written during a
    // delete keep the documented survive-the-snapshot behaviour. The gate releases
    // BEFORE the bulk-delete notification (an awaited UI hop) and media cleanup, so
    // retention is never blocked behind UI work.
    private readonly SemaphoreSlim _destructiveGate = new(1, 1);

    /// <summary>
    /// Commit a tracked-delete SaveChanges tolerating rows a CONCURRENT deleter already
    /// removed. EF surfaces "my DELETE affected zero rows" as
    /// <see cref="DbUpdateConcurrencyException"/>; for a deletion that is success, not
    /// conflict — the row is gone either way. Detach the already-gone entries and retry
    /// (terminates: every reported entry is detached, the tracked set is finite). The
    /// destructive gate makes this unreachable from in-process callers — it guards
    /// against out-of-process writers, and is what the deterministic seam test uses.
    /// </summary>
    private static async Task SaveDeletesToleratingConcurrentDeletesAsync(
        VoiceWinkDbContext db, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.Information(
                    "History delete raced a concurrent deleter on {Count} row(s) — already gone, continuing",
                    ex.Entries.Count);
                foreach (var entry in ex.Entries)
                    entry.State = EntityState.Detached;
            }
        }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        string? referencePath;
        string? imagePath;
        await _destructiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var item = await db.TranscriptionRecords.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
            if (item == null)
                return;

            // Capture the media paths, but delete the files only AFTER the row commit
            // succeeds — a failed SaveChangesAsync must never leave a surviving row
            // pointing at an already-recycled file (the DeleteAllAsync invariant, F10).
            imagePath = item.ImageFilePath;
            referencePath = item.ReferenceImagePath;

            db.TranscriptionRecords.Remove(item);
            await SaveDeletesToleratingConcurrentDeletesAsync(db, ct).ConfigureAwait(false);
            Logger.Debug("Deleted transcription #{Id}", id);
        }
        finally
        {
            _destructiveGate.Release();
        }

        // Files delete AFTER the row commit. Reference copies additionally go through the
        // single-owner check so a copy still referenced by another row survives (ENH-6b);
        // the row's encoded value fans out to its paths (ENH-6f), one batch call.
        if (!string.IsNullOrEmpty(imagePath))
            _recycleImageFile(imagePath);
        // Post-commit reference cleanup is UNTOKENED (diff r5): the row is gone, and a
        // cancellation here is swallowed by ReferencePersistence's tri-state query guard
        // as a failed query — RETAINING every candidate — so honoring the caller's token
        // after commit could return "success" with the copies still on disk.
        await _referencePersistence.TryDeleteIfUnreferencedAsync(
            Helpers.ReferencePathList.Decode(referencePath), CancellationToken.None).ConfigureAwait(false);
        RaiseHistoryChanged();
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        List<Models.Entities.TranscriptionRecord> items;
        await _destructiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            // ONE materialized snapshot, then delete exactly those rows. The old shape ran
            // three path queries and then an unrestricted "DELETE FROM TranscriptionRecords",
            // so a row committing in between — ordinary since IMG-4b made batch versions
            // commit incrementally — was deleted with its image/reference files never
            // cleaned up, orphaning them (Codex plan r2). Deleting the snapshot instead
            // makes the documented behaviour true: rows created DURING the delete survive,
            // with their media intact.
            items = await db.TranscriptionRecords.ToListAsync(ct).ConfigureAwait(false);
            _afterDeleteAllSnapshotForTests?.Invoke();

            db.TranscriptionRecords.RemoveRange(items);
            await SaveDeletesToleratingConcurrentDeletesAsync(db, ct).ConfigureAwait(false);
            // ── COMMITTED. Everything below is fail-soft settlement (diff r4): the rows
            // are gone, so nothing past this line may abort the method — a throw would
            // skip retirement + media cleanup with the row-derived paths unrecoverable.
            // Force WAL checkpoint so the delete is flushed to the main .db file immediately.
            // Without this, a force-kill (e.g., taskkill during deploy) can lose the delete.
            try
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("Post-delete WAL checkpoint failed ({ErrorType}) — the delete is committed, continuing",
                    ex.GetType().Name);
            }
        }
        finally
        {
            _destructiveGate.Release();
        }

        // Media paths come from the snapshot, so they can never describe a row the
        // delete did not remove; the files themselves are deleted only AFTER the DB
        // commit + checkpoint succeed, so a failed delete never strands a live row
        // pointing at removed media.
        var imagePaths = items.Select(i => i.ImageFilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var audioPaths = items.Select(i => i.AudioFilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var referencePaths = items.Select(i => i.ReferenceImagePath).Where(p => !string.IsNullOrEmpty(p)).ToList();

        // Committed — retire any UI state pinning reference copies and fold the paths it
        // was holding into this same cleanup, so they are gone before we return.
        var retired = await RaiseBulkDeleteCommittedAsync(BulkDeleteScope.All).ConfigureAwait(false);

        // DB rows are gone — now best-effort delete the media files (both guarded to their dirs).
        foreach (var path in imagePaths)
            _recycleImageFile(path!);
        foreach (var path in audioPaths)
            DeleteRecordingFile(path!);
        await _referencePersistence.TryDeleteIfUnreferencedAsync(
            Combine(DecodeAll(referencePaths!), retired), CancellationToken.None).ConfigureAwait(false); // untokened post-commit — see DeleteAsync

        Logger.Information("Deleted all transcriptions ({Count} rows)", items.Count);
        RaiseHistoryChanged();
    }

    /// <summary>
    /// Delete all image generation records (EnhancedText is TranscriptionRecord.ImageGeneratedMarker or TranscriptionRecord.ImageFailedMarker).
    /// Returns the number of records deleted.
    /// </summary>
    public async Task<int> DeleteAllImagesAsync(CancellationToken ct = default)
    {
        List<TranscriptionRecord> items;
        await _destructiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            items = await db.TranscriptionRecords
                .Where(IsImageRow)
                .ToListAsync(ct).ConfigureAwait(false);
            if (items.Count > 0)
            {
                db.TranscriptionRecords.RemoveRange(items);
                await SaveDeletesToleratingConcurrentDeletesAsync(db, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _destructiveGate.Release();
        }

        if (items.Count > 0)
        {
            // Media paths from the snapshot; files recycle only AFTER the row commit
            // succeeded (F10 — matches DeleteAllAsync's documented ordering).
            var imagePaths = items
                .Select(i => i.ImageFilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            var referencePaths = items
                .Select(i => i.ReferenceImagePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            var retiredRows = await RaiseBulkDeleteCommittedAsync(BulkDeleteScope.Images).ConfigureAwait(false);
            foreach (var path in imagePaths)
                _recycleImageFile(path!);
            await _referencePersistence.TryDeleteIfUnreferencedAsync(
                Combine(DecodeAll(referencePaths!), retiredRows), CancellationToken.None).ConfigureAwait(false); // untokened post-commit — see DeleteAsync
            Logger.Information("Deleted {Count} image generation records", items.Count);
            RaiseHistoryChanged();
            return items.Count;
        }

        // Zero rows still retires matching UI state: a stale claim can outlive its row
        // (its row was deleted individually earlier), and its file would otherwise sit
        // there until the next arm replacement or the startup sweep.
        var retired = await RaiseBulkDeleteCommittedAsync(BulkDeleteScope.Images).ConfigureAwait(false);
        if (retired.Count > 0)
            await _referencePersistence.TryDeleteIfUnreferencedAsync(retired, CancellationToken.None).ConfigureAwait(false); // untokened — claims already retired
        return 0;
    }

    /// <summary>
    /// Delete all text transcription records (everything that is NOT an image generation).
    /// Returns the number of records deleted.
    /// </summary>
    public async Task<int> DeleteAllTextAsync(CancellationToken ct = default)
    {
        List<TranscriptionRecord> items;
        await _destructiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            items = await db.TranscriptionRecords
                .Where(IsTextRow)
                .ToListAsync(ct).ConfigureAwait(false);
            if (items.Count > 0)
            {
                db.TranscriptionRecords.RemoveRange(items);
                await SaveDeletesToleratingConcurrentDeletesAsync(db, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _destructiveGate.Release();
        }

        if (items.Count > 0)
        {
            // Text rows never carry references by construction, but a corrupt/imported
            // row might — collect defensively so no delete path can leak a copy.
            var referencePaths = items
                .Select(i => i.ReferenceImagePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            var retiredRows = await RaiseBulkDeleteCommittedAsync(BulkDeleteScope.Text).ConfigureAwait(false);
            await _referencePersistence.TryDeleteIfUnreferencedAsync(
                Combine(DecodeAll(referencePaths!), retiredRows), CancellationToken.None).ConfigureAwait(false); // untokened post-commit — see DeleteAsync
            Logger.Information("Deleted {Count} text transcription records", items.Count);
            RaiseHistoryChanged();
            return items.Count;
        }

        // Zero rows still retires matching UI state — see DeleteAllImagesAsync.
        var retired = await RaiseBulkDeleteCommittedAsync(BulkDeleteScope.Text).ConfigureAwait(false);
        if (retired.Count > 0)
            await _referencePersistence.TryDeleteIfUnreferencedAsync(retired, CancellationToken.None).ConfigureAwait(false); // untokened — claims already retired
        return 0;
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        List<TranscriptionRecord> oldRecords;
        await _destructiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            oldRecords = await db.TranscriptionRecords
                .Where(r => r.Timestamp < cutoffUtc)
                .ToListAsync(ct).ConfigureAwait(false);

            if (oldRecords.Count > 0)
            {
                db.TranscriptionRecords.RemoveRange(oldRecords);
                await SaveDeletesToleratingConcurrentDeletesAsync(db, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _destructiveGate.Release();
        }

        if (oldRecords.Count > 0)
        {
            // Media paths from the snapshot; files recycle only AFTER the row commit
            // succeeded (F10 — matches DeleteAllAsync's documented ordering).
            var imagePaths = oldRecords
                .Select(r => r.ImageFilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            var referencePaths = oldRecords
                .Select(r => r.ReferenceImagePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            foreach (var path in imagePaths)
                _recycleImageFile(path!);
            await _referencePersistence.TryDeleteIfUnreferencedAsync(
                DecodeAll(referencePaths!), CancellationToken.None).ConfigureAwait(false); // untokened post-commit — see DeleteAsync
            Logger.Information("Deleted {Count} transcriptions older than {CutoffUtc}", oldRecords.Count, cutoffUtc);
            RaiseHistoryChanged();
        }
        return oldRecords.Count;
    }

    public async Task<TranscriptionRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.TranscriptionRecords.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetAllImagePathsAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.TranscriptionRecords
            .AsNoTracking()
            .Where(r => r.ImageFilePath != null)
            .Select(r => r.ImageFilePath!)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// IMG-3 ambiguity probe: does any row reference this generated-image path? Resolves
    /// an <see cref="RowWriteDisposition.AttemptedAmbiguous"/> row write — the path is the
    /// EXACT string the same run just wrote into the record (and collision-proof unique
    /// per item), so exact SQL equality is correct; the case-insensitive canonicalization
    /// the delete routes need for FOREIGN paths doesn't apply. Tri-state on purpose:
    /// a failed QUERY must never read as "not found" — the caller would delete a PNG a
    /// committed row still references.
    /// </summary>
    internal async Task<RowProbe> TryFindIdByImagePathAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var id = await db.TranscriptionRecords
                .AsNoTracking()
                .Where(r => r.ImageFilePath == imagePath)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return id.HasValue
                ? new RowProbe(RowProbeOutcome.Found, id)
                : new RowProbe(RowProbeOutcome.NotFound, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Image-path row probe failed");
            return new RowProbe(RowProbeOutcome.QueryFailed, null);
        }
    }

    /// <summary>
    /// History-based aggregate stats with no record limit. Uses SQL aggregates to avoid loading all text into memory.
    /// </summary>
    public Task<MetricsAggregate> GetAggregateStatsAsync(CancellationToken ct = default)
    {
        var boundary = LocalDayBoundary.CreateSystem();
        return GetAggregateStatsAsync(boundary, boundary.TodayLocalDate(), ct);
    }

    /// <summary>
    /// Boundary-injected overload (test seam + the lifetime-metrics seed, which must
    /// count "Today" under ITS zone/clock). The CALLER captures today's local date once
    /// and passes it in, so a midnight crossing during the queries can't split the
    /// operation across two days (Codex diff review R1). "Today" is that local calendar
    /// day as a half-open UTC interval over the stored UTC timestamps — rows on future
    /// local DAYS are excluded; a row stamped later within today still counts
    /// (clock-skew tolerance, pinned by test).
    /// </summary>
    internal async Task<MetricsAggregate> GetAggregateStatsAsync(LocalDayBoundary boundary, DateTime todayLocalDate, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();

        var total = await db.TranscriptionRecords.CountAsync(ct).ConfigureAwait(false);

        if (total == 0)
            return new MetricsAggregate();

        var (todayStartUtc, todayEndUtc) = boundary.DayIntervalUtc(todayLocalDate);
        var today = await db.TranscriptionRecords
            .CountAsync(t => t.Timestamp >= todayStartUtc && t.Timestamp < todayEndUtc, ct).ConfigureAwait(false);

        // Use SQL aggregates to compute character and word counts without loading all text.
        // Word count: normalize whitespace (tabs, newlines → spaces, collapse multi-spaces)
        // then count single spaces + 1 per non-empty row.
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == global::System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen)
                await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            // Normalize whitespace: replace tabs/CR/LF with spaces, then collapse runs of
            // multiple spaces (3 passes of double→single handles up to 8 consecutive spaces).
            cmd.CommandText = @"
                WITH normalized AS (
                    SELECT REPLACE(REPLACE(REPLACE(
                        REPLACE(REPLACE(REPLACE(
                            TRIM(COALESCE(EnhancedText, Text)),
                        char(9), ' '), char(10), ' '), char(13), ' '),
                    '  ', ' '), '  ', ' '), '  ', ' ') AS t
                    FROM TranscriptionRecords
                )
                SELECT
                    COALESCE((SELECT SUM(LENGTH(COALESCE(EnhancedText, Text))) FROM TranscriptionRecords), 0),
                    COALESCE(SUM(CASE WHEN t != ''
                        THEN (LENGTH(t) - LENGTH(REPLACE(t, ' ', ''))) + 1
                        ELSE 0 END), 0)
                FROM normalized";

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var totalChars = reader.GetInt64(0);
                var totalWords = reader.GetInt64(1);
                return new MetricsAggregate
                {
                    Total = total,
                    Today = today,
                    TotalCharacters = totalChars,
                    TotalWords = totalWords
                };
            }
        }
        finally
        {
            // Only close the connection if we opened it; EF Core manages its own connection lifecycle
            if (!wasOpen && conn.State == global::System.Data.ConnectionState.Open)
                await conn.CloseAsync().ConfigureAwait(false);
        }

        return new MetricsAggregate { Total = total, Today = today };
    }
}

/// <summary>Aggregate metrics computed from all transcription records.</summary>
public sealed class MetricsAggregate
{
    public int Total { get; init; }
    public int Today { get; init; }
    public long TotalCharacters { get; init; }
    public long TotalWords { get; init; }
}
