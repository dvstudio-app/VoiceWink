using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Entities;
using VoiceWink.Services.Data;
using VoiceWink.Services.Maintenance;
using VoiceWink.Services.System;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>LAI-32: how a tracked dictation's late enhancement ended.</summary>
internal enum FirstUseFollowUpKind
{
    Ready,
    NotMade,
}

/// <summary>LAI-32: what a click on the ready pill did.</summary>
internal enum FirstUseCopyResult
{
    NotReady,
    Copied,
    ClipboardFailed,
}

/// <summary>LAI-32: one dictation refused during a first-use check; <see cref="Text"/> is the text the enhancement would have received.</summary>
internal sealed record FirstUseFollowUpRequest(
    string Text,
    CustomPrompt? Prompt,
    IReadOnlyList<string>? VocabularyTerms,
    string ModelId,
    int? HistoryId,
    Task<FirstUseCheckOutcome> Settled);

/// <summary>LAI-32: the follow-up's result, stamped with the generation it belongs to.</summary>
internal sealed record FirstUseFollowUpResult(long Generation, FirstUseFollowUpKind Kind);

/// <summary>LAI-32: the pill's decisions and copy, pure so the hub classes only call them.</summary>
internal static class FirstUseFollowUpPolicy
{
    /// <summary>Replaces the fallback pill while the check runs (desk decision 2).</summary>
    internal const string WaitingMessage = "Transcript pasted — AI version follows";
    internal const string ReadyMessage = "AI version ready — click to copy";
    internal const string NotMadeMessage = "No AI version — transcript stays as pasted";
    internal const string CopyCue = "Click to copy the AI version";

    /// <summary>Only a landed paste with the warming redo armed promises a follow-up; every other presentation stays authoritative.</summary>
    internal static bool ShouldTrack(bool warmingUp, bool pasteLanded, bool partialDecodeRetry)
        => warmingUp && pasteLanded && !partialDecodeRetry;

    /// <summary>A copy result shows only on the pill that was clicked: the recorder idle and no newer presentation since the click.</summary>
    internal static bool ShowsCopyResult(bool recorderIdle, long epochAtClick, long epochNow)
        => recorderIdle && epochAtClick == epochNow;

    /// <summary>How a result is shown: a ready result retires the source redo, a not-made one re-arms it with its own line.</summary>
    internal static FirstUseFollowUpPresentation Present(FirstUseFollowUpKind kind, bool sourceRedoStillArmed)
        => kind == FirstUseFollowUpKind.Ready
            ? new(ClearSourceRedo: sourceRedoStillArmed, RearmSourceRedo: false, ReadyMessage, OffersCopy: true)
            : new(ClearSourceRedo: false, RearmSourceRedo: sourceRedoStillArmed, NotMadeMessage, OffersCopy: false);
}

internal readonly record struct FirstUseFollowUpPresentation(
    bool ClearSourceRedo, bool RearmSourceRedo, string Message, bool OffersCopy);

/// <summary>
/// LAI-32: finishes the enhancement of the newest dictation a built-in model's first-use check refused,
/// once the check settles. The result is offered for copying and saved to History; it never replaces pasted text (PST-6).
/// </summary>
public sealed class FirstUseEnhancementFollowUp : IDisposable
{
    private static ILogger Logger => Log.ForContext<FirstUseEnhancementFollowUp>();

    internal const string FailedMarker = "[Enhancement failed]";

    private readonly Func<string, CustomPrompt?, string, IReadOnlyList<string>?, CancellationToken, Task<string>> _enhance;
    private readonly Func<int, CancellationToken, Task<TranscriptionRecord?>> _readRow;
    private readonly Func<TranscriptionRecord, CancellationToken, Task> _writeRow;
    private readonly Func<bool> _isHistoryEnabled;
    private readonly IMaintenanceGate _gate;
    private readonly Func<string, Task<bool>> _copy;
    private readonly object _lock = new();
    private Func<bool> _isPipelineIdle;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private long _generation;
    private string? _readyText;
    private bool _disposed;

    public FirstUseEnhancementFollowUp(AIEnhancementService enhancement, TranscriptionHistoryService history,
        IMaintenanceGate gate, ClipboardService clipboard, SettingsService settings)
        : this(
            (text, prompt, modelId, vocab, ct) => enhancement.EnhanceWithModelAsync(text, prompt, modelId, vocab, AIProvider.OnThisPc, ct),
            (id, ct) => history.GetByIdAsync(id, ct),
            (row, ct) => history.UpdateAsync(row, ct),
            () => settings.GetBool(AppDefaults.IsHistoryEnabled, true),
            gate,
            clipboard.SetClipboardForPasteAsync,
            isPipelineIdle: () => false)
    {
    }

    /// <summary>Test seam: every collaborator a delegate; no clipboard, no database.</summary>
    internal FirstUseEnhancementFollowUp(
        Func<string, CustomPrompt?, string, IReadOnlyList<string>?, CancellationToken, Task<string>> enhance,
        Func<int, CancellationToken, Task<TranscriptionRecord?>> readRow,
        Func<TranscriptionRecord, CancellationToken, Task> writeRow,
        Func<bool> isHistoryEnabled,
        IMaintenanceGate gate,
        Func<string, Task<bool>> copy,
        Func<bool> isPipelineIdle)
    {
        _enhance = enhance;
        _readRow = readRow;
        _writeRow = writeRow;
        _isHistoryEnabled = isHistoryEnabled;
        _gate = gate;
        _copy = copy;
        _isPipelineIdle = isPipelineIdle;
    }

