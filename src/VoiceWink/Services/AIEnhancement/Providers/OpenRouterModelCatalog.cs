namespace VoiceWink.Services.AIEnhancement.Providers;

/// <summary>Which catalog a fetch is building. Selects OpenRouter's modality query.</summary>
public enum ModelCatalogQuery
{
    Text,
    Image,
}

/// <summary>
/// One row of OpenRouter's models response, reduced to the fields that decide inclusion.
/// The modality lists are the evidence; the id is not.
/// </summary>
internal readonly record struct OpenRouterModelEntry(
    string Id,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities,
    string? ExpirationDate);

/// <summary>
/// Curates OpenRouter's catalogs from the metadata OpenRouter publishes.
///
/// <para><b>Where the rows come from.</b> TEXT rows come from the general list's modality query
/// (<c>/api/v1/models?output_modalities=text</c> — the plain list omits image models entirely, so
/// it ≈ the text catalog). IMAGE rows come from the dedicated Image API catalog
/// (<c>/api/v1/images/models</c>) since 2026-07-31: OpenRouter adds new image models EXCLUSIVELY
/// to the dedicated API, so the former <c>?output_modalities=image</c> query would go stale.
/// (History: the plain list never contained <c>openai/gpt-image-2</c> — the app's own OpenRouter
/// image default — and <c>IsImageModel</c> recognised only 7 of the 11 image models that WERE in
/// it, so the picker offered 7 of a real 40 until PR #285's modality queries.)</para>
///
/// <para><b>Positive checks, not source trust.</b> The catalog URL is a request; the row's own
/// <c>output_modalities</c> is the evidence. A row whose metadata is missing or malformed is left out
/// of curated discovery rather than assumed — while <c>RawCount</c> (the pre-parse array length that
/// answers "is this key valid") is untouched.</para>
///
/// <para><b>Image-side retirement hiding is best-effort</b> (recorded policy, 2026-07-31): the
/// dedicated image catalog does not document <c>expiration_date</c>, so the scheduled-retirement
/// rule below may simply never fire there. That is acceptable — the weekly /vw-model-review is the
/// durable retirement watch; discovery-hiding was always best-effort UX on top.</para>
///
/// <para><b>A retirement date hides a model only once it has PASSED</b> (owner decision,
/// 2026-08-03): the rule was "any non-blank <c>expiration_date</c> hides", which hid the live
/// <c>z-ai/glm-5-turbo</c> and <c>z-ai/glm-5v-turbo</c> over a <c>2098-12-31</c> value and hid
/// <c>z-ai/glm-4.5</c> five months before a retirement it serves normally right up to. See
/// <see cref="Helpers.ModelRetirement"/> for the rule, the evidence, the accepted trade-off, and
/// why the verdict must never be cached or persisted.</para>
///
/// Pure: no HTTP, no filesystem. Pinned by <c>OpenRouterModelCatalogTests</c> against fixtures taken
/// from the live catalogs on 2026-07-30.
/// </summary>
internal static class OpenRouterModelCatalog
{
    /// <summary>
    /// Meta-router products: they pick a model per request, so neither the model nor the price is
    /// knowable from the selection. They appear in BOTH modality catalogs (`text` and `image`), so
    /// they need an explicit exclusion rather than falling out of a modality filter.
    /// <para>The WHOLE vendor is excluded (ENH-10, widened 2026-07-30 from the original
    /// <c>openrouter/auto</c> family): every id in the namespace is one of OpenRouter's own routing
    /// products — <c>auto</c>, <c>auto-beta</c>, <c>free</c> ("selects free models at random"),
    /// <c>fusion</c> (multi-model deliberation panel), <c>bodybuilder</c> (builds API request
    /// objects), <c>pareto-code</c> (tiered coding-model router) — all descriptions-verified
    /// 2026-07-30, and future router products will land in the same namespace. Matched on the
    /// slash-delimited VENDOR segment, so a hypothetical third-party <c>openrouter-labs/x</c> is
    /// never mistaken for it.</para>
    /// </summary>
    private const string RouterVendor = "openrouter";

    /// <summary>
    /// OpenRouter routing-variant suffix excluded from discovery (ENH-10, owner decision
    /// 2026-07-30). Every <c>:batch</c> row in the live catalog is a serving/pricing VARIANT
    /// duplicate of a base model listed alongside it (all 28 have their bare sibling in the same
    /// catalog — pinned by the committed fixture), so hiding them removes no capability. Other
    /// variants (<c>:free</c>, <c>:nitro</c>, <c>:floor</c>, <c>:thinking</c>) are untouched.
    /// </summary>
    private const string BatchVariant = "batch";

