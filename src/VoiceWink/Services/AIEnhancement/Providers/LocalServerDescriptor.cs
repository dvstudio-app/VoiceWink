using VoiceWink.Services.AIEnhancement.Clients;
using VoiceWink.Services.Http;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// LAI-1: a user-run AI server (Ollama, LM Studio, any OpenAI-compatible endpoint). Keyless, text
/// only, over its own no-retry, no-proxy, no-redirect HttpClient (<c>local-ai</c>).
/// </summary>
public sealed class LocalServerDescriptor : IAIProviderDescriptor
{
    public AIProvider Provider => AIProvider.LocalServer;
    public bool SupportsImageGeneration => false;
    public bool RequiresApiKey => false;

    /// <summary>Unused: <see cref="SupportsImageGeneration"/> is false.</summary>
    public string DefaultImageModel => "";

    public Task<string> EnhanceTextAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string systemPrompt,
        string userText,
        CancellationToken ct)
        => new LocalServerClient(httpFactory.CreateClient(VoiceWinkHttpClients.LocalAi), config)
            .EnhanceAsync(systemPrompt, userText, ct);

    public Task<byte[]> GenerateImageAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string description,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken ct)
        => throw new NotSupportedException("A local server does not generate images.");

    public Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
        => query == ModelCatalogQuery.Text
            ? new LocalServerClient(httpFactory.CreateClient(VoiceWinkHttpClients.LocalAi), config)
                .FetchModelsAsync(unfiltered, ct)
            // No image models: an empty answer, not a request.
            : Task.FromResult(ProviderModelList.Empty);
}
