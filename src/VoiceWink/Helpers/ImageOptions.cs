using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Single source of truth for the three image-generation knobs the UI exposes:
/// <c>aspect</c>, <c>size tier</c>, and <c>quality</c>. Owns the <c>(Aspect, Tier) → W×H</c>
/// synthesis used for OpenAI gpt-image. Gemini consumes <c>aspect</c> + <c>tier</c> directly
/// as <c>aspectRatio</c> + <c>imageSize</c>.
/// </summary>
public static class ImageOptions
{
    // ─── Aspect tags (persisted on CustomPrompt.ImageAspect / TranscriptionRecord.ImageAspect) ───

    public const string Aspect1x1   = "1:1";
    public const string Aspect4x3   = "4:3";
    public const string Aspect3x4   = "3:4";
    public const string Aspect3x2   = "3:2";
    public const string Aspect2x3   = "2:3";
    public const string Aspect16x9  = "16:9";
    public const string Aspect9x16  = "9:16";
    public const string Aspect4x5   = "4:5";
    public const string Aspect5x4   = "5:4";
    public const string Aspect21x9  = "21:9";
    // Extreme ratios — only the gemini-3.1 flash-image family accepts these (all > 3:1, so
    // they violate the gpt-image-2 / 2.5 ratio constraint and are rejected there).
    public const string Aspect1x4   = "1:4";
    public const string Aspect4x1   = "4:1";
    public const string Aspect1x8   = "1:8";
    public const string Aspect8x1   = "8:1";
    // IMG-2: ratios only the flexible-size gpt-image models offer (see GptImage2ExtraAspects).
    public const string Aspect2x1   = "2:1";
    public const string Aspect1x2   = "1:2";
    public const string Aspect9x21  = "9:21";

    /// <summary>Aspects supported by the flexible-size gpt-image models + all current Gemini image models (10).</summary>
    public static readonly string[] CommonAspects =
    {
        Aspect1x1,
        Aspect4x3, Aspect3x2, Aspect16x9, Aspect5x4, Aspect21x9,
        Aspect3x4, Aspect2x3, Aspect9x16, Aspect4x5,
    };

    /// <summary>Extreme ratios — only the <c>gemini-3.1</c> flash-image family accepts these.</summary>
    public static readonly string[] ExtremeAspects =
    {
        Aspect4x1, Aspect8x1, Aspect1x4, Aspect1x8,
    };

    /// <summary>
    /// IMG-2: ratios offered ONLY on OPENAI-DIRECT models with flexible sizing
    /// (<see cref="HasFlexibleSize"/> — gpt-image-2 and, since IMG-13, the two gpt-image-2.5
    /// ids) — their generations/edits endpoints get synthesized W×H, which preserves exact
    /// ratios. Deliberately NOT offered on Gemini (not in its documented set), gpt-image-1x
    /// (whose 3-size bucketing would silently render a different ratio than History records —
    /// Codex plan round 2), or OpenRouter's gpt-image-2 (its unified Image API publishes an
    /// <c>aspect_ratio</c> enum WITHOUT these — live /api/v1/images/models, 2026-07-31;
    /// the pre-migration claim that the unified endpoint documents them was wrong).
    /// </summary>
    public static readonly string[] GptImage2ExtraAspects =
    {
        Aspect2x1, Aspect1x2, Aspect9x21,
    };

    /// <summary>
    /// OpenRouter's gpt-image-2 endpoint publishes exactly these <c>aspect_ratio</c> values
    /// (live <c>/api/v1/images/models</c>, 2026-07-31): <see cref="CommonAspects"/> minus
    /// 5:4/4:5, none of the <see cref="GptImage2ExtraAspects"/>.
    /// </summary>
    public static readonly string[] OpenRouterGptImage2Aspects =
    {
        Aspect1x1,
        Aspect4x3, Aspect3x2, Aspect16x9, Aspect21x9,
        Aspect3x4, Aspect2x3, Aspect9x16,
    };

    /// <summary>
    /// OpenRouter's gpt-image-1/-mini endpoints publish ONLY these <c>aspect_ratio</c>
    /// values (same source, same day).
    /// </summary>
    public static readonly string[] OpenRouterGptImage1xAspects =
    {
        Aspect1x1, Aspect3x2, Aspect2x3,
    };

    /// <summary>
    /// Every aspect tag the app can actually OFFER — the union of the three sets above, and exactly
    /// the non-null tags in <c>AppTheme.PopulateImageAspectComboWithIndicators</c>'s fixed options
    /// table. This is the authority IMG-5's catalog parsing intersects against
    /// (<see cref="ImageModelCapabilities"/>): a live catalog publishes ratios the app has no
    /// constant, no combo entry, and no persistence story for (<c>9:19.5</c>, <c>19.5:9</c>,
    /// <c>9:20</c>, <c>20:9</c> — seedream and grok). Without the intersection, capability-driven
    /// gating would report an aspect that the combo silently drops while
    /// <see cref="NormalizeForModel"/> accepted it as valid.
    /// <para>Keep in sync with that options table — a tag here with no combo entry is unofferable,
    /// and a combo entry missing here would be filtered out of every capability-driven list.
    /// Pinned by <c>ImageOptionsTests</c>.</para>
    /// </summary>
    public static readonly string[] AllKnownAspects =
        Concat(Concat(CommonAspects, ExtremeAspects), GptImage2ExtraAspects);

    /// <summary>
    /// The aspect combo's rows — label, tag, and indicator box size. Pure data (strings + doubles,
    /// no UI types), so it lives HERE rather than in <c>AppTheme</c>, whose static initializer
    /// needs the WinUI runtime and therefore cannot be touched from a unit test.
    ///
    /// <para>That placement is what makes the drift guard real (IMG-5, Kimi diff r2): the combo is
    /// BUILT from this array, so a test asserting its tag set equals <see cref="AllKnownAspects"/>
    /// genuinely proves the offer set and the renderable set agree. The dangerous direction is a
    /// tag in <c>AllKnownAspects</c> with no row here — the capability path would report it
    /// offerable, <see cref="NormalizeForModel"/> would accept it onto the wire and into History,
    /// and no UI could re-display it.</para>
    ///
    /// <para>Indicator sizes roughly track the real ratio so the user can scan visually, capped at
    /// 24×24 (the indicatorSlot width); extreme aspects shrink the short edge to keep the long edge
    /// ≤ 24. The leading "Auto" row carries a NULL tag by design — it is the omit-the-field choice,
    /// not an aspect — and is excluded from the tag comparison.</para>
    /// </summary>
    public static readonly (string Label, string? Tag, double Width, double Height)[] AspectComboRows =
    {
        ("Auto",  null,      14, 14),
        ("1:1",   Aspect1x1, 14, 14),
        ("5:4",   Aspect5x4, 15, 12),
        ("4:3",   Aspect4x3, 16, 12),
        ("3:2",   Aspect3x2, 18, 12),
        ("16:9",  Aspect16x9, 20, 11),
        ("2:1",   Aspect2x1, 22, 11),  // OpenAI-direct HasFlexibleSize only (IMG-2; 2.5 since IMG-13)
        ("21:9",  Aspect21x9, 23, 10),
        ("4:5",   Aspect4x5, 12, 15),
        ("3:4",   Aspect3x4, 12, 16),
        ("2:3",   Aspect2x3, 12, 18),
        ("9:16",  Aspect9x16, 11, 20),
        ("1:2",   Aspect1x2, 11, 22),  // OpenAI-direct HasFlexibleSize only (IMG-2; 2.5 since IMG-13)
        ("9:21",  Aspect9x21, 10, 23), // OpenAI-direct HasFlexibleSize only (IMG-2; 2.5 since IMG-13)
        // Extreme ratios (gemini-3.1 flash-image family only). Capped at 24 long edge.
        ("4:1",   Aspect4x1, 24,  6),  // 24/6 = 4 ✓
        ("8:1",   Aspect8x1, 24,  3),  // 24/3 = 8 ✓
        ("1:4",   Aspect1x4,  6, 24),
        ("1:8",   Aspect1x8,  3, 24),
    };

