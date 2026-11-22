using Serilog;

namespace VoiceWink.Helpers;

/// <summary>TRN-103: names the stop-pipeline stage a recording is waiting on once that wait passes <see cref="SlowAfter"/>; silent otherwise.</summary>
internal static class StopStageWatch
{
    /// <summary>TRN-103: long enough that a healthy stop never logs, short enough that a stuck one names its stage.</summary>
    internal static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);

    private static ILogger Logger => Log.ForContext(typeof(StopStageWatch));

    internal static Task RunAsync(string stage, Func<Task> body)
        => RunAsync(stage, async () => { await body(); return true; });

    internal static Task<T> RunAsync<T>(string stage, Func<Task<T>> body)
        => RunAsync(stage, body, ct => Task.Delay(SlowAfter, ct), TimeProvider.System);

    /// <summary>Test seam: <paramref name="slowSignal"/> completes when the stage counts as slow and must honour its token; <paramref name="clock"/> times it.</summary>
    internal static async Task<T> RunAsync<T>(
        string stage, Func<Task<T>> body, Func<CancellationToken, Task> slowSignal, TimeProvider clock)
    {
        var started = clock.GetTimestamp();
        var reportedSlow = 0;
        using var finished = new CancellationTokenSource();
        var watcher = WatchAsync();
        var outcome = "completed";
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch
        {
            outcome = "failed";
            throw;
        }
        finally
        {
            finished.Cancel();
            await watcher.ConfigureAwait(false);
            if (Volatile.Read(ref reportedSlow) == 1)
                Logger.Information("Stop pipeline: {Stage:l} finished after {ElapsedMs} ms ({Outcome:l})",
                    stage, ElapsedMs(), outcome);
        }

        long ElapsedMs() => (long)clock.GetElapsedTime(started).TotalMilliseconds;

        // TRN-103: resumes off the caller's context so the warning still fires when the UI dispatcher is starved.
        async Task WatchAsync()
        {
            try
            {
                await slowSignal(finished.Token).ConfigureAwait(false);
                if (finished.IsCancellationRequested) return;
                Logger.Warning("Stop pipeline: still waiting on {Stage:l} after {ElapsedMs} ms", stage, ElapsedMs());
                Volatile.Write(ref reportedSlow, 1);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                try { Logger.Debug(ex, "Stop pipeline stage watch failed for {Stage}", stage); } catch { }
            }
        }
    }
}
