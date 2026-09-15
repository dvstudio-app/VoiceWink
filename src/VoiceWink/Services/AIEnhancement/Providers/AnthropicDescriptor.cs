using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.Clients;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>Anthropic provider — uses the native Messages API. No image generation.</summary>
public sealed class AnthropicDescriptor : IAIProviderDescriptor
{
    private static ILogger Logger => Log.ForContext<AnthropicDescriptor>();

    public AIProvider Provider => AIProvider.Anthropic;

    public bool SupportsImageGeneration => false;

    public string DefaultImageModel => "";

    public Task<string> EnhanceTextAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string systemPrompt,
        string userText,
        CancellationToken ct)
    {
        var client = new AnthropicClient(httpFactory.CreateClient("ai"), config);
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
        => throw new NotSupportedException("Anthropic does not support image generation.");

    public async Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.GetBaseUrl()}/models");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var http = httpFactory.CreateClient("ai");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Anthropic models", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);

        // Anthropic's transport differs (x-api-key + a version header, not Bearer), but its
        // RESPONSE is the same `data[].id` shape and its display rule was character-for-character
        // the OpenAI-compatible one. It used to carry its own copy of that loop; ENH-20 deleted the
        // copy rather than extracting a second parser, because two hand-written parsers for one
        // shape is the drift this work exists to remove. Anthropic takes no curation branch, so
        // ParseAndCurate applies exactly the rule this method used to apply inline.
        //
        // The clock is passed for signature completeness only — nothing on the Anthropic path reads
        // it (no retirement field is published), and passing UTC keeps it consistent with the other
        // caller rather than inventing a second convention.
        return ProviderCatalogParser.ParseAndCurate(
            json, AIProvider.Anthropic, query, unfiltered, DateOnly.FromDateTime(DateTime.UtcNow)).List;
    }
}
