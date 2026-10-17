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
/// bundles, which <c>ModelDownloadManager</c> restarts from zero rather than resuming. A Cancel (the
/// AI Enhancement page, the Downloads card) ends the job for good, and turns off an AI switch that
/// would otherwise fail every dictation. A failure keeps it for ONE more attempt at the next
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
    private readonly Action? _switchAiOff;
    private readonly Func<AIProvider>? _activeTextProvider;
    private readonly Func<string?>? _activeBuiltInModel;
    private readonly Func<bool>? _activePromptIsImage;
    private readonly HashSet<LocalModelDownloadAttempt> _watched = new();
    private bool _resumed;

    /// <param name="useModel">Takes the model into use: provider, model, the AI switch ON, residency.</param>
    /// <param name="startFirstUseCheck">The engine's first-use GPU check, as a finished download on the AI page runs it.</param>
    /// <param name="switchAiOff">Turns the AI switch off, as the AI page's switch does.</param>
    /// <param name="activeTextProvider">The provider the ACTIVE prompt cleans with (its override first).</param>
    /// <param name="activeBuiltInModel">The built-in model the active prompt would use, or null.</param>
    /// <param name="activePromptIsImage">Whether the active prompt generates images (it uses no text model).</param>
    internal BuiltInModelAdoption(SettingsService settings, LocalModelStore store,
        Action<string> useModel, Action<string>? startFirstUseCheck, Action? switchAiOff = null,
        Func<AIProvider>? activeTextProvider = null, Func<string?>? activeBuiltInModel = null,
        Func<bool>? activePromptIsImage = null)
        : this(settings, store.StartDownload, store.IsInstalled, useModel, startFirstUseCheck, switchAiOff,
               activeTextProvider, activeBuiltInModel, activePromptIsImage)
    {
        // Every Cancel, not only of the Express job's download (Codex diff r1).
        store.DownloadCancelled += _ => SwitchAiOffIfNothingCanServe();
    }

    /// <summary>Test seam: the store's two members as delegates, so an attempt's outcome can be set.</summary>
    internal BuiltInModelAdoption(SettingsService settings,
        Func<string, LocalModelDownloadAttempt> startDownload, Func<string, bool> isInstalled,
        Action<string> useModel, Action<string>? startFirstUseCheck, Action? switchAiOff = null,
        Func<AIProvider>? activeTextProvider = null, Func<string?>? activeBuiltInModel = null,
        Func<bool>? activePromptIsImage = null)
    {
        _settings = settings;
        _activeTextProvider = activeTextProvider;
        _activeBuiltInModel = activeBuiltInModel;
        _activePromptIsImage = activePromptIsImage;
        _startDownload = startDownload;
        _isInstalled = isInstalled;
        _useModel = useModel;
        _startFirstUseCheck = startFirstUseCheck;
        _switchAiOff = switchAiOff;
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
                SwitchAiOffIfNothingCanServe();
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
            // A Cancel (the AI page, the Downloads card): the user ended it, so it is not restarted
            // at the next launch.
            Logger.Information("Express setup: built-in model download cancelled; not restarted");
            SwitchAiOffIfNothingCanServe();
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

    /// <summary>
    /// After a Cancel: an AI switch turned ON meanwhile (the AI page's, during the download) is
    /// turned off when the built-in models are the provider and no installed one is selected —
    /// left on, every dictation would fail "No AI model selected" (owner, 2026-10-17). A switch
    /// that is off, another provider, or an installed model in use is left as it is.
    /// </summary>
    internal void SwitchAiOffIfNothingCanServe()
    {
        // An active image prompt uses no text model, so a text model's Cancel cannot fail it (Codex r2).
        if (!_settings.GetBool(AppDefaults.AiEnhancementEnabled) || _activePromptIsImage?.Invoke() == true)
            return;
        // The ACTIVE prompt decides, its own provider and model overrides included (Kimi diff r1):
        // a prompt routed to another provider leaves the switch alone, and a prompt on the built-in
        // models is judged by its own model. The global settings are the fallback where no
        // service is wired (the test seam).
        var onBuiltIn = _activeTextProvider is not null
            ? _activeTextProvider() == AIProvider.OnThisPc
            : string.Equals(_settings.GetString(AppDefaults.AiProvider, ""), nameof(AIProvider.OnThisPc), StringComparison.Ordinal);
        if (!onBuiltIn)
            return;
        var current = _activeBuiltInModel is not null
            ? _activeBuiltInModel() ?? ""
            : _settings.GetString(AppDefaults.AiModelKey(AIProvider.OnThisPc), "");
        if (!string.IsNullOrEmpty(current) && _isInstalled(current))
            return;
        Logger.Information("Express setup: AI enhancement switched off; its model download was cancelled");
        _switchAiOff?.Invoke();
    }

    private void Clear()
    {
        _settings.Remove(AppDefaults.ExpressBuiltInPending);
        _settings.Flush();
    }
}
