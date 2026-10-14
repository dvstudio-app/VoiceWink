using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LNC-15: finishes what Express setup started after the setup page is gone — downloads the
/// built-in AI model for this PC and, once it is in, takes it into use and switches AI clean-up on
/// (owner, 2026-10-13: "Yes, both, AI on").
///
/// <para><b>AI clean-up stays OFF until the model is in.</b> Switching it on beside a missing model
/// would fail every early dictation; with it off they paste the plain transcript. A switch stored ON
/// when the job starts is removed (not stored OFF, which would read as the user's choice).</para>
///
/// <para><b>It never overrides a choice made meanwhile.</b> At completion it adopts only while the
/// job is still pending (a "Reset all settings" removes it), the text provider is still the built-in
/// models, the AI switch is not stored OFF, and no installed built-in model is selected. A switch
/// stored ON is not a contrary choice: it is the half-set state this completes — the AI page's
/// switch turned on while the download runs — and leaving it would fail every dictation with
/// "No AI model selected".</para>
///
/// <para><b>A restart mid-download starts it again.</b> The model id is kept in
/// <see cref="AppDefaults.ExpressBuiltInPending"/> until the job ends;
/// <see cref="ResumePending"/> runs once per launch from <c>App.StartGatedRuntimeServices</c>
/// (the UI thread, behind the legal gate — the LIC-23 scheduler's seat). The built-in models are
/// bundles, which <c>ModelDownloadManager</c> restarts from zero rather than resuming. A Cancel on the
/// AI Enhancement page ends the job for good. A failure keeps it for ONE more attempt at the next
/// start; a resumed attempt that fails too drops it, so a model that cannot be fetched is not
/// downloaded again at every launch.</para>
///
/// <para>Call <see cref="Start"/> and <see cref="ResumePending"/> on the UI thread:
/// <see cref="LocalModelStore.StartDownload"/> reports progress on the thread that started it, and
/// the AI page's rows render that progress (Grok plan review, Blocker 2). Nothing ever awaits the
/// download: <see cref="Start"/> returns once the attempt is queued.</para>
/// </summary>
internal sealed class BuiltInModelAdoption
{
    private static ILogger Logger => Log.ForContext<BuiltInModelAdoption>();

    private readonly SettingsService _settings;
    private readonly Func<string, LocalModelDownloadAttempt> _startDownload;
    private readonly Func<string, bool> _isInstalled;
    private readonly Action<string> _useModel;
    private readonly Action<string>? _startFirstUseCheck;
    private readonly HashSet<LocalModelDownloadAttempt> _watched = new();
    private bool _resumed;

    /// <param name="useModel">Takes the model into use: provider, model, the AI switch ON, residency.</param>
    /// <param name="startFirstUseCheck">The engine's first-use GPU check, as a finished download on the AI page runs it.</param>
    internal BuiltInModelAdoption(SettingsService settings, LocalModelStore store,
        Action<string> useModel, Action<string>? startFirstUseCheck)
        : this(settings, store.StartDownload, store.IsInstalled, useModel, startFirstUseCheck)
    {
    }

    /// <summary>Test seam: the store's two members as delegates, so an attempt's outcome can be set.</summary>
    internal BuiltInModelAdoption(SettingsService settings,
        Func<string, LocalModelDownloadAttempt> startDownload, Func<string, bool> isInstalled,
        Action<string> useModel, Action<string>? startFirstUseCheck)
    {
        _settings = settings;
        _startDownload = startDownload;
        _isInstalled = isInstalled;
        _useModel = useModel;
        _startFirstUseCheck = startFirstUseCheck;
    }

    /// <summary>Takes an installed model into use now — Express setup when the pick is already on disk.</summary>
    internal void UseNow(string id) => _useModel(id);

    /// <summary>
    /// Records the job, starts (or joins) the download and watches it. Returns the watch — callers
    /// discard it; tests await it. A second call for an attempt already watched watches nothing new.
    /// </summary>
    internal Task Start(string id) => StartCore(id, resumed: false);

