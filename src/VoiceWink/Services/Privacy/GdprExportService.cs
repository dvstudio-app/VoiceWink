using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.Data;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Privacy;

/// <summary>Outcome of a GDPR export — how many media/log files were skipped (in use) and omitted.</summary>
public sealed record GdprExportResult(int SkippedFileCount);

/// <summary>
/// GDPR right-of-access / data-portability export (Art. 15 / 20). Produces a single ZIP
/// containing every personal-data store the app holds, in portable formats:
/// <list type="bullet">
///   <item><c>settings.json</c> — all preferences, with sensitive values (API keys, license,
///     custom base URLs) replaced by <c>&lt;REDACTED&gt;</c>. Keys are kept so the user can see
///     which providers were configured; the values are DPAPI-encrypted and not portable anyway.</item>
///   <item><c>history.csv</c> (convenience) + <c>history.json</c> (complete — every
///     <see cref="TranscriptionRecord"/> field, media paths reduced to basenames).</item>
///   <item><c>vocabulary.json</c>, <c>word-replacements.json</c> — the dictionary tables.</item>
///   <item><c>README.txt</c> — explains the contents and limitations.</item>
///   <item>When <c>includeMedia</c> is set: <c>recordings/</c>, <c>images/</c>, and
///     <c>logs/</c> (logs scrubbed via <see cref="LogRedactionEnricher.RedactString"/>).</item>
/// </list>
/// <c>Models/</c> is intentionally excluded — it is a re-downloadable cache, not personal data.
/// Files that are locked/in-use at export time are skipped and counted in
/// <see cref="GdprExportResult.SkippedFileCount"/> so the UI can tell the user the archive is
/// incomplete rather than silently claiming success.
/// </summary>
public sealed class GdprExportService
{
    private static ILogger Logger => Log.ForContext<GdprExportService>();

    private const string RedactedValue = "<REDACTED>";

    private readonly SettingsService _settings;
    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly CsvExportService _csv;
    private readonly string _rootDir;
    private readonly string _recordingsDir;
    private readonly string _imagesDir;
    private readonly string _referencesDir;
    private readonly string _logsDir;
    private readonly string _reportsDir;

    public GdprExportService(
        SettingsService settings,
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        CsvExportService csv,
        string? rootDir = null)
    {
        _settings = settings;
        _dbFactory = dbFactory;
        _csv = csv;
        var root = rootDir ?? AppPaths.RootDir;
        _rootDir = root;
        _recordingsDir = Path.Combine(root, "Recordings");
        _imagesDir = Path.Combine(root, "Images");
        _referencesDir = Path.Combine(root, "References");
        _logsDir = Path.Combine(root, "Logs");
        _reportsDir = Path.Combine(root, "Reports");
    }

    /// <summary>
    /// Write the export ZIP to <paramref name="zipPath"/>. When <paramref name="includeMedia"/>
    /// is true, audio recordings, generated images, and redacted logs are bundled too (these can
    /// be large, so the UI defaults the option off). Returns a result reporting how many files were
    /// in use and therefore omitted.
    /// </summary>
    public async Task<GdprExportResult> ExportAsync(string zipPath, bool includeMedia, CancellationToken ct = default)
    {
        // Legacy-key migration must run BEFORE Flush so its removal is materialized into the
        // settings.json snapshot this export redacts (keys are kept in the redacted output, so
        // the pre-rename key name must not linger here either).
        AppModeSettingsMigration.Run(_settings);

        _settings.Flush();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var records = await db.TranscriptionRecords.AsNoTracking()
            .OrderByDescending(r => r.Timestamp).ToListAsync(ct).ConfigureAwait(false);
        var vocabulary = await db.VocabularyWords.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var replacements = await db.WordReplacements.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var skipped = new List<string>();

        await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddTextEntry(zip, "settings.json", BuildRedactedSettingsJson(skipped));
        AddTextEntry(zip, "history.csv", _csv.ExportToCsv(records));
        AddTextEntry(zip, "history.json", JsonSerializer.Serialize(records.Select(ToPortable), jsonOpts));
        AddTextEntry(zip, "vocabulary.json", JsonSerializer.Serialize(vocabulary, jsonOpts));
        AddTextEntry(zip, "word-replacements.json", JsonSerializer.Serialize(replacements, jsonOpts));
        AddTextEntry(zip, "README.txt", ReadmeText(includeMedia));

        if (includeMedia)
        {
            AddDirectoryFiles(zip, _recordingsDir, "recordings", skipped, ct);
            // REL-17: the Debug retention subfolder + report bundles ride the media
            // opt-in too (report bundles can hold raw prompts + voice audio and may
            // outlive their expired sources — Codex plan round 4). Both go through the
            // kernel-verified read gate: naive recursion would follow a planted junction.
            AddVerifiedDirectoryFiles(zip, Path.Combine(_recordingsDir, "Debug"), _recordingsDir, "recordings/Debug", skipped, ct);
            AddDirectoryFiles(zip, _imagesDir, "images", skipped, ct);
            AddDirectoryFiles(zip, _referencesDir, "references", skipped, ct);
            // Containment root is the APP ROOT, not Reports itself (diff review round 3:
            // when the checked dir IS the junction, both sides resolve through it and
            // "containment" trivially passes) — plus the report-name pattern so nothing
            // unrelated can ever be packaged.
            AddVerifiedDirectoryFiles(zip, _reportsDir, _rootDir, "reports", skipped, ct, "voicewink-report-*");
            AddRedactedLogs(zip, _logsDir, skipped, ct);
        }

        Logger.Information("GDPR export written ({Records} records, includeMedia={IncludeMedia}, skipped={Skipped})",
            records.Count, includeMedia, skipped.Count);
        return new GdprExportResult(skipped.Count);
    }

