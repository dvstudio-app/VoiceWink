using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// How a history row write ended (IMG-3). <see cref="Disabled"/> and
/// <see cref="AttemptedAmbiguous"/> both surfaced as a bare null id before — but they
/// demand opposite reconciliation: a Disabled write provably attempted NOTHING (safe to
/// delete the item's PNG), while an ambiguous one may have COMMITTED before the failure
/// (the ReferencePersistence doctrine — never destroy what a committed row references
/// without a query).
/// </summary>
public enum RowWriteDisposition
{
    /// <summary>History was off at the writer's own gate — no save was attempted.</summary>
    Disabled,

    /// <summary>The row committed; <see cref="RowWriteResult.Id"/> carries its id.</summary>
    Committed,

    /// <summary>The save threw — the row MAY have committed (fail-soft, logged).</summary>
    AttemptedAmbiguous,
}

/// <summary>A row write's disposition plus the committed id (null unless Committed).</summary>
public readonly record struct RowWriteResult(RowWriteDisposition Disposition, int? Id);

/// <summary>
/// Persists <see cref="TranscriptionRecord"/> instances to history, gated on the
/// <c>IsHistoryEnabled</c> setting, with exceptions logged and swallowed (a matching
/// cancellation is the one exception — it rethrows).
/// Consolidates five nearly-identical call sites that used to live inside
/// <c>MainViewModel</c>. Callers build the record (shapes differ by path:
/// normal text, image generation, redo success, redo failure, etc.) and pass
/// it here; this service decides whether to save and reports the resulting id.
/// </summary>
public sealed class TranscriptionHistoryWriter
{
    private static ILogger Logger => Log.ForContext<TranscriptionHistoryWriter>();

    private readonly TranscriptionHistoryService _history;
    private readonly SettingsService _settings;

    public TranscriptionHistoryWriter(TranscriptionHistoryService history, SettingsService settings)
    {
        _history = history;
        _settings = settings;
    }

    /// <summary>
    /// Persist a transcription record if history is enabled. Returns the saved record's
    /// id on success, or null if history is disabled or the save failed. Errors are
    /// logged; callers keep running — EXCEPT a matching cancellation, which rethrows:
    /// mapping it to null would let a cancelled pipeline (and ReferencePersistence's
    /// ownership reconciliation above this) mistake the cancel for a plain save
    /// failure and keep running (Codex post-#167 audit).
    /// </summary>
    public async Task<int?> TryWriteAsync(TranscriptionRecord record, CancellationToken ct = default)
        => (await TryWriteWithDispositionAsync(record, ct).ConfigureAwait(false)).Id;

    /// <summary>
    /// <see cref="TryWriteAsync"/> with the disposition preserved (IMG-3): the batch
    /// persistence contract needs to tell "History was off — nothing attempted" apart
    /// from "the save threw — the row may have committed", which the nullable id
    /// conflates. Same gate, same fail-soft logging, same matching-cancel rethrow.
    /// </summary>
    public async Task<RowWriteResult> TryWriteWithDispositionAsync(TranscriptionRecord record, CancellationToken ct = default)
    {
        if (!_settings.GetBool(AppDefaults.IsHistoryEnabled, true))
            return new RowWriteResult(RowWriteDisposition.Disabled, null);

        try
        {
            await _history.SaveAsync(record, ct).ConfigureAwait(false);
            return new RowWriteResult(RowWriteDisposition.Committed, record.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save transcription to history");
            return new RowWriteResult(RowWriteDisposition.AttemptedAmbiguous, null);
        }
    }
}
