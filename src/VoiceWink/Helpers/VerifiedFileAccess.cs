namespace VoiceWink.Helpers;

/// <summary>
/// The SHARED kernel-verified file-access gate (REL-17, extracted from
/// <see cref="ReferenceImagePolicy"/> so the support bundle's recording reads, the GDPR
/// export's Debug-folder reads, and the destructive retention sweeps all use ONE
/// implementation): opens the file and verifies the KERNEL-RESOLVED final path of the
/// opened handle lands inside the kernel-resolved final path of the given root before a
/// single byte is read. Lexical containment (<see cref="MediaPathPolicy.TryResolveWithin"/>)
/// can't see reparse points — a junction/symlink planted inside an app data folder could
/// otherwise turn a crafted path into a read of a file OUTSIDE the app's own data (Codex
/// diff review 2026-07-11; re-confirmed for REL-17 in plan round 4). Resolving BOTH sides
/// keeps a legitimately-junctioned profile (e.g. relocated %LOCALAPPDATA%) working: both
/// resolve through the same links. Read from the RETURNED stream — no re-open, no TOCTOU
/// window. Null on any containment or IO failure (fail closed).
/// </summary>
public static class VerifiedFileAccess
{
    /// <summary>
    /// Open <paramref name="path"/> for read and verify handle-resolved containment under
    /// <paramref name="rootDir"/>. <paramref name="verifiedPath"/> carries the physical
    /// resolved target (empty on failure) for consumers that must hand a PATH onward —
    /// they act on what was verified, never the junction-bearing input string.
    /// <paramref name="asyncIo"/> opens the handle with
    /// <see cref="FileOptions.Asynchronous"/> so ReadAsync is genuinely interruptible.
    /// </summary>
    public static FileStream? TryOpenVerifiedUnder(
        string? path, string rootDir, out string verifiedPath, bool asyncIo = false)
    {
        verifiedPath = string.Empty;
        if (!MediaPathPolicy.TryResolveWithin(path, rootDir, out var fullPath))
            return null;

        FileStream? stream = null;
        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, asyncIo ? FileOptions.Asynchronous : FileOptions.None);

            var finalFile = TryGetFinalPath(stream.SafeFileHandle);
            var finalDir = TryGetFinalDirectoryPath(rootDir);
            if (finalFile == null || finalDir == null)
            {
                stream.Dispose();
                return null;
            }

            var dirWithSep = finalDir.EndsWith(Path.DirectorySeparatorChar)
                ? finalDir
                : finalDir + Path.DirectorySeparatorChar;
            if (!finalFile.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                return null;
            }

            verifiedPath = finalFile;
            return stream;
        }
        catch
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Delete-side guard for the retention sweeps (REL-17): true when the file's
    /// kernel-resolved final path lands under <paramref name="rootDir"/>. Opens a
    /// transient handle for resolution only.
    /// </summary>
    public static bool IsVerifiedUnder(string path, string rootDir)
    {
        using var stream = TryOpenVerifiedUnder(path, rootDir, out _);
        return stream != null;
    }

    /// <summary>
    /// True when <paramref name="dir"/> kernel-resolves to a location inside
    /// <paramref name="rootDir"/> — the DIRECTORY analogue of
    /// <see cref="TryOpenVerifiedUnder"/>, for destructive callers that need containment before
    /// they enumerate or delete a tree (TRN-1 step 2).
    ///
    /// <para><b>Why the attribute check on the leaf is not enough.</b>
    /// <see cref="IsReparsePointOrUnreadable"/> answers "is THIS directory a junction", which
    /// misses an ANCESTOR junction: if <c>Models\.staging</c> is itself a junction pointing
    /// outside the app's data, then <c>Models\.staging\some-model</c> is an ordinary directory
    /// that passes the leaf check while living somewhere else entirely. Resolving both sides
    /// through the kernel closes that, and — as in <see cref="TryOpenVerifiedUnder"/> — keeps a
    /// legitimately relocated <c>%LOCALAPPDATA%</c> working, because both sides resolve through
    /// the same links.</para>
    ///
    /// <para>Fail-closed: a missing directory, an unreadable handle, or any resolution failure
    /// returns false, so a destructive caller refuses rather than guesses. A caller that must
    /// tolerate absence should test for it separately — absence is not containment.</para>
    /// </summary>
    public static bool IsDirectoryVerifiedUnder(string dir, string rootDir)
    {
        try
        {
            var finalDir = TryGetFinalDirectoryPath(dir);
            var finalRoot = TryGetFinalDirectoryPath(rootDir);
            if (finalDir == null || finalRoot == null) return false;

            var rootWithSep = finalRoot.EndsWith(Path.DirectorySeparatorChar)
                ? finalRoot
                : finalRoot + Path.DirectorySeparatorChar;

            return finalDir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="dir"/> itself carries the reparse-point attribute — a
    /// destructive sweep must not walk INTO a junctioned directory (deletes would land
    /// outside the app's data). Missing directory or attribute-read failure returns true
    /// (fail closed for destructive callers: skip the sweep rather than risk it).
    /// </summary>
    public static bool IsReparsePointOrUnreadable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return true;
            return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    internal static string? TryGetFinalPath(global::Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new global::System.Text.StringBuilder(1024);
        var length = NativeInterop.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer = new global::System.Text.StringBuilder((int)length + 1);
            length = NativeInterop.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length > buffer.Capacity)
            return null;

        var s = buffer.ToString();
        // Strip the \\?\ device prefix the API returns (\\?\UNC\server\share → \\server\share).
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
            return s[4..];
        return s;
    }

    internal static string? TryGetFinalDirectoryPath(string dir)
    {
        // FileStream can't open directories; CreateFileW + FILE_FLAG_BACKUP_SEMANTICS
        // is the documented way to get a directory handle for path resolution.
        using var handle = NativeInterop.CreateFileW(
            dir,
            NativeInterop.FILE_READ_ATTRIBUTES,
            NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE | NativeInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeInterop.OPEN_EXISTING,
            NativeInterop.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        return TryGetFinalPath(handle);
    }
}
