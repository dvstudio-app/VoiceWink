using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>
/// One row of a provider catalog, reduced to what a CONSUMER OUTSIDE the app needs: the id, and
/// whatever retirement date that provider publishes for it.
///
/// <para>The app ignores this. It exists for <c>tools/model-filter-sim</c>, whose weekly snapshot
/// records each catalog's ids and their published retirement dates — the one piece the harness
/// would otherwise have to parse for itself, re-opening exactly the hand-transcription this work
/// exists to close (Codex plan review, ENH-20).</para>
///
/// <para><b>The field is provider-agnostic on purpose.</b> OpenRouter publishes
/// <c>expiration_date</c>, Mistral <c>deprecation</c>, OpenAI <c>shutdown_date</c>; one slot holds
/// whichever applies, so a date APPEARING or being WITHDRAWN is visible in a diff without a schema
/// change. Withdrawal is real: <c>z-ai/glm-4.5v</c> published a date, then none, then one again.</para>
///
/// <para><b>Carrying OpenAI's <c>shutdown_date</c> here does NOT make it actionable.</b> Nothing in
/// the app reads it, deliberately: measured 2026-08-19 and again 2026-08-29, several OpenAI ids
/// carry dates already PAST while answering normally and appearing in the dropdown, so that field
/// does not track removal. It is a watch input for the weekly review, never a hiding rule.</para>
/// </summary>
internal readonly record struct CatalogModelRow(string Id, string? PublishedRetirement);

/// <summary>
/// What <see cref="ProviderCatalogParser.ParseAndCurate"/> returns: the display list the app uses,
/// plus the raw projection the harness uses, plus the count the caller needs for its log line.
/// </summary>
/// <param name="List">The display-ready result — exactly what the fetch used to return.</param>
/// <param name="Rows">Every parsed row, id + published retirement. Empty when nothing parsed.</param>
/// <param name="CuratedInputCount">
/// How many entries were handed to a curation class, i.e. the <c>total=</c> in the caller's log
/// line. Zero when this provider/query did not curate — read it only when
/// <see cref="ProviderModelList.Curated"/> is true.
/// </param>
internal readonly record struct ParsedProviderCatalog(
    ProviderModelList List,
    IReadOnlyList<CatalogModelRow> Rows,
    int CuratedInputCount);

