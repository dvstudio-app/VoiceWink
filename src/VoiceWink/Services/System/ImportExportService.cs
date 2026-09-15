using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AppMode;

namespace VoiceWink.Services.System;

/// <summary>
/// Export/import all settings as JSON.
/// </summary>
public sealed class ImportExportService
{
    private static ILogger Logger => Log.ForContext<ImportExportService>();

    private readonly SettingsService _settings;

    public ImportExportService(SettingsService settings)
    {
        _settings = settings;
    }

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

    public async Task ExportAsync(string filePath)
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
        if (_settings.IsPersistenceSuppressed)
            throw new InvalidOperationException(
                "Settings can't be exported right now — persistence is disabled (e.g. a data wipe is in progress). Restart the app and try again.");

        var settingsPath = _settings.FilePath;
        var filtered = new Dictionary<string, JsonElement>();
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

        // Write to a SAME-DIRECTORY temp file, then atomically replace the target — so a write
        // failure mid-way can't truncate the user's EXISTING backup at filePath (Codex diff r5).
        var options = new JsonSerializerOptions { WriteIndented = true };
        var tmpPath = filePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(filtered, options)).ConfigureAwait(false);
            File.Move(tmpPath, filePath, overwrite: true); // same-volume rename = atomic replace
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
            throw;
        }

        // SEC-4: user-chosen destination. Token last so the rendered scrub reaches it.
        Logger.Information("Settings exported ({Stripped} sensitive keys excluded): filePath={UserFilePath}",
            strippedCount, Helpers.LogPathProjection.FileNameOnly(filePath));
    }

    /// <summary>Maximum import file size (1 MB).</summary>
    private const long MaxImportFileSizeBytes = 1_048_576;

    /// <summary>Outcome of a settings import.</summary>
    public sealed record ImportResult(int AppliedCount);

    public async Task<ImportResult> ImportAsync(string filePath)
    {
        // Validate file size before reading
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxImportFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"Import file is too large ({fileInfo.Length:N0} bytes). Maximum allowed size is {MaxImportFileSizeBytes:N0} bytes.");
        }

        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

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

        // SEC-4: user-chosen source.
        Logger.Information("Settings imported ({Count} keys applied): filePath={UserFilePath}",
            importedCount, Helpers.LogPathProjection.FileNameOnly(filePath));
        return new ImportResult(importedCount);
    }
}
