using VoiceWink.Models;
using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>How the failed-marker persistence ended: committed (compose and present) or
/// refused by the shutdown latch (silent — the quit drain owns the surface, today's
/// commit-refused rule). A wrong-phase refusal THROWS in the production adapter and
/// never reaches this type.</summary>
internal enum MarkerPersistOutcome { Committed, ShutdownRefused }

/// <summary>The failed-marker step's outputs for the composer: outcome, the marker
/// row's id (retry-from-History target), the sanitized re-arm selections, and the
/// reference-persistence claim (joins the executor's single ownership list).</summary>
internal sealed record MarkerPersist(
    MarkerPersistOutcome Outcome,
    int? MarkerRowId,
    IReadOnlyList<ReferenceImageSelection>? RearmSelections,
    IDisposable? Claim);

/// <summary>How one FAILED batch version's own History row landed (IMG-4c). Unlike
/// <see cref="MarkerPersistOutcome"/> — deliberately left as-is for the N=1 terminal
/// path — this carries the writer's typed disposition, because a batch PROMISES a row
/// per version: failing to write one is the more actionable news and must supersede the
/// provider failure the row was recording.</summary>
internal enum ItemFailureRowOutcome { Committed, HistoryDisabled, RowAmbiguous, RowFailed }

/// <summary>One failed version's row (IMG-4c): outcome, the row id (retry target — only
/// the FIRST-processed failure's is presented), the sanitized re-arm selections for that
/// item's OWN exception, and the reference claim (appended to the executor's single
/// ownership list — never carried on <see cref="ImageBatchRunner.BatchResult"/>).</summary>
internal sealed record ItemFailureRowPersist(
    ItemFailureRowOutcome Outcome,
    int? RowId,
    IReadOnlyList<ReferenceImageSelection>? RearmSelections,
    IDisposable? Claim);

