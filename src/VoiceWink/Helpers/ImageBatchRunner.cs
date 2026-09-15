using System.Threading.Channels;

namespace VoiceWink.Helpers;

/// <summary>
/// The control flow of a multi-version image generation. IMG-3 ran the batch as a
/// sequential generate→commit loop; IMG-4 (2026-07-27) launched all provider calls
/// concurrently but withheld every commit until zero calls remained; IMG-4b (owner
/// directive 2026-07-28: "images appear in the history as soon as they have been
/// created") runs ONE drain pump with INLINE, pump-serialized commits — each version's
/// commit cycle (<c>tryBeginCommit</c> → persist → phase close) runs the moment its
/// envelope is dequeued, WHILE the remaining provider calls keep running. Envelopes
/// arrive on an unbounded FIFO channel, so commits happen in publication/dequeue order
/// (the order calls settled and published). The pump is sequential, so commits never
/// overlap each other — only cancellable HTTP work overlaps a commit, which is what
/// keeps the quit drain's bounded-while-cancellable rule and the "Committing holds only
/// one uncancellable local persistence" invariant true (paired with the IMG-4b
/// <c>ImageGenerationJobService</c> rule that the commit phase ALWAYS closes, even
/// under the shutdown latch). Pure orchestration over injected delegates; the UI-affine
/// side effects stay with the caller (<c>ImageBatchExecutor</c> / <c>MainViewModel</c>).
/// N=1 keeps the pre-IMG-4 sequential body byte-identically.
/// </summary>
internal static class ImageBatchRunner
{
    /// <summary>Why the batch ended. <see cref="Completed"/> is the only full-success
    /// value; everything else stopped early with <see cref="BatchResult.Completed"/>
    /// items already committed and safe.</summary>
    internal enum BatchStop
    {
        Completed,

        /// <summary>A provider call threw (including a NON-caller OCE with neither token
        /// cancelled — an HTTP-timeout OCE is a failure, not a user cancel; today's
        /// catch-pair rule). A provider failure does not cancel siblings (owner
        /// 2026-07-28): the remaining calls drain to natural settlement and EVERY
        /// success — settling before or after the failure — still commits as its
        /// envelope is dequeued (IMG-4b incremental commits). The first-processed
        /// failure is the presented one; later independent failures drop here (the
        /// production generate delegate logs each per item).</summary>
        ProviderFailure,

        /// <summary>An item's local persistence provably failed (PNG save or row write).
        /// Supersedes an earlier ProviderFailure/Cancelled (the persist machinery is the
        /// broken component; the failed marker is skipped — writing another row through
        /// it would be wrong).</summary>
        PersistFailure,

        /// <summary>An item's row-commit verdict could not be verified (probe query
        /// failed) — its PNG is retained; only confirmed items are counted. Supersedes
        /// like <see cref="PersistFailure"/>.</summary>
        RowAmbiguous,

        /// <summary>History turned off — remaining items have no output destination.
        /// Supersedes like <see cref="PersistFailure"/>.</summary>
        HistoryDisabled,

        /// <summary>The job's own token cancelled (user stop / tray cancel / quit).
        /// IMG-4: recorded at envelope processing AND once after the drain — a cancel is
        /// never lost even when every provider ignores it and returns normally. Settling
        /// successes still commit as they are dequeued ("cancel keeps completed
        /// versions" — since IMG-4b they are already in History); the shutdown latch,
        /// not the token, is what stops commits.</summary>
        Cancelled,

        /// <summary>Commit admission or cycle resume refused by the shutdown latch.
        /// Supersedes everything; the quit drain owns the surface.</summary>
        ShutdownRefused,
    }

