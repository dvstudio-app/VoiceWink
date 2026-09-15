using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for the enhancement/image options dialog's model dropdown (UX-1):
/// which model to pre-select after a provider switch or list refresh, and whether a
/// confirmed choice is written back to the per-provider memory (<c>aiModel_{provider}</c> /
/// <c>aiImageModel_{provider}</c>). Complementary to <see cref="PromptModelOverridePolicy"/> —
/// that policy decides whether the prompt Configure dialog KEEPS a still-valid explicit
/// override on a context change; this one decides the concrete DEFAULT model the options
/// dialog shows. The Configure dialog's "(Default)" sentinel needs no equivalent seeding:
/// it already resolves to this same per-provider memory at runtime via
/// <c>AIEnhancementService.ResolveModelForPrompt</c>.
/// </summary>
internal static class ProviderModelMemoryPolicy
{
    /// <summary>
    /// Pre-selection precedence, every step membership-gated against the live filtered
    /// list the combo actually shows:
    /// <list type="number">
    /// <item>the redo-chain's previous model when the selected provider IS the previous
    /// provider (in-session recency beats persisted memory);</item>
    /// <item>the RAW persisted per-provider model (null when never chosen — callers pass
    /// <c>AIEnhancementService.PersistedModelFor</c>, which does NO implicit-default
    /// substitution);</item>
    /// <item>the provider default (<see cref="ImageOptions.DefaultModelFor"/> /
    /// <see cref="TextModelDefaults.DefaultModelFor"/>);</item>
    /// <item>null — the caller leaves the combo UNSELECTED with the confirm button gated,
    /// never an arbitrary alphabetical <c>list[0]</c>.</item>
    /// </list>
    /// </summary>
    internal static string? NextDialogModel(
        AIProvider selectedProvider,
        AIProvider? previousProvider,
        string? previousModel,
        string? persistedModel,
        string? defaultModel,
        IReadOnlyCollection<string> models)
    {
        if (selectedProvider == previousProvider
            && !string.IsNullOrWhiteSpace(previousModel) && models.Contains(previousModel))
            return previousModel;

        if (!string.IsNullOrWhiteSpace(persistedModel) && models.Contains(persistedModel))
            return persistedModel;

        if (!string.IsNullOrWhiteSpace(defaultModel) && models.Contains(defaultModel))
            return defaultModel;

        return null;
    }

    /// <summary>
    /// A confirmed dialog choice persists iff the user actually adjusted the model in the
    /// dialog AND the confirmed value differs from the RAW persisted value. Null persisted
    /// means "never chosen", so a user-adjusted pick of the current implicit default
    /// persists (pinning it against future default changes), while an UNTOUCHED pre-fill
    /// never writes — users who never picked a model keep following default upgrades.
    /// The intent flag is conservative by construction (reset on every list refresh,
    /// programmatic mutations suppressed, combo disabled while a fetch is unsettled), so
    /// this can only under-persist, never persist an automatic fallback. Ordinal: model
    /// ids are case-sensitive slugs.
    /// </summary>
    internal static bool ShouldPersistOnConfirm(
        bool userAdjustedModel, string? confirmedModel, string? persistedModel)
        => userAdjustedModel
           && !string.IsNullOrWhiteSpace(confirmedModel)
           && !string.Equals(confirmedModel, persistedModel, StringComparison.Ordinal);
}