    /// <summary>
    /// Recraft's vector line returns SVG — verified live 2026-07-30:
    /// <c>recraft/recraft-v4.1-vector</c> answered HTTP 200 with a payload decoding to
    /// <c>&lt;svg </c>, which the PNG save pipeline cannot store.
    /// <para><b>Scoped to the verified vendor</b>, not a global `vector` segment rule: the evidence
    /// covers Recraft only, and a `vector` token in another vendor's slug proves nothing about its
    /// output format. <see cref="Helpers.ImageBytesFormat"/> is the runtime backstop for every other
    /// route into an SVG payload (typed ids, prompt overrides, persisted selections) — this rule only
    /// keeps known-unusable models out of DISCOVERY.</para>
    /// </summary>
    private const string RecraftVendorPrefix = "recraft/";

    /// <summary>
    /// Safety classifiers, not chat models. Provider-scoped and delimiter-exact on purpose: the
    /// shared <c>ExcludedModelPatterns</c> already carries <c>safeguard</c> (so
    /// <c>openai/gpt-oss-safeguard-20b</c> is handled), but adding a bare <c>guard</c> there would
    /// misfire across every other provider — the cross-provider drift the Mistral work avoided.
    /// </summary>
    private const string LlamaGuardFamily = "llama-guard";

    /// <param name="today">
    /// The caller's current date, feeding <see cref="Helpers.ModelRetirement"/>. Injected
    /// rather than read here so this type stays clock-free and its tests stay deterministic — a
    /// dated fixture must curate the same way forever, not drift as its retirement dates age past.
    /// </param>
    internal static List<string> Curate(
        IReadOnlyList<OpenRouterModelEntry> entries,
        ModelCatalogQuery query,
        Func<string, bool> isChatModel,
        DateOnly today)
    {
        var kept = new List<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            // Every rule below reads the piece of the id it means (ENH-10): the slash-delimited
            // vendor, the bare model name with its :variant stripped, or the variant itself.
            var (vendor, bareCore, variant) = Decompose(entry.Id);

            // Meta-router products: excluded from both catalogs.
            if (vendor.Equals(RouterVendor, StringComparison.OrdinalIgnoreCase))
                continue;

            // :batch serving variants: excluded from both catalogs (none exist in the image
            // catalog today — future-proofing there). See BatchVariant.
            if (variant.Equals(BatchVariant, StringComparison.OrdinalIgnoreCase))
                continue;

            // An ALREADY-PASSED retirement hides the model from DISCOVERY only — a persisted
            // selection is never migrated, cleared, or runtime-blocked.
            //
            // A model retiring next month still works this month, so it stays listed until the day
            // it stops working (owner decision, 2026-08-03). This used to hide any row with a
            // non-blank date, which hid the LIVE z-ai/glm-5-turbo and glm-5v-turbo over a
            // 2098-12-31 value, and hid z-ai/glm-4.5 five months early. Rule and evidence live in
            // ModelRetirement. Mistral deliberately keeps its own "any non-null deprecation
            // excludes", which is owner-approved and recorded in CLAUDE.md.
            if (Helpers.ModelRetirement.HasRetired(entry.ExpirationDate, today))
                continue;

            if (query == ModelCatalogQuery.Image)
            {
                // Positive evidence, both directions: it must produce an image, AND accept a text
                // prompt — VoiceWink always starts from dictated text, so an image-to-image-only
                // model would enter a flow it cannot serve. (All 40 pass today; the rule is for the
                // model that will not.)
                if (!HasModality(entry.OutputModalities, "image")) continue;
                if (!HasModality(entry.InputModalities, "text")) continue;

                // Known-unusable output format — see RecraftVendorPrefix.
                if (IsRecraftVector(entry.Id)) continue;
            }
            else
            {
                if (!HasModality(entry.OutputModalities, "text")) continue;

                // The shared product baseline still applies to text (reasoning/instruct/
                // snapshot families etc. stay hidden by standing policy).
                if (!isChatModel(entry.Id)) continue;

                // …and AGAIN on the bare id (ENH-10): the baseline's prefix-sensitive rules
                // (`o`+digit reasoning, `gpt-3.5`, `chatgpt-`) read the FIRST characters, which a
                // vendor slug hides — `openai/o1` and `openai/gpt-3.5-turbo` sailed through.
                // Re-running the SAME shared predicate on the bare part applies those rules without
                // duplicating rule knowledge (a future prefix rule applies here automatically) and
                // is monotone — it can only remove. Variant-stripped, so `vendor/o1:free` and the
                // dated `qwen/qwen-plus-2025-07-28:thinking` (whose `:thinking` tail defeats the
                // $-anchored dated-snapshot regex on the full id) are caught too.
                if (!isChatModel(bareCore)) continue;

                if (IsLlamaGuard(bareCore)) continue;

                // Guardrail classifiers, vendor-agnostic: a delimiter-bounded "content-safety"
                // names moderation models (today: NVIDIA's Nemotron Content Safety line —
                // "moderates both inputs to and responses from LLMs", catalog description
                // 2026-07-30). `vendor/discontent-safety` never matches (segment-exact).
                if (HasConsecutiveSegments(bareCore, "content", "safety")) continue;

                // Morph's apply line: code-edit models REQUIRING a non-chat prompt format
                // ("<instruction>…<code…", catalog description 2026-07-30). Family-shaped
                // (morph-v3 today, a morph-v4 later); `morph/morph-vault-chat` never matches.
                // Accepted mirror risk: a future Morph CHAT model would be hidden — watched by the
                // weekly /vw-model-review.
                if (IsMorphApply(vendor, bareCore)) continue;

                // Relace's code-patching line ("merges AI-suggested edits straight into your
                // source files", catalog description 2026-07-30). relace-search is already caught
                // by the shared `search` pattern.
                if (IsRelaceApply(vendor, bareCore)) continue;

                // Deliberately KEPT (owner decision 2026-07-30): the ten `~vendor/…-latest` alias
                // rows — functional evergreen redirects to a family's current model, same spirit
                // as Gemini's `-latest` pointers. Do not re-flag them as a leak.
            }

            kept.Add(entry.Id);
        }

