using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Shared model-list fetcher for providers that expose an OpenAI-compatible /models endpoint.
/// Gemini wraps this with extra validation since its compatible endpoint doesn't enforce auth.
/// </summary>
internal static class OpenAICompatibleModelFetch
{
    private static ILogger Logger => Log.ForContext(typeof(OpenAICompatibleModelFetch));

    public static async Task<ProviderModelList> FetchAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
    {
        var baseUrl = config.GetBaseUrl().TrimEnd('/');

        // Gemini routes the OpenAI-compatible models list under /openai/models.
        var url = config.Provider == AIProvider.Gemini
            ? $"{baseUrl}/openai/models"
            : $"{baseUrl}/models";

        // OpenRouter serves its catalogs per surface. TEXT is the modality query on the general
        // list (the plain list omits image models entirely, so it ≈ the text catalog). IMAGE is
        // the dedicated Image API catalog (/images/models) since 2026-07-31 — OpenRouter adds new
        // image models EXCLUSIVELY to the dedicated API, so the old ?output_modalities=image query
        // on the general list would go stale. Its entries carry the same data[].id +
        // architecture.input_modalities/output_modalities shape, so parsing below is shared.
        // Every other provider's URL is untouched.
        if (config.Provider == AIProvider.OpenRouter)
        {
            url = query == ModelCatalogQuery.Image
                ? $"{baseUrl}/images/models"
                : $"{baseUrl}/models?output_modalities=text";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

        var http = httpFactory.CreateClient("ai");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync($"{config.Provider} models", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);

        // The parse + curation is ProviderCatalogParser's, and it is pure (ENH-20). It used to live
        // here, inline, which meant /vw-model-review could not run it and re-implemented it in
        // PowerShell instead — a transcription that broke on four consecutive weekly runs. The
        // clock is read HERE and injected, so the parser stays deterministic for its tests and for
        // the harness.
        var parsed = ProviderCatalogParser.ParseAndCurate(
            json, config.Provider, query, unfiltered, DateOnly.FromDateTime(DateTime.UtcNow));

        LogCuration(config.Provider, query, parsed);
        return parsed.List;
    }

    /// <summary>
    /// The per-provider curation log lines, emitted here rather than inside the parser — a type
    /// that references Serilog cannot be linked into <c>tools/model-filter-sim</c>, which is the
    /// whole reason the parse was extracted. The shapes are unchanged from when they sat inline.
    ///
    /// <para>Only a CURATED result has anything to say: <see cref="ProviderModelList.Curated"/> is
    /// true exactly when a curation class ran, which is also exactly when
    /// <see cref="ParsedProviderCatalog.CuratedInputCount"/> is meaningful.</para>
    /// </summary>
    private static void LogCuration(AIProvider provider, ModelCatalogQuery query, ParsedProviderCatalog parsed)
    {
        if (!parsed.List.Curated)
            return;

        var total = parsed.CuratedInputCount;
        var curated = parsed.List.Models.Count;

        switch (provider)
        {
            case AIProvider.Mistral:
                if (curated == 0 && total > 0)
                {
                    // Not an error and not corrected here — curation is honest, and key validity is
                    // judged on RawCount by the caller. Worth a Warning because the realistic cause is
                    // Mistral changing its capability shape.
                    Logger.Warning("Mistral models: {Total} returned, 0 survived curation", total);
                }
                else
                {
                    Logger.Information("Mistral models: total={Total} curated={Curated}", total, curated);
                }
                break;

            case AIProvider.OpenRouter:
                if (curated == 0 && total > 0)
                {
                    // Same posture as the Mistral warning above: not an error, not corrected here, and
                    // key validity is still judged on RawCount by the caller. Worth a Warning because a
                    // full catalog curating to nothing means a RULE is misfiring across every row —
                    // the realistic cause being an OpenRouter schema change under one of the fields
                    // curation reads (modalities, expiration_date).
                    Logger.Warning("OpenRouter models ({Query}): {Total} returned, 0 survived curation",
                        query, total);
                }
                Logger.Information("OpenRouter models ({Query}): total={Total} curated={Curated} capabilities={Capabilities}",
                    query, total, curated, parsed.List.ImageCapabilities?.Count ?? 0);
                break;

            case AIProvider.Gemini:
                Logger.Information("Gemini models: total={Total} curated={Curated}", total, curated);
                break;

            case AIProvider.Cerebras:
                // ENH-25: the withdrawn-id table is the only rule beyond the baseline, so the gap
                // between the two numbers is what the weekly model review reads it for.
                Logger.Information("Cerebras models: total={Total} curated={Curated}", total, curated);
                break;
        }
    }
}

