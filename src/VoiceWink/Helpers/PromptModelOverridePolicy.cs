using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for the prompt Configure dialog's model-override dropdown
/// (field bug 2026-07-08): the dialog restored the model selection from the
/// PERSISTED prompt on every provider/type change, clobbering the user's
/// in-dialog revert to "(Default)" — and carried a model from provider X into
/// provider Y as free text, producing configs that 404 at runtime
/// (claude-fable-5 sent to OpenAI). The dialog now tracks its live pending
/// choice and routes every refresh through <see cref="NextSelection"/>.
/// Complementary to <see cref="ProviderModelMemoryPolicy"/> (UX-1): this policy decides
/// whether a still-valid explicit OVERRIDE is kept; that one decides the concrete DEFAULT
/// model the options dialog pre-selects. The two act on disjoint state (per-prompt
/// override field vs per-provider settings keys) and must not be merged.
/// </summary>
internal static class PromptModelOverridePolicy
{
    /// <summary>
    /// The context a model override is meaningful in: the effective provider
    /// (null = the "(Default)" selection — resolves to the main provider at
    /// runtime) and the text-vs-image model domain. A model override from a
    /// different context is stale: it belongs to another provider's catalog
    /// or the other model domain.
    /// </summary>
    internal readonly record struct OverrideContext(AIProvider? ProviderOverride, bool IsImage);

    /// <summary>
    /// The model-override value the dialog should show after a refresh.
    /// <list type="bullet">
    /// <item>Unchanged context keeps the pending value — even when it is not in
    /// the fetched list (users may type unlisted models deliberately). The
    /// dialog's initial populate is this case by construction: lastContext is
    /// initialized from the same combos the first refresh reads.</item>
    /// <item>A context CHANGE (provider or text/image domain) keeps the pending
    /// value only when the new context's model list contains it; otherwise the
    /// override is stale and resets to null = "(Default)". There is deliberately
    /// NO initial-populate exemption: if the user switches context while the
    /// initial async model fetch is still in flight, the superseding refresh
    /// must not preserve a stale saved model into the new context (Codex diff
    /// round 1 — that re-created the cross-provider 404).</item>
    /// </list>
    /// </summary>
    internal static string? NextSelection(
        OverrideContext previous,
        OverrideContext next,
        string? pending,
        IReadOnlyCollection<string> nextModels)
    {
        if (string.IsNullOrWhiteSpace(pending))
            return null;

        if (previous == next)
            return pending;

        return nextModels.Contains(pending) ? pending : null;
    }
}
