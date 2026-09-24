namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Provider-specific behavior for AI text enhancement, image generation, and model discovery.
/// One descriptor per AIProvider value. Registered in DI, looked up via AIProviderRegistry.
/// </summary>
public interface IAIProviderDescriptor
{
    AIProvider Provider { get; }

    bool SupportsImageGeneration { get; }

    /// <summary>
    /// LAI-1: false for a provider that works WITHOUT a stored API key (a user-run server). Every
    /// "no key, so stop" site consults this — the model fetch, the provider persist on the
    /// Enhancement page — and a keyless provider's ids skip the catalog display policy
    /// (<c>ModelDisplayPolicy</c>), which exists to curate the cloud catalogs and would refuse an
    /// Ollama id like <c>qwen2.5:7b-instruct</c> for the substring "instruct".
    /// </summary>
    bool RequiresApiKey => true;

    /// <summary>Model ID used when the user hasn't selected an image model yet.</summary>
    string DefaultImageModel { get; }

    Task<string> EnhanceTextAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string systemPrompt,
        string userText,
        CancellationToken ct);

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when <see cref="SupportsImageGeneration"/> is false.
    /// Inputs are the user's choices on the three independent dropdowns; the implementation
    /// translates them to provider-specific request parameters (W×H for OpenAI gpt-image,
    /// aspectRatio + imageSize for Gemini-native). <paramref name="references"/> is the
    /// validated reference set in selection order (ENH-6f) — empty = no-reference request.
    /// </summary>
    Task<byte[]> GenerateImageAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string description,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken ct);

    /// <summary>
    /// Fetches the provider's model list. Returns the display list AND the raw catalog size —
    /// see <see cref="ProviderModelList"/> for why key-validity decisions must use the latter.
    /// </summary>
    Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct);
}
