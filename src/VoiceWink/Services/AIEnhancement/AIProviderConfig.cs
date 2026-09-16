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
    /// <summary>
    /// The value every request carries unless a published per-model ceiling clamps it down.
    ///
    /// <para><b>16000 since 2026-09-15; it was <see cref="LegacyMaxTokens"/> from the repository's
    /// first commit (<c>8b8d16f</c>, 2026-03-03)</b> — a round number with no comment, no recorded
    /// basis and no override anywhere in the app. The value matters because on a reasoning model the
    /// budget counts THINKING tokens as well as the answer, so a cap sized for the answer alone lets
    /// a model spend the whole allowance thinking and return nothing. That starvation is what
    /// <see cref="Helpers.ReasoningEffortPolicy"/> cites for sending <c>reasoning_effort=none</c> on
    /// Cerebras <c>qwen-3.8-27b</c>.</para>
    ///
    /// <para><b>Measured, 2026-09-15:</b> every provider the app ships accepted 16000 at a probed
    /// chat model except Mistral, which answered HTTP 429 at every value tried INCLUDING the
    /// then-current 4096 — rate-limited rather than cap-limited, and therefore the one provider left
    /// unmeasured. Above 16000 the providers diverge: Groq refused 32768, OpenAI and Anthropic
    /// refused 131072. The probed ids were chosen for coverage and are NOT all
    /// <see cref="Helpers.TextModelDefaults"/> entries.</para>
    ///
    /// <para><b>Why a cap exists at all.</b> Anthropic's Messages API returns HTTP 400 without
    /// <c>max_tokens</c> (probed), so removing it is a per-provider branch rather than a deletion;
    /// and on the providers that do allow omission the only remaining bound on a looping model is
    /// <c>AIEnhancementService.DefaultEnhancementTimeout</c>. Upstream VoiceInk sends no cap on its
    /// cloud chat calls at all — a deliberate difference, not an oversight.</para>
    /// </summary>
    internal const int DefaultMaxTokens = 16000;

    /// <summary>
    /// What every build before 2026-09-15 sent, kept as the conservative fallback for a provider
    /// that PUBLISHES per-model ceilings whose catalog has not been read yet. Not a floor under
    /// every published ceiling — OpenRouter's <c>google/gemma-2-27b-it</c> publishes 2048 — just the
    /// value already proven in the field, so a cache miss can never be worse than the previous
    /// release.
    /// </summary>
    internal const int LegacyMaxTokens = 4096;

    /// <summary>
    /// Output-token ceiling for THIS request, under three wire names — <c>max_completion_tokens</c>
    /// (OpenAI chat), <c>max_tokens</c> (Anthropic and the OpenAI-compatible providers) and
    /// <c>max_output_tokens</c> (the OpenAI Responses-API fallback).
    ///
    /// <para>Defaults to <see cref="DefaultMaxTokens"/>; <c>AIEnhancementService.BuildConfig</c>
    /// clamps it DOWN per model where the provider publishes a ceiling. A flat global value is not
    /// safe: Groq <c>allam-2-7b</c> publishes 4096 and returns HTTP 400 at 16000, and 44 of
    /// OpenRouter's 445 models publish a ceiling below it.</para>
    ///
    /// <para><b>Two recorded residuals on the providers that publish nothing</b> (Kimi diff round 1,
    /// A1/A2 — accepted as residuals rather than coded around, because both fail LOUDLY with a red
    /// pill and the raw transcript, and the obvious mitigation costs more than it buys). First: the
    /// probe covered one CURRENT model per provider, and the long tail is not covered — a legacy id
    /// reachable only through <c>ShowAllModels</c> or the editable model box (the <c>claude-3</c>
    /// generation at 4096, <c>claude-3-5-sonnet</c> and the Gemini 1.5 era at 8192) gets this value
    /// and an HTTP 400 where 4096 worked. Clamping every dated snapshot to
    /// <see cref="LegacyMaxTokens"/> was proposed and declined: dated ids are also how a user PINS a
    /// current model, so it would deny the raise to exactly the deliberate production choice this
    /// change is meant to serve. Second: a custom <c>aiBaseUrl_*</c> can point a non-publisher
    /// provider enum at an endpoint whose models DO have low ceilings — no hydration runs for that
    /// enum, so nothing clamps. Endpoint-keyed ceilings are out of scope; this is the same
    /// custom-base-URL residual <see cref="Helpers.ReasoningEffortPolicy"/> already records for its
    /// own rows.</para>
    ///
    /// <para><b>What this does NOT fix</b> — the second half of audit finding F12
    /// (<c>docs/investigations/prompt-stack-audit-2026-07-17.md</c> named the 4096 value AND
    /// undetected truncation as one finding; PRM-2 shipped the logging half only). <b>ENH-27 closed
    /// it the same week, 2026-09-15:</b> a cleanup truncated with NON-EMPTY content is now REFUSED
    /// rather than pasted — see <see cref="Clients.OpenAICompatibleClient.TruncatedReason"/>, which
    /// all three wire spellings throw — so the complete raw transcript pastes and the pill says
    /// why. What remains silent is narrower and unrelated to this constant: an empty-but-present
    /// content string is still returned as success and substituted in
    /// <c>AIEnhancementService</c>. Raising the cap moves the threshold at which any of this is
    /// reached; how much dictation it admits is bounded by the 60 s enhancement deadline and
    /// provider throughput, not by this number, so no minutes figure is claimed here.</para>
    /// </summary>
    public int MaxTokens { get; init; } = DefaultMaxTokens;

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
