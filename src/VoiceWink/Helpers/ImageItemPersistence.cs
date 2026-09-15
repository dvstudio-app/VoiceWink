using VoiceWink.Models;
using VoiceWink.Services.Data;

namespace VoiceWink.Helpers;

/// <summary>Tri-state result of the ambiguity probe (<see cref="RowProbeOutcome"/> + the
/// found row's id). A failed QUERY is its own state — collapsing it into NotFound would
/// let the caller delete a image file a committed row still references.</summary>
internal readonly record struct RowProbe(RowProbeOutcome Outcome, int? Id);

internal enum RowProbeOutcome { Found, NotFound, QueryFailed }

/// <summary>The row-write stage's combined outputs: the write disposition plus the
/// reference-persistence side products (fresh-copy claim, re-arm selections) that ride
/// the same <c>PersistWithRowAsync</c> call.</summary>
internal sealed record ItemRowWrite(
    RowWriteResult Row,
    IDisposable? FreshCopyClaim,
    IReadOnlyList<ReferenceImageSelection>? RearmSelections);

/// <summary>How one batch item's persistence ended (IMG-3).</summary>
internal enum ImageItemPersistOutcome
{
    /// <summary>Image file + row landed (or N=1 legacy fail-soft — see <see cref="ImageItemPersistence"/>).</summary>
    Committed,

    /// <summary>History off at a gate — nothing retained for this item; the batch stops honestly.</summary>
    HistoryDisabled,

    /// <summary>The image file never landed; the row was never attempted.</summary>
    SaveFailed,

    /// <summary>The row provably did not commit; the item's image file was deleted.</summary>
    RowFailed,

    /// <summary>The row write threw AND the probe could not verify — the image file is RETAINED
    /// (a committed row must never lose its file) and the batch stops with the
    /// "couldn't verify" copy.</summary>
    RowAmbiguous,
}

/// <summary>One item's persistence result. <see cref="HistoryId"/> is nullable even at
/// <see cref="ImageItemPersistOutcome.Committed"/> (N=1 history-off / legacy fail-soft);
/// claim and re-arm selections are surfaced for the executor's single ownership list
/// regardless of outcome.</summary>
internal sealed record ImageItemPersistResult(
    ImageItemPersistOutcome Outcome,
    string? ImagePath,
    int? HistoryId,
    IDisposable? FreshCopyClaim,
    IReadOnlyList<ReferenceImageSelection>? RearmSelections);

/// <summary>
/// The per-item persistence sequence for image generation (IMG-3), extracted
/// delegate-seamed so its contract is test-pinned without the UI-affine job body.
///
/// <para><b>History gates:</b> N&gt;1 has THREE live reads per item — the runner's
/// pre-generation money gate, this sequence's image file gate (read 1), and the history
/// writer's own internal gate (read 2, inside <paramref name="writeRowAsync"/>). N=1 has
/// the latter two, exactly today's shape. A row write that passed the writer's gate
/// commits even if the toggle lands mid-transaction; the toggle takes effect at the next
/// item's gates.</para>
///
/// <para><b>Metrics come FIRST</b> — the provider call is paid and committed by the time
/// persistence runs, so lifetime usage counts regardless of the History setting
/// (today's metrics-before-History-read ordering, Codex round 6).</para>
///
/// <para><b>Batch contract</b> (<paramref name="applyBatchContract"/>, count &gt; 1):
/// a batch's only output surface is History, so an item that cannot land there stops the
/// batch honestly. Reconciliation follows the ReferencePersistence doctrine: a
/// <see cref="RowWriteDisposition.Disabled"/> write provably attempted nothing (image file
/// deleted); an <see cref="RowWriteDisposition.AttemptedAmbiguous"/> one is resolved by
/// the unique-path probe — Found adopts the committed row, NotFound deletes the image file,
/// QueryFailed RETAINS it (never delete on ambiguity).</para>
///
/// <para><b>N=1</b> keeps today's fail-soft path verbatim: history-gated save, row
/// write, <see cref="ImageItemPersistOutcome.Committed"/> unconditionally with the
/// nullable id passthrough — no probe, no delete, both recorded legacy edges preserved
/// (image-less row on save failure; orphan image file when History races off).</para>
/// </summary>
internal static class ImageItemPersistence
{
    internal static async Task<ImageItemPersistResult> PersistAsync(
        bool applyBatchContract,
        Func<bool> readHistoryEnabled,
        Func<Task> recordMetricsAsync,
        Func<Task<string?>> saveImageAsync,
        Func<bool, string?, Task<ItemRowWrite>> writeRowAsync,
        Func<string, Task<RowProbe>> probeRowByImagePathAsync,
        Func<string, Task> tryDeleteImageAsync)
    {
        await recordMetricsAsync();
        var historyEnabled = readHistoryEnabled();

        if (!applyBatchContract)
        {
            // Today's single-image sequence: the ONE read gates the image file and rides into the
            // row write (whose writer re-reads internally); every failure is fail-soft.
            string? legacyPath = historyEnabled ? await saveImageAsync() : null;
            var legacy = await writeRowAsync(historyEnabled, legacyPath);
            return new ImageItemPersistResult(
                ImageItemPersistOutcome.Committed, legacyPath, legacy.Row.Id,
                legacy.FreshCopyClaim, legacy.RearmSelections);
        }

        if (!historyEnabled)
            return new ImageItemPersistResult(ImageItemPersistOutcome.HistoryDisabled, null, null, null, null);

        var imagePath = await saveImageAsync();
        if (imagePath == null)
            return new ImageItemPersistResult(ImageItemPersistOutcome.SaveFailed, null, null, null, null);

        var write = await writeRowAsync(historyEnabled, imagePath);
        switch (write.Row.Disposition)
        {
            case RowWriteDisposition.Committed:
                return new ImageItemPersistResult(
                    ImageItemPersistOutcome.Committed, imagePath, write.Row.Id,
                    write.FreshCopyClaim, write.RearmSelections);

            case RowWriteDisposition.Disabled:
                // The writer's own gate said off — it provably attempted nothing, so the
                // just-written image file has no owner and is removed (best-effort).
                await tryDeleteImageAsync(imagePath);
                return new ImageItemPersistResult(
                    ImageItemPersistOutcome.HistoryDisabled, null, null,
                    write.FreshCopyClaim, write.RearmSelections);

            default: // AttemptedAmbiguous — resolve by the collision-proof unique path.
                var probe = await probeRowByImagePathAsync(imagePath);
                switch (probe.Outcome)
                {
                    case RowProbeOutcome.Found:
                        // The row DID commit before the failure surfaced — adopt it.
                        return new ImageItemPersistResult(
                            ImageItemPersistOutcome.Committed, imagePath, probe.Id,
                            write.FreshCopyClaim, write.RearmSelections);
                    case RowProbeOutcome.NotFound:
                        await tryDeleteImageAsync(imagePath);
                        return new ImageItemPersistResult(
                            ImageItemPersistOutcome.RowFailed, null, null,
                            write.FreshCopyClaim, write.RearmSelections);
                    default: // QueryFailed — retain the image file; never delete on ambiguity.
                        return new ImageItemPersistResult(
                            ImageItemPersistOutcome.RowAmbiguous, imagePath, null,
                            write.FreshCopyClaim, write.RearmSelections);
                }
        }
    }
}
