using System.Text.RegularExpressions;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// One entry of Mistral's <c>GET /v1/models</c> response, reduced to the fields that decide
/// whether it belongs in the text-enhancement dropdown. The capability flags are
/// <see langword="bool"/>? on purpose: <c>null</c> means "Mistral did not tell us" (field absent,
/// JSON null, or not a boolean) and must never be read as a "no" — see
/// <see cref="MistralModelCatalog.Curate"/>'s tri-state contract.
/// </summary>
internal readonly record struct MistralModelEntry(
    string Id,
    bool? CompletionChat,
    bool? CompletionFim,
    string? Deprecation);

/// <summary>
/// Curates Mistral's model list for the text-enhancement dropdown.
///
/// Mistral returns ~60 entries, of which the shared <c>IsChatModel</c> id-substring filter admits
/// 34 — embeddings, OCR, transcription, code-completion and about-to-retire models included,
/// because none of them contain a token that filter knows about (<c>mistral-embed</c> does not
/// contain "embedding"; there is no OCR pattern; <c>voxtral-mini-latest</c> names no audio token).
/// Rather than grow the cross-provider blocklist with Mistral tokens — which is how that list
/// drifts into misfiring on OpenAI/Gemini/Groq ids — this reads the intent Mistral publishes:
/// per-model <c>capabilities</c> plus a <c>deprecation</c> date.
///
/// Pure: no HTTP, no filesystem, no clock. Pinned by <c>MistralModelCatalogTests</c> against a
/// fixture transcribed from a real live catalog (2026-07-29).
/// </summary>
internal static class MistralModelCatalog
{
    /// <summary>
    /// Families excluded wholesale because a capability flag cannot express "wrong tool for
    /// dictation cleanup". <c>voxtral</c> is the audio line — the transcription/TTS members are
    /// already non-chat, but <c>voxtral-small</c> IS chat-capable and is still an audio-input
    /// model, so the family rule is what removes it (owner decision 2026-07-29).
    /// <c>mistral-vibe-cli</c> is a coding-agent CLI line.
    /// </summary>
    private static readonly string[] ExcludedFamilies = { "voxtral", "mistral-vibe-cli" };

    /// <summary>
    /// Mistral ships experimental models under a <c>labs-</c> prefix (e.g. the Lean 4 theorem
    /// prover <c>labs-leanstral-1-5</c>).
    /// </summary>
    private const string ExperimentalPrefix = "labs-";

