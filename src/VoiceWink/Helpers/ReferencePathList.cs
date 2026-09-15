namespace VoiceWink.Helpers;

/// <summary>
/// The ONE encode/decode site for the multi-value <c>TranscriptionRecords.ReferenceImagePath</c>
/// column (ENH-6f): multiple per-row reference-copy paths are stored in the existing single
/// TEXT column joined with <c>'|'</c> — an ILLEGAL character in Windows paths, so the join is
/// unambiguous. Encoding a ONE-element list yields the raw path itself, so pre-ENH-6f rows,
/// new single-reference rows, and a build DOWNGRADE all agree for the single-reference case
/// (multi-reference rows are declared downgrade-UNSUPPORTED — an older build's sweep treats
/// the joined value as one bogus path and reaps the copies; recorded decision, ENH-6f plan).
/// Every reference query compares canonically in memory, so no SQL semantics rest on the
/// column shape. Consumed by ReferencePersistence, TranscriptionHistoryService,
/// GdprExportService, and the History page. Pinned by ReferencePathListTests.
/// </summary>
public static class ReferencePathList
{
    public const char Separator = '|';

    /// <summary>
    /// Join copy paths into the column value: null for an empty list, the raw path for one
    /// (the compatibility passthrough), joined for many. Throws on a separator-bearing or
    /// blank segment — impossible for a real Windows path, and silently splitting a corrupt
    /// input into several rows' worth of paths must never happen (Codex plan round 1).
    /// </summary>
    public static string? Encode(IReadOnlyList<string>? paths)
    {
        if (paths == null || paths.Count == 0)
            return null;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Reference path list entries must be non-empty.", nameof(paths));
            if (path.Contains(Separator))
                throw new ArgumentException("Reference paths must not contain the separator character.", nameof(paths));
        }
        return paths.Count == 1 ? paths[0] : string.Join(Separator, paths);
    }

    /// <summary>
    /// Split a column value into paths. Tolerant by design (this reads DB values that may
    /// be corrupt or imported): blanks and whitespace-only segments are dropped, never
    /// thrown on. Null/empty input decodes to an empty list.
    /// </summary>
    public static IReadOnlyList<string> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return Array.Empty<string>();
        if (!encoded.Contains(Separator))
            return new[] { encoded };
        return encoded.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
