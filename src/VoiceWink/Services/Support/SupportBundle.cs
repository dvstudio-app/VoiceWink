using System.IO.Compression;
using System.Text;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Support;

/// <summary>How the prompt trace rides a report bundle (REL-17).</summary>
public enum PromptTraceMode
{
    /// <summary>No trace files at all.</summary>
    None,
    /// <summary>The write-time redacted sidecars only (<c>prompts-redacted-*.log</c>) —
    /// structure + validated metadata, all free text as byte counts. The default whenever
    /// logs are included.</summary>
    Redacted,
    /// <summary>The RAW trace files too (verbatim dictation + prompts). Explicit per-report
    /// user opt-in with a privacy advisory; secret redaction still applies per line.</summary>
    Raw,
}

/// <summary>
/// A recording the user consented to include, snapshotted AT CONSENT TIME (dialog open).
/// The bundle build revalidates this exact snapshot against the opened handle and never
/// re-enumerates — a file created or changed after the checkbox was shown must not ride
/// the report (Codex plan round 1/3). <see cref="ContentSha256"/> pins the BYTES, not just
/// metadata: a same-size same-timestamp replacement is rejected at build time (diff
/// review — metadata alone is not a consent snapshot).
/// </summary>
public readonly record struct RecordingCandidate(string Path, long Bytes, DateTime LastWriteUtc, string ContentSha256);

/// <summary>What goes into a report bundle. <see cref="PromptTrace"/> other than
/// <see cref="PromptTraceMode.None"/> requires <see cref="IncludeApplicationLogs"/> —
/// the trace rides WITH the logs (dialog enforces, the builder validates).
/// <see cref="EnvironmentSummary"/> is the pre-rendered environment/configuration block
/// (<c>DiagnosticSnapshot.RenderBlock</c>) appended to <c>report-info.txt</c> — rendered by the
/// caller so this builder stays free of settings reads; null (the tests' default) writes the
/// pre-audit file unchanged.</summary>
public sealed record SupportBundleOptions(
    bool IncludeApplicationLogs,
    PromptTraceMode PromptTrace,
    IReadOnlyList<RecordingCandidate> Recordings,
    string? EnvironmentSummary = null);

/// <summary>
/// Pure(ish) helpers behind the "Report a problem" flow (REL-3, extended by REL-17): bundle
/// the local logs — plus, per the user's per-report choices, the prompt trace
/// (redacted sidecars by default, raw on explicit opt-in) and a size-budgeted selection of
/// retained recordings — into a ZIP, and build a length-guarded <c>mailto:</c> URI.
/// Extracted from the dialog so the bundling + URI + selection logic is unit-testable
/// without constructing a WinUI control. Redaction reuses
/// <see cref="LogRedactionEnricher.RedactString"/> (the same primitive the Sentry sink
/// uses) — including RE-redacting the sidecars at bundle time, so a redaction rule added
/// after a sidecar was written still applies (Codex plan round 4). Recording reads go
/// through the kernel-verified <see cref="VerifiedFileAccess"/> gate and copy from the
/// SAME opened stream (no TOCTOU, no junction escape). ZIP writes are STAGED
/// (<c>.tmp</c> → move) so a partial bundle is never left looking complete.
/// </summary>
public static class SupportBundle
{
    private static ILogger Logger => Log.ForContext(typeof(SupportBundle));

    /// <summary>Conservative cap on the raw subject+body length to keep the mailto URI within
    /// the limits common mail clients impose (~2000 chars after URL-encoding).</summary>
    public const int MaxMailtoChars = 1800;

    internal const string OverflowNote = " (Full details are in the attached log file.)";

    /// <summary>Uncompressed budget for included recordings: 16 kHz mono 16-bit ≈ 1.9 MB/min,
    /// so ~8 minutes of audio; WAV barely compresses and mail attachments cap ~20-25 MB.</summary>
    public const long RecordingsBudgetBytes = 15 * 1024 * 1024;

