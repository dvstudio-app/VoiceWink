using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Enums;
using VoiceWink.Services.System;
using VoiceWink.Services.Transcription.Clients;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Selects ITranscriptionService implementation by model provider.
/// </summary>
public sealed class TranscriptionServiceRegistry
{
    private static ILogger Logger => Log.ForContext<TranscriptionServiceRegistry>();

    private readonly LocalModelPreparer _localModels;
    private readonly ApiKeyManager _apiKeys;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SettingsService _settings;

    private readonly object _cloudCacheLock = new();
    private ITranscriptionService? _cachedCloudService;
    private string? _cachedCloudModelName;
    // Store the DPAPI-encrypted blob from settings (not the plaintext key) so key rotation
    // is detected without holding a plaintext key as a process-lifetime field.
    private string? _cachedCloudApiKeyBlob;

    public TranscriptionServiceRegistry(
        LocalModelPreparer localModels,
        ApiKeyManager apiKeys,
        IHttpClientFactory httpFactory,
        SettingsService settings)
    {
        _localModels = localModels;
        _apiKeys = apiKeys;
        _httpFactory = httpFactory;
        _settings = settings;
    }

    /// <summary>
    /// Get the transcription service for the currently selected model,
    /// or for the specified <paramref name="modelOverride"/> if non-null.
    /// The override does NOT mutate SettingsService — it is transient for this call only.
    /// </summary>
    public ITranscriptionService GetService(string? modelOverride = null)
    {
        var modelName = modelOverride
            ?? _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);

        // Canonicalize FIRST (PRM-3): any persisted/imported casing normalizes to
        // catalog spelling, so the exact-match lookup below stays sound and an
        // oddly-cased cloud selection can never silently fall back to local Whisper.
        modelName = CloudModels.Canonicalize(modelName);

        // Check if it's a cloud model. This stays FIRST and stays ahead of the local lookup: a
        // retired or oddly-cased cloud name must reach its cloud successor via Canonicalize above
        // and never fall through to a local runtime.
        var cloudModel = CloudModels.Models.FirstOrDefault(m => m.Name == modelName);
        if (cloudModel != null)
        {
            return GetCloudService(cloudModel);
        }

        // Local: ask the ONE authority which runtime owns this name. Previously this returned
        // `_whisper` for anything unrecognised, which is a silent substitution — harmless while
        // every local model was Whisper, and a wrong-engine transcription the moment it is not.
        var binding = _localModels.TryResolve(modelName);
        if (binding is not null)
            return binding.Service;

        // Fail-closed. Callers are expected to have gone through LocalModelPreparer.PrepareAsync
        // and handled UnknownModel with their own copy; reaching here means one did not, and
        // substituting a model the user did not choose is worse than throwing.
        Logger.Warning("No transcription runtime serves the selected model");
        throw new UnknownTranscriptionModelException(modelName);
    }

    /// <summary>
    /// Whether a cloud client for this provider could be constructed — i.e. a key is stored for it
    /// (TRN-17, the retry picker's "which models can run right now" half).
    ///
    /// <para>Lives here rather than on the caller because the lowercase-provider convention is
    /// <see cref="GetCloudService"/>'s, and every other site restates it
    /// (<c>provider.ToString().ToLowerInvariant()</c> appears in ModelsPage, EnhancementViewModel and
    /// OnboardingPage). One more copy in a ViewModel is one more place for the two to disagree about
    /// which provider a key belongs to.</para>
    ///
    /// <para>Deliberately asks <c>HasApiKey</c>, which DECRYPTS: a stored-but-undecryptable blob is
    /// not a runnable provider, and the cheaper settings-presence check would offer a model that
    /// fails at the first request.</para>
    /// </summary>
    public bool HasKeyFor(ModelProvider provider)
        => _apiKeys.HasApiKey(provider.ToString().ToLowerInvariant());

    public ITranscriptionService GetCloudService(TranscriptionModelInfo model)
    {
        var providerKey = model.Provider.ToString().ToLowerInvariant();
        // Read the encrypted blob from settings — no DPAPI decryption needed for cache
        // comparison, so no plaintext key is held in memory when the cache hits.
        var apiKeyBlob = _settings.GetString(AppDefaults.ApiKey(providerKey), "");

        lock (_cloudCacheLock)
        {
            // Return cached client if provider/model and API key (blob) haven't changed.
            // Comparing the encrypted blob detects key rotation without storing plaintext.
            if (_cachedCloudService != null
                && _cachedCloudModelName == model.Name
                && _cachedCloudApiKeyBlob == apiKeyBlob)
            {
                return _cachedCloudService;
            }

            Logger.Information("Creating cloud service for {Provider}/{Model}", model.Provider, model.Name);

            // Decrypt the key only when we actually need to construct a new client.
            var apiKey = _apiKeys.GetApiKey(providerKey) ?? "";

            // ENH-15: the same pre-send boundary the enhancement path gets in
            // AIEnhancementService.BuildConfig. Save-time validation stops NEW malformed keys at
            // every surface including the Models page, so what remains here is a key written by a
            // build that predates it — which would otherwise reach the four cloud clients and fail
            // as a statusless network error on every live and file transcription, forever. Empty
            // passes through untouched: "no key set" is a legitimate state the callers handle.
            if (!string.IsNullOrEmpty(apiKey))
            {
                var verdict = Helpers.ApiKeyFormat.Validate(apiKey);
                if (verdict != Helpers.ApiKeyFormatVerdict.Ok)
                    throw new Helpers.InvalidApiKeyFormatException(model.Provider.ToString(), verdict);
            }

            _cachedCloudService = CreateCloudClient(model, apiKey);
            _cachedCloudModelName = model.Name;
            _cachedCloudApiKeyBlob = apiKeyBlob;
            return _cachedCloudService;
        }
    }

    private ITranscriptionService CreateCloudClient(TranscriptionModelInfo model, string apiKey)
    {
        var http = _httpFactory.CreateClient("transcription");
        return model.Provider switch
        {
            ModelProvider.Groq => new GroqClient(http, apiKey, model.Name),
            // The "Send dictionary and trigger words" toggles are read per request
            // through the Funcs (PRM-3) — the cached client must react to a flip
            // without reconstruction. Both default ON (owner decision 2026-07-18).
            // GetBoolDefaulted, not a literal: the shipped default belongs to AppDefaults, and a
            // literal here is only correct until someone changes one — at which point the request
            // path and the Models-page switch disagree on a malformed stored value. That is not
            // hypothetical; it happened to the OpenAI toggle within a day of it shipping.
            ModelProvider.Deepgram => new DeepgramClient(http, apiKey, model.Name,
                () => _settings.GetBoolDefaulted(AppDefaults.DeepgramKeytermsEnabled)),
            ModelProvider.ElevenLabs => new ElevenLabsClient(http, apiKey, model.Name,
                () => _settings.GetBoolDefaulted(AppDefaults.ElevenLabsKeytermsEnabled)),
            ModelProvider.OpenAI => new OpenAITranscriptionClient(http, apiKey, model.Name),
            // Unreachable today — every provider in CloudModels has a case above — and it throws
            // rather than falling back to local Whisper, which is what it used to do. A cloud model
            // whose provider has no client is a catalog/code mismatch; quietly transcribing the
            // user's cloud selection on a LOCAL model instead is the same silent-substitution class
            // this PR removes elsewhere, and here it would also mean audio not going where the user
            // expected. Loud is correct.
            _ => throw new UnknownTranscriptionModelException(model.Name)
        };
    }
}
