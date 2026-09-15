using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// What the image options dialog should OFFER for one (provider, model) — the pure decision behind
/// both dialog builders (the App-hosted picker and the EnhancementPage editor).
///
/// <para>Extracted rather than written inline (IMG-5, Codex final check): the two builders had
/// drifting copies of this logic, neither was unit-testable, and <c>App.xaml.cs</c> is under the
/// launch-freeze hub rule that forbids growing its responsibilities. Pure — no DI, no settings, no
/// UI types; the caller resolves the model and looks up capabilities, then renders what this
/// returns.</para>
/// </summary>
/// <param name="Aspects">Aspect tags to offer. Empty ⇒ hide the aspect row entirely.</param>
/// <param name="Tiers">Size tiers to offer. Empty ⇒ hide the size row entirely.</param>
/// <param name="Qualities">
/// Quality tags to offer. Empty ⇒ hide the quality row entirely. A LIST since IMG-12 (2026-08-13) —
/// it was a bool, and a model publishing <c>quality</c> at all was offered all three tiers, so
/// <c>x-ai/grok-imagine-image-2.0</c> (which publishes <c>{low, medium}</c>) answered "Maximum" with
/// an HTTP 400.
/// </param>
public readonly record struct ImageOptionGating(
    IReadOnlyList<string> Aspects,
    IReadOnlyList<string> Tiers,
    IReadOnlyList<string> Qualities)
{
    /// <summary>
    /// Hide the aspect row when the model publishes no <c>aspect_ratio</c> — today the
    /// <c>gpt-5*-image*</c> family, and any OpenRouter model not yet in the capability snapshot.
    ///
    /// <para><b>Hiding is not enough on its own</b>: <c>AppTheme.PopulateIndicatorCombo</c>
    /// atomically replaces the rows and can only preserve a tag the new rows still contain — a
    /// hidden row's tag never survives, so it falls back to Auto. A builder that
    /// populates-then-hides therefore silently erases the user's saved aspect the next time the
    /// dialog Saves (both builders read the combo's tag on save). Callers must SKIP repopulation
    /// when this is false, exactly as the size row already does — the tier path is the precedent to
    /// mirror.</para>
    /// </summary>
    public bool ShowAspect => Aspects.Count > 0;

    /// <summary>Same rule for the size row; the existing builders already gate on tier count.</summary>
    public bool ShowSize => Tiers.Count > 0;

    /// <summary>
    /// Same rule again for the quality row. Derived rather than carried, so a row can never be
    /// shown with nothing in it — or hidden while it still holds a value the model accepts.
    /// </summary>
    public bool ShowQuality => Qualities.Count > 0;

    /// <summary>
    /// Decide from the model the run will ACTUALLY use and what that model publishes.
    ///
    /// <para><paramref name="capabilities"/> null ⇒ <see cref="ImageOptions"/>'s static rules
    /// (OpenAI-direct, Gemini-direct — never in OpenRouter's catalog, tables docs-verified by
    /// IMG-2). Non-null ⇒ the catalog answers, including
    /// <see cref="ImageModelCapabilities.AllAuto"/> for an OpenRouter model the snapshot does not
    /// classify.</para>
    /// </summary>
    public static ImageOptionGating Decide(
        AIProvider provider, string? model, ImageModelCapabilities? capabilities)
        => new(
            ImageOptions.SupportedAspectsFor(provider, model, capabilities),
            ImageOptions.SupportedTiersFor(provider, model, capabilities),
            ImageOptions.SupportedQualitiesFor(provider, model, capabilities));

    /// <summary>
    /// The model the dialog must gate against: the user's PENDING pick if there is one, else the
    /// context's previous model, else the model the runtime would resolve.
    ///
    /// <para><b>Why the runtime fallback matters</b> (IMG-5, Codex plan review): the dialogs used to
    /// pass null for "(Default)", and <see cref="ImageOptions"/> substitutes the provider's
    /// HARDCODED default for a null model — while the runtime resolves the provider's
    /// PERSISTED model via <c>ResolveEffectiveImageModel</c>. On OpenRouter with a default prompt
    /// that meant the dialog could show <c>gpt-image-2</c>'s controls while the generation ran
    /// krea, which defeats the entire point of per-model gating.</para>
    ///
    /// <para><b>Why the pending pick wins</b> (Kimi final check): resolving purely through the
    /// runtime seam would honour <c>prompt.ModelOverride</c>, which is STALE mid-edit — the user's
    /// in-progress choice lives in the combo and has not been saved yet. Combo first, then the
    /// context, then runtime.</para>
    /// </summary>
    public static string? ResolveGatingModel(
        string? pendingComboModel, string? previousModel, string? runtimeResolvedModel)
    {
        if (!string.IsNullOrWhiteSpace(pendingComboModel)) return pendingComboModel;
        if (!string.IsNullOrWhiteSpace(previousModel)) return previousModel;
        return string.IsNullOrWhiteSpace(runtimeResolvedModel) ? null : runtimeResolvedModel;
    }

    // IMG-12: a `QualitySelection` record plus `NextQualitySelection` / `ConfirmedQuality` lived here
    // between review rounds 1 and 2, inferring the user's edits by comparing the combo's tag against
    // what the dialog last displayed. They are GONE because that inference is unsound, not because
    // the split was: an edit that returns to the displayed value (pick Standard, change your mind,
    // pick Enhanced again) is indistinguishable from no edit at all, and the comparison then
    // persisted the hidden ask over the user's actual final choice (Codex diff r2). The ask now
    // lives in `Helpers/QualitySelectionTracker`, fed by the combo's SelectionChanged — the only
    // signal that can see an intermediate pick — and the display side is a plain
    // `ImageOptions.ClampQuality` call at each populate.

}
