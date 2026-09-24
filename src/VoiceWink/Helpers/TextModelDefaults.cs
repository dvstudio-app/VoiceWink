using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Suggested default TEXT model per provider (UX-1) — the text sibling of
/// <see cref="ImageOptions.DefaultModelFor"/>. Consumed ONLY membership-gated against the
/// provider's LIVE fetched-and-filtered model list: a value absent from that list silently
/// falls through (no selection is forced), so entries are best-effort suggestions and drift
/// is harmless. Every entry must pass <c>ModelDisplayPolicy.IsChatModel</c> — structurally
/// pinned by <c>TextModelDefaultsTests</c>, so an entry the chat dropdown filter would hide
/// (e.g. a dated snapshot id like <c>claude-haiku-4-5-20251001</c>) turns CI red instead of
/// silently never matching. Keep entries on cheap chat-capable tiers — this seeds a
/// never-configured provider's dropdown, it is not a recommendation surface (backlog ENH-4).
///
/// <para><b>Passing <c>IsChatModel</c> is necessary but NOT sufficient.</b> The id must also still
/// EXIST in that provider's live catalog, and no unit test can check that — the suite never calls a
/// provider API. The Anthropic entry sat dead for exactly this reason (see the comment on it
/// below): a plausible undated alias that the catalog does not publish. Live membership is verified
/// by <c>/vw-model-review</c>, which runs weekly and reports a dead default as P1.</para>
/// </summary>
internal static class TextModelDefaults
{
    internal static string? DefaultModelFor(AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "gpt-5.4-mini",
        // Sonnet 5 (owner decision 2026-07-29). This was claude-haiku-4-5, which could NEVER
        // match: Anthropic's catalog publishes haiku 4.5 only as the dated
        // claude-haiku-4-5-20251001, and IsDatedSnapshot hides that — while the undated alias the
        // entry named is not in the catalog at all. So Anthropic silently had no pre-selected
        // model. An undated alias form is necessary but NOT sufficient; the id must also actually
        // EXIST in the provider's live list. claude-sonnet-5 does, as an undated alias.
        AIProvider.Anthropic => "claude-sonnet-5",
        // 3.8 Flash (2026-09-05, from the weekly /vw-model-review). Was gemini-3.7-flash, which
        // still resolves — this is the stale-but-working class Phase 3b exists to catch, not a dead
        // id. Same vendor, same published `flash` tier, one version newer, and Google prices the two
        // IDENTICALLY: $0.75 in / $3.75 out per 1M through 2026-12-31, $1.50 / $7.50 from
        // 2027-01-01, on BOTH — they step together, so "no billed dimension costs more" holds on
        // either side of that date. (In the standard configuration this default routes DIRECT to
        // Gemini, so the direct price is the one that gates it; a user-set `aiBaseUrl_gemini` wins
        // over that in AIProviderConfig.GetBaseUrl, and prices behind a proxy are unknowable here.
        // OpenRouter's Gemini rows sit behind a separate default and are priced separately.)
        // ReasoningEffortPolicy.ResolveGeminiDefault (the no-choice path every Release request
        // takes; ResolveGemini is the Debug override path, gated the same way) gates on the
        // `gemini-3` family prefix after Canonicalize unifies '.' and '-', so gemini-3-8-flash
        // takes the SAME row as 3.7 rather than an unknown-model fallback. Changes only what a
        // user who has never picked a Gemini
        // model gets pre-selected: no persisted aiModel_gemini value is migrated or cleared.
        AIProvider.Gemini => "gemini-3.8-flash",
        // gpt-oss-120b (owner decision 2026-08-19, ENH-18, after a live A/B on the shipped prompt
        // path). Was llama-3.3-70b-versatile, which Groq RETIRED on 2026-08-16 — absent from the
        // catalog and 404 per-id, exactly the Anthropic failure this file's remarks describe. What
        // that cost, stated at the one call site that exists rather than app-wide: the Enhancement
        // Options dialog's UX-1 pre-select fell through to null for any Groq user with no persisted
        // aiModel_groq, so the dialog opened with no model chosen and its primary button disabled by
        // HasUsableModelSelection() until they picked one by hand. Recoverable (that combo is
        // editable), never a dead end.
        //
        // This value DOES reach a live provider request, and an earlier revision of this comment
        // wrongly said it could not (Codex diff review, PR #579). The normal enhancement path indeed
        // never consults this table — AIEnhancementService.SelectedModel reads the persisted key with
        // a "" default — but the REDO path does: the dialog returns its pre-selected model as
        // EnhancementDialogSelection.Model, App.xaml.cs hands that to
        // MainViewModel.RedoEnhancementAsync, and it lands in EnhanceWithModelAsync as the model the
        // request runs on, without ever being persisted. So a user who accepts the pre-selection
        // redoes against THIS id. The retired id never got that far only because membership gating
        // left the dialog unconfirmable. Verified live 2026-08-19: this id answers 200 with usable
        // content through the shipped wire shape. Groq deleted its whole Meta chat line, so every
        // successor is a cross-vendor move; the choice was between the two surviving PRODUCTION
        // models (qwen/qwen3.6-27b is preview-tier, "evaluation purposes only", and 4x/5x the price).
        //
        // 120b over the cheaper 20b DELIBERATELY, against this file's own "keep entries on cheap
        // tiers" guidance — do not "fix" it back. On a Dutch spoken self-correction ("met de
        // boekhouder gesproken, nee wacht, maak dat de notaris") 120b applied the correction 2/2 and
        // 20b transcribed it literally 0/2, emitting text that still named the wrong person AND
        // carried the meta-instruction. That is the small-instruction-tuned-model failure behind the
        // 2026-07-17 incidents (see AIPrompts' firewall note), on the language most of this app's
        // dictation is in. Both models passed the question-shaped and injection-shaped cases.
        // The 20b price advantage buys nothing here: Groq advertises 1000 vs 500 tok/s but the
        // MEASURED medians were 2713 ms (20b) vs 2736 ms (120b) — 20b emits more reasoning, which
        // cancels its throughput. $0.15 in / $0.60 out per 1M vs $0.075 / $0.30, i.e. ~$0.0002 per
        // dictation.
        //
        // Groq's gpt-oss serving turns reasoning ON (medium) and offers no "none" — the ~2.7 s
        // round trips measured above against the Cerebras pick's 0.6-1.6 s. Since ENH-23
        // (2026-09-12) ReasoningEffortPolicy's dictation default sends reasoning_effort=low for
        // both gpt-oss ids in every build, the lowest value Groq documents; the measurement above
        // predates that and has not been re-taken. With the Meta line gone Groq has no
        // non-reasoning chat model left (allam-2-7b is 7B, 4096-token context, Arabic-first).
        // Catalog-membership-verified live 2026-08-19. Changes only what a user who has never
        // picked a Groq model gets pre-selected: no persisted aiModel_groq value is migrated or
        // cleared.
        AIProvider.Groq => "openai/gpt-oss-120b",
        AIProvider.Mistral => "mistral-small-latest",
        AIProvider.OpenRouter => "openai/gpt-5.4-mini",
        // qwen-3.8-27b (ENH-24, owner decision 2026-09-12). Cerebras's deprecation notice: "Starting
        // September 3, 2026, gemma-4-31b is no longer available on Cerebras public endpoints" (it
        // stays on Dedicated Endpoints only), naming qwen-3.8-27b as the public-endpoint
        // replacement — and the /models catalog STILL lists gemma-4-31b, which is why the weekly
        // /vw-model-review's membership check rated the old default "current" on 2026-09-12. A
        // membership check cannot see an endpoint withdrawal; found by Codex in ENH-23's review.
        // Since ENH-25 (2026-09-13) CerebrasModelCatalog hides the withdrawn id from the dropdown too.
        //
        // The previous default was gemma-4-31b (owner fitness pick 2026-07-31, after a live A/B at
        // 0.6–1.6 s round trips): dense 31B, no thinking tax, strongest multilingual fit for
        // Dutch+English dictation. It was chosen OVER qwen-3.8-27b precisely because qwen's serving
        // default is HIGH reasoning; ENH-23 (2026-09-12) sends reasoning_effort=none for this id in
        // every build, which removes that tax and makes the vendor's own replacement the natural
        // pick. gpt-oss-120b (runs at low since ENH-23) was the other candidate — the owner's own
        // usage since 2026-09-07 ran on qwen. Before gemma, ENH-11 had repaired the dead
        // llama-3.3-70b to gpt-oss-120b (a migration-target pick, not a fitness ranking).
        //
        // Changes only what a user who has never picked a Cerebras model gets pre-selected. A
        // persisted gemma-4-31b selection is NOT migrated: it fails on the pill with Cerebras's own
        // message (ENH-1 surfaces the provider's error text), and the install base at the time was
        // the owner's machines plus the beta tester ring — a handful of people the owner can reach
        // directly if one of them had picked gemma. (An earlier revision of this comment said "no
        // third-party installs", which was false: the ring existed and had reported bugs.)
        // Catalog-membership-verified against the 2026-09-12 weekly snapshot (gemma-4-31b,
        // gpt-oss-120b, qwen-3.8-27b); NOT verified by a live call from this change.
        AIProvider.Cerebras => "qwen-3.8-27b",
        // LAI-1: the user's own server carries whatever models THEY installed; there is no id
        // VoiceWink could suggest that the server is known to have. Nothing is pre-selected.
        AIProvider.LocalServer => null,
        _ => null,
    };
}
