using global::System.Text.Json;
using global::System.Threading;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// JSON file-backed settings with thread-safe in-memory cache.
/// Writes are debounced by <see cref="DebounceMs"/>; rapid Set calls (e.g. slider drags)
/// coalesce into a single disk write. Call <see cref="Flush"/> or <see cref="Dispose"/>
/// to force a pending save. The DI container's disposal on app shutdown flushes for us.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static ILogger Logger => Log.ForContext<SettingsService>();

    private const int DebounceMs = 250;

    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    // Serializes the file-write critical section so a debounced timer tick can't race
    // Flush/FlushOrThrow/Dispose at File.Move on the same path. Ordering is always
    // _saveIoLock -> _lock (SaveOrThrow snapshots the cache inside it); never the reverse.
    private readonly object _saveIoLock = new();
    private readonly Timer _saveTimer;
    private Dictionary<string, JsonElement> _cache = new();
    private int _disposed;
    private int _persistenceSuppressed;
    // Set when Load() found an existing settings file whose bytes it could not get safely out
    // of harm's way: an UNREADABLE primary (a transiently-locked file that may still be good),
    // or a corrupt one whose quarantine copy could not be written. Blocks destructive saves
    // while set so the original is never wiped. Cleared whenever a later Load()/Reload()
    // succeeds (the transient condition resolved) or safely quarantines the corrupt bytes —
    // persistence must never stay dead longer than the file is actually at risk, or every
    // durable write in the app (legal acceptance, license activation) is silently lost for
    // the session and the corrupt file can never self-repair.
    private int _loadFailedProtectFile;

    // Test seams: override the file read/write/copy sides to inject IO faults without
    // touching the file system. Production defaults are the real File methods.
    // InternalsVisibleTo gives the test project access; never reassigned in production.
    internal Action<string, string> _writeFile = global::System.IO.File.WriteAllText;
    internal Func<string, string> _readFile = global::System.IO.File.ReadAllText;
    internal Action<string, string> _copyFile = (src, dst) => global::System.IO.File.Copy(src, dst, overwrite: true);

    // Test seam: fires AFTER a snapshot is committed to the real path (post-File.Move), so a
    // test can await the debounced background save as an EVENT instead of polling a wall clock
    // (the 3 s poll it replaced failed under machine load). _writeFile is NOT a usable signal
    // for that — it receives the TEMP path, and the commit happens after it returns. Never
    // reassigned in production; exceptions from it are the test's own problem, so it is invoked
    // outside the try that maps a write failure onto the caller.
    // No-op default rather than null: the sibling seams above all carry real defaults, and a
    // never-assigned nullable field warns CS0649 in the production assembly (the test project's
    // assignment is invisible to it).
    internal Action _afterSaveCommitted = static () => { };

    /// <summary>The file path backing this settings instance.</summary>
    public string FilePath => _settingsFilePath;

    public SettingsService() : this(null) { }

    /// <summary>
    /// Create a SettingsService backed by a specific file path.
    /// Pass null to use the default %LOCALAPPDATA%/VoiceWink/settings.json.
    /// </summary>
    public SettingsService(string? settingsFilePath)
    {
        if (settingsFilePath != null)
        {
            var dir = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _settingsFilePath = settingsFilePath;
        }
        else
        {
            global::VoiceWink.Helpers.AppPaths.EnsureRoot();
            _settingsFilePath = global::VoiceWink.Helpers.AppPaths.SettingsFile;
        }
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var element))
            {
                try
                {
                    var result = element.Deserialize<T>();
                    return result ?? defaultValue;
                }
                catch
                {
                    return defaultValue;
                }
            }

            // Check AppDefaults
            if (AppDefaults.Defaults.TryGetValue(key, out var appDefault) && appDefault is T typed)
            {
                return typed;
            }

            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            var json = JsonSerializer.SerializeToElement(value);
            _cache[key] = json;
        }
        ScheduleSave();
    }

    /// <summary>
    /// Deletes a key from settings entirely (used for one-way legacy-key migration).
    /// Returns false without scheduling a save when the key wasn't present.
    /// </summary>
    public bool Remove(string key)
    {
        bool removed;
        lock (_lock)
        {
            removed = _cache.Remove(key);
        }
        if (removed)
            ScheduleSave();
        return removed;
    }

    /// <summary>
    /// True if the key is explicitly present in settings. A stored empty string counts;
    /// a value that would only come from <see cref="AppDefaults.Defaults"/> does not —
    /// unlike <see cref="Get{T}"/>, which falls through to defaults on absence.
    /// </summary>
    public bool Contains(string key)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(key);
        }
    }

    /// <summary>
    /// The raw JSON value-kind stored for a key (<see cref="JsonValueKind.Undefined"/> when
    /// absent). Lets a caller distinguish a genuinely string-typed value from a non-string one
    /// WITHOUT the <see cref="Get{T}"/> coercion that would collapse a non-string to a default —
    /// used by UPD-4's reseed to leave a corrupted (non-string) prompts/config blob untouched
    /// rather than treat it as an empty array (Codex diff r1).
    /// </summary>
    public JsonValueKind GetValueKind(string key)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(key, out var e) ? e.ValueKind : JsonValueKind.Undefined;
        }
    }

    public bool GetBool(string key, bool defaultValue = false) => Get(key, defaultValue);

    /// <summary>
    /// A bool preference read against its SHIPPED default on every path — absent, present, or
    /// present-and-malformed. Prefer this over <see cref="GetBool"/> for any preference that has
    /// an entry in <see cref="AppDefaults.Defaults"/>.
    ///
    /// <para><see cref="Get{T}"/> consults <c>AppDefaults</c> only when a key is ABSENT; a stored
    /// value that fails to deserialize returns the caller's literal instead. So two call sites
    /// reading the same preference with different literals genuinely disagree on a corrupt or
    /// hand-edited settings file — the UI can render a toggle ON while the request path behaves as
    /// though it were OFF. Routing both through here removes the opportunity rather than relying on
    /// every future caller passing the same literal.</para>
    /// </summary>
    public bool GetBoolDefaulted(string key) => Get(key, AppDefaults.BoolDefault(key));
    public int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);
    public double GetDouble(string key, double defaultValue = 0.0) => Get(key, defaultValue);
    public string GetString(string key, string defaultValue = "") => Get(key, defaultValue);

    /// <summary>
    /// Discard any in-memory changes and re-read settings from disk.
    /// Pending debounced writes are cancelled so the disk state wins.
    /// </summary>
    public void Reload()
    {
        try { _saveTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* already disposed */ }
        // Serialize with saves (same _saveIoLock -> _lock order as PersistUnderLock) so a
        // concurrent flush can't observe the pre-load guard state and then have Load() replace
        // the cache / raise protect mode underneath it, writing an empty cache to disk.
        lock (_saveIoLock) { Load(); }
    }

    /// <summary>
    /// Persist the in-memory cache to disk immediately, cancelling any pending debounced save.
    /// Called automatically on Dispose so DI-managed shutdown is durable.
    /// </summary>
    public void Flush()
    {
        try { _saveTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* already disposed */ }
        Save();
    }

    /// <summary>
    /// Persist the in-memory cache to disk synchronously and rethrow any IO exception
    /// so callers (e.g. <see cref="VoiceWink.Services.Legal.LegalAcceptanceService.Accept"/>)
    /// can detect persistence failure. Behaves like <see cref="Flush"/> except it does
    /// NOT swallow exceptions. Existing debounced + Dispose paths continue using
    /// <see cref="Save"/> so their fail-quiet behaviour is unchanged.
    /// </summary>
    public void FlushOrThrow()
    {
        try { _saveTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* already disposed */ }
        lock (_saveIoLock)
        {
            // Checked INSIDE the lock (atomically with the write) so a Reload flipping protect
            // mid-flush can't be raced past. The intentional data-erasure quiesce stays a no-op;
            // a protect-mode load failure is a REAL persistence failure the durability callers
            // (legal acceptance, UPD-3c visible-restart flag) must see — a silent skip would let
            // FlushOrThrow falsely report a durable write.
            if (Volatile.Read(ref _persistenceSuppressed) != 0) return;
            if (Volatile.Read(ref _loadFailedProtectFile) != 0)
                throw new global::System.IO.IOException(
                    "Settings persistence is disabled: the settings file could not be loaded and was left intact to protect recoverable data. Restarting the app usually resolves this.");
            // Re-arm the debounce on write failure so the pending cache genuinely IS retried by a
            // later tick (and on Dispose) — otherwise a caller that catches the throw and reports
            // "deferred to debounce" would be wrong, since FlushOrThrow cancelled the timer above
            // (Codex diff r3). The cache is unchanged on failure (WriteSnapshot is atomic).
            try
            {
                WriteSnapshot();
            }
            catch
            {
                ScheduleSave();
                throw;
            }
        }
    }

    private void ScheduleSave()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _saveTimer.Change(DebounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* lost race with Dispose */ }
    }

    /// <summary>
    /// True once persistence has been quiesced (data-erasure) via <see cref="SuppressPersistence"/>.
    /// A durability-REQUIRED caller (e.g. a settings import) must treat this as FAILURE: a flush is
    /// a silent no-op in this state, so reporting success would be a lie (Codex diff r4).
    /// </summary>
    public bool IsPersistenceSuppressed => Volatile.Read(ref _persistenceSuppressed) != 0;

    /// <summary>
    /// Permanently quiesce persistence for this instance: cancel any pending debounced save,
    /// wait for an in-flight timer-triggered save to finish, then make every future
    /// <see cref="Save"/> / <see cref="Flush"/> / <see cref="FlushOrThrow"/> / Dispose-save a
    /// no-op. Called by <c>DataErasureService.DeleteAllAsync</c> BEFORE it deletes
    /// <c>settings.json</c>, so the normal DI-disposal-on-exit (which calls
    /// <see cref="Dispose"/> → <see cref="Save"/>) — or a pending debounced save — cannot
    /// resurrect the file with encrypted API keys / license state after a "delete all my data"
    /// wipe. Idempotent.
    /// </summary>
    public void SuppressPersistence()
    {
        // Only the FIRST caller owns timer disposal (Dispose(waitHandle) can run once); but EVERY
        // caller must still drain _saveIoLock below before returning, because a caller is typically
        // about to delete settings.json — a second concurrent erasure that returned early (without
        // draining) could delete while the first caller's in-flight save is still moving its temp
        // file into place, resurrecting the file.
        bool firstCaller = Interlocked.Exchange(ref _persistenceSuppressed, 1) == 0;
        if (firstCaller)
        {
            // Dispose(waitHandle) cancels future ticks AND signals once any in-flight tick's
            // Save() has completed. Future saves are then blocked by the flag check inside
            // PersistUnderLock, so disposing the timer here is safe.
            using var timerDone = new ManualResetEvent(false);
            try
            {
                _saveTimer.Dispose(timerDone);
                timerDone.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (ObjectDisposedException) { /* timer already disposed (Dispose ran first) */ }
        }
        // Every caller drains: acquiring _saveIoLock blocks until any non-timer write already inside
        // the critical section (e.g. an explicit Flush on another thread that passed its guard before
        // the flag was set) has finished. The flag is already set, so no new save can start. Acquired
        // AFTER the timer wait so we never hold the IO lock while blocking on the timer callback.
        lock (_saveIoLock) { }
        if (firstCaller)
            Logger.Information("Settings persistence suppressed for data erasure");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // If persistence was suppressed (data erasure), the timer is already disposed and saves
        // are no-ops — skip the timer wait + Save entirely so we don't resurrect deleted data.
        if (Volatile.Read(ref _persistenceSuppressed) != 0) return;
        // Wait for any in-flight timer callback before saving — otherwise a tick already
        // running on the pool can race with this Save() at File.Move on the same path.
        using var timerDone = new ManualResetEvent(false);
        _saveTimer.Dispose(timerDone);
        timerDone.WaitOne(TimeSpan.FromSeconds(2));
        Save();
    }

    public void SetBool(string key, bool value) => Set(key, value);
    public void SetInt(string key, int value) => Set(key, value);
    public void SetDouble(string key, double value) => Set(key, value);
    public void SetString(string key, string value) => Set(key, value);

    /// <summary>
    /// Atomically apply a batch of string-key changes and flush to disk in ONE operation
    /// (UPD-4, Codex plan-review r2): build a CANDIDATE snapshot (clone of the cache with
    /// <paramref name="changes"/> applied — a null value REMOVES the key), write that
    /// candidate to disk, and commit the changes to the in-memory cache ONLY after the disk
    /// write succeeds. Either disk AND cache both advance, or neither does (an IO fault throws
    /// with the cache untouched). Used by <c>DefaultPromptsReseed</c> so a set of related keys
    /// (the two blobs + the manifest) can never be observed half-written: the debounce timer is
    /// cancelled and the cache is not mutated until the disk write has committed, so a concurrent
    /// tick cannot snapshot an intermediate state. (Settings-import achieves its own consistency
    /// by invalidating the manifest BEFORE applying + a single <see cref="FlushOrThrow"/> at the
    /// end.) Honors persistence suppression / protect mode exactly like <see cref="FlushOrThrow"/>.
    /// </summary>
    public void SetManyAndFlushOrThrow(IReadOnlyDictionary<string, string?> changes)
    {
        if (changes.Count == 0) return;
        try { _saveTimer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* already disposed */ }
        lock (_saveIoLock)
        {
            // Same guards as FlushOrThrow, checked inside the IO lock.
            if (Volatile.Read(ref _persistenceSuppressed) != 0) return;
            if (Volatile.Read(ref _loadFailedProtectFile) != 0)
                throw new global::System.IO.IOException(
                    "Settings persistence is disabled: the settings file could not be loaded and was left intact to protect recoverable data. Restarting the app usually resolves this.");

            Dictionary<string, JsonElement> candidate;
            lock (_lock)
            {
                candidate = new Dictionary<string, JsonElement>(_cache);
            }
            foreach (var (key, value) in changes)
            {
                if (value == null) candidate.Remove(key);
                else candidate[key] = JsonSerializer.SerializeToElement(value);
            }

            // Disk FIRST — throws on failure, leaving the cache untouched. On failure re-arm the
            // debounce timer so EARLIER pending changes (e.g. the startup migrations that ran
            // before this batch) aren't stranded memory-only until an unrelated Set/Dispose
            // (Codex diff r1).
            try
            {
                WriteSnapshotFrom(candidate);
            }
            catch
            {
                ScheduleSave();
                throw;
            }

            // Commit on success. Apply to the LIVE cache (not a reference swap) so any
            // concurrent Set that landed between the clone and here is preserved.
            lock (_lock)
            {
                foreach (var (key, value) in changes)
                {
                    if (value == null) _cache.Remove(key);
                    else _cache[key] = JsonSerializer.SerializeToElement(value);
                }
            }
        }
    }

    private enum PrimaryState { Loaded, NotFound, Unreadable, Corrupt }

    private void Load()
    {
        lock (_lock)
        {
            switch (TryLoadPrimary(out var loaded))
            {
                case PrimaryState.Loaded:
                    // A successful load clears any earlier protect-mode latch: the file is
                    // demonstrably readable again (the transient lock resolved), so keeping
                    // saves dead would only turn a healed condition into session-long silent
                    // data loss for every Set/Flush that follows.
                    Volatile.Write(ref _loadFailedProtectFile, 0);
                    _cache = loaded;
                    Logger.Information("Settings loaded from {Path}", _settingsFilePath);
                    return;

                case PrimaryState.NotFound:
                    Volatile.Write(ref _loadFailedProtectFile, 0);
                    _cache = new Dictionary<string, JsonElement>();
                    Logger.Information("No settings file found, using defaults");
                    return;

                case PrimaryState.Corrupt:
                    // The file was READ successfully but didn't parse (truncated/torn write).
                    // Preserve it in the fixed-name quarantine file and keep persistence
                    // ENABLED — the next save rewrites the primary and the app stays
                    // functional (a protected-but-corrupt primary can never self-repair:
                    // saves are what fix it, and blocking them bricked legal acceptance /
                    // onboarding for good). The fixed name keeps disk use bounded and is
                    // covered by the DataErasureService allowlist — the GDPR-erasure gap that
                    // sank the original timestamped settings.json.corrupt-<ts> quarantine.
                    // Only if the quarantine copy itself cannot be written do we fall back to
                    // protect mode, so the sole copy of the bytes is never overwritten.
                    if (TryQuarantineCorruptFile())
                        Volatile.Write(ref _loadFailedProtectFile, 0);
                    else
                        Volatile.Write(ref _loadFailedProtectFile, 1);
                    RecoverFromBackupOrDefaults();
                    return;

                default: // PrimaryState.Unreadable — the file EXISTS but could not be read
                         // (AV/EDR/sharing lock, access error). The on-disk bytes may be a
                         // perfectly good file, so it MUST NOT be overwritten: enter protect
                         // mode so no save can wipe it; the next launch (or a later successful
                         // Reload) re-reads it. Mirrors the SQLite
                         // TryBackupDatabase-before-recreate stance in CLAUDE.md "Database init safety".
                    Volatile.Write(ref _loadFailedProtectFile, 1);
                    RecoverFromBackupOrDefaults();
                    return;
            }
        }
    }

    // Recover this session's runtime cache from the last-good backup if possible (the .bak
    // used to be write-only / never read back — that gap turned a transient fault into silent
    // data loss), else start from defaults. MUST be called with _lock held.
    private void RecoverFromBackupOrDefaults()
    {
        var protectActive = Volatile.Read(ref _loadFailedProtectFile) != 0;
        if (TryParseFile(_settingsFilePath + ".bak", out var bak))
        {
            _cache = bak;
            Logger.Warning("Settings file {Path} could not be loaded; using backup for this session " +
                "(persistence {Persistence})", _settingsFilePath, protectActive ? "disabled to protect the original" : "enabled");
        }
        else
        {
            _cache = new Dictionary<string, JsonElement>();
            Logger.Error("Settings file {Path} could not be loaded and no valid backup existed " +
                "(persistence {Persistence})", _settingsFilePath, protectActive ? "disabled to protect the original" : "enabled");
        }
    }

    // Preserve a corrupt primary in the fixed-name side file so a save can safely rewrite the
    // primary without destroying potentially salvageable content. A file COPY, not a rewrite
    // of the decoded text — string round-tripping would transform invalid/truncated UTF-8,
    // BOMs, or UTF-16 content, and the quarantine's whole point is exact bytes. Overwrites any
    // previous quarantine (bounded disk use; the newest corruption is the one worth keeping).
    private bool TryQuarantineCorruptFile()
    {
        var quarantinePath = _settingsFilePath + ".corrupt";
        try
        {
            _copyFile(_settingsFilePath, quarantinePath);
            Logger.Warning("Corrupt settings file preserved at {Path}; persistence stays enabled so the " +
                "primary can self-repair", quarantinePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not quarantine the corrupt settings file to {Path}; entering protect mode",
                quarantinePath);
            return false;
        }
    }

    // Classify the primary settings file: Loaded, NotFound, Unreadable, or Corrupt. File.Exists
    // is deliberately NOT used: it also returns false for some access errors, which would
    // mis-classify a present-but-inaccessible file as NotFound and let an empty cache overwrite
    // it. Read first; only a genuine FileNotFound/DirectoryNotFound is NotFound — every other
    // read fault is Unreadable (protected: the bytes may be a good file we just can't see right
    // now). A parse fault is Corrupt, which Load() quarantines by file copy; an unexpected
    // parse-time exception (resource failure, etc.) takes the same path, and if its quarantine
    // copy also fails, Load() falls back to protect mode — still fail-safe.
    private PrimaryState TryLoadPrimary(out Dictionary<string, JsonElement> result)
    {
        result = new Dictionary<string, JsonElement>();
        string json;
        try
        {
            json = _readFile(_settingsFilePath);
        }
        catch (Exception ex) when (ex is global::System.IO.FileNotFoundException
                                      or global::System.IO.DirectoryNotFoundException)
        {
            return PrimaryState.NotFound;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not read settings file {Path}", _settingsFilePath);
            return PrimaryState.Unreadable;
        }

        try
        {
            result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                     ?? new Dictionary<string, JsonElement>();
            return PrimaryState.Loaded;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not parse settings file {Path}", _settingsFilePath);
            return PrimaryState.Corrupt;
        }
    }

    private bool TryParseFile(string path, out Dictionary<string, JsonElement> result)
    {
        result = new Dictionary<string, JsonElement>();
        try
        {
            var json = _readFile(path);
            result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                     ?? new Dictionary<string, JsonElement>();
            return true;
        }
        catch (Exception ex) when (ex is global::System.IO.FileNotFoundException
                                      or global::System.IO.DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not read/parse backup settings file {Path}", path);
            return false;
        }
    }

    private void Save()
    {
        try { PersistUnderLock(); }
        catch (Exception ex) { Logger.Error(ex, "Failed to save settings"); }
    }

    // The single write critical section. Serializes all saves (timer, Flush, Dispose) AND
    // re-checks the suppress/protect guards INSIDE the lock so a Reload/Suppress that flips them
    // mid-flight can't be raced past. Ordering is always _saveIoLock -> _lock.
    private void PersistUnderLock()
    {
        lock (_saveIoLock)
        {
            // Persistence quiesced for data erasure, or a protect-mode load failure — never write
            // (would resurrect deleted data / overwrite a recoverable-but-unreadable file).
            if (Volatile.Read(ref _persistenceSuppressed) != 0) return;
            if (Volatile.Read(ref _loadFailedProtectFile) != 0) return;
            WriteSnapshot();
        }
    }

    // Serializes the actual file write. MUST be called with _saveIoLock held.
    private void WriteSnapshot()
    {
        Dictionary<string, JsonElement> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, JsonElement>(_cache);
        }
        WriteSnapshotFrom(snapshot);
    }

    // Write a specific snapshot dictionary to disk (tmp-write + atomic move + .bak refresh).
    // Split from WriteSnapshot so SetManyAndFlushOrThrow can write a CANDIDATE snapshot to
    // disk BEFORE committing it to the cache (disk-first, commit-on-success).
    private void WriteSnapshotFrom(Dictionary<string, JsonElement> snapshot)
    {
        var tmpPath = $"{_settingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            _writeFile(tmpPath, json);
            File.Move(tmpPath, _settingsFilePath, overwrite: true);

            // Refresh the backup FROM the just-written known-good file (after the move, not
            // before). The old copy-before-move copied whatever was on disk — which could be
            // the very corrupt/empty file we were about to replace — poisoning the backup so
            // Load()'s recovery would restore garbage. Best-effort: a failed backup must not
            // fail the save.
            try { _copyFile(_settingsFilePath, _settingsFilePath + ".bak"); } catch { }
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }

        // Commit signal (test seam, a no-op in production) — outside the try so it can never be
        // mistaken for a save failure, and only on the success path: the snapshot is on disk
        // at the real path by now.
        _afterSaveCommitted();
    }
}
