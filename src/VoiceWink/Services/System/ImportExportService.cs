using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.Data;

namespace VoiceWink.Services.System;

/// <summary>Which parts of a backup file an export writes or an import applies.</summary>
[Flags]
public enum BackupParts
{
    None = 0,
    Settings = 1,
    Dictionary = 2,
    All = Settings | Dictionary,
}

/// <summary>
/// Export/import a backup file: the settings as a flat JSON object of portable preferences, plus
/// (optionally) the Dictionary under the reserved <see cref="DictionaryKey"/>.
///
/// <para><b>Why flat with reserved keys, not an envelope:</b> a backup written before the
/// Dictionary travelled has no reserved key and imports as settings exactly as before; and an
/// older VoiceWink reading a new file skips the unknown reserved keys and still applies the
/// settings — an envelope would make it apply none (older builds refuse a file over 1 MB, so
/// this holds up to that size). Both reserved names start with an
/// underscore, which no settings key does (pinned by a test).</para>
///
/// <para><b>One gate:</b> every import reads the file through <see cref="ReadBackupAsync"/> —
/// size, shape, format version, the whole-file sensitive-key refusal and the Dictionary's
/// parsing all happen there, before anything is applied, whichever part is asked for.</para>
/// </summary>
public sealed class ImportExportService
{
    private static ILogger Logger => Log.ForContext<ImportExportService>();

    /// <summary>Marks a backup file and its version. Absent = a settings file written before the
    /// Dictionary travelled.</summary>
    internal const string FormatKey = "_format";
    internal const string FormatValue = "voicewink-backup/1";
    /// <summary>The Dictionary's section: <c>{ "words": [..], "replacements": [{ "original",
    /// "replacement", "enabled" }] }</c>.</summary>
    internal const string DictionaryKey = "_dictionary";

    private readonly SettingsService _settings;
    private readonly DictionaryTransfer? _dictionary;

    /// <param name="dictionary">The Dictionary's database side. Optional only so settings-only
    /// callers (and their tests) need not build a database; DI always supplies it, and asking for
    /// the Dictionary part without it is refused before anything is written.</param>
    public ImportExportService(SettingsService settings, DictionaryTransfer? dictionary = null)
    {
        _settings = settings;
        _dictionary = dictionary;
    }

    /// <summary>The ONE size limit, for both directions: an export larger than an import accepts
    /// would be a backup nobody can read.</summary>
    internal const long MaxBackupBytes = 4 * 1_048_576;

    // Every entry refuses while persistence is suppressed (mid data-erasure): the settings write
    // would be a silent no-op, and a Dictionary read or write would open — and so recreate — the
    // database the erasure just deleted.
    private void EnsurePersistenceAvailable(string verb)
    {
        if (_settings.IsPersistenceSuppressed)
            throw new InvalidOperationException(
                $"The backup can't be {verb} right now — persistence is disabled (e.g. a data wipe is in progress). Restart the app and try again.");
    }

    private DictionaryTransfer RequireDictionary()
        => _dictionary ?? throw new InvalidOperationException("The Dictionary isn't available for backup in this session.");

