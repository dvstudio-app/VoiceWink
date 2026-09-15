using VoiceWink.Services.Transcription;

namespace VoiceWink.Helpers;

/// <summary>
/// Validates a bundle-relative path before it becomes a filesystem path (TRN-1 step 2) — by
/// REJECTING, never by repairing, exactly like <see cref="ModelNameGuard"/>.
///
/// <para><b>Why this exists rather than reusing <see cref="ModelNameGuard"/> directly:</b> that guard
/// rejects directory separators outright, which is its whole job — a model NAME must not be a path.
/// A bundle-relative path legitimately contains them. So this borrows the guard's rule set and
/// applies it PER SEGMENT, which is the part that actually carries the safety.</para>
///
/// <para>Paths here come from the catalog, which is author-controlled, so this is defence against a
/// future one-line catalog edit rather than against a live attacker. It is still worth having: the
/// composed path drives a recursive directory delete.</para>
///
/// <para><b>Collection-level rules matter as much as per-path ones.</b> Two entries differing only in
/// case name one file on Windows, and <c>a</c> alongside <c>a/b</c> asks for a path to be both a file
/// and a directory. Neither is visible when validating a path in isolation, so
/// <see cref="ValidateAll"/> is the entry point and the single-path overload is its helper.</para>
/// </summary>
internal static class BundleRelativePathGuard
{
    /// <summary>
    /// The manifest's own filename, reserved so a bundle file can never overwrite the marker that
    /// decides whether the bundle is installed.
    /// </summary>
    internal const string ManifestFileName = ".vw-model-complete.json";

    /// <summary>
    /// The staging root's directory name, reserved as a MODEL NAME. Verified 2026-08-02:
    /// <c>ModelNameGuard.Validate(".staging")</c> PASSES — <c>".staging".Split('.')[0]</c> is the
    /// empty string, so the reserved-device-name check never fires — meaning a catalog model could
    /// legitimately be called <c>.staging</c> and collide its bundle directory with the staging root.
    /// </summary>
    internal const string StagingRootName = ".staging";

    /// <summary>Windows path-component ceiling. A longer segment cannot be created at all, so
    /// refusing it here turns an obscure IO failure mid-install into a validation error.</summary>
    private const int MaxSegmentLength = 255;

    /// <summary>
    /// Validate an entire bundle's file set. Call this BEFORE any filesystem mutation: a bad entry
    /// must fail the whole install, not leave a partial tree behind.
    /// </summary>
    /// <exception cref="ArgumentException">Any path is unsafe, or the set is internally inconsistent.</exception>
    internal static void ValidateAll(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths is null || relativePaths.Count == 0)
            throw Reject("(none)", "a bundle must declare at least one file");

        var normalized = new List<string>(relativePaths.Count);
        foreach (var path in relativePaths)
            normalized.Add(Validate(path));

        // Case-insensitive duplicates: two entries naming one file on Windows. The second download
        // would overwrite the first, and the SHA check would then fail on whichever lost — an
        // install that can never succeed, reported as corruption.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in normalized)
        {
            if (!seen.Add(path))
                throw Reject(path, "two files in the bundle resolve to the same path");
        }

        // Prefix conflicts: "a" and "a/b" require "a" to be both a file and a directory. Whichever
        // is created first makes the other impossible.
        foreach (var candidate in normalized)
        {
            foreach (var other in normalized)
            {
                if (ReferenceEquals(candidate, other)) continue;
                if (other.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw Reject(candidate, $"it is also used as a directory by '{Sanitize(other)}'");
            }
        }

        // Download-shadow collisions. Each file streams to "<path>.download" before being moved into
        // place, so a bundle declaring both "weights" and "weights.download" has file B's FINAL path
        // equal to file A's TEMP path. The install would then overwrite one payload with the other's
        // partial and could still commit — a manifest member silently missing from a "successful"
        // install. Neither path is individually invalid, so only a set-level check sees it.
        foreach (var candidate in normalized)
        {
            if (seen.Contains(candidate + DownloadSuffix))
                throw Reject(candidate, $"its download temp path collides with '{Sanitize(candidate)}{DownloadSuffix}'");
        }
    }