    /// <summary>
    /// Reads settings.json and redacts sensitive values (keeps keys). Fail-soft: a locked or
    /// corrupt file (e.g. a CR-1 protect-mode session where the on-disk primary is unreadable)
    /// must degrade to a placeholder entry counted in the skipped list — a GDPR export that
    /// crashes outright on one bad file denies the user their Art. 15/20 copy of everything else.
    /// </summary>
    private string BuildRedactedSettingsJson(List<string> skipped)
    {
        var result = new Dictionary<string, object?>();
        var path = _settings.FilePath;
        try
        {
            // Read directly (no File.Exists pre-check): Exists also returns false on ACCESS
            // errors, which would silently export {} with a skipped count of zero. Reading
            // first lets a genuine absence and a read failure take their own branches.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = ImportExportService.IsSensitiveKey(prop.Name)
                    ? RedactedValue
                    : JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Fresh install / no settings file yet — an empty settings object, nothing skipped.
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "settings.json could not be read/parsed for GDPR export; emitting placeholder");
            skipped.Add("settings.json");
            result.Clear();
            result["_error"] = "settings.json could not be read or parsed at export time; " +
                "the file was left in place and is not included in this archive.";
        }
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Project a record for export, reducing media paths to basenames (no local %LOCALAPPDATA% leak).</summary>
    private static object ToPortable(TranscriptionRecord r) => new
    {
        r.Id,
        r.Text,
        r.EnhancedText,
        r.Timestamp,
        r.DurationSeconds,
        AudioFile = BaseName(r.AudioFilePath),
        r.ModelName,
        r.Language,
        r.WasEnhanced,
        r.PromptUsed,
        r.EnhancementModelName,
        ImageFile = BaseName(r.ImageFilePath),
        r.ImageAspect,
        r.ImageSizeTier,
        r.ImageQuality,
        r.ImageVersionCount,
        ReferenceImageFiles = ReferenceBaseNames(r.ReferenceImagePath),
    };

