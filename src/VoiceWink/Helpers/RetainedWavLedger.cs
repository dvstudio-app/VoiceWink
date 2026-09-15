using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Tracks every failure-retained recording WAV whose deletion has not been CONFIRMED
/// (REL-12). The retained WAV moves between owners (armed retry slot → in-flight retry
/// pipeline → fire-and-forget supersede delete), and any of those can be interrupted by
/// process exit — a single "current path" field provably leaks (retain A → async-delete A
/// starts → retain B overwrites → exit drains only B; Codex round-3). The ledger is the
/// one place that always knows what is still on disk:
/// <list type="bullet">
/// <item><see cref="Track"/> at retain time (idempotent);</item>
/// <item><see cref="ConfirmDeleted"/> only after a delete actually succeeded;</item>
/// <item><see cref="DrainBestEffort"/> at shutdown (normal <c>App.Cleanup</c> AND the
/// forced-exit durable-state flush) — synchronous, non-UI (no observables, no
/// DispatcherTimer), so it is safe off the UI thread. A file still open (in-flight
/// retry upload) or already gone is skipped; the 7-day recordings sweep remains the
/// final backstop.</item>
/// </list>
/// Registered as a DI singleton. Steady-state size 0–1 (paths enter only on
/// transcription failure and leave on confirmed deletion).
/// </summary>
public sealed class RetainedWavLedger
{
    private static ILogger Logger => Log.ForContext<RetainedWavLedger>();

    private readonly object _lock = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record a retained WAV that now needs eventual deletion. Idempotent.
    /// <para>
    /// Logs the retention (2026-08-15). Every arm-for-retry site calls this, so it is the ONE
    /// place that can say a failed recording was kept and name it — and before this the failure
    /// path was the only disposition that named no file at all. `Recording retained for
    /// debugging` fires from the end-of-life disposition, i.e. only once a run COMPLETES, so a
    /// transcription that failed and armed a retry left the log silent about its WAV: the
    /// 2026-08-15 empty-Parakeet incident produced two failures whose audio reached no log line
    /// until an unrelated later retry happened to succeed. The one class of recording most worth
    /// diagnosing was the one class whose location never appeared.
    /// </para>
    /// <para>
    /// Emitted only on a GENUINE first add, which is what keeps it one line per retention
    /// EPISODE rather than per arm: a re-arm over an already-tracked path (the "Retry cancelled"
    /// re-arm, or a retry run that fails again without the path ever leaving the ledger) is a
    /// no-op here and must not re-announce a file whose location has not changed. A path that
    /// legitimately leaves via <see cref="ConfirmDeleted"/> and is later retained again IS a new
    /// episode and logs again. Rendered OUTSIDE the lock — a synchronous sink write must not run
    /// under the lock every other member contends on.
    /// </para>
    /// </summary>
    public void Track(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool added;
        lock (_lock) { added = _paths.Add(path); }
        if (added)
            Logger.Information("Recording retained for retry: {Name}", Path.GetFileName(path));
    }

    /// <summary>
    /// Forget a path whose deletion actually succeeded (or provably no longer exists).
    /// <para>
    /// Returns whether this call actually removed a TRACKED path — i.e. whether it closed a
    /// retention <see cref="Track"/> had announced. Callers that dispose a WAV silently use it to
    /// log exactly the recordings that were announced and no others: an ordinary dictation's WAV
    /// was never tracked, returns false, and stays unlogged. Without this the asymmetry is worse
    /// than the original silence — once most dispositions name their file, a retention with NO
    /// closing line reads as "still on disk in Recordings\", and the most common ending for a
    /// retained WAV is the retry SUCCEEDING and the file being deleted. Support would send the
    /// user looking for a file that is gone. Deliberately mirrors <see cref="Track"/>'s
    /// added-only rule so announce and close cannot drift.
    /// </para>
    /// </summary>
    public bool ConfirmDeleted(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (_lock) { return _paths.Remove(path); }
    }

