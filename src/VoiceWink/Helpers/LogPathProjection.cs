namespace VoiceWink.Helpers;

/// <summary>
/// Projects a user-chosen file path down to the part that is useful in a diagnostic and drops the
/// part that identifies the user's work.
///
/// <para><b>Why (SEC-4).</b> Export/import/transcribe sites log the path the user picked.
/// <c>LogRedactionEnricher.RedactString</c> folds the Windows USERNAME segment
/// (<c>C:\Users\&lt;USER&gt;\…</c>) but deliberately keeps the tail, so
/// <c>C:\Users\&lt;USER&gt;\Documents\Acme\medical-note.csv</c> still names a client and a subject
/// in a Sentry breadcrumb, a support zip and a GDPR export (Codex diff r3). The directory tree is
/// where that information lives; the file name and extension are what a support case actually
/// needs — "did the export write a .csv, and what was it called".</para>
///
/// <para>Owner decision 2026-08-07: fold to <b>filename + extension only</b>. Dropping the path
/// entirely was considered and rejected — an export or import failure becomes materially harder
/// to diagnose from a bundle with no file identity at all.</para>
///
/// <para>Note the file NAME can still be user-authored ("Acme Q3 layoffs.csv"). This narrows the
/// exposure to what the diagnostic needs; it does not claim to eliminate it. The paired log
/// property rides the <c>filePath</c> token, which the rendered scrub redacts, so the local file
/// keeps the projection and remote sinks keep nothing.</para>
/// </summary>
internal static class LogPathProjection
{
    /// <summary>Shown when a path is null, empty, or has no file-name component.</summary>
    internal const string UnknownMarker = "<none>";

    /// <summary>
    /// Returns the file name with extension, with no directory component. Never throws — a path
    /// containing characters illegal on this platform still yields something loggable, because a
    /// diagnostic that throws while describing a failure is worse than a vague one.
    /// </summary>
    internal static string FileNameOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UnknownMarker;

        string name;
        try
        {
            name = global::System.IO.Path.GetFileName(path.Trim());
        }
        // Filtered rather than a bare catch-all: the contract is "never throws for a malformed
        // PATH", not "swallows a dying process". OutOfMemory / StackOverflow / thread-abort must
        // keep propagating (Kimi diff r6).
        catch (global::System.Exception ex) when (ex is global::System.ArgumentException
                                                     or global::System.IO.IOException
                                                     or global::System.NotSupportedException)
        {
            // Deliberately catch-all, and the doc comment above is why: this method promises never
            // to throw, and `ArgumentException` alone does not deliver that — `PathTooLongException`
            // (an IOException) and `NotSupportedException` are both reachable on some runtimes for
            // a hostile path (Kimi diff r1). A narrower catch would make the contract a lie on
            // exactly the malformed input this helper exists to survive.
            var idx = path.LastIndexOfAny(new[] { '\\', '/' });
            name = idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
        }

        // A trailing separator yields an empty name; a bare directory has no file identity to log.
        if (string.IsNullOrWhiteSpace(name)) return UnknownMarker;

        return LogValueSanitizer.SingleLine(name);
    }
}