    /// <summary>
    /// Keys that must never be included in exported/imported settings.
    /// API keys and license keys are DPAPI-encrypted blobs; base URLs could redirect credentials to a malicious server.
    /// License metadata also stays local because exported settings are intended for preferences, not seat transfer.
    /// The reference-dedup HMAC key (ENH-6g) is device-local secret state: exporting it would let a
    /// history.json recipient reverse the keyed content-addressing (known-image membership test),
    /// and importing a foreign one would silently fork this install's dedup identity.
    /// </summary>
    internal static bool IsSensitiveKey(string key) =>
        key.StartsWith(AppDefaults.ApiKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(AppDefaults.AiBaseUrlPrefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("license", StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.ReferenceDedupKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Device-local state/consent keys a settings IMPORT must never write (G2). Distinct from
    /// <see cref="IsSensitiveKey"/> (which also drives the GDPR export's redaction and rejects
    /// the whole file): these aren't secrets — they just belong to THIS device/user's own
    /// actions. A foreign import file flipping hasCompletedOnboarding could re-trigger or skip
    /// onboarding, and crashReportingOptIn is Art. 6(1)(a) consent — only the consent UI may
    /// set it. Skipped per-key (like unknown keys), not whole-file-rejected.
    /// </summary>
    private static bool IsImportProtectedKey(string key) =>
        key.Equals(AppDefaults.HasCompletedOnboarding, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.CrashReportingOptIn, StringComparison.OrdinalIgnoreCase) ||
        // Legal acceptance is consent state — only the in-app acceptance flow may set it; an import
        // must NEVER mark the EULA / privacy policy accepted on this device (Codex diff r3).
        key.Equals(AppDefaults.AcceptedEulaVersion, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.AcceptedPrivacyVersion, StringComparison.OrdinalIgnoreCase) ||
        // App-managed / device-local runtime state — never a portable preference.
        key.Equals(AppDefaults.LifetimeMetricsState, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.ShowWindowAfterUpdateRestart, StringComparison.OrdinalIgnoreCase) ||
        // TRN-53: GPU acceleration is a property of the MACHINE, not the profile — an export
        // from a laptop where it was turned off must not silently disable the GPU on a capable
        // desktop (the recordingDeviceId reasoning exactly; self-review, regression lens).
        key.Equals(AppDefaults.GpuAccelerationEnabled, StringComparison.OrdinalIgnoreCase) ||
        // NOTE (owner decision 2026-07-22): the "Enable Dictionary" toggles
        // (ElevenLabsKeytermsEnabled / DeepgramKeytermsEnabled) were previously import-protected
        // as device-local consent, but that made import surprising — an import should reproduce
        // the exported profile exactly (that is the point of export/import). They are ordinary
        // preferences now: exported AND imported like any other setting. Reset still restores
        // them to their ON defaults.
        // UPD-4: the defaults manifest is app-managed per-install state, never importable. An
        // import that carries prompts/App-Mode instead INVALIDATES the affected manifest domain
        // (see ImportAsync) so the next startup reconciles the imported data.
        key.Equals(AppDefaults.DefaultsManifest, StringComparison.OrdinalIgnoreCase) ||
        // REL-17: the diagnostics opt-ins are device-local consent (raw dictation tracing +
        // voice-recording retention) — a foreign profile must not switch them on here, and they
        // don't export (same shared predicate). The legacy Debug-era trace key is skipped too so
        // an old export can't resurrect it ahead of the launch migration. REL-21's image-prompt
        // sub-option is the same class of consent — a foreign profile must not widen what this
        // device traces.
        key.Equals(AppDefaults.PromptTraceLoggingOptIn, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.PromptTraceIncludeImagePrompts, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.LegacyPromptTraceLoggingEnabled, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(AppDefaults.KeepRecordingsForDebug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the imported JSON value's kind is compatible with the expected kind.
    /// Int vs Double are treated as compatible (both Number); True and False are interchangeable.
    /// </summary>
    private static bool IsKindCompatible(JsonValueKind expected, JsonValueKind actual)
    {
        if (expected == actual) return true;
        if ((expected == JsonValueKind.True || expected == JsonValueKind.False) &&
            (actual == JsonValueKind.True || actual == JsonValueKind.False))
            return true;
        return false;
    }

    private static bool TryGetExpectedKind(string key, out JsonValueKind expectedKind)
    {
        if (AppDefaults.Defaults.TryGetValue(key, out var defaultValue))
        {
            expectedKind = JsonSerializer.SerializeToElement(defaultValue).ValueKind;
            return true;
        }

        if (key.StartsWith(AppDefaults.AiModelPrefix, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(AppDefaults.AiImageModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            expectedKind = JsonValueKind.String;
            return true;
        }

        expectedKind = JsonValueKind.Undefined;
        return false;
    }

    /// <summary>
    /// Reject imported values that are type-valid but semantically DANGEROUS. The critical case is
    /// the transcription retention: cleanup computes cutoff = now − retentionMinutes, so a
    /// non-positive value yields a FUTURE cutoff that deletes ALL history + recordings (Codex diff
    /// r3). Other numerics are non-destructive, so only the destructive case is guarded here.
    /// </summary>
    private static bool IsSemanticallyValidForImport(string key, JsonElement value)
    {
        if (key.Equals(AppDefaults.TranscriptionRetentionMinutes, StringComparison.OrdinalIgnoreCase))
            return value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var minutes) && minutes > 0;
        return true;
    }

    /// <summary>
    /// The shared portable-preference predicate. A key is importable iff it is NOT sensitive, NOT
    /// import-protected (onboarding / consent / legal / app-managed / manifest), and is a KNOWN
    /// preference (in <see cref="AppDefaults.Defaults"/> or a model-selection prefix). Export AND
    /// import use this SAME predicate, so the exported file is symmetric with what an import will
    /// accept — no app-managed key exports only to be rejected on import (Codex diff r4).
    /// </summary>
    private static bool IsImportableKey(string key) =>
        !IsSensitiveKey(key) && !IsImportProtectedKey(key) && TryGetExpectedKind(key, out _);

    /// <summary>Settings-only export (the pre-Dictionary behaviour).</summary>
    public Task ExportAsync(string filePath) => ExportAsync(filePath, BackupParts.Settings);

    public async Task ExportAsync(string filePath, BackupParts parts)
    {
        EnsurePersistenceAvailable("exported");
        if (parts == BackupParts.None)
            throw new ArgumentException("Nothing to export.", nameof(parts));
        var dictionary = parts.HasFlag(BackupParts.Dictionary) ? RequireDictionary() : null;

        var filtered = new Dictionary<string, JsonElement>();
        var strippedCount = 0;
        if (parts.HasFlag(BackupParts.Settings))
            strippedCount = await CollectSettingsAsync(filtered).ConfigureAwait(false);
        filtered[FormatKey] = JsonSerializer.SerializeToElement(FormatValue);
        if (dictionary != null)
            filtered[DictionaryKey] = SerializeDictionary(await dictionary.ReadAsync().ConfigureAwait(false));

        var options = new JsonSerializerOptions { WriteIndented = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(filtered, options);
        if (bytes.LongLength > MaxBackupBytes)
            throw new InvalidOperationException(
                $"The backup would be {bytes.LongLength:N0} bytes, more than the {MaxBackupBytes:N0} bytes an import accepts. Nothing was written.");

        // Write to a SAME-DIRECTORY temp file, then atomically replace the target — so a write
        // failure mid-way can't truncate the user's EXISTING backup at filePath (Codex diff r5).
        var tmpPath = filePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, bytes).ConfigureAwait(false);
            File.Move(tmpPath, filePath, overwrite: true); // same-volume rename = atomic replace
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }

        // SEC-4: user-chosen destination. Token last so the rendered scrub reaches it.
        Logger.Information("Backup exported (parts {Parts}, {Stripped} sensitive keys excluded): filePath={UserFilePath}",
            parts, strippedCount, Helpers.LogPathProjection.FileNameOnly(filePath));
    }

    private static JsonElement SerializeDictionary(DictionarySnapshot snapshot)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["words"] = snapshot.Words,
            ["replacements"] = snapshot.Rules.Select(r => new Dictionary<string, object>
            {
                ["original"] = r.OriginalText,
                ["replacement"] = r.ReplacementText,
                ["enabled"] = r.IsEnabled,
            }).ToList(),
        });

    /// <summary>Fills <paramref name="filtered"/> with every importable preference's effective
    /// value; returns how many persisted keys were stripped.</summary>
    private async Task<int> CollectSettingsAsync(Dictionary<string, JsonElement> filtered)
    {
        // Legacy-key migration must run BEFORE Flush so its removal is materialized into the
        // snapshot being exported (a user can export before anything else touched App Mode).
        AppModeSettingsMigration.Run(_settings);

        // Materialize pending debounced writes so the on-disk snapshot is current. FlushOrThrow
        // (not Flush) so a persistence failure surfaces to the caller instead of silently
        // exporting stale state (Codex diff r3).
        _settings.FlushOrThrow();

        // FlushOrThrow is a SILENT no-op under persistence suppression (mid data-erasure), so a read
        // below could see a half-deleted/missing file and produce a bogus "successful" default-only
        // export. Treat suppression as failure (Codex diff r5).
        EnsurePersistenceAvailable("exported");

        var settingsPath = _settings.FilePath;
        var strippedCount = 0;

        // Read the persisted snapshot. A GENUINE absence (fresh profile, nothing persisted yet) is
        // fine — the synthesized defaults below still export. Any OTHER read failure (access
        // denied, sharing lock) must PROPAGATE, not silently produce a near-empty "successful"
        // export (Codex diff r3 — File.Exists can't distinguish absence from an access failure).
        string? json = null;
        try { json = await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false); }
        catch (FileNotFoundException) { /* genuine fresh profile */ }
        catch (DirectoryNotFoundException) { /* genuine fresh profile */ }

        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);

            // Export only IMPORTABLE preferences: strip sensitive keys (DPAPI key blobs, license,
            // custom base URLs) AND every import-protected key (onboarding, consent, legal
            // acceptance, app-managed/transient state, the defaults manifest) — export and import
            // exclude the SAME set, so the file is symmetric with what an import will accept.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (IsImportableKey(prop.Name))
                    filtered[prop.Name] = prop.Value.Clone();
                else
                    strippedCount++;
            }
        }