    /// <summary>
    /// Close an announced retention and NAME it — the single authority for the closing half of
    /// the announce/close protocol (Codex diff round 1). Logs if and only if this call actually
    /// removed the path, so the caller that WINS the removal is the one that reports it.
    /// <para>
    /// Gating the line on winning is what makes the protocol observable rather than merely
    /// balanced. Set membership was already correct — <see cref="Track"/> adds once,
    /// <see cref="ConfirmDeleted"/> removes once — but a caller that logged independently could
    /// still report work it did not do: superseding a retry schedules a fire-and-forget delete,
    /// quitting during the new recording runs <see cref="DrainBestEffort"/>, the drain disposes
    /// of the file, and the task's own delete then succeeds as a missing-file no-op and announces
    /// a deletion that already happened elsewhere. Exactly one caller can win the removal, so
    /// exactly one line is emitted.
    /// </para>
    /// <para>
    /// <paramref name="outcome"/> states what actually happened to the bytes, never what was
    /// configured. Callers pass an "already absent" outcome too: a retention that closes because
    /// the file vanished underneath us is still a close, and reporting it beats the silence that
    /// made the failure path undiagnosable in the first place.
    /// </para>
    /// </summary>
    public bool TryCloseRetention(string path, string outcome)
    {
        if (!ConfirmDeleted(path)) return false;
        Logger.Information("Retained recording closed ({Outcome}): {Name}", outcome, Path.GetFileName(path));
        return true;
    }

    /// <summary>Snapshot of currently tracked paths (tests + diagnostics).</summary>
    public IReadOnlyCollection<string> Snapshot()
    {
        lock (_lock) { return _paths.ToArray(); }
    }

    /// <summary>
    /// Best-effort synchronous disposition of every tracked path at shutdown.
    /// Default: delete (REL-12's guarantee). REL-17 (diff review round 2): when the
    /// keep-recordings debug setting is on, tracked WAVs are MOVED into
    /// <paramref name="debugDir"/> instead — the UI promises each recording is kept for
    /// up to 7 days, and the exit drain must not silently break that. Handled (or
    /// already-missing) paths are confirmed out of the ledger; a file that cannot be
    /// deleted/moved (in use) stays tracked and falls to the recordings sweep. Returns
    /// the number of files actually removed from their original location.
    /// </summary>
    public int DrainBestEffort(bool keepForDebug = false, string? debugDir = null)
    {
        string[] pending;
        lock (_lock) { pending = _paths.ToArray(); }

        // Write-side junction guard (diff round 5): a junctioned Debug dir would move
        // WAVs outside the app root, where the sweeps and erasure refuse to follow —
        // retention falls back to delete (REL-12's original guarantee).
        string? moveDir = null;
        if (keepForDebug && !string.IsNullOrEmpty(debugDir))
        {
            try
            {
                Directory.CreateDirectory(debugDir);
                if (!VerifiedFileAccess.IsReparsePointOrUnreadable(debugDir))
                    moveDir = debugDir;
            }
            catch { /* unverifiable — delete fallback */ }
        }

        var drained = 0;
        foreach (var path in pending)
        {
            try
            {
                string outcome;
                if (File.Exists(path))
                {
                    if (moveDir != null)
                    {
                        File.Move(path, Path.Combine(moveDir, Path.GetFileName(path)), overwrite: true);
                        outcome = "kept for debugging at shutdown";
                    }
                    else
                    {
                        File.Delete(path);
                        outcome = "deleted at shutdown";
                    }
                    drained++;
                }
                else
                {
                    outcome = "already absent at shutdown";
                }
                // NAME each closed retention, not just a count (Codex diff round 1). The
                // aggregate below survives as a summary, but it cannot answer the question that
                // matters: with two tracked WAVs and one failing on a sharing violation,
                // "drained 1" says nothing about WHICH file was disposed of and which is still
                // sitting in Recordings\ — and each of them was announced by name on the way in.
                TryCloseRetention(path, outcome); // gone either way
            }
            catch (Exception ex)
            {
                // In use / access denied — leave tracked; the 7-day sweep is the backstop.
                Logger.Debug(ex, "RetainedWavLedger drain could not handle {Path}", path);
            }
        }

        // Report what HAPPENED, not what was configured — the same rule the recording-disposition
        // sites follow. `moveDir` is null when the setting is off, but ALSO when CreateDirectory
        // threw (an AV/EDR hold on %LOCALAPPDATA% during shutdown, `Debug` existing as a file) or
        // when the junction guard refused the directory. Keying the line on `keepForDebug` claimed
        // "kept for debugging" while every WAV had in fact been DELETED — sending support to look
        // in Recordings\Debug for a file that was never written there, on precisely the run where
        // a keep-recordings user silently lost the 7-day promise.
        if (drained > 0)
            Logger.Information("RetainedWavLedger drained {Count} retained recording(s) at shutdown ({Mode})",
                drained, moveDir != null ? "kept for debugging" : "deleted");
        return drained;
    }
}
