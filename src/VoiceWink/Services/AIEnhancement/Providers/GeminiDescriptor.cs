using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.Clients;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Gemini provider — text via OpenAI-compatible endpoint, image via the native Gemini image API
/// (Imagen support removed ahead of Google's deprecation).
/// The model fetch checks the key on the native /models endpoint before listing models from the
/// OpenAI-compatible one — see <see cref="FetchAvailableModelsAsync"/>.
/// </summary>
public sealed class GeminiDescriptor : IAIProviderDescriptor
{
    private static ILogger Logger => Log.ForContext<GeminiDescriptor>();

    public AIProvider Provider => AIProvider.Gemini;

    public bool SupportsImageGeneration => true;

    // Nano Banana 2. MUST equal ImageOptions.DefaultModelFor(Gemini) — that one wins at runtime
    // (AIEnhancementService.DefaultImageModelFor coalesces to this only if it returns null), so a
    // disagreement here is a value nothing reads. DefaultsAgreementTests pins the pair.
    public string DefaultImageModel => "gemini-3.1-flash-image";

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
        var client = new GeminiImageClient(httpFactory.CreateClient("images"), config);
        return client.GenerateAsync(description, imageAspect, imageSizeTier, imageQuality, references, ct);
    }

    public async Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
    {
        // Check the key on the NATIVE endpoint first. ENH-29: both endpoints answer an invalid key
        // with HTTP 400 (measured 2026-10-07 — the OpenAI-compatible one no longer lets a bad key
        // through), but only the native answer names the key in a structured reason, which
        // ProviderApiException turns into a 401. Asking the OpenAI-compatible endpoint first
        // threw its prose-only 400, which read as an outage, so the wrong key was saved.
        var baseUrl = config.GetBaseUrl().TrimEnd('/');
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models?pageSize=1"))
        {
            request.Headers.Add("x-goog-api-key", config.ApiKey);
            var http = httpFactory.CreateClient("ai");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            await response.EnsureSuccessOrLogAndThrowAsync("Gemini native key validation", Logger, ct).ConfigureAwait(false);
        }

        return await OpenAICompatibleModelFetch.FetchAsync(httpFactory, config, query, unfiltered, ct).ConfigureAwait(false);
    }
}
