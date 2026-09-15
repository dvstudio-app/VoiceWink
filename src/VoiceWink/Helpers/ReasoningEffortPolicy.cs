using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// The user's per-prompt reasoning choice (ENH-8). Stored on
/// <see cref="Models.CustomPrompt.ReasoningOverride"/> as a string; parsed fail-soft.
/// </summary>
public enum ReasoningChoice
{
    Minimal,
    Thorough,
}

/// <summary>Which wire shape (if any) carries the reasoning preference to the provider.</summary>
public enum ReasoningWireKind
{
    /// <summary>
    /// Send nothing — a model with no dictation-default row (ENH-23) and no override row, a provider
    /// without rows, or a Thorough override on Groq/Cerebras (the provider's own default).
    /// </summary>
    None,
    /// <summary>OpenAI chat <c>reasoning_effort</c> / Responses-API <c>reasoning.effort</c>.</summary>
    OpenAIEffort,
    /// <summary>Anthropic <c>output_config.effort</c> (the current adaptive-effort surface).</summary>
    AnthropicOutputEffort,
    /// <summary>Gemini OpenAI-compat <c>reasoning_effort</c> (mapped to thinking budgets).</summary>
    GeminiEffort,
    /// <summary>OpenRouter unified <c>reasoning.effort</c> block.</summary>
    OpenRouterEffort,
    /// <summary>
    /// ENH-23: OpenAI-compatible <c>reasoning_effort</c> on the plain <c>max_tokens</c> +
    /// <c>temperature</c> chat body — the Groq and Cerebras wire. A separate kind (not
    /// <see cref="OpenAIEffort"/>) because the client gates every reasoning-bearing shape on
    /// provider AND kind, and the OpenAI body differs (<c>max_completion_tokens</c>, no temperature).
    /// </summary>
    CompatEffort,
}

/// <summary>
/// The resolved, provider-ready reasoning decision for ONE request. Immutable record —
/// value equality; <see cref="None"/> means "omit every reasoning field" and MUST leave
/// the request byte-identical to a pre-ENH-8 build.
/// </summary>
public sealed record ReasoningDirective(ReasoningWireKind Kind, string? Effort)
{
    public static readonly ReasoningDirective None = new(ReasoningWireKind.None, null);

    /// <summary>
    /// Content-free description for the prompt trace's <c>[REASONING]</c> line (all builds since
    /// REL-17; the value is the ENH-23 evidence a Release build sent a row).
    /// </summary>
    public string Describe() => Kind switch
    {
        ReasoningWireKind.OpenAIEffort => $"reasoning_effort={Effort}",
        ReasoningWireKind.AnthropicOutputEffort => $"output_config.effort={Effort}",
        ReasoningWireKind.GeminiEffort => $"reasoning_effort={Effort}",
        ReasoningWireKind.OpenRouterEffort => $"reasoning.effort={Effort}",
        ReasoningWireKind.CompatEffort => $"reasoning_effort={Effort}",
        _ => "omitted",
    };
}