    /// <summary>Raised off the UI thread, once per current generation, after the cleanup lease is released.</summary>
    internal event Action<FirstUseFollowUpResult>? Completed;

    /// <summary>The live recording-pipeline probe; read each time the follow-up would run.</summary>
    internal void AttachPipeline(Func<bool> isPipelineIdle) => _isPipelineIdle = isPipelineIdle;

    /// <summary>The run a test awaits; null when nothing was tracked.</summary>
    internal Task? RunningForTest { get; private set; }

    /// <summary>Replaces any earlier follow-up (last wins) and returns this one's generation.</summary>
    internal long Track(FirstUseFollowUpRequest request)
    {
        CancellationToken token;
        long generation;
        lock (_lock)
        {
            if (_disposed)
                return 0;
            generation = ++_generation;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _readyText = null;
            token = _cts.Token;
        }
        Logger.Information("First-use follow-up tracked for {LocalModelId}", request.ModelId);
        RunningForTest = RunAsync(request, generation, token);
        return generation;
    }

    /// <summary>True while <paramref name="generation"/> is the newest tracked follow-up and nothing cancelled it.</summary>
    internal bool IsCurrent(long generation)
    {
        lock (_lock)
            return !_disposed && generation == _generation && _cts is { IsCancellationRequested: false };
    }

    /// <summary>Drops the follow-up and its ready text (a text-covering History delete, quit).</summary>
    internal void Cancel()
    {
        lock (_lock)
        {
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _readyText = null;
        }
    }

    /// <summary>The recording pipeline went idle: a follow-up waiting to run re-reads the probe.</summary>
    internal void OnPipelineIdle()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            signal = _idle;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    internal async Task<FirstUseCopyResult> CopyReadyTextAsync()
    {
        string? text;
        lock (_lock)
            text = _readyText;
        if (text is null)
            return FirstUseCopyResult.NotReady;
        return await _copy(text).ConfigureAwait(false) ? FirstUseCopyResult.Copied : FirstUseCopyResult.ClipboardFailed;
    }

    private async Task RunAsync(FirstUseFollowUpRequest request, long generation, CancellationToken ct)
    {
        try
        {
            // LAI-32: Completed never fires inside Track, so the caller's track record exists first.
            await Task.Yield();
            var outcome = await request.Settled.WaitAsync(ct).ConfigureAwait(false);
            if (outcome != FirstUseCheckOutcome.Ready)
            {
                Logger.Information("First-use follow-up: the check ended without a usable model - not made");
                Publish(generation, FirstUseFollowUpKind.NotMade);
                return;
            }
            await WaitForIdleAsync(ct).ConfigureAwait(false);
            // K1: the enhancement writes prompt traces, so the lease covers it as well as the History write.
            if (!_gate.TryBeginCleanupPass(out var lease))
            {
                Logger.Information("First-use follow-up dropped - an update or a data deletion is under way");
                return;
            }
            FirstUseFollowUpKind kind;
            using (lease)
            {
                if (!IsCurrent(generation))
                    return;
                kind = await EnhanceAndSaveAsync(request, generation, ct).ConfigureAwait(false);
            }
            Publish(generation, kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warning("First-use follow-up failed: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task<FirstUseFollowUpKind> EnhanceAndSaveAsync(FirstUseFollowUpRequest request, long generation, CancellationToken ct)
    {
        string text;
        try
        {
            text = await _enhance(request.Text, request.Prompt, request.ModelId, request.VocabularyTerms, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("First-use follow-up enhancement failed: {ErrorType}", ex.GetType().Name);
            return FirstUseFollowUpKind.NotMade;
        }
        if (string.IsNullOrWhiteSpace(text))
            return FirstUseFollowUpKind.NotMade;
        lock (_lock)
        {
            if (!IsCurrentLocked(generation))
                throw new OperationCanceledException(ct);
            _readyText = text;
        }
        await SaveToHistoryAsync(request.HistoryId, text, generation, ct).ConfigureAwait(false);
        return FirstUseFollowUpKind.Ready;
    }

    private async Task SaveToHistoryAsync(int? historyId, string text, long generation, CancellationToken ct)
    {
        // K6: History may have been switched off while the check ran.
        if (historyId is not { } id || !_isHistoryEnabled())
            return;
        try
        {
            var row = await _readRow(id, ct).ConfigureAwait(false);
            if (row is null || row.EnhancedText != FailedMarker || !IsCurrent(generation))
                return;
            row.EnhancedText = text;
            await _writeRow(row, ct).ConfigureAwait(false);
            Logger.Information("First-use follow-up saved the AI version to History");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            Logger.Information("First-use follow-up: the History row was deleted - not saved");
        }
        catch (Exception ex)
        {
            Logger.Warning("First-use follow-up could not save to History: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
                signal = _idle.Task;
            if (_isPipelineIdle())
                return;
            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private bool IsCurrentLocked(long generation)
        => !_disposed && generation == _generation && _cts is { IsCancellationRequested: false };

    private void Publish(long generation, FirstUseFollowUpKind kind)
    {
        if (!IsCurrent(generation))
            return;
        Logger.Information("First-use follow-up finished: {Kind}", kind);
        Completed?.Invoke(new FirstUseFollowUpResult(generation, kind));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts?.Cancel();
            _cts = null;
            _readyText = null;
        }
        OnPipelineIdle();
    }
}
