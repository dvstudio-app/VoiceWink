namespace VoiceWink.Helpers;

/// <summary>
/// Containment guard for file operations on persisted media paths
/// (<c>TranscriptionRecord.AudioFilePath</c> / <c>ImageFilePath</c>). Those columns are
/// free-form strings that could be corrupt, legacy, or imported from another machine — so
/// before any delete or export touches the file, callers confirm it canonicalizes to a real
/// location <i>inside</i> the app's own data directory. Anything null/empty/malformed or
/// outside the allowed directory is rejected, so a bad DB value can never cause VoiceWink to
/// delete or export an arbitrary file on disk.
/// </summary>
public static class MediaPathPolicy
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="persistedPath"/> canonicalizes to a path inside
    /// <paramref name="allowedDir"/>, emitting the canonical absolute path via
    /// <paramref name="fullPath"/>. Null/empty/malformed/outside paths return <c>false</c>.
    /// </summary>
    public static bool TryResolveWithin(string? persistedPath, string allowedDir, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(persistedPath) || string.IsNullOrWhiteSpace(allowedDir))
            return false;

        try
        {
            var candidate = Path.GetFullPath(persistedPath);
            var root = Path.GetFullPath(allowedDir);

            // Append a trailing separator so "…\VoiceWinkEvil" is not treated as inside "…\VoiceWink".
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                fullPath = candidate;
                return true;
            }
        }
        catch
        {
            // Illegal chars, path too long, etc. — treat as outside the data directory.
        }

        return false;
    }
}