    private Task StartCore(string id, bool resumed)
    {
        _settings.SetString(AppDefaults.ExpressBuiltInPending, id);
        // AI waits OFF for the model (Codex diff r1): a switch stored ON — the AI step's, before
        // Back to Express — would fail every dictation "No AI model selected" until the download
        // ends. Removing it restores the OFF default without storing the OFF choice that would
        // stop the adoption.
        if (_settings.GetBool(AppDefaults.AiEnhancementEnabled))
            _settings.Remove(AppDefaults.AiEnhancementEnabled);
        _settings.Flush();
        var attempt = _startDownload(id);
        if (!_watched.Add(attempt))
        {
            return Task.CompletedTask;
        }
        Logger.Information("Express setup: downloading the built-in model {LocalModelId}", id);
        return WatchAsync(id, attempt, resumed);
    }

    /// <summary>
    /// Once per process: restart a job a previous session left unfinished, or drop it when the user
    /// has chosen something since.
    /// </summary>
    internal Task ResumePending()
    {
        if (_resumed)
        {
            return Task.CompletedTask;
        }
        _resumed = true;

        var id = _settings.GetString(AppDefaults.ExpressBuiltInPending, "");
        if (string.IsNullOrEmpty(id))
        {
            return Task.CompletedTask;
        }
        if (LocalModelCatalog.Find(id) is null || !ShouldAdopt())
        {
            Logger.Information("Express setup: dropping the pending built-in model download (no longer wanted)");
            Clear();
            return Task.CompletedTask;
        }
        Logger.Information("Express setup: restarting the built-in model download {LocalModelId}", id);
        return StartCore(id, resumed: true);
    }

    /// <summary>The user has not chosen anything about AI since Express started the job.</summary>
    internal bool ShouldAdopt()
    {
        if (!string.Equals(_settings.GetString(AppDefaults.AiProvider, ""), nameof(AIProvider.OnThisPc), StringComparison.Ordinal))
        {
            return false;
        }
        if (_settings.Contains(AppDefaults.AiEnhancementEnabled) && !_settings.GetBool(AppDefaults.AiEnhancementEnabled))
        {
            return false;
        }
        var current = _settings.GetString(AppDefaults.AiModelKey(AIProvider.OnThisPc), "");
        return string.IsNullOrEmpty(current) || !_isInstalled(current);
    }

    private bool IsPending(string id)
        => string.Equals(_settings.GetString(AppDefaults.ExpressBuiltInPending, ""), id, StringComparison.Ordinal);

    private async Task WatchAsync(string id, LocalModelDownloadAttempt attempt, bool resumed)
    {
        try
        {
            // The same outcome contract the AI page's rows read (LocalModelsSection.Watch): an
            // outcome for Installed / refused / unknown, a throw for cancel and failure.
            var outcome = await attempt.Completion;
            // A Cancel can land after the store's last token check, or before this continuation
            // runs, and the attempt still completes Installed (Codex diff r1): the Cancel decides.
            if (attempt.IsCancellationRequested)
            {
                Logger.Information("Express setup: built-in model download cancelled; not restarted");
                Clear();
                return;
            }
            switch (outcome)
            {
                case LocalModelDownloadOutcome.Installed:
                    _startFirstUseCheck?.Invoke(id);
                    if (IsPending(id) && ShouldAdopt())
                    {
                        _useModel(id);
                        Logger.Information("Express setup: built-in model {LocalModelId} in use, AI clean-up on", id);
                    }
                    else
                    {
                        Logger.Information("Express setup: built-in model {LocalModelId} downloaded; AI was chosen meanwhile, left as chosen", id);
                    }
                    Clear();
                    break;
                case LocalModelDownloadOutcome.RefusedDuringMaintenance:
                    // An update or erasure held the lease; the next launch tries again.
                    break;
                default:
                    Clear();
                    break;
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            // A Cancel on the AI page: the user ended it, so it is not restarted at the next launch.
            Logger.Information("Express setup: built-in model download cancelled; not restarted");
            Clear();
        }
        catch (Exception ex)
        {
            // The store has logged the cause once.
            if (resumed)
            {
                Logger.Warning("Express setup: built-in model download failed again ({ErrorType}); not retried",
                    ex.GetType().Name);
                Clear();
            }
            else
            {
                Logger.Warning("Express setup: built-in model download did not finish ({ErrorType}); retried once at the next start",
                    ex.GetType().Name);
            }
        }
        finally
        {
            _watched.Remove(attempt);
        }
    }

    private void Clear()
    {
        _settings.Remove(AppDefaults.ExpressBuiltInPending);
        _settings.Flush();
    }
}
