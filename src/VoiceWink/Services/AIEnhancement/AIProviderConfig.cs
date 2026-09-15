namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// AI provider configuration. The <see cref="AIProvider"/> enum lives in its own file so that code
/// needing only the provider key does not drag in this type's dependencies — see the remarks there.
/// </summary>
public class AIProviderConfig
{
    public AIProvider Provider { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public double Temperature { get; init; } = 0.3;
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// ENH-8: the resolved per-request reasoning directive (never null —
    /// <see cref="Helpers.ReasoningDirective.None"/> means "omit every reasoning field",
    /// which must leave the request byte-identical to a pre-ENH-8 build).
    /// </summary>
    public Helpers.ReasoningDirective Reasoning { get; init; } = Helpers.ReasoningDirective.None;

    /// <summary>
    /// IMG-4: the parallel batch's shared call state (response-materialization gate),
    /// set by <c>AIEnhancementService</c> ONLY on the internal batch path — null on every
    /// other call, which the image clients treat as "no gate, no buffer cap" so
    /// single-image behavior stays byte-identical. Internal carrier, deliberately not
    /// part of the public config surface.
    /// </summary>
    internal ImageBatchCallContext? BatchContext { get; set; }

    public string GetBaseUrl()
    {
        // User-configured base URL takes priority (supports Azure OpenAI, proxies, self-hosted)
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
                throw new InvalidOperationException(
                    $"Custom base URL is not a valid absolute URL: {BaseUrl}");
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new InvalidOperationException(
                    $"Custom base URL must use HTTPS (got: {BaseUrl}). Use http:// only for localhost.");
            if (uri.Scheme != "http" && uri.Scheme != "https")
                throw new InvalidOperationException(
                    $"Custom base URL must use HTTP or HTTPS (got: {uri.Scheme}://).");
            return BaseUrl;
        }

        return Provider switch
        {
            AIProvider.Anthropic => "https://api.anthropic.com/v1",
            AIProvider.OpenAI => "https://api.openai.com/v1",
            AIProvider.Groq => "https://api.groq.com/openai/v1",
            AIProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta",
            AIProvider.Mistral => "https://api.mistral.ai/v1",
            AIProvider.OpenRouter => "https://openrouter.ai/api/v1",
            AIProvider.Cerebras => "https://api.cerebras.ai/v1",
            _ => ""
        };
    }
}
