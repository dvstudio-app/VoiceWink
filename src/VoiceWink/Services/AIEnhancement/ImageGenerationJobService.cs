using Serilog;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Lifecycle phase of the single background image-generation job slot (IMG-BG, 2026-07-16).
/// </summary>
public enum ImageGenerationJobPhase
{
    /// <summary>No job: the slot is free.</summary>
    Idle,

    /// <summary>Slot reserved; the dispatcher is still capturing the request (focus handoff,
    /// reference leases). A reservation disposed in this phase frees the slot without a body
    /// ever running.</summary>
    Preparing,

    /// <summary>The job body is running its cancellable section (provider HTTP work). A
    /// multi-version batch (IMG-4) runs ALL its provider calls concurrently inside this
    /// phase; since IMG-4b (incremental commits) each version COMMITS as its envelope is
    /// dequeued, cycling Generating⇄Committing via
    /// <see cref="ImageGenerationJobService.EndCommitResumeGenerating"/> WHILE the
    /// remaining provider calls keep running — the batch pump serializes commits, so
    /// only cancellable HTTP work ever overlaps a commit.</summary>
    Generating,

    /// <summary>The job body is persisting user data (image file, history row, reference
    /// copies — success AND failed-attempt retention). Deliberately NOT cancellable: a saved
    /// image must never be stranded without its History row. This phase contains ONLY local
    /// persistence, one commit at a time. Shutdown never abandons an in-flight commit — it
    /// awaits the per-commit signal armed by <see cref="ImageGenerationJobService.TryBeginCommit"/>
    /// with no cap, then applies a FRESH generation bound to whatever cancellable work
    /// remains (IMG-4b; the latch makes a NEW commit impossible, so that wait covers exactly
    /// one persistence). N=1 deliberately keeps its whole tail (persist + paste + composition)
    /// inside this phase until the body ends — shutdown waits that tail out, today's behavior.
    /// Only the user's explicit second Quit click abandons a commit.</summary>
    Committing,
}

/// <summary>
/// Coordinator for the single background image-generation job (IMG-BG, 2026-07-16): image
/// generation runs detached from the recording pipeline so a multi-minute generation no longer
/// holds <c>RecordingState.Enhancing</c> and blocks recording. This class owns ONLY the slot
/// lifecycle — reservation, the job's own CancellationTokenSource (never the pipeline's shared
/// <c>_transcriptionCts</c>, which every next recording cancels+disposes), the atomic
/// Generating→Committing transition, and the shutdown latch. The job BODY stays with the caller
/// (<c>MainViewModel</c>), which passes it to <see cref="JobReservation.Start"/>.
///
/// <para><b>Dispatch order is load-bearing (Codex plan review round 2):</b> reserve FIRST via
/// <see cref="TryReserve"/>, then capture the immutable request (focus-slot handoff, reference
/// leases), then <see cref="JobReservation.Start"/>. A lost reservation must transfer nothing;
/// a preparation failure disposes the reservation and every already-transferred resource.</para>
///
/// <para><b>Threading:</b> all mutations (reserve, start, commit transition, cancel, shutdown)
/// happen on the UI thread — the body runs UI-thread-affine (fire-and-forget async, standard
/// SynchronizationContext continuations), preserving the pipeline's threading invariants.
/// <see cref="IsRunning"/>/<see cref="Phase"/> are lock-guarded because the maintenance gate
/// polls them off-thread (<c>IMaintenanceGate.Check()</c>). <see cref="StateChanged"/> is always
/// raised on the UI thread.</para>
/// </summary>
public sealed class ImageGenerationJobService
{
    private static ILogger Logger => Log.ForContext<ImageGenerationJobService>();

    private readonly object _gate = new();
    private ImageGenerationJobPhase _phase = ImageGenerationJobPhase.Idle;
    // HIS-3: bumped under _gate on EVERY phase mutation. Consumed only through the atomic
    // TryCaptureIdleEpoch/IsIdleAtEpoch pair — never exposed raw, so separate state reads
    // can't become a (racy) coordination contract.
    private long _stateEpoch;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _completion;
    // IMG-4b: armed by TryBeginCommit under _gate, completed when the commit phase closes
    // (EndCommitResumeGenerating, or ReleaseSlot for N=1/fault paths). ShutdownAsync awaits
    // THIS — never whole-job completion — so its uncapped wait covers exactly one persistence.
    private TaskCompletionSource? _commitDone;
    private bool _shutdown;

