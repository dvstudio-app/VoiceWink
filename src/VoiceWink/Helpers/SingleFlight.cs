namespace VoiceWink.Helpers;

/// <summary>
/// A tiny compare-and-swap lease that admits exactly one holder at a time. The winner of
/// <see cref="TryAcquire"/> gets an <see cref="IDisposable"/>; every concurrent caller gets
/// <c>null</c> until that lease is disposed. Used to make an operation single-flight when it
/// is reachable from several unsynchronized entry points (C3, 2026-07-14: three of four
/// recording-stop call sites bypass the RelayCommand's built-in concurrency guard, so two
/// stops could both enter <c>StopAndTranscribeAsync</c> and the second disposed the first's
/// live CancellationTokenSource).
/// </summary>
public sealed class SingleFlight
{
    // 0 = free, 1 = held. Interlocked so acquisition is atomic across threads.
    private int _state;

    /// <summary>
    /// Atomically acquire the lease. Returns a release handle to the winner, or <c>null</c>
    /// if the lease is already held. Dispose the handle (a <c>using</c> is ideal) to release;
    /// disposing more than once is safe and releases only once.
    /// </summary>
    public IDisposable? TryAcquire()
    {
        return global::System.Threading.Interlocked.CompareExchange(ref _state, 1, 0) == 0
            ? new Lease(this)
            : null;
    }

    /// <summary>True while a lease is outstanding.</summary>
    public bool IsHeld => global::System.Threading.Volatile.Read(ref _state) == 1;

    private void Release() => global::System.Threading.Interlocked.Exchange(ref _state, 0);

    private sealed class Lease : IDisposable
    {
        private SingleFlight? _owner;

        public Lease(SingleFlight owner) => _owner = owner;

        public void Dispose()
        {
            // Idempotent — only the first Dispose releases.
            var owner = global::System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
