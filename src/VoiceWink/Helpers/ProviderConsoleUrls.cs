using VoiceWink.Models.Enums;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Where a user manages the API key for each BYOK transcription provider (owner request
/// 2026-07-31 — the Models page asked for a key without saying where to get one).
///
/// <para>These are the providers' own console/dashboard pages, NOT deep links into a specific
/// key: vendors restructure their dashboards, and a landing page that redirects is better than a
/// path that 404s. Kept beside <see cref="VoiceWinkUrls"/> rather than inside it — that type is
/// the frozen contract for OUR domains, while these are third-party surfaces that may drift.</para>
///
/// <para>All nine were opened and confirmed correct by the owner on 2026-07-31. Nothing in the
/// build can re-check that — a vendor can retire a path at any time and the app would keep
/// shipping the dead link silently — so treat a URL change here as needing the same manual pass.
/// The Cerebras entry below is what a wrong one looks like in the field.</para>
/// </summary>
internal static class ProviderConsoleUrls
{
    /// <summary>Transcription providers (Models page). Null for a provider with no known console
    /// (never render a dead link).</summary>
    internal static string? ForProvider(ModelProvider provider) => provider switch
    {
        ModelProvider.Groq => GroqKeys,
        ModelProvider.Deepgram => "https://console.deepgram.com/",
        ModelProvider.ElevenLabs => "https://elevenlabs.io/app/settings/api-keys",
        ModelProvider.OpenAI => OpenAIKeys,
        _ => null,
    };

    /// <summary>AI-enhancement + image providers (AI Enhancement page). Shares the Groq/OpenAI
    /// constants with the transcription map above — the same account issues both keys, so a
    /// future URL change must not be fixable in one place and missed in the other.</summary>
    internal static string? ForProvider(AIProvider provider) => provider switch
    {
        AIProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        AIProvider.OpenAI => OpenAIKeys,
        AIProvider.Gemini => "https://aistudio.google.com/apikey",
        AIProvider.Groq => GroqKeys,
        AIProvider.Mistral => "https://console.mistral.ai/api-keys",
        AIProvider.OpenRouter => "https://openrouter.ai/keys",
        // Console ROOT, not /platform/apikeys: that path is ORG-SCOPED (the real address carries
        // an org id) and a bare visit answers "organization does not exist" (owner report
        // 2026-07-31). The root redirects each user to their own org's dashboard.
        AIProvider.Cerebras => "https://cloud.cerebras.ai/",
        _ => null,
    };

    private const string GroqKeys = "https://console.groq.com/keys";
    private const string OpenAIKeys = "https://platform.openai.com/api-keys";
}