/// <summary>
/// ENH-8: the SINGLE mapping site from (provider, model, per-prompt choice) to the wire
/// directive — all provider/model gating lives here so drift is one file to correct (the
/// IMG-2 lesson). Rows are POSITIVE enumeration: anything unmatched omits.
///
/// <para><b>ENH-23 (2026-09-12): the DEFAULT choice is no longer "omit".</b> With no per-prompt
/// choice, <see cref="Resolve"/> answers from the dictation-default table — for each listed model
/// the lowest reasoning effort its OWN model page documents. That is VoiceInk's RULE
/// (<c>ReasoningConfig.swift</c>: the lowest effort a model supports, hard-coded per id, no user
/// control); this table covers more ids than VoiceInk's, whose table sends nothing for
/// gpt-5/5.1/5.2, for any Gemini id outside its own exact sets, or for Cerebras and Groq qwen —
/// every extra row here rests on the provider document its comment cites. OpenAI's reasoning guide
/// says the accepted set is model-dependent ("check the relevant model page"), so the OpenAI rows are
/// EXACT ids with no family fallback: the family-wide <c>low</c> that ENH-23 shipped for unlisted
/// gpt-5 ids switched reasoning ON for gpt-5.1 and gpt-5.2, whose pages document <c>none</c> as the
/// DEFAULT (found by the 2026-09-12 fixloop). Dictation cleanup is instruction following; a model
/// left at its provider default thinks
/// at whatever effort the provider picked for it (Cerebras <c>qwen-3.8-27b</c>: <c>high</c>), and
/// with the app's 4096-token cap counting reasoning tokens that produced a no-answer failure roughly
/// once per 60 dictations. The default table applies in EVERY build; only the per-prompt override
/// (ENH-8 phase A) stays Debug-only, at the <c>AIEnhancementService.BuildConfig</c> choke point.
/// Every default row cites a provider document or VoiceInk's shipping set — a wrong row 400s the
/// default path of a release build. A family prefix (gemini-3, gemini-2-5-pro) only selects a row
/// whose VALUE the provider's per-model table lists on every id of that family it names (the Gemini
/// thinking guide, read 2026-09-12) — an id the guide does not name, such as a <c>-preview</c> of one
/// it does, takes the family row on that family's documented set; no value is guessed from a name.
/// The table's one inference is the gpt-5-mini / gpt-5-nano <c>low</c> (their pages list no values)
/// — the ENH-23 card names the evidence event that retires it.</para>
///
/// Canonical normalization before ALL classification: lowercase, strip an OpenRouter
/// <c>vendor/</c> prefix and <c>:suffix</c> routing variant, then unify <c>.</c> and
/// <c>-</c> — so <c>anthropic/claude-sonnet-4.6:nitro</c> and <c>claude-sonnet-4-6</c> hit
/// the same family row, and <c>gpt-5.4-pro</c> is recognizably a versioned Pro model. The strip
/// runs for every provider, but a decoration the provider's own catalog never carries omits instead,
/// on both paths — OpenRouter's grammar is both halves, Groq's ids carry the prefix and never a
/// <c>:variant</c>, every other provider's chat ids carry neither — see <see cref="Resolve"/>.
/// Family prefixes are delimiter-aware (<c>gpt-50</c>/<c>gemini-30</c> never match).
/// </summary>
public static class ReasoningEffortPolicy
{
    /// <summary>Stored value for <see cref="ReasoningChoice.Minimal"/>.</summary>
    public const string MinimalValue = "minimal";

    /// <summary>Stored value for <see cref="ReasoningChoice.Thorough"/>.</summary>
    public const string ThoroughValue = "thorough";

    /// <summary>
    /// Anthropic families documented for <c>output_config.effort</c> (canonical form).
    /// Deliberately EXCLUDES <c>claude-haiku-4-5</c> (not in the documented effort list —
    /// and it was the app's Anthropic text default in <see cref="TextModelDefaults"/> when
    /// phase A shipped, so a wrong row here would have 400'd the default path; the default is
    /// <c>claude-sonnet-5</c> since 2026-07-29) and every <c>claude-3*</c> generation.
    /// </summary>
    private static readonly string[] AnthropicEffortFamilies =
    {
        "claude-opus-4-5",
        "claude-opus-4-6",
        "claude-opus-4-7",
        "claude-opus-4-8",
        "claude-sonnet-4-6",
        "claude-sonnet-5",
        "claude-fable-5",
        "claude-mythos-5",
    };