/// <summary>
/// The production terminal orchestrator for an image-generation job (IMG-3): runs the
/// batch loop, decides the terminal action, executes its side effects, and hands the
/// SINGLE ownership list of disposables to the one compose-and-queue site. Extracted
/// from the job body so the seam tests execute THIS composition — exactly-one queue,
/// claim transfer XOR disposal on every exit, no paste at N&gt;1 — instead of trusting
/// private <c>MainViewModel</c> wiring (Codex round 5).
///
/// <para><b>Ownership:</b> the claims list is seeded with the request's reference lease
/// at ENTRY (the list is the lease's only owner — no second alias anywhere may dispose
/// it); every per-item fresh-copy claim and the marker step's claim append as they are
/// produced. <paramref name="composeAndQueueAsync"/> receives the list, must not throw
/// after queueing, and its normal return marks the transfer; every non-transferred exit
/// disposes the whole list in the finally.</para>
///
/// <para><b>Threading:</b> plain awaits throughout — called UI-thread-affine from the
/// job body, continuations stay on the calling context (no Task.Run, no
/// ConfigureAwait(false)).</para>
/// </summary>
internal static class ImageBatchExecutor
{
    internal static async Task RunAsync<T>(
        int count,
        IDisposable? referenceLease,
        Func<int, CancellationToken, Task<T>> generateItemAsync,
        Func<bool> isHistoryEnabled,
        Func<bool> tryBeginCommit,
        Func<int, T, Task<ImageItemPersistResult>> persistItemAsync,
        Func<int, Exception, Task<ItemFailureRowPersist>> persistFailedItemAsync,
        Func<bool> resumeAfterCommit,
        Action<int> reportProgress,
        Func<bool> isShutdownRequested,
        Func<Exception, Task<MarkerPersist>> persistFailedMarkerAsync,
        Func<Task<(string Status, MiniRecorderTone Tone)>> pasteAsync,
        Func<ImageBatchRunner.BatchResult, MarkerPersist?, (string Status, MiniRecorderTone Tone)?,
            IReadOnlyList<ReferenceImageSelection>?, List<IDisposable>, Task> composeAndQueueAsync,
        Action<int, int> presentBareCancel,
        CancellationToken ct)
    {
        var claims = new List<IDisposable>(2);
        if (referenceLease != null)
            claims.Add(referenceLease);
        var transferred = false;
        // The terminal item's re-arm selections are the composer's middle fallback link
        // (a first-item RowAmbiguous re-arms with its durable AppReferences copies, not
        // the fragile originals; Codex round 6).
        // IMG-4c: the re-arm of whichever persist produced the TERMINAL stop — success
        // OR failure row. A persist-side stop sets commitsAllowed=false, so the last
        // persist to run is always the one that caused the stop. Tracking only the
        // success side would drop a failed row's SANITIZED re-arm when its
        // HistoryDisabled/RowAmbiguous outcome supersedes ProviderFailure, and the
        // composer would then fall back to the raw request references — re-arming a
        // reference sanitation deliberately removed (Codex diff r1, blocking).
        IReadOnlyList<ReferenceImageSelection>? lastTerminalRearm = null;

        try
        {
            var result = await ImageBatchRunner.RunAsync(
                count, generateItemAsync, isHistoryEnabled, tryBeginCommit,
                async (item, generation) =>
                {
                    var persist = await persistItemAsync(item, generation);
                    lastTerminalRearm = persist.RearmSelections;
                    if (persist.FreshCopyClaim != null)
                        claims.Add(persist.FreshCopyClaim);
                    return persist;
                },
                // IMG-4c: every failed version's row claim joins the SAME single
                // ownership list here — the runner only ever carries non-owning
                // (rowId, re-arm) metadata, never an IDisposable.
                async (item, error) =>
                {
                    var row = await persistFailedItemAsync(item, error);
                    lastTerminalRearm = row.RearmSelections;
                    if (row.Claim != null)
                        claims.Add(row.Claim);
                    return row;
                },
                resumeAfterCommit, reportProgress, ct);

            var action = ImageBatchPolicy.DecideBatchTerminal(
                result.Reason, result.Completed, count, isShutdownRequested());
            switch (action)
            {
                case ImageBatchPolicy.BatchTerminalAction.PasteThenPresent:
                    // Clipboard/paste immediately before the presentation queue — the
                    // IMG-BG write-then-announce ordering, unchanged for N=1.
                    var pasteDerived = await pasteAsync();
                    await composeAndQueueAsync(result, null, pasteDerived, lastTerminalRearm, claims);
                    transferred = true;
                    break;

                case ImageBatchPolicy.BatchTerminalAction.PresentBatchSuccess:
                case ImageBatchPolicy.BatchTerminalAction.PresentPartialStop:
                    await composeAndQueueAsync(result, null, null, lastTerminalRearm, claims);
                    transferred = true;
                    break;

                case ImageBatchPolicy.BatchTerminalAction.PresentBatchFailure:
                    // IMG-4c: the failure rows already exist (written inline, one per
                    // failed version). Present from the FIRST failure's row — the one
                    // whose exception the pill shows — synthesised into the marker shape
                    // the composer already consumes. No terminal marker step, so no
                    // duplicate row and no second commit admission.
                    await composeAndQueueAsync(
                        result,
                        new MarkerPersist(
                            MarkerPersistOutcome.Committed,
                            result.FirstFailureRowId,
                            result.FirstFailureRearm,
                            Claim: null), // every failure-row claim already joined the list
                        null, lastTerminalRearm, claims);
                    transferred = true;
                    break;

                case ImageBatchPolicy.BatchTerminalAction.PersistFailedMarkerThenPresent:
                    var marker = await persistFailedMarkerAsync(result.Failure!);
                    if (marker.Claim != null)
                        claims.Add(marker.Claim);
                    if (marker.Outcome == MarkerPersistOutcome.Committed)
                    {
                        await composeAndQueueAsync(result, marker, null, lastTerminalRearm, claims);
                        transferred = true;
                    }
                    // ShutdownRefused: silent — the quit drain owns the surface.
                    break;

                case ImageBatchPolicy.BatchTerminalAction.BareCancelPresenter:
                    presentBareCancel(result.Completed, count);
                    break;

                // Silent: nothing presents (shutdown refusal / cancel during quit).
            }
        }
        finally
        {
            if (!transferred)
            {
                foreach (var claim in claims)
                    claim.Dispose();
                claims.Clear();
            }
        }
    }
}
