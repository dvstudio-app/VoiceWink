namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// Curates Cerebras's model list for the text-enhancement dropdown (ENH-25).
///
/// Cerebras keeps a model in its <c>/models</c> catalog after withdrawing it from the public
/// endpoints. Its deprecation notice reads "Starting September 3, 2026, gemma-4-31b is no longer
/// available on Cerebras public endpoints" (the id stays on Dedicated Endpoints only), yet the
/// catalog the app fetches still lists it — so the shared baseline (<c>IsChatModel</c>, which knows
/// nothing about withdrawals) went on offering a model that answers HTTP 404 "Model does not exist
/// or you do not have access to it" to every request: owner UAT 198.2 on v1.84.378, 2026-09-13.
/// ENH-24 had already moved the DEFAULT off it; this hides it from the dropdown, the same reading of
/// "a model that cannot work is not offered" that PR #908 gave OpenAI's <c>gpt-live-1</c>.
///
/// DISPLAY-only, and deliberately NOT a baseline rule (the choice PR #908 made the other way):
/// ShowAllModels bypasses curation and still reveals the id, which a Dedicated Endpoints customer
/// can genuinely use; and a persisted <c>gemma-4-31b</c> selection is NOT refused by
/// <c>EnhanceAsync</c>'s ENH-12 guard — it keeps failing loudly with Cerebras's own message and
/// survives a relaunch (owner UAT 198.2 / 198.3), the no-migration decision ENH-24 recorded. (The
/// redo picker is the one surface that changes: its membership-gated pre-select — UX-1's
/// <c>ProviderModelMemoryPolicy.NextDialogModel</c> — falls to the default for a hidden id, as it
/// does for every curated-away id on every provider; nothing is persisted by that pre-fill.) The
/// withdrawn set is a hardcoded table because a catalog-membership check cannot see an endpoint
/// withdrawal. Rows are added and retired by hand from the weekly <c>/vw-model-review</c> report:
/// its deprecation-page read (ENH-24's skill change) is SCOPED to the default set, so a withdrawn
/// non-default id reaches the report only because the run reports each provider's page in full —
/// the skill records that as a known gap, and nothing maps the page onto this table automatically.
///
/// Pure: no HTTP, no filesystem. Pinned by <c>CerebrasModelCatalogTests</c> against the catalog of
/// 2026-09-12 (<c>gemma-4-31b</c>, <c>gpt-oss-120b</c>, <c>qwen-3.8-27b</c>).
/// </summary>
internal static class CerebrasModelCatalog
{
    /// <summary>
    /// Ids Cerebras has withdrawn from its public endpoints while its <c>/models</c> catalog still
    /// lists them — each row names the withdrawal it rests on. EXACT ids, compared ordinal
    /// case-insensitively: a family or substring rule would also hide a successor Cerebras has not
    /// withdrawn.
    /// </summary>
    internal static readonly IReadOnlyList<string> WithdrawnFromPublicEndpoints = new[]
    {
        // Cerebras deprecation notice: not available on public endpoints from 2026-09-03 (Dedicated
        // Endpoints only); the live 404 on the public endpoint was confirmed 2026-09-13 (owner UAT 198.2).
        "gemma-4-31b",
    };

    /// <summary>True when the id is on <see cref="WithdrawnFromPublicEndpoints"/>.</summary>
    internal static bool IsWithdrawn(string modelId) =>
        WithdrawnFromPublicEndpoints.Contains(modelId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies, in order: the withdrawn-id exclusion, then the shared id baseline. The baseline is
    /// injected (<c>ModelDisplayPolicy.IsChatModel</c>) so this stays pure; it handles the
    /// transcription-only ids (<c>whisper-large-v3</c> and the like) and dated snapshots for
    /// Cerebras exactly as it does on the uncurated path.
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

            if (IsWithdrawn(id))
                continue;

            if (!isChatModel(id))
                continue;

            kept.Add(id);
        }

        kept.Sort(StringComparer.OrdinalIgnoreCase);
        return kept;
    }
}