    /// <summary>
    /// A trailing PRODUCT VERSION: digit groups joined by <c>-</c> or <c>.</c>
    /// (<c>mistral-medium-3</c>, <c>-3-5</c>, <c>-3.5</c>). Deliberately digits-only per segment so
    /// numeric PRODUCT names never match — <c>ministral-3b-latest</c>, <c>ministral-8b-latest</c>
    /// and <c>ministral-14b-latest</c> are distinct models, not versions of "ministral", and
    /// collapsing them would delete three real entries.
    /// </summary>
    private static readonly Regex VersionAliasPattern =
        new(@"^(?<family>.+?)-(?<version>\d+(?:[.-]\d+)*)$",
            RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Applies, in order: family exclusions, deprecation, the capability gate, dated-snapshot
    /// hiding, then sibling-dependent alias collapse.
    /// </summary>
    /// <param name="entries">Parsed catalog, in any order.</param>
    /// <param name="isChatModel">
    /// The shared id heuristic (<c>ModelDisplayPolicy.IsChatModel</c>), injected so this stays
    /// pure. Used for entries whose capability metadata is unknown.
    /// </param>
    internal static List<string> Curate(
        IReadOnlyList<MistralModelEntry> entries,
        Func<string, bool> isChatModel)
    {
        var kept = new List<MistralModelEntry>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            // A scheduled retirement hides the model from DISCOVERY only. It does not migrate,
            // clear or runtime-block an already-persisted selection: a user pinned to a deprecated
            // model keeps working until the provider actually retires it.
            if (!string.IsNullOrWhiteSpace(entry.Deprecation))
                continue;

            if (IsExcludedFamily(entry.Id))
                continue;

            if (!PassesCapabilityGate(entry, isChatModel))
                continue;

            // Version-pinned snapshots (mistral-medium-2604, ministral-14b-2512, …) stay hidden in
            // favour of the stable pointer, exactly as they are for every other provider.
            if (ModelDisplayPolicy.IsDatedSnapshot(entry.Id))
                continue;

            kept.Add(entry);
        }

        var keptIds = new HashSet<string>(kept.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        return kept
            .Select(e => e.Id)
            .Where(id => !IsRedundantVersionAlias(id, keptIds))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // No catalog-wide fallback, deliberately. Two review rounds landed on the same conclusion:
        // metadata drift is already handled PER ENTRY (an unknown capability falls back to the
        // shared id heuristic in PassesCapabilityGate), so a blanket "if the result is empty, run
        // IsChatModel over everything" adds nothing except the power to resurrect entries that a
        // KNOWN FIM / family / deprecation / snapshot rule confidently excluded. An empty curated
        // list is an honest answer; whether an empty list means "bad API key" is a question about
        // the raw catalog, and callers answer it from the raw count rather than from this list.
    }

    /// <summary>
    /// Delimiter-aware family match: the id must equal the family or continue with <c>-</c>, so a
    /// hypothetical <c>voxtralytics</c> is never mistaken for the <c>voxtral</c> line.
    /// </summary>
    private static bool IsExcludedFamily(string modelId)
    {
        if (modelId.StartsWith(ExperimentalPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var family in ExcludedFamilies)
        {
            if (modelId.Equals(family, StringComparison.OrdinalIgnoreCase))
                return true;
            if (modelId.Length > family.Length
                && modelId.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                && modelId[family.Length] == '-')
                return true;
        }

        return false;
    }

    /// <summary>
    /// The tri-state capability gate. A KNOWN <c>completion_chat=false</c> excludes (embeddings,
    /// OCR, moderation, transcription, TTS); a KNOWN <c>completion_fim=true</c> excludes the
    /// code-completion line (<c>codestral</c>, <c>mistral-code-*</c>) — those carry
    /// <c>completion_chat=true</c> too, so FIM is the discriminator. When the metadata is UNKNOWN
    /// the entry falls back to the shared id heuristic, never to a silent "no".
    ///
    /// A capability "yes" NARROWS the shared baseline, it does not override it: the id must still
    /// satisfy <c>IsChatModel</c>, whose established exclusions are product decisions Mistral's
    /// metadata knows nothing about — most visibly the <c>ft:</c> prefix, so fine-tuned model cards
    /// stay out (onboarding consumes this list directly). The residual risk is the mirror image — a
    /// future genuinely-chat Mistral id caught by an id pattern (e.g. one containing "instruct")
    /// would be hidden. That is what <c>/vw-model-review</c>'s "wrongly hidden" bucket is for.
    /// </summary>
    private static bool PassesCapabilityGate(MistralModelEntry entry, Func<string, bool> isChatModel)
    {
        if (entry.CompletionFim == true)
            return false;

        return entry.CompletionChat switch
        {
            true => isChatModel(entry.Id),
            false => false,
            null => isChatModel(entry.Id),
        };
    }

    /// <summary>
    /// True when <paramref name="modelId"/> is a version alias (<c>mistral-medium-3.5</c>) AND a
    /// retained sibling for the same family survives — the bare family name
    /// (<c>mistral-medium</c>) or its <c>-latest</c> pointer. The sibling condition is what makes
    /// this safe: a future family published ONLY as a version alias keeps its entry instead of
    /// disappearing from the dropdown entirely.
    /// </summary>
    private static bool IsRedundantVersionAlias(string modelId, HashSet<string> keptIds)
    {
        var match = VersionAliasPattern.Match(modelId);
        if (!match.Success)
            return false;

        var family = match.Groups["family"].Value;
        return keptIds.Contains(family) || keptIds.Contains($"{family}-latest");
    }
}
