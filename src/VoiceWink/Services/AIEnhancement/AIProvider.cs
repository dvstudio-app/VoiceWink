namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// The AI providers the app can talk to — the key every per-provider table, setting and
/// descriptor is keyed on.
///
/// <para><b>Kept in its own file, dependency-free, on purpose.</b> It used to sit at the top of
/// <c>AIProviderConfig.cs</c> beside <see cref="AIProviderConfig"/>, which carries
/// <c>Helpers.ReasoningDirective</c> and <c>ImageBatchCallContext</c> — so anything wanting just
/// this enum had to drag those in too. That blocked the <c>tools/model-filter-sim</c> harness from
/// linking the shipped display filters (measured: the spike failed to compile on exactly those two
/// types), and a harness that cannot link the shipped path ends up re-implementing it, which is the
/// failure mode <c>/vw-model-review</c> has hit repeatedly. Adding a dependency here re-arms that,
/// so keep this file free of them.</para>
/// </summary>
public enum AIProvider
{
    Anthropic,
    OpenAI,
    Gemini,
    Groq,
    Mistral,
    OpenRouter,
    Cerebras,

    /// <summary>
    /// LAI-1: an AI server the user runs themselves — Ollama, LM Studio, or any
    /// OpenAI-compatible endpoint. No API key required (<c>RequiresApiKey</c> is false), no
    /// image generation, and its model list is the server's own. Shown to the user as
    /// "Local server" (<c>AIProviderDisplay</c>); persisted by this member NAME like the rest.
    /// </summary>
    LocalServer
}
