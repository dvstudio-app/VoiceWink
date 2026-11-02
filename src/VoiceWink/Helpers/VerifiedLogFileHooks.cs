using System.Text;
using Serilog;
using Serilog.Sinks.File;

namespace VoiceWink.Helpers;

/// <summary>REL-19: Serilog's file sink opens and retention-deletes only files that resolve directly in <c>Logs</c>.</summary>
// shortcut: a junction planted mid-session gets one empty file (Serilog creates it before the hook runs) and is reported at the next logger rebuild, upgrade if either is ever seen.
internal sealed class VerifiedLogFileHooks : FileLifecycleHooks
{
    private static string? _pendingRefusal;

    /// <summary>Whether to add the file sink at all: false when <paramref name="logDir"/> itself is redirected, which also spares the empty file Serilog's open would create there.</summary>
    internal static bool AllowsFileSink(string logDir)
    {
        if (VerifiedFileAccess.DirectoryResolvesDirectly(logDir)) return true;
        _pendingRefusal = "the Logs folder is redirected or unreadable";
        return false;
    }

    /// <summary>Logs a refusal recorded since the last call; called once a rebuilt logger is live, because nothing may log from inside the sink.</summary>
    internal static void ReportRefusal()
    {
        var reason = Interlocked.Exchange(ref _pendingRefusal, null);
        if (reason != null)
            Log.ForContext<VerifiedLogFileHooks>().Warning("Application log file not written: {Reason}", reason);
    }

    // shortcut: path-verified, not handle-verified (Serilog hands in a counting wrapper, not the FileStream); a swap between Serilog's open and this check is a same-user race. Upgrade if Serilog exposes the handle.
    // A non-sharing IOException makes Serilog's OpenFile rethrow before retention and retry at the next roll; it disposes only a returned stream.
    public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
    {
        if (IsContained(path)) return underlyingStream;
        underlyingStream.Dispose();
        _pendingRefusal = "a log file resolves outside the Logs folder";
        throw new IOException("REL-19: log file is not inside the Logs folder");
    }

    public override void OnFileDeleting(string path)
    {
        if (!IsContained(path))
            throw new IOException("REL-19: refusing to delete a log file outside the Logs folder");
    }

    private static bool IsContained(string path)
    {
        var dir = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(dir)
            && VerifiedFileAccess.DirectoryResolvesDirectly(dir)
            && VerifiedFileAccess.PathResolvesDirectlyIn(path, dir);
    }
}
