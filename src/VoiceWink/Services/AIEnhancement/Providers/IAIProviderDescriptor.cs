namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Provider-specific behavior for AI text enhancement, image generation, and model discovery.
/// One descriptor per AIProvider value. Registered in DI, looked up via AIProviderRegistry.
/// </summary>
public interface IAIProviderDescriptor
{
    AIProvider Provider { get; }

    bool SupportsImageGeneration { get; }

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