    // ─── Size-tier tags (persisted on CustomPrompt.ImageSizeTier / TranscriptionRecord.ImageSizeTier) ───

    /// <summary>Gemini-only "small" tier; the API value is the literal <c>512</c> (no K suffix).</summary>
    public const string Tier512 = "512";
    public const string Tier1K  = "1K";
    public const string Tier2K  = "2K";
    public const string Tier4K  = "4K";

    public static readonly string[] AllTiers = { Tier512, Tier1K, Tier2K, Tier4K };

    /// <summary>
    /// The size combo's rows — label, tag, indicator box size — pure data here for the same reason
    /// as <see cref="AspectComboRows"/> and <see cref="QualityComboRows"/>: a unit test pins the tag
    /// set against <see cref="AllTiers"/>. IMG-15 (owner, 2026-09-13): labels are the providers' OWN
    /// tokens — Gemini's <c>imageSize</c> and OpenRouter's <c>resolution</c> both speak
    /// <c>512</c>/<c>1K</c>/<c>2K</c>/<c>4K</c>, so the label IS the tag (the small tier read
    /// "0.5K" before, a name no provider uses). OpenAI has no size vocabulary at all (arbitrary
    /// W×H), so its rows keep the same tokens and the synthesizer turns them into pixels.
    /// </summary>
    public static readonly (string Label, string? Tag, double Width, double Height)[] TierComboRows =
    {
        ("Auto", null,    10, 10),
        (Tier512, Tier512, 10, 10),
        (Tier1K,  Tier1K,  12, 12),
        (Tier2K,  Tier2K,  16, 16),
        (Tier4K,  Tier4K,  20, 20),
    };

    // ─── Quality tags (persisted on CustomPrompt.ImageQuality / TranscriptionRecord.ImageQuality) ───
    // The wire parameter is `quality` (low/medium/high, plus xhigh/max on gpt-image-2.5 — IMG-14).
    // Gemini has no separate detail knob — its imageSize covers what would be both Tier and
    // Quality elsewhere.

    public const string QualityStandard = "standard";
    public const string QualityEnhanced = "enhanced";
    /// <summary>
    /// The PERSISTED tag is <c>"maximum"</c> and it means the wire value <c>high</c> — it was the
    /// top of the ladder until IMG-14 (2026-09-13) added the two gpt-image-2.5 tiers above it.
    /// The tag name is frozen (it lives in every saved prompt and History row), so the LABEL moved
    /// instead: the combo and History call this tier "High" now — OpenAI's own word for the value it
    /// sends — and "Max" is <see cref="QualityMax"/>. Never re-point this tag at a dearer wire value
    /// — a saved "maximum" must keep costing what it cost when the user picked it.
    /// </summary>
    public const string QualityMaximum  = "maximum";
    /// <summary>IMG-14: OpenAI's <c>xhigh</c> — gpt-image-2.5 only. Persisted as the wire value.</summary>
    public const string QualityExtraHigh = "xhigh";
    /// <summary>IMG-14: OpenAI's <c>max</c> — gpt-image-2.5 only. Persisted as the wire value.</summary>
    public const string QualityMax = "max";

    /// <summary>
    /// Every quality tag the app knows, ASCENDING (standard &lt; enhanced &lt; maximum &lt; xhigh
    /// &lt; max).
    ///
    /// <para><b>The order is load-bearing</b>, not cosmetic: <see cref="ClampQuality"/> walks it
    /// downwards to find the best value a model will actually accept. It is also the vocabulary the
    /// published catalog values are intersected against, so a tier the app has no tag and no
    /// persistence story for can never reach a consumer — the same intersection
    /// <see cref="AllKnownAspects"/> and <see cref="AllTiers"/> already apply to their fields.</para>
    ///
    /// <para>Since IMG-14 the combo's rows live here too (<see cref="QualityComboRows"/>), so
    /// <c>ImageOptionsTests</c> pins this list against them the way the aspect list is pinned —
    /// the 2026-08-13 self-review's warning that a tag without a row would be "offerable,
    /// clampable, sendable and unrenderable" is now a failing test rather than a comment.</para>
    /// </summary>
    public static readonly string[] AllQualities =
        { QualityStandard, QualityEnhanced, QualityMaximum, QualityExtraHigh, QualityMax };

    /// <summary>
    /// The three tiers every gpt-image model before 2.5 takes (<c>low</c>/<c>medium</c>/<c>high</c>)
    /// — the static answer for OpenAI-direct ids that are not gpt-image-2.5, and for the two OpenAI
    /// families on OpenRouter when no catalog snapshot exists. Offering <see cref="QualityExtraHigh"/>
    /// or <see cref="QualityMax"/> to gpt-image-2 earns an HTTP 400, the IMG-12 class.
    /// </summary>
    public static readonly string[] ClassicQualities = { QualityStandard, QualityEnhanced, QualityMaximum };

    /// <summary>
    /// The quality combo's rows — label, tag, indicator box size — pure data, here rather than in
    /// <c>AppTheme</c> for the same reason as <see cref="AspectComboRows"/>: so a unit test can pin
    /// the tag set against <see cref="AllQualities"/>. IMG-15 (owner, 2026-09-13): the labels are
    /// the providers' OWN words for the wire values — <c>low</c>/<c>medium</c>/<c>high</c>/
    /// <c>xhigh</c>/<c>max</c> is what OpenAI's `quality` takes and what OpenRouter's catalog
    /// publishes, so "Low"/"Medium"/"High"/"XHigh"/"Max" reads the same in the app and in the
    /// providers' docs ("Standard"/"Enhanced"/"Extra high" were app inventions). The persisted TAGS
    /// are untouched (<see cref="QualityMaximum"/> still renders as "High" — see its doc). The
    /// leading "Auto" row carries a NULL tag by design and is excluded from the tag comparison.
    /// </summary>
    public static readonly (string Label, string? Tag, double Width, double Height)[] QualityComboRows =
    {
        ("Auto",   null,             10, 10),
        ("Low",    QualityStandard,  10, 10),
        ("Medium", QualityEnhanced,  13, 13),
        ("High",   QualityMaximum,   16, 16),
        ("XHigh",  QualityExtraHigh, 19, 19),
        ("Max",    QualityMax,       22, 22),
    };

    /// <summary>
    /// The label a quality tag renders under — the combo's row label, so History and the dropdown
    /// can never disagree about what a tier is called. Null/blank ⇒ "Auto"; an unrecognized
    /// persisted tag falls back to a title-cased copy of itself (the pre-IMG-14 History rule) rather
    /// than throwing or hiding the row.
    /// </summary>
    public static string QualityLabelFor(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "Auto";
        foreach (var row in QualityComboRows)
            if (row.Tag != null && row.Tag.Equals(tag, System.StringComparison.Ordinal))
                return row.Label;
        return char.ToUpperInvariant(tag![0]) + tag[1..];
    }

