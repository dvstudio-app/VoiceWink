using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Enums;
using VoiceWink.Services.System;
using VoiceWink.Services.Transcription;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for model management.
/// </summary>
public partial class ModelManagementViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<ModelManagementViewModel>();

    private readonly ModelDownloadManager _downloadManager;
    // The local-runtime seam, not a concrete engine — see LocalModelPreparer.
    private readonly LocalModelPreparer _localModels;
    private readonly SettingsService _settings;
    private readonly Services.Transcription.IParakeetPcppBackend? _pcppBackend;
    private readonly AppRestartService? _appRestart;

    [ObservableProperty]
    private ObservableCollection<ModelItemViewModel> _models = new();

    [ObservableProperty]
    private string _selectedModelName = "";

    [ObservableProperty]
    private string _selectedLanguage = "en";

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _downloadingModelName = "";

    [ObservableProperty]
    private double _downloadProgress;

    public static readonly string[] SupportedLanguages =
    [
        "auto", "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl",
        "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi", "he", "uk", "el",
        "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt",
        "la", "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl",
        "kn", "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq",
        "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka",
        "be", "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk",
        "nn", "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln",
        "ha", "ba", "jw", "su"
    ];

    /// <summary>
    /// Human-readable display names for Whisper language ISO codes.
    /// Languages not present here fall back to the raw ISO code.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LanguageDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = "Auto-detect",
            ["en"]   = "English",
            ["zh"]   = "Chinese",
            ["de"]   = "German",
            ["es"]   = "Spanish",
            ["ru"]   = "Russian",
            ["ko"]   = "Korean",
            ["fr"]   = "French",
            ["ja"]   = "Japanese",
            ["pt"]   = "Portuguese",
            ["tr"]   = "Turkish",
            ["pl"]   = "Polish",
            ["ca"]   = "Catalan",
            ["nl"]   = "Dutch",
            ["ar"]   = "Arabic",
            ["sv"]   = "Swedish",
            ["it"]   = "Italian",
            ["id"]   = "Indonesian",
            ["hi"]   = "Hindi",
            ["fi"]   = "Finnish",
            ["vi"]   = "Vietnamese",
            ["he"]   = "Hebrew",
            ["uk"]   = "Ukrainian",
            ["el"]   = "Greek",
            ["ms"]   = "Malay",
            ["cs"]   = "Czech",
            ["ro"]   = "Romanian",
            ["da"]   = "Danish",
            ["hu"]   = "Hungarian",
            ["ta"]   = "Tamil",
            ["no"]   = "Norwegian",
            ["th"]   = "Thai",
            ["ur"]   = "Urdu",
            ["hr"]   = "Croatian",
            ["bg"]   = "Bulgarian",
            ["lt"]   = "Lithuanian",
            ["la"]   = "Latin",
            ["mi"]   = "Maori",
            ["ml"]   = "Malayalam",
            ["cy"]   = "Welsh",
            ["sk"]   = "Slovak",
            ["te"]   = "Telugu",
            ["fa"]   = "Persian",
            ["lv"]   = "Latvian",
            ["bn"]   = "Bengali",
            ["sr"]   = "Serbian",
            ["az"]   = "Azerbaijani",
            ["sl"]   = "Slovenian",
            ["kn"]   = "Kannada",
            ["et"]   = "Estonian",
            ["mk"]   = "Macedonian",
            ["br"]   = "Breton",
            ["eu"]   = "Basque",
            ["is"]   = "Icelandic",
            ["hy"]   = "Armenian",
            ["ne"]   = "Nepali",
            ["mn"]   = "Mongolian",
            ["bs"]   = "Bosnian",
            ["kk"]   = "Kazakh",
            ["sq"]   = "Albanian",
            ["sw"]   = "Swahili",
            ["gl"]   = "Galician",
            ["mr"]   = "Marathi",
            ["pa"]   = "Punjabi",
            ["si"]   = "Sinhala",
            ["km"]   = "Khmer",
            ["sn"]   = "Shona",
            ["yo"]   = "Yoruba",
            ["so"]   = "Somali",
            ["af"]   = "Afrikaans",
            ["oc"]   = "Occitan",
            ["ka"]   = "Georgian",
            ["be"]   = "Belarusian",
            ["tg"]   = "Tajik",
            ["sd"]   = "Sindhi",
            ["gu"]   = "Gujarati",
            ["am"]   = "Amharic",
            ["yi"]   = "Yiddish",
            ["lo"]   = "Lao",
            ["uz"]   = "Uzbek",
            ["fo"]   = "Faroese",
            ["ht"]   = "Haitian Creole",
            ["ps"]   = "Pashto",
            ["tk"]   = "Turkmen",
            ["nn"]   = "Nynorsk",
            ["mt"]   = "Maltese",
            ["sa"]   = "Sanskrit",
            ["lb"]   = "Luxembourgish",
            ["my"]   = "Myanmar",
            ["bo"]   = "Tibetan",
            ["tl"]   = "Tagalog",
            ["mg"]   = "Malagasy",
            ["as"]   = "Assamese",
            ["tt"]   = "Tatar",
            ["haw"]  = "Hawaiian",
            ["ln"]   = "Lingala",
            ["ha"]   = "Hausa",
            ["ba"]   = "Bashkir",
            ["jw"]   = "Javanese",
            ["su"]   = "Sundanese",
        };

    /// <summary>
    /// Returns the human-readable display name for a language ISO code,
    /// falling back to the raw code if not found.
    /// </summary>
    public static string GetLanguageDisplayName(string isoCode)
        => LanguageDisplayNames.TryGetValue(isoCode, out var name) ? name : isoCode;

    /// <summary>
    /// UI-11: the GPU acceleration preference, for the Models page row that sits above the local
    /// model list. It is exposed here rather than on <c>SettingsViewModel</c> because
    /// <c>ModelsPage</c> already resolves this ViewModel and AGENTS.md forbids adding a third
    /// <c>App.Services</c> lookup there.
    ///
    /// <para>Both halves delegate to <see cref="GpuAccelerationPreference"/>, which owns the whole
    /// operation — persist, log, re-arm the TRN-50 self-test — and is also what
    /// <c>SettingsViewModel.ResetAllSettings</c> calls. This property adds NO behaviour of its own;
    /// a second implementation of the write here is exactly the split that class exists to
    /// prevent.</para>
    ///
    /// <para>Read-through, deliberately not <c>[ObservableProperty]</c>: the settings file stays
    /// the one source of truth, so nothing can hold a stale copy. <c>ModelsPage</c> is constructed
    /// per navigation, so every render re-reads.</para>
    ///
    /// <para>Setting it does NOT change this session's decoding: the load order is read pre-boot
    /// and frozen at the first native load, which is why an interactive flip is followed by the
    /// TRN-59 restart dialog (UI-12 removed the row's standing restart sentence).</para>
    /// </summary>
    public bool GpuAccelerationEnabled
    {
        get => GpuAccelerationPreference.Read(_settings);
        set => GpuAccelerationPreference.Write(_settings, value);
    }

    /// <summary>TRN-59: the restart the Models page offers after a GPU-acceleration flip — reached
    /// through this ViewModel because the page already resolves it and AGENTS.md forbids a new
    /// <c>App.Services</c> lookup there. Null only in tests (the optional-dependency shape of
    /// <c>pcppBackend</c>); the page then makes no offer, and since UI-12 nothing on the row says
    /// the flip applies at the next start either.</summary>
    public AppRestartService? AppRestart => _appRestart;

    /// <remarks><paramref name="pcppBackend"/> (TRN-29 slice 4): null in every ordinary build.
    /// When registered, a delete of the Parakeet row routes through it so the hidden GGUF
    /// artifact is removed WITH the legacy bundle — otherwise "Delete" would strip sherpa and
    /// silently orphan ~940 MB the Models page cannot see (Step 0 row 11).
    /// <paramref name="appRestart"/> (TRN-59): optional for the same reason — the three-argument
    /// test construction keeps compiling; DI supplies it in the app.</remarks>
    public ModelManagementViewModel(
        ModelDownloadManager downloadManager,
        LocalModelPreparer localModels,
        SettingsService settings,
        Services.Transcription.IParakeetPcppBackend? pcppBackend = null,
        AppRestartService? appRestart = null)
    {
        _downloadManager = downloadManager;
        _localModels = localModels;
        _settings = settings;
        _pcppBackend = pcppBackend;
        _appRestart = appRestart;

        SelectedModelName = _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);
        SelectedLanguage = _settings.GetString(AppDefaults.SelectedLanguage, "auto");

        LoadModels();
    }

    public void LoadModels()
    {
        // Case-insensitive, matching RefreshDownloadedState: a plain List.Contains is ordinal, so an
        // on-disk "GGML-SMALL.bin" would leave the ggml-small row reading "not downloaded".
        var downloaded = new HashSet<string>(
            _downloadManager.GetDownloadedModels(), ModelDiskReconciliation.NameComparer);
        AugmentWithPcppRowState(downloaded);
        Models.Clear();

        foreach (var model in PredefinedModels.Models)
        {
            var isDownloaded = downloaded.Contains(model.Name);
            Models.Add(new ModelItemViewModel
            {
                Name = model.Name,
                DisplayName = model.DisplayName,
                FileSizeBytes = model.FileSizeBytes,
                IsDownloaded = isDownloaded,
                IsSelected = ModelDiskReconciliation.IsSameModel(model.Name, SelectedModelName),
                DownloadUrl = model.DownloadUrl ?? ""
            });
        }
    }

    [RelayCommand]
    public async Task DownloadModelAsync(ModelItemViewModel model)
    {
        if (model.IsDownloaded || model.IsDownloading)
            return;

        var modelInfo = PredefinedModels.Models.FirstOrDefault(m => m.Name == model.Name);
        if (modelInfo == null) return;

        // Availability gate (TRN-1 step 3). PrepareOutcome.Unavailable is far too late to be the only
        // defence: it is raised at PREPARE time, i.e. after the download has already completed, so on
        // a lever-disabled or unsupported machine the user would spend 670 MB and then be told the
        // engine can't run. Both diff reviewers caught the runtime comment claiming this was already
        // handled by ownership — it was not; this is the gate.
        //
        // FAIL CLOSED when nothing claims the name. A first version failed open, reasoning that a
        // dead button with no explanation is unfriendly — but an unclaimed catalog row is a
        // composition defect, `PrepareAsync` answers UnknownModel for it, and the model can
        // therefore NEVER be used. Spending a multi-hundred-megabyte transfer to arrive at that is
        // the more harmful polarity, so it is refused with a message instead (Codex).
        //
        // Inside the try/catch because `TryResolve` THROWS when two runtimes claim one name, and an
        // escaped exception from a RelayCommand is an unobserved-task crash rather than a message.
        try
        {
            var owner = _localModels.TryResolve(modelInfo.Name)?.Runtime;
            if (owner is null || !owner.IsAvailable)
            {
                Logger.Warning(
                    "Download refused for {Name}: {Reason}", modelInfo.Name,
                    owner is null ? "no local runtime claims it" : "its runtime cannot run on this build or machine");
                model.ErrorMessage = "This model can't run on this version of VoiceWink.";
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not determine which runtime owns {Name}; refusing the download", modelInfo.Name);
            model.ErrorMessage = "This model can't run on this version of VoiceWink.";
            return;
        }

        // Update-apply gate (UPD-1 Phase 3): don't start a (potentially multi-GB) download while an
        // update is being applied — the apply would interrupt it.
        if (App.IsExclusiveMaintenanceActive())
        {
            model.ErrorMessage = "Update in progress — try again after it finishes";
            return;
        }

        var cts = new CancellationTokenSource();
        model.DownloadCts = cts;

        try
        {
            IsDownloading = true;
            DownloadingModelName = model.DisplayName;
            model.IsDownloading = true;
            model.ErrorMessage = "";

            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                model.DownloadProgress = p;
            });

            await _downloadManager.DownloadModelAsync(modelInfo, progress, cts.Token);

            model.IsDownloaded = true;
            model.IsDownloading = false;
            Logger.Information("Model downloaded: {Name}", model.Name);
        }
        catch (OperationCanceledException)
        {
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            if (cts.Token.IsCancellationRequested)
            {
                Logger.Information("Download cancelled: {Name}", model.Name);
            }
            else
            {
                Logger.Warning("Download stalled (network timeout): {Name}", model.Name);
                model.ErrorMessage = "Download stalled. Check your connection and try again.";
            }
        }
        catch (Services.Transcription.ModelSourceIOException ex) when (ex.InnerException is OperationCanceledException)
        {
            // TRN-33's RemoteAsync re-types a stall/timeout OCE as a typed SOURCE failure so the
            // mirror→upstream fallback filter can tell remote from local — without this arm the
            // same environmental stall would land in the generic catch below at Error (Error+
            // forwards to Sentry) with the generic copy, undoing the deliberate stall handling
            // above (Kimi, verification round). A user cancel still propagates as a plain
            // OperationCanceledException and takes the arm above.
            // LOG-1's shape since NET-5c: the re-typed stall carries an OperationCanceledException
            // inside - the inner type is the fact, the stack is not.
            Logger.Warning("Download stalled (network timeout): {Name}: {ErrorType}({InnerErrorType}): {ErrorMessage}",
                model.Name, ex.GetType().Name, ex.InnerException?.GetType().Name, ex.Message);
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            model.ErrorMessage = "Download stalled. Check your connection and try again.";
        }
        catch (HttpRequestException ex)
        {
            // Warning: download-source/network failures are environmental and fully
            // user-surfaced below — keep them out of Sentry events (Error+ forwards). LOG-1's
            // shape since NET-5c: type + message, never the object (its stack reached the file
            // log and the Log Viewer on every offline download).
            Logger.Warning("Failed to download model (HTTP): {Name}: {ErrorType}: {ErrorMessage}",
                model.Name, ex.GetType().Name, ex.Message);
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            model.ErrorMessage = "Download failed. Check your connection and try again.";
        }
        catch (Services.Transcription.ModelLocalIOException ex)
        {
            // NET-5: the disk is full. Environmental, fully handled, and the ONE download failure
            // where "try again" is wrong advice - so Warning (Error+ forwards to Sentry, and a full
            // disk is not a defect of ours) and copy that says what to do instead. Three raise
            // sites reach this arm (NET-5b): the mid-write fault, whose partial is kept so the next
            // Download resumes, and the two free-space pre-checks, which refuse before a byte is
            // written - a bundle never resumes and a first attempt has nothing to resume - so the
            // copy promises no resume; the pre-check's MB arithmetic rides {ErrorMessage} into the log.
            Logger.Warning("Model download stopped, disk full: {Name}: {ErrorType}: {ErrorMessage}",
                model.Name, ex.GetType().Name, ex.Message);
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            model.ErrorMessage = "Not enough disk space. Free some space and download again.";
        }
        catch (Services.Transcription.ModelInstallBlockedException ex)
        {
            // The install refused because it could not classify what is already on disk — a lock, an
            // ACL denial, or a manifest from a newer build. Expected and environmental, so it logs at
            // WARNING (Error+ forwards to Sentry) and the specific guidance reaches the user instead
            // of being replaced by the generic "try again" below, which for a durable cause is
            // advice that can never work.
            Logger.Warning(ex, "Model install blocked: {Name} (retryable={Retryable})",
                model.Name, ex.IsRetryable);
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            model.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download model: {Name}", model.Name);
            model.IsDownloading = false;
            model.DownloadProgress = 0;
            model.ErrorMessage = "Download failed. Please try again.";
        }
        finally
        {
            // Dispose the CancellationTokenSource to free its resources
            cts.Dispose();
            model.DownloadCts = null;

            // Only clear global state if no other models are still downloading
            if (!Models.Any(m => m.IsDownloading))
            {
                IsDownloading = false;
                DownloadingModelName = "";
                DownloadProgress = 0;
            }
        }
    }

    /// <summary>
    /// Re-read the on-disk model set and correct each row's <c>IsDownloaded</c> flag IN PLACE.
    /// <para>Needed because this ViewModel is a DI singleton whose <see cref="LoadModels"/> runs only
    /// in the constructor — so before this existed, a model deleted from disk stayed "downloaded"
    /// for the rest of the process (UAT 15.16, 2026-07-25). Called from <c>ModelsPage</c>'s Loaded
    /// handler, so every navigation re-reads disk.</para>
    /// <para>In place, NEVER <c>Clear()</c> + re-<c>Add()</c>: ENH-7 (PR #163) established that
    /// repopulating a live UI-bound <c>ObservableCollection</c> with that churn corrupted native
    /// combo/popup state and crashed with <c>COMException 0x80070490</c>. Fail-soft — a directory
    /// enumeration fault must not break page navigation.</para>
    /// </summary>
    public void RefreshDownloadedState()
    {
        try
        {
            var onDisk = new HashSet<string>(
                _downloadManager.GetDownloadedModels(), ModelDiskReconciliation.NameComparer);
            AugmentWithPcppRowState(onDisk);
            var changed = ApplyDownloadedState(Models, onDisk);
            if (changed > 0)
                Logger.Information("Models page: reconciled {Count} model row(s) against disk", changed);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Models page: disk reconciliation failed — showing last known state");
        }
    }

    /// <summary>
    /// Apply <see cref="ModelDiskReconciliation.Decide"/> to every row in place; returns how many
    /// flags changed. Internal + static so tests can drive it with a real
    /// <c>ObservableCollection</c> and assert that NO CollectionChanged event fires (the ENH-7
    /// invariant) directly, with no ViewModel to construct. (That used to be a hard requirement —
    /// constructing it built a <c>ModelDownloadManager</c> against the user's real Models directory.
    /// The manager now REQUIRES an injected models root, so the ViewModel is constructible; driving
    /// the pure method is simply the tighter test.)
    /// </summary>
    internal static int ApplyDownloadedState(IList<ModelItemViewModel> models, ISet<string> onDisk)
    {
        var changed = 0;
        foreach (var model in models)
        {
            switch (ModelDiskReconciliation.Decide(model.IsDownloaded, model.IsDownloading, onDisk.Contains(model.Name)))
            {
                case ModelDiskAction.MarkDownloaded:
                    model.IsDownloaded = true;
                    changed++;
                    break;
                case ModelDiskAction.MarkMissing:
                    model.IsDownloaded = false;
                    changed++;
                    break;
            }
        }
        return changed;
    }

    [RelayCommand]
    public async Task SelectModelAsync(ModelItemViewModel model)
    {
        // TRN-38 (Codex verification round, THIRD door onto the same Blocker): with the row
        // preserved as installed under an unknown answer, Select is offered too — and
        // GateSelect(isDownloaded: true, fileExists: false) returns SelfHealMissing, which clears
        // the flag and puts Download back over an artifact mid-delete. Same reason as the delete
        // guard: unknown is not absence.
        if (IsPcppRowStateIndeterminate(model))
        {
            Logger.Information(
                "Select deferred for {Name}: the parakeet coordinator's state is busy, so the row is left unchanged",
                model.Name);
            return;
        }

        // TRN-57: a user selecting a model must never wait behind the once-per-version GPU
        // warm-up — the prepare below reaches the coordinator's spawn quiesce, whose GRACE is meant
        // for the startup preload alone. Cancel first, like recording admission does; the
        // warm-up never re-arms this session and the next launch warms instead.
        GpuWarmup.Instance.Cancel(GpuWarmupCancelReason.ModelSelection);

        await SelectModelCoreAsync(
            Models, model,
            // A null path IS the existence signal, resolved ONCE so no second lookup can disagree
            // with the first. Descriptor-driven since TRN-1 step 2, so a bundle resolves by its own
            // full installed check rather than by a .bin that may not exist.
            ResolveInstalledPath,
            (name, language) => _localModels.PrepareAsync(name, language, CancellationToken.None),
            name =>
            {
                SelectedModelName = name;
                _settings.SetString(AppDefaults.SelectedModelName, name);
            },
            SelectedLanguage).ConfigureAwait(true);
    }

    /// <summary>
    /// The select choreography, extracted with injected delegates so the ORDERING is test-pinned
    /// directly, with no filesystem to arrange. (It originally had to be extracted because the
    /// ViewModel could not be constructed at all — its <c>ModelDownloadManager</c> resolved the
    /// user's real Models directory. That is no longer so: the manager now REQUIRES a models root
    /// and <c>ModelManagementViewModelTests</c> passes an isolated one.)
    /// <para><b>Order: resolve once → gate → publish (settings + rows) → load.</b> The ONLY change
    /// this PR makes to the shipped behaviour is the gate: existence is now checked BEFORE anything
    /// is persisted, which is what fixes the UAT-15.16 symptom (a stale row could be clicked and
    /// persisted as Active even though its file was gone).</para>
    /// <para><b>Why nothing is written after the load</b> (the shape five Codex rounds converged on,
    /// 2026-07-25): an intervening design that persisted AFTER the load introduced four distinct
    /// races in a row — a slow load overwriting a newer cloud selection, a completing select resetting
    /// a language the user had just changed, a recording started mid-load installing the PREVIOUS
    /// model into the shared Whisper singleton after the new one loaded, and a rollback that restored
    /// settings while leaving Whisper on the abandoned model. All four existed only because state was
    /// mutated after an await. Publishing everything synchronously before the load leaves no
    /// post-await write to race, so no epoch, supersession check, or rollback is needed.</para>
    /// <para>Pre-existing and deliberately NOT changed here: if the load fails, settings still name
    /// the newly selected model while Whisper keeps the previously loaded one. That is the shipped
    /// behaviour, the next recording start reconciles it via <c>EnsureModelLoadedAsync</c>, and
    /// "fixing" it is what produced the rollback race above. It belongs in a focused change with its
    /// own UAT, not in a Models-page reconciliation PR.</para>
    /// </summary>
    internal static async Task<SelectModelResult> SelectModelCoreAsync(
        IList<ModelItemViewModel> models,
        ModelItemViewModel model,
        Func<string, string?> resolveExistingPath,
        Func<string, string, Task<PrepareOutcome>> prepareModelAsync,
        Action<string> persistSelection,
        string currentLanguage)
    {
        var modelPath = resolveExistingPath(model.Name);
        switch (ModelDiskReconciliation.GateSelect(model.IsDownloaded, modelPath != null))
        {
            case ModelCommandGate.Ignore:
                return SelectModelResult.Ignored;
            case ModelCommandGate.SelfHealMissing:
                model.IsDownloaded = false;
                Logger.Information("Model file missing on select — row corrected, download offered: {Name}", model.Name);
                return SelectModelResult.SelfHealedMissing;
        }

        // Selection NEVER writes the user's language setting (owner 2026-07-31): the old
        // derived-language persist silently reset an explicit language choice to "auto" on
        // every model switch (the live symptom: English kept reverting; auto then misdetected
        // short clips — the 2026-07-26 incident class). The model-constrained language is used
        // for the in-memory Whisper preload ONLY: an .en model can only do English; recording
        // start re-resolves the real language via TranscriptionLanguageResolver anyway. The
        // No catalogue row is English-only since 2026-08-03, so this branch is unreachable today;
        // it is kept because EffectiveTranscriptionLanguage still answers for a restored .en row.
        // Was an inline `.en` check here. Shared now, because recording start answered the same
        // question DIFFERENTLY (it applied no coercion at all), so selecting an English-only model
        // preloaded it as English and the next recording prepared it again with something else —
        // a 2-8 s rebuild for a language that model cannot produce.
        var loadLanguage = Helpers.EffectiveTranscriptionLanguage
            .ForModelName(model.Name, currentLanguage).Language;

        // Everything observable is published HERE, synchronously, before the first await.
        persistSelection(model.Name);
        foreach (var m in models)
            m.IsSelected = m.Name == model.Name;

        try
        {
            // The delegate takes the model NAME, not the resolved path: the local-runtime seam
            // resolves per runtime, because a second engine's artifacts are not a single .bin.
            // `modelPath` above is still the GATE — resolved once, checked before anything is
            // persisted, which is the invariant five review rounds converged on. What is no longer
            // literally true is "resolved once and reused": the runtime looks it up again.
            //
            // That second lookup can race — the file may vanish between gate and load — and a
            // NotDownloaded here maps onto the SAME LoadFailed result an exception would have
            // produced, whose semantics are already documented above.
            var outcome = await prepareModelAsync(model.Name, loadLanguage).ConfigureAwait(true);
            if (outcome != PrepareOutcome.Loaded)
            {
                Logger.Warning("Model not ready after select ({Outcome}): {Name}", outcome, model.Name);
                return SelectModelResult.LoadFailed;
            }
        }
        catch (Exception ex)
        {
            // Logged, not reverted — see the "pre-existing" note above.
            Logger.Warning(ex, "Model load failed after select: {Name}", model.Name);
            return SelectModelResult.LoadFailed;
        }

        Logger.Information("Model selected and loaded: {Name}", model.Name);
        return SelectModelResult.Selected;
    }

    /// <summary>
    /// Selects a cloud transcription model. Deselects all local models. Deliberately does NOT
    /// touch <see cref="SelectedLanguage"/> (owner 2026-07-31): the old reset wrote "auto"
    /// unconditionally — its English-forcing branch tested "-en", which no model id ever
    /// matches — so every cloud switch silently clobbered an explicit language choice.
    /// </summary>
    public void SelectCloudModel(string modelName)
    {
        SelectedModelName = modelName;
        _settings.SetString(AppDefaults.SelectedModelName, modelName);

        // Deselect all local models
        foreach (var m in Models)
            m.IsSelected = false;

        Logger.Information("Cloud model selected: {Name}", modelName);
    }

    /// <summary>TRN-37: the delete runs its blocking half on the pool — TryDeleteBoth's bounded
    /// joins + retire gate could park the UI thread ~20 s worst-case (typical: milliseconds).
    /// The gate reads and the outcome's UI mutations stay on the caller's thread; only the
    /// manager/coordinator call moves. **The re-entrancy guard is the PAGE's button disable,
    /// not this command** (self-review lens 2, measured against toolkit 8.4.0):
    /// AsyncRelayCommand's no-concurrent-executions default only feeds CanExecute, which
    /// ExecuteAsync does not consult and a Tapped+= wiring never reads — so ModelsPage disables
    /// the row's Select AND Delete before awaiting, exactly as the Select/Download siblings do.
    /// ACCEPTED residual (Kimi plan round B2, shape (i), carded as TRN-38): while a delete is in
    /// flight, the Models page's Loaded refresh and an OTHER-row Select can park on the
    /// coordinator's _state for the delete's remainder — self-recovering, no corruption.</summary>
    [RelayCommand]
    public Task DeleteModelAsync(ModelItemViewModel model)
    {
        // TRN-38 (Codex diff round 1, the Blocker's SECOND door): with the row now preserved as
        // installed under an unknown answer, Delete stays clickable during a delete — and the
        // core would then resolve no path, conclude "not on disk", and CLEAR the row's flag
        // (ModelDiskReconciliation's honest-page job, correct for a genuine absence). That
        // reintroduces the Download affordance the fix above exists to remove. Unknown is not
        // absence: refuse the action outright and leave the row saying exactly what it said.
        if (IsPcppRowStateIndeterminate(model))
        {
            Logger.Information(
                "Delete deferred for {Name}: the parakeet coordinator's state is busy, so whether any artifact exists is unknown - the row is left unchanged",
                model.Name);
            return Task.CompletedTask;
        }

        return DeleteModelCoreAsync(
            // The active-model check must be case-INSENSITIVE. This is the destructive boundary:
            // an ordinal comparison against a settings value cased differently from the catalog
            // (import, hand-edit) reports "not the active model" and deletes the file the running
            // app resolved and loaded.
            model, ModelDiskReconciliation.IsSameModel(model.Name, SelectedModelName),
            ResolveInstalledPath,
            name => Task.Run(() => DeleteThroughManager(name)));
    }

    /// <summary>TRN-38: is this the Parakeet row in the state where a command would act on an
    /// UNKNOWN artifact answer? Exactly the condition <see cref="ResolveInstalledPath"/> consults
    /// the backend under — a Parakeet descriptor whose catalog install resolves nothing — plus an
    /// unknown answer. Both reads are bounded, and a race between this check and the core's own
    /// read is harmless: every outcome of that race is "refuse or resolve honestly", never acting
    /// on the wrong model.
    ///
    /// <para><b>ONE policy, not three ad-hoc guards</b> — it took a Blocker plus its verification
    /// round to arrive at it, so it is stated rather than left to be re-derived: <b>while the
    /// coordinator's state is unknown, no command may change this row and nothing may change what
    /// it claims.</b> Delete and Select both consult this; Download needs no guard because a
    /// preserved-installed row makes <see cref="DownloadModelAsync"/> return at its own
    /// already-downloaded check. Every route Codex found reached the same end — clearing the flag,
    /// exposing Download, and letting a direct manager download reinstall 940 MB the user had just
    /// asked to delete — from a different door (round 1: the refresh; its fix opened the delete's
    /// self-heal; verification: Select's <c>GateSelect</c> → <c>SelfHealMissing</c>). Anything
    /// added here that can flip a row must consult this too.</para></summary>
    private bool IsPcppRowStateIndeterminate(ModelItemViewModel model)
    {
        if (_pcppBackend is null) return false;
        var descriptor = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, model.Name));
        if (descriptor is null || descriptor.Runtime != LocalRuntimeKind.Parakeet) return false;
        if (_downloadManager.TryGetInstalledLocation(descriptor)?.Path is not null) return false;
        return _pcppBackend.HasAnyArtifact() is null;
    }

    /// <summary>TRN-29 slice 4 (Codex diff r1): the pcpp transition decides the Parakeet row's
    /// installed presentation — post-migration the catalog-driven name set no longer contains
    /// the row (the GGUF descriptor is hidden by design), so without this the page renders
    /// "Download" over a healthy install. Membership is set VERBATIM from RowInstalled: add on
    /// true, remove on false (a broken-artifact state must offer Download as the repair even
    /// if a stale set said otherwise). Fail-soft: a throwing backend leaves the set untouched.
    ///
    /// <para><b>TRN-38:</b> an UNKNOWN answer (the coordinator's state lock was busy past its
    /// bounded wait — a delete in flight) must not be coerced to a verdict: false would show
    /// Download over a healthy 940 MB install, true would claim a deleted model is present.
    ///
    /// <para><b>Unknown preserves the row's LAST CONFIRMED presentation, and "leave the set
    /// untouched" is NOT that</b> (Codex diff round 1, Blocker — a real defect this change armed,
    /// not a hypothetical). The set is catalog-derived, and in the MIGRATION WINDOW (sherpa
    /// installed, GGUF not yet) the catalog does not contain this row at all — the sherpa bundle
    /// is installed under its OWN name while the row's name is the GGUF's. So an untouched set
    /// renders <b>Download</b>, and <c>DownloadModelAsync</c> goes straight to the manager
    /// without consulting the coordinator's tombstone: a user who clicked Delete, navigated away
    /// and back during the ~20 s delete, and then clicked the Download the page was now offering
    /// would re-install the 940 MB artifact they had just asked to remove. Before this card the
    /// UI froze instead, so the affordance was unreachable — the bounded wait is what exposed it.
    /// Preserving the row's own flag keeps Select/Delete showing until the coordinator can
    /// actually answer.</para>
    ///
    /// <para>First load is the one case with no prior truth (<c>LoadModels</c> builds the rows
    /// after this runs), and it falls back to the catalog. That is sound rather than a gap: the
    /// ViewModel is constructed at startup, where no delete of this app's own making can be in
    /// flight.</para></summary>
    private void AugmentWithPcppRowState(ISet<string> names)
    {
        if (_pcppBackend is null) return;
        try
        {
            var row = PredefinedModels.Models.FirstOrDefault(m => m.Runtime == LocalRuntimeKind.Parakeet);
            if (row is null) return;
            var installed = _pcppBackend.RowInstalled();
            if (installed is null)
            {
                var current = Models.FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, row.Name));
                if (current is null) return; // first load: no prior truth, the catalog stands
                if (current.IsDownloaded) names.Add(row.Name); else names.Remove(row.Name);
                return;
            }
            if (installed.Value)
            {
                names.Add(row.Name);
            }
            else
            {
                names.Remove(row.Name);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pcpp row-state augmentation failed - showing catalog-derived state");
        }
    }

    /// <summary>
    /// Catalog lookup + install-state resolution, in one place. Descriptor-driven so the expected
    /// on-disk SHAPE comes from the catalog rather than from whatever happens to be on disk — a
    /// stray <c>{bundle-name}.bin</c> can never mark a bundle installed, or the reverse.
    /// A name with no catalog row resolves to null, which the gate reads as "nothing to act on".
    /// </summary>
    private string? ResolveInstalledPath(string modelName)
    {
        var descriptor = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, modelName));
        if (descriptor is null) return null;

        var resolved = _downloadManager.TryGetInstalledLocation(descriptor)?.Path;

        // TRN-29 slice 4 (Codex diff r1): in the GGUF-only state the catalog (sherpa)
        // resolution is null, and the delete gate upstream read that as "nothing to act on" —
        // making the hidden 940 MB undeletable from the UI. Any Parakeet bytes at all keep the
        // delete reachable; the coordinator's TryDeleteBoth owns what actually gets removed.
        //
        // TRN-38: `== true`, so an UNKNOWN answer (state lock busy — a delete in flight) falls
        // through to the catalog resolution instead of blocking this UI-thread read. Refusing a
        // second delete while one runs is the correct direction, and this reader is consulted
        // ONLY where the catalog already resolved nothing.
        if (resolved is null && _pcppBackend is not null
            && descriptor.Runtime == LocalRuntimeKind.Parakeet
            && _pcppBackend.HasAnyArtifact() == true)
        {
            return _downloadManager.ModelsDirectory;
        }

        return resolved;
    }

    /// <summary>
    /// Deletion runs through the manager, which owns both shapes, the per-model lock, and the
    /// marker-first ordering for bundles.
    /// </summary>
    private ModelDeleteOutcome DeleteThroughManager(string modelName)
    {
        var descriptor = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, modelName));
        if (descriptor is null)
        {
            return ModelDeleteOutcome.NotPresent;
        }

        // TRN-29 slice 4: with the pcpp backend registered, the Parakeet row's delete removes
        // BOTH artifacts (legacy bundle + hidden GGUF) under the coordinator's durable
        // tombstone. Partial failure maps to FailedBeforeInvalidation — remaining artifacts
        // still serve, so the row must stay marked installed, and the tombstone makes the retry
        // resume rather than the migration re-download what the user removed.
        if (_pcppBackend is not null && descriptor.Runtime == LocalRuntimeKind.Parakeet)
        {
            return _pcppBackend.TryDeleteBoth()
                ? ModelDeleteOutcome.Deleted
                : ModelDeleteOutcome.FailedBeforeInvalidation;
        }

        return _downloadManager.DeleteModel(descriptor);
    }

    /// <summary>
    /// The delete choreography, extracted for the same reason as
    /// <see cref="SelectModelCoreAsync"/> — the ordering needs pinning without a real
    /// <c>ModelDownloadManager</c>. There is deliberately NO second existence check after the gate:
    /// re-guarding on <c>File.Exists</c> meant a file that vanished between the two checks fell
    /// through leaving <c>IsDownloaded</c> stale — the exact dead-button symptom (Codex diff review
    /// round 1, 2026-07-25). The manager's delete is a no-op on an absent target for BOTH shapes
    /// (<c>Directory.Delete</c> would otherwise throw where <c>File.Delete</c> does not), so calling
    /// it unguarded is still simpler and race-free.
    ///
    /// <para><b>Phase-aware since TRN-1 step 2.</b> A bundle delete removes the manifest before the
    /// payload, so there is a point past which the model is no longer installed even if cleanup
    /// then fails. The row must follow that: a failure BEFORE invalidation keeps
    /// <c>IsDownloaded</c> TRUE (the model really is still there), a failure AFTER it must clear the
    /// flag (it is not). Collapsing the two would either strand a row claiming a deleted model is
    /// present, or claim a present model was deleted.</para>
    ///
    /// <para><b>TRN-37:</b> async twin of the former sync core (which is REMOVED, not delegated —
    /// its only production caller is the command above). Byte-identical gate and outcome logic;
    /// the ONE difference is that the delete callback is awaited, so the command can move the
    /// blocking manager/coordinator work off the UI thread while the gate reads and the row
    /// mutations stay on the caller's context.</para>
    /// </summary>
    internal static async Task<DeleteModelResult> DeleteModelCoreAsync(
        ModelItemViewModel model,
        bool isSelected,
        Func<string, string?> resolveExistingPath,
        Func<string, Task<ModelDeleteOutcome>> deleteModelAsync)
    {
        var modelPath = resolveExistingPath(model.Name);
        switch (ModelDiskReconciliation.GateDelete(model.IsDownloaded, isSelected, modelPath != null))
        {
            case ModelCommandGate.Ignore:
                return DeleteModelResult.Ignored;
            case ModelCommandGate.SelfHealMissing:
                // Already gone. Correcting the row IS the action — returning silently here is what
                // made the delete button look dead (UAT 15.16, 2026-07-25).
                model.IsDownloaded = false;
                Logger.Information("Model file already gone on delete — row corrected: {Name}", model.Name);
                return DeleteModelResult.SelfHealedMissing;
        }

        ModelDeleteOutcome outcome;
        try
        {
            outcome = await deleteModelAsync(model.Name);
        }
        catch (Exception ex)
        {
            // An unexpected throw says nothing about how far the delete got, so the safe reading is
            // that the model is still there and the row should keep saying so.
            Logger.Error(ex, "Failed to delete model: {Name}", model.Name);
            return DeleteModelResult.DeleteFailed;
        }

        switch (outcome)
        {
            case ModelDeleteOutcome.NotPresent:
                model.IsDownloaded = false;
                Logger.Information("Model file already gone on delete — row corrected: {Name}", model.Name);
                return DeleteModelResult.SelfHealedMissing;

            case ModelDeleteOutcome.Deleted:
                model.IsDownloaded = false;
                Logger.Information("Model deleted: {Name}", model.Name);
                return DeleteModelResult.Deleted;

            case ModelDeleteOutcome.InvalidatedButCleanupFailed:
                // No longer installed — leftovers remain and the next download clears them.
                model.IsDownloaded = false;
                Logger.Warning("Model no longer installed but some files could not be removed: {Name}", model.Name);
                return DeleteModelResult.InvalidatedButCleanupFailed;

            default:
                // Still there (locked, ACL) — keep the flag TRUE so the UI stays honest.
                Logger.Error("Failed to delete model: {Name}", model.Name);
                return DeleteModelResult.DeleteFailed;
        }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _settings.SetString(AppDefaults.SelectedLanguage, value);
        Logger.Information("Language changed to: {Language}", value);
        if (Models.Count > 0)
        {
            _ = ReloadSelectedModelForLanguageAsync(value);
        }
    }

    private async Task ReloadSelectedModelForLanguageAsync(string language)
    {
        // Cloud models have nothing to reload. Previously this was implicit — GetModelPath simply
        // returned null for a cloud name — but the seam would answer UnknownModel for one, which
        // is a misleading log line for something that is simply not its business.
        // Fully qualified: `Models` is this class's ObservableCollection property, which shadows
        // the namespace.
        if (VoiceWink.Models.CloudModels.IsCloudModel(SelectedModelName))
            return;

        try
        {
            // The EFFECTIVE language, not the raw one. Reloading an English-only model under `nl`
            // rebuilds the processor for a language that model cannot honour — and the next
            // recording, which resolves the effective value, rebuilds it straight back. Two
            // multi-second rebuilds for a setting change that should have caused none.
            var effective = EffectiveTranscriptionLanguage.ForModelName(SelectedModelName, language).Language;

            // Fail-soft, unchanged: a language change is a convenience reload. NotDownloaded,
            // UnknownModel and Unavailable all mean "nothing to do here"; recording start reports
            // each of them properly.
            // TRN-57: same rule as the model selection above — a user action cancels the warm-up
            // rather than waiting behind the spawn grace meant for the startup preload.
            GpuWarmup.Instance.Cancel(GpuWarmupCancelReason.LanguageReload);
            await _localModels.PrepareAsync(SelectedModelName, effective, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to reload selected model for language change");
        }
    }
}

/// <summary>
/// ViewModel for a single model item in the list.
/// </summary>
public partial class ModelItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private long _fileSizeBytes;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadUrl = "";
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>Cancellation source for an in-progress download.</summary>
    public CancellationTokenSource? DownloadCts { get; set; }

    public string FileSizeDisplay => FileSizeBytes switch
    {
        >= 1_000_000_000 => $"{FileSizeBytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{FileSizeBytes / 1_000_000.0:F0} MB",
        _ => $"{FileSizeBytes / 1_000.0:F0} KB"
    };
}
