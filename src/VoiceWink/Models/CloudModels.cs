using VoiceWink.Models.Enums;

namespace VoiceWink.Models;

/// <summary>
/// Cloud transcription model definitions.
///
/// <para><b>ORDER IS LOAD-BEARING, AND THE RULE IS "BEST FIRST" (owner, 2026-08-02).</b>
/// <c>OnboardingPage.PopulateModels</c> selects index 0 of whatever <see cref="GetModelsForProvider"/>
/// returns, so the FIRST row for a provider IS that provider's onboarding default. The standing rule:
/// each provider's best model leads its block. "Best" is the model that dominates on the Models page's
/// own star ratings — accuracy first, speed as the tiebreak — so the catalog and the ratings table can
/// be checked against each other rather than drifting on someone's memory.</para>
///
/// <para>Pinned by <c>CloudModelOrderTests</c>: a new model added in the wrong position fails the
/// build rather than silently changing what every new user gets.</para>
/// </summary>
public static class CloudModels
{
    public static readonly TranscriptionModelInfo[] Models =
    [
        // Groq — turbo leads: same accuracy as Large V3 (★4 since TRN-3). It no longer leads on SPEED:
// both rows carry ★5 since the 2026-08-31 cloud levelling, so ordering here rests on accuracy
// and on turbo's lower cost, not on a speed gap.
        // Stars live in Helpers/ModelRatings.cs; this comment states the ORDERING reason, and a
        // reader checking it should read them there rather than trust the numbers repeated here.
        new() { Name = "whisper-large-v3-turbo", DisplayName = "Groq Whisper Large V3 Turbo", Provider = ModelProvider.Groq },
        new() { Name = "whisper-large-v3", DisplayName = "Groq Whisper Large V3", Provider = ModelProvider.Groq },

        // Deepgram — Nova 3 leads (★4 accuracy vs Nova 2's ★3, same speed).
        new() { Name = "nova-3", DisplayName = "Deepgram Nova 3", Provider = ModelProvider.Deepgram },
        new() { Name = "nova-2", DisplayName = "Deepgram Nova 2", Provider = ModelProvider.Deepgram },

        // ElevenLabs (scribe_v1 retired 2026-07-17; its migration machinery was removed
        // 2026-07-23 once no user selection pointed at it anymore)
        new() { Name = "scribe_v2", DisplayName = "ElevenLabs Scribe V2", Provider = ModelProvider.ElevenLabs },

        // OpenAI — GPT Transcribe, the provider's only row since TRN-84 retired the GPT-4o pair.
        new() { Name = "gpt-transcribe", DisplayName = "OpenAI GPT Transcribe", Provider = ModelProvider.OpenAI },
    ];

    /// <summary>A cloud model the provider shut down: a saved selection naming it runs on its successor (TRN-84).</summary>
    public sealed record RetiredModel(string Name, string DisplayName, ModelProvider Provider, string Successor);

    /// <summary>Retired cloud ids; OpenAI shuts both down on 2027-02-26 and names gpt-transcribe as the replacement (TRN-84).</summary>
    public static readonly RetiredModel[] Retired =
    [
        new("gpt-4o-transcribe", "OpenAI GPT-4o Transcribe", ModelProvider.OpenAI, "gpt-transcribe"),
        new("gpt-4o-mini-transcribe", "OpenAI GPT-4o Mini Transcribe", ModelProvider.OpenAI, "gpt-transcribe"),
    ];

    private static readonly Dictionary<string, RetiredModel> _retired =
        Retired.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical-name lookup for live models (case-insensitive → exact casing).</summary>
    private static readonly Dictionary<string, string> _canonicalNames =
        Models.ToDictionary(m => m.Name, m => m.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The retired row a name denotes (any casing), or null for a live or unknown name.</summary>
    public static RetiredModel? FindRetired(string? modelName)
        => !string.IsNullOrEmpty(modelName) && _retired.TryGetValue(modelName, out var r) ? r : null;

    /// <summary>O(1) lookup for cloud model names — avoids repeated .Any() linear scans.</summary>
    private static readonly HashSet<string> _cloudModelNames =
        new(Models.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a model name to its canonical form: live names normalize to catalog
    /// casing (so the registry's lookups can stay exact), a retired name resolves to its
    /// successor (TRN-84), unknown names pass through unchanged.
    /// </summary>
    public static string Canonicalize(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return modelName ?? "";
        if (FindRetired(modelName) is { } retired)
            return retired.Successor;
        return _canonicalNames.TryGetValue(modelName, out var canonical) ? canonical : modelName;
    }

    /// <summary>
    /// Returns true if the model name is a known cloud model. Canonicalizes first, so
    /// any casing classifies as cloud — callers that branch local-vs-cloud can never
    /// mis-route an oddly-cased selection to the local path.
    /// </summary>
    public static bool IsCloudModel(string? modelName)
        => !string.IsNullOrEmpty(modelName) && _cloudModelNames.Contains(Canonicalize(modelName));

    public static TranscriptionModelInfo[] GetModelsForProvider(ModelProvider provider)
    {
        return Models.Where(m => m.Provider == provider).ToArray();
    }
}
