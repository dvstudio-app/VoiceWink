using System.Threading;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Helper for running synchronous COM operations on a thread-pool MTA worker
/// under a bounded wait. Designed for the VoiceWink hang-isolation pattern:
/// the calling thread (typically UI STA) gets a guaranteed-prompt return,
/// and a wedged COM call is abandoned without leaking the late result.
///
/// <para><b>Why this exists.</b> Windows COM calls into the audio service
/// (MMDeviceEnumerator, GetDefaultAudioEndpoint, WasapiCapture construction)
/// can block for tens of seconds under heavy system CPU contention (logged
/// 28s and 238s incidents during normal-load Excel heavy recalc / VM running
/// scenarios). A synchronous call to these from the UI thread freezes the
/// entire app for the duration. Wrapping them with this helper gives a fast
/// fail path while still allowing the abandoned worker to complete cleanly
/// in the background.</para>
///
/// <para><b>Abandonment contract.</b> If the bounded wait is abandoned —
/// either by the timeout expiring OR by the caller's CancellationToken
/// cancelling — the worker is allowed to keep running. When it finally
/// completes:</para>
/// <list type="bullet">
/// <item>If <typeparamref name="T"/> implements <see cref="IDisposable"/>,
/// the late result is disposed.</item>
/// <item>If the caller supplied a non-null <paramref name="dependency"/>,
/// it is disposed AFTER the late result. Order matters because the result
/// may borrow native resources from the dependency.</item>
/// <item>Late faults are observed (logged at Information) and not rethrown,
/// preventing unobserved-task exceptions on the finalizer thread.</item>
/// </list>
///
/// <para>On the success path (worker returns before the wait abandons), the
/// caller takes ownership of both T and any supplied dependency — this helper
/// does NOT dispose either in that case.</para>
/// </summary>
internal static class BoundedComCall
{
    // A property, never a cached field: the root logger is REBUILT at runtime (the Log Viewer's
    // Clear, and since the launch defaults audit the crash-consent sink attach), and a contextual
    // logger captured before a rebuild forwards to the disposed root, so every line after it is
    // silently dropped -- this was the one static-readonly logger in the app (2026-09-13).
    private static ILogger Logger => Log.ForContext(typeof(BoundedComCall));

    /// <summary>
    /// Execute <paramref name="factory"/> on a thread-pool worker (MTA),
    /// bounded by <paramref name="timeout"/> or <paramref name="ct"/>.
    /// </summary>
    /// <typeparam name="T">Result type. If <see cref="IDisposable"/>, late
    /// results on the abandonment path are disposed by this helper.</typeparam>
    /// <param name="factory">The synchronous COM call to invoke. Must be
    /// self-contained: it runs on an arbitrary thread-pool worker after a
    /// possible long delay, so it cannot capture mutable non-thread-safe
    /// state from the caller.</param>
    /// <param name="timeout">Maximum time to wait before abandoning the
    /// wait and returning null. Pick a value that's tight enough to keep
    /// the UI responsive but generous enough that legitimate-but-slow COM
    /// calls don't fail under normal load.</param>
    /// <param name="operationLabel">Short identifier for log lines (e.g.
    /// "recording-GetCurrentDevice"). Used to distinguish abandonments in
    /// log search.</param>
    /// <param name="dependency">Optional resource borrowed by the factory.
    /// If the wait is abandoned, this is disposed alongside the late result.
    /// On success, the caller takes ownership and this helper does NOT
    /// dispose it.</param>
    /// <param name="ct">Caller cancellation. Cancellation abandons the wait
    /// (same cleanup path as timeout) and rethrows
    /// <see cref="OperationCanceledException"/> to the caller.</param>
    /// <returns>The factory's result on success; <c>null</c> on timeout.
    /// Throws <see cref="OperationCanceledException"/> on cancellation.</returns>
    public static async Task<T?> RunBoundedAsync<T>(
        Func<T?> factory,
        TimeSpan timeout,
        string operationLabel,
        IDisposable? dependency = null,
        CancellationToken ct = default)
        where T : class
    {
        // Intentionally do NOT pass ct into Task.Run. Cancelling the token
        // should abandon the wait, not the worker — cancelling a worker
        // mid-COM-call wouldn't actually interrupt the COM operation, and
        // omitting ct here keeps the worker independent of the caller's
        // lifetime so the late-cleanup continuation can run.
        var task = Task.Run(factory);

        try
        {
            return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ArrangeLateCleanup(task, operationLabel, dependency,
                abandonReason: "timeout", budget: timeout);
            return null;
        }
        catch (OperationCanceledException)
        {
            ArrangeLateCleanup(task, operationLabel, dependency,
                abandonReason: "cancellation", budget: timeout);
            throw;
        }
    }

    /// <summary>
    /// Attach a continuation that disposes the late-arriving result + dependency
    /// when the abandoned worker eventually completes, and logs late faults
    /// so they don't surface as unobserved-task exceptions.
    /// </summary>
    private static void ArrangeLateCleanup<T>(
        Task<T?> task,
        string operationLabel,
        IDisposable? dependency,
        string abandonReason,
        TimeSpan budget)
        where T : class
    {
        Logger.Warning(
            "{Operation} abandoned by {Reason} at {Ms}ms — arranging late-result cleanup",
            operationLabel, abandonReason, (int)budget.TotalMilliseconds);

        _ = task.ContinueWith(static (t, stateObj) =>
        {
            var state = (CleanupState)stateObj!;
            try
            {
                if (t.IsFaulted)
                {
                    Log.ForContext(typeof(BoundedComCall)).Information(
                        "{Operation} late fault after {Reason}: {ExType}",
                        state.OperationLabel, state.AbandonReason,
                        t.Exception?.InnerException?.GetType().Name ?? "Unknown");
                }
                else if (t.IsCompletedSuccessfully && t.Result is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex)
                    {
                        Log.ForContext(typeof(BoundedComCall)).Information(
                            "{Operation} late-result dispose threw: {ExType}",
                            state.OperationLabel, ex.GetType().Name);
                    }
                }
            }
            finally
            {
                // Dispose dependency AFTER the result. The result may borrow
                // native resources from the dependency (e.g. WasapiCapture
                // borrows from MMDevice); disposing the dependency first
                // would crash the result's Dispose().
                if (state.Dependency != null)
                {
                    try { state.Dependency.Dispose(); }
                    catch (Exception ex)
                    {
                        Log.ForContext(typeof(BoundedComCall)).Information(
                            "{Operation} late-dependency dispose threw: {ExType}",
                            state.OperationLabel, ex.GetType().Name);
                    }
                }
            }
        }, new CleanupState(operationLabel, abandonReason, dependency), TaskScheduler.Default);
    }

    /// <summary>
    /// State passed to the cleanup continuation. Record type to keep the
    /// static lambda closure-free.
    /// </summary>
    private sealed record CleanupState(string OperationLabel, string AbandonReason, IDisposable? Dependency);
}
