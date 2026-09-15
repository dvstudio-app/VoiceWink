namespace VoiceWink.Helpers;

/// <summary>
/// Centralizes the <c>%LOCALAPPDATA%/VoiceWink/...</c> filesystem layout. Every consumer
/// previously hand-rolled <c>Path.Combine(Environment.GetFolderPath(LocalApplicationData), "VoiceWink", ...)</c>
/// with the leaf folder name as a string literal — a typo or rename meant hunting 13 files.
///
/// <para>Each property is a static string the runtime can use directly. Subfolder names match
/// the layout documented in <c>CLAUDE.md</c> § Data Storage. <see cref="EnsureRoot"/>,
/// <see cref="EnsureLogs"/>, etc. wrap <c>Directory.CreateDirectory</c> when the caller
/// also needs the folder to exist.</para>
/// </summary>
public static class AppPaths
{
    /// <summary>Root directory: <c>%LOCALAPPDATA%/VoiceWink/</c>.</summary>
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceWink");

    public static string LogsDir       => Path.Combine(RootDir, "Logs");
    public static string ModelsDir     => Path.Combine(RootDir, "Models");
    public static string RecordingsDir => Path.Combine(RootDir, "Recordings");
    public static string ImagesDir     => Path.Combine(RootDir, "Images");
    /// <summary>ENH-6b: retained copies of reference images used by generations —
    /// content-addressed (keyed hash names) and SHARED between rows since ENH-6g, so a
    /// file is deleted only when the LAST History row referencing it (and the last
    /// live claim) is gone. A new app-owned data root MUST also be covered by
    /// DataErasureService (allowlist) and GdprExportService (media opt-in); pinned by
    /// the GDPR coverage test.</summary>
    public static string ReferencesDir => Path.Combine(RootDir, "References");
    /// <summary>Sentry offline-event cache (REL-1b): envelopes queued on disk awaiting an online flush.</summary>
    public static string SentryCacheDir => Path.Combine(RootDir, "sentry-cache");
    /// <summary>REL-17: problem-report bundles (Report a problem). Built here (never
    /// %TEMP%) so the mailto-fallback file the user must attach manually survives under an
    /// app-owned, erasure-covered root; deleted after a successful MAPI hand-off and swept
    /// after 7 days. Covered by DataErasureService (allowlist) — deliberately NOT in the
    /// GDPR media export: raw-mode bundles hold personal data, but export includes it via
    /// the top-level <c>reports/</c> opt-in copy instead (GdprExportService).</summary>
    public static string ReportsDir => Path.Combine(RootDir, "Reports");
    /// <summary>REL-17: opt-in debug-retained recording WAVs — the pipeline MOVES each
    /// recording here at the moment it would otherwise delete it
    /// (<c>AppDefaults.KeepRecordingsForDebug</c>). Location IS the retention marker: the
    /// 7-day sweep of this folder runs unconditionally (independent of the toggle and of
    /// history cleanup), so turning the setting off can never orphan files. Nested inside
    /// <see cref="RecordingsDir"/> so the existing erasure allowlist + export media opt-in
    /// cover it.</summary>
    public static string RecordingsDebugDir => Path.Combine(RecordingsDir, "Debug");

    public static string SettingsFile  => Path.Combine(RootDir, "settings.json");
    public static string DatabaseFile  => Path.Combine(RootDir, "voicewink.db");

    /// <summary>Ensure the <see cref="RootDir"/> folder exists (creates it idempotently).</summary>
    public static string EnsureRoot()       => Ensure(RootDir);
    public static string EnsureLogs()       => Ensure(LogsDir);
    public static string EnsureModels()     => Ensure(ModelsDir);
    public static string EnsureRecordings() => Ensure(RecordingsDir);
    public static string EnsureImages()     => Ensure(ImagesDir);
    public static string EnsureReferences() => Ensure(ReferencesDir);
    public static string EnsureReports()    => Ensure(ReportsDir);
    public static string EnsureRecordingsDebug() => Ensure(RecordingsDebugDir);

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
