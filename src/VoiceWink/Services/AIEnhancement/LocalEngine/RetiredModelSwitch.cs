using Serilog;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>LAI-22: what the card of a retired model in use offers.</summary>
internal enum RetiredOfferKind
{
    None,
    Switch,
    DownloadAndSwitch,
    Switching,
}

/// <summary>LAI-22: the offer's state and its line (null: no offer).</summary>
internal readonly record struct RetiredOffer(RetiredOfferKind Kind, string? Line);

/// <summary>LAI-22: how an accepted switch ended.</summary>
internal enum RetiredSwitchResult
{
    Switched,
    Cancelled,
    DownloadFailed,
    RefusedDuringMaintenance,
    CheckNotSettled,
    TooSlow,
    ChoseAnother,
    Failed,
}

/// <summary>LAI-22: switches from a retired built-in model to this PC's pick on the user's click; call Accept on the UI thread.</summary>
internal sealed class RetiredModelSwitch
{
    private static ILogger Logger => Log.ForContext<RetiredModelSwitch>();

    private readonly Func<string, LocalModelDownloadAttempt> _startDownload;
    private readonly Func<string, bool> _isInstalled;
    private readonly Func<string, bool> _isSelected;
    private readonly Action<string> _useModel;
    private readonly Action<string>? _startFirstUseCheck;
    private readonly Func<string, Task?>? _whenCheckSettled;
    private readonly Func<string, bool>? _isCheckPending;
    private readonly Func<string, int?>? _measuredMs;
    private (string Source, string Target, Task<RetiredSwitchResult> Run)? _pending;
    private (string Source, string Target, RetiredSwitchResult Result)? _last;

    /// <param name="isSelected">Whether a model id is the built-in models' selected model.</param>
    /// <param name="useModel">Makes an installed model the selected one, as a row's Select does.</param>
    /// <param name="startFirstUseCheck">The engine's first-use check, as a finished download runs it.</param>
    /// <param name="whenCheckSettled">The running first-use check's completion, or null when none runs.</param>
    /// <param name="isCheckPending">Whether the model's first-use check is still owed or running.</param>
    /// <param name="measuredMs">The model's measured time on this PC, or null.</param>
    internal RetiredModelSwitch(
        Func<string, LocalModelDownloadAttempt> startDownload, Func<string, bool> isInstalled,
        Func<string, bool> isSelected, Action<string> useModel,
        Action<string>? startFirstUseCheck = null, Func<string, Task?>? whenCheckSettled = null,
        Func<string, bool>? isCheckPending = null, Func<string, int?>? measuredMs = null)
    {
        _startDownload = startDownload;
        _isInstalled = isInstalled;
        _isSelected = isSelected;
        _useModel = useModel;
        _startFirstUseCheck = startFirstUseCheck;
        _whenCheckSettled = whenCheckSettled;
        _isCheckPending = isCheckPending;
        _measuredMs = measuredMs;
    }

    /// <summary>The model a running switch away from <paramref name="source"/> goes to, or null.</summary>
    internal string? PendingTargetFor(string source)
        => _pending is { } p && string.Equals(p.Source, source, StringComparison.Ordinal) ? p.Target : null;

    /// <summary>The running switch away from <paramref name="source"/>, or null.</summary>
    internal Task<RetiredSwitchResult>? PendingRunFor(string source)
        => _pending is { } p && string.Equals(p.Source, source, StringComparison.Ordinal) ? p.Run : null;

    /// <summary>Why the last switch away from <paramref name="source"/> was not made, kept so a rebuilt card still says it; null once it no longer holds.</summary>
    /// <param name="currentTarget">The pick the card offers now; null skips that test.</param>
    internal string? LastStatusFor(string source, string? currentTarget = null)
    {
        if (_last is not { } l || !string.Equals(l.Source, source, StringComparison.Ordinal))
            return null;
        // Dropped for good once its cause is gone: another selection, another pick, the pick installed or checked since.
        if (!_isSelected(source)
            || (currentTarget is not null && !string.Equals(l.Target, currentTarget, StringComparison.Ordinal))
            || (l.Result is RetiredSwitchResult.DownloadFailed or RetiredSwitchResult.RefusedDuringMaintenance && _isInstalled(l.Target))
            || (l.Result == RetiredSwitchResult.CheckNotSettled && (!_isInstalled(l.Target) || _isCheckPending?.Invoke(l.Target) == false)))
        {
            _last = null;
            return null;
        }
        return StatusLine(l.Result, LocalModelCatalog.Find(l.Target)?.DisplayName ?? l.Target);
    }

