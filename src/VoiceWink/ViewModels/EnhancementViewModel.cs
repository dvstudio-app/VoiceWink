using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;
using Providers = VoiceWink.Services.AIEnhancement.Providers;
using VoiceWink.Services.System;

namespace VoiceWink.ViewModels;

public enum ApiKeyStatus { Unknown, Valid, Invalid }

/// <summary>
/// ViewModel for AI enhancement settings.
/// </summary>
public partial class EnhancementViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<EnhancementViewModel>();

    private readonly AIEnhancementService _enhancement;
    private readonly ApiKeyManager _apiKeys;
    private readonly Services.System.SettingsService _settings;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private AIProvider _selectedProvider;
    [ObservableProperty] private AIProvider _selectedImageProvider;
    [ObservableProperty] private string _selectedModel = "";
    [ObservableProperty] private string _selectedImageModel = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _imageApiKey = "";
    [ObservableProperty] private ObservableCollection<CustomPrompt> _prompts = new();
    // BulkObservableCollection: these two feed live editable ComboBoxes on EnhancementPage —
    // repopulates must go through ReplaceAll (one Reset), never Clear + Add loops.
    [ObservableProperty] private BulkObservableCollection<string> _availableModels = new();
    [ObservableProperty] private BulkObservableCollection<string> _availableImageModels = new();
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private bool _isLoadingImageModels;
    // Two independent escape hatches since the provider card was split into Text and Image
    // (owner 2026-07-31): each card's checkbox governs only its own list, so revealing every
    // text model no longer floods the image dropdown (and vice versa). Session-only, like the
    // single flag they replace — deliberately not persisted.
    [ObservableProperty] private bool _showAllModels;
    [ObservableProperty] private bool _showAllImageModels;
    [ObservableProperty] private ApiKeyStatus _apiKeyStatus = ApiKeyStatus.Unknown;
    [ObservableProperty] private ApiKeyStatus _imageApiKeyStatus = ApiKeyStatus.Unknown;
    // ENH-1: why the last text/image model fetch produced no models (null = no error).
    // UI-destined text (ProviderApiException.UserMessage) — display only, never log.
    [ObservableProperty] private string? _modelFetchError;
    [ObservableProperty] private string? _imageModelFetchError;

    // Observable so the page's key-status indicator reflects ACTUAL stored-key state,
    // not the password box's contents (Codex PR-2 R2 — a DPAPI-failed save left text in
    // the box, which the indicator read as "Key saved — not verified").
    [ObservableProperty]
    private bool _hasExistingKey;
    [ObservableProperty]
    private bool _hasExistingImageKey;
    private bool _suppressModelSync; // prevent feedback loop during provider switch
    private CancellationTokenSource? _fetchCts; // cancel stale text model fetches
    private CancellationTokenSource? _fetchImageCts; // cancel stale image model fetches
    private const string MaskedKeyPlaceholder = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

    public static AIProvider[] AvailableProviders => Enum.GetValues<AIProvider>();

    public EnhancementViewModel(AIEnhancementService enhancement, ApiKeyManager apiKeys, Services.System.SettingsService settings)
    {
        _enhancement = enhancement;
        _apiKeys = apiKeys;
        _settings = settings;

        _isEnabled = enhancement.IsEnabled;
        _selectedProvider = enhancement.SelectedProvider;
        _selectedImageProvider = enhancement.SelectedImageProvider;
        _selectedModel = enhancement.SelectedModel;
        _selectedImageModel = enhancement.SelectedImageModel;

        ReloadPrompts();
        LoadApiKey();
        LoadImageApiKey();
        LoadLocalServerSettings();
    }

    /// <summary>
    /// Re-read the enable switch from the service after something OTHER than this ViewModel wrote
    /// it — the onboarding wizard's AI Enhancement step writes <c>AiEnhancementEnabled</c> straight
    /// to settings (2026-09-13). This is a singleton, and <c>_isEnabled</c> was captured once in the
    /// constructor: after "Relaunch setup wizard" the Enhancement page rebuilt its toggle from the
    /// stale copy while the runtime already read the new value. The page's own
    /// <c>AttachSubscriptions</c> resyncs provider and model but never this flag.
    /// </summary>
    /// <remarks>
    /// A reload to OFF assigns the property under <see cref="_reloadingEnabled"/>, which makes
    /// <see cref="OnIsEnabledChanged"/> a no-op: that branch of the handler is a USER-change
    /// action — it deactivates the active prompt and remembers it — and replaying it while merely
    /// LOADING a preference the service already holds would deactivate a prompt the wizard did not
    /// touch. A reload to ON lets the handler run: its branch is idempotent (a same-value write-back,
    /// and it activates a prompt only when none is active) and it repairs the one state a wizard
    /// write leaves behind — enabled, with the prompt this page's own OFF had deactivated still
    /// off, which <c>MainViewModel</c> would then treat as "no prompt, do not enhance" while the
    /// switch reads ON (self-review, correctness lens). (Not a direct backing-field write: the
    /// toolkit's MVVMTK0034 analyzer warns on that, and the build gate is zero warnings.)
    /// </remarks>
    public void ReloadFromSettings()
    {
        var current = _enhancement.IsEnabled;
        if (IsEnabled == current)
            return;
        _reloadingEnabled = !current;
        try { IsEnabled = current; }
        finally { _reloadingEnabled = false; }
    }

    /// <summary>True only inside a <see cref="ReloadFromSettings"/> to OFF — see its remarks.</summary>
    private bool _reloadingEnabled;

    /// <summary>
    /// Reload prompts from the service. Call when prompts may have been
    /// modified externally (e.g. Settings page clearing a prompt hotkey).
    /// </summary>
    public void ReloadPrompts()
    {
        Prompts.Clear();
        foreach (var p in _enhancement.GetPrompts())
            Prompts.Add(p);
    }

    private void LoadApiKey()
    {
        var key = _apiKeys.GetApiKey(SelectedProvider.ToString().ToLowerInvariant());
        HasExistingKey = !string.IsNullOrEmpty(key);
        ApiKey = HasExistingKey ? MaskedKeyPlaceholder : "";
    }

    private void LoadImageApiKey()
    {
        var key = _apiKeys.GetApiKey(SelectedImageProvider.ToString().ToLowerInvariant());
        HasExistingImageKey = !string.IsNullOrEmpty(key);
        ImageApiKey = HasExistingImageKey ? MaskedKeyPlaceholder : "";
    }

    /// <summary>
    /// Refreshes the masked API key display for both the text and image providers.
    /// Call this after externally loading API keys to update the UI
    /// without rebuilding the entire page.
    /// </summary>
    public void RefreshApiKeys()
    {
        LoadApiKey();
        LoadImageApiKey();
        ApiKeyStatus = ApiKeyStatus.Unknown;
        ImageApiKeyStatus = ApiKeyStatus.Unknown;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        // A reload to OFF carries a value the service already holds: no write-back, and no
        // deactivating a prompt the user did not turn off here.
        if (_reloadingEnabled)
            return;

        _enhancement.IsEnabled = value;

        if (!value)
        {
            // Remember which prompt was active before deactivating
            var active = Prompts.FirstOrDefault(p => p.IsActive);
            if (active != null)
            {
                _settings.SetString(AppDefaults.LastActivePromptId, active.Id);
                SetActivePrompt(active); // toggles it off + saves
            }
        }
        else if (!Prompts.Any(p => p.IsActive))
        {
            // Restore last active prompt, or fall back to the default Improve Transcription prompt.
            // Match the fallback by immutable SeedKey, not Title — the title is user-facing and was
            // renamed (Improve Accuracy → Improve Transcription, 2026-07-22); SeedKey never changes.
            var lastId = _settings.GetString(AppDefaults.LastActivePromptId, "");
            var toActivate = !string.IsNullOrEmpty(lastId)
                ? Prompts.FirstOrDefault(p => p.Id == lastId)
                : null;
            toActivate ??= Prompts.FirstOrDefault(p => p.SeedKey == PromptTemplates.ImproveAccuracy.Key);
            if (toActivate != null)
                SetActivePrompt(toActivate);
        }
    }

    partial void OnSelectedProviderChanged(AIProvider value)
    {
        // Only persist provider to settings if it is USABLE: it has an API key, or it needs none
        // (LAI-1, a user-run server). This prevents navigating away with a keyless CLOUD provider
        // stuck in settings. The UI combo still shows the selection so the user can enter a key
        // and save it, which then persists the provider (see SaveApiKey).
        //
        // The keyless half is not a convenience. Without it, choosing "Local server" while an
        // OpenAI key is stored leaves SelectedProvider on OpenAI, and the next dictation goes to
        // the cloud under a local label — the privacy fail-open the plan's round-1 review found.
        // All three uses below read the same flag: persist, restore the model, fetch the list.
        var usable = _apiKeys.HasApiKey(value.ToString().ToLowerInvariant())
                     || !_enhancement.RequiresApiKey(value);
        if (usable)
            _enhancement.SelectedProvider = value;

        ApiKeyStatus = ApiKeyStatus.Unknown;
        ModelFetchError = null; // previous provider's error must not linger
        LoadApiKey();
        LoadLocalServerSettings();
        OnPropertyChanged(nameof(IsLocalServerSelected));

        // Restore the last-used model for this provider, or clear if it cannot run
        _suppressModelSync = true;
        SelectedModel = usable ? _enhancement.SelectedModel : "";
        _suppressModelSync = false;

        // Fetch text models for the new provider. The loading flag is reset here
        // because a stale in-flight fetch may no longer clear it (its finally is
        // identity-guarded); a new fetch re-sets it immediately.
        AvailableModels.Clear();
        IsLoadingModels = false;
        if (usable)
            _ = FetchModelsAsync();
    }

    // ── LAI-1: Local server settings ────────────────────────────────────

    /// <summary>True while the text provider is the user's own AI server.</summary>
    public bool IsLocalServerSelected => SelectedProvider == AIProvider.LocalServer;

    /// <summary>
    /// The server type the card shows. Picking one stores it at once together with that type's
    /// default address — the two settings always describe ONE server, never an Ollama API aimed
    /// at LM Studio's port.
    /// </summary>
    [ObservableProperty] private LocalServerApi _localServerApi;

    /// <summary>The address in the card's box — what Save would store.</summary>
    [ObservableProperty] private string _localServerUrl = "";

    /// <summary>Why the last Save was refused; null when it was not.</summary>
    [ObservableProperty] private string? _localServerError;

    /// <summary>
    /// The host the text is sent to when the SAVED address is not this PC, else null — the card
    /// says so, because "Local server" would otherwise read as "stays on this PC".
    /// </summary>
    public string? LocalServerRemoteHost
        => LocalServerEndpoints.RemoteHost(LocalServerEndpoints.EffectiveBaseUrl(StoredLocalServerApi(), StoredLocalServerUrl()));

    private static string LocalServerUrlKey => AppDefaults.AiBaseUrlPrefix + AIProvider.LocalServer.ToString().ToLowerInvariant();

    private LocalServerApi StoredLocalServerApi()
        => LocalServerEndpoints.ParseApi(_settings.GetString(AppDefaults.LocalServerApiSetting, LocalServerEndpoints.OllamaToken));

    private string StoredLocalServerUrl() => _settings.GetString(LocalServerUrlKey, "");

    private bool _loadingLocalServer;

    /// <summary>
    /// Re-read the stored server into the card — at construction, on a provider change, and each
    /// time the page is built (this VM is a singleton, so an unsaved address typed on an earlier
    /// visit would otherwise reappear as if it were stored).
    /// </summary>
    public void LoadLocalServerSettings()
    {
        _loadingLocalServer = true;
        try
        {
            var api = StoredLocalServerApi();
            LocalServerApi = api;
            LocalServerUrl = LocalServerEndpoints.EffectiveBaseUrl(api, StoredLocalServerUrl());
            LocalServerError = null;
        }
        finally { _loadingLocalServer = false; }
        OnPropertyChanged(nameof(LocalServerRemoteHost));
    }

    partial void OnLocalServerApiChanged(LocalServerApi value)
    {
        if (_loadingLocalServer)
            return;
        LocalServerUrl = LocalServerEndpoints.DefaultUrlFor(value);
        // A different kind of server means a different model catalog: the old id would be sent to
        // the new server and fail, so the selection starts empty and the user picks from the list.
        if (IsLocalServerSelected)
            SelectedModel = "";
        SaveLocalServerAddress();
    }

    /// <summary>
    /// Store the server type and the address in the box. A refused address changes nothing
    /// stored — the previous server keeps working until a valid one is saved.
    /// </summary>
    [RelayCommand]
    public void SaveLocalServerAddress()
    {
        var url = LocalServerUrl.Trim();
        var reason = LocalServerEndpoints.Validate(url);
        if (reason is not null)
        {
            LocalServerError = reason;
            return;
        }

        var api = LocalServerApi;
        _settings.SetString(AppDefaults.LocalServerApiSetting, LocalServerEndpoints.TokenFor(api));
        _settings.SetString(LocalServerUrlKey, url);
        _enhancement.InvalidateModelLists(AIProvider.LocalServer);
        LocalServerError = null;
        OnPropertyChanged(nameof(LocalServerRemoteHost));
        Logger.Information("Local server set: api={Api}, remote={Remote}", api, LocalServerEndpoints.RemoteHost(url) is not null);

        if (IsLocalServerSelected)
        {
            ModelFetchError = null;
            AvailableModels.Clear();
            _ = FetchModelsAsync();
        }
    }

    partial void OnSelectedImageProviderChanged(AIProvider value)
    {
        var hasImageKey = _apiKeys.HasApiKey(value.ToString().ToLowerInvariant());
        if (hasImageKey)
            _enhancement.SelectedImageProvider = value;

        ImageApiKeyStatus = ApiKeyStatus.Unknown;
        ImageModelFetchError = null; // previous provider's error must not linger
        LoadImageApiKey();

        // Restore the last-used image model for this provider, or clear if no key
        _suppressModelSync = true;
        SelectedImageModel = hasImageKey ? _enhancement.SelectedImageModel : "";
        _suppressModelSync = false;

        // Fetch image models for the new provider (loading-flag reset: see text mirror)
        AvailableImageModels.Clear();
        IsLoadingImageModels = false;
        if (hasImageKey)
            _ = FetchImageModelsAsync();
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (!_suppressModelSync)
            _enhancement.SelectedModel = value;
    }

    partial void OnSelectedImageModelChanged(string value)
    {
        if (!_suppressModelSync)
            _enhancement.SelectedImageModel = value;
    }

    partial void OnShowAllModelsChanged(bool value) => _ = FetchModelsAsync();

    partial void OnShowAllImageModelsChanged(bool value) => _ = FetchImageModelsAsync();

    [RelayCommand]
    public async Task SaveApiKeyAsync()
    {
        if (ApiKey == MaskedKeyPlaceholder)
            return;

        // ENH-17: capture the provider ONCE and use the captured local for every read after the
        // await — storage write, KeyChanged and UI alike.
        //
        // This is KEY HYGIENE, not tidiness. Under validate-before-write the ONLY write happens
        // after the await, so a live `SelectedProvider` read there would write this provider's key
        // into whichever slot the combo had moved to — an OpenAI key landing in `apikey_groq` and
        // then being sent to api.groq.com. The old write-first flow could not produce that, because
        // its write happened before the await; deleting the temp-save is what makes the capture
        // load-bearing (Kimi, ENH-17 plan review). The fetch path has always done this via
        // IsCurrentTextFetch; the save path never did.
        var provider = SelectedProvider;
        var providerKey = provider.ToString().ToLowerInvariant();
        var mirrorsImageRow = SelectedImageProvider == provider;
        var newKey = ApiKey;

        // Claim the slot's write generation SYNCHRONOUSLY, before the first await. Clear claims
        // too — a clear owns the slot exactly as a save does, and one that did not claim would be
        // silently overwritten by an in-flight save's commit.
        var generation = _apiKeys.BeginKeyWrite(providerKey);

        // Empty or whitespace-only = clear.
        if (string.IsNullOrWhiteSpace(newKey))
        {
            if (!_apiKeys.TryCommitApiKey(providerKey, "", generation, out _))
            {
                Logger.Information("Key clear for {Provider} superseded by a newer write", provider);
                return;
            }
            HasExistingKey = false;
            ApiKeyStatus = ApiKeyStatus.Unknown;
            ModelFetchError = null;
            CancelPendingTextFetch();
            Logger.Information("API key cleared for {Provider}", provider);
            AvailableModels.Clear();
            // LAI-1: a keyless provider still works without the key — list its models again.
            if (!_enhancement.RequiresApiKey(provider) && TextRowStillShows(provider))
                _ = FetchModelsAsync();
            if (mirrorsImageRow)
            {
                LoadImageApiKey();
                ImageApiKeyStatus = ApiKeyStatus.Unknown;
                ImageModelFetchError = null;
                CancelPendingImageFetch();
                AvailableImageModels.Clear();
            }
            return;
        }

        // Format-check the candidate BEFORE any network call — the same rule the write funnel
        // enforces, so a malformed paste is refused with identical copy and never leaves the
        // machine. It also makes InvalidApiKeyFormatException unreachable in this flow: the
        // candidate is pre-checked, and the stored key is never read during validation.
        var verdict = Helpers.ApiKeyFormat.Validate(Helpers.ApiKeyFormat.Normalize(newKey));
        if (verdict != Helpers.ApiKeyFormatVerdict.Ok)
        {
            ApiKeyStatus = ApiKeyStatus.Invalid;
            ModelFetchError = Helpers.ApiKeyFormat.Describe(verdict);
            Logger.Warning("Candidate API key for {Provider} rejected: {Verdict}", provider, verdict);
            return;
        }

        // Invalidate any in-flight background fetch before touching row state (Codex PR-2 R2): a
        // page-attach fetch completing later still passes its CTS identity check and would
        // overwrite this save's outcome.
        CancelPendingTextFetch();
        if (mirrorsImageRow)
            CancelPendingImageFetch();
        ApiKeyStatus = ApiKeyStatus.Unknown;

        try
        {
            // ENH-17: validate the CANDIDATE. Nothing is written and nothing is cached until it
            // passes — which is what deletes the rollback, and with it every defect that lived in
            // one. ONE fetch answers both questions: RawCount decides whether the KEY works (so
            // judging it on a FILTERED list would let a curation rule masquerade as a bad key),
            // while testModels is the display list.
            var (testModels, rawModelCount, fetchError) = await _enhancement.ValidateCandidateKeyAsync(
                provider, newKey, Providers.ModelCatalogQuery.Text, showAll: ShowAllModels, ct: default);

            if (fetchError != null)
            {
                // The provider failed for a NON-auth reason (geo-block, outage, timeout) — the key
                // may be perfectly fine, so save it. Under write-first this happened by accident
                // of ordering; now it is a DELIBERATE write, and it still goes through the commit
                // check so a clear during the window wins. Status stays Unknown, never Valid.
                if (!CommitKeyAndOwnRow(providerKey, newKey, generation, provider))
                    return;
                HasExistingKey = true;
                ApiKey = MaskedKeyPlaceholder;
                _enhancement.SelectedProvider = provider;
                ApiKeyStatus = ApiKeyStatus.Unknown;
                ModelFetchError = fetchError;
                AvailableModels.Clear(); // stale models beside a fetch error would mislead
                if (mirrorsImageRow)
                {
                    LoadImageApiKey();
                    ImageApiKeyStatus = ApiKeyStatus.Unknown;
                    ImageModelFetchError = fetchError;
                    AvailableImageModels.Clear();
                }
                Logger.Warning("API key for {Provider} saved, but the model fetch failed for a non-auth reason", provider);
                return;
            }

            // Empty CATALOG with a key set likely means the key was rejected (some providers
            // return an empty list instead of 401). Nothing was written, so there is nothing to
            // undo — the stored key was never touched.
            // LAI-1: not for a keyless provider — its key is optional, and a user server with no
            // models installed says nothing about the key.
            if (rawModelCount == 0 && _enhancement.RequiresApiKey(provider))
            {
                RejectCandidate(providerKey, generation, provider, mirrorsImageRow);
                Logger.Warning("API key for {Provider} returned no models — likely invalid, not saved", provider);
                return;
            }

            if (!CommitKeyAndOwnRow(providerKey, newKey, generation, provider))
                return;

            HasExistingKey = true;
            if (mirrorsImageRow)
                HasExistingImageKey = true;
            ApiKeyStatus = ApiKeyStatus.Valid;
            ModelFetchError = null;
            Logger.Information("API key saved for {Provider}", provider);
            ApiKey = MaskedKeyPlaceholder;
            _enhancement.SelectedProvider = provider;

            // testModels already reflects the display policy — fetched with `showAll: ShowAllModels`,
            // so curation ran inside the fetch. No second request, and no window where the dropdown
            // sits empty next to "Key valid".
            AvailableModels.ReplaceAll(testModels);

            if (mirrorsImageRow)
            {
                LoadImageApiKey();
                ImageApiKeyStatus = ApiKeyStatus.Valid;
                ImageModelFetchError = null; // don't show a stale error while the refetch is in flight
                _ = FetchImageModelsAsync();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                   or global::System.Net.HttpStatusCode.Forbidden)
        {
            // Rejected. Nothing was ever written, so the stored key is already correct.
            RejectCandidate(providerKey, generation, provider, mirrorsImageRow);
            Logger.Warning("API key invalid for {Provider}, not saved ({Status})", provider, ex.StatusCode);
        }
        catch (Exception ex)
        {
            // Network error — same deliberate-write posture as the fetchError branch above.
            if (!CommitKeyAndOwnRow(providerKey, newKey, generation, provider))
                return;
            HasExistingKey = true;
            ApiKey = MaskedKeyPlaceholder;
            _enhancement.SelectedProvider = provider;
            ApiKeyStatus = ApiKeyStatus.Unknown;
            Logger.Warning(ex, "Could not validate API key for {Provider}, saved anyway", provider);
            _ = FetchModelsAsync();
        }
    }

    /// <summary>
    /// Commit a validated candidate AND confirm this flow still owns the row. Returns <c>false</c>
    /// when the caller must touch nothing further — superseded, write refused, or the row moved on.
    /// </summary>
    /// <remarks>
    /// <para>The ownership check is INSIDE this method deliberately. When it sat in the callers, the
    /// DPAPI-failure branch painted the row BEFORE the check ran, so a save begun for OpenAI and
    /// then switched to Groq wrote OpenAI's save error onto the Groq row — a post-await UI mutation
    /// that bypassed the very gate meant to prevent it (Codex ENH-17 verification round). Folding it
    /// in makes that ordering unexpressible rather than merely fixed.</para>
    ///
    /// <para>The one and only ordering check per flow. It may only ever GATE — if it is ever used to
    /// choose WHAT to write, the rollback has been reinvented.</para>
    /// </remarks>
    private bool CommitKeyAndOwnRow(string providerKey, string key, int generation, AIProvider provider)
    {
        if (!_apiKeys.TryCommitApiKey(providerKey, key, generation, out var result))
        {
            // K6: the only post-mortem evidence that a save was abandoned mid-flight.
            Logger.Information("API key save for {Provider} superseded by a newer write", provider);
            return false;
        }

        if (!result.IsSuccess())
        {
            // The slot was ours, but the write itself failed — a DPAPI refusal (F28/F44). The
            // previously stored key is untouched. Report it only on the row that asked.
            if (TextRowStillShows(provider))
            {
                ApiKeyStatus = ApiKeyStatus.Unknown;
                ModelFetchError = "Could not save the key on this device — try again.";
            }
            return false;
        }

        return TextRowStillShows(provider);
    }

    /// <summary>
    /// The provider refused the candidate. Nothing was written, so this only re-derives the row
    /// from whatever is actually stored — there is no rollback to perform.
    /// </summary>
    private void RejectCandidate(string providerKey, int generation, AIProvider provider, bool mirrorsImageRow)
    {
        // Nothing is written on this path, so there is no commit to gate it — but it still
        // repaints the row, and a superseded save must not paint "Invalid" over a newer save's
        // validated key.
        if (!_apiKeys.IsCurrentKeyWrite(providerKey, generation) || !TextRowStillShows(provider))
        {
            Logger.Information("Rejection for {Provider} superseded or the row moved on — left alone", provider);
            return;
        }
        LoadApiKey();
        ApiKeyStatus = ApiKeyStatus.Invalid;
        ModelFetchError = null; // the key status line owns auth messaging
        if (mirrorsImageRow)
            LoadImageApiKey();
    }
    [RelayCommand]
    public async Task SaveImageApiKeyAsync()
    {
        if (ImageApiKey == MaskedKeyPlaceholder)
            return;

        // Captured once — see the text row's comment for why this is key hygiene rather than a
        // UI nicety now that the only write happens after the await.
        var provider = SelectedImageProvider;
        var providerKey = provider.ToString().ToLowerInvariant();
        var mirrorsTextRow = SelectedProvider == provider;
        var newKey = ImageApiKey;

        var generation = _apiKeys.BeginKeyWrite(providerKey);

        if (string.IsNullOrWhiteSpace(newKey))
        {
            if (!_apiKeys.TryCommitApiKey(providerKey, "", generation, out _))
            {
                Logger.Information("Image key clear for {Provider} superseded by a newer write", provider);
                return;
            }
            HasExistingImageKey = false;
            ImageApiKeyStatus = ApiKeyStatus.Unknown;
            ImageModelFetchError = null;
            CancelPendingImageFetch();
            Logger.Information("API key cleared for image provider {Provider}", provider);
            AvailableImageModels.Clear();
            if (mirrorsTextRow)
            {
                LoadApiKey();
                ApiKeyStatus = ApiKeyStatus.Unknown;
                ModelFetchError = null;
                CancelPendingTextFetch();
                AvailableModels.Clear();
            }
            return;
        }

        var verdict = Helpers.ApiKeyFormat.Validate(Helpers.ApiKeyFormat.Normalize(newKey));
        if (verdict != Helpers.ApiKeyFormatVerdict.Ok)
        {
            ImageApiKeyStatus = ApiKeyStatus.Invalid;
            ImageModelFetchError = Helpers.ApiKeyFormat.Describe(verdict);
            Logger.Warning("Candidate API key for image provider {Provider} rejected: {Verdict}", provider, verdict);
            return;
        }

        CancelPendingImageFetch();
        if (mirrorsTextRow)
            CancelPendingTextFetch();
        ImageApiKeyStatus = ApiKeyStatus.Unknown;

        try
        {
            var (testModels, rawModelCount, fetchError) = await _enhancement.ValidateCandidateKeyAsync(
                provider, newKey, Providers.ModelCatalogQuery.Image, showAll: ShowAllImageModels, ct: default);

            if (fetchError != null)
            {
                if (!CommitImageKeyAndOwnRow(providerKey, newKey, generation, provider))
                    return;
                HasExistingImageKey = true;
                ImageApiKey = MaskedKeyPlaceholder;
                _enhancement.SelectedImageProvider = provider;
                ImageApiKeyStatus = ApiKeyStatus.Unknown;
                ImageModelFetchError = fetchError;
                AvailableImageModels.Clear();
                if (mirrorsTextRow)
                {
                    LoadApiKey();
                    ApiKeyStatus = ApiKeyStatus.Unknown;
                    ModelFetchError = fetchError;
                    AvailableModels.Clear();
                }
                Logger.Warning("API key for image provider {Provider} saved, but the model fetch failed for a non-auth reason", provider);
                return;
            }

            if (rawModelCount == 0)
            {
                RejectImageCandidate(providerKey, generation, provider, mirrorsTextRow);
                Logger.Warning("API key for image provider {Provider} returned no models — likely invalid, not saved", provider);
                return;
            }

            if (!CommitImageKeyAndOwnRow(providerKey, newKey, generation, provider))
                return;

            HasExistingImageKey = true;
            if (mirrorsTextRow)
                HasExistingKey = true;
            ImageApiKeyStatus = ApiKeyStatus.Valid;
            ImageModelFetchError = null;
            Logger.Information("API key saved for image provider {Provider}", provider);
            ImageApiKey = MaskedKeyPlaceholder;
            _enhancement.SelectedImageProvider = provider;

            AvailableImageModels.ReplaceAll(testModels);

            if (mirrorsTextRow)
            {
                LoadApiKey();
                ApiKeyStatus = ApiKeyStatus.Valid;
                ModelFetchError = null;
                _ = FetchModelsAsync();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                   or global::System.Net.HttpStatusCode.Forbidden)
        {
            RejectImageCandidate(providerKey, generation, provider, mirrorsTextRow);
            Logger.Warning("API key invalid for image provider {Provider}, not saved ({Status})", provider, ex.StatusCode);
        }
        catch (Exception ex)
        {
            if (!CommitImageKeyAndOwnRow(providerKey, newKey, generation, provider))
                return;
            HasExistingImageKey = true;
            ImageApiKey = MaskedKeyPlaceholder;
            _enhancement.SelectedImageProvider = provider;
            ImageApiKeyStatus = ApiKeyStatus.Unknown;
            Logger.Warning(ex, "Could not validate API key for image provider {Provider}, saved anyway", provider);
            _ = FetchImageModelsAsync();
        }
    }

    /// <summary>Image-row twin of <see cref="CommitKeyAndOwnRow"/> — see its remarks.</summary>
    private bool CommitImageKeyAndOwnRow(string providerKey, string key, int generation, AIProvider provider)
    {
        if (!_apiKeys.TryCommitApiKey(providerKey, key, generation, out var result))
        {
            Logger.Information("Image API key save for {Provider} superseded by a newer write", provider);
            return false;
        }

        if (!result.IsSuccess())
        {
            if (ImageRowStillShows(provider))
            {
                ImageApiKeyStatus = ApiKeyStatus.Unknown;
                ImageModelFetchError = "Could not save the key on this device — try again.";
            }
            return false;
        }

        return ImageRowStillShows(provider);
    }

    /// <summary>Image-row twin of <see cref="RejectCandidate"/> — nothing was written.</summary>
    private void RejectImageCandidate(string providerKey, int generation, AIProvider provider, bool mirrorsTextRow)
    {
        if (!_apiKeys.IsCurrentKeyWrite(providerKey, generation) || !ImageRowStillShows(provider))
        {
            Logger.Information("Image rejection for {Provider} superseded or the row moved on — left alone", provider);
            return;
        }
        LoadImageApiKey();
        ImageApiKeyStatus = ApiKeyStatus.Invalid;
        ImageModelFetchError = null;
        if (mirrorsTextRow)
            LoadApiKey();
    }

    /// <summary>
    /// True while THIS fetch invocation is still the current one for the current
    /// provider. Every fetch-owned observable mutation (model list, fetch error, key
    /// status, loading flag) must pass this — a stale fetch completing after a provider
    /// switch or a newer fetch must not touch the newer state (ENH-1 round-3 review:
    /// the old 401/403 and finally branches checked provider equality only, so a stale
    /// auth failure could mark the CURRENT provider invalid).
    /// </summary>
    private bool IsCurrentTextFetch(CancellationTokenSource cts, AIProvider provider)
        => ReferenceEquals(cts, _fetchCts) && provider == SelectedProvider;

    /// <summary>
    /// Does the TEXT row still show the provider this save was started for?
    /// </summary>
    /// <remarks>
    /// ENH-17 (Codex diff review). The storage write uses the CAPTURED provider — that is key
    /// hygiene and must never follow the combo. But the row's UI and
    /// <c>_enhancement.SelectedProvider</c> describe what the user is looking at NOW, so a save
    /// that completes after a provider switch must not touch them: it would paint the new row with
    /// the old provider's verdict and, worse, point the service at OpenAI while the UI reads Groq,
    /// so the next dictation would silently enhance through the wrong provider. Same rule the fetch
    /// path has always had via <see cref="IsCurrentTextFetch"/>.
    /// </remarks>
    private bool TextRowStillShows(AIProvider provider) => SelectedProvider == provider;

    /// <summary>Image-row twin of <see cref="TextRowStillShows"/>.</summary>
    private bool ImageRowStillShows(AIProvider provider) => SelectedImageProvider == provider;

    private bool IsCurrentImageFetch(CancellationTokenSource cts, AIProvider provider)
        => ReferenceEquals(cts, _fetchImageCts) && provider == SelectedImageProvider;

    /// <summary>
    /// Cancel + invalidate the in-flight text model fetch (key-clear paths). Nulling
    /// the CTS makes the identity guard reject the stale completion even for the SAME
    /// provider — without this, a fetch racing a key clear could repopulate the just-
    /// cleared models/error.
    /// </summary>
    private void CancelPendingTextFetch()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = null;
        // The invalidated fetch's finally is identity-guarded and will NOT clear the
        // flag anymore — reset it here or the spinner sticks (Codex diff round 5).
        IsLoadingModels = false;
    }

    private void CancelPendingImageFetch()
    {
        _fetchImageCts?.Cancel();
        _fetchImageCts?.Dispose();
        _fetchImageCts = null;
        IsLoadingImageModels = false;
    }

    [RelayCommand]
    public async Task FetchModelsAsync()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        var cts = _fetchCts = new CancellationTokenSource();
        var provider = SelectedProvider;

        IsLoadingModels = true;
        try
        {
            // unfiltered: ShowAllModels — NOT an unconditional true. Providers that publish model
            // metadata (Mistral) can only be curated where the JSON is still in hand, i.e. inside
            // the fetch, so asking for a filtered list is what lets that curation run. The
            // ShowAllModels escape hatch still returns everything; toggling it already re-fetches
            // (OnShowAllModelsChanged).
            var (allModels, _, fetchError) = await _enhancement.TryFetchAvailableModelsAsync(
                Providers.ModelCatalogQuery.Text, showAll: ShowAllModels, providerOverride: provider, ct: cts.Token);

            // If a newer call has replaced _fetchCts, our `cts` may be disposed — reading
            // cts.Token would throw ObjectDisposedException. Compare identities first.
            if (!IsCurrentTextFetch(cts, provider))
                return;

            ModelFetchError = fetchError;

            // Display-ready: ModelDisplayPolicy.ApplyDisplayPolicy already ran capability +
            // preview/date policy (and honoured ShowAllModels). Re-filtering here would undo
            // provider curation — for OpenRouter's image catalog it dropped 33 of 40.
            AvailableModels.ReplaceAll(allModels);

            if (fetchError != null)
            {
                // The provider refused/failed for a NON-auth reason (geo-block, outage,
                // timeout). A green "Key valid" beside the error line would lie; the key
                // may well be fine, so Unknown — not Invalid — is the honest status.
                ApiKeyStatus = ApiKeyStatus.Unknown;
            }
            else if (HasExistingKey)
            {
                // Models fetched successfully — key is valid
                ApiKeyStatus = ApiKeyStatus.Valid;
            }

            // "Received", not "Fetched {Total}": the fetch now pre-filters unless ShowAllModels is
            // on, so this count is what the service handed back — NOT the provider's catalog size.
            // The provider-side total is logged by the fetch itself.
            Logger.Information("Received {Received} models for text provider {Provider} ({Text} shown, showAll={ShowAll}, fetchError={HasError})",
                allModels.Count, provider, AvailableModels.Count, ShowAllModels, fetchError != null);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                   or global::System.Net.HttpStatusCode.Forbidden)
        {
            Logger.Warning("API key invalid for text provider {Provider}: {Status}", provider, ex.StatusCode);
            if (IsCurrentTextFetch(cts, provider))
            {
                ApiKeyStatus = ApiKeyStatus.Invalid;
                ModelFetchError = null; // the key status line owns auth messaging
            }
        }
        catch (Helpers.InvalidApiKeyFormatException ex)
        {
            // ENH-15: page-attach fetch over a malformed STORED key. No rollback here — nothing
            // was just saved — but the row must say so rather than fall into the silent generic
            // catch below, which is what left the owner with a dead provider and no explanation.
            // The reason is shown because, unlike the auth case, the key status line has no copy
            // that tells the user what to actually fix.
            Logger.Warning("Stored API key for text provider {Provider} has an invalid format: {Verdict}",
                provider, ex.Verdict);
            if (IsCurrentTextFetch(cts, provider))
            {
                ApiKeyStatus = ApiKeyStatus.Invalid;
                ModelFetchError = ex.UserMessage;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to fetch text models");
        }
        finally
        {
            if (IsCurrentTextFetch(cts, provider))
                IsLoadingModels = false;
        }
    }

    [RelayCommand]
    public async Task FetchImageModelsAsync()
    {
        _fetchImageCts?.Cancel();
        _fetchImageCts?.Dispose();
        var cts = _fetchImageCts = new CancellationTokenSource();
        var provider = SelectedImageProvider;

        IsLoadingImageModels = true;
        try
        {
            var (allModels, _, fetchError) = await _enhancement.TryFetchAvailableModelsAsync(
                Providers.ModelCatalogQuery.Image, showAll: ShowAllImageModels, providerOverride: provider, ct: cts.Token);

            // See comment in FetchModelsAsync: avoid touching potentially-disposed cts.Token.
            if (!IsCurrentImageFetch(cts, provider))
                return;

            ImageModelFetchError = fetchError;

            AvailableImageModels.ReplaceAll(allModels); // display-ready — see FetchModelsAsync

            if (fetchError != null)
                ImageApiKeyStatus = ApiKeyStatus.Unknown; // see FetchModelsAsync rationale
            else if (HasExistingImageKey)
                ImageApiKeyStatus = ApiKeyStatus.Valid;

            Logger.Information("Fetched {Total} models for image provider {Provider} ({Image} image, fetchError={HasError})",
                allModels.Count, provider, AvailableImageModels.Count, fetchError != null);
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                   or global::System.Net.HttpStatusCode.Forbidden)
        {
            Logger.Warning("API key invalid for image provider {Provider}: {Status}", provider, ex.StatusCode);
            if (IsCurrentImageFetch(cts, provider))
            {
                ImageApiKeyStatus = ApiKeyStatus.Invalid;
                ImageModelFetchError = null; // the key status line owns auth messaging
            }
        }
        catch (Helpers.InvalidApiKeyFormatException ex)
        {
            // ENH-15: mirrors the text fetch's arm — see the comment there.
            Logger.Warning("Stored API key for image provider {Provider} has an invalid format: {Verdict}",
                provider, ex.Verdict);
            if (IsCurrentImageFetch(cts, provider))
            {
                ImageApiKeyStatus = ApiKeyStatus.Invalid;
                ImageModelFetchError = ex.UserMessage;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to fetch image models");
        }
        finally
        {
            if (IsCurrentImageFetch(cts, provider))
                IsLoadingImageModels = false;
        }
    }

    /// <summary>
    /// Create a blank DETACHED prompt for the "Add Prompt" flow — deliberately NOT added to the
    /// collection. The edit dialog opens on it; only <see cref="CommitNewPrompt"/> adds + persists
    /// it, and only once the user saves valid content. Keeping it detached until commit means a
    /// cancelled add — or a dialog that throws before it returns — can never leave an invisible
    /// blank prompt in the collection that a later save would then persist (Codex diff review r1).
    /// Blank Title/PromptText let the dialog show its placeholder watermarks instead of filler
    /// text. A user prompt carries no SeedKey, so the re-seed never manages it (UPD-4).
    /// </summary>
    public CustomPrompt CreateDraftPrompt() => new()
    {
        Title = "",
        PromptText = "",
        IsActive = false
    };

    /// <summary>
    /// Commit a draft from <see cref="CreateDraftPrompt"/>: set its title/text, add it to the
    /// collection, and persist — only once the user has confirmed valid content, so an abandoned
    /// or failed add never touches the collection.
    /// </summary>
    public void CommitNewPrompt(CustomPrompt prompt, string title, string promptText)
    {
        prompt.Title = title;
        prompt.PromptText = promptText;
        Prompts.Add(prompt);
        SavePrompts();
    }

    /// <summary>
    /// Add a new prompt from a template. Skips if a prompt with the same title OR the same
    /// SeedKey already exists — the SeedKey guard prevents a duplicate provenance record when the
    /// user renamed the seeded default and its template title became "available" again (UPD-4,
    /// Codex diff r2: a duplicate SeedKey would drop both from the reseed's unique index and null
    /// the App-Mode links pointing at it).
    /// </summary>
    public bool AddFromTemplate(TemplatePrompt template)
    {
        if (Prompts.Any(p => p.Title == template.Title
                || (!string.IsNullOrEmpty(template.Key) && p.SeedKey == template.Key)))
        {
            Logger.Information("Template '{Title}' already exists (by title or seed key), skipping", template.Title);
            return false;
        }

        var prompt = template.ToCustomPrompt();
        Prompts.Add(prompt);
        SavePrompts();
        Logger.Information("Prompt added from template: {Title}", template.Title);
        return true;
    }

    /// <summary>
    /// Resolve the enhancement an App-Mode template should link to by its stable SeedKey, adding it
    /// from the shipped default when absent — so adding e.g. Outlook restores the E-mail enhancement
    /// the user had removed. Deterministic, UI-free (the App-Mode add flow decides the notice from
    /// the outcome). Resolves ONLY against <see cref="PromptTemplates.All"/> (the reseed-managed set)
    /// so an auto-added prompt is one the re-seed keeps in sync. The notice title comes from the
    /// shipped template, never user text (privacy: keeps user prompt names out of logs).
    /// </summary>
    public DefaultEnhancementResolution ResolveOrAddDefaultEnhancement(string? seedKey)
    {
        if (string.IsNullOrEmpty(seedKey))
            return new(null, DefaultEnhancementOutcome.NoKey, null);

        var template = PromptTemplates.All.FirstOrDefault(t => t.Key == seedKey);
        var title = template?.Title;

        var existing = Prompts.Where(p => p.SeedKey == seedKey).ToList();
        if (existing.Count == 1)
            return new(existing[0].Id, DefaultEnhancementOutcome.LinkedExisting, title);
        if (existing.Count > 1)
            return new(null, DefaultEnhancementOutcome.AmbiguousSeedKey, title);

        // None present. Only a reseed-managed template key is auto-addable.
        if (template == null)
            return new(null, DefaultEnhancementOutcome.UnknownKey, null);

        // AddFromTemplate returns false on a title OR SeedKey collision; the SeedKey case is already
        // ruled out (count == 0), so a false here means a same-title UNSEEDED prompt blocks it.
        if (!AddFromTemplate(template))
            return new(null, DefaultEnhancementOutcome.TitleConflict, title);

        var added = Prompts.FirstOrDefault(p => p.SeedKey == seedKey);
        return added != null
            ? new(added.Id, DefaultEnhancementOutcome.Added, title)
            : new(null, DefaultEnhancementOutcome.TitleConflict, title); // defensive; should not happen
    }

    /// <summary>
    /// Get templates that haven't been added yet. Shows all templates (including translations)
    /// minus any prompts the user already has with a matching title OR SeedKey (so a renamed
    /// seeded default doesn't re-offer its template and create a duplicate SeedKey — UPD-4).
    /// </summary>
    public TemplatePrompt[] GetAvailableTemplates()
    {
        var allTitles = Prompts.Select(p => p.Title).ToHashSet();
        var allSeedKeys = Prompts.Where(p => !string.IsNullOrEmpty(p.SeedKey)).Select(p => p.SeedKey!).ToHashSet();
        return PromptTemplates.Extended
            .Where(t => !allTitles.Contains(t.Title) && !allSeedKeys.Contains(t.Key))
            .ToArray();
    }

    [RelayCommand]
    public void DeletePrompt(CustomPrompt prompt)
    {
        Prompts.Remove(prompt);
        SavePrompts();

        // Re-register hotkeys so the deleted prompt's hotkey is removed
        if (!string.IsNullOrEmpty(prompt.Hotkey))
            ((App)App.Current).RegisterPromptHotkeys();
    }

    [RelayCommand]
    public void SetActivePrompt(CustomPrompt prompt)
    {
        // Toggle: if already active, deactivate it; otherwise activate it and deactivate all others
        var wasActive = prompt.IsActive;
        foreach (var p in Prompts)
            p.IsActive = false;
        if (!wasActive)
            prompt.IsActive = true;
        SavePrompts();
    }

    /// <summary>
    /// Update the trigger words for a prompt.
    /// </summary>
    public void UpdateTriggerWords(CustomPrompt prompt, List<string> triggerWords)
    {
        prompt.TriggerWords = triggerWords;
        SavePrompts();
        // {Prompt}/{TriggerWords} carry user-authored text — both names are redacted in the
        // Sentry sub-logger + breadcrumb scrub (local file keeps detail). NOT the generic {Title}.
        Logger.Information("Trigger words updated: userTerm={Prompt} | {TriggerWords}",
            Helpers.LogValueSanitizer.SingleLine(prompt.Title), Helpers.LogValueSanitizer.SingleLine(string.Join(", ", triggerWords)));
    }

    /// <summary>
    /// Update prompt title and text (for editing).
    /// </summary>
    public void UpdatePrompt(CustomPrompt prompt, string title, string promptText)
    {
        prompt.Title = title;
        prompt.PromptText = promptText;
        SavePrompts();
    }

    /// <summary>
    /// Fetch models for a specific provider (for the per-prompt provider override dialog).
    /// Returns a filtered list without touching the main AvailableModels collection.
    /// </summary>
    public async Task<List<string>> FetchModelsForProviderAsync(AIProvider provider, bool isImage)
    {
        try
        {
            // unfiltered: isImage — the text side asks for a filtered list so provider-metadata
            // curation (Mistral) runs inside the fetch; image discovery stays unfiltered and is
            // narrowed by ModelDisplayPolicy.ApplyDisplayPolicy inside the service (dated
            // snapshots only — previews are shown). This said "narrowed by ShouldShowImageModel
            // below"; there is no such narrowing below, and that helper has no production call
            // site at all. Corrected 2026-08-06 (weekly model review).
            // Display-ready from the service (ShowAllModels deliberately not honoured here — this
            // path is the per-prompt override dialog, where the user explicitly picked a provider).
            return await _enhancement.FetchAvailableModelsAsync(
                isImage ? Providers.ModelCatalogQuery.Image : Providers.ModelCatalogQuery.Text,
                showAll: false, providerOverride: provider);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to fetch models for provider {Provider}", provider);
            return new();
        }
    }

    /// <summary>
    /// Update the prompt type, provider override, model override, and image-generation knobs.
    /// </summary>
    public void UpdatePromptConfig(
        CustomPrompt prompt,
        bool isImageGeneration,
        string? providerOverride,
        string? modelOverride,
        string? imageAspect = null,
        string? imageSizeTier = null,
        bool askImageSize = false,
        string? imageQuality = null)
    {
        prompt.IsImageGeneration = isImageGeneration;
        prompt.ProviderOverride = string.IsNullOrWhiteSpace(providerOverride) ? null : providerOverride.Trim();
        prompt.ModelOverride = string.IsNullOrWhiteSpace(modelOverride) ? null : modelOverride.Trim();
        prompt.ImageAspect = isImageGeneration && !string.IsNullOrWhiteSpace(imageAspect) ? imageAspect.Trim() : null;
        prompt.ImageSizeTier = isImageGeneration && !string.IsNullOrWhiteSpace(imageSizeTier) ? imageSizeTier.Trim() : null;
        prompt.AskImageSize = isImageGeneration && askImageSize;
        prompt.ImageQuality = isImageGeneration && !string.IsNullOrWhiteSpace(imageQuality) ? imageQuality.Trim() : null;
        SavePrompts();
        Logger.Information("Prompt config updated: type={Type}, provider={Provider}, model={Model}, aspect={Aspect}, tier={Tier}, quality={Quality}, askOptions={Ask} userTerm={Prompt}",
            isImageGeneration ? "Image" : "Text",
            prompt.ProviderOverride ?? "(default)", prompt.ModelOverride ?? "(default)",
            prompt.ImageAspect ?? "(auto)", prompt.ImageSizeTier ?? "(auto)", prompt.ImageQuality ?? "(auto)", prompt.AskImageSize,
            Helpers.LogValueSanitizer.SingleLine(prompt.Title));
    }

    /// <summary>
    /// Update the hotkey for a prompt. Pass null or empty to clear.
    /// </summary>
    public void UpdateHotkey(CustomPrompt prompt, string? hotkey)
    {
        prompt.Hotkey = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey.Trim();
        SavePrompts();
        // Re-register all prompt hotkeys with HotkeyService
        ((App)App.Current).RegisterPromptHotkeys();
        Logger.Information("Hotkey updated: hotkey={Hotkey} userTerm={Prompt}",
            prompt.Hotkey ?? "(none)", Helpers.LogValueSanitizer.SingleLine(prompt.Title));
    }

    private void SavePrompts()
    {
        _enhancement.SavePrompts(Prompts.ToList());
    }
}
