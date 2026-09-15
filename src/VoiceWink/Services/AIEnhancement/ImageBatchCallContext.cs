namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// IMG-4: the single owner of the cross-call state a PARALLEL multi-version batch shares
/// between its N concurrent provider calls. Created per batch by the job body (N&gt;1
/// only) and threaded to <c>AIEnhancementService.GenerateImageWithModelForBatchAsync</c>;
/// a null context everywhere means "not a batch" and keeps every existing path
/// byte-identical.
///
/// <para><b>Shared reference read:</b> <see cref="ReadAsync"/> is the memoization owner —
/// the FIRST invocation captures that caller's token (every batch item passes the same
/// batch token) and starts the validated read exactly once; all items await the same
/// task, so all versions are generated from identical reference bytes and the source
/// bytes are held once instead of N times. The read is invoked INSIDE the service at the
/// existing post-validation read site, so invalid-key/offline/unsupported-model errors
/// still win over a broken reference file.</para>
///
/// <para><b>Response materialization gate:</b> one <see cref="SemaphoreSlim"/>(1) the
/// image clients acquire around their alloc-heavy response materialization (body
/// read-as-string → JSON parse → base64 decode; the URL-fallback body copy). Requests
/// stay fully concurrent — only the ~5×-body transient phase serializes, bounding the
/// batch's response-side peak at ~one materialization chain plus the capped buffered
/// bodies.</para>
///
/// <para><b>Lifecycle:</b> disposed by the job body's finally AFTER the batch executor
/// returns — the runner joins every wrapper task before returning (structured cleanup),
/// so no call can touch the gate or the read task afterwards.</para>
/// </summary>
internal sealed class ImageBatchCallContext : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<ReferenceImage>>> _readFactory;
    private Task<IReadOnlyList<ReferenceImage>>? _readTask;

    /// <summary>Serializes the clients' response-materialization sections (IMG-4 §2.6b).
    /// Acquisition-safe usage is the CLIENTS' contract: release only after a successful
    /// WaitAsync — a cancelled waiter must never increment the semaphore.</summary>
    public SemaphoreSlim ResponseMaterializationGate { get; } = new(1, 1);

    public ImageBatchCallContext(Func<CancellationToken, Task<IReadOnlyList<ReferenceImage>>> readFactory)
    {
        _readFactory = readFactory;
    }

    /// <summary>Memoized shared read: starts the underlying read on the FIRST call (that
    /// caller's token wins — all batch items carry the same token) and returns the same
    /// task to every caller. The factory's synchronous prologue runs under the init lock;
    /// it is trivially cheap (the real read awaits off-thread immediately).
    ///
    /// <para>IMG-6 briefly threaded a per-model reference bound through here. It was removed along
    /// with the per-model READ gate: a memoized read takes whichever value arrives first, so a
    /// bound passed per call could silently disagree between items (Codex/Kimi diff review). The
    /// read gate is a fixed sanity ceiling now, so there is nothing per-run to carry.</para>
    /// </summary>
    public Task<IReadOnlyList<ReferenceImage>> ReadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _readTask ??= _readFactory(ct);
            return _readTask;
        }
    }

    public void Dispose() => ResponseMaterializationGate.Dispose();

    /// <summary>
    /// The ONE acquisition-safe gate wrapper the image clients use around their
    /// response-materialization sections. Null gate (not a batch) runs the body
    /// directly — zero overhead on the single-image path. Release happens ONLY after a
    /// successful WaitAsync: a cancelled waiter throws BEFORE the try and never
    /// increments the semaphore; a body failure (parse error, cancelled body read,
    /// copy failure) releases via the finally.
    /// </summary>
    internal static async Task<T> RunGatedAsync<T>(SemaphoreSlim? gate, Func<Task<T>> body, CancellationToken ct)
    {
        if (gate == null)
            return await body().ConfigureAwait(false);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
