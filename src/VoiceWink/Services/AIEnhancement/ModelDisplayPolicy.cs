namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// The app's model DISPLAY policy: which ids reach a dropdown, and which are hidden.
///
/// <para><b>Pure by contract — no HTTP, no Serilog, no DI, no clock.</b> That is not tidiness, it is
/// the whole point (ENH-20). <c>/vw-model-review</c> has to answer "what would this dropdown show?"
/// every week, and it used to answer by RE-IMPLEMENTING these rules in PowerShell — a transcription
/// that produced a defect on four consecutive runs. These members previously lived on
/// <see cref="AIEnhancementService"/>, which carries HTTP, Serilog, DI and caching, so no harness
/// could link them and the weekly run had no choice but to re-derive them. They live here so
/// <c>tools/model-filter-sim</c> can link the SHIPPED rules instead.</para>
///
/// <para><b>Adding a dependency here re-arms that.</b> A <c>using</c> that drags in Serilog, HTTP or
/// the WinUI stack breaks the harness link, and nothing in CI would catch it — the tool sits outside
/// <c>VoiceWink.sln</c> by the <c>tools/</c> convention. If a rule genuinely needs I/O, it does not
/// belong in this file.</para>
///
/// <para>Moved VERBATIM from <see cref="AIEnhancementService"/> (2026-08-29); every rule, comment and
/// recorded owner decision below is unchanged as of that move, and the existing suite is the proof —
/// <see cref="HasLiveSegment"/> (2026-09-13) is the one addition since, and its own remarks say why. There are
/// deliberately NO forwarding members left behind on the service: callers name this class directly,
/// so there is one home for the question rather than two.</para>
/// </summary>
internal static class ModelDisplayPolicy
{
    /// <summary>
    /// Non-chat model patterns to exclude from the model dropdown.
    /// These are model types that cannot be used for text enhancement.
    /// </summary>
    private static readonly string[] ExcludedModelPatterns =
    {
        "dall-e", "whisper", "tts", "text-embedding", "embedding",
        "moderation", "sora", "babbage", "davinci", "text-search",
        "code-search", "realtime", "transcription", "audio",
        "computer-use", "codex", "gpt-image", "imagen", "nano-banana",
        "transcribe", "diarize", "search", "instruct", "-image-",
        // Non-chat models on Groq
        "orpheus", "prompt-guard", "safeguard", "compound",
        // "gemma" was excluded here from the Groq era ("weak/local models") until 2026-07-30 —
        // by then its only effect was hiding CHAT-CAPABLE gemma-4 models the owner wants offered
        // (a third of Cerebras's 3-model catalog, two verified Gemini entries). Owner decision
        // ENH-11: gemma shows everywhere; quality steering is ENH-4's job, not a blocklist's.
        // Gemini non-chat models
        "veo", "lyria", "robotics", "aqa"
    };