    /// <summary>What a model's card offers: only a retired model in use, and only while this PC has a pick.</summary>
    internal static RetiredOffer Offer(LocalModelEntry entry, bool inUse, LocalModelEntry? pick, bool pickInstalled,
        LocalModelEntry? pendingTarget)
    {
        if (!entry.Retired || !inUse)
            return default;
        if (pendingTarget is not null)
            return new(RetiredOfferKind.Switching, $"Switches to {pendingTarget.DisplayName} once it is ready.");
        if (pick is null || pick.Retired)
            return default;
        return new(pickInstalled ? RetiredOfferKind.Switch : RetiredOfferKind.DownloadAndSwitch,
            $"{pick.DisplayName} is recommended for this PC.");
    }

    /// <summary>The line the retired model's card shows when a switch ended without switching; null where the user chose it or another card says it.</summary>
    internal static string? StatusLine(RetiredSwitchResult result, string pickName) => result switch
    {
        RetiredSwitchResult.DownloadFailed => $"{pickName} did not download. Try again.",
        RetiredSwitchResult.RefusedDuringMaintenance => "An update or data erasure is running. Try again after it finishes.",
        RetiredSwitchResult.CheckNotSettled => $"{pickName} could not be checked yet. Try again.",
        RetiredSwitchResult.Failed => $"Could not switch to {pickName}. Try again.",
        _ => null,
    };

    /// <summary>Starts the switch, or returns the one already running for this model; the task never faults.</summary>
    internal Task<RetiredSwitchResult> Accept(string source, string target)
    {
        if (PendingRunFor(source) is { } running)
            return running;
        var done = new TaskCompletionSource<RetiredSwitchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _last = null;
        _pending = (source, target, done.Task);
        _ = RunAsync(source, target, done);
        return done.Task;
    }

    private async Task RunAsync(string source, string target, TaskCompletionSource<RetiredSwitchResult> done)
    {
        var result = RetiredSwitchResult.Failed;
        try
        {
            result = await SwitchAsync(source, target);
        }
        catch (Exception ex)
        {
            Logger.Warning("Retired model switch failed ({ErrorType}); nothing changed", ex.GetType().Name);
        }
        finally
        {
            Logger.Information("Retired model switch from {RetiredModelId} to {LocalModelId}: {Result}", source, target, result);
            // A later click on another retired model owns the slot by now.
            if (_pending is { } p && ReferenceEquals(p.Run, done.Task))
            {
                _pending = null;
                _last = _isSelected(source) ? (source, target, result) : null;
            }
            done.TrySetResult(result);
        }
    }

    private async Task<RetiredSwitchResult> SwitchAsync(string source, string target)
    {
        LocalModelDownloadAttempt? attempt = null;
        if (!_isInstalled(target))
        {
            attempt = _startDownload(target);
            LocalModelDownloadOutcome outcome;
            try
            {
                outcome = await attempt.Completion;
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                return RetiredSwitchResult.Cancelled;
            }
            catch (Exception)
            {
                return RetiredSwitchResult.DownloadFailed;   // the store logged the cause
            }
            // A Cancel that lands after the store's last token check still completes Installed.
            if (attempt.IsCancellationRequested)
                return RetiredSwitchResult.Cancelled;
            if (outcome == LocalModelDownloadOutcome.RefusedDuringMaintenance)
                return RetiredSwitchResult.RefusedDuringMaintenance;
            if (outcome != LocalModelDownloadOutcome.Installed)
                return RetiredSwitchResult.DownloadFailed;
        }
        _startFirstUseCheck?.Invoke(target);
        if (_whenCheckSettled?.Invoke(target) is { } check)
        {
            try
            {
                await check;
            }
            catch (Exception)
            {
                return RetiredSwitchResult.CheckNotSettled;
            }
        }
        // A Cancel that landed while the check ran still counts: nothing is switched after a Cancel.
        if (attempt?.IsCancellationRequested == true)
            return RetiredSwitchResult.Cancelled;
        if (!_isInstalled(target))
            return RetiredSwitchResult.DownloadFailed;
        if (_isCheckPending?.Invoke(target) == true)
            return RetiredSwitchResult.CheckNotSettled;
        if (LocalModelRecommendation.IsTooSlow(_measuredMs?.Invoke(target)))
            return RetiredSwitchResult.TooSlow;
        if (!_isSelected(source))
            return RetiredSwitchResult.ChoseAnother;
        _useModel(target);
        return RetiredSwitchResult.Switched;
    }
}