        // Synthesize the EFFECTIVE value of every importable default-backed preference the user
        // never explicitly changed (unpersisted → absent above). Without this a profile left at
        // defaults wouldn't round-trip: importing it over a MODIFIED target would keep the target's
        // values instead of restoring the source's effective defaults (Codex diff r4). An
        // unpersisted key's effective value IS its registered default.
        foreach (var (key, defaultValue) in AppDefaults.Defaults)
        {
            if (IsImportableKey(key) && !filtered.ContainsKey(key))
                filtered[key] = JsonSerializer.SerializeToElement(defaultValue);
        }

        return strippedCount;
    }

    /// <summary>
    /// A backup file read and validated by <see cref="ReadBackupAsync"/>, holding nothing of the
    /// file but the validated settings and the parsed Dictionary — applying it never reads the
    /// file again.
    /// </summary>
    public sealed class BackupFile
    {
        internal BackupFile(IReadOnlyDictionary<string, JsonElement> settingsToApply, DictionarySnapshot? dictionary, int dictionarySkipped, string sourceName)
        {
            SettingsToApply = settingsToApply;
            Dictionary = dictionary;
            DictionarySkipped = dictionarySkipped;
            SourceName = sourceName;
        }

        internal IReadOnlyDictionary<string, JsonElement> SettingsToApply { get; }
        internal DictionarySnapshot? Dictionary { get; }
        /// <summary>Dictionary entries that were not words or rules (wrong type, a blank side).</summary>
        internal int DictionarySkipped { get; }
        /// <summary>The file NAME only (SEC-4) for log lines.</summary>
        internal string SourceName { get; }

        public bool HasSettings => SettingsToApply.Count > 0;
        public bool HasDictionary => Dictionary != null;
    }

    /// <summary>Outcome of an import. <paramref name="DictionaryFailed"/>: the settings were applied
    /// and durable, then the Dictionary could not be written (nothing of it was added).</summary>
    public sealed record ImportResult(
        int AppliedCount,
        DictionaryMergeResult? Dictionary = null,
        int DictionarySkipped = 0,
        bool DictionaryFailed = false);

    /// <summary>Settings-only import (the pre-Dictionary behaviour): a file's Dictionary is
    /// ignored, and a file with no applicable setting applies nothing rather than failing.</summary>
    public async Task<ImportResult> ImportAsync(string filePath)
    {
        var backup = await ReadBackupAsync(filePath).ConfigureAwait(false);
        return new ImportResult(ApplySettings(backup));
    }

    /// <summary>Reads, validates and applies <paramref name="parts"/> of a file in one go — the
    /// Dictionary page's path; the gate is the same one the Settings page previews with.</summary>
    public async Task<ImportResult> ImportAsync(string filePath, BackupParts parts)
        => await ImportAsync(await ReadBackupAsync(filePath).ConfigureAwait(false), parts).ConfigureAwait(false);

    /// <summary>
    /// Applies the requested parts of a validated backup: settings first (durable or reverted,
    /// as before), then the Dictionary in one database write. A part the file does not carry is
    /// refused before anything changes. When both were asked for and the Dictionary write fails
    /// after the settings landed, the result says so instead of throwing — the settings ARE
    /// applied, and the user must be told which half happened.
    /// </summary>
    public async Task<ImportResult> ImportAsync(BackupFile backup, BackupParts parts)
    {
        EnsurePersistenceAvailable("imported");
        if (parts == BackupParts.None)
            throw new ArgumentException("Nothing to import.", nameof(parts));
        var wantSettings = parts.HasFlag(BackupParts.Settings);
        var wantDictionary = parts.HasFlag(BackupParts.Dictionary);
        if (wantSettings && !backup.HasSettings)
            throw new InvalidOperationException("This file has no settings.");
        if (wantDictionary && !backup.HasDictionary)
            throw new InvalidOperationException("This file has no Dictionary.");
        var dictionary = wantDictionary ? RequireDictionary() : null;

        var applied = wantSettings ? ApplySettings(backup) : 0;
        if (dictionary == null)
            return new ImportResult(applied);

        try
        {
            var merged = await dictionary.MergeAsync(backup.Dictionary!).ConfigureAwait(false);
            return new ImportResult(applied, merged, backup.DictionarySkipped);
        }
        catch (Exception ex) when (wantSettings)
        {
            Logger.Error(ex, "Dictionary import failed after the settings were applied");
            return new ImportResult(applied, null, backup.DictionarySkipped, DictionaryFailed: true);
        }
    }

    /// <summary>
    /// THE file gate, for every import: size, a JSON object at the root, a known format, no
    /// sensitive key anywhere in the file (whichever part is asked for), a well-formed Dictionary
    /// section — and the settings validated into what an import would apply. Nothing is applied.
    /// </summary>
    public async Task<BackupFile> ReadBackupAsync(string filePath)
    {
        EnsurePersistenceAvailable("imported");

        // Validate file size before reading
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxBackupBytes)
        {
            throw new InvalidOperationException(
                $"Import file is too large ({fileInfo.Length:N0} bytes). Maximum allowed size is {MaxBackupBytes:N0} bytes.");
        }

        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("This file isn't a VoiceWink backup.");

        // Reject any import that contains sensitive state (prevent key/license injection)
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // SEC-4 scope note: the {Key} sites BELOW this point (legacy-supersede,
            // device-local-skip, out-of-range) are all gated on the name matching an app CONSTANT,
            // so $prop.Name there is a known key, not user data — deliberately left generic. The
            // three that see an ARBITRARY name from the imported file (sensitive-prefix reject,
            // unknown-key skip, type mismatch) use {ImportedSettingKey}, which is allowlisted.
            if (IsSensitiveKey(prop.Name))
            {
                Logger.Warning("Import rejected: file contains a sensitive key: userTerm={ImportedSettingKey}", Helpers.LogValueSanitizer.SingleLine(prop.Name));
                throw new InvalidOperationException("Imported settings file contains sensitive data (API keys, license state, or base URLs), which is not allowed.");
            }
        }

        // A backup this version cannot read is refused before anything is asked or applied —
        // a later format may move what the reserved keys mean.
        if (doc.RootElement.TryGetProperty(FormatKey, out var format)
            && !(format.ValueKind == JsonValueKind.String && format.GetString() == FormatValue))
            throw new InvalidOperationException("This backup was made by a newer version of VoiceWink. Update VoiceWink and try again.");

        DictionarySnapshot? dictionary = null;
        var dictionarySkipped = 0;
        if (doc.RootElement.TryGetProperty(DictionaryKey, out var dictionarySection))
            dictionary = ParseDictionary(dictionarySection, out dictionarySkipped);

        // Old exports carry the pre-rename App Mode key ("powerModeConfigs"). The new key wins
        // regardless of JSON property order or casing, so pre-scan for it case-insensitively;
        // the legacy property is aliased to the new key only when the new key is absent.
        var hasAppModeKey = doc.RootElement.EnumerateObject()
            .Any(p => p.Name.Equals(AppDefaults.AppModeConfigs, StringComparison.OrdinalIgnoreCase));

        // Build the CANONICAL, validated set to apply in ONE pass — the SINGLE source of truth for
        // both application and defaults-manifest invalidation (Codex diff r3: two separate passes
        // diverged on mis-case and duplicate properties). Canonicalize the reseed-domain keys +
        // the legacy alias; skip protected/unknown/type-mismatch; duplicate valid properties are
        // last-valid-wins.
        var toApply = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var name = prop.Name;
            if (name is FormatKey or DictionaryKey)
                continue; // the backup's own sections, not settings — never logged as unknown
            if (name.Equals(AppDefaults.CustomPrompts, StringComparison.OrdinalIgnoreCase))
                name = AppDefaults.CustomPrompts; // canonicalize case (Codex r3)
            else if (name.Equals(AppDefaults.AppModeConfigs, StringComparison.OrdinalIgnoreCase))
                name = AppDefaults.AppModeConfigs;
            else if (name.Equals(AppDefaults.LegacyPowerModeConfigs, StringComparison.OrdinalIgnoreCase))
            {
                if (hasAppModeKey)
                {
                    Logger.Information("Import: legacy setting '{Key}' superseded by '{New}' — skipped",
                        prop.Name, AppDefaults.AppModeConfigs);
                    continue;
                }
                name = AppDefaults.AppModeConfigs;
            }

            if (IsImportProtectedKey(name))
            {
                Logger.Information("Import: device-local state key '{Key}' skipped — not importable", prop.Name);
                continue;
            }
            if (!TryGetExpectedKind(name, out var expectedKind))
            {
                // SEC-4: the key comes from a file the user supplied, so it is arbitrary attacker- or
                // user-controlled text — including line breaks. Sanitized and scrub-covered.
                Logger.Warning("Import: unknown setting skipped: userTerm={ImportedSettingKey}", Helpers.LogValueSanitizer.SingleLine(prop.Name));
                continue;
            }
            if (!IsKindCompatible(expectedKind, prop.Value.ValueKind))
            {
                Logger.Warning(
                    "Import: setting has a type mismatch (expected {Expected}, got {Got}) — skipped: userTerm={ImportedSettingKey}",
                    expectedKind, prop.Value.ValueKind, Helpers.LogValueSanitizer.SingleLine(prop.Name));
                continue;
            }
            if (!IsSemanticallyValidForImport(name, prop.Value))
            {
                Logger.Warning("Import: setting '{Key}' has an out-of-range value — skipped", prop.Name);
                continue;
            }

            toApply[name] = prop.Value.Clone(); // last-valid-wins on duplicate canonical keys
        }

        return new BackupFile(toApply, dictionary, dictionarySkipped, Helpers.LogPathProjection.FileNameOnly(filePath));
    }

    /// <summary>Applies a validated file's settings — durable, or reverted and thrown. Returns how
    /// many were applied (0 is a success: nothing in the file was applicable).</summary>
    private int ApplySettings(BackupFile backup)
    {
        var toApply = backup.SettingsToApply;

        // UPD-4: invalidate the defaults-manifest domain(s) that WILL apply — derived from the
        // SAME canonical set, so it can't diverge from what the apply below does — and do it
        // BEFORE applying (atomicity, Codex diff r2): no intermediate persisted state carries
        // [imported blob + old manifest]. The final FlushOrThrow writes the whole result in one
        // atomic snapshot.
        var willApplyPrompts = toApply.ContainsKey(AppDefaults.CustomPrompts);
        var willApplyAppModes = toApply.ContainsKey(AppDefaults.AppModeConfigs);
        if (willApplyPrompts || willApplyAppModes)
        {
            var newManifest = DefaultsManifest.WithDomainsInvalidated(
                _settings.GetString(AppDefaults.DefaultsManifest, ""), willApplyPrompts, willApplyAppModes);
            if (newManifest == null) _settings.Remove(AppDefaults.DefaultsManifest);
            else _settings.SetString(AppDefaults.DefaultsManifest, newManifest);
        }

        // KNOWN LIMITATION (backlog SET-1, owner-accepted 2026-07-22): import is mutate-then-flush
        // with Reload rollback on failure, NOT a true disk-first candidate commit. On a late flush
        // failure a brief (not strictly bounded — this method resumes off-context after
        // ConfigureAwait) window exists where a concurrent provider request could observe an
        // imported value before Reload reverts it; a debounce tick firing mid-import could persist a
        // partial import; and Reload discards any unrelated concurrent Set. Accepted for a
        // deliberate, infrequent manual import; the rigorous fix (a typed disk-first
        // SetManyAndFlushOrThrow) is backlogged.
        var importedCount = 0;
        foreach (var (name, value) in toApply)
        {
            _settings.Set(name, value);
            importedCount++;
        }

        // The import itself never writes the legacy key, but the CURRENT settings may still
        // carry one (import can run before anything else touched App Mode) — uphold the
        // "legacy key removed either way" invariant standalone rather than relying on the
        // startup migration having run first.
        AppModeSettingsMigration.Run(_settings);

        // TRN-6: same reasoning, one migration later. An imported backup predating the q8-only
        // catalogue carries model names that no longer resolve, and import applies BOTH surfaces
        // they live on (selectedModelName and appModeConfigs). Running the startup migration here
        // is not belt-and-braces: `MainViewModel` re-reads the selected model for EVERY recording,
        // so without this the very next dictation after an import fails with UnknownModel until
        // the app is restarted — and the dialog's restart hint is advice, not a guard (both diff
        // reviewers). Ordering is load-bearing: BEFORE the flush below, so the migrated names are
        // what actually lands on disk.
        LocalModelMigration.RunOnSettings(_settings);

        // Durable-or-nothing (Codex diff r3/r4). Now that import is user-facing, a failed OR
        // suppressed flush must not leave the import live in memory: provider toggles are read
        // per-request, so a non-durable "failed" import could still enable paid egress, and a
        // re-armed debounce could persist a "failed" import later. Reload() discards the in-memory
        // import AND cancels that debounce, reverting to the last durable state; the caller reports
        // the failure.
        try
        {
            _settings.FlushOrThrow();
        }
        catch
        {
            _settings.Reload();
            throw;
        }
        // FlushOrThrow is a SILENT no-op when persistence is suppressed (mid data-erasure), so it
        // won't have thrown above — check explicitly and treat suppression as failure too.
        if (_settings.IsPersistenceSuppressed)
        {
            _settings.Reload();
            throw new InvalidOperationException(
                "Settings can't be imported right now — persistence is disabled (e.g. a data wipe is in progress). Restart the app and try again.");
        }

        // SEC-4: user-chosen source (already projected to its file name).
        Logger.Information("Settings imported ({Count} keys applied): filePath={UserFilePath}",
            importedCount, backup.SourceName);
        return importedCount;
    }

    /// <summary>
    /// The Dictionary section, read at the trust boundary: the containers must have the right
    /// shape (anything else is a damaged file, refused whole); an ENTRY of the wrong type — a
    /// word that is not a string, a rule without both texts, an <c>enabled</c> that is not a
    /// boolean — is skipped and counted, never guessed at. An absent <c>enabled</c> reads as on,
    /// as every rule was before rules could be switched off.
    /// </summary>
    private static DictionarySnapshot ParseDictionary(JsonElement section, out int skipped)
    {
        const string damaged = "The Dictionary in this file is damaged.";
        if (section.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(damaged);
        skipped = 0;

        var words = new List<string>();
        if (section.TryGetProperty("words", out var wordsEl))
        {
            if (wordsEl.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(damaged);
            foreach (var w in wordsEl.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(w.GetString()))
                    words.Add(w.GetString()!);
                else
                    skipped++;
            }
        }

        var rules = new List<DictionaryRule>();
        if (section.TryGetProperty("replacements", out var rulesEl))
        {
            if (rulesEl.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(damaged);
            foreach (var r in rulesEl.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.Object
                    && r.TryGetProperty("original", out var o) && o.ValueKind == JsonValueKind.String
                    && r.TryGetProperty("replacement", out var t) && t.ValueKind == JsonValueKind.String
                    && DictionaryMerge.SplitVariants(o.GetString()!).Length > 0
                    && !string.IsNullOrWhiteSpace(t.GetString()))
                {
                    var enabled = true;
                    if (r.TryGetProperty("enabled", out var e))
                    {
                        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { skipped++; continue; }
                        enabled = e.GetBoolean();
                    }
                    rules.Add(new DictionaryRule(o.GetString()!, t.GetString()!, enabled));
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new DictionarySnapshot(words, rules);
    }
}