    /// <summary>Raised on every slot/phase transition, always on the UI thread. Consumers
    /// (pill snapshot, tray menu) re-read <see cref="IsRunning"/>/<see cref="Phase"/>.</summary>
    public event Action? StateChanged;

    /// <summary>True from a successful <see cref="TryReserve"/> until the body ends or the
    /// unstarted reservation is disposed. Safe to read off-thread (maintenance gate).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _phase != ImageGenerationJobPhase.Idle; }
    }

    /// <summary>Current lifecycle phase. Safe to read off-thread.</summary>
    public ImageGenerationJobPhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    /// <summary>
    /// True once <see cref="ShutdownAsync"/> latched admission shut (quit drain); latched
    /// forever. The latch is set BEFORE the drain's cancel while App's <c>_isQuitting</c> is
    /// set only AFTER the drain returns — this is how the job body's cancel presentation
    /// tells a quit-drain cancel (end hidden, no pill) from a user cancel (amber
    /// confirmation). Safe to read off-thread.
    /// </summary>
    public bool IsShutdownRequested
    {
        get { lock (_gate) return _shutdown; }
    }

    /// <summary>
    /// Completed when the slot is free; otherwise completes when the current reservation ends
    /// (body finished — any outcome — or unstarted reservation disposed). Never faults.
    /// </summary>
    public Task Completion
    {
        get { lock (_gate) return _completion?.Task ?? Task.CompletedTask; }
    }

    /// <summary>
    /// Atomically reserve the single job slot. Returns <c>null</c> when a job is already
    /// reserved/running or the service has been shut down. The winner MUST either
    /// <see cref="JobReservation.Start"/> the body or <see cref="JobReservation.Dispose"/> the
    /// reservation (preparation failure) — both free the slot deterministically.
    /// </summary>
    public JobReservation? TryReserve()
    {
        lock (_gate)
        {
            if (_shutdown || _phase != ImageGenerationJobPhase.Idle)
                return null;

            _phase = ImageGenerationJobPhase.Preparing;
            _stateEpoch++;
            _cts = new CancellationTokenSource();
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RaiseStateChanged();
        return new JobReservation(this, _cts!.Token);
    }

    /// <summary>Cancel the active job (no-op when idle). Idempotent. The cancellable section is
    /// <see cref="ImageGenerationJobPhase.Generating"/>; a job already Committing finishes its
    /// persistence tail by design.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _phase == ImageGenerationJobPhase.Idle ? null : _cts;

        // Cancel outside the lock — CTS callbacks run inline on the cancelling thread.
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* body finished between the read and the cancel */ }
    }

    /// <summary>
    /// Terminal shutdown for app quit: atomically latch admission shut (every later
    /// <see cref="TryReserve"/> returns null forever), cancel the active job, then drain.
    /// IMG-4b shape: an in-flight COMMIT (snapshotted under the same lock that latches —
    /// after the latch no new commit can ever begin) is awaited via its per-commit signal
    /// with no cap — an already-started commit is never abandoned by this method; the
    /// caller's explicit second-click skip is the only abandonment path, and it lives at
    /// the call site. Only THEN does the <paramref name="generationBound"/> start, fresh,
    /// for the residual cancellable drain (cancellation lands in ms; the bound is a safety
    /// net). For N=1 the commit signal completes at slot release, so the commit wait is the
    /// whole persist+paste tail — today's behavior, deliberately kept.
    /// Returns true when fully drained, false when the bound expired while still cancellable.
    /// </summary>
    public async Task<bool> ShutdownAsync(TimeSpan generationBound)
    {
        CancellationTokenSource? cts;
        Task completion;
        Task? commitDone;
        lock (_gate)
        {
            _shutdown = true;
            cts = _phase == ImageGenerationJobPhase.Idle ? null : _cts;
            completion = _completion?.Task ?? Task.CompletedTask;
            // Snapshot under the SAME lock as the latch: either a commit is in flight NOW
            // (we must wait it out) or none can start later (TryBeginCommit checks
            // _shutdown under this lock) — no window in between.
            commitDone = _phase == ImageGenerationJobPhase.Committing ? _commitDone?.Task : null;
        }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        // Never abandon the in-flight persistence — uncapped, but scoped to exactly the
        // one commit (the latch forbids another).
        if (commitDone != null)
            await Task.WhenAny(completion, commitDone);

        if (completion.IsCompleted)
            return true;

        // FRESH bound for the cancellable residual drain — the commit wait above must not
        // consume it (Codex plan round: a long commit would otherwise leave the drain a
        // sliver of the bound).
        // FRESH bound for the cancellable residual drain — created HERE, after the
        // commit wait, never before it (a bound started earlier would already have
        // expired and the residual drain would get no bound at all; pinned by
        // Shutdown_MidCommit_WaitsCommitUncapped_ThenFreshBoundBoundsResidualDrain,
        // verified to fail against that mutation).
        var winner = await Task.WhenAny(completion, Task.Delay(generationBound));
        if (winner == completion)
            return true;

        // Bound expired. Post-latch the phase cannot be Committing (lock argument above);
        // this arm is a frozen safety net against future drift, not a reachable path.
        if (Phase == ImageGenerationJobPhase.Committing)
        {
            await completion;
            return true;
        }
        Logger.Warning("Image job shutdown drain abandoned in phase {Phase} after {Bound}",
            Phase, generationBound);
        return false;
    }

    /// <summary>
    /// Atomic Generating→Committing transition, synchronized with <see cref="ShutdownAsync"/>'s
    /// latch: either shutdown wins first (returns false — the body must abort WITHOUT starting
    /// its persistence tail, so nothing can be orphaned) or the commit wins (shutdown's drain
    /// then waits for the tail to complete). The body calls this immediately after its final
    /// cancellation check, before any user data is written.
    /// </summary>
    internal bool TryBeginCommit()
    {
        bool won;
        lock (_gate)
        {
            won = !_shutdown && _phase == ImageGenerationJobPhase.Generating;
            if (won)
            {
                _phase = ImageGenerationJobPhase.Committing;
                _stateEpoch++;
                // IMG-4b: the per-commit signal ShutdownAsync waits on. Armed under the
                // same lock that flips the phase, so a Committing snapshot always sees it.
                _commitDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (won)
            RaiseStateChanged();
        return won;
    }

    /// <summary>
    /// Batch commit-phase close: the item's persistence is DONE — the phase ALWAYS exits
    /// <see cref="ImageGenerationJobPhase.Committing"/> back to Generating (IMG-4b: even
    /// under the shutdown latch — "the commit finished" and "may more work start" are
    /// independent facts; leaving the phase Committing would make ShutdownAsync classify
    /// the batch's still-running provider calls as uncancellable persistence) and the
    /// per-commit signal completes. The RETURN VALUE alone carries admission: false ONLY
    /// under the shutdown latch — the batch commits nothing further and nothing new
    /// starts, mirroring <see cref="TryBeginCommit"/>'s latch rule. Since IMG-4b the
    /// batch's remaining provider calls DO keep running across this cycle (incremental
    /// commits); the batch pump serializes, so commits still never overlap. Any other
    /// phase THROWS: a wrong-phase call is a caller bug, and a silent false would
    /// masquerade as a quiet quit and eat the rest of a paid batch (Codex plan reviews
    /// r2/r5).
    /// </summary>
    internal bool EndCommitResumeGenerating()
    {
        bool resumed;
        TaskCompletionSource? commitDone;
        lock (_gate)
        {
            if (_phase != ImageGenerationJobPhase.Committing)
                throw new InvalidOperationException(
                    $"EndCommitResumeGenerating called in phase {_phase} — only Committing may resume");
            _phase = ImageGenerationJobPhase.Generating;
            _stateEpoch++;
            commitDone = _commitDone;
            _commitDone = null;
            resumed = !_shutdown;
        }
        commitDone?.TrySetResult();
        RaiseStateChanged();
        if (!resumed)
            Logger.Information("Batch resume refused — shutdown in progress; stopping after the committed item");
        return resumed;
    }

    /// <summary>
    /// HIS-3 sweep coordination, part 1: atomically observe "the slot is IDLE right now" and
    /// capture the state epoch under the same lock. The orphan-image sweep calls this before
    /// its row snapshot; a false return means a job is live and the sweep skips the pass —
    /// separate <see cref="IsRunning"/>-then-epoch reads would leave a window for a job to
    /// reserve and park mid-Committing (PNG saved, row pending) unseen (Codex plan round 2).
    /// </summary>
    internal bool TryCaptureIdleEpoch(out long epoch)
    {
        lock (_gate)
        {
            epoch = _stateEpoch;
            return _phase == ImageGenerationJobPhase.Idle;
        }
    }

    /// <summary>
    /// HIS-3 sweep coordination, part 2: still idle AND no phase mutation happened since the
    /// captured epoch (every mutation bumps — a full reserve→release cycle returning to Idle
    /// still reads false). Idle-at-capture + idle-at-same-epoch ⟹ no job ran across the
    /// sweep's snapshot/enumeration window, so no enumerated PNG can belong to an in-flight
    /// commit whose row is missing from the snapshot.
    /// </summary>
    internal bool IsIdleAtEpoch(long epoch)
    {
        lock (_gate)
            return _phase == ImageGenerationJobPhase.Idle && _stateEpoch == epoch;
    }

    private void MarkGenerating()
    {
        lock (_gate)
        {
            if (_phase == ImageGenerationJobPhase.Preparing)
            {
                _phase = ImageGenerationJobPhase.Generating;
                _stateEpoch++;
            }
        }
        RaiseStateChanged();
    }

    private void ReleaseSlot()
    {
        TaskCompletionSource? completion;
        TaskCompletionSource? commitDone;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            completion = _completion;
            commitDone = _commitDone;
            _cts = null;
            _completion = null;
            // IMG-4b: complete any leftover per-commit signal — N=1 never calls the
            // resume (its whole tail lives inside Committing), and a faulted body may
            // die mid-commit; a waiter must never outlive the slot.
            _commitDone = null;
            _phase = ImageGenerationJobPhase.Idle;
            _stateEpoch++;
        }
        cts?.Dispose();
        commitDone?.TrySetResult();
        completion?.TrySetResult();
        RaiseStateChanged();
    }

    private async Task RunBodyAsync(Func<CancellationToken, Task> body, CancellationToken token)
    {
        try
        {
            await body(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The body handles its own cancel presentation; this is the backstop for an OCE
            // that escapes it (e.g. thrown between the body's own catch scopes).
            Logger.Information("Image generation job cancelled");
        }
        catch (Exception ex)
        {
            // Top-level fault boundary: the job is fire-and-forget, so an escaped exception
            // would otherwise vanish unobserved (Codex plan review round 2).
            Logger.Error(ex, "Image generation job faulted outside the body's own handlers");
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Logger.Error(ex, "Image job StateChanged subscriber threw"); }
    }

    /// <summary>
    /// The reservation handed to the single winner of <see cref="TryReserve"/>. Exactly one of
    /// <see cref="Start"/> / <see cref="Dispose"/> takes effect (first call wins; both are safe
    /// to call, so a <c>using</c> around preparation composes with a successful Start).
    /// </summary>
    public sealed class JobReservation : IDisposable
    {
        private ImageGenerationJobService? _owner;

        /// <summary>The job's own cancellation token (tray cancel / quit shutdown).</summary>
        public CancellationToken Token { get; }

        internal JobReservation(ImageGenerationJobService owner, CancellationToken token)
        {
            _owner = owner;
            Token = token;
        }

        /// <summary>
        /// Start the job body fire-and-forget on the CALLING (UI) thread — continuations stay
        /// UI-affine. The slot stays occupied until the body completes, whatever the outcome.
        /// </summary>
        public void Start(Func<CancellationToken, Task> body)
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
                return;

            owner.MarkGenerating();
            _ = owner.RunBodyAsync(body, Token);
        }

        /// <summary>Abort an unstarted reservation (preparation failed): frees the slot and
        /// resolves <see cref="Completion"/>. No-op after <see cref="Start"/>.</summary>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseSlot();
        }
    }
}