/// <summary>
/// Turns a provider's raw <c>/models</c> JSON into the display list the dropdowns show.
///
/// <para><b>Pure by contract — no HTTP, no Serilog, no DI, no clock</b> (<paramref name="today"/> is
/// injected). Split out of <c>OpenAICompatibleModelFetch.FetchAsync</c> for ENH-20: while this logic
/// sat behind an HTTP call, <c>/vw-model-review</c> could not run it and re-implemented it in
/// PowerShell instead, which produced a defect on four consecutive weekly runs. The fetch keeps the
/// HTTP half and calls this; <c>tools/model-filter-sim</c> calls it directly with a dumped body, so
/// both answer "what would the dropdown show?" from the SAME code.</para>
///
/// <para><b>Adding a dependency here re-arms that</b>, and CI would not catch it — the harness sits
/// outside <c>VoiceWink.sln</c> by the <c>tools/</c> convention. Logging belongs to the caller: this
/// returns the counts a log line needs rather than emitting one.</para>
/// </summary>
internal static class ProviderCatalogParser
{
    /// <param name="json">The provider's raw response body.</param>
    /// <param name="provider">Which provider's shapes and curation rules to apply.</param>
    /// <param name="query">Text or image catalog — selects curation and the display filter.</param>
    /// <param name="unfiltered">The user's ShowAllModels escape hatch: skips curation entirely.</param>
    /// <param name="today">
    /// UTC date for the retirement rule. Injected rather than read here so this stays clock-free and
    /// its tests stay deterministic; production passes <c>DateOnly.FromDateTime(DateTime.UtcNow)</c>.
    /// UTC, not local — a published retirement date carries no timezone, and reading it in the
    /// machine's zone would let the same catalog curate differently in two places.
    /// </param>
    internal static ParsedProviderCatalog ParseAndCurate(
        string json,
        AIProvider provider,
        ModelCatalogQuery query,
        bool unfiltered,
        DateOnly today)
    {
        using var doc = global::System.Text.Json.JsonDocument.Parse(json);

        // Every parsed row, for the harness's snapshot. Collected before the per-provider branches
        // below, all of which `continue`.
        var rows = new List<CatalogModelRow>();


        // Mistral publishes per-model `capabilities` + a `deprecation` date, which is strictly
        // better evidence than id substrings; collect the entries so MistralModelCatalog can use
        // them. Every other provider keeps the id-only path byte-for-byte.
        var curateForMistral = provider == AIProvider.Mistral && !unfiltered;
        var mistralEntries = curateForMistral ? new List<MistralModelEntry>() : null;

        // OpenRouter publishes per-row input/output modalities — the evidence the id patterns could
        // never supply. Collected the same way, for OpenRouterModelCatalog. `expiration_date` is
        // read tolerantly: the dedicated image catalog does not document it, so image-side
        // retirement hiding is BEST-EFFORT by recorded policy (2026-07-31) — the weekly
        // /vw-model-review is the durable retirement watch.
        var curateForOpenRouter = provider == AIProvider.OpenRouter && !unfiltered;
        var openRouterEntries = curateForOpenRouter ? new List<OpenRouterModelEntry>() : null;

        // IMG-5: the Image API catalog publishes typed `supported_parameters` per model — the
        // evidence id patterns could never supply, on the SAME response the picker already fetches
        // (no extra HTTP). Collected for EVERY image fetch, curated or not: ShowAllModels widens
        // the list but must not blind the option gating, and a model the curation hides can still
        // be a persisted selection whose options need resolving. Text queries collect nothing.
        var collectImageCapabilities = provider == AIProvider.OpenRouter
            && query == ModelCatalogQuery.Image;
        var imageCapabilities = collectImageCapabilities
            ? new Dictionary<string, ImageModelCapabilities>(StringComparer.OrdinalIgnoreCase)
            : null;

        // Gemini publishes NO capability metadata on this endpoint (the native
        // supportedGenerationMethods is stripped), so its TEXT list goes through the Curated branch
        // (GeminiModelCatalog, ENH-9) — which since 2026-09-13 applies only the injected shared
        // baseline, whose HasLiveSegment is what hides the bidi-only Live ids. TEXT query only: the
        // image query must stay uncurated so ApplyDisplayPolicy's IsImageModel branch keeps building
        // the image list — a Curated image result without an image filter would dump the whole
        // catalog into the picker.
        var curateForGeminiText = provider == AIProvider.Gemini
            && !unfiltered && query == ModelCatalogQuery.Text;
        var geminiIds = curateForGeminiText ? new List<string>() : null;

        // Cerebras publishes no capability metadata either, and — the reason ENH-25 exists — keeps a
        // model in this catalog after withdrawing it from its public endpoints (gemma-4-31b since
        // 2026-09-03), which no id classifier can see. Its TEXT list goes through the Curated branch
        // (CerebrasModelCatalog: the withdrawn-id table, then the injected shared baseline). TEXT
        // query only, and skipped when unfiltered, for the same reasons as Gemini's branch.
        var curateForCerebrasText = provider == AIProvider.Cerebras
            && !unfiltered && query == ModelCatalogQuery.Text;
        var cerebrasIds = curateForCerebrasText ? new List<string>() : null;

        var models = new List<string>();
        var rawCount = 0;
        var dataIsArray = doc.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == global::System.Text.Json.JsonValueKind.Array;
        if (dataIsArray)
        {
            // RawCount is the ARRAY LENGTH, taken before any parsing. It answers "did the provider
            // return a catalog", which is what key-validity decisions hinge on — so an entry we
            // fail to parse must not make a valid key look rejected.
            rawCount = data.GetArrayLength();

            foreach (var model in data.EnumerateArray())
            {
                // Kind-guarded on both sides: a non-object entry or a non-string id is SKIPPED
                // (omitted from Models, RawCount untouched) rather than throwing —
                // JsonElement.GetString() throws on a non-string, which would abort the whole fetch
                // over one malformed row.
                if (model.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
                    model.TryGetProperty("id", out var id) &&
                    id.ValueKind == global::System.Text.Json.JsonValueKind.String &&
                    id.GetString() is string modelId)
                {
                    // Gemini returns IDs with "models/" prefix — strip for consistency with
                    // the IDs clients send in request bodies.
                    //
                    // ANTHROPIC IS EXCLUDED, and the exclusion is load-bearing (Codex diff review,
                    // ENH-20). This normalization belongs to the OpenAI-compatible fetch; Anthropic's
                    // own loop — which this method replaced — returned catalog ids VERBATIM. Applying
                    // it there would make the app display and SEND an id the catalog never published,
                    // which is a behaviour change smuggled into an extraction that claims to preserve
                    // behaviour. Whether stripping would be "better" for Anthropic is a separate
                    // product question nobody has asked; a refactor is not the place to answer it.
                    if (provider != AIProvider.Anthropic &&
                        modelId.StartsWith("models/", StringComparison.Ordinal))
                        modelId = modelId["models/".Length..];

                    // Before the per-provider branches below, all of which `continue`.
                    rows.Add(new CatalogModelRow(modelId, ReadPublishedRetirement(model, provider)));

                    if (imageCapabilities != null)
                        imageCapabilities[modelId] = ReadImageCapabilities(model);

                    if (mistralEntries != null)
                    {
                        mistralEntries.Add(new MistralModelEntry(
                            modelId,
                            ReadOptionalBool(model, "capabilities", "completion_chat"),
                            ReadOptionalBool(model, "capabilities", "completion_fim"),
                            ReadOptionalString(model, "deprecation")));
                        continue;
                    }

                    if (openRouterEntries != null)
                    {
                        openRouterEntries.Add(new OpenRouterModelEntry(
                            modelId,
                            ReadStringArray(model, "architecture", "input_modalities"),
                            ReadStringArray(model, "architecture", "output_modalities"),
                            ReadOptionalString(model, "expiration_date")));
                        continue;
                    }

                    if (geminiIds != null)
                    {
                        geminiIds.Add(modelId);
                        continue;
                    }

                    if (cerebrasIds != null)
                    {
                        cerebrasIds.Add(modelId);
                        continue;
                    }

                    // The TEXT classifier must not run on an IMAGE query: it rejects every image
                    // model by design (gpt-image*, *-image), so applying it here would empty the
                    // image catalog before the service's image policy ever saw it. Descriptors
                    // return modality-appropriate CANDIDATES; ModelDisplayPolicy.ApplyDisplayPolicy
                    // makes the display decision (Codex plan review r2).
                    if (unfiltered || query == ModelCatalogQuery.Image || ModelDisplayPolicy.IsChatModel(modelId))
                        models.Add(modelId);
                }
            }
        }

        // Snapshot integrity (IMG-5): a 200 whose `data` is missing, not an array, OR EMPTY proves
        // nothing useful about the catalog, so none of them may be committed as a capability
        // snapshot. Null means "this fetch produced no snapshot"; the caller preserves the
        // last-good one.
        //
        // Empty is included deliberately (Kimi diff r2, adopted 2026-08-01). An earlier revision
        // treated `{"data":[]}` as an authoritative "the catalog lists nothing" — but operationally
        // that response is far more likely a transient provider fault than OpenRouter deleting all
        // 34 image models, and committing it erased the good cache and silently degraded every
        // request to Auto (options stripped, visible only as log adjustments). The asymmetry is
        // decisive: an empty catalog ALSO empties the model picker, so preserving stale
        // capabilities costs nothing real, while discarding them on a blip costs the user their
        // size and aspect choices. The delay in noticing a genuinely emptied catalog is covered by
        // the weekly /vw-model-review.
        // The gate is "no rows PARSED", not "the array was empty" (Codex diff r3). `rawCount` is
        // the pre-parse array length, so `{"data":[null]}` counts 1 while yielding zero usable
        // rows — and an empty-but-committed map is authoritative, which would push every
        // OpenRouter model to all-Auto and strip its options. Keying on the map itself covers the
        // empty array, the all-malformed array, and any future shape that parses to nothing.
        if (!dataIsArray || imageCapabilities is { Count: 0 })
            imageCapabilities = null;

        // The four curated branches return the SAME ProviderModelList they always did. What moved
        // out is only the logging: the caller reads CuratedInputCount and emits the line, because a
        // parser that logs cannot be linked into the harness.
        if (mistralEntries != null)
        {
            var curated = MistralModelCatalog.Curate(mistralEntries, ModelDisplayPolicy.IsChatModel);
            return new ParsedProviderCatalog(
                new ProviderModelList(curated, rawCount, Curated: true), // already sorted
                rows, mistralEntries.Count);
        }

        if (openRouterEntries != null)
        {
            var curated = OpenRouterModelCatalog.Curate(
                openRouterEntries, query, ModelDisplayPolicy.IsChatModel, today);
            return new ParsedProviderCatalog(
                new ProviderModelList(curated, rawCount, Curated: true, ImageCapabilities: imageCapabilities), // already sorted
                rows, openRouterEntries.Count);
        }

        if (geminiIds != null)
        {
            var curated = GeminiModelCatalog.Curate(geminiIds, ModelDisplayPolicy.IsChatModel);
            return new ParsedProviderCatalog(
                new ProviderModelList(curated, rawCount, Curated: true), // already sorted
                rows, geminiIds.Count);
        }

        if (cerebrasIds != null)
        {
            var curated = CerebrasModelCatalog.Curate(cerebrasIds, ModelDisplayPolicy.IsChatModel);
            return new ParsedProviderCatalog(
                new ProviderModelList(curated, rawCount, Curated: true), // already sorted
                rows, cerebrasIds.Count);
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        // The unfiltered (ShowAllModels) image path lands here — capabilities ride it too, since
        // widening the list must not blind the option gating.
        return new ParsedProviderCatalog(
            new ProviderModelList(models, rawCount, ImageCapabilities: imageCapabilities),
            rows, 0);
    }

    /// <summary>
    /// The retirement date THIS provider publishes, or null. One field per provider, read only for
    /// <see cref="CatalogModelRow"/> — the app's own hiding rule reads its own field inside the
    /// per-provider entry types, and is untouched by this.
    /// </summary>
    private static string? ReadPublishedRetirement(
        global::System.Text.Json.JsonElement model, AIProvider provider) => provider switch
    {
        AIProvider.OpenRouter => ReadOptionalString(model, "expiration_date"),
        AIProvider.Mistral    => ReadOptionalString(model, "deprecation"),
        AIProvider.OpenAI     => ReadOptionalString(model, "shutdown_date"),
        _ => null,
    };

    /// <summary>
    /// Reads <c>&lt;object&gt;.&lt;property&gt;</c> as a string array, or an empty list when absent or
    /// the wrong shape. Empty is meaningful to <see cref="OpenRouterModelCatalog"/>: a row with no
    /// usable modality evidence stays out of curated discovery rather than being assumed.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(
        global::System.Text.Json.JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var obj) ||
            obj.ValueKind != global::System.Text.Json.JsonValueKind.Object ||
            !obj.TryGetProperty(propertyName, out var arr) ||
            arr.ValueKind != global::System.Text.Json.JsonValueKind.Array)
        {
            return global::System.Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == global::System.Text.Json.JsonValueKind.String && item.GetString() is string s)
                values.Add(s);
        return values;
    }

    /// <summary>
    /// Reads <c>&lt;object&gt;.&lt;property&gt;</c> as a boolean, or <see langword="null"/> when the
    /// object or property is absent, JSON null, or not a boolean. The null is load-bearing:
    /// <see cref="MistralModelCatalog"/> treats "unknown" differently from "false", so a response
    /// shape change can never be read as "this model cannot chat".
    /// </summary>
    private static bool? ReadOptionalBool(
        global::System.Text.Json.JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var obj) ||
            obj.ValueKind != global::System.Text.Json.JsonValueKind.Object ||
            !obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            global::System.Text.Json.JsonValueKind.True => true,
            global::System.Text.Json.JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a string property, or <see langword="null"/> when absent, JSON null, or another kind.
    /// </summary>
    private static string? ReadOptionalString(
        global::System.Text.Json.JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == global::System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    // A retirement date is read with the plain ReadOptionalString above, DELIBERATELY (Codex diff
    // review, 2026-08-03). An earlier revision of the horizon work passed a wrong-KIND value
    // through as its raw JSON text so it would fail to parse and hide the model. That looked like
    // the fail-closed choice and was not: the shipped reader collapses a non-string kind to null,
    // which SHOWS the model, so the "raw text" version silently inverted behaviour on a path the
    // change was not meant to touch — and worse, a catalog-wide schema-kind change would then hide
    // EVERY row and empty the dropdown, which is the failure `ProviderModelList.RawCount` exists to
    // keep impossible. Showing a model whose retirement we cannot read is the strictly milder
    // error. It also keeps the Mistral and OpenRouter readers symmetric.

    /// <summary>
    /// IMG-5: reads one Image API row's <c>supported_parameters</c> into
    /// <see cref="ImageModelCapabilities"/>.
    ///
    /// <para><b>Absence is authoritative; wrong shape is not.</b> A parameter the catalog omits
    /// genuinely means the model does not take it — that is the whole point of reading published
    /// capabilities, and treating it as "unknown" would put us back to guessing. But a parameter
    /// present in an unexpected SHAPE (OpenRouter revising its schema) proves nothing. So the
    /// readers answer differently for the two: <b>absent ⇒ empty</b> (authoritative unsupported),
    /// <b>wrong shape ⇒ null</b> (unknown, falls back to the static rule). The distinction is drawn
    /// per PARAMETER rather than per row: one drifted field must not discard a row's other good
    /// evidence, and must not be reported as a confident "unsupported".</para>
    ///
    /// <para>Aspects are intersected with <see cref="ImageOptions.AllKnownAspects"/> and
    /// <c>auto</c> is dropped — see <see cref="ImageModelCapabilities"/> for why that happens here
    /// rather than at the consumers.</para>
    /// </summary>
    private static ImageModelCapabilities ReadImageCapabilities(
        global::System.Text.Json.JsonElement model)
    {
        var aspects = ReadEnumValues(model, "aspect_ratio", ImageOptions.AllKnownAspects);
        var tiers = ReadEnumValues(model, "resolution", ImageOptions.AllTiers);
        // IMG-12: the published VALUES, not merely the parameter's presence — the wire enum
        // (low/medium/high, plus xhigh/max since IMG-14) is translated to the app's tags here, so every consumer downstream
        // speaks one vocabulary. `auto` and anything the app has no tag for map to null and drop,
        // exactly as `auto` and unrenderable ratios already drop from the aspect list.
        var quality = ReadMappedEnumValues(model, "quality", ImageOptions.QualityTagForWireValue);
        var maxReferences = ReadRangeMax(model, "input_references");
        return new ImageModelCapabilities(aspects, tiers, quality, maxReferences);
    }

    /// <summary>
    /// Reads <c>supported_parameters.&lt;name&gt;.values</c>, keeping only entries present in
    /// <paramref name="vocabulary"/> (catalog order preserved). Case-insensitive against the
    /// vocabulary so a catalog casing change can't silently drop a value. Tri-state per
    /// <see cref="ReadMappedEnumValues"/>: absent ⇒ empty, wrong shape ⇒ null.
    /// </summary>
    private static IReadOnlyList<string>? ReadEnumValues(
        global::System.Text.Json.JsonElement model, string name, string[] vocabulary)
        => ReadMappedEnumValues(model, name, value =>
        {
            foreach (var known in vocabulary)
            {
                if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
            return null;
        });

    /// <summary>
    /// The shape-checking body behind every enum-valued parameter, with the catalog value → app
    /// value translation supplied by the caller. A value the mapper rejects is DROPPED, so no
    /// consumer can ever see a value the app has no tag, no combo row and no persistence story for.
    ///
    /// <para>ONE body, because the tri-state contract below is the part that must not drift between
    /// fields — aspects, tiers and quality (IMG-12) all read it here.</para>
    /// </summary>
    private static IReadOnlyList<string>? ReadMappedEnumValues(
        global::System.Text.Json.JsonElement model, string name, Func<string, string?> map)
    {
        // No `supported_parameters` object at all ⇒ unknown, not "supports nothing": the row
        // carries no evidence either way.
        if (!model.TryGetProperty("supported_parameters", out var parameters) ||
            parameters.ValueKind != global::System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        // ABSENT parameter ⇒ authoritative UNSUPPORTED. This is the whole value of reading
        // published capabilities: the catalog omitting `resolution` is how we know gpt-image
        // takes no size tier.
        if (!parameters.TryGetProperty(name, out var parameter))
            return global::System.Array.Empty<string>();

        // PRESENT but not the shape we know ⇒ UNKNOWN. Schema drift proves nothing, and reading
        // it as "unsupported" would silently strip a model's valid aspects or tiers.
        //
        // The `type` discriminator is checked, not just the presence of `values` (Codex diff r2):
        // the catalog documents three shapes — enum / range / boolean — and only `enum` carries a
        // meaningful value list. A `range`-typed field that happened to also carry `values` would
        // otherwise be read as an authoritative enum.
        if (!IsEnumParameter(parameter) ||
            !parameter.TryGetProperty("values", out var values) ||
            values.ValueKind != global::System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var kept = new List<string>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != global::System.Text.Json.JsonValueKind.String) continue;
            if (item.GetString() is not string value) continue;

            // "auto" is the app's null selection, never a tag. Anything outside the app's
            // vocabulary is unofferable and unpersistable — drop it here so no consumer can
            // surface a value the UI cannot render.
            if (map(value) is { } mapped) kept.Add(mapped);
        }

        return kept;
    }

    /// <summary>
    /// True only for the documented <c>{"type":"enum", …}</c> shape. Deliberately strict: a shape
    /// we do not recognise must read as UNKNOWN (⇒ static fallback), never as evidence.
    /// </summary>
    private static bool IsEnumParameter(global::System.Text.Json.JsonElement parameter)
        => parameter.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
           parameter.TryGetProperty("type", out var type) &&
           type.ValueKind == global::System.Text.Json.JsonValueKind.String &&
           string.Equals(type.GetString(), "enum", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>supported_parameters.&lt;name&gt;.max</c> as an int, or null when absent or not a
    /// number. Enforced since IMG-6 as the dialog's raise-only ADD bound (see
    /// <see cref="ImageModelCapabilities"/>).
    /// </summary>
    private static int? ReadRangeMax(global::System.Text.Json.JsonElement model, string name)
        => model.TryGetProperty("supported_parameters", out var parameters) &&
           parameters.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
           parameters.TryGetProperty(name, out var parameter) &&
           parameter.ValueKind == global::System.Text.Json.JsonValueKind.Object &&
           parameter.TryGetProperty("max", out var max) &&
           max.ValueKind == global::System.Text.Json.JsonValueKind.Number &&
           max.TryGetInt32(out var value)
            ? value
            : null;
}