    /// <summary>
    /// Enumerate WAVs eligible for a report — the Recordings root plus the REL-17
    /// <c>Debug\</c> retention folder — as consent-time snapshots. Fail-soft: enumeration
    /// trouble yields what was seen so far.
    /// </summary>
    public static IReadOnlyList<RecordingCandidate> EnumerateRecordingCandidates(string recordingsRoot)
    {
        var result = new List<RecordingCandidate>();
        try
        {
            foreach (var dir in new[] { recordingsRoot, Path.Combine(recordingsRoot, "Debug") })
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.wav"))
                {
                    try
                    {
                        // Metadata-only snapshot through the verified gate (a
                        // junction-planted file is never even OFFERED). Hashing is
                        // deliberately DEFERRED to WithContentHashes on the SELECTED
                        // ≤budget set, off the UI thread — hashing every WAV at dialog
                        // open froze the dispatcher (diff review round 2).
                        using var stream = VerifiedFileAccess.TryOpenVerifiedUnder(file, recordingsRoot, out _);
                        if (stream == null)
                            continue;
                        result.Add(new RecordingCandidate(file, stream.Length, File.GetLastWriteTimeUtc(file), ContentSha256: ""));
                    }
                    catch { /* vanished mid-enumeration — skip */ }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Recording candidate enumeration failed");
        }
        return result;
    }

    /// <summary>
    /// Complete the consent snapshot for the SELECTED candidates: hash each through the
    /// verified gate. Call OFF the UI thread with the ≤budget selection only (diff
    /// review round 2 — the split keeps dialog-open cheap while the build-time check
    /// still pins content identity). A candidate whose file changed size or can't be
    /// read keeps an empty hash, which the build then rejects as drift.
    /// </summary>
    public static IReadOnlyList<RecordingCandidate> WithContentHashes(
        IReadOnlyList<RecordingCandidate> candidates, string recordingsRoot)
    {
        var result = new List<RecordingCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var hash = "";
            try
            {
                using var stream = VerifiedFileAccess.TryOpenVerifiedUnder(candidate.Path, recordingsRoot, out _);
                if (stream != null && stream.Length == candidate.Bytes)
                    hash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(stream));
            }
            catch { /* drift/unreadable — the empty hash is rejected at build time */ }
            result.Add(candidate with { ContentSha256 = hash });
        }
        return result;
    }

    /// <summary>
    /// Newest-first selection under <paramref name="budgetBytes"/> (uncompressed). A file
    /// larger than the REMAINING budget is skipped and selection continues with older
    /// files — a single huge WAV must not block everything behind it. Deterministic;
    /// pinned by tests. The dialog computes its checkbox label from this SAME selection,
    /// so what it promises is what the zip gets.
    /// </summary>
    public static IReadOnlyList<RecordingCandidate> SelectRecordings(
        IReadOnlyList<RecordingCandidate> candidates, long budgetBytes)
    {
        var selected = new List<RecordingCandidate>();
        var remaining = budgetBytes;
        foreach (var candidate in candidates.OrderByDescending(c => c.LastWriteUtc))
        {
            if (candidate.Bytes <= 0 || candidate.Bytes > remaining)
                continue;
            selected.Add(candidate);
            remaining -= candidate.Bytes;
        }
        return selected;
    }

    /// <summary>
    /// Write the report ZIP per <paramref name="options"/>. Returns <c>true</c> on success;
    /// <c>false</c> (fail-closed) if anything throws or the options are inconsistent, so the
    /// caller can refuse to launch the email rather than risk shipping an unredacted or
    /// partial bundle. Production entry — binds the real recordings root and the
    /// kernel-verified opener.
    /// </summary>
    public static bool TryWriteReportZip(string logsDir, string zipPath, SupportBundleOptions options)
        => TryWriteReportZip(logsDir, zipPath, options, AppPaths.RecordingsDir,
            static (path, root) => VerifiedFileAccess.TryOpenVerifiedUnder(path, root, out _));

    /// <summary>
    /// Seam-carrying overload (Codex plan round 4: the signature must expose the
    /// injectable recordings root and opener — tests drive hostile openers and junction
    /// layouts deterministically through here).
    /// </summary>
    internal static bool TryWriteReportZip(
        string logsDir,
        string zipPath,
        SupportBundleOptions options,
        string recordingsRoot,
        Func<string, string, FileStream?> recordingOpener)
    {
        // An undefined mode must never fall through to the raw branch (diff round 5:
        // the Redacted-vs-else split would have treated cast garbage as "raw").
        if (!Enum.IsDefined(options.PromptTrace))
        {
            Logger.Warning("Report bundle refused: undefined prompt-trace mode {Mode}", (int)options.PromptTrace);
            return false;
        }

        // The trace rides WITH the logs — a recordings-only bundle must not smuggle trace
        // files, and the dialog's UI dependency (raw needs logs checked) is enforced here
        // too so no future caller can bypass it.
        if (options.PromptTrace != PromptTraceMode.None && !options.IncludeApplicationLogs)
        {
            Logger.Warning("Report bundle refused: prompt trace requires application logs");
            return false;
        }

        var tmpPath = zipPath + ".tmp";
        try
        {
            var includedRecordings = new List<(string Name, long Bytes)>();
            var skippedRecordings = 0;
            var logFiles = 0;
            var traceFiles = 0;

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (options.IncludeApplicationLogs && Directory.Exists(logsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(logsDir, "*.log"))
                    {
                        var name = Path.GetFileName(file);
                        // Trace files never ride the generic pass — they are added below
                        // per the explicit PromptTrace mode (REL-3's original exclusion,
                        // now mode-governed).
                        if (name.StartsWith("prompts-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        WriteRedactedTextEntry(zip, file, name);
                        logFiles++;
                    }

                    if (options.PromptTrace != PromptTraceMode.None && Directory.Exists(logsDir))
                    {
                        foreach (var file in Directory.EnumerateFiles(logsDir, "prompts-*.log"))
                        {
                            var name = Path.GetFileName(file);
                            var isSidecar = name.StartsWith(PromptTraceLog.RedactedFilePrefix,
                                StringComparison.OrdinalIgnoreCase);
                            // Redacted mode: sidecars only. Raw mode: raw files only — the
                            // sidecar would be pure duplication next to its raw source.
                            var wanted = options.PromptTrace == PromptTraceMode.Redacted ? isSidecar : !isSidecar;
                            if (!wanted)
                                continue;
                            // Re-redact per line at bundle time — sidecars are safe by
                            // construction, but a redaction rule added after the file was
                            // written must still apply; raw files get their only secret
                            // scrub here ("raw" never means "keys included").
                            WriteRedactedTextEntry(zip, file, name);
                            traceFiles++;
                        }
                    }
                }

                foreach (var candidate in options.Recordings)
                {
                    var stream = ValidateAndOpenRecording(candidate, recordingsRoot, recordingOpener);
                    if (stream == null)
                    {
                        skippedRecordings++;
                        continue;
                    }
                    using (stream)
                    {
                        var relative = Path.GetRelativePath(recordingsRoot, candidate.Path)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        var entry = zip.CreateEntry($"recordings/{relative}", CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        stream.CopyTo(entryStream);
                        includedRecordings.Add((relative, candidate.Bytes));
                    }
                }

                // Built from what was ACTUALLY copied — support triage sees at a glance
                // what it received (and what consent trimmed away).
                var traceSummary = options.PromptTrace switch
                {
                    PromptTraceMode.Redacted => $"redacted sidecars ({traceFiles} file(s))",
                    PromptTraceMode.Raw => $"RAW ({traceFiles} file(s), user opted in)",
                    _ => "not included",
                };
                var appVersion = global::System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                var info = new StringBuilder()
                    .AppendLine($"VoiceWink problem report · app {appVersion}")
                    .AppendLine($"created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z")
                    .AppendLine($"application logs: {(options.IncludeApplicationLogs ? $"{logFiles} file(s), secrets redacted" : "not included")}")
                    .AppendLine($"prompt trace: {traceSummary}")
                    .AppendLine($"recordings: {includedRecordings.Count} included ({includedRecordings.Sum(r => r.Bytes)} bytes), {skippedRecordings} skipped (changed/unavailable)");
                foreach (var (name, _) in includedRecordings)
                    info.AppendLine($"  recordings/{name}");
                if (!string.IsNullOrWhiteSpace(options.EnvironmentSummary))
                {
                    // Scrubbed line by line like the log copy (the EOL-anchored patterns do not
                    // span a block): the model id in the block comes from an EDITABLE combo box, so
                    // a key mis-pasted there would otherwise reach this one file unredacted.
                    info.AppendLine();
                    foreach (var line in options.EnvironmentSummary.TrimEnd().Split('\n'))
                        info.AppendLine(LogRedactionEnricher.RedactString(line.TrimEnd('\r')));
                }
                AddTextEntry(zip, "report-info.txt", info.ToString());
            }

            File.Move(tmpPath, zipPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to build support report bundle");
            TryDeleteQuiet(tmpPath);
            return false;
        }
    }

    /// <summary>
    /// REL-17 retention: delete report zips (and orphaned <c>.tmp</c> stages) older than
    /// 7 days from the app's Reports dir AND the legacy <c>%TEMP%</c> location the
    /// pre-REL-17 dialog wrote to. Called from the hourly cleanup pass; fail-soft per file.
    /// </summary>
    public static void SweepOldReports(string reportsDir, string legacyTempDir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var (dir, patterns) in new[]
        {
            (reportsDir, new[] { "voicewink-report-*.zip", "voicewink-report-*.zip.tmp" }),
            (legacyTempDir, new[] { "voicewink-report-*.zip" }),
        })
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                // Destructive sweep — same reparse/final-path guards as the recording
                // sweeps (REL-17 diff review: a junctioned dir would delete outside files).
                if (VerifiedFileAccess.IsReparsePointOrUnreadable(dir))
                    continue;
                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < cutoff &&
                                VerifiedFileAccess.IsVerifiedUnder(file, dir))
                            {
                                File.Delete(file);
                            }
                        }
                        catch { /* in use — next pass */ }
                    }
                }
            }
            catch { /* fail-soft per root */ }
        }
    }

    /// <summary>
    /// Open a consented recording through the verified gate and check the OPEN HANDLE
    /// against the consent-time snapshot: extension, containment (the opener), byte
    /// length, last-write time, and the CONTENT hash — verified on the same handle the
    /// copy then reads from, so a same-size same-timestamp replacement cannot ride the
    /// consent (diff review). Any drift ⇒ null (skip, never substitute); the stream is
    /// disposed on every rejection path.
    /// </summary>
    private static FileStream? ValidateAndOpenRecording(
        RecordingCandidate candidate, string recordingsRoot, Func<string, string, FileStream?> opener)
    {
        FileStream? stream = null;
        try
        {
            if (!candidate.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return null;
            stream = opener(candidate.Path, recordingsRoot);
            if (stream == null)
                return null;
            if (stream.Length != candidate.Bytes ||
                File.GetLastWriteTimeUtc(candidate.Path) != candidate.LastWriteUtc)
            {
                stream.Dispose();
                return null;
            }
            var hash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(stream));
            if (!hash.Equals(candidate.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                return null;
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>Read a text file line-by-line through <see cref="LogRedactionEnricher.RedactString"/>
    /// into a zip entry. FileShare.ReadWrite — the live log is held open by the Serilog sink.</summary>
    private static void WriteRedactedTextEntry(ZipArchive zip, string sourceFile, string entryName)
    {
        var redacted = new StringBuilder();
        using (var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                redacted.AppendLine(LogRedactionEnricher.RedactString(line));
        }
        AddTextEntry(zip, entryName, redacted.ToString());
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Build a <c>mailto:</c> URI, truncating the body if subject+body would exceed
    /// <see cref="MaxMailtoChars"/> (appending <see cref="OverflowNote"/> so the recipient knows
    /// the attached log carries the full detail). Subject and body are URL-encoded.
    /// </summary>
    public static string BuildMailto(string email, string subject, string body)
        => BuildMailto(email, subject, body, tail: null);

    /// <summary>
    /// The same, with a <paramref name="tail"/> RESERVED from the budget and appended after the
    /// (possibly truncated) body and its overflow note — so it survives however long the body is.
    /// Exists for the version footer (PR #938, Codex diff r1 Blocker): the footer is the one line
    /// that names the build on a report sent without logs, and appending it to the body put it
    /// exactly where the truncation cuts. A tail longer than the whole budget is still appended
    /// whole; the body is what gives way.
    /// </summary>
    public static string BuildMailto(string email, string subject, string body, string? tail)
    {
        subject ??= string.Empty;
        body ??= string.Empty;
        tail ??= string.Empty;

        var budget = MaxMailtoChars - subject.Length;
        if (budget < 0) budget = 0;
        if (body.Length + tail.Length > budget)
        {
            var keep = Math.Max(0, budget - tail.Length - OverflowNote.Length);
            body = body.Substring(0, Math.Min(body.Length, keep)) + OverflowNote;
        }

        return $"mailto:{email}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body + tail)}";
    }
}
