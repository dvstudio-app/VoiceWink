using Serilog;
using VoiceWink.Services.Audio;

namespace VoiceWink.Helpers;

/// <summary>
/// Disposes of a ledger-tracked recording nobody needs any more (REL-12 superseded retry, REL-20
/// last recording): moved to <c>Recordings\Debug</c> when keep-recordings is on, else deleted with
/// its REL-23 raw sibling. A failed attempt leaves the path PENDING in the ledger, and every later
/// call (plus the hourly cleanup pass) retries it, so a briefly locked file is not kept until quit.
/// </summary>
public static class RetainedWavDisposal
{
    private static ILogger Logger => Log.ForContext(typeof(RetainedWavDisposal));

    /// <summary>Fire-and-forget production entry: the recording-start hotkey path must not pay file IO.</summary>
    public static void DisposeInBackground(string path, RetainedWavLedger ledger, bool keepForDebug, string reason)
    {
        ledger.MarkPendingDisposal(path);
        _ = Task.Run(() =>
        {
            var moveDir = ResolveMoveDir(keepForDebug, AppPaths.RecordingsDebugDir);
            DisposeNow(path, ledger, moveDir, AppPaths.RecordingsDebugDir, reason);
            RetryPending(ledger, moveDir, AppPaths.RecordingsDebugDir);
        });
    }

    /// <summary>Retries every pending disposal under <paramref name="recordingsRoot"/>; the hourly
    /// cleanup pass calls this (REL-20). Touches no folder while nothing is pending.</summary>
    public static int RetryPending(RetainedWavLedger ledger, bool keepForDebug, string recordingsRoot)
    {
        if (ledger.PendingSnapshot().Count == 0) return 0;
        var debugDir = Path.Combine(recordingsRoot, "Debug");
        return RetryPending(ledger, ResolveMoveDir(keepForDebug, debugDir), debugDir);
    }

    internal static int RetryPending(RetainedWavLedger ledger, string? moveDir, string rawSiblingDir)
    {
        var closed = 0;
        foreach (var path in ledger.PendingSnapshot())
            if (DisposeNow(path, ledger, moveDir, rawSiblingDir, "retried")) closed++;
        return closed;
    }

    /// <summary>One attempt. <paramref name="moveDir"/> is a verified Debug dir or null (= delete).
    /// Returns whether the bytes left their place (or were already gone).</summary>
    internal static bool DisposeNow(string path, RetainedWavLedger ledger, string? moveDir, string rawSiblingDir, string reason)
    {
        try
        {
            // What THIS call did to the bytes — reported only if it also wins the ledger removal,
            // because the shutdown drain can dispose of the same file concurrently (Codex diff r1).
            var outcome = $"already absent ({reason})";
            if (File.Exists(path))
            {
                if (moveDir != null && !ledger.MustDelete(path))
                {
                    File.Move(path, Path.Combine(moveDir, Path.GetFileName(path)), overwrite: true);
                    outcome = $"kept for debugging ({reason})";
                }
                else
                {
                    File.Delete(path);
                    // REL-23: a toggle-off delete promises to keep nothing, the pre-gain copy included.
                    RecordingAudioPreparation.DeleteRawSiblingFor(path, rawSiblingDir);
                    outcome = $"deleted ({reason})";
                }
            }
            ledger.TryCloseRetention(path, outcome);
            return true;
        }
        catch (Exception ex) when (!File.Exists(path))
        {
            // A concurrent disposal (another retry task, the quit drain) moved or deleted it first.
            Logger.Debug(ex, "Retained recording already gone ({Reason}): {Name}", reason, Path.GetFileName(path));
            ledger.TryCloseRetention(path, $"already absent ({reason})");
            return true;
        }
        catch (Exception ex)
        {
            ledger.MarkPendingDisposal(path);
            Logger.Warning(ex, "Retained recording could not be removed yet ({Reason}); will retry: {Name}",
                reason, Path.GetFileName(path));
            return false;
        }
    }

    // Write-side junction guard (REL-17 diff round 5): an unverifiable Debug dir ⇒ delete.
    private static string? ResolveMoveDir(bool keepForDebug, string dir)
    {
        if (!keepForDebug) return null;
        try
        {
            Directory.CreateDirectory(dir);
            return VerifiedFileAccess.IsReparsePointOrUnreadable(dir) ? null : dir;
        }
        catch { return null; }
    }
}