        kept.Sort(StringComparer.OrdinalIgnoreCase);
        return kept;
    }

    /// <summary>
    /// The one id-normalization path every ENH-10 rule reads from: <c>vendor</c> = before the
    /// FIRST <c>/</c> (empty when unslugged), <c>bareCore</c> = after the LAST <c>/</c> with any
    /// terminal <c>:variant</c> removed, <c>variant</c> = that terminal suffix (empty when none).
    /// </summary>
    private static (string Vendor, string BareCore, string Variant) Decompose(string modelId)
    {
        var firstSlash = modelId.IndexOf('/');
        var vendor = firstSlash >= 0 ? modelId[..firstSlash] : "";

        var lastSlash = modelId.LastIndexOf('/');
        var bare = lastSlash >= 0 ? modelId[(lastSlash + 1)..] : modelId;

        var colon = bare.LastIndexOf(':');
        return colon >= 0
            ? (vendor, bare[..colon], bare[(colon + 1)..])
            : (vendor, bare, "");
    }

    /// <summary>
    /// True when <paramref name="first"/> and <paramref name="second"/> appear as CONSECUTIVE
    /// delimiter-bounded segments of <paramref name="bareCore"/> (split on <c>-</c> and <c>.</c>).
    /// </summary>
    private static bool HasConsecutiveSegments(string bareCore, string first, string second)
    {
        var segments = bareCore.Split('-', '.');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals(first, StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals(second, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Vendor `morph` + bare segments starting `morph`, `v&lt;digits&gt;` — see the call site.</summary>
    private static bool IsMorphApply(string vendor, string bareCore)
    {
        if (!vendor.Equals("morph", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = bareCore.Split('-', '.');
        return segments.Length >= 2
            && segments[0].Equals("morph", StringComparison.OrdinalIgnoreCase)
            && segments[1].Length >= 2
            && (segments[1][0] == 'v' || segments[1][0] == 'V')
            && segments[1][1..].All(char.IsDigit);
    }

    /// <summary>Vendor `relace` + bare `relace-apply` or `relace-apply-*` — see the call site.</summary>
    private static bool IsRelaceApply(string vendor, string bareCore)
        => vendor.Equals("relace", StringComparison.OrdinalIgnoreCase)
           && (bareCore.Equals("relace-apply", StringComparison.OrdinalIgnoreCase)
               || bareCore.StartsWith("relace-apply-", StringComparison.OrdinalIgnoreCase));

    private static bool HasModality(IReadOnlyList<string>? modalities, string wanted)
    {
        if (modalities is null) return false;
        foreach (var m in modalities)
            if (string.Equals(m, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// A Recraft model whose slug carries a delimiter-bounded <c>vector</c> segment.
    /// <c>recraft-v4.1-utility</c> (raster) is deliberately NOT matched.
    /// </summary>
    private static bool IsRecraftVector(string modelId)
    {
        if (!modelId.StartsWith(RecraftVendorPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var segment in modelId.Split('-', '/', '.'))
            if (segment.Equals("vector", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Delimiter-exact <c>llama-guard</c> / <c>llama-guard-*</c> on the decomposed bare id. ENH-10
    /// moved it onto <c>Decompose</c>'s variant-stripped bare part: <c>llama-guard-4-12b:free</c>
    /// matched before and still does, and a bare <c>vendor/llama-guard:free</c> — which the old
    /// self-derived bare (variant attached) missed — now correctly matches too.
    /// </summary>
    private static bool IsLlamaGuard(string bareCore)
        => bareCore.Equals(LlamaGuardFamily, StringComparison.OrdinalIgnoreCase)
           || (bareCore.Length > LlamaGuardFamily.Length
               && bareCore.StartsWith(LlamaGuardFamily, StringComparison.OrdinalIgnoreCase)
               && bareCore[LlamaGuardFamily.Length] == '-');
}
