namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Lookup table for <see cref="IAIProviderDescriptor"/>. DI-registered as a singleton.
/// Built from all <see cref="IAIProviderDescriptor"/> instances registered in the container.
/// </summary>
public sealed class AIProviderRegistry
{
    private readonly Dictionary<AIProvider, IAIProviderDescriptor> _byProvider;

    public AIProviderRegistry(IEnumerable<IAIProviderDescriptor> descriptors)
    {
        _byProvider = descriptors.ToDictionary(d => d.Provider);
    }

    public IAIProviderDescriptor Get(AIProvider provider)
    {
        if (!_byProvider.TryGetValue(provider, out var descriptor))
            throw new InvalidOperationException($"No descriptor registered for AIProvider.{provider}");
        return descriptor;
    }

    public IReadOnlyCollection<IAIProviderDescriptor> All => _byProvider.Values;

    public IReadOnlyCollection<AIProvider> ImageCapableProviders =>
        _byProvider.Values.Where(d => d.SupportsImageGeneration).Select(d => d.Provider).ToArray();

    public bool SupportsImageGeneration(AIProvider provider)
        => _byProvider.TryGetValue(provider, out var descriptor) && descriptor.SupportsImageGeneration;

    /// <summary>
    /// Build the standard production registry with all 7 supported providers.
    /// Used by DI registration and by tests that need a working registry.
    /// </summary>
    public static AIProviderRegistry CreateDefault() => new(new IAIProviderDescriptor[]
    {
        new AnthropicDescriptor(),
        new OpenAIStyleDescriptor(AIProvider.OpenAI, supportsImageGeneration: true),
        new GeminiDescriptor(),
        new OpenAIStyleDescriptor(AIProvider.Groq, supportsImageGeneration: false),
        new OpenAIStyleDescriptor(AIProvider.Mistral, supportsImageGeneration: false),
        // OpenRouter: ALL image generation flows through `ImageGenerationClient` against the
        // documented unified Image API (`POST {base}/images`) since 2026-07-31 — the dedicated
        // API is the only surface OpenRouter adds new image models to, and it takes normalized
        // resolution/aspect_ratio/quality instead of the OpenAI-shaped size/quality the retired
        // /images/generations shim sent (the option-fidelity gap this comment recorded on
        // 2026-07-30; the once-open chat-completions migration is resolved by this instead).
        // Historical shim reachability, verified live 2026-07-30: openai/gpt-image-2 (the
        // default), google/gemini-3-pro-image (owner UAT incl. the reference route — which
        // already used the unified API), black-forest-labs/flux.2-pro (exact-shape probe ->
        // HTTP 200 + image). The unified NO-REFERENCE body is the reference body minus
        // input_references. LNC-7 no longer owns verifying it — that card CLOSED 2026-07-31 by
        // owner decision without its per-provider matrix running; the owner reports images
        // generating successfully through this route since PR #322 (owner-reported, not a
        // recorded probe, and not specific to the no-reference shape). The durable watch is the
        // weekly /vw-model-review.
        new OpenAIStyleDescriptor(AIProvider.OpenRouter, supportsImageGeneration: true),
        new OpenAIStyleDescriptor(AIProvider.Cerebras, supportsImageGeneration: false),
    });
}
