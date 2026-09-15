namespace VoiceWink.Helpers;

/// <summary>Where the image model a dispatch is about to use actually came from.</summary>
/// <remarks>
/// Provenance decides WHO MAY BE HEALED, never whether the model is valid. It is established in
/// <c>AIEnhancementService.ResolveEffectiveImageModelWithSource</c> — the one place that already
/// distinguishes these — because by the time the background job runs, the model is just a string and
/// the distinction is gone.
/// </remarks>
internal enum ImageModelSource
{
    /// <summary>A prompt's explicit <c>ModelOverride</c>. Never mutated (the F6/F17 wrong-owner rule).</summary>
    PromptOverride,

    /// <summary>
    /// The per-provider persisted setting (<c>AppDefaults.AiImageModelKey</c>). The ONLY healable
    /// source — but note it is NOT the same as "not chosen by the user": the editable
    /// "Select or type an image model" field and the options dialog's confirm path both write here.
    /// </summary>
    PersistedProviderSetting,

    /// <summary>A value the user picked in the redo / new-image dialog. Explicit; never mutated.</summary>
    PickerSelection,

    /// <summary>Nothing persisted — <c>ImageOptions.DefaultModelFor</c> supplied it. Our code, not their setting.</summary>
    ProviderDefault,
}

/// <summary>What a dispatch should do with the image model it resolved.</summary>
internal enum ImageModelGateAction
{
    /// <summary>Dispatch normally.</summary>
    Proceed,

    /// <summary>Refuse the generation, and clear the offending per-provider persisted setting.</summary>
    RefuseAndHeal,

    /// <summary>Refuse the generation, mutating nothing.</summary>
    Refuse,
}

/// <summary>An image model plus where it came from.</summary>
internal readonly record struct ImageModelResolution(string Model, ImageModelSource Source);

/// <summary>
/// Decides whether a resolved image model may be dispatched.
///
/// Born from a live finding (2026-07-30): <c>aiImageModel_openrouter</c> held
/// <c>aion-labs/aion-2.0</c>, a TEXT-ONLY model (`output_modalities: ["text"]`, verified against
/// OpenRouter's per-model endpoint). UX-1 gates the DIALOG pre-select on membership in the live
/// filtered list, but the resolve path reads the RAW setting — so a stale value from the pre-UX-1
/// alphabetical-<c>list[0]</c> era could be sent as the image model, spending a call on a model that
/// cannot return an image.
///
/// <para><b>The load-bearing subtlety: a heuristic miss is NOT evidence of a text model.</b>
/// <c>!IsImageModel</c> alone must never refuse, because the model combos are EDITABLE — a user can
/// type any id, and a genuinely valid image model our id patterns do not know would be blocked with
/// no way around it. Refusing an explicit choice on a guess is worse than letting the provider answer
/// for one call.</para>
///
/// <para><b>Refusal therefore requires INDEPENDENTLY VERIFIED evidence, not inference.</b> An earlier
/// revision inferred "text-only" from <c>IsChatModel(id) &amp;&amp; !IsImageModel(id)</c>. That was
/// wrong and the diff review caught it: <c>IsChatModel</c> is an EXCLUSION filter that returns true
/// for almost any unfamiliar slug, so the inference refused real image models whose ids carry no
/// "image" token. Verified against OpenRouter's per-model endpoint on 2026-07-30:
/// <c>bytedance-seed/seedream-4.5</c> and <c>black-forest-labs/flux.2-pro</c> are both
/// <c>output_modalities: ["image"]</c> and would have been refused. Only ids in
/// <see cref="KnownTextOnlyModels"/> — each one checked against the provider's own metadata — can be
/// refused; everything else is unknown capability and proceeds.</para>
///
/// Pure: no HTTP, no filesystem, no settings access. Pinned by <c>ImageModelGateTests</c>.
/// </summary>
internal static class ImageModelGate
{
    /// <summary>
    /// Model ids INDEPENDENTLY VERIFIED as text-only against the provider's own metadata. This list
    /// is deliberately tiny and grows only by evidence — an id belongs here after someone confirmed
    /// its output modalities, never because a classifier guessed.
    /// <para>Bear the cost of it being incomplete: an unlisted text model still reaches the provider
    /// and fails there, which costs one call. The alternative — inferring text-ness — refuses working
    /// image models, which costs the user a feature with no override.</para>
    /// </summary>
    private static readonly HashSet<string> KnownTextOnlyModels = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenRouter. output_modalities ["text"], verified 2026-07-30 via
        // GET /api/v1/models/aion-labs/aion-2.0/endpoints. Found persisted as
        // aiImageModel_openrouter — a pre-UX-1 alphabetical-list[0] leftover.
        "aion-labs/aion-2.0",
    };

    /// <summary>
    /// True only for an id verified text-only. The <paramref name="isImageModel"/> check is a
    /// fail-open safety valve: if the image classifier ever recognises a listed id, believe the
    /// classifier and proceed rather than refuse.
    /// <para>Everything not on the list is "unknown capability" and proceeds — including
    /// <c>dall-e-3</c>, which <c>AIEnhancementService.EnsureModelStillSupported</c> refuses further
    /// downstream with an accurate retired-family message. This gate deliberately does not pre-empt
    /// that guard.</para>
    /// </summary>
    internal static bool IsKnownNonImageModel(string model, Func<string, bool> isImageModel)
        => !string.IsNullOrWhiteSpace(model)
           && !isImageModel(model)
           && KnownTextOnlyModels.Contains(model.Trim());

    /// <summary>
    /// Applies the decision matrix. A blank model proceeds unchanged — resolution never produces one
    /// (it falls through to the provider default), and inventing a refusal for it would change an
    /// unrelated path.
    /// </summary>
    internal static ImageModelGateAction Decide(
        ImageModelResolution resolution,
        Func<string, bool> isImageModel)
    {
        if (!IsKnownNonImageModel(resolution.Model, isImageModel))
            return ImageModelGateAction.Proceed;

        return resolution.Source switch
        {
            // The stale-setting case this gate exists for. Clearing the key means the NEXT attempt
            // resolves to the provider default and works: one honest refusal, then healed.
            ImageModelSource.PersistedProviderSetting => ImageModelGateAction.RefuseAndHeal,

            // Explicit user choices. Refuse — a text model cannot make an image — but never rewrite
            // what the user or their prompt asked for.
            ImageModelSource.PromptOverride or ImageModelSource.PickerSelection => ImageModelGateAction.Refuse,

            // Our own default is wrong: a code bug. Fail closed and log loudly; clearing a user
            // setting would not be the fix, since no user setting is involved.
            ImageModelSource.ProviderDefault => ImageModelGateAction.Refuse,

            _ => ImageModelGateAction.Refuse,
        };
    }

    /// <summary>
    /// The user-facing refusal copy. App-authored and actionable, within the 55-code-unit pill budget
    /// for every provider name (longest today: "OpenRouter" → 35).
    /// </summary>
    internal static string RefusalMessage(string providerName) => $"Pick an image model for {providerName}";
}