    /// <summary>Deliberately minimal: <paramref name="Completed"/> counts CONFIRMED
    /// image commits only. Per-item metadata (ids, effective options, re-arm selections)
    /// is the caller's persist-delegate closure concern, not the runner's — with ONE
    /// exception (IMG-4c): the FIRST-processed failure's row id + re-arm ride here,
    /// because that row is the retry target the pill presents and it must agree with
    /// <paramref name="Failure"/>. Those are NON-OWNING values; the reference claim for
    /// every failure row goes straight to the executor's single claims list and is never
    /// carried on this record. <paramref name="FailureRowsCommitted"/> counts failure
    /// rows THIS RUNNER confirmed (observability only — the N=1 terminal marker is
    /// written by the executor afterwards and is deliberately not counted here).</summary>
    internal sealed record BatchResult(
        int Completed,
        BatchStop Reason,
        Exception? Failure,
        int? FirstFailureRowId = null,
        IReadOnlyList<Models.ReferenceImageSelection>? FirstFailureRearm = null,
        int FailureRowsCommitted = 0);

    /// <summary>How one wrapper task settled (IMG-4 §2.1 classifier).</summary>
    private enum EnvelopeKind { Success, HardFailure, Cancelled, SiblingCancelled }

    private readonly record struct Envelope<T>(int Item, T? Result, Exception? Error, EnvelopeKind Kind);

