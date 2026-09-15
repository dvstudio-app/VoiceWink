namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// A model-list fetch: what the dropdown should show, and how big the provider's catalog actually
/// was.
///
/// The two are separate answers to separate questions, and conflating them caused a real bug class.
/// Key-save flows treat "no models" as "the provider rejected this key" and roll the key back — so
/// judging validity on a FILTERED list means a filtering rule (or a provider changing its metadata
/// shape) can masquerade as a bad API key. <see cref="RawCount"/> is the count BEFORE any filtering
/// and is the only honest input to that decision; <see cref="Models"/> is for display.
///
/// Carrying both in one response also keeps it to a single HTTP call: the alternative — validate
/// with an unfiltered fetch, then re-fetch for display — leaves the dropdown visibly empty next to
/// a green "Key valid" for the length of the second request.
/// </summary>
/// <param name="Models">Ids to offer, already filtered unless the caller asked for unfiltered.</param>
/// <param name="RawCount">Entries the provider returned, before filtering.</param>
/// <param name="Curated">
/// True when provider-scoped curation selected these ids rather than the shared id classifiers —
/// metadata-backed where the provider publishes metadata (Mistral capabilities, OpenRouter
/// modalities), or — for Gemini, whose OpenAI-compatible list carries no
/// <c>supportedGenerationMethods</c> — the <see cref="GeminiModelCatalog"/> branch, which since
/// 2026-09-13 applies only the shared <c>IsChatModel</c> baseline (the Live-line evidence the native
/// endpoint verified now lives in <c>ModelDisplayPolicy.HasLiveSegment</c>).
/// <para><b>Narrow by design.</b> It means only "the id classifier has already been superseded — do
/// not run <c>IsChatModel</c>/<c>IsImageModel</c> over this list". It does NOT exempt the list from
/// the app's display policy (dated snapshots, previews), which
/// <see cref="AIEnhancementService"/> applies to curated and uncurated lists alike. An earlier
/// revision let it bypass display policy too, which would have regressed Mistral — its curation does
/// not remove ordinary previews, and something must.</para>
/// <para>Consumed inside <see cref="AIEnhancementService"/> and never surfaced beyond it: callers
/// receive display-ready lists, so no UI file has to know which providers curate.</para>
/// </param>
/// <remarks>
/// Public because it rides the public <see cref="IAIProviderDescriptor"/> surface — an
/// accessibility formality inside the app assembly, not an API commitment (same reasoning as
/// <c>MiniRecorderPresentation</c>).
/// </remarks>
/// <param name="ImageCapabilities">
/// Per-model image options the provider PUBLISHED, keyed by model id (IMG-5). Populated only by
/// OpenRouter image fetches, whose Image API catalog carries typed <c>supported_parameters</c> on
/// the same response — so this costs no extra HTTP. Null for every other fetch, and null here means
/// "this fetch produced no snapshot" (a non-image query, a failure, or a 200 whose <c>data</c> was
/// not an array), which PRESERVES the last-good cache rather than erasing it. Note a model MISSING
/// from a non-null map is the opposite case — positive evidence, resolved to
/// <see cref="VoiceWink.Helpers.ImageModelCapabilities.AllAuto"/>, not to the static rules.
/// Carried here rather than fetched on demand because the options dialog
/// must open offline — the map is persisted by the caller and reused until the next successful
/// fetch.
/// </param>
public readonly record struct ProviderModelList(
    List<string> Models,
    int RawCount,
    bool Curated = false,
    IReadOnlyDictionary<string, VoiceWink.Helpers.ImageModelCapabilities>? ImageCapabilities = null)
{
    internal static ProviderModelList Empty => new(new List<string>(), 0);

    /// <summary>Both counts are the same when nothing was filtered out.</summary>
    internal static ProviderModelList Unfiltered(List<string> models) => new(models, models.Count);
}
