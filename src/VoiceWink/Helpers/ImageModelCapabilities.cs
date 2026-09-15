namespace VoiceWink.Helpers;

/// <summary>
/// What ONE image model publishes about the options it accepts, read from the provider's own
/// catalog rather than inferred from its id (IMG-5).
///
/// <para><b>Absent object vs empty list is the load-bearing distinction.</b> A null
/// <see cref="ImageModelCapabilities"/> means "we have no catalog data for this model" — the caller
/// falls back to <see cref="ImageOptions"/>'s static rules. A NON-null instance with an empty
/// <see cref="Aspects"/> or <see cref="Tiers"/> means "the catalog says this model publishes no such
/// parameter" — the caller hides that control. Collapsing the two would make an unfetched model
/// indistinguishable from one that genuinely takes no size, which is how the app ended up offering
/// three size tiers to <c>openai/gpt-5-image</c> (it publishes neither aspect_ratio nor resolution).</para>
///
/// <para><b>Aspects are pre-intersected with what the app can render</b> (see
/// <see cref="ImageOptions.AllKnownAspects"/>). The catalog publishes ratios the app has no constant,
/// no combo entry, and no persistence story for — <c>9:19.5</c>, <c>19.5:9</c>, <c>9:20</c>,
/// <c>20:9</c> on seedream/grok. Carrying them would make <c>SupportedAspectsFor</c> claim an aspect
/// that <c>PopulateImageAspectComboWithIndicators</c> silently drops (it filters a fixed options
/// table), while <c>NormalizeForModel</c> would accept it as a valid persisted value. Intersecting
/// at PARSE time means every consumer sees only offerable values, so that incoherence cannot arise.
/// Ratios the app DOES know — incl. <c>1:2</c>/<c>2:1</c>/<c>9:21</c>, which already have constants,
/// combo entries, and redo support — pass through normally.</para>
///
/// <para><b><see cref="MaxReferences"/> is ENFORCED as of IMG-6</b> (2026-08-02; this said
/// "observability only" until then). It sets the dialog's reference ADD bound and only ever RAISES
/// it — gpt-image 16, gemini-3 14, riverflow-v2 10 — per the owner's "NO CAP" rule. A figure BELOW
/// the app's fallback never lowers the bound: krea/recraft/mai publish 1, and dropping to 1 would
/// mean discarding photos already chosen under another model, so that direction is an ADVISORY
/// instead. The generation-side read gate deliberately does NOT re-apply the per-model figure — it
/// enforces one generous sanity ceiling, because re-applying it there rejected lists the dialog had
/// legitimately accepted after a model switch.</para>
///
/// Pure data. Produced by the catalog fetch, consumed by <see cref="ImageOptions"/>'s
/// capability-taking overloads — which stay pure because the capabilities are passed IN.
/// </summary>
/// <param name="Aspects">
/// Renderable aspect tags the model accepts, catalog order preserved, <c>auto</c> removed (the app
/// models Auto as a null selection, not a tag). Empty ⇒ the model publishes no aspect_ratio.
/// </param>
/// <param name="Tiers">
/// Size-tier tags the model accepts (<c>512</c>/<c>1K</c>/<c>2K</c>/<c>4K</c>). Empty ⇒ the model
/// publishes no resolution.
/// </param>
/// <param name="Quality">
/// Quality tags the model accepts, translated from the published <c>low</c>/<c>medium</c>/<c>high</c>
/// (<c>xhigh</c>/<c>max</c> since IMG-14) enum into the app's own tags and catalog order preserved. Empty ⇒ the model publishes no
/// <c>quality</c>.
///
/// <para><b>This was a BOOLEAN until IMG-12 (2026-08-13), and the values are why.</b> Knowing that a
/// parameter exists is not knowing what it takes: <c>x-ai/grok-imagine-image-2.0</c> publishes
/// <c>{low, medium}</c>, so the app's "Maximum" tier sent <c>high</c> and earned
/// <c>HTTP 400 … xAI: quality: not supported</c> on every attempt. Aspects and tiers have carried
/// their published values since IMG-5; quality was the field left behind.</para>
/// </param>
/// <param name="MaxReferences">
/// Published <c>input_references.max</c>, or null when the model publishes no reference support.
/// Enforced as the dialog's ADD bound since IMG-6, raise-only — see the type remarks.
/// </param>
/// <remarks>
/// <b>Every field is TRI-STATE, and that is the contract</b> (Codex diff review, 2026-08-01 —
/// an earlier revision collapsed it and this type's own rules said it must not): <c>null</c> =
/// UNKNOWN (the parameter was present but in a shape we do not recognise — provider schema drift),
/// an EMPTY list = authoritative UNSUPPORTED (the catalog omitted the parameter),
/// a populated list = supported, with exactly these values. Unknown falls back to the static rule for THAT FIELD
/// ALONE, so one drifted field can neither strip a model's valid aspects/tiers nor turn an
/// unsupported <c>quality</c> into a sent one. Consumers get this for free via
/// <c>caps?.Field ?? staticRule</c> — the null-coalescing chain handles "no capabilities at all"
/// and "this field unknown" identically, which is correct: both mean "no usable evidence here".
/// </remarks>
public sealed record ImageModelCapabilities(
    IReadOnlyList<string>? Aspects,
    IReadOnlyList<string>? Tiers,
    IReadOnlyList<string>? Quality,
    int? MaxReferences)
{
    /// <summary>
    /// True when this row carries no usable option evidence at all — no aspects, no tiers, no
    /// quality. Such a row is still meaningful (it means "offer nothing but Auto"), so it is NOT
    /// treated as missing data; this exists for logging and for the model-review doc.
    /// </summary>
    public bool PublishesNoOptions =>
        Aspects is { Count: 0 } && Tiers is { Count: 0 } && Quality is { Count: 0 };

    /// <summary>
    /// The answer for an OpenRouter model that a snapshot we HOLD does not list: offer and send
    /// nothing but Auto.
    ///
    /// <para><b>Not</b> the answer when there is no snapshot at all — that returns null (static
    /// rules) instead. The two look similar and are opposites: an absent model in a held catalog is
    /// positive evidence the catalog does not describe it, whereas no catalog is no evidence, and
    /// answering all-Auto there stripped <c>resolution</c>/<c>aspect_ratio</c> from every request
    /// on a fresh install.</para>
    ///
    /// <para><b>Why this is not null.</b> A null capability argument means "no catalog governs this
    /// provider, use <see cref="ImageOptions"/>'s static rules", which is correct for OpenAI-direct
    /// and Gemini-direct (they never appear in this catalog and their static tables are
    /// docs-verified). Letting null ALSO mean "unknown OpenRouter model" would collapse two
    /// opposite instructions into one value — the contradiction both final reviews flagged in the
    /// v2 plan. An unclassified OpenRouter model gets this explicit instance instead.</para>
    ///
    /// <para>All-Auto is the safe direction: omitting every optional field can never provoke a
    /// rejection, whereas the permissive <c>{1K,2K,4K}</c> + <c>CommonAspects</c> guess is precisely
    /// what offered three size tiers and ten aspects to <c>openai/gpt-5-image</c>, which publishes
    /// neither. The cost is a first-run window where OpenRouter models show only Auto until the
    /// first successful image fetch re-gates — seconds, and recorded as an accepted residual.</para>
    /// </summary>
    /// <remarks>Every field is authoritative-UNSUPPORTED here, never unknown — the point is to
    /// omit every optional field, which an unknown would instead route back to the static rules.</remarks>
    public static readonly ImageModelCapabilities AllAuto = new(
        global::System.Array.Empty<string>(),
        global::System.Array.Empty<string>(),
        Quality: global::System.Array.Empty<string>(),
        MaxReferences: null);
}