    /// <summary>
    /// Synthesise the <c>size</c> parameter for OpenAI's flexible-size models — <c>gpt-image-2</c>
    /// and the two <c>gpt-image-2.5</c> ids (<see cref="HasFlexibleSize"/>; the API reference names
    /// all of them in the ONE sentence that documents arbitrary <c>WIDTHxHEIGHT</c>, with identical
    /// constraints — verified 2026-09-13). Returns null when:
    /// <list type="bullet">
    ///   <item>Both inputs are null/empty (caller should send no <c>size</c> field).</item>
    ///   <item>The aspect is one of the extreme ratios (<c>1:4</c>, <c>4:1</c>, <c>1:8</c>, <c>8:1</c>) —
    ///         these violate the 3:1 ratio cap and are unrenderable.</item>
    /// </list>
    /// All returned W×H values satisfy: both edges multiple of 16, max edge ≤ 3840, long-to-short
    /// ratio ≤ 3:1, total pixels in [655_360, 8_294_400], **exact** aspect ratio.
    /// (The cookbook says "less than 3840"; the documented pixel ceiling = 3840×2160 exactly,
    /// which only makes sense if the cap is inclusive — interpreting as ≤ 3840.)
    /// </summary>
    public static string? SynthesizeGptImage2Size(string? aspect, string? sizeTier)
    {
        // Default a missing dimension so a single user choice still produces something sensible:
        //   • aspect missing → 1:1
        //   • tier missing → 1K
        if (string.IsNullOrEmpty(aspect) && string.IsNullOrEmpty(sizeTier))
            return null;

        var a = string.IsNullOrEmpty(aspect)   ? Aspect1x1 : aspect;
        var t = string.IsNullOrEmpty(sizeTier) ? Tier1K   : sizeTier;

        // Every entry is exact-ratio (cross-multiplied integer arithmetic, not float compare).
        return (a, t) switch
        {
            (Aspect1x1,   Tier1K) => "1024x1024", // 1.05 MP
            (Aspect1x1,   Tier2K) => "2048x2048", // 4.19 MP
            (Aspect1x1,   Tier4K) => "2880x2880", // 8.29 MP — exactly at ceiling

            (Aspect4x3,   Tier1K) => "1280x960",  // 1.23 MP, exact 4:3
            (Aspect4x3,   Tier2K) => "2048x1536", // 3.15 MP, exact 4:3 (QXGA)
            (Aspect4x3,   Tier4K) => "3264x2448", // 7.99 MP, exact 4:3
            (Aspect3x4,   Tier1K) => "960x1280",
            (Aspect3x4,   Tier2K) => "1536x2048",
            (Aspect3x4,   Tier4K) => "2448x3264",

            (Aspect3x2,   Tier1K) => "1248x832",  // 1.04 MP, exact 3:2
            (Aspect3x2,   Tier2K) => "2400x1600", // 3.84 MP, exact 3:2
            (Aspect3x2,   Tier4K) => "3504x2336", // 8.19 MP, exact 3:2
            (Aspect2x3,   Tier1K) => "832x1248",
            (Aspect2x3,   Tier2K) => "1600x2400",
            (Aspect2x3,   Tier4K) => "2336x3504",

            (Aspect16x9,  Tier1K) => "1280x720",  // 0.92 MP, exact 16:9 (HD)
            (Aspect16x9,  Tier2K) => "2560x1440", // 3.69 MP, exact 16:9 (QHD)
            (Aspect16x9,  Tier4K) => "3840x2160", // 8.29 MP, canonical 4K UHD — at edge cap AND pixel cap
            (Aspect9x16,  Tier1K) => "720x1280",
            (Aspect9x16,  Tier2K) => "1440x2560",
            (Aspect9x16,  Tier4K) => "2160x3840",

            (Aspect5x4,   Tier1K) => "1200x960",  // 1.15 MP, exact 5:4
            (Aspect5x4,   Tier2K) => "2400x1920", // 4.61 MP, exact 5:4
            (Aspect5x4,   Tier4K) => "3200x2560", // 8.19 MP, exact 5:4
            (Aspect4x5,   Tier1K) => "960x1200",
            (Aspect4x5,   Tier2K) => "1920x2400",
            (Aspect4x5,   Tier4K) => "2560x3200",

            (Aspect21x9,  Tier1K) => "1456x624",  // 0.91 MP, exact 21:9
            (Aspect21x9,  Tier2K) => "3024x1296", // 3.92 MP, exact 21:9
            (Aspect21x9,  Tier4K) => "3808x1632", // 6.21 MP, exact 21:9 (max-edge cap forces sub-8MP)

            (Aspect2x1,   Tier1K) => "1440x720",  // 1.04 MP, exact 2:1 (IMG-2)
            (Aspect2x1,   Tier2K) => "2720x1360", // 3.70 MP, exact 2:1
            (Aspect2x1,   Tier4K) => "3840x1920", // 7.37 MP, exact 2:1 — at edge cap
            (Aspect1x2,   Tier1K) => "720x1440",
            (Aspect1x2,   Tier2K) => "1360x2720",
            (Aspect1x2,   Tier4K) => "1920x3840",

            (Aspect9x21,  Tier1K) => "624x1456",  // 21:9 rows mirrored (IMG-2)
            (Aspect9x21,  Tier2K) => "1296x3024",
            (Aspect9x21,  Tier4K) => "1632x3808",

            // Extreme ratios + tier 512 fall through to null — no flexible-size model supports them.
            _ => null,
        };
    }

    /// <summary>
    /// gpt-image-1 / 1-mini / 1.5 only accept three enumerated sizes. Aspect picks the bucket;
    /// tier is ignored (these models effectively render at ~1K only). Returns null for "Auto".
    /// </summary>
    public static string? SynthesizeGptImage1xSize(string? aspect)
    {
        if (string.IsNullOrEmpty(aspect)) return null;
        // Every landscape aspect collapses to 1536×1024; every portrait to 1024×1536; square to 1024².
        // The IMG-2 ratios (2:1/1:2/9:21) are never OFFERED on 1x models, but a persisted tag
        // must still bucket sanely if it reaches here.
        return aspect switch
        {
            Aspect1x1
                => "1024x1024",
            Aspect4x3 or Aspect3x2 or Aspect16x9 or Aspect5x4 or Aspect21x9 or Aspect4x1 or Aspect8x1 or Aspect2x1
                => "1536x1024",
            Aspect3x4 or Aspect2x3 or Aspect9x16 or Aspect4x5 or Aspect1x4 or Aspect1x8 or Aspect1x2 or Aspect9x21
                => "1024x1536",
            _   => "1024x1024",
        };
    }

