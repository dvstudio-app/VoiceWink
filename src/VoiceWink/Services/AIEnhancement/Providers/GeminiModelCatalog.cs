namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Curates Gemini's model list for the text-enhancement dropdown (ENH-9).
///
/// Google ships Live-API models in the same catalog as chat models, and the OpenAI-compatible
/// endpoint the app fetches from STRIPS the native capability metadata
/// (<c>supportedGenerationMethods</c>) that would tell them apart. Verified against the native
/// <c>/v1beta/models</c> endpoint on 2026-07-30: <c>gemini-3.1-flash-live-preview</c> and
/// <c>gemini-3.5-live-translate-preview</c> support ONLY <c>bidiGenerateContent</c> — no
/// <c>generateContent</c>, which is what the app's <c>chat/completions</c> call requires — so
/// selecting either yields a guaranteed provider error. Every other id on the current text list
/// supports <c>generateContent</c>. Both were exposed by the 2026-07-30 previews-shown decision;
/// <c>IsPreviewModel</c> had been hiding them by accident.
///
/// The rule is id-shaped (Google's Live line carries a delimiter-bounded <c>live</c> segment)
/// because a metadata gate would require switching the fetch to the native endpoint — a different
/// response shape plus pagination, out of proportion for two ids. It was Gemini-SCOPED from ENH-9
/// until OpenAI shipped the same id shape (<c>gpt-live-1</c>, a <c>v1/live/sessions</c>-only
/// model, weekly model review 2026-09-12); the segment rule now lives in the shared baseline as
/// <c>ModelDisplayPolicy.HasLiveSegment</c>, which <c>IsChatModel</c> applies for every provider,
/// so this catalog no longer carries the Live rule. Its one rule is <see cref="IsNonTextModel"/>
/// (ENH-40 Decision line, ENH-44 Genie). The accepted mirror risk — a future genuinely-chat
/// <c>…-live-…</c> id would be wrongly hidden — is watched by the weekly <c>/vw-model-review</c>
/// (which verifies against the native metadata). Recovery is no longer a user-side matter: while
/// the rule was display-only here, ShowAllModels and the AI Enhancement page's editable combo were
/// a real escape; in the baseline the same id is also refused by <c>EnhanceAsync</c>'s ENH-12 guard
/// on the next dictation, whatever the toggle — the same footing as every other non-chat token, and
/// the reason a false exclusion is a code change, not a setting.
///
/// Pure: no HTTP, no filesystem. Pinned by <c>GeminiModelCatalogTests</c> against the live
/// catalog of 2026-07-30.
/// </summary>
internal static class GeminiModelCatalog
{
    /// <summary>
    /// Applies the shared id baseline, then <see cref="IsNonTextModel"/>. The baseline is
    /// injected (<c>ModelDisplayPolicy.IsChatModel</c>) so this stays pure; it handles the Live line,
    /// embeddings/TTS/Veo and dated snapshots for Gemini, exactly as it does on the uncurated path.
    /// </summary>
    internal static List<string> Curate(
        IReadOnlyList<string> modelIds,
        Func<string, bool> isChatModel)
    {
        var kept = new List<string>();
        foreach (var id in modelIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!isChatModel(id) || IsNonTextModel(id))
                continue;

            kept.Add(id);
        }

        kept.Sort(StringComparer.OrdinalIgnoreCase);
        return kept;
    }

    /// <summary>A model family Google serves outside text generation; display-only, so a saved selection is not cleared.</summary>
    internal static bool IsNonTextModel(string modelId)
    {
        foreach (var family in NonTextFamilies)
        {
            if (modelId.Equals(family, StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] NonTextFamilies =
    {
        "gemini-decision", // ENH-40: Decisions API only.
        "genie",           // ENH-44: Interactions API world generation only.
    };
}