    /// <summary>The suffix <c>ModelDownloadManager</c> streams to before moving a file into place.
    /// Declared here because the collision it can cause is a PATH-SET property, which is this type's
    /// subject — the manager owns the mechanism, this owns the rule that keeps it unambiguous.</summary>
    internal const string DownloadSuffix = ".download";

    /// <summary>
    /// Validate one bundle-relative path and return it in a normalized form (forward slashes folded
    /// to <see cref="Path.DirectorySeparatorChar"/>). Never returns a path that differs in meaning
    /// from the input — only in separator spelling.
    /// </summary>
    internal static string Validate(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw Reject(relativePath, "it is empty");

        // Rooted, UNC, and the drive-RELATIVE form "C:foo" — which Path.IsPathRooted reports as
        // rooted but which resolves against the process's per-drive current directory, so it is not
        // even deterministic, let alone contained.
        if (Path.IsPathRooted(relativePath))
            throw Reject(relativePath, "it is an absolute or drive-relative path");

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);

        foreach (var segment in segments)
        {
            // Empty segments cover both a leading separator and a doubled one ("a//b"), each of
            // which normalizes away and would let two spellings name one file.
            if (segment.Length == 0)
                throw Reject(relativePath, "it contains an empty path segment");

            if (segment == "." || segment == "..")
                throw Reject(relativePath, $"it contains a '{segment}' segment");

            // ModelNameGuard's rules, per segment — including the measured '~' 8.3-alias and
            // trailing-dot cases. Shared rather than restated: a second copy of this list would
            // drift the day one of them is edited.
            var problem = ModelNameGuard.DescribeSegmentProblem(segment);
            if (problem != null)
                throw Reject(relativePath, problem);

            if (segment.Length > MaxSegmentLength)
                throw Reject(relativePath, $"a path segment exceeds {MaxSegmentLength} characters");

            // Checked on EVERY segment, not just the filename: `.vw-model-complete.json/payload`
            // has the reserved name as a DIRECTORY, so a leaf-only check let it through — the
            // install would create a directory exactly where the manifest belongs and only discover
            // the conflict after every file had been downloaded.
            if (string.Equals(segment, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                throw Reject(relativePath, $"'{ManifestFileName}' is reserved for the install manifest");
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// Canonicalise-and-contain, the <c>MediaPathPolicy</c> rule: the composed path must resolve
    /// inside <paramref name="bundleRoot"/>. Belt to <see cref="Validate"/>'s braces — the lexical
    /// rules should already make escape impossible, and this catches the case they missed.
    ///
    /// <para>Lexical containment cannot see reparse points. That is handled separately and
    /// fail-closed by <c>VerifiedFileAccess.IsDirectoryVerifiedUnder</c>, which kernel-resolves BOTH
    /// sides — an attribute check on the directory being deleted would miss an ANCESTOR junction —
    /// and runs before any enumeration, write or recursive delete. Necessary because leftover
    /// staging outlives a crashed run, so the app did not create every path it later follows.</para>
    /// </summary>
    internal static string ResolveWithin(string bundleRoot, string validatedRelativePath)
    {
        var rootFull = Path.GetFullPath(bundleRoot);
        var combined = Path.GetFullPath(Path.Combine(rootFull, validatedRelativePath));

        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw Reject(validatedRelativePath, "it resolves outside the bundle directory");

        return combined;
    }

    private static string Sanitize(string? value) => UntrustedTextSanitizer.Sanitize(value, 60) ?? "(empty)";

    private static ArgumentException Reject(string? path, string because)
        => new($"Invalid bundle file path: {because}. Received: '{Sanitize(path)}'.");
}