    /// <summary>Parse the stored per-prompt value. Unknown/empty → null = Default (fail-soft).</summary>
    public static ReasoningChoice? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        MinimalValue => ReasoningChoice.Minimal,
        ThoroughValue => ReasoningChoice.Thorough,
        _ => null,
    };

    /// <summary>
    /// ENH-23: OpenAI ids whose model page lists <c>reasoning_effort: "none"</c> (developers.openai.com,
    /// read 2026-09-12), exact canonical ids: VoiceInk's shipping set (<c>openAINoneReasoningModels</c>,
    /// its 2026-09-03 source) plus gpt-5.1 and gpt-5.2 — both document "none (default)", so the
    /// family-wide <c>low</c> they took until the 2026-09-12 fixloop switched reasoning ON for them —
    /// and the bare <c>gpt-5.6</c> alias, which OpenAI's page routes to Sol. <c>gpt-6-astra</c> is
    /// deliberately absent: OpenAI documents that it returns HTTP 400 on <c>none</c>. The 5.0 trio
    /// (gpt-5, gpt-5-mini, gpt-5-nano) lists no <c>none</c> and has its own rows in
    /// <see cref="ResolveOpenAIDefault"/>; anything else omits.
    /// </summary>
    private static readonly string[] OpenAINoneEffortModels =
    {
        "gpt-5-1",
        "gpt-5-2",
        "gpt-5-4",
        "gpt-5-4-mini",
        "gpt-5-4-nano",
        "gpt-5-5",
        "gpt-5-6",
        "gpt-5-6-luna",
        "gpt-5-6-sol",
        "gpt-5-6-terra",
    };

    /// <summary>
    /// ENH-23: Gemini ids whose thinking can be switched OFF — the OpenAI-compat docs allow
    /// <c>reasoning_effort: "none"</c> on 2.5 models other than Pro ("Reasoning cannot be turned
    /// off for Gemini 2.5 Pro or 3 models"). Exact canonical ids.
    /// </summary>
    private static readonly string[] GeminiNoThinkingModels =
    {
        "gemini-2-5-flash",
        "gemini-2-5-flash-lite",
    };

    /// <summary>
    /// ENH-23: Gemini 3.x ids that take thinking level <c>minimal</c> — VoiceInk's shipping set
    /// (<c>geminiMinimalThinkingModels</c>) plus <c>gemini-3-flash-preview</c>, whose row in the
    /// Gemini thinking guide (read 2026-09-12) lists "minimal, low, medium, high" — the 2026-09-12
    /// fixloop added it; ENH-23's "-preview falls to the family row" had stranded it on <c>low</c>.
    /// The value itself is documented on the wire this app uses: the Gemini OpenAI-compatibility docs
    /// accept <c>reasoning_effort</c> <c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c> and map them
    /// to <c>thinking_level</c> on 3.x models (VoiceInk sends the native <c>thinkingConfig</c>; the two
    /// carry the same levels). The thinking guide lists no <c>minimal</c> for 3.7 Flash, 3.8 Flash,
    /// 3-pro-preview or 3.1-pro-preview, so every other 3.x id takes <c>low</c>, which that guide
    /// lists on each of them (the compat wire maps <c>minimal</c> to <c>low</c> for 3.1 Pro rather
    /// than rejecting it — VoiceInk's "does not accept" is a native-API fact). Exact canonical ids.
    /// </summary>
    private static readonly string[] GeminiMinimalThinkingModels =
    {
        "gemini-3-flash-preview",
        "gemini-3-5-flash",
        "gemini-3-5-flash-lite",
        "gemini-3-6-flash",
        "gemini-3-1-flash-lite",
    };

    /// <summary>
    /// Resolve the wire directive for one request. With no per-prompt choice (null / unknown
    /// stored value) the answer is the ENH-23 dictation default for the model — omit for every
    /// model without a default row. With a choice, the ENH-8 override rows apply; unknown model
    /// families and providers without override rows (Mistral) yield <see cref="ReasoningDirective.None"/>.
    /// </summary>
    public static ReasoningDirective Resolve(AIProvider provider, string? modelName, string? rawChoice)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return ReasoningDirective.None;

        // Canonicalize strips a `vendor/` prefix and a `:variant` suffix for every provider because
        // Groq's own catalog ids carry the prefix (openai/gpt-oss-120b, qwen/qwen3.8-27b) and
        // OpenRouter's grammar is both. No other provider publishes a CHAT id shaped like that (an
        // OpenAI or Mistral `ft:` fine-tune matches no row either way — it is refused here, and it
        // canonicalized to "ft" before; the parser strips Gemini's `models/` before an id is ever
        // stored), so a decorated id reaches the rows only through a custom base URL pointed at a
        // proxy — an OpenRouter slug on `aiBaseUrl_openai`, say — where a row's value is exactly the
        // unverified case that keeps OpenRouter without default rows. Omit there, on both paths: the
        // default path is then what every Release build before ENH-23 sent, and the Debug override
        // no longer canonicalizes such an id into a family row.
        if (HasRoutingDecoration(provider, modelName))
            return ReasoningDirective.None;

        var canon = Canonicalize(modelName);
        var choice = Parse(rawChoice);
        if (choice is null)
            return ResolveDictationDefault(provider, canon);

        return provider switch
        {
            AIProvider.OpenAI => ResolveOpenAI(canon, choice.Value, ReasoningWireKind.OpenAIEffort),
            AIProvider.Anthropic => ResolveAnthropic(canon, choice.Value, ReasoningWireKind.AnthropicOutputEffort),
            AIProvider.Gemini => ResolveGemini(canon, choice.Value, ReasoningWireKind.GeminiEffort),
            // OpenRouter reuses the SAME family rows on the normalized slug and emits its
            // unified reasoning block; an unknown slug omits (no blanket "ignored" claim).
            AIProvider.OpenRouter => ResolveOpenRouter(canon, choice.Value),
            // ENH-23: on the two OpenAI-compatible providers the override is a two-way A/B over
            // the default table — Minimal = the default row, Thorough = omit, i.e. the provider's
            // own default (exactly the pre-ENH-23 request). No new wire values, so no 400 risk
            // beyond the default row's own, and the Debug dropdown compares "our floor" against
            // "what the provider does on its own" on the user's real dictations.
            AIProvider.Groq or AIProvider.Cerebras => choice.Value == ReasoningChoice.Minimal
                ? ResolveDictationDefault(provider, canon)
                : ReasoningDirective.None,
            _ => ReasoningDirective.None,
        };
    }

    /// <summary>
    /// ENH-23: the per-model dictation default — for each listed model the lowest reasoning effort
    /// its own model page documents. Positive enumeration; anything unlisted omits and the request
    /// stays byte-identical to a pre-ENH-23 build. Anthropic (no thinking is ever requested;
    /// <c>output_config.effort</c> is not reasoning tokens), Mistral (no parameter exists) and
    /// OpenRouter (its accepted set for <c>none</c>/<c>minimal</c> is unverified, and the app's
    /// OpenRouter traffic runs on models with no row) deliberately have NO default rows — VoiceInk
    /// sends nothing there either.
    /// </summary>
    private static ReasoningDirective ResolveDictationDefault(AIProvider provider, string canon) => provider switch
    {
        AIProvider.OpenAI => ResolveOpenAIDefault(canon),
        AIProvider.Gemini => ResolveGeminiDefault(canon),
        AIProvider.Cerebras => ResolveCerebrasDefault(canon),
        AIProvider.Groq => ResolveGroqDefault(canon),
        _ => ReasoningDirective.None,
    };

    private static ReasoningDirective ResolveOpenAIDefault(string canon)
    {
        if (OpenAINoneEffortModels.Contains(canon))
            return new ReasoningDirective(ReasoningWireKind.OpenAIEffort, "none");
        return canon switch
        {
            // The 5.0 trio lists no "none". gpt-5's page (read 2026-09-12): "minimal, low, medium,
            // and high" — minimal is its floor. The mini and nano pages list no values at all, so
            // they take low, the lowest value every gpt-5 page that lists any carries; VoiceInk sends
            // nothing for all three. Pro (rejects low), the chat variants (no reasoning), gpt-6 (astra
            // 400s on none), a dated snapshot and any id no page was read for omit — an unlisted id's
            // default may be none, and a family-wide low switched reasoning ON for 5.1 and 5.2.
            "gpt-5" => new ReasoningDirective(ReasoningWireKind.OpenAIEffort, "minimal"),
            "gpt-5-mini" or "gpt-5-nano" => new ReasoningDirective(ReasoningWireKind.OpenAIEffort, "low"),
            _ => ReasoningDirective.None,
        };
    }

    private static ReasoningDirective ResolveGeminiDefault(string canon)
    {
        if (IsGeminiImageModel(canon))
            return ReasoningDirective.None;
        if (GeminiNoThinkingModels.Contains(canon))
            return new ReasoningDirective(ReasoningWireKind.GeminiEffort, "none");
        if (GeminiMinimalThinkingModels.Contains(canon))
            return new ReasoningDirective(ReasoningWireKind.GeminiEffort, "minimal");
        // 2.5 Pro cannot switch thinking off (low = the 1024-token budget); every 3.x id maps
        // reasoning_effort onto thinking_level, and the thinking guide's per-model table (read
        // 2026-09-12) lists low on every 3.x TEXT id it names. The `gemini-*-latest` aliases are NOT
        // in the 3 family by prefix and deliberately omit: their target is unpublished, and `low` on
        // a 2.5 flash-lite target would turn thinking ON for a model whose own default is off.
        if (HasFamilyPrefix(canon, "gemini-2-5-pro") || HasFamilyPrefix(canon, "gemini-3"))
            return new ReasoningDirective(ReasoningWireKind.GeminiEffort, "low");
        return ReasoningDirective.None;
    }

    private static ReasoningDirective ResolveCerebrasDefault(string canon) => canon switch
    {
        // Cerebras docs (read 2026-09-12): qwen-3.8-27b accepts none/low/medium/high and
        // ENABLES reasoning at high by default — the row this feature exists for.
        "qwen-3-8-27b" => new ReasoningDirective(ReasoningWireKind.CompatEffort, "none"),
        // gemma-4-31b: the same docs list none (its default)/low/medium/high as accepted values;
        // sending the default explicitly pins a listed model against a provider-side default change
        // (the qwen row above is what such a change looks like). It was the app's Cerebras text
        // default until ENH-24 (2026-09-12): Cerebras withdrew the id from its public endpoints on
        // 2026-09-03 while its catalog kept listing it. The row stays — inert on a withdrawn id,
        // correct on a dedicated endpoint, and a persisted selection can still name it.
        "gemma-4-31b" => new ReasoningDirective(ReasoningWireKind.CompatEffort, "none"),
        // gpt-oss-120b: low/medium/high only (no none), default medium. VoiceInk sends low.
        "gpt-oss-120b" => new ReasoningDirective(ReasoningWireKind.CompatEffort, "low"),
        _ => ReasoningDirective.None,
    };

    private static ReasoningDirective ResolveGroqDefault(string canon) => canon switch
    {
        // Groq docs (read 2026-09-12): gpt-oss accepts low/medium/high (no none) and serves
        // with reasoning ON — the ~2.7 s round trips TextModelDefaults records. VoiceInk sends low.
        "gpt-oss-120b" or "gpt-oss-20b" => new ReasoningDirective(ReasoningWireKind.CompatEffort, "low"),
        // qwen/qwen3.8-27b → qwen3-8-27b after the vendor strip: none/default/low/medium/high;
        // qwen/qwen3.6-27b: none/default. Both accept none.
        "qwen3-8-27b" or "qwen3-6-27b" => new ReasoningDirective(ReasoningWireKind.CompatEffort, "none"),
        _ => ReasoningDirective.None,
    };

    /// <summary>
    /// Gemini image models (<c>gemini-3.1-flash-lite-image</c>, <c>gemini-2.5-flash-image</c>) take no
    /// text row under any choice: the thinking guide lists "minimal, high" only for
    /// <c>gemini-3.1-flash-lite-image</c>, so the 3.x family's <c>low</c> would be a 400, and an image
    /// model is not a dictation model in the first place. Reachable only through the redo path
    /// (<c>AIEnhancementService.EnhanceWithModelAsync</c>, which runs no <c>IsChatModel</c> guard):
    /// the main path refuses an image id before <c>BuildConfig</c>, and the text dropdown hides them.
    /// </summary>
    private static bool IsGeminiImageModel(string canon) => HasToken(canon, "image");

    /// <summary>
    /// Versioned Pro models (gpt-5-pro, gpt-5.4-pro → gpt-5-4-pro). Detected by the exact
    /// <c>pro</c> token so no versioning scheme slips a Pro model into the generic row.
    /// </summary>
    private static bool IsOpenAIProModel(string canon) => HasToken(canon, "pro");

    /// <summary>
    /// ENH-23: the chat variants (gpt-5-chat-latest, gpt-5.1-chat-latest, ...) are the
    /// non-reasoning ChatGPT-tuned models; a reasoning value is not theirs to take under any
    /// choice. Closed in ENH-23, when the family-wide default row could still reach them in Release;
    /// since the default rows became exact ids only the override path and OpenRouter's reuse of it
    /// consult this guard.
    /// </summary>
    private static bool IsOpenAIChatVariant(string canon) => HasToken(canon, "chat");

    private static ReasoningDirective ResolveOpenAI(string canon, ReasoningChoice choice, ReasoningWireKind kind)
    {
        if (!HasFamilyPrefix(canon, "gpt-5") || IsOpenAIChatVariant(canon))
            return ReasoningDirective.None; // gpt-4o etc. 400 on the param; chat variants have no reasoning

        // Pro models reject "low"; all document "high".
        if (IsOpenAIProModel(canon))
        {
            return choice == ReasoningChoice.Thorough
                ? new ReasoningDirective(kind, "high")
                : ReasoningDirective.None;
        }

        if (choice == ReasoningChoice.Thorough)
            return new ReasoningDirective(kind, "high");

        // OpenRouter's reuse of this row keeps the pre-ENH-23 "low": its accepted set for "none" is
        // unverified, the reason it has no default rows (so on the slugs whose OpenAI page documents
        // a "none" default, Minimal there asks for MORE than the provider's own default — a stated
        // Debug-only residual, not a floor).
        if (kind != ReasoningWireKind.OpenAIEffort)
            return new ReasoningDirective(kind, "low");

        // On the OpenAI wire, Minimal is the model's floor — the value its ENH-23 default row sends
        // (none / minimal / low) — otherwise "Minimal — fastest" would ask for MORE thinking than
        // Default (Gemini diff r1). An in-family id with no default row sends nothing: no page was
        // read for it, so no value is known to sit at or below its default (a family-wide "low" is
        // exactly what switched reasoning ON for gpt-5.1 and 5.2).
        return ResolveOpenAIDefault(canon);
    }

    private static ReasoningDirective ResolveAnthropic(string canon, ReasoningChoice choice, ReasoningWireKind kind)
    {
        if (!AnthropicEffortFamilies.Any(f => HasFamilyPrefix(canon, f)))
            return ReasoningDirective.None;
        return new ReasoningDirective(kind, choice == ReasoningChoice.Minimal ? "low" : "high");
    }

    private static ReasoningDirective ResolveGemini(string canon, ReasoningChoice choice, ReasoningWireKind kind)
    {
        if ((!HasFamilyPrefix(canon, "gemini-2-5") && !HasFamilyPrefix(canon, "gemini-3")) || IsGeminiImageModel(canon))
            return ReasoningDirective.None;
        if (choice == ReasoningChoice.Thorough)
            return new ReasoningDirective(kind, "high");

        // OpenRouter's reuse of this row keeps the pre-ENH-23 "low": its accepted set for
        // none/minimal is unverified, the reason it has no default rows (a stated Debug-only residual).
        if (kind != ReasoningWireKind.GeminiEffort)
            return new ReasoningDirective(kind, "low");

        // Minimal is the model's floor on the Gemini wire — the value its ENH-23 default row sends
        // (none / minimal / low), so "Minimal — fastest" never asks for MORE thinking than Default
        // (the same inversion the review fixed on the OpenAI wire; a 2.5 flash at "low" would turn
        // thinking ON). An in-family id with no default row sends nothing, for the same reason: a
        // 2.5 flash-lite preview's own default is thinking OFF, and "low" would switch it on.
        return ResolveGeminiDefault(canon);
    }

    private static ReasoningDirective ResolveOpenRouter(string canon, ReasoningChoice choice)
    {
        var family = ResolveOpenAI(canon, choice, ReasoningWireKind.OpenRouterEffort);
        if (family.Kind == ReasoningWireKind.None)
            family = ResolveAnthropic(canon, choice, ReasoningWireKind.OpenRouterEffort);
        if (family.Kind == ReasoningWireKind.None)
            family = ResolveGemini(canon, choice, ReasoningWireKind.OpenRouterEffort);
        return family;
    }

    /// <summary>
    /// Lowercase; strip an OpenRouter vendor prefix (last '/') and ':' routing suffix;
    /// unify '.' and '-' so dotted and hyphenated version forms classify identically.
    /// </summary>
    internal static string Canonicalize(string modelName)
    {
        var s = modelName.Trim().ToLowerInvariant();
        var slash = s.LastIndexOf('/');
        if (slash >= 0)
            s = s[(slash + 1)..];
        var colon = s.IndexOf(':');
        if (colon >= 0)
            s = s[..colon];
        return s.Replace('.', '-');
    }

    /// <summary>Delimiter-aware family prefix on the CANONICAL form (exact, or prefix + '-').</summary>
    private static bool HasFamilyPrefix(string canon, string family)
        => canon == family || canon.StartsWith(family + "-", StringComparison.Ordinal);

    /// <summary>Exact '-'-delimited token on the CANONICAL form (<c>gpt-5-4-pro</c> has "pro"; <c>gpt-5-prophet</c> would not).</summary>
    private static bool HasToken(string canon, string token) => canon.Split('-').Contains(token);

    /// <summary>
    /// A <c>vendor/</c> prefix or <c>:variant</c> suffix on the RAW id that the provider's own catalog
    /// never carries: OpenRouter's grammar is both, so nothing is decoration there; Groq's catalog ids
    /// carry the prefix but never a <c>:variant</c>; no other provider's chat ids carry either.
    /// </summary>
    private static bool HasRoutingDecoration(AIProvider provider, string modelName) => provider switch
    {
        AIProvider.OpenRouter => false,
        AIProvider.Groq => modelName.Contains(':'),
        _ => modelName.Contains('/') || modelName.Contains(':'),
    };
}
