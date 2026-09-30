using VoiceWink.Services.AIEnhancement.LocalEngine;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// LAI-4: "On this PC" — a catalog model run by the bundled llama-server. Keyless, text only, no
/// address; the engine resolves the child's loopback port and per-child key at request time
/// (<see cref="OnThisPcEngine"/>), so nothing about the endpoint is ever stored. A registry built
/// without an engine (tests) keeps a descriptor for the enum member and refuses every call.
/// </summary>
internal sealed class OnThisPcDescriptor : IAIProviderDescriptor
{
    private readonly OnThisPcEngine? _engine;

    internal OnThisPcDescriptor(OnThisPcEngine? engine) => _engine = engine;

    public AIProvider Provider => AIProvider.OnThisPc;
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
        // Not offered here (the feature off) with the provider still stored - imported
        // settings: refused, never sent anywhere else.
        => _engine is null || !OnThisPcAvailability.IsOffered
            ? throw new InvalidOperationException(OnThisPcEngine.UnavailableMessage)
            : _engine.EnhanceAsync(config, systemPrompt, userText, ct);

    public Task<byte[]> GenerateImageAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        string description,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken ct)
        => throw new NotSupportedException("The local engine does not generate images.");

    /// <summary>Recording started: see <see cref="OnThisPcEngine.Prepare"/>.</summary>
    internal void Prepare(string modelId, string? warmSystemPrompt)
    {
        if (OnThisPcAvailability.IsOffered)
            _engine?.Prepare(modelId, warmSystemPrompt);
    }

    /// <summary>The installed catalog models, catalog order; no request is made.</summary>
    public Task<ProviderModelList> FetchAvailableModelsAsync(
        IHttpClientFactory httpFactory,
        AIProviderConfig config,
        ModelCatalogQuery query,
        bool unfiltered,
        CancellationToken ct)
    {
        if (query != ModelCatalogQuery.Text || _engine is null)
            return Task.FromResult(ProviderModelList.Empty);
        var ids = _engine.InstalledModelIds().ToList();
        return Task.FromResult(new ProviderModelList(ids, ids.Count, Curated: true));
    }
}
