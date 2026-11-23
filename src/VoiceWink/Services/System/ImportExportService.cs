using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement;
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
    /// <summary>SET-1: true only in a settings export that carries every valid per-provider model choice, so an import may clear the ones it does not carry.</summary>
    internal const string ModelPrefsCompleteKey = "_modelPrefsComplete";

    private readonly SettingsService _settings;
    private readonly object _importGate = new(); // SET-1: two imports never build candidates from the same live state.
    private readonly DictionaryTransfer? _dictionary;
    private readonly AIEnhancementService? _enhancement;
    private readonly AppModeManager? _appModes;

    /// <param name="dictionary">The Dictionary's database side. Optional only so settings-only
    /// callers (and their tests) need not build a database; DI always supplies it, and asking for
    /// the Dictionary part without it is refused before anything is written.</param>
    /// <param name="enhancement">ENH-39: its prompt cache is dropped after a settings import; DI supplies it.</param>
    /// <param name="appModes">ENH-39: its App Mode cache is dropped after a settings import; DI supplies it.</param>
    public ImportExportService(SettingsService settings, DictionaryTransfer? dictionary = null,
        AIEnhancementService? enhancement = null, AppModeManager? appModes = null)
    {
        _settings = settings;
        _dictionary = dictionary;
        _enhancement = enhancement;
        _appModes = appModes;
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

    /// <summary>The value test import applies to every setting, reused by export so an exported value always imports (SET-1).</summary>
    private static bool IsValidSettingValue(string key, JsonElement value) =>
        TryGetExpectedKind(key, out var expectedKind)
        && IsKindCompatible(expectedKind, value.ValueKind)
        && IsSemanticallyValidForImport(key, value);

    private static bool IsModelPrefKey(string key) =>
        key.StartsWith(AppDefaults.AiModelPrefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(AppDefaults.AiImageModelPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The spelling the app reads a per-provider model choice under (<see cref="AppDefaults.AiModelKey"/>): the prefix, then the provider in lower case (SET-1).</summary>
    private static string CanonicalModelPrefKey(string key)
    {
        var prefix = key.StartsWith(AppDefaults.AiModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? AppDefaults.AiModelPrefix
            : AppDefaults.AiImageModelPrefix;
        return prefix + key[prefix.Length..].ToLowerInvariant();
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
        {
            (strippedCount, var modelPrefsComplete) = await CollectSettingsAsync(filtered).ConfigureAwait(false);
            if (modelPrefsComplete)
                filtered[ModelPrefsCompleteKey] = JsonSerializer.SerializeToElement(true);
        }
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
    /// value; returns how many persisted keys were stripped, and whether every stored model choice made it in.</summary>
    private async Task<(int Stripped, bool ModelPrefsComplete)> CollectSettingsAsync(Dictionary<string, JsonElement> filtered)
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
        var leftOut = 0;
        var invalidKeys = new HashSet<string>(StringComparer.Ordinal);

        // Read the persisted snapshot. A GENUINE absence (fresh profile, nothing persisted yet) is
        // fine — the synthesized defaults below still export. Any OTHER read failure (access
        // denied, sharing lock) must PROPAGATE, not silently produce a near-empty "successful"
        // export (Codex diff r3 — File.Exists can't distinguish absence from an access failure).
        string? json = null;
        try { json = await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false); }
        catch (FileNotFoundException) { /* genuine fresh profile */ }
        catch (DirectoryNotFoundException) { /* genuine fresh profile */ }
        EnsurePersistenceAvailable("exported"); // SET-1: an erasure that began during the read must not export a bogus snapshot
        // SET-1: only a snapshot actually read may claim the complete model-pref set; a missing file says nothing about the source's choices.
        var modelPrefsComplete = json != null;

        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);

            // Export only IMPORTABLE preferences: strip sensitive keys (DPAPI key blobs, license,
            // custom base URLs) AND every import-protected key (onboarding, consent, legal
            // acceptance, app-managed/transient state, the defaults manifest) — export and import
            // exclude the SAME set, so the file is symmetric with what an import will accept.
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!IsImportableKey(prop.Name))
                {
                    strippedCount++;
                    continue;
                }
                // SET-1: a mis-cased model key is one the app never reads, so it must not travel and override the one it does.
                if (IsModelPrefKey(prop.Name) && prop.Name != CanonicalModelPrefKey(prop.Name))
                {
                    leftOut++;
                    continue;
                }
                // SET-1: a value import would refuse is left out, never replaced by a default, so the target keeps its own.
                if (!IsValidSettingValue(prop.Name, prop.Value))
                {
                    leftOut++;
                    invalidKeys.Add(prop.Name);
                    if (IsModelPrefKey(prop.Name)) modelPrefsComplete = false;
                    continue;
                }
                filtered[prop.Name] = prop.Value.Clone();
            }
        }
        if (leftOut > 0)
            Logger.Warning("Export: {LeftOut} stored settings were left out (a value an import would refuse, or a key spelling the app does not read)", leftOut);

        // Synthesize the EFFECTIVE value of every importable default-backed preference the user
        // never explicitly changed (unpersisted → absent above). Without this a profile left at
        // defaults wouldn't round-trip: importing it over a MODIFIED target would keep the target's
        // values instead of restoring the source's effective defaults (Codex diff r4). An
        // unpersisted key's effective value IS its registered default.
        foreach (var (key, defaultValue) in AppDefaults.Defaults)
        {
            if (IsImportableKey(key) && !filtered.ContainsKey(key) && !invalidKeys.Contains(key))
                filtered[key] = JsonSerializer.SerializeToElement(defaultValue);
        }

        return (strippedCount, modelPrefsComplete);
    }

    /// <summary>
    /// A backup file read and validated by <see cref="ReadBackupAsync"/>, holding nothing of the
    /// file but the validated settings and the parsed Dictionary — applying it never reads the
    /// file again.
    /// </summary>
    public sealed class BackupFile
    {
        internal BackupFile(IReadOnlyDictionary<string, JsonElement> settingsToApply, DictionarySnapshot? dictionary, int dictionarySkipped, string sourceName, int settingsRefused = 0,
            bool modelPrefsComplete = false, HashSet<string>? suppliedModelPrefKeys = null)
        {
            SettingsToApply = settingsToApply;
            Dictionary = dictionary;
            DictionarySkipped = dictionarySkipped;
            SourceName = sourceName;
            SettingsRefused = settingsRefused;
            ModelPrefsComplete = modelPrefsComplete;
            SuppliedModelPrefKeys = suppliedModelPrefKeys ?? new HashSet<string>(StringComparer.Ordinal);
        }

        internal IReadOnlyDictionary<string, JsonElement> SettingsToApply { get; }
        /// <summary>SET-1: the file says it carries every per-provider model choice its source had.</summary>
        internal bool ModelPrefsComplete { get; }
        /// <summary>SET-1: the per-provider model keys the file names, in the app's spelling, valid or not.</summary>
        internal HashSet<string> SuppliedModelPrefKeys { get; }
        /// <summary>SET-3: settings in the file the gate skipped (protected, unknown, wrong type, out of range).</summary>
        internal int SettingsRefused { get; }
        internal DictionarySnapshot? Dictionary { get; }
        /// <summary>Dictionary entries that were not words or rules (wrong type, a blank side).</summary>
        internal int DictionarySkipped { get; }
        /// <summary>The file NAME only (SEC-4) for log lines.</summary>
        internal string SourceName { get; }

        public bool HasSettings => ModelPrefsComplete || SettingsToApply.Count > 0 || SettingsRefused > 0; // SET-3: a file of refused settings still reports its count; SET-1: a complete model-pref set can clear choices on its own.
        public bool HasDictionary => Dictionary != null;
    }

    /// <summary>Outcome of an import. <paramref name="DictionaryFailed"/>: the settings were applied
    /// and durable, then the Dictionary could not be written (nothing of it was added).
    /// <paramref name="SettingsRefused"/>: settings in the file that were not applied (SET-3).</summary>
    public sealed record ImportResult(
        int AppliedCount,
        DictionaryMergeResult? Dictionary = null,
        int DictionarySkipped = 0,
        bool DictionaryFailed = false,
        int SettingsRefused = 0);

    /// <summary>Settings-only import (the pre-Dictionary behaviour): a file's Dictionary is
    /// ignored, and a file with no applicable setting applies nothing rather than failing.</summary>
    public async Task<ImportResult> ImportAsync(string filePath)
    {
        var backup = await ReadBackupAsync(filePath); // ENH-39: stay on the caller's thread, ApplySettings tells UI-bound holders
        var (applied, refused) = ApplySettings(backup);
        return new ImportResult(applied, SettingsRefused: refused);
    }

    /// <summary>Reads, validates and applies <paramref name="parts"/> of a file in one go — the
    /// Dictionary page's path; the gate is the same one the Settings page previews with.</summary>
    public async Task<ImportResult> ImportAsync(string filePath, BackupParts parts)
        => await ImportAsync(await ReadBackupAsync(filePath), parts); // ENH-39: caller's thread, as above

    /// <summary>
    /// Applies the requested parts of a validated backup: settings first (durable, or nothing
    /// changed), then the Dictionary in one database write. A part the file does not carry is
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

        var (applied, refused) = wantSettings ? ApplySettings(backup) : (0, 0);
        if (dictionary == null)
            return new ImportResult(applied, SettingsRefused: refused);

        // SET-1: one suppression decision before the database is opened, so an erasure that began during the settings write never reaches it.
        if (!wantSettings)
            EnsurePersistenceAvailable("imported");
        else if (_settings.IsPersistenceSuppressed)
        {
            // An expected outcome, so a Warning, not a Sentry event.
            Logger.Warning("Dictionary import skipped: persistence was disabled after the settings were applied");
            return new ImportResult(applied, null, backup.DictionarySkipped, DictionaryFailed: true, SettingsRefused: refused);
        }
        try
        {
            var merged = await dictionary.MergeAsync(backup.Dictionary!).ConfigureAwait(false);
            return new ImportResult(applied, merged, backup.DictionarySkipped, SettingsRefused: refused);
        }
        catch (Exception ex) when (wantSettings)
        {
            Logger.Error(ex, "Dictionary import failed after the settings were applied");
            return new ImportResult(applied, null, backup.DictionarySkipped, DictionaryFailed: true, SettingsRefused: refused);
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
        var hasFormat = doc.RootElement.TryGetProperty(FormatKey, out var format);
        if (hasFormat && !(format.ValueKind == JsonValueKind.String && format.GetString() == FormatValue))
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
        var suppliedModelPrefKeys = new HashSet<string>(StringComparer.Ordinal);
        // SET-1: an older export can carry a stale mis-cased alias beside the spelling the app read; with the exact spelling present, valid or not, the alias never applies.
        var exactModelPrefKeys = doc.RootElement.EnumerateObject()
            .Select(prop => prop.Name)
            .Where(key => IsModelPrefKey(key) && key == CanonicalModelPrefKey(key))
            .ToHashSet(StringComparer.Ordinal);
        // SET-1: only a formatted backup may claim a complete model-pref set, so a hand-made file never resets anything.
        var modelPrefsComplete = hasFormat && doc.RootElement.TryGetProperty(ModelPrefsCompleteKey, out var complete)
            && complete.ValueKind == JsonValueKind.True;
        var refused = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var name = prop.Name;
            if (name is FormatKey or DictionaryKey or ModelPrefsCompleteKey)
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
            else if (IsModelPrefKey(name))
            {
                name = CanonicalModelPrefKey(name); // SET-1: a mis-cased key lands where the app reads it
                suppliedModelPrefKeys.Add(name); // before validation, so a refused value keeps the target's choice
            }

            if (IsImportProtectedKey(name))
            {
                Logger.Information("Import: device-local state key '{Key}' skipped — not importable", prop.Name);
                refused++;
                continue;
            }
            if (!TryGetExpectedKind(name, out var expectedKind))
            {
                // SEC-4: the key comes from a file the user supplied, so it is arbitrary attacker- or
                // user-controlled text — including line breaks. Sanitized and scrub-covered.
                Logger.Warning("Import: unknown setting skipped: userTerm={ImportedSettingKey}", Helpers.LogValueSanitizer.SingleLine(prop.Name));
                refused++;
                continue;
            }
            if (!IsKindCompatible(expectedKind, prop.Value.ValueKind))
            {
                Logger.Warning(
                    "Import: setting has a type mismatch (expected {Expected}, got {Got}) — skipped: userTerm={ImportedSettingKey}",
                    expectedKind, prop.Value.ValueKind, Helpers.LogValueSanitizer.SingleLine(prop.Name));
                refused++;
                continue;
            }
            if (!IsSemanticallyValidForImport(name, prop.Value))
            {
                Logger.Warning("Import: setting '{Key}' has an out-of-range value — skipped", prop.Name);
                refused++;
                continue;
            }

            if (prop.Name != name && IsModelPrefKey(name) && exactModelPrefKeys.Contains(name))
                continue;

            toApply[name] = prop.Value.Clone(); // last-valid-wins on duplicate canonical keys
        }

        return new BackupFile(toApply, dictionary, dictionarySkipped, Helpers.LogPathProjection.FileNameOnly(filePath), refused,
            modelPrefsComplete, suppliedModelPrefKeys);
    }

    /// <summary>Applies a validated file's settings — durable, or nothing changed and thrown. Returns how
    /// many were applied (0 is a success: nothing in the file was applicable) and how many refused.</summary>
    private (int Applied, int Refused) ApplySettings(BackupFile backup)
    {
        (int Applied, int Refused) result;
        lock (_importGate)
        {
            result = ApplySettingsCore(backup);
        }
        // ENH-39: the import changed the store under the caches. SET-1: only after a commit, because a failed import changed nothing.
        _appModes?.DiscardCachedConfigs(); // first: the prompt discard raises an event, which must not skip this
        _enhancement?.DiscardCachedPrompts();
        _enhancement?.SyncOnThisPcResidency(); // SET-3: the imported provider, model or prompts decide which built-in model stays loaded.
        return result;
    }

    private (int Applied, int Refused) ApplySettingsCore(BackupFile backup)
    {
        var toApply = backup.SettingsToApply;
        // SET-1: built off-store and committed disk-first, so a failed or suppressed import changes nothing.
        var candidate = new SettingsCandidate(_settings);

        // UPD-4: invalidate the defaults-manifest domain(s) that WILL apply — derived from the
        // SAME canonical set, so it can't diverge from what the apply below does. It lands in the
        // same atomic write as the imported blobs.
        var willApplyPrompts = toApply.ContainsKey(AppDefaults.CustomPrompts);
        var willApplyAppModes = toApply.ContainsKey(AppDefaults.AppModeConfigs);
        if (willApplyPrompts || willApplyAppModes)
        {
            var newManifest = DefaultsManifest.WithDomainsInvalidated(
                candidate.GetString(AppDefaults.DefaultsManifest, ""), willApplyPrompts, willApplyAppModes);
            if (newManifest == null) candidate.Remove(AppDefaults.DefaultsManifest);
            else candidate.SetString(AppDefaults.DefaultsManifest, newManifest);
        }

        var importedCount = 0;
        var refused = backup.SettingsRefused;
        foreach (var (name, value) in toApply)
        {
            // REL-19: a recorded opt-out stays authoritative; only a durable toggle or reset clears it.
            if (ConsentTombstone.IsVetoed(_settings.FilePath, name))
            {
                Logger.Information("Import: '{Key}' skipped — a recorded opt-out keeps it off", name);
                if (value.ValueKind == JsonValueKind.True) refused++; // SET-3: an imported "off" agrees with the opt-out, so only an "on" was refused.
                continue;
            }
            candidate.Set(name, value);
            importedCount++;
        }

        // SET-1: a complete export's model memory replaces this one, so a per-provider choice the source never made is cleared.
        if (backup.ModelPrefsComplete)
        {
            foreach (var key in _settings.GetKeysSnapshot())
            {
                if (IsModelPrefKey(key) && !backup.SuppliedModelPrefKeys.Contains(CanonicalModelPrefKey(key))
                    && candidate.Remove(key))
                    importedCount++; // a cleared choice is a change the result must report

            }
        }

        // The import itself never writes the legacy key, but the CURRENT settings may still
        // carry one (import can run before anything else touched App Mode) — uphold the
        // "legacy key removed either way" invariant standalone rather than relying on the
        // startup migration having run first.
        AppModeSettingsMigration.Run(candidate);

        // TRN-6: same reasoning, one migration later. An imported backup predating the q8-only
        // catalogue carries model names that no longer resolve, and import applies BOTH surfaces
        // they live on (selectedModelName and appModeConfigs). Running the startup migration here
        // is not belt-and-braces: `MainViewModel` re-reads the selected model for EVERY recording,
        // so without this the very next dictation after an import fails with UnknownModel until
        // the app is restarted — and the dialog's restart hint is advice, not a guard (both diff
        // reviewers). It runs on the candidate, so the migrated names are what lands on disk.
        LocalModelMigration.RunOnSettings(candidate);

        // Durable-or-nothing: provider toggles are read per request, so nothing of the import may
        // reach the store unless all of it reached the disk.
        if (!_settings.TrySetManyAndFlushOrThrow(candidate.Changes))
            throw new InvalidOperationException(
                "Settings can't be imported right now — persistence is disabled (e.g. a data wipe is in progress). Restart the app and try again.");

        // SEC-4: user-chosen source (already projected to its file name).
        Logger.Information("Settings imported ({Count} keys applied, {Refused} refused): filePath={UserFilePath}",
            importedCount, refused, backup.SourceName);
        return (importedCount, refused);
    }

    /// <summary>The import's settings as they will be once committed, built without touching the store (SET-1).</summary>
    private sealed class SettingsCandidate : ISettingsAccess
    {
        private readonly SettingsService _live;
        // An Undefined value is a removal, the typed batch's own convention.
        private readonly Dictionary<string, JsonElement> _changes = new(StringComparer.Ordinal);

        public SettingsCandidate(SettingsService live) => _live = live;

        public IReadOnlyDictionary<string, JsonElement> Changes => _changes;

        public bool Contains(string key) =>
            _changes.TryGetValue(key, out var value) ? value.ValueKind != JsonValueKind.Undefined : _live.Contains(key);

        public string GetString(string key, string defaultValue = "") =>
            _changes.TryGetValue(key, out var value) ? SettingsService.Resolve(value, key, defaultValue) : _live.GetString(key, defaultValue);

        public void SetString(string key, string value) => Set(key, JsonSerializer.SerializeToElement(value));

        public void Set(string key, JsonElement value) => _changes[key] = value;

        public bool Remove(string key)
        {
            if (!Contains(key)) return false;
            _changes[key] = default;
            return true;
        }
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