    private static readonly global::System.Text.RegularExpressions.Regex DateSnapshotPattern =
        new(@"-(\d{8}|\d{4}(-\d{2}(-\d{2})?)?)(-preview)?$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

    /// <summary>Matches version-pinned models like gemini-2.0-flash-001.</summary>
    private static readonly global::System.Text.RegularExpressions.Regex VersionSuffixPattern =
        new(@"-\d{3}$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// Known image generation model patterns. <c>dall-e</c> and <c>imagen</c> are intentionally
    /// excluded — those families are deprecated and stay in <see cref="ExcludedModelPatterns"/>
    /// so they're filtered out of both chat and image dropdowns.
    /// </summary>
    private static readonly string[] ImageModelPatterns =
    {
        "gpt-image", "nano-banana"
    };

    /// <summary>
    /// Returns true if the model ID looks like an image generation model. Includes
    /// <c>-preview</c>-suffixed variants, and they are KEPT: the production image dropdown path
    /// (<see cref="ApplyDisplayPolicy"/>) hides only dated snapshots, per the 2026-07-30
    /// previews-shown decision.
    /// </summary>
    internal static bool IsImageModel(string modelId)
    {
        return ImageModelPatterns.Any(p => modelId.Contains(p, StringComparison.OrdinalIgnoreCase))
            || modelId.EndsWith("-image", StringComparison.OrdinalIgnoreCase)
            || modelId.EndsWith("-image-preview", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for preview / experimental model IDs we'd rather hide from the default
    /// dropdown — e.g. <c>gemini-2.5-pro-preview-06-05</c>, <c>gemini-3-pro-image-preview</c>,
    /// <c>gemini-2.5-flash-exp</c>. Callers combine this with <see cref="IsImageModel"/> /
    /// <see cref="IsChatModel"/>; users can flip <c>ShowAllModels</c> to bypass and see them.
    /// </summary>
    internal static bool IsPreviewModel(string modelId)
        => modelId.Contains("-preview", StringComparison.OrdinalIgnoreCase)
        || modelId.Contains("-exp-", StringComparison.OrdinalIgnoreCase)
        || modelId.EndsWith("-exp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for date-stamped model snapshots — e.g. <c>gpt-4o-2024-08-06</c>,
    /// <c>gpt-image-2-2026-04-21</c>, <c>gpt-4-0613</c>. These are hidden from the default chat AND
    /// image dropdowns in favour of the stable alias (<c>gpt-image-2</c>); users can flip
    /// <c>ShowAllModels</c> to bypass. Symmetric with <see cref="IsPreviewModel"/>.
    /// </summary>
    internal static bool IsDatedSnapshot(string modelId) => DateSnapshotPattern.IsMatch(modelId);

    /// <summary>
    /// Google ships its current image models under a <c>-image-preview</c> suffix
    /// (<c>gemini-3-pro-image-preview</c>, <c>gemini-3.1-flash-image-preview</c>, and the OpenRouter
    /// <c>google/…-image-preview</c> slugs). Unlike chat models — where <c>-preview</c> marks an
    /// unstable snapshot — these ARE the production image models, so the default image dropdown must
    /// not hide them via <see cref="IsPreviewModel"/>. Date-stamped snapshots are still hidden
    /// separately by <see cref="IsDatedSnapshot"/>.
    /// </summary>
    internal static bool IsProductionImagePreview(string modelId)
        => modelId.EndsWith("-image-preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The default image-model dropdown predicate: an image model that is NOT a date-stamped snapshot
    /// and NOT an experimental preview — except that Google's <c>-image-preview</c> production models
    /// are kept (see <see cref="IsProductionImagePreview"/>). <paramref name="showAll"/> (the user's
    /// <c>ShowAllModels</c> toggle) bypasses the hiding.
    ///
    /// <para><b>NO PRODUCTION CALL SITE (verified 2026-08-06, weekly model review).</b> This doc used
    /// to claim it was "used by EVERY image-model dropdown … so the filter can't drift between them",
    /// which is false and actively misleading: every image dropdown is fed by
    /// <see cref="ApplyDisplayPolicy"/>, which applies only <see cref="IsDatedSnapshot"/>. The
    /// difference is the PREVIEW half — the 2026-07-30 owner decision recorded on
    /// <see cref="ApplyDisplayPolicy"/> is that previews are SHOWN, so nothing gates on
    /// <see cref="IsPreviewModel"/> any more. The only caller left is a unit test.</para>
    ///
    /// <para>Kept rather than deleted because <see cref="IsProductionImagePreview"/> encodes a real
    /// fact about Google's id shapes that is cheaper to keep than to rediscover. If you reach for this
    /// predicate, you are re-introducing preview hiding — that is a product decision, not a refactor.</para>
    /// </summary>
    internal static bool ShouldShowImageModel(string modelId, bool showAll)
        => showAll
        || (IsImageModel(modelId)
            && !IsDatedSnapshot(modelId)
            && (!IsPreviewModel(modelId) || IsProductionImagePreview(modelId)));

    internal static bool IsChatModel(string modelId)
    {
        // Exclude non-chat model types
        if (ExcludedModelPatterns.Any(p => modelId.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Exclude Live-API (full-duplex voice session) models — see HasLiveSegment
        if (HasLiveSegment(modelId))
            return false;

        // Exclude fine-tuned models (ft:gpt-4o:org:...)
        if (modelId.StartsWith("ft:", StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude reasoning models (o1, o1-mini, o3, o3-pro, o4-mini) — slow/expensive for text tasks
        if (modelId.Length >= 2 && modelId[0] == 'o' && char.IsDigit(modelId[1]))
            return false;

        // Exclude chatgpt-* models (ChatGPT-specific, not standard API)
        if (modelId.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude deprecated GPT-3.5 models
        if (modelId.StartsWith("gpt-3.5", StringComparison.OrdinalIgnoreCase))
            return false;

        // Note: -preview is NOT filtered here, and it is not filtered downstream either — previews
        // are SHOWN (owner decision 2026-07-30, recorded on ApplyDisplayPolicy). This comment used to
        // say EnhancementViewModel applied IsPreviewModel alongside this check; it does not, and has
        // not since that decision. Corrected 2026-08-06 (weekly model review).

        // Exclude version-pinned models (e.g. gemini-2.0-flash-001) — use the stable pointer instead
        if (VersionSuffixPattern.IsMatch(modelId))
            return false;

        // Exclude specialized variants (e.g. -customtools, -image)
        if (modelId.EndsWith("-customtools", StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude image-capable chat variants (e.g. gemini-2.5-flash-image) — base model covers text
        if (modelId.EndsWith("-image", StringComparison.OrdinalIgnoreCase))
            return false;

        // Exclude dated snapshots (gpt-4o-2024-08-06, gpt-4-0613)
        if (IsDatedSnapshot(modelId))
            return false;

        return true;
    }

    /// <summary>
    /// True when the id carries a delimiter-bounded <c>live</c> segment (split on <c>-</c>, <c>.</c>,
    /// <c>/</c> and <c>:</c> — the id grammar's four delimiters, so a vendor prefix or a routing
    /// <c>:variant</c> suffix never glues onto the segment): <c>gpt-live-1</c>,
    /// <c>gemini-3.1-flash-live-preview</c>, <c>gemini-3.5-live-translate-preview</c> and
    /// <c>openai/gpt-live-1:nitro</c> match; <c>gemini-4-olive</c>, <c>gpt-5.6-luna</c> or a
    /// hypothetical <c>liveried-x</c> never do.
    ///
    /// <para>A <c>live</c> segment names a full-duplex voice-session model at BOTH providers that ship
    /// one, and neither can serve the app's <c>chat/completions</c> call. Google: verified against the
    /// native <c>/v1beta/models</c> endpoint on 2026-07-30 — the two ids above support ONLY
    /// <c>bidiGenerateContent</c>, and the OpenAI-compatible endpoint the app fetches from strips that
    /// metadata. OpenAI: <c>gpt-live-1</c> (weekly model review, 2026-09-12) lists exactly one endpoint
    /// on its model page, <c>v1/live/sessions</c>, and states it does not support Chat Completions or
    /// Responses; <c>/v1/models</c> carries no capability field at all. Before this rule, selecting
    /// either family for text enhancement was a provider error pill per dictation.</para>
    ///
    /// <para>Lived in <c>GeminiModelCatalog</c> as a Gemini-SCOPED, DISPLAY-only rule from ENH-9
    /// (2026-07-30) until OpenAI shipped the same id shape; one rule in the shared baseline now covers
    /// both, and a provider-scoped copy would be the duplication that drifts. Being in
    /// <see cref="IsChatModel"/> it reaches the SAME two places every other non-chat rule here does:
    /// the dropdowns (which ShowAllModels bypasses) AND <c>AIEnhancementService.EnhanceAsync</c>'s
    /// ENH-12 guard, which ShowAllModels does NOT bypass — a persisted live id is refused there on the
    /// next dictation ("Model unusable; reset") and the global selection cleared, instead of the
    /// provider error it used to draw. It is a SEGMENT match, not an <see cref="ExcludedModelPatterns"/>
    /// substring, on purpose: a bare <c>live</c> token would hide any future id merely containing the
    /// letters. The accepted mirror risk — a genuinely-chat <c>…-live-…</c> id wrongly refused, with
    /// no user-side escape (same as for <c>audio</c>, <c>realtime</c> and the rest of the token list) —
    /// is watched by the weekly review, which is the one reader that sees provider capability metadata.</para>
    /// </summary>
    internal static bool HasLiveSegment(string modelId)
    {
        foreach (var segment in modelId.Split('-', '.', '/', ':'))
            if (segment.Equals("live", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// THE single display-policy stage. Everything a dropdown shows passes through here, so callers
    /// receive display-ready lists and no UI file needs to know which providers curate.
    ///
    /// <para>Two stages, in order. First the CAPABILITY decision: a provider-curated list
    /// (<see cref="Providers.ProviderModelList.Curated"/> — Mistral capabilities, OpenRouter
    /// modalities, and Gemini, whose curation is the shared <see cref="IsChatModel"/> baseline applied
    /// inside its branch) already ran the capability decision, so it is not re-run here; otherwise
    /// <see cref="IsChatModel"/> / <see cref="IsImageModel"/> run as before. Then the PRODUCT policy —
    /// hide previews and dated snapshots — which applies to curated lists TOO. Skipping it for
    /// curated lists would regress Mistral, whose curation does not remove ordinary previews and
    /// which relied on the ViewModel to do it.</para>
    ///
    /// <para><b>Preview / experimental models are NOT hidden</b> (owner decision 2026-07-30). The
    /// centralization exposed an inconsistency — the main dropdown hid them while the App picker and
    /// onboarding showed them — and the owner resolved it toward SHOWING them: a preview is a model
    /// the user may legitimately want, and several providers ship current models under a
    /// <c>-preview</c> id (which is why <see cref="IsProductionImagePreview"/> had to exist as a
    /// carve-out). <see cref="IsPreviewModel"/> is kept for that classifier and for callers that
    /// still ask the question, but it no longer gates discovery.</para>
    ///
    /// <para><paramref name="showAll"/> skips both stages and returns the provider's list verbatim —
    /// it is the user's escape hatch, not a filtering mode. For OpenRouter that means the raw
    /// modality-query result (routers and vector variants included), never the other catalog. It
    /// still means something with previews visible: it also bypasses dated-snapshot hiding and
    /// provider curation.</para>
    /// </summary>
    internal static List<string> ApplyDisplayPolicy(
        Providers.ProviderModelList fetched, Providers.ModelCatalogQuery query, bool showAll)
    {
        if (showAll)
            return fetched.Models;

        var capable = fetched.Curated
            ? fetched.Models
            : fetched.Models.Where(m => query == Providers.ModelCatalogQuery.Image
                ? IsImageModel(m)
                : IsChatModel(m));

        // Dated snapshots stay hidden: those ARE redundant aliases of a stable pointer, which is a
        // different claim from "preview".
        return query == Providers.ModelCatalogQuery.Image
            ? capable.Where(m => !IsDatedSnapshot(m)).ToList()
            : capable.ToList();
    }
}
