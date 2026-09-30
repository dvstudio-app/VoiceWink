namespace VoiceWink.Helpers;

/// <summary>
/// The bounded retry the small per-machine state files (<see cref="GpuWarmupMarker"/>,
/// <c>LlamaGpuCheckStore</c>) wrap around a read or a read-modify-write. An on-access scanner
/// briefly holding a file just read or written makes the write-then-move replace throw
/// <see cref="UnauthorizedAccessException"/> (access denied) — measured on the owner's laptop
/// 2026-09-29 at about 1 in 1,000 replaces, and only when the file had been read just before.
/// Unretried, a best-effort write drops silently.
/// </summary>
internal static class TransientFileRetry
{
    /// <summary>How many times one call is attempted. The waits between attempts total 150 ms at
    /// worst and are paid only on a fault — some callers run on the UI thread.</summary>
    internal const int Attempts = 4;

    /// <summary>The production wait before retry <c>n</c> (1-based).</summary>
    internal static readonly Action<int> DefaultBackoff = static attempt => Thread.Sleep(25 * attempt);

    /// <summary>Runs <paramref name="attempt"/> up to <see cref="Attempts"/> times while it fails
    /// with a transient fault, calling <paramref name="backoff"/> between attempts; any other
    /// exception, or the last attempt's, propagates.</summary>
    internal static T Run<T>(Func<T> attempt, Action<int> backoff)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                return attempt();
            }
            catch (Exception ex) when (n < Attempts && IsTransient(ex))
            {
                backoff(n);
            }
        }
    }

    /// <summary>A sharing violation or an access denial can clear on its own; a missing directory
    /// or file cannot, so it fails at once rather than paying the waits.</summary>
    internal static bool IsTransient(Exception ex)
        => ex is UnauthorizedAccessException
           || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException);
}