    /// <summary>
    /// ENH-6: whether an OpenAI image model accepts the <c>input_fidelity</c> parameter
    /// on <c>/images/edits</c>. Per the API reference (verified 2026-07-11): supported by
    /// <c>gpt-image-1</c> and <c>gpt-image-1.5</c> only; explicitly NOT
    /// <c>gpt-image-1-mini</c>; gpt-image-2 always processes at high fidelity and
    /// REJECTS the parameter. Equality-first matching — a broad
    /// <c>StartsWith("gpt-image-1")</c> would wrongly include the mini (plan review).
    /// Dated snapshots (<c>gpt-image-1.5-2026-…</c>) match via their family prefix.
    /// </summary>
    public static bool SupportsInputFidelity(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        if (model.Equals("gpt-image-1", StringComparison.OrdinalIgnoreCase)) return true;
        if (model.Equals("gpt-image-1.5", StringComparison.OrdinalIgnoreCase)) return true;
        if (model.StartsWith("gpt-image-1.5-", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Maps the app's quality-tier tag to the wire <c>quality</c> value. Named for gpt-image, whose
    /// vocabulary it is, but OpenRouter's unified Image API publishes the same
    /// <c>low</c>/<c>medium</c>/<c>high</c> enum, so every route uses this one mapping; IMG-14
    /// adds gpt-image-2.5's <c>xhigh</c>/<c>max</c>. Returns null for "Auto" so the caller omits
    /// the field. The CLAMP, not this map, keeps the two top values off models that reject them —
    /// this map answers for any tag it knows.
    /// </summary>
    public static string? QualityToGptImageParam(string? quality)
    {
        if (string.IsNullOrEmpty(quality)) return null;
        return quality switch
        {
            QualityStandard  => "low",
            QualityEnhanced  => "medium",
            QualityMaximum   => "high",
            QualityExtraHigh => "xhigh",
            QualityMax       => "max",
            _ => null,
        };
    }

    /// <summary>
    /// The inverse of <see cref="QualityToGptImageParam"/> — a published catalog value back to the
    /// app's tag, or null for anything the app cannot offer (<c>auto</c>, which the app models as a
    /// null selection rather than a tag, and any value a provider invents that has no tag, no combo
    /// row and no persistence story here).
    ///
    /// <para>Case-insensitive, so a catalog casing change cannot silently drop a value — the same
    /// tolerance <c>ReadEnumValues</c> applies to aspects and tiers. The two directions are pinned
    /// as a ROUND TRIP by <c>ImageOptionsTests</c>, which is what keeps a future edit to one switch
    /// from drifting away from the other.</para>
    /// </summary>
    public static string? QualityTagForWireValue(string? wireValue)
    {
        if (string.IsNullOrEmpty(wireValue)) return null;
        if (wireValue.Equals("low", System.StringComparison.OrdinalIgnoreCase)) return QualityStandard;
        if (wireValue.Equals("medium", System.StringComparison.OrdinalIgnoreCase)) return QualityEnhanced;
        if (wireValue.Equals("high", System.StringComparison.OrdinalIgnoreCase)) return QualityMaximum;
        if (wireValue.Equals("xhigh", System.StringComparison.OrdinalIgnoreCase)) return QualityExtraHigh;
        if (wireValue.Equals("max", System.StringComparison.OrdinalIgnoreCase)) return QualityMax;
        return null;
    }

    // ─── Per-provider/model capability queries (used by the UI to filter dropdowns) ───────────

    /// <summary>
    /// IMG-5: aspects for a model whose PUBLISHED capabilities are known — the form production
    /// uses. A non-null <paramref name="capabilities"/> with a non-null <c>Aspects</c> answers from
    /// the catalog (already intersected with <see cref="AllKnownAspects"/> at parse time, so every
    /// value is offerable). No capabilities, or an UNKNOWN <c>Aspects</c> (drifted shape), falls
    /// through to the static overload — authoritative for OpenAI-direct and Gemini-direct, which
    /// never appear in OpenRouter's catalog and whose tables are docs-verified (IMG-2).
    /// </summary>
    public static IReadOnlyList<string> SupportedAspectsFor(
        AIProvider provider, string? model, ImageModelCapabilities? capabilities)
        => capabilities?.Aspects ?? SupportedAspectsFor(provider, model);

    /// <summary>
    /// The STATIC rules — the fallback since IMG-5, and the whole answer for OpenAI/Gemini-direct.
    /// Baseline <see cref="CommonAspects"/>; adds <see cref="ExtremeAspects"/> only on the
    /// gemini-3.1 flash-image family and <see cref="GptImage2ExtraAspects"/> only on
    /// OPENAI-DIRECT flexible-size models (<see cref="HasFlexibleSize"/> — IMG-2, widened to
    /// gpt-image-2.5 by IMG-13); the OpenRouter gpt-image families get their
    /// catalog-exact sets and gpt-5*-image* gets none. <paramref name="model"/> may be
    /// null/empty when the user hasn't picked one yet — we then substitute the provider's
    /// documented default model so the UI reflects what the runtime will actually use.
    /// </summary>
    public static IReadOnlyList<string> SupportedAspectsFor(AIProvider provider, string? model)
    {
        if (string.IsNullOrEmpty(model)) model = DefaultModelFor(provider);
        // OpenRouter accepted alongside Gemini: a `google/...` slug routes the same
        // underlying model, and the classifiers strip the slug before matching.
        // IMG-5 Phase B: the ASPECT family includes flash-LITE (probe-proven 2026-08-02); the tier
        // rule below deliberately does not — see IsGemini31FlashAspectFamily.
        if ((provider == AIProvider.Gemini || provider == AIProvider.OpenRouter) && IsGemini31FlashAspectFamily(model))
            return Concat(CommonAspects, ExtremeAspects);
        if (provider == AIProvider.OpenAI && HasFlexibleSize(model))
            return Concat(CommonAspects, GptImage2ExtraAspects);
        // OpenRouter keeps the gpt-image-2-ONLY branch: its catalog-exact set was read for that
        // id (2026-07-31); a 2.5 slug there is answered by IMG-5's published capabilities when a
        // snapshot exists, and by the family fallback below otherwise — never by this table.
        if (provider == AIProvider.OpenRouter && IsGptImage2(model))
            return OpenRouterGptImage2Aspects;
        if (provider == AIProvider.OpenRouter && IsGptImageFamily(model))
            return OpenRouterGptImage1xAspects;
        // IMG-5 "D1": the gpt-5*-image* family publishes NO aspect_ratio (live catalog). Without
        // this it fell through to CommonAspects and offered ten ratios the model cannot take.
        if (provider == AIProvider.OpenRouter && IsGpt5ImageFamily(model))
            return System.Array.Empty<string>();
        return CommonAspects;
    }

    private static string[] Concat(string[] first, string[] second)
    {
        var combined = new string[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return combined;
    }

    /// <summary>
    /// IMG-5: tiers for a model whose PUBLISHED capabilities are known — the twin of
    /// <see cref="SupportedAspectsFor(AIProvider, string?, ImageModelCapabilities?)"/> and the form
    /// production uses. This is what makes krea (<c>1K</c> only), grok (<c>1K,2K</c>) and
    /// <c>riverflow-v2.5-fast</c> (<c>1K,2K</c> where its siblings take <c>4K</c>) correct without a
    /// per-model id rule. No capabilities, or an UNKNOWN <c>Tiers</c> (drifted shape), falls through
    /// to the static overload.
    /// </summary>
    public static IReadOnlyList<string> SupportedTiersFor(
        AIProvider provider, string? model, ImageModelCapabilities? capabilities)
        => capabilities?.Tiers ?? SupportedTiersFor(provider, model);

    /// <summary>
    /// The STATIC rules — the fallback since IMG-5, and the whole answer for OpenAI/Gemini-direct.
    /// Returns an empty list for models that don't accept any size tier (e.g. gpt-image-1.x and
    /// gemini-2.x flash-image variants) — the UI should then hide the Size dropdown entirely.
    /// When <paramref name="model"/> is null/empty, substitutes the provider's documented
    /// default model — so the answer follows the default rather than a hardcoded assumption about
    /// it. (That mattered literally while the Gemini default was <c>gemini-2.5-flash-image</c>, a
    /// no-tier model; since 2026-08-13 it is <c>gemini-3.1-flash-image</c>, which has the full
    /// 512/1K/2K/4K set — the substitution is what makes that change need no edit here.)
    /// IMG-2 (docs verified 2026-07-15): Gemini validates <c>imageSize</c> HARD (flash-lite
    /// rejected 2K with an API error), so unknown/lite Gemini models get the conservative
    /// `{1K}` floor — the docs state 1K is accepted by ALL models, and offering more to a
    /// model we don't know reproduces the flash-lite bug. Known capable families keep their
    /// full documented sets via the classifiers. OpenRouter ids the live catalog proves publish
    /// NO <c>resolution</c> (the whole gpt-image family, gpt-5*-image*, and flux) return the empty
    /// set; any other OpenRouter id keeps the `{1K,2K,4K}` catch-all — reached only when no catalog
    /// snapshot exists, since IMG-5 answers from published capabilities whenever one does.
    /// </summary>
    public static IReadOnlyList<string> SupportedTiersFor(AIProvider provider, string? model)
    {
        if (string.IsNullOrEmpty(model)) model = DefaultModelFor(provider);

        // OpenAI-family ids: on OPENAI the rule mirrors the SERIALIZER exactly (Codex diff
        // r2) — only the flexible-size models (HasFlexibleSize: gpt-image-2 and the two
        // gpt-image-2.5 ids, IMG-13) get W×H synthesis; every other family rides the
        // enumerated preset serializer that IGNORES tiers, so offering tiers there would
        // make the Effective record claim a size the request never carried. On OPENROUTER
        // the unified Image API publishes NO `resolution` for ANY gpt-image model — incl.
        // gpt-image-2, the app's OpenRouter default (live /api/v1/images/models,
        // 2026-07-31) — and the migrated route no longer synthesizes W×H, so the whole
        // family gets no tiers there.
        // IMG-5 "D1": gpt-5*-image* joins the family here — it publishes no `resolution` either,
        // and it is not a `gpt-image-*` id so IsGptImageFamily never matched it.
        var isOpenAiImageFamily = provider == AIProvider.OpenAI
            || (provider == AIProvider.OpenRouter && (IsGptImageFamily(model) || IsGpt5ImageFamily(model)));
        if (isOpenAiImageFamily)
            return provider == AIProvider.OpenAI && HasFlexibleSize(model)
                ? new[] { Tier1K, Tier2K, Tier4K }
                : System.Array.Empty<string>();

        // black-forest-labs flux family via OpenRouter publishes NO `resolution` either
        // (same source, same day) — aspect_ratio is its only geometry knob.
        if (provider == AIProvider.OpenRouter && IsFluxFamily(model))
            return System.Array.Empty<string>();

        var isGeminiFamily = provider == AIProvider.Gemini
            || (provider == AIProvider.OpenRouter && !string.IsNullOrEmpty(model)
                && Bare(model!).StartsWith("gemini-", System.StringComparison.OrdinalIgnoreCase));
        if (isGeminiFamily)
        {
            // Gemini-2.x flash-image silently ignores imageSize.
            if (IsLegacyGeminiFlashImage(model))
                return System.Array.Empty<string>();

            // gemini-3.1 flash-image adds a 0.5K (512px) tier: 512/1K/2K/4K.
            if (IsGemini31FlashImage(model))
                return new[] { Tier512, Tier1K, Tier2K, Tier4K };

            // gemini-3 pro-image: 1K/2K/4K.
            if (IsGeminiProImage(model))
                return new[] { Tier1K, Tier2K, Tier4K };

            // Everything else — gemini-3.1-flash-lite-image (docs: 1K ONLY, "2K and 4K are
            // unsupported") and any future/unknown Gemini image model — gets the floor.
            return new[] { Tier1K };
        }

        return new[] { Tier1K, Tier2K, Tier4K };
    }

    /// <summary>
    /// Provider's default image model, used when capability queries arrive with a null model
    /// (e.g. the user picked "Default" in the model dropdown). Mirrors each descriptor's
    /// <c>DefaultImageModel</c>; kept here as a tiny self-contained mirror to avoid pulling
    /// the descriptor registry into <see cref="ImageOptions"/>.
    /// </summary>
    /// <summary>
    /// The default image model for a provider, WITH the correct provider-slug prefix — OpenRouter
    /// needs <c>openai/gpt-image-2</c>, not the bare <c>gpt-image-2</c> the provider descriptor
    /// advertises. Returns null for providers that don't generate images. Public so the single
    /// source of truth is shared with the image-default resolution in AIEnhancementService.
    /// </summary>
    public static string? DefaultModelFor(AIProvider provider) => provider switch
    {
        AIProvider.OpenAI     => "gpt-image-2",
        // Nano Banana 2 (2026-08-13). Was gemini-2.5-flash-image — the 2025 model Google has since
        // superseded twice, still the default because nothing re-examined it. Google's own catalog
        // calls 3.1 Flash Image "state of the art … delivering Pro-level visual quality"; it also
        // gains the 512 tier the 2.5 model has no tier concept for at all. Nano Banana PRO
        // (gemini-3-pro-image) was the other candidate and was NOT taken: 2x this model's output
        // price for a default the user never explicitly chose. Keeping this current is now part of
        // the weekly /vw-model-review.
        AIProvider.Gemini     => "gemini-3.1-flash-image",
        AIProvider.OpenRouter => "openai/gpt-image-2",
        _ => null,
    };

    /// <summary>
    /// WHICH detail/quality tiers a model accepts — the twin of <see cref="SupportedTiersFor"/>,
    /// and the answer both the dropdown and the wire read.
    ///
    /// <para><b>Values, not a yes/no (IMG-12).</b> Until 2026-08-13 this was a boolean, so a model
    /// that published <c>quality</c> at all was offered all three tiers —
    /// <c>x-ai/grok-imagine-image-2.0</c> publishes <c>{low, medium}</c> and answered "Maximum" with
    /// <c>HTTP 400 … xAI: quality: not supported</c>. Knowing a parameter exists is not knowing what
    /// it takes; aspects and tiers have carried their published VALUES since IMG-5 and quality was
    /// the one field left behind.</para>
    ///
    /// <para><b>The provider-only form was a real defect (IMG-5 "D2").</b> It answered true for ALL
    /// of OpenRouter, so the app offered and SENT <c>quality</c> to krea, flux, recraft, seedream,
    /// riverflow, grok, mai and gemini-on-OpenRouter — the catalog publishes a <c>quality</c>
    /// parameter for a minority of models. The single-argument overload was removed rather than
    /// kept, so no caller can reach the old answer.</para>
    ///
    /// <para>Null/unknown <paramref name="capabilities"/> falls back to the static rule: OpenAI-direct
    /// (the classic low/medium/high on every gpt-image model, plus xhigh/max on the two gpt-image-2.5
    /// ids — IMG-14), and on OpenRouter the two OpenAI image families only, classic three. Gemini has
    /// no separate knob — <c>imageSize</c> covers both axes.</para>
    /// </summary>
    public static IReadOnlyList<string> SupportedQualitiesFor(
        AIProvider provider, string? model, ImageModelCapabilities? capabilities)
    {
        // Tri-state: a KNOWN answer wins (an EMPTY published list is authoritative "no quality
        // knob"); unknown (drifted shape) falls through to the static rule for this field alone, so
        // schema drift can never turn an unsupported quality into a sent one.
        if (capabilities?.Quality is { } published)
            return published;

        // IMG-14: only the two gpt-image-2.5 ids document xhigh/max (image guide + API reference,
        // 2026-09-13); every older gpt-image model stops at high and 400s above it. On OpenRouter
        // the catalog snapshot answers when one exists (its published values translate at parse
        // time, so a 2.5 slug there gets whatever OpenRouter publishes); the static fallback stays
        // the classic three for the whole family — no live read of OpenRouter's 2.5 rows was made.
        if (provider != AIProvider.OpenAI && provider != AIProvider.OpenRouter)
            return System.Array.Empty<string>();

        // A null model means "the provider's default" on BOTH providers (self-review: the OpenAI
        // branch used to classify null directly, which only stayed correct while the OpenAI default
        // was not a 2.5 id).
        if (string.IsNullOrEmpty(model)) model = DefaultModelFor(provider);
        if (provider == AIProvider.OpenAI)
            return IsGptImage25(model) ? AllQualities : ClassicQualities;
        return IsGptImageFamily(model) || IsGpt5ImageFamily(model)
            ? ClassicQualities
            : System.Array.Empty<string>();
    }

    // A boolean `SupportsQuality(provider, model, capabilities)` used to live here and was DELETED
    // by IMG-12 (self-review, 2026-08-13) once nothing in src/ called it: row visibility comes from
    // ImageOptionGating.ShowQuality, which derives from the value list, and the wire comes from
    // SupportedQualitiesFor directly. Keeping a public yes/no predicate for a field whose whole
    // defect was being a yes/no is an invitation to re-introduce it — the same reasoning IMG-5 used
    // when it deleted the provider-only overload rather than leaving it reachable.

    /// <summary>
    /// The best supported quality at or below <paramref name="requested"/>: the request itself when
    /// the model accepts it, else the nearest supported tier BELOW it, else null (omit ⇒ Auto).
    ///
    /// <para><b>Deliberately missing <see cref="ClampTier"/>'s "else the LOWEST supported" tail</b>,
    /// and the asymmetry is the point: quality tiers are BILLED per tier (grok-imagine-image-2.0's
    /// own price table ran low_1k $0.04 → medium_2k $0.08 on 2026-08-13 — the figures move, the
    /// ordering does not), so this never NAMES a tier above the one the user asked for. With nothing
    /// below, it omits the field instead.</para>
    ///
    /// <para><b>That is not a cost CEILING, and the doc claimed one until Codex diff r1.</b> Omitting
    /// the field means the provider applies its own default, which may well be dearer than the
    /// Standard the user picked — we simply stop choosing. A real ceiling would have to REFUSE the
    /// generation, which is not this function's job and is not what the dialog promises. The
    /// guarantee is narrow and worth stating exactly: VoiceWink never substitutes a costlier tier of
    /// its own accord.</para>
    ///
    /// <para>An unrecognized tag returns null — never guess a billing tier from garbage (imported or
    /// corrupt prompt JSON can carry anything). <b>The match is ORDINAL</b>, so an imported
    /// <c>"Maximum"</c> clamps to Auto rather than to Maximum: unchanged from the pre-IMG-12
    /// constant pattern match, and fail-closed. Worth naming only because the CATALOG side of the
    /// same field re-validates case-INsensitively — our own persisted tags are always written in
    /// canonical form, while a provider's spelling is not ours to police.</para>
    /// </summary>
    internal static string? ClampQuality(string? requested, IReadOnlyList<string> supported)
    {
        if (string.IsNullOrEmpty(requested) || supported.Count == 0) return null;
        var requestedIndex = System.Array.IndexOf(AllQualities, requested);
        if (requestedIndex < 0) return null;
        for (var i = requestedIndex; i >= 0; i--)
            if (supported.Contains(AllQualities[i])) return AllQualities[i];
        return null;
    }

    /// <summary>
    /// The <c>gpt-5*-image*</c> family (<c>gpt-5-image</c>, <c>gpt-5-image-mini</c>,
    /// <c>gpt-5.4-image-2</c>), bare or slugged.
    ///
    /// <para>IMG-5 "D1": these do NOT match <see cref="IsGptImageFamily"/> (they are not
    /// <c>gpt-image-*</c>), so before this classifier they fell through every OpenRouter branch to
    /// the permissive catch-all — the dialog offered ten aspects and three size tiers to three
    /// models whose catalog rows publish NEITHER <c>aspect_ratio</c> NOR <c>resolution</c>, and the
    /// unified route serialized those values onto the wire. Delimiter-aware on both halves so a
    /// future <c>gpt-50-image</c> or <c>gpt-5-imagery</c> cannot inherit the rule.</para>
    /// </summary>
    internal static bool IsGpt5ImageFamily(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var segments = Bare(model!).Split('-');
        if (segments.Length < 3) return false;
        if (!segments[0].Equals("gpt", System.StringComparison.OrdinalIgnoreCase)) return false;

        // The VERSION segment must be 5 or 5.x — compared as a whole segment, so `gpt-50-image`
        // (a different family) cannot inherit the rule the way a `StartsWith("gpt-5")` test would
        // have let it.
        var version = segments[1];
        if (!version.Equals("5", System.StringComparison.Ordinal)
            && !version.StartsWith("5.", System.StringComparison.Ordinal))
        {
            return false;
        }

        // ...and some later segment must be exactly "image": "gpt-5.4-image-2" splits to
        // [gpt, 5.4, image, 2] ✓, "gpt-5-imagery" to [gpt, 5, imagery] ✗.
        for (var i = 2; i < segments.Length; i++)
            if (segments[i].Equals("image", System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// True for OpenAI's gpt-image-2, matching both the bare id and an OpenRouter slug
    /// (<c>openai/gpt-image-2</c>) via <see cref="Bare"/> so
    /// size-tier synthesis routes correctly regardless of the provider-slug prefix.
    /// Delimiter-aware (IMG-2, Codex diff r1): exact alias or the <c>gpt-image-2-</c>
    /// snapshot prefix — <c>gpt-image-20</c> and the <c>gpt-image-2.5</c> ids never
    /// match HERE. The 2.5 ids DO share the synthesis table and the exclusive ratios,
    /// but by their own docs-verified rule (<see cref="IsGptImage25"/>), not by inheriting
    /// this one; OpenRouter's catalog-exact aspect set stays keyed on this predicate alone.
    /// </summary>
    public static bool IsGptImage2(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var bare = Bare(model!);
        return bare.Equals("gpt-image-2", System.StringComparison.OrdinalIgnoreCase)
            || bare.StartsWith("gpt-image-2-", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IMG-13 (2026-09-13): the two <c>gpt-image-2.5</c> ids OpenAI publishes —
    /// <c>gpt-image-2.5-sunburst</c> and <c>gpt-image-2.5-flare</c> — bare or slugged, exact or
    /// with their dated-snapshot suffix (<c>gpt-image-2.5-flare-2026-09-08</c>). The API reference
    /// lists both beside gpt-image-2 in the one sentence that grants arbitrary
    /// <c>WIDTHxHEIGHT</c> sizes, so they take the same synthesis table and the same exclusive
    /// ratios. NAMED ids plus a dated snapshot, not a <c>gpt-image-2.5-</c> prefix, on purpose: a future
    /// <c>gpt-image-2.5-mini</c> could ship with gpt-image-1-mini's enumerated sizes, and a
    /// prefix would offer it 2K/4K that the request then carries as a rejected W×H — where an
    /// unmatched id rides the preset serializer and simply works at 1K, today's behaviour.
    /// </summary>
    public static bool IsGptImage25(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var bare = Bare(model!);
        return MatchesIdOrDatedSnapshot(bare, "gpt-image-2.5-sunburst")
            || MatchesIdOrDatedSnapshot(bare, "gpt-image-2.5-flare");
    }

    /// <summary>
    /// The ONE predicate behind "this OpenAI model takes an arbitrary <c>WIDTHxHEIGHT</c>":
    /// the serializer (generations AND edits), the OpenAI-direct tier rule and the OpenAI-direct
    /// aspect rule all ask it, so the three cannot disagree about which ids synthesize.
    /// </summary>
    public static bool HasFlexibleSize(string? model) => IsGptImage2(model) || IsGptImage25(model);

    // Exact id, or that id followed by a DATED snapshot suffix in OpenAI's published shape
    // (`-yyyy-MM-dd`, e.g. gpt-image-2.5-flare-2026-09-08). Deliberately narrower than
    // IsGptImage2's bare "-" delimiter (self-review): a "-" alone would also admit a hypothetical
    // "gpt-image-2.5-flare-mini", which is exactly the enumerated-size sibling the named-id rule
    // exists to keep on the preset. Stricter-than-needed fails toward today's behaviour.
    private static bool MatchesIdOrDatedSnapshot(string bare, string id)
    {
        if (bare.Equals(id, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (!bare.StartsWith(id + "-", System.StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = bare.AsSpan(id.Length + 1);
        return suffix.Length == 10
            && System.DateOnly.TryParseExact(suffix, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>
    /// The whole <c>gpt-image-</c> family (1, 1-mini, 1.5, 2, any future member), bare or
    /// slugged. Used by the OpenRouter capability rules — the unified Image API publishes
    /// no <c>resolution</c> for any member (live catalog 2026-07-31).
    /// </summary>
    // internal (was private) so ReferenceImagePolicy can read the same classification for OpenAI's
    // documented 16-image edits limit — one family rule, not a second copy that can drift.
    internal static bool IsGptImageFamily(string? model) =>
        !string.IsNullOrEmpty(model)
        && Bare(model!).StartsWith("gpt-image-", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Black Forest Labs' flux family — delimiter-exact FIRST segment of the bare id
    /// (<c>flux.2-pro</c>, <c>flux.2-klein-4b</c>, a future <c>flux-3</c>); a hypothetical
    /// <c>fluxion-x</c> never matches.
    /// </summary>
    private static bool IsFluxFamily(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var segments = Bare(model!).Split('-', '.');
        return segments.Length > 0 && segments[0].Equals("flux", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Public so <see cref="VoiceWink.Services.AIEnhancement.Clients.GeminiImageClient"/> can share the
    /// classifier. True for <c>gemini-2.x-flash-image*</c> models — the API silently ignores
    /// <c>imageConfig.imageSize</c> on these, so the client strips it and the UI hides the tier dropdown.
    /// </summary>
    public static bool IsLegacyGeminiFlashImage(string? model) =>
        !string.IsNullOrEmpty(model)
        && Bare(model!).StartsWith("gemini-2.", System.StringComparison.OrdinalIgnoreCase)
        && model!.Contains("flash-image", System.StringComparison.OrdinalIgnoreCase);

    // Delimiter-aware prefixes (IMG-2, Codex round 2): "gemini-3.1-" not "gemini-3.1" so a
    // future gemini-3.10-* can't misclassify; matches the GA id (docs dropped "-preview"
    // 2026-07-15) AND the old preview id via the contains-based family segment.
    private static bool IsGemini31FlashImage(string? model) =>
        !string.IsNullOrEmpty(model)
        && Bare(model!).StartsWith("gemini-3.1-", System.StringComparison.OrdinalIgnoreCase)
        && model!.Contains("flash-image", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// IMG-5 Phase B: the gemini-3.1 flash family for the ASPECT decision ONLY — flash-image AND
    /// flash-<b>lite</b>-image.
    ///
    /// <para><b>Why this is separate from <see cref="IsGemini31FlashImage"/> rather than a widening
    /// of it.</b> That classifier gates BOTH aspects and tiers, and flash-lite needs opposite
    /// answers: it DOES accept the extreme ratios but does NOT accept the 512 tier. Owner-run live
    /// probes, 2026-08-02, settled both — <c>aspectRatio:"4:1"</c> returned 200 (on Gemini-direct
    /// AND through OpenRouter), while <c>imageSize:"512"</c> returned <b>400</b>. Widening the
    /// shared classifier would have fixed the aspects and simultaneously offered a tier the model
    /// rejects.</para>
    ///
    /// <para>Note the id shapes: <c>gemini-3.1-flash-image</c> contains "flash-image", but
    /// <c>gemini-3.1-flash-lite-image</c> does NOT (it is "flash-lite-image") — which is exactly
    /// why flash-lite silently missed the extreme ratios and was under-offered.</para>
    ///
    /// <para><b>Evidence note:</b> Google's published aspect list for flash-lite shows only the ten
    /// common ratios and is INCOMPLETE — the probe contradicts it. OpenRouter's catalog was right.
    /// Do not "correct" this back to the docs.</para>
    /// </summary>
    private static bool IsGemini31FlashAspectFamily(string? model) =>
        !string.IsNullOrEmpty(model)
        && Bare(model!).StartsWith("gemini-3.1-", System.StringComparison.OrdinalIgnoreCase)
        && (model!.Contains("flash-image", System.StringComparison.OrdinalIgnoreCase)
            || model!.Contains("flash-lite-image", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// IMG-2: gemini-3-family pro image models (<c>gemini-3-pro-image</c> + dated snapshots +
    /// a future 3.x pro) — the 1K/2K/4K tier class per the docs. Delimiter-aware so
    /// <c>gemini-30-*</c> never matches.
    ///
    /// <para>IMG-9 (owner UAT 2026-08-03) added the <c>nano-banana-pro</c> alias — see
    /// <see cref="IsNanoBananaPro"/> for why that id needed its own branch rather than a
    /// widening of the version-prefix test above.</para>
    /// </summary>
    private static bool IsGeminiProImage(string? model) =>
        IsVersionedGeminiProImage(model) || IsNanoBananaPro(model);

    /// <summary>The original IMG-2 rule, unchanged, split out so the alias below could join it
    /// without an inline mix of <c>&amp;&amp;</c> and <c>||</c> whose precedence has to be
    /// re-derived by every future reader.</summary>
    private static bool IsVersionedGeminiProImage(string? model) =>
        !string.IsNullOrEmpty(model)
        && (Bare(model!).StartsWith("gemini-3-", System.StringComparison.OrdinalIgnoreCase)
            || Bare(model!).StartsWith("gemini-3.", System.StringComparison.OrdinalIgnoreCase))
        && model!.Contains("pro-image", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// IMG-9: Google publishes Gemini 3 Pro Image on the Gemini API under its MARKETING name as
    /// well — <c>nano-banana-pro-preview</c> — and that id shares no token with the version-prefix
    /// rule above (no "gemini-3", no "pro-image"). It therefore matched no classifier and fell to
    /// the unknown-Gemini floor in <see cref="SupportedTiersFor"/>, offering 1K where the identical
    /// model under its <c>gemini-3-pro-image</c> id offers 1K/2K/4K. Same defect class as the
    /// flash-lite bug IMG-2 was created to fix: an id nobody taught the classifier about.
    ///
    /// <para><b>Evidence (live catalogs, 2026-08-03), because the marketing name alone would not
    /// have been enough to act on.</b> The Gemini API lists exactly ONE nano-banana id,
    /// <c>nano-banana-pro-preview</c>; alongside it OpenRouter's image catalog spells the whole
    /// family out in its display names — "Nano Banana Pro (Gemini 3 Pro Image)", "Nano Banana 2
    /// (Gemini 3.1 Flash Image)", "Nano Banana 2 Lite (Gemini 3.1 Flash Lite Image)" and "Nano
    /// Banana (Gemini 2.5 Flash Image)". So the family maps onto FOUR different tier classes, and
    /// a single blanket <c>nano-banana*</c> rule would be wrong for three of them.</para>
    ///
    /// <para><b>Only <c>-pro</c> is claimed here, deliberately.</b> It is the one nano-banana id
    /// the Gemini API actually publishes, so it is the only one whose mapping is both evidenced and
    /// reachable. A future <c>nano-banana-2</c> on Gemini-direct keeps the conservative floor until
    /// someone confirms it exists — a wrong guess is worse than the floor, because Gemini VALIDATES
    /// <c>imageSize</c> hard and an over-offered tier becomes a 400 in the user's face.</para>
    ///
    /// <para>Match is delimiter-aware in the same style as the rules above (exact, or a
    /// <c>-</c>-delimited prefix) so a hypothetical <c>nano-banana-professional</c> cannot inherit
    /// Pro's tiers. OpenRouter routes these under <c>google/gemini-3-pro-image</c> slugs and gets
    /// live capabilities from its catalog, so this alias is a Gemini-DIRECT concern —
    /// <see cref="Bare"/> still runs first, keeping a slugged or <c>:nitro</c>-suffixed form
    /// working if one ever appears.</para>
    ///
    /// <para>Aspects deliberately need no companion branch: Gemini 3 Pro Image uses the
    /// <see cref="CommonAspects"/> baseline, which is already where an unmatched Gemini id lands.
    /// Only the TIER classifier was wrong.</para>
    /// </summary>
    private static bool IsNanoBananaPro(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        var bare = Bare(model!);
        return bare.Equals("nano-banana-pro", System.StringComparison.OrdinalIgnoreCase)
            || bare.StartsWith("nano-banana-pro-", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IMG-2: normalize a generation's requested options against the resolved model's
    /// capabilities. THE single normalization site — applied in both
    /// <c>AIEnhancementService.GenerateImage*</c> paths BEFORE the request log line (so the
    /// log, the provider request, <c>ImageGenerationResult.Effective</c>, the History row,
    /// and the redo re-arm all tell one story) and defensively in
    /// <c>GeminiImageClient</c> (direct-client callers; subsumes the old legacy-2.x
    /// imageSize strip via the empty supported-tier set). Rules:
    /// <list type="bullet">
    /// <item>Unsupported ASPECT → null/Auto (no "nearest aspect" exists).</item>
    /// <item>Unsupported TIER → nearest supported tier BELOW the request, else the lowest
    /// supported (flash-lite 2K → explicit <c>1K</c> — Gemini REJECTS unsupported values,
    /// and an omitted tier would render History's label as Auto while the user asked 2K);
    /// an EMPTY supported set (gemini-2.x, gpt-image-1x) or an unrecognized tag omits.</item>
    /// <item>Unsupported QUALITY → nearest supported tier BELOW the request (grok-imagine-image-2.0
    /// publishes <c>{low, medium}</c>, so a saved "maximum" now sends <c>medium</c> instead of
    /// earning a 400), else omitted. Unlike the tier rule there is NO "else the lowest supported"
    /// tail — see <see cref="ClampQuality"/>. No quality knob at all (Gemini) or an unrecognized tag
    /// → omitted.</item>
    /// </list>
    /// The shared <c>CustomPrompt</c> is NEVER mutated — callers persist the returned record.
    /// </summary>
    /// <param name="capabilities">
    /// IMG-5: the model's PUBLISHED capabilities, or null to use the static rules. Both generation
    /// paths pass the live value, which is what makes this the wire oracle rather than a
    /// dropdown-only fix: every option that reaches a request WITHOUT passing the dialog's
    /// offer-filter — a History redo/Iterate re-arm carrying <c>Previous*</c> tags, a persisted
    /// prompt's saved tags after the per-provider model changed — is normalized against the same
    /// evidence the dialog used. Without it a saved <c>2K</c> on krea, <c>21:9</c> on grok, or any
    /// saved aspect on <c>gpt-5-image</c> would still be sent (both 2026-08-01 final reviews).
    /// </param>
    public static EffectiveImageOptions NormalizeForModel(
        AIProvider provider, string? model, string? aspect, string? sizeTier, string? quality,
        ImageModelCapabilities? capabilities = null)
    {
        global::System.Collections.Generic.List<string>? adjustments = null;

        var effAspect = string.IsNullOrEmpty(aspect) ? null : aspect;
        if (effAspect != null && !SupportedAspectsFor(provider, model, capabilities).Contains(effAspect))
        {
            (adjustments ??= new()).Add($"aspect {SafeTag(effAspect, KnownAspectVocabulary)} -> auto");
            effAspect = null;
        }

        var effTier = string.IsNullOrEmpty(sizeTier) ? null : sizeTier;
        if (effTier != null)
        {
            var tiers = SupportedTiersFor(provider, model, capabilities);
            if (!tiers.Contains(effTier))
            {
                var clamped = ClampTier(effTier, tiers);
                (adjustments ??= new()).Add(clamped == null
                    ? $"imageSize {SafeTag(effTier, AllTiers)} -> omitted"
                    : $"imageSize {SafeTag(effTier, AllTiers)} -> {clamped}");
                effTier = clamped;
            }
        }

        var effQuality = string.IsNullOrEmpty(quality) ? null : quality;
        if (effQuality != null)
        {
            var clamped = ClampQuality(effQuality, SupportedQualitiesFor(provider, model, capabilities));
            if (clamped != effQuality)
            {
                (adjustments ??= new()).Add(clamped == null
                    ? $"quality {SafeTag(effQuality, AllQualities)} -> auto"
                    : $"quality {SafeTag(effQuality, AllQualities)} -> {clamped}");
                effQuality = clamped;
            }
        }

        // IMG-13 (Codex diff r1, Blocker): the OpenAI client synthesizes a concrete W×H whenever
        // EITHER geometry setting is present — SynthesizeGptImage2Size fills the missing one
        // (aspect → 1:1, tier → 1K). Materialize that paired default HERE so Effective, History
        // and the redo re-arm describe the request that actually went out: an aspect-only ask
        // used to record "Size: Auto" while the wire carried 1280x720. Both-null stays null —
        // no `size` is sent then, and Auto is the truthful record. Scoped to the flexible-size
        // ids on OpenAI-direct: the 1.x preset ignores the tier, and Gemini's Auto IS the
        // provider default. A re-armed redo sends the same bytes it always did (1:1 / 1K were
        // the synthesizer's defaults all along).
        if (provider == AIProvider.OpenAI && HasFlexibleSize(model)
            && (effAspect != null || effTier != null))
        {
            if (effAspect == null)
            {
                effAspect = Aspect1x1;
                (adjustments ??= new()).Add("aspect auto -> 1:1");
            }
            if (effTier == null)
            {
                effTier = Tier1K;
                (adjustments ??= new()).Add("imageSize auto -> 1K");
            }
        }

        return new EffectiveImageOptions(effAspect, effTier, effQuality,
            adjustments ?? (global::System.Collections.Generic.IReadOnlyList<string>)global::System.Array.Empty<string>());
    }

    // Adjustment fragments are LOGGED (Information → Sentry breadcrumbs): only tags from
    // the app's own vocabulary appear verbatim — an unrecognized value (imported/corrupt
    // prompt JSON could carry arbitrary path-like or sensitive text) logs as "<invalid>"
    // (Codex diff r1, privacy).
    private static string SafeTag(string value, global::System.Collections.Generic.IReadOnlyList<string> vocabulary)
        => vocabulary.Contains(value) ? value : "<invalid>";

    /// <summary>
    /// The same union as <see cref="AllKnownAspects"/> — kept as an alias so the two can never
    /// drift apart (they had identical hand-rolled concatenations before IMG-5).
    /// </summary>
    private static string[] KnownAspectVocabulary => AllKnownAspects;

    // Nearest supported tier BELOW the request, else the LOWEST supported (AllTiers order);
    // empty set or an unrecognized tag → null (omit — never guess from garbage).
    private static string? ClampTier(string requested, global::System.Collections.Generic.IReadOnlyList<string> supported)
    {
        if (supported.Count == 0) return null;
        var requestedIndex = global::System.Array.IndexOf(AllTiers, requested);
        if (requestedIndex < 0) return null;
        for (var i = requestedIndex - 1; i >= 0; i--)
            if (supported.Contains(AllTiers[i])) return AllTiers[i];
        foreach (var tier in AllTiers)
            if (supported.Contains(tier)) return tier;
        return null;
    }

    /// <summary>
    /// Strip an optional provider slug prefix used by OpenRouter (e.g. <c>openai/gpt-image-2</c>
    /// → <c>gpt-image-2</c>). When multiple slash segments exist, the final segment is the
    /// upstream model id. When the model name has no slash, returns it unchanged. Lets the
    /// classifier helpers above match both bare model names and OpenRouter slugs. Internal so
    /// sibling model-id classifiers (e.g. the deprecated-model guard in AIEnhancementService,
    /// F31) share THIS stripping rule instead of growing a divergent one.
    /// </summary>
    internal static string Bare(string model)
    {
        var slash = model.LastIndexOf('/');
        var bare = slash >= 0 ? model[(slash + 1)..] : model;
        // OpenRouter routing-variant suffixes (":nitro", ":floor", ":free" — documented
        // as applicable to ANY model id) are routing hints, not part of the model
        // family — strip before classification so "openai/gpt-image-2:nitro" keeps
        // gpt-image-2's capabilities and "dall-e-3:nitro" is still refused (Codex
        // diff r2: the delimiter-aware classifiers would otherwise regress them).
        var colon = bare.IndexOf(':');
        return colon >= 0 ? bare[..colon] : bare;
    }
}

/// <summary>
/// IMG-2: what one generation will ACTUALLY request after per-model capability
/// normalization (<see cref="ImageOptions.NormalizeForModel"/>) — MANDATORY on
/// <c>ImageGenerationResult</c> and the single source the success paths persist
/// (History labels, redo pre-selection), so recorded state always matches what
/// rendered. <c>Adjustments</c> carries one fragment per normalized knob
/// (e.g. <c>"imageSize 2K -> 1K"</c>) for the service's Information log.
/// </summary>
public sealed record EffectiveImageOptions(
    string? Aspect,
    string? SizeTier,
    string? Quality,
    global::System.Collections.Generic.IReadOnlyList<string> Adjustments)
{
    /// <summary>All-Auto instance for tests/fixtures.</summary>
    public static readonly EffectiveImageOptions Empty =
        new(null, null, null, global::System.Array.Empty<string>());
}