    private static string? BaseName(string? path) =>
        string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);

    /// <summary>
    /// ENH-6f: the row's encoded reference value decodes to one basename per copy
    /// (was the singular <c>ReferenceImageFile</c>). Fail-soft per item — a malformed
    /// segment is skipped, never fails the Art. 15/20 export; null when none survive.
    /// </summary>
    private static IReadOnlyList<string>? ReferenceBaseNames(string? encoded)
    {
        List<string>? names = null;
        foreach (var path in Helpers.ReferencePathList.Decode(encoded))
        {
            string? name;
            try { name = Path.GetFileName(path); }
            catch { continue; }
            if (!string.IsNullOrEmpty(name))
                (names ??= new List<string>()).Add(name);
        }
        return names;
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddDirectoryFiles(ZipArchive zip, string sourceDir, string entryPrefix, List<string> skipped, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir)) return;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                zip.CreateEntryFromFile(file, $"{entryPrefix}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked or permission-denied file shouldn't abort the whole export —
                // record it as omitted (F37; ACL faults throw UnauthorizedAccessException,
                // which the IOException-only filter let escape and kill the entire archive).
                Logger.Warning(ex, "Skipped {Prefix} file during export (in use or unreadable)", entryPrefix);
                skipped.Add($"{entryPrefix}/{Path.GetFileName(file)}");
            }
        }
    }

    /// <summary>
    /// REL-17: like <see cref="AddDirectoryFiles"/> but every file opens through the
    /// kernel-verified <see cref="VerifiedFileAccess"/> gate (containment under
    /// <paramref name="containmentRoot"/>) and copies from the SAME opened stream — a
    /// junction planted inside a data subfolder must not exfiltrate outside files into
    /// the user's export. A gate refusal counts as skipped, never substituted.
    /// </summary>
    private static void AddVerifiedDirectoryFiles(
        ZipArchive zip, string sourceDir, string containmentRoot, string entryPrefix, List<string> skipped, CancellationToken ct,
        string searchPattern = "*")
    {
        if (!Directory.Exists(sourceDir)) return;
        // A reparse-point source dir is refused outright (matches the sweep policy) —
        // and the refusal is REPORTED as an omission (diff review round 4): an export
        // that silently drops a whole root while saying "complete" denies the user
        // their Art. 15/20 completeness signal. Absence above stays non-skip (a dir
        // that never existed holds nothing).
        if (VerifiedFileAccess.IsReparsePointOrUnreadable(sourceDir))
        {
            Logger.Warning("Skipped {Prefix} during export — folder could not be verified", entryPrefix);
            skipped.Add($"{entryPrefix}/ (folder could not be verified)");
            return;
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir, searchPattern))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = VerifiedFileAccess.TryOpenVerifiedUnder(file, containmentRoot, out _);
                if (stream == null)
                {
                    skipped.Add($"{entryPrefix}/{Path.GetFileName(file)}");
                    continue;
                }
                var entry = zip.CreateEntry($"{entryPrefix}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                stream.CopyTo(entryStream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Skipped {Prefix} file during export (in use or unreadable)", entryPrefix);
                skipped.Add($"{entryPrefix}/{Path.GetFileName(file)}");
            }
        }
    }

    private static void AddRedactedLogs(ZipArchive zip, string logsDir, List<string> skipped, CancellationToken ct)
    {
        if (!Directory.Exists(logsDir)) return;
        foreach (var file in Directory.EnumerateFiles(logsDir, "*.log"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var redacted = new StringBuilder();
                // FileShare.ReadWrite — the live log is held open by the Serilog sink.
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        redacted.AppendLine(LogRedactionEnricher.RedactString(line));
                }
                AddTextEntry(zip, $"logs/{Path.GetFileName(file)}", redacted.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Skipped log file during export (in use or unreadable)");
                skipped.Add($"logs/{Path.GetFileName(file)}");
            }
        }
    }

    private static string ReadmeText(bool includeMedia) =>
        "VoiceWink — personal data export\n" +
        "================================\n\n" +
        "This archive contains the personal data VoiceWink stores on this device (GDPR Art. 15 / 20).\n\n" +
        "Contents:\n" +
        "  settings.json          Your preferences. Sensitive values (API keys, license key,\n" +
        "                         custom base URLs) appear as \"<REDACTED>\". They are encrypted by\n" +
        "                         Windows (DPAPI) and tied to your Windows account, so they cannot\n" +
        "                         be decrypted by another app or machine — re-enter them from your\n" +
        "                         password manager or the provider's dashboard if migrating.\n" +
        "  history.csv            Transcription history (spreadsheet-friendly).\n" +
        "  history.json           Transcription history (complete — every field).\n" +
        "  vocabulary.json        Your custom vocabulary entries.\n" +
        "  word-replacements.json Your word-replacement rules.\n" +
        (includeMedia
            ? "  recordings/            Your saved audio recordings (.wav). A recording without a\n" +
              "                         matching history entry is a failed dictation retained for the\n" +
              "                         in-app Retry button (deleted on retry, new recording, or exit).\n" +
              "  recordings/Debug/      Recordings kept because the \"Keep audio recordings\"\n" +
              "                         debugging option is (or was) enabled — deleted automatically\n" +
              "                         after 7 days.\n" +
              "  reports/               Problem-report bundles not yet emailed (kept for manual\n" +
              "                         attach; deleted automatically after 7 days).\n" +
              "  images/                Your generated images.\n" +
              "  references/            Reference images used for generation (stored once per unique\n" +
              "                         image and shared by the History entries that use it; a copy\n" +
              "                         is deleted when the last entry referencing it is deleted —\n" +
              "                         or, if a redo was still using it, normally right after;\n" +
              "                         interrupted cleanup is retried by a startup sweep).\n" +
              "  logs/                  Diagnostic logs, with secrets redacted.\n"
            : "  (Recordings, images, reference images, problem-report bundles, and logs were\n" +
              "   not included — re-export with the \"Include recordings, images, problem\n" +
              "   reports and logs\" option to add them.)\n") +
        "\n" +
        "Not included: downloaded speech models (Models/) — these are a re-downloadable cache,\n" +
        "not personal data.\n\n" +
        "Re-import: history, vocabulary, and word replacements can be imported into a fresh\n" +
        "VoiceWink install; API keys and the license key must be re-entered.\n";
}
