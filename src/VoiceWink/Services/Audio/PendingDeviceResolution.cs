using System.Diagnostics;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Audio;

/// <summary>
/// A recording-device resolution started EARLY, so it overlaps the recording pre-flight
/// (App Mode detect, language/model resolution) instead of running after it. Owns the resolved
/// <c>MMDevice</c> until a caller successfully takes it.
///
/// <para><b>Why this is not a bare <c>Task</c> field.</b> A resolution nobody awaits still
/// produces a COM device object. <c>MainViewModel.StartRecordingAsync</c> has five
/// supersession/cancel returns plus two superseded catch paths between the hoist and the
/// consumption point, and on every one of them the device must still be released — a bare task
/// would leak an <c>MMDevice</c> per abandoned start.</para>
///
/// <para><b>Ownership contract.</b> This object owns the resolved device until
/// <see cref="TryTakeAsync"/> RETURNS A NON-NULL RESULT. After that the caller owns it — in
/// practice it is handed to <c>AudioRecorderService.StartRecordingAsync</c>, which takes
/// unconditional ownership from entry — and this object never touches it again. A null return
/// (policy refusal, fault, or cancellation) leaves ownership HERE, so <see cref="Dispose"/> is
/// still responsible for it. Do not add a caller-side dispose: that is the double-dispose this
/// note exists to prevent.</para>
///
/// <para><b>Dispose is non-blocking on every path</b>, including when resolution has already
/// completed. It attaches a continuation rather than awaiting: a superseded start disposes this
/// inside a <c>finally</c> on the UI thread, and a synchronous wait there would block the UI
/// behind a wedged COM call — the exact hang <c>BoundedComCall</c> exists to prevent.</para>
///
/// <para>The resolver and the disposal projection are both injected because
/// <see cref="ResolvedRecordingDevice"/> hard-types a non-disposable <c>MMDevice</c>, so the
/// disposal matrix cannot otherwise be tested without live COM (Codex plan review).</para>
/// </summary>
internal sealed class PendingDeviceResolution : IDisposable
{
    private static ILogger Logger => Log.ForContext<PendingDeviceResolution>();

    private readonly Task<ResolvedRecordingDevice> _resolution;
    private readonly Action<ResolvedRecordingDevice> _disposeResolved;
    private readonly Func<long> _timestampSource;
    private readonly long _frequency;
    private readonly long _startedTimestamp;

    // Single ownership authority. Two independent flags could not express this: closing the
    // "Dispose lands between the claim and the re-check" leak with an inline dispose opened its
    // mirror — Dispose's continuation releasing first (no claim seen), then the taker claiming,
    // seeing _disposed, and releasing the SAME device again (Kimi diff review). One CAS decides,
    // so exactly one party ever calls _disposeResolved.
    private const int OwnershipAvailable = 0;
    private const int OwnershipTaken = 1;
    private const int OwnershipReleased = 2;

    private int _ownership = OwnershipAvailable;
    private int _disposeRequested;

    public PendingDeviceResolution(
        Func<CancellationToken, Task<ResolvedRecordingDevice>> resolver,
        CancellationToken ct)
        : this(resolver, ct, r => r.Device?.Dispose(), Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    internal PendingDeviceResolution(
        Func<CancellationToken, Task<ResolvedRecordingDevice>> resolver,
        CancellationToken ct,
        Action<ResolvedRecordingDevice> disposeResolved,
        Func<long> timestampSource,
        long frequency)
    {
        _disposeResolved = disposeResolved;
        _timestampSource = timestampSource;
        _frequency = frequency;
        _startedTimestamp = timestampSource();
        // Started here, deliberately: the caller's next statement is the pre-flight it overlaps.
        _resolution = resolver(ct);
    }

    /// <summary>Wall time since the resolution was started.</summary>
    public TimeSpan Age =>
        TimeSpan.FromMilliseconds((_timestampSource() - _startedTimestamp) * 1000.0 / _frequency);

    /// <summary>
    /// Take the hoisted resolution if it is still trustworthy, else null so the caller resolves
    /// fresh (today's behaviour, today's cost).
    ///
    /// <para>Age is checked BEFORE awaiting and AGAIN after — the await itself can push the
    /// resolution past the bound, and the check that matters is the one immediately before
    /// ownership transfers (Codex final check).</para>
    /// </summary>
    /// <returns>The resolution, with ownership transferred, or null.</returns>
    public async Task<ResolvedRecordingDevice?> TryTakeAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposeRequested) != 0 ||
            Volatile.Read(ref _ownership) != OwnershipAvailable)
            return null;

        if (!HoistedResolutionPolicy.ShouldUseHoisted(Age))
            return null;

        ResolvedRecordingDevice resolved;
        try
        {
            resolved = await _resolution.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller-cancel is real and must propagate; ownership stays here for Dispose.
            throw;
        }
        catch (Exception ex)
        {
            // A faulted hoist must not fail the recording — fall back to a fresh resolve, which
            // surfaces its own error through the existing path. Dispose observes the fault.
            Logger.Debug(ex, "Hoisted device resolution faulted — falling back to fresh resolution");
            return null;
        }

        // Re-check after the await, then claim atomically so a second caller cannot also take it.
        if (!HoistedResolutionPolicy.ShouldUseHoisted(Age))
            return null;

        // The single hand-off. Winning the CAS IS ownership: a concurrent Dispose continuation
        // that already claimed the device will release it and this returns null, and if we win,
        // that continuation skips. No path releases a device it did not claim, and no device
        // ends up claimed by nobody.
        if (Interlocked.CompareExchange(ref _ownership, OwnershipTaken, OwnershipAvailable)
            != OwnershipAvailable)
            return null;

        return resolved;
    }

    /// <summary>
    /// Release the resolution if it was never taken. Non-blocking on every path: the device is
    /// disposed on a thread-pool continuation whenever the resolution settles, and a faulted
    /// resolution is observed so it cannot surface as an unobserved task exception. Idempotent,
    /// and safe to call after a successful take (it then owns nothing).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        // No ContinueWith flags: the continuation must NOT run inline on the disposing (UI)
        // thread, and must run even when the resolution is already complete.
        _ = _resolution.ContinueWith(
            static (task, state) =>
            {
                var self = (PendingDeviceResolution)state!;

                if (task.IsFaulted)
                {
                    // Touching Exception marks the fault observed.
                    Logger.Debug(task.Exception, "Abandoned device resolution faulted");
                    return;
                }

                if (task.IsCanceled)
                    return;

                // Same CAS, other side: claim before releasing, so a taker that already won
                // keeps sole ownership and this never double-releases.
                if (Interlocked.CompareExchange(
                        ref self._ownership, OwnershipReleased, OwnershipAvailable)
                    != OwnershipAvailable)
                    return;

                try { self._disposeResolved(task.Result); }
                catch (Exception ex) { Logger.Debug(ex, "Disposing abandoned capture device failed"); }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
