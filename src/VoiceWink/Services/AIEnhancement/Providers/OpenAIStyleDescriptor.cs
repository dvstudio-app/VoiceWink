using VoiceWink.Services.AIEnhancement.Clients;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Generic descriptor for providers that use the OpenAI-compatible chat-completions API
/// and (optionally) image generation — OpenAI via the /images/generations + /images/edits
/// endpoints, OpenRouter via its unified Image API (see <see cref="ImageGenerationClient"/>).
/// Covers OpenAI, Groq, Mistral, OpenRouter, and Cerebras.
/// </summary>
public sealed class OpenAIStyleDescriptor : IAIProviderDescriptor
{
    public AIProvider Provider { get; }
    public bool SupportsImageGeneration { get; }
    public string DefaultImageModel => "gpt-image-2";

    public OpenAIStyleDescriptor(AIProvider provider, bool supportsImageGeneration)
    {
        Provider = provider;
        SupportsImageGeneration = supportsImageGeneration;
    }

    public Task<string> EnhanceTextAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var client = new OpenAICompatibleClient(httpFactory.CreateClient("ai"), config);
        return client.EnhanceAsync(systemPrompt, userText, ct);
    }

    public Task<byte[]> GenerateImageAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string description,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken ct)
    {
        if (!SupportsImageGeneration)
            throw new NotSupportedException($"{Provider} does not support image generation.");

        var client = new ImageGenerationClient(httpFactory.CreateClient("images"), config);
        return client.GenerateAsync(description, imageAspect, imageSizeTier, imageQuality, references, ct);
    }

    public Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
        => OpenAICompatibleModelFetch.FetchAsync(httpFactory, config, query, unfiltered, ct);
}