    /// <summary>
    /// Run <paramref name="count"/> items. Contracts:
    /// <list type="bullet">
    /// <item>N=1 runs the sequential pre-IMG-4 body byte-identically: ct → progress(1) →
    /// generate → post-generation ct check (a generated-but-uncommitted item DROPS on
    /// cancel — the serial rule, retired only for N&gt;1) → commit admission → persist.</item>
    /// <item>N&gt;1 (IMG-4b, one-stage incremental): ONE pre-launch gate
    /// (<paramref name="ct"/>, then the <paramref name="isHistoryEnabled"/> money gate)
    /// and ONE <c>reportProgress(0)</c>; then all items launch on a linked batch token
    /// and each envelope is dequeued in publication order — a Success reports progress
    /// and COMMITS IMMEDIATELY (its History row exists while siblings still generate),
    /// unless commits are over. A hard failure records the first-processed reason +
    /// exception and never cancels the batch token — a provider failure does not cancel
    /// siblings (owner 2026-07-28); user/quit cancellation via <paramref name="ct"/>
    /// still cancels the job, and the runner imposes no deadline of its own (remaining
    /// calls drain to settlement, however long their delegates run);
    /// <paramref name="ct"/> is observed at every envelope AND once post-drain, so a
    /// cancel is recorded even when every call returns normally — a recorded Cancelled
    /// (or ProviderFailure) does NOT stop commits for later-settling successes.
    /// Structured cleanup: on EVERY exit — including a throwing
    /// <paramref name="reportProgress"/> or persist delegate — the batch token is
    /// cancelled and every wrapper task is awaited BEFORE the runner returns or
    /// rethrows, so the caller can never race a live provider call.</item>
    /// <item>IMG-4c — EVERY version is represented: a HardFailure envelope also takes a
    /// commit and writes that version's OWN History row via
    /// <paramref name="persistFailedItemAsync"/>, inline, exactly like a success. Only
    /// the FIRST-processed failure's row id + re-arm ride out on the result (it is the
    /// row the pill presents, and it must agree with the presented exception); later
    /// failures' rows exist in History and are independently retryable but never feed
    /// the pill. A failure row that does NOT commit supersedes the provider failure it
    /// was recording — a batch promises a row per version, so failing to write one is
    /// the more actionable news — and stops all further commits.</item>
    /// <item>Commit rules: <paramref name="tryBeginCommit"/> false ⇒ ShutdownRefused
    /// (supersedes everything; the remaining generations are cancelled — a job-level
    /// quit, not sibling-failure propagation — and the pump stops reading). After a
    /// successful admission, BOTH persist delegates run through the same commit-cycle
    /// helper, whose finally calls <paramref name="resumeAfterCommit"/> EXACTLY once —
    /// returned outcome, returned failure, and a throwing delegate all close the commit
    /// phase before anything else runs (the close cannot throw there: the phase is
    /// provably Committing and the pump is its only mutator). A persist-side outcome
    /// stops all FURTHER commits and supersedes an earlier ProviderFailure/Cancelled,
    /// while generations keep draining to settlement uncommitted; a refused resume ⇒
    /// ShutdownRefused (supersedes even the persist-side stop). Exit phase is
    /// Generating on every batch path; N=1 alone still ends Committing.</item>
    /// <item>Exceptions from the non-generation delegates propagate (after the join):
    /// those are bugs for the job's top-level fault boundary, not batch outcomes.</item>
    /// </list>
    /// </summary>
    internal static async Task<BatchResult> RunAsync<T>(
        int count,
        Func<int, CancellationToken, Task<T>> generateItemAsync,
        Func<bool> isHistoryEnabled,
        Func<bool> tryBeginCommit,
        Func<int, T, Task<ImageItemPersistResult>> persistItemAsync,
        Func<int, Exception, Task<ItemFailureRowPersist>> persistFailedItemAsync,
        Func<bool> resumeAfterCommit,
        Action<int> reportProgress,
        CancellationToken ct)
    {
        if (count <= 1)
            return await RunSingleAsync(generateItemAsync, tryBeginCommit, persistItemAsync, reportProgress, ct);

        // ---- N>1: one drain pump, inline serialized commits (IMG-4b) ----
        if (ct.IsCancellationRequested)
            return new BatchResult(0, BatchStop.Cancelled, null);
        if (!isHistoryEnabled())
            return new BatchResult(0, BatchStop.HistoryDisabled, null);
        reportProgress(0);

        BatchStop? stop = null;
        Exception? failure = null;
        var settled = 0;
        var completed = 0;
        var failureRowsCommitted = 0;
        int? firstFailureRowId = null;
        IReadOnlyList<Models.ReferenceImageSelection>? firstFailureRearm = null;
        var commitsAllowed = true;

        // Envelopes arrive in PUBLICATION/DEQUEUE order — the order the calls settled
        // and published, which is what "a version appears in History as soon as it has
        // been created" means operationally. Unbounded ⇒ the wrapper's TryWrite can
        // never fail or block, so the exact-count read below can never hang on a
        // settled call.
        var envelopes = Channel.CreateUnbounded<Envelope<T>>();
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wrappers = new Task[count];
        for (var i = 0; i < count; i++)
            wrappers[i] = RunWrapperAsync(i + 1, generateItemAsync, envelopes.Writer, batchCts.Token, ct);

        // The ONE commit-cycle shape, shared by success rows and failure rows (IMG-4c):
        // exactly one phase-close per successful admission on EVERY path — returned
        // outcome, returned failure, AND a throwing delegate. A throw would otherwise
        // leave the phase Committing while the cleanup below joins live wrappers,
        // sending a quit into the unbounded commit wait. The close cannot throw here:
        // the phase is provably Committing and this pump is its only mutator.
        async Task<bool> RunCommitCycleAsync(Func<Task> persistBody)
        {
            var admitted = false;
            try
            {
                await persistBody();
            }
            finally
            {
                admitted = resumeAfterCommit();
            }
            return admitted;
        }

        try
        {
            for (var read = 0; read < count; read++)
            {
                var envelope = await envelopes.Reader.ReadAsync();

                // Cancellation linearization: observed BEFORE handling any envelope —
                // cancel-before-failure wins, and a cancel is recorded even when the
                // settling call returned normally.
                if (stop == null && ct.IsCancellationRequested)
                    stop = BatchStop.Cancelled;

                switch (envelope.Kind)
                {
                    case EnvelopeKind.Success:
                        // Progress first (generation vocabulary — the version EXISTS)...
                        reportProgress(++settled);
                        // ...then its commit, immediately, while siblings keep running —
                        // unless commits are over (persist-side stop / shutdown), in
                        // which case the version stays settled-but-uncommitted and only
                        // the progress count is honest about it.
                        if (!commitsAllowed)
                            break;
                        if (!tryBeginCommit())
                        {
                            // Shutdown latch: the quit drain owns the surface — stop
                            // everything, including the remaining generations (a
                            // job-level cancel, not sibling-failure propagation).
                            stop = BatchStop.ShutdownRefused;
                            commitsAllowed = false;
                            batchCts.Cancel();
                            break;
                        }
                        ImageItemPersistResult persist = default!;
                        var admission = await RunCommitCycleAsync(async () =>
                            persist = await persistItemAsync(envelope.Item, envelope.Result!));
                        if (persist.Outcome == ImageItemPersistOutcome.Committed)
                        {
                            completed++;
                        }
                        else
                        {
                            // Persist-side stop: the local persist machinery is the
                            // broken component — no FURTHER commits; generations keep
                            // draining to settlement (the failure-independence
                            // posture). Supersedes an earlier ProviderFailure or
                            // Cancelled (the return line then carries no failure).
                            stop = persist.Outcome switch
                            {
                                ImageItemPersistOutcome.HistoryDisabled => BatchStop.HistoryDisabled,
                                ImageItemPersistOutcome.RowAmbiguous => BatchStop.RowAmbiguous,
                                _ => BatchStop.PersistFailure, // SaveFailed / RowFailed
                            };
                            commitsAllowed = false;
                        }
                        if (!admission)
                        {
                            // Latch landed during the commit: ShutdownRefused
                            // supersedes even the persist-side stop (top of the
                            // lattice; the quit drain owns the surface).
                            stop = BatchStop.ShutdownRefused;
                            commitsAllowed = false;
                            batchCts.Cancel();
                        }
                        break;
                    case EnvelopeKind.HardFailure:
                        var isFirstFailure = stop == null;
                        if (isFirstFailure)
                        {
                            // First-processed hard failure is THE presented failure —
                            // and it never cancels the batch token (owner 2026-07-28),
                            // so siblings run on to natural settlement and every success
                            // stays commit-eligible. Only the job token, a shutdown
                            // refusal, or structured cleanup cancels the batch token.
                            stop = BatchStop.ProviderFailure;
                            failure = envelope.Error;
                        }
                        // IMG-4c: this version gets its OWN History row, written inline
                        // like a success — every requested version is represented, and
                        // each failure is independently retryable from History.
                        if (!commitsAllowed)
                            break;
                        if (!tryBeginCommit())
                        {
                            stop = BatchStop.ShutdownRefused;
                            commitsAllowed = false;
                            batchCts.Cancel();
                            break;
                        }
                        ItemFailureRowPersist row = default!;
                        var rowAdmission = await RunCommitCycleAsync(async () =>
                            row = await persistFailedItemAsync(envelope.Item, envelope.Error!));
                        if (row.Outcome == ItemFailureRowOutcome.Committed)
                        {
                            failureRowsCommitted++;
                            // Only the FIRST-processed failure's row is presented: it is
                            // the one whose exception the pill shows and whose per-item
                            // reference sanitation the re-arm reflects. Later failures'
                            // rows exist in History but never feed the pill.
                            if (isFirstFailure)
                            {
                                firstFailureRowId = row.RowId;
                                firstFailureRearm = row.RearmSelections;
                            }
                        }
                        else
                        {
                            // A batch PROMISES a row per version, so failing to write one
                            // supersedes the provider failure it was recording — that is
                            // the more actionable news (plan r1 finding 1, N>1 only).
                            stop = row.Outcome switch
                            {
                                ItemFailureRowOutcome.HistoryDisabled => BatchStop.HistoryDisabled,
                                ItemFailureRowOutcome.RowAmbiguous => BatchStop.RowAmbiguous,
                                _ => BatchStop.PersistFailure, // RowFailed
                            };
                            failure = null; // a persist-side stop carries no provider exception
                            commitsAllowed = false;
                        }
                        if (!rowAdmission)
                        {
                            stop = BatchStop.ShutdownRefused;
                            commitsAllowed = false;
                            batchCts.Cancel();
                        }
                        break;
                    case EnvelopeKind.Cancelled:
                        stop ??= BatchStop.Cancelled;
                        break;
                    // SiblingCancelled: inert — never a reason, never a failure.
                }

                if (stop == BatchStop.ShutdownRefused)
                    break; // quit: stop reading; the finally cancels + joins
            }

            // Post-drain linearization: every provider returned normally, yet the user
            // cancelled — without this check the batch would present as a full success.
            if (stop == null && ct.IsCancellationRequested)
                stop = BatchStop.Cancelled;
        }
        finally
        {
            // Structured cleanup on EVERY exit (incl. a throwing reportProgress or
            // persist delegate): cancel the batch token and JOIN all wrappers before
            // returning or rethrowing — runner exit ⇒ zero generations in flight, so
            // the executor's lease disposal and ReleaseSlot can never race a live
            // provider call.
            batchCts.Cancel();
            await Task.WhenAll(wrappers);
        }

        return new BatchResult(completed, stop ?? BatchStop.Completed,
            stop == BatchStop.ProviderFailure ? failure : null,
            firstFailureRowId, firstFailureRearm, failureRowsCommitted);
    }

    /// <summary>The pre-IMG-4 sequential body for a single image — kept byte-identical
    /// (incl. the post-generation ct check that DROPS a generated-but-uncommitted item
    /// on cancel, and the <c>reportProgress(1)</c> delegate call whose text is null at
    /// N=1 so nothing renders).</summary>
    private static async Task<BatchResult> RunSingleAsync<T>(
        Func<int, CancellationToken, Task<T>> generateItemAsync,
        Func<bool> tryBeginCommit,
        Func<int, T, Task<ImageItemPersistResult>> persistItemAsync,
        Action<int> reportProgress,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new BatchResult(0, BatchStop.Cancelled, null);

        reportProgress(1);

        T generation;
        try
        {
            generation = await generateItemAsync(1, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BatchResult(0, BatchStop.Cancelled, null);
        }
        catch (Exception ex)
        {
            return new BatchResult(0, BatchStop.ProviderFailure, ex);
        }

        if (ct.IsCancellationRequested)
            return new BatchResult(0, BatchStop.Cancelled, null);
        if (!tryBeginCommit())
            return new BatchResult(0, BatchStop.ShutdownRefused, null);

        var persist = await persistItemAsync(1, generation);
        return persist.Outcome switch
        {
            ImageItemPersistOutcome.Committed => new BatchResult(1, BatchStop.Completed, null),
            ImageItemPersistOutcome.HistoryDisabled => new BatchResult(0, BatchStop.HistoryDisabled, null),
            ImageItemPersistOutcome.RowAmbiguous => new BatchResult(0, BatchStop.RowAmbiguous, null),
            _ => new BatchResult(0, BatchStop.PersistFailure, null),
        };
    }

    /// <summary>One generation settled into an envelope PUBLISHED to the batch channel
    /// at the moment it settles — publication is non-cancellable (unbounded channel;
    /// <c>TryWrite</c> cannot fail or block) and wrapper tasks never fault, so the
    /// pump's exact-count read can never hang on a settled call. Classifier order
    /// matters: job-token OCE = user/quit cancel; batch-token-only OCE =
    /// cleanup-or-shutdown-induced cancel (inert; producers are the structured-cleanup
    /// finally and the ShutdownRefused stop — both after the pump has stopped reading);
    /// an OCE with NEITHER token cancelled falls through to HardFailure — the pinned
    /// non-caller-OCE → ProviderFailure contract. (Residual, recorded: on those induced
    /// paths an independent non-caller OCE is indistinguishable from the induced cancel
    /// and drops — the exit's owner already presents.)</summary>
    private static async Task RunWrapperAsync<T>(
        int item,
        Func<int, CancellationToken, Task<T>> generateItemAsync,
        ChannelWriter<Envelope<T>> envelopes,
        CancellationToken batchToken,
        CancellationToken jobToken)
    {
        Envelope<T> envelope;
        try
        {
            var result = await generateItemAsync(item, batchToken);
            envelope = new Envelope<T>(item, result, null, EnvelopeKind.Success);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            envelope = new Envelope<T>(item, default, null, EnvelopeKind.Cancelled);
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
        {
            envelope = new Envelope<T>(item, default, null, EnvelopeKind.SiblingCancelled);
        }
        catch (Exception ex)
        {
            envelope = new Envelope<T>(item, default, ex, EnvelopeKind.HardFailure);
        }
        envelopes.TryWrite(envelope);
    }
}
