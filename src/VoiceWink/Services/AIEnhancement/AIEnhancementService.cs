using System.Net.NetworkInformation;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement.Providers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Orchestrator for AI text enhancement.
/// Routes to provider-specific clients via <see cref="AIProviderRegistry"/>, manages prompts,
/// applies output filtering.
/// </summary>
public sealed class AIEnhancementService
{
    private static ILogger Logger => Log.ForContext<AIEnhancementService>();

    /// <summary>
    /// Upper bound for one TEXT-enhancement provider call (ENH-3). 2× the worst
    /// healthy call observed in the field (30 s, gpt-5-nano 2026-07-07); the
    /// 2026-07-07 network incident held the pill on "Enhancing..." for 132 s.
    /// Expiry falls back to the raw transcript with the reason on the redo pill —
    /// recoverable, unlike a frozen pipeline. Image generation is NOT bounded by
    /// this (2–4 min runs are legitimate).
    /// </summary>
    internal static readonly TimeSpan DefaultEnhancementTimeout = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ApiKeyManager _apiKeys;
    private readonly SettingsService _settings;
    private readonly AIProviderRegistry _providers;
    private readonly Func<bool> _networkAvailable;
    private readonly TimeSpan _enhancementTimeout;

    private List<CustomPrompt>? _cachedPrompts;

    // ── IMG-10b: session-scoped model-list cache (stale-while-revalidate) ─────────────
    //
    // The options dialog binds a cached list INSTANTLY on open and refreshes in the
    // background; the fresh result lands for the NEXT open. Serving from this cache is
    // OPT-IN PER CALL SITE (TryGetCachedModels) — the fetch itself never reads it, so a
    // key-validation probe (which judges validity on RawCount, PR #271) can never be
    // answered by a stale list. Session-scoped only, never persisted: a stale list
    // surviving a restart has no natural correction. Key = the RESOLVED provider ×
    // catalog query × showAll, because each combination is a different display list.
    private readonly Dictionary<(AIProvider, Providers.ModelCatalogQuery, bool), IReadOnlyList<string>> _modelListCache = new();
    // Value carries a per-registration TOKEN beside the task: an invalidation can drop a dead
    // refresh's registration while it is still running, so the refresh must be able to tell
    // whether the entry under its key is still ITS OWN before unregistering (otherwise it would
    // evict a newer refresh and let a third caller start a duplicate fetch).
    private readonly Dictionary<(AIProvider, Providers.ModelCatalogQuery, bool), (object Token, Task Task)> _modelListRefreshes = new();
    // Per-provider epoch, bumped on every key change: a fetch captures it before the
    // HTTP call and stores only if unchanged at completion, so a fetch in flight when
    // the user saves a different key cannot store a list fetched with the old one.
    private readonly Dictionary<AIProvider, int> _modelListEpochs = new();
    private readonly object _modelListLock = new();

    public AIEnhancementService(
        IHttpClientFactory httpFactory,
        ApiKeyManager apiKeys,
        SettingsService settings,
        AIProviderRegistry providers,
        Func<bool>? networkAvailable = null,
        TimeSpan? enhancementTimeout = null)
    {
        _httpFactory = httpFactory;
        _apiKeys = apiKeys;
        _settings = settings;
        _providers = providers;
        // Optional test seams — MEDI fills unresolvable optional params with their
        // defaults, so the DI registration stays a plain AddSingleton.
        _networkAvailable = networkAvailable ?? NetworkInterface.GetIsNetworkAvailable;
        _enhancementTimeout = enhancementTimeout ?? DefaultEnhancementTimeout;
        // IMG-10b: a key change makes every cached list for that provider suspect. The
        // manager is the ONE key-write funnel, so this covers all save/clear/rollback
        // sites at once. Both are DI singletons with identical lifetimes — the
        // subscription holds nothing alive that wouldn't be.
        _apiKeys.KeyChanged += InvalidateModelListCacheForProviderName;
    }

    /// <summary>
    /// IMG-10b key-change invalidation. NON-THROWING BY CONSTRUCTION — it runs inline
    /// inside <see cref="ApiKeyManager.SetApiKey"/>, including the key-validation
    /// ROLLBACK path, where a throw would break rollback UX out of a bool-returning
    /// API: lock + dictionary removals + an epoch bump, no logging, no user code.
    /// The parse is CASE-INSENSITIVE and that is load-bearing (plan review): every
    /// production key-save site passes <c>provider.ToString().ToLowerInvariant()</c>,
    /// so a default (case-sensitive) TryParse would never match and this whole
    /// mechanism would be dead code. An unparseable name (a transcription-only
    /// provider like "deepgram" sharing the key store) has nothing cached under it and
    /// invalidates nothing; "groq" parses and over-invalidates the AI-side cache on a
    /// transcription-side key change — harmless by design.
    /// </summary>
    private void InvalidateModelListCacheForProviderName(string providerName)
    {
        if (!Enum.TryParse<AIProvider>(providerName, ignoreCase: true, out var provider))
            return;
        // _imageCapabilityLock FIRST, and this is the whole reason capability admission is
        // atomic (Codex diff r4). Two earlier attempts failed because they only excluded
        // competing PUBLISHERS: the capability publisher holds the image lock across its epoch
        // check and its write, but the INVALIDATOR needed only the model lock, so the epoch could
        // still move between that check and that write and a stale snapshot got persisted. Taking
        // the image lock here freezes the epoch for the duration of any publication's critical
        // section. Direction stays image → model, matching EnsureImageCapabilitiesAsync and the
        // admit predicate; a model → image path anywhere would close the cycle. Safe to hold
        // here because this body is dictionary work only — no settings write, no user code.
        lock (_imageCapabilityLock)
        lock (_modelListLock)
        {
            foreach (var key in _modelListCache.Keys.Where(k => k.Item1 == provider).ToList())
                _modelListCache.Remove(key);
            // Also unregister that provider's IN-FLIGHT background refreshes (Kimi diff r3): the
            // epoch bump below guarantees they will store nothing, so leaving them registered
            // makes the next dialog open JOIN a refresh that is already dead — one open cycle
            // with no live revalidation. The refresh's own finally-Remove is then a no-op, and a
            // dropped task is never awaited by anyone but its own continuation.
            foreach (var key in _modelListRefreshes.Keys.Where(k => k.Item1 == provider).ToList())
                _modelListRefreshes.Remove(key);
            _modelListEpochs[provider] = ModelListEpoch(provider) + 1;
        }
    }

    /// <summary>Caller must hold <see cref="_modelListLock"/>.</summary>
    private int ModelListEpoch(AIProvider provider)
        => _modelListEpochs.TryGetValue(provider, out var epoch) ? epoch : 0;

    /// <summary>
    /// Takes <see cref="_modelListLock"/> itself. Called as the <c>admit</c> predicate of
    /// <see cref="CaptureImageCapabilities"/> — i.e. while <see cref="_imageCapabilityLock"/> is
    /// held — so this is the image→model lock direction; see that method's remarks for why the
    /// reverse must never appear.
    /// </summary>
    private bool ModelListEpochIsCurrent(AIProvider provider, int epochAtStart)
    {
        lock (_modelListLock)
            return epochAtStart == ModelListEpoch(provider);
    }

    /// <summary>
    /// ENH-3 offline preflight: with no network interface up, a provider call can
    /// only burn retry/backoff time before failing. Runs AFTER local validation
    /// (no-key / unsupported-model checks) so offline never masks deterministic
    /// config errors on the paths that have them — and <c>GetBaseUrl()</c> is
    /// evaluated here for the same reason: an invalid custom base URL throws its
    /// own InvalidOperationException instead of "No network connection". Loopback
    /// endpoints (e.g. a local Ollama at http://localhost:11434) are reachable
    /// without any network interface, so they skip the probe entirely. Static
    /// message: log-safe and short enough (21 chars) to fit the 55-char pill cap
    /// under both artifact-naming suffixes (" — pasted transcription" /
    /// " — transcription on clipboard").
    /// </summary>
    private void ThrowIfOffline(AIProviderConfig config)
    {
        if (Uri.TryCreate(config.GetBaseUrl(), UriKind.Absolute, out var uri) && uri.IsLoopback)
            return;
        if (!_networkAvailable())
            throw new HttpRequestException("No network connection");
    }

    public bool IsEnabled
    {
        get => _settings.GetBool(AppDefaults.AiEnhancementEnabled, false);
        set => _settings.SetBool(AppDefaults.AiEnhancementEnabled, value);
    }

    /// <summary>Provider for text enhancement.</summary>
    public AIProvider SelectedProvider
    {
        get => ReadProviderSetting(AppDefaults.AiProvider);
        set => _settings.SetString(AppDefaults.AiProvider, value.ToString());
    }

    /// <summary>Provider for image generation. Defaults to OpenAI.</summary>
    public AIProvider SelectedImageProvider
    {
        get => ReadProviderSetting(AppDefaults.AiImageProvider);
        set => _settings.SetString(AppDefaults.AiImageProvider, value.ToString());
    }

    private AIProvider ReadProviderSetting(string key)
    {
        var raw = _settings.GetString(key, "");
        if (Enum.TryParse<AIProvider>(raw, out var p))
            return p;

        Logger.Warning("Setting '{Key}'='{Value}' failed to parse as AIProvider, defaulting to {Default}",
            key, raw, AIProvider.OpenAI);
        return AIProvider.OpenAI;
    }

    /// <summary>Text enhancement model configured for a SPECIFIC provider (per-provider persisted).</summary>
    private string TextModelFor(AIProvider provider) => _settings.GetString(AppDefaults.AiModelKey(provider), "");

    /// <summary>Text enhancement model (per-provider).</summary>
    public string SelectedModel
    {
        get => TextModelFor(SelectedProvider);
        set => _settings.SetString(AppDefaults.AiModelKey(SelectedProvider), value);
    }

    /// <summary>
    /// Default image model for a provider, WITH the correct provider-slug prefix (OpenRouter needs
    /// <c>openai/gpt-image-2</c>, not the bare <c>gpt-image-2</c> the descriptor advertises — the bare
    /// slug 404s at OpenRouter). <see cref="ImageOptions.DefaultModelFor"/> is the single source of
    /// truth; fall back to the descriptor for any provider it doesn't enumerate.
    /// </summary>
    private string DefaultImageModelFor(AIProvider provider) =>
        ImageOptions.DefaultModelFor(provider) ?? _providers.Get(provider).DefaultImageModel;

    /// <summary>Image model configured for a SPECIFIC provider (per-provider persisted; slug-correct default).</summary>
    private string ImageModelFor(AIProvider provider) =>
        _settings.GetString(AppDefaults.AiImageModelKey(provider), DefaultImageModelFor(provider));

    /// <summary>Image generation model (per-image-provider). Defaults come from <see cref="DefaultImageModelFor"/>.</summary>
    public string SelectedImageModel
    {
        get => ImageModelFor(SelectedImageProvider);
        set => _settings.SetString(
            AppDefaults.AiImageModelKey(SelectedImageProvider),
            value);
    }

    /// <summary>
    /// Raw persisted model for a SPECIFIC provider — null when the user never chose one
    /// (no implicit-default substitution, unlike the <see cref="SelectedModel"/> /
    /// <see cref="SelectedImageModel"/> getters; a persisted empty/whitespace value also
    /// counts as "never chosen"). UX-1 needs the raw value twice: the options dialog's
    /// pre-select (persisted memory and provider default are SEPARATE precedence steps in
    /// <c>ProviderModelMemoryPolicy.NextDialogModel</c>) and its persist gate (an untouched
    /// pre-fill must not write; an adjusted re-pick of the current default must). Public so
    /// the dialog can be provider-aware for its currently-selected provider instead of
    /// reading the GLOBAL selection. (F17/UX-1)
    /// </summary>
    public string? PersistedModelFor(AIProvider provider, bool isImage)
    {
        var key = isImage ? AppDefaults.AiImageModelKey(provider) : AppDefaults.AiModelKey(provider);
        if (!_settings.Contains(key))
            return null;
        var value = _settings.GetString(key, "");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Persist the model choice for a SPECIFIC provider (UX-1) — the write-side twin of
    /// <see cref="PersistedModelFor"/>. Used by the options dialog's confirm path so a
    /// live-switched provider's choice lands in THAT provider's memory, never the globally
    /// selected provider's (which is what the <see cref="SelectedModel"/> /
    /// <see cref="SelectedImageModel"/> setters write).
    /// </summary>
    public void RememberModelFor(AIProvider provider, string model, bool isImage)
    {
        if (isImage)
            _settings.SetString(AppDefaults.AiImageModelKey(provider), model);
        else
            _settings.SetString(AppDefaults.AiModelKey(provider), model);
    }

    /// <summary>
    /// The effective model for a prompt: an explicit <c>ModelOverride</c>, else the model configured
    /// for the EFFECTIVE provider — the prompt's <c>ProviderOverride</c> if set, otherwise the global
    /// text/image provider. Resolving from the effective provider is what stops a provider-only
    /// override from sending the global provider's model id to a different provider (e.g. an OpenAI
    /// model id to OpenRouter/Anthropic → 404). Public so MainViewModel's picker/redo/label paths
    /// share this single resolution instead of re-deriving the global-provider model. (F6/F17)
    /// </summary>
    public string ResolveModelForPrompt(CustomPrompt? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt?.ModelOverride))
            return prompt.ModelOverride;

        var isImage = prompt?.IsImageGeneration == true;
        var provider = ParseProviderOverride(prompt?.ProviderOverride)
                       ?? (isImage ? SelectedImageProvider : SelectedProvider);
        return isImage ? ImageModelFor(provider) : TextModelFor(provider);
    }

    private string ResolveModel(CustomPrompt? prompt) => ResolveModelForPrompt(prompt);

    /// <summary>
    /// IMG-BG: the exact NONBLANK IMAGE model the generation paths will use for
    /// <paramref name="provider"/> — an explicit prompt override wins, else the provider's
    /// persisted image model, else the provider default (the same blank fallback
    /// <c>GenerateImage*Async</c> applies internally). <paramref name="provider"/> is
    /// AUTHORITATIVE for the lookup (Codex diff round 4): routing a null/odd prompt through
    /// <see cref="ResolveModelForPrompt"/> would resolve the TEXT model — a chat model id in
    /// an image request. The background job captures this at dispatch and passes it as the
    /// explicit generation model, so History labels and redo metadata can never disagree with
    /// the actual request.
    /// </summary>
    public string ResolveEffectiveImageModel(CustomPrompt? prompt, AIProvider provider)
        => ResolveEffectiveImageModelWithSource(prompt, provider).Model;

    /// <summary>
    /// The same resolution, carrying WHERE the model came from. This is the only place that knows —
    /// by the time the background job runs, the model is a bare string — and
    /// <see cref="Helpers.ImageModelGate"/> needs the provenance to decide who may be healed.
    /// <para>A <c>ProviderOverride</c> with no <c>ModelOverride</c> deliberately reports
    /// <see cref="Helpers.ImageModelSource.PersistedProviderSetting"/>: it reads that provider's
    /// persisted key, so it stays healable. Derived through <see cref="PersistedModelFor"/> so an
    /// absent, blank, or already-healed key resolves as
    /// <see cref="Helpers.ImageModelSource.ProviderDefault"/> rather than a phantom setting.</para>
    /// </summary>
    internal Helpers.ImageModelResolution ResolveEffectiveImageModelWithSource(
        CustomPrompt? prompt, AIProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(prompt?.ModelOverride))
            return new Helpers.ImageModelResolution(prompt.ModelOverride, Helpers.ImageModelSource.PromptOverride);

        var persisted = PersistedModelFor(provider, isImage: true);
        return persisted is not null
            ? new Helpers.ImageModelResolution(persisted, Helpers.ImageModelSource.PersistedProviderSetting)
            : new Helpers.ImageModelResolution(DefaultImageModelFor(provider), Helpers.ImageModelSource.ProviderDefault);
    }

    // ─────────────────────────── IMG-5: live image-model capabilities ───────────────────────────

    /// <summary>
    /// Settings key holding the last successful OpenRouter image-capability snapshot.
    ///
    /// <para><b>Deliberately NOT registered in <c>AppDefaults.Defaults</c></b> — this is a refetchable
    /// CACHE, not a preference. Staying out of Defaults is what excludes it from settings
    /// export/import for free, because <c>ImportExportService.IsImportableKey</c> requires Defaults
    /// membership; importing another machine's catalog snapshot could only mislead. The cost of that
    /// choice is that Settings → "Reset all settings" is an ENUMERATED reset which an unregistered
    /// key SURVIVES, so <c>SettingsViewModel.ResetAllSettings</c> removes this key explicitly
    /// (precedent: <c>LastUpdateCheckUtc</c>, flagged for exactly this omission on 2026-05-20).
    /// GDPR delete-all wipes settings.json wholesale, so that path needs nothing. The GDPR EXPORT
    /// redacts only <c>IsSensitiveKey</c> values, so the snapshot appears verbatim there — accepted:
    /// it is public catalog data, identical for every user.</para>
    /// </summary>
    internal const string ImageCapabilitiesKey = "imageModelCapabilities";

    private readonly object _imageCapabilityLock = new();
    private Dictionary<string, Helpers.ImageModelCapabilities>? _imageCapabilities;
    /// <summary>
    /// The exact settings value <see cref="_imageCapabilities"/> was parsed from. Re-read (cheaply —
    /// SettingsService is an in-memory cache) on every lookup and compared, so the parse happens
    /// once per DISTINCT value rather than once per session.
    ///
    /// <para>This is also what makes "Reset all settings" take effect immediately: the service is a
    /// DI SINGLETON, so a latched cache would keep answering from the removed snapshot until the
    /// next restart or fetch (Codex diff review). Keying on the raw value means any external change
    /// — reset, GDPR wipe, a hand-edited settings.json — self-heals with no extra plumbing and no
    /// new constructor dependency in SettingsViewModel.</para>
    /// </summary>
    private string? _imageCapabilitiesRaw;
    private bool _imageCapabilitiesPresent;

    /// <summary>
    /// IMG-12: the loaded snapshot contains at least one entry written in the PRE-IMG-12 boolean
    /// <c>"q"</c> shape, so its quality evidence is UNKNOWN rather than merely unsupported.
    ///
    /// <para><b>It exists because "present" is not the same question as "current" (Codex diff r1).</b>
    /// A legacy <c>q: true</c> reads as unknown — correctly, since the boolean recorded no values —
    /// and the static rule then answers "no quality knob" for every non-OpenAI OpenRouter model. On
    /// the no-dialog path (<c>AskImageSize</c> off) nothing would ever refetch over it, because
    /// <see cref="EnsureImageCapabilitiesAsync"/> early-returns while a snapshot is present. An
    /// upgraded install with a cached grok entry and a saved Standard would silently stop sending a
    /// tier that model accepts, across restarts. Treating legacy-shaped as not-yet-hydrated buys
    /// exactly one fail-soft catalog fetch, after which the map is rewritten in the array shape and
    /// this flag clears by itself.</para>
    /// </summary>
    private bool _imageCapabilitiesLegacyShape;

    /// <summary>
    /// Commit a fetch's capability snapshot, if it produced one.
    ///
    /// <para><b>Null means "no snapshot from this fetch" and PRESERVES the last-good cache</b> — a
    /// failed fetch, a non-image query, or a 200 whose <c>data</c> was missing/not an array. Only a
    /// structurally valid catalog replaces the cache, and it replaces it WHOLE (never merged):
    /// concurrent fetches — a key-save validation racing the dialog's refresh — would otherwise
    /// interleave into a snapshot matching no real catalog, and a merge could resurrect a model
    /// OpenRouter has removed.</para>
    ///
    /// <para><paramml name="admit"/> is IMG-10b's staleness guard, and it is evaluated INSIDE the
    /// publication lock so that check-and-publish is ONE linearizable step (Codex diff r3 —
    /// checking outside and publishing after is a TOCTOU race whose window is a thread suspension,
    /// i.e. unbounded, not the "microseconds" an earlier revision of this claimed). A returning-false
    /// admit publishes nothing at all. Callers that have no version to check pass null.</para>
    ///
    /// <para><b>Lock direction is load-bearing:</b> <paramref name="admit"/> runs while
    /// <see cref="_imageCapabilityLock"/> is held and itself takes <c>_modelListLock</c> — the
    /// image→model direction, which is the one <see cref="EnsureImageCapabilitiesAsync"/> already
    /// establishes (its <c>??=</c> starts the async body, whose synchronous prefix captures the
    /// model-list epoch, while holding the image lock). NOTHING may take those two in the opposite
    /// order; a model→image path would close the cycle.</para>
    /// </summary>
    internal void CaptureImageCapabilities(Providers.ProviderModelList fetched, Func<bool>? admit = null)
    {
        if (fetched.ImageCapabilities is not { } snapshot)
            return;

        // Serialize BEFORE taking the lock, then publish memory + settings together under it, so a
        // concurrent capture cannot leave a newer map in memory beside an older one on disk
        // (Codex diff review). The raw value doubles as the in-memory cache's identity.
        string raw;
        try
        {
            raw = SerializeCapabilities(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Warning("Failed to serialize image capabilities: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            return;
        }

        lock (_imageCapabilityLock)
        {
            // Atomic with the publication below: any competing publisher must hold this same
            // lock, so a fetch whose version went stale can neither pass this check nor slip a
            // write in after a fresher one committed.
            if (admit != null && !admit())
                return;

            try
            {
                _settings.SetString(ImageCapabilitiesKey, raw);
            }
            catch (Exception ex)
            {
                // Fail soft: a refetchable cache must never fail a model refresh. Leave the
                // in-memory state alone too, so memory and settings stay in agreement.
                Logger.Warning("Failed to persist image capabilities: {ErrorType}: {ErrorMessage}",
                    ex.GetType().Name, ex.Message);
                return;
            }

            _imageCapabilities = new Dictionary<string, Helpers.ImageModelCapabilities>(
                snapshot, StringComparer.OrdinalIgnoreCase);
            _imageCapabilitiesRaw = raw;
            _imageCapabilitiesPresent = true;
        }

        Logger.Information("Image capabilities cached: {Count} models", snapshot.Count);
    }

    /// <summary>
    /// What <paramref name="model"/> publishes, for the option-gating and normalization paths.
    ///
    /// <para>Three-way, and the distinction is load-bearing (both 2026-08-01 final reviews flagged
    /// an earlier design that collapsed it): <b>null</b> = "no catalog governs this provider, use
    /// <see cref="Helpers.ImageOptions"/>'s static rules" — correct for OpenAI-direct and
    /// Gemini-direct, which never appear in this catalog and whose static tables are docs-verified.
    /// A <b>populated instance</b> = catalog evidence. <see cref="Helpers.ImageModelCapabilities.AllAuto"/>
    /// = an OpenRouter model that a snapshot we HOLD does not list: offer and send nothing but Auto,
    /// which can never provoke a rejection. <b>No snapshot at all is the null case, not this one</b>
    /// — an absent model in a held catalog is positive evidence, whereas no catalog is no evidence
    /// (conflating them stripped every OpenRouter request's options on a fresh install).</para>
    ///
    /// <para>Lookup strips OpenRouter routing suffixes (<c>:nitro</c>, <c>:floor</c>) via the same
    /// <c>Bare</c> rule the id classifiers use, so a suffixed alias keeps its capabilities, and is
    /// case-insensitive on both the cached keys and the probe.</para>
    /// </summary>
    public Helpers.ImageModelCapabilities? ImageCapabilitiesFor(AIProvider provider, string? model)
    {
        if (provider != AIProvider.OpenRouter)
            return null;

        var (present, _, map) = LoadImageCapabilities();

        // NO SNAPSHOT AT ALL ⇒ static rules, not all-Auto. These are two different states and
        // collapsing them was a real regression (caught by the OpenRouter wire tests): with no
        // catalog yet, all-Auto would strip `resolution`/`aspect_ratio` from every request until
        // the first fetch — so a fresh install silently stopped honouring a user's saved 2K on
        // seedream. Codex's all-Auto argument is about an UNKNOWN MODEL — one absent from a
        // catalog we DO have, where we have positive evidence the catalog does not list it. With
        // no catalog we have no evidence at all, and the docs-verified static tables are the
        // better answer. A blank model is the same case: the static path substitutes the
        // provider's default and answers for that.
        //
        // PRESENCE, not emptiness (Codex diff review): a well-formed `{"data":[]}` is a real
        // snapshot that happens to list nothing, and keying on Count would read it as "no
        // catalog" and re-enable the permissive static offers this change exists to remove.
        if (!present || string.IsNullOrWhiteSpace(model))
            return null;

        if (map.TryGetValue(model!, out var exact))
            return exact;

        // A BARE id (no vendor prefix) deliberately does NOT match — it falls to AllAuto rather
        // than being resolved by suffix. Catalog keys are vendor-qualified, and matching on the
        // tail alone would be ambiguous (two vendors can both publish a `gemini-…`), so a wrong
        // capability set is a worse answer than none. Bare ids are not a real path anyway:
        // persisted and default OpenRouter models are always slugs, and a bare id would 404 at the
        // provider. Do not "fix" this into a suffix match.
        //
        // Strip ONLY the routing variant (`:nitro`, `:floor`), never the vendor prefix.
        // ImageOptions.Bare removes both — that is what lets IsGptImage2 match
        // "openai/gpt-image-2" — but catalog keys ARE vendor-qualified, so Bare would turn
        // "krea/krea-2-large:nitro" into "krea-2-large" and match nothing.
        var colon = model!.IndexOf(':');
        if (colon > 0 && map.TryGetValue(model[..colon], out var stripped))
            return stripped;

        return Helpers.ImageModelCapabilities.AllAuto;
    }

    private Task? _imageCapabilityHydration;

    /// <summary>
    /// Ensure an OpenRouter capability snapshot exists before a generation reads it.
    ///
    /// <para><b>Why generation cannot rely on the dialog</b> (Codex diff r4): the capture rides the
    /// model-list fetch, and with <c>AskImageSize</c> off the whole picker is skipped — so a fresh
    /// install, or a "Reset all settings" (which removes the key by design), would fall to the
    /// static rules on EVERY generation and keep sending krea <c>4K</c>/<c>21:9</c> indefinitely.
    /// That is not a regression — it is exactly the pre-IMG-5 behaviour — but it leaves the card's
    /// goal unmet on the commonest no-dialog path.</para>
    ///
    /// <para>SINGLE-FLIGHT and awaited: a batch launches N generations at once and must not fire N
    /// catalog fetches, and the fetch is a ~29 KB public GET against a request that already takes
    /// tens of seconds, so waiting for it is cheap enough to buy a correct FIRST generation rather
    /// than only correct subsequent ones. FAIL-SOFT: any failure leaves the snapshot absent and the
    /// static rules answer, exactly as before — hydration may never turn a working generation into
    /// a failed one. Non-OpenRouter providers and an already-present snapshot return immediately.</para>
    /// </summary>
    internal Task EnsureImageCapabilitiesAsync(AIProvider provider, CancellationToken ct = default)
    {
        if (provider != AIProvider.OpenRouter) return Task.CompletedTask;

        // PRESENT is not CURRENT (IMG-12, Codex diff r1): a snapshot carrying the pre-IMG-12
        // boolean `"q"` records no quality VALUES, so every non-OpenAI model in it answers "no
        // quality knob" from the static rule. Without this clause nothing on the no-dialog path
        // would ever refetch, and an upgraded install would stop sending a tier its model accepts —
        // silently, across restarts. One fail-soft fetch rewrites the map in the array shape.
        var (present, legacyShape, _) = LoadImageCapabilities();
        if (present && !legacyShape) return Task.CompletedTask;

        lock (_imageCapabilityLock)
        {
            // A completed attempt is not retried within the session: a provider outage must not
            // add a fetch to every generation. The next successful model refresh still populates.
            _imageCapabilityHydration ??= HydrateImageCapabilitiesAsync(ct);
            return _imageCapabilityHydration;
        }
    }

    private async Task HydrateImageCapabilitiesAsync(CancellationToken ct)
    {
        try
        {
            await TryFetchAvailableModelsAsync(
                Providers.ModelCatalogQuery.Image,
                providerOverride: AIProvider.OpenRouter,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning("Image capability hydration failed: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Lazily read the persisted snapshot. <b>Fails closed to empty</b> on any absence, parse
    /// failure, or shape drift across app versions — the dialog opens on this path, so a corrupt
    /// cache must degrade to all-Auto, never throw. The failure is latched, so a bad value is
    /// parsed once per session rather than on every gating call.
    /// </summary>
    private (bool Present, bool LegacyShape, Dictionary<string, Helpers.ImageModelCapabilities> Map)
        LoadImageCapabilities()
    {
        lock (_imageCapabilityLock)
        {
            var raw = _settings.GetString(ImageCapabilitiesKey, "");

            if (_imageCapabilities is not null &&
                string.Equals(_imageCapabilitiesRaw, raw, StringComparison.Ordinal))
            {
                return (_imageCapabilitiesPresent, _imageCapabilitiesLegacyShape, _imageCapabilities);
            }

            _imageCapabilitiesRaw = raw;
            _imageCapabilities = new Dictionary<string, Helpers.ImageModelCapabilities>(
                StringComparer.OrdinalIgnoreCase);
            _imageCapabilitiesPresent = false;
            _imageCapabilitiesLegacyShape = false;

            if (string.IsNullOrWhiteSpace(raw))
                return (false, false, _imageCapabilities);

            try
            {
                DeserializeCapabilities(raw, _imageCapabilities, out _imageCapabilitiesLegacyShape);
                // Zero SURVIVING entries ⇒ absent, mirroring the fetch path (Codex diff r4). A
                // valid object like {"model":"bad-shape"} parses without throwing but yields
                // nothing, and an empty map is authoritative — it would push every OpenRouter
                // model to all-Auto. We never persist an empty map (the fetch nulls those), so an
                // empty parse here means corruption or version drift, not "the catalog is empty".
                _imageCapabilitiesPresent = _imageCapabilities.Count > 0;
            }
            catch (Exception ex)
            {
                _imageCapabilities.Clear();
                _imageCapabilitiesPresent = false;
                _imageCapabilitiesLegacyShape = false;
                Logger.Warning("Image capability cache unreadable, treating as absent: {ErrorType}",
                    ex.GetType().Name);
            }

            return (_imageCapabilitiesPresent, _imageCapabilitiesLegacyShape, _imageCapabilities);
        }
    }

    /// <summary>
    /// Compact on-disk shape — <c>{"vendor/model":{"a":[…aspects],"t":[…tiers],"q":[…qualities],"r":int}}</c>.
    /// Short names because this lands in the user's <c>settings.json</c> beside hand-editable
    /// preferences; ~34 models stay a few KB.
    /// </summary>
    private static string SerializeCapabilities(
        IReadOnlyDictionary<string, Helpers.ImageModelCapabilities> snapshot)
    {
        var buffer = new global::System.IO.MemoryStream();
        using (var writer = new global::System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (id, caps) in snapshot)
            {
                // Tri-state rides the KEY's presence: an omitted "a"/"t"/"q" round-trips as
                // UNKNOWN, an empty array as authoritative unsupported.
                writer.WriteStartObject(id);
                if (caps.Aspects is { } aspects)
                {
                    writer.WriteStartArray("a");
                    foreach (var aspect in aspects) writer.WriteStringValue(aspect);
                    writer.WriteEndArray();
                }
                if (caps.Tiers is { } tiers)
                {
                    writer.WriteStartArray("t");
                    foreach (var tier in tiers) writer.WriteStringValue(tier);
                    writer.WriteEndArray();
                }
                // IMG-12: "q" became an ARRAY of quality tags here (it was a bool). The READ side
                // still accepts the boolean form — see DeserializeCapabilities.
                if (caps.Quality is { } quality)
                {
                    writer.WriteStartArray("q");
                    foreach (var tag in quality) writer.WriteStringValue(tag);
                    writer.WriteEndArray();
                }
                if (caps.MaxReferences is { } max) writer.WriteNumber("r", max);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return global::System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Inverse of <see cref="SerializeCapabilities"/>, tolerant per ENTRY: a row of the wrong shape
    /// is skipped rather than throwing, so one drifted entry cannot discard an otherwise good
    /// snapshot. A malformed DOCUMENT throws and is caught by the caller as "cache absent".
    /// </summary>
    private static void DeserializeCapabilities(
        string raw, Dictionary<string, Helpers.ImageModelCapabilities> into, out bool legacyShape)
    {
        legacyShape = false;
        using var doc = global::System.Text.Json.JsonDocument.Parse(raw);

        // A JSON array or scalar PARSES but is not a snapshot. Returning quietly would latch
        // "snapshot present" over an empty map, which reads downstream as "the catalog lists
        // nothing" (⇒ all-Auto for every model) rather than "no cache" (⇒ static rules).
        // Throwing routes it through the caller's fail-closed catch, which is the honest answer.
        if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object)
            throw new InvalidOperationException("Image capability cache root is not an object");

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != global::System.Text.Json.JsonValueKind.Object) continue;

            // Any surviving entry in the pre-IMG-12 boolean shape marks the whole snapshot stale —
            // see the _imageCapabilitiesLegacyShape field for why "present" then must not mean
            // "no need to fetch". Checked per entry because a partially-rewritten file is possible.
            if (entry.Value.TryGetProperty("q", out var legacyProbe) &&
                legacyProbe.ValueKind is global::System.Text.Json.JsonValueKind.True
                    or global::System.Text.Json.JsonValueKind.False)
            {
                legacyShape = true;
            }

            into[entry.Name] = new Helpers.ImageModelCapabilities(
                ReadCachedArray(entry.Value, "a", Helpers.ImageOptions.AllKnownAspects),
                ReadCachedArray(entry.Value, "t", Helpers.ImageOptions.AllTiers),
                ReadCachedQuality(entry.Value),
                entry.Value.TryGetProperty("r", out var r) &&
                    r.ValueKind == global::System.Text.Json.JsonValueKind.Number &&
                    r.TryGetInt32(out var max) ? max : null);
        }
    }

    /// <summary>
    /// Absent key ⇒ null (unknown). Present-but-not-an-array ⇒ null too (drift, never a silent
    /// "supports nothing"). Values are re-validated against <paramref name="vocabulary"/>: the
    /// cache lives in the user's editable settings.json and was written by a possibly older build,
    /// so a tag the app cannot render must not reach a combo or the wire.
    /// </summary>
    private static IReadOnlyList<string>? ReadCachedArray(
        global::System.Text.Json.JsonElement entry, string name, string[] vocabulary)
    {
        if (!entry.TryGetProperty(name, out var array)) return null;
        if (array.ValueKind != global::System.Text.Json.JsonValueKind.Array) return null;

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != global::System.Text.Json.JsonValueKind.String) continue;
            if (item.GetString() is not string s) continue;
            foreach (var known in vocabulary)
            {
                if (string.Equals(known, s, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(known);
                    break;
                }
            }
        }
        return values;
    }

    /// <summary>
    /// The quality field, reading BOTH the current array shape and the pre-IMG-12 boolean one.
    ///
    /// <para><b>The mapping is what the old snapshot actually knew.</b> <c>false</c> translates
    /// EXACTLY — it meant "publishes no quality", which is the empty list. <c>true</c> meant
    /// "publishes quality, values unrecorded", which is UNKNOWN, not all three: expanding it would
    /// re-send <c>high</c> to a model that never published it, reintroducing the exact defect
    /// IMG-12 fixes, from cache. Unknown falls back to the static rule for this field alone.</para>
    ///
    /// <para><b>How long that fallback lasts is NOT bounded, and an earlier version of this comment
    /// claimed it was</b> ("one dialog open long" — self-review, 2026-08-13).
    /// <see cref="EnsureImageCapabilitiesAsync"/> early-returns while a snapshot is PRESENT, and a
    /// legacy one is present, so nothing refetches over it on the no-dialog path
    /// (<c>AskImageSize</c> off, AI Enhancement page never opened) — the legacy entry can survive
    /// restarts indefinitely. The consequence is only that a legacy-cached model offers no quality
    /// knob until something refreshes the catalog: fail-closed, never a new rejection. Same shape as
    /// the recorded [[IMG-11]] window, and not worth a second mechanism.</para>
    ///
    /// <para>The key stayed <c>"q"</c> because it is the same field. A new key would leave the old
    /// one orphaned in the user's settings.json and would throw away the exact <c>false</c>
    /// translation, buying nothing: an absent key already reads as unknown either way.</para>
    /// </summary>
    private static IReadOnlyList<string>? ReadCachedQuality(global::System.Text.Json.JsonElement entry)
    {
        if (entry.TryGetProperty("q", out var q) &&
            q.ValueKind == global::System.Text.Json.JsonValueKind.False)
        {
            return global::System.Array.Empty<string>();
        }

        // Array ⇒ parsed and re-validated; legacy `true`, any other kind, and an absent key all
        // fall out of ReadCachedArray as null (unknown).
        return ReadCachedArray(entry, "q", Helpers.ImageOptions.AllQualities);
    }

    /// <summary>
    /// Clear the per-provider persisted IMAGE model (the heal side of
    /// <see cref="Helpers.ImageModelGate"/>). Distinct from the <see cref="SelectedImageModel"/>
    /// setter, which only ever writes the CURRENTLY selected provider — a gate refusal must clear the
    /// key belonging to the provider that was actually resolved.
    /// </summary>
    internal void ClearPersistedImageModel(AIProvider provider)
        => _settings.SetString(AppDefaults.AiImageModelKey(provider), "");

    private static AIProvider? ParseProviderOverride(string? value)
        => !string.IsNullOrWhiteSpace(value) && Enum.TryParse<AIProvider>(value, out var p) ? p : null;

    public async Task<string> EnhanceAsync(string transcribedText, CustomPrompt? prompt = null, IReadOnlyList<string>? vocabularyTerms = null, CancellationToken ct = default)
    {
        // ENH-12: model-configuration failures THROW instead of silently returning the input.
        // The 2026-07-30 incident: a blocklisted model selection was refused here, the self-heal
        // wiped it, and 13 minutes of dictations pasted raw while the caller logged "Transcription
        // enhanced" — the caller cannot distinguish a pass-through from a success, so the silent
        // return was a lie by omission. The throws ride MainViewModel's existing enhancement
        // catch (same route as a thrown no-key/offline failure): raw text still pastes, history
        // stores the [Enhancement failed] marker, and the pill states the cause. Messages are
        // composition-safe app copy — ComposeFallbackFailureStatus renders "{reason} — {outcome}"
        // with the outcome never truncated, leaving a 26-unit worst-case reason budget.
        var model = ResolveModel(prompt);
        if (string.IsNullOrWhiteSpace(model))
        {
            Logger.Warning("AI Enhancement skipped: no model selected for {Provider}", SelectedProvider);
            throw new InvalidOperationException("No AI model selected");
        }

        // Guard against stale settings pointing to non-chat models (e.g. classifiers, TTS)
        if (!ModelDisplayPolicy.IsChatModel(model))
        {
            // Only self-heal the GLOBAL selection when THIS model actually came from it — a stale
            // non-chat model resolved through a prompt's provider/model override is not the global
            // setting's fault, and clearing SelectedModel would mutate the wrong owner (F17 review).
            var fromGlobalSelection = string.IsNullOrWhiteSpace(prompt?.ModelOverride)
                                      && string.IsNullOrWhiteSpace(prompt?.ProviderOverride);
            Logger.Warning("AI Enhancement skipped: model '{Model}' is not a chat model{Cleared}",
                model, fromGlobalSelection ? " — clearing global selection" : "");
            if (fromGlobalSelection)
                SelectedModel = "";
            // Source-neutral copy on the non-global branch: it covers BOTH a prompt ModelOverride
            // and a provider-only override resolving that provider's persisted model — naming
            // "this prompt" would be wrong for the latter (Codex plan review R2).
            throw new InvalidOperationException(fromGlobalSelection ? "Model unusable; reset" : "Model unusable");
        }

        prompt ??= PredefinedPrompts.Default;

        // Both wire messages come from the single composition site (AIPrompts) so the
        // normal and redo paths can never drift apart. Pinned by captured-request
        // parity rows in AIEnhancementServiceTests.
        var systemPrompt = AIPrompts.BuildSystemPrompt(prompt.PromptText, vocabularyTerms);
        var userPrompt = AIPrompts.BuildUserPrompt(transcribedText);

        var providerOverride = ParseProviderOverride(prompt.ProviderOverride);
        Logger.Information("AI Enhancement: provider={Provider}, model={Model} userTerm={Prompt}",
            providerOverride ?? SelectedProvider, model,
            global::VoiceWink.Helpers.LogValueSanitizer.SingleLine(prompt.Title));

        try
        {
            var config = BuildConfig(model, providerOverride, prompt.ReasoningOverride);
            // Trace moved after BuildConfig (ENH-8) so the entry can carry the RESOLVED
            // reasoning directive; a BuildConfig throw (rare) now skips the entry.
            Helpers.PromptTraceLog.WriteSections(Helpers.PromptTraceOp.TextEnhancement,
                new Helpers.TraceMeta(Provider: (providerOverride ?? SelectedProvider).ToString(), Model: model),
                $"enhancement · {providerOverride ?? SelectedProvider} · {model} · {prompt.Title}",
                ("SYSTEM", systemPrompt),
                ("USER", userPrompt),
                ("REASONING", config.Reasoning.Describe()));
            ThrowIfOffline(config);
            var enhanced = await CallProviderWithDeadlineAsync(config, systemPrompt, userPrompt, ct).ConfigureAwait(false);
            // RAW provider output, before AIEnhancementOutputFilter — the trace records
            // provider-boundary truth on both directions of the wire.
            Helpers.PromptTraceLog.WriteOutput(Helpers.PromptTraceOp.EnhancementOutput,
                new Helpers.TraceMeta(Provider: (providerOverride ?? SelectedProvider).ToString(), Model: model),
                $"enhancement output · {providerOverride ?? SelectedProvider} · {model} · {prompt.Title}",
                enhanced);
            var filtered = AIEnhancementOutputFilter.Filter(enhanced);
            // Plain whitespace check, deliberately. A round of this change applied the content
            // rule here on the reasoning that model output is machine-authored — which is only
            // half true. The PROMPT is user-authored, and a custom prompt may legitimately ask
            // for a punctuation-only result (extract the punctuation, emit Morse, return an
            // emoticon). Rejecting those silently restores the original transcript and destroys
            // a correct answer — the same data-loss class this card set out to fix, one surface
            // over (Codex diff review r2). The comma incident is already stopped upstream, at
            // the machine/user boundary inside TextPipelineRunner, so nothing here needs it.
            return string.IsNullOrWhiteSpace(filtered) ? transcribedText : filtered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancel (stop-button skip / pipeline supersede) — a
            // deliberate user action, not a defect: never the Error branch below,
            // which would ship it to Sentry (ENH-3).
            Logger.Information("AI Enhancement cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Provider-shaped failures → Warning (message-only; the client sites logged
            // status+body, and the caller surfaces the failure to the user) so the Sentry
            // sub-logger (Error+) doesn't ship provider outages/DNS failures as events
            // (VOICEWINK-8). Unexpected exceptions keep Error + stack.
            // ENH-15 adds InvalidApiKeyFormatException to the provider-shaped list. Without it a
            // legacy malformed key logs Error on EVERY dictation, and Error+ ships to Sentry — an
            // event per utterance until the user re-enters the key, which is the quota burn this
            // classification exists to prevent. Its message is app-authored and carries no key
            // material, so it is safe in a log template. (The general gap — a raw FormatException
            // from any other cause still logs Error — is carded as ENH-16.)
            if (ex is HttpRequestException or InvalidOperationException or TimeoutException
                or Helpers.InvalidApiKeyFormatException)
                Logger.Warning("AI Enhancement failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
            else
                Logger.Error(ex, "AI Enhancement failed");
            throw;
        }
    }

    /// <summary>
    /// Build the per-request provider config. Deliberately NO caching (ENH-8): the DPAPI
    /// <c>GetApiKey</c> call — the only expensive step — ran unconditionally before the old
    /// one-entry cache's check, so the cache saved a single cheap allocation while carrying
    /// an unsynchronized two-field race on this singleton and a stale-reasoning-directive
    /// hazard. This is also the SINGLE production choke point for the ENH-8 DEBUG gate:
    /// a Release build never reads the per-prompt override, so a Debug-tested profile's
    /// persisted choice cannot change a Release request — it resolves the ENH-23 per-model
    /// dictation DEFAULT only (pinned by ReasoningReleaseGateTests, compiled only in Release).
    /// </summary>
    private AIProviderConfig BuildConfig(string model, AIProvider? providerOverride = null, string? reasoningChoice = null)
        => BuildConfig(model, providerOverride, reasoningChoice, candidateKey: null);

    /// <summary>
    /// ENH-17: the same config build, optionally against a CANDIDATE key that is not (yet) in
    /// storage.
    /// </summary>
    /// <remarks>
    /// <para><b>Private on purpose, and reachable through exactly one public method
    /// (<see cref="ValidateCandidateKeyAsync"/>).</b> An optional <c>keyOverride</c> on a public
    /// fetch method would be one argument away from misuse by every present and future caller, and
    /// optional arguments are invisible at call sites in review. Keeping it private makes
    /// "ordinary traffic's key comes only from storage" a property the compiler preserves rather
    /// than a convention — which matters here, because convention failed on this exact code five
    /// rounds running (Kimi, ENH-17 plan review).</para>
    ///
    /// <para>The candidate is a PARAMETER and never a field. A "pending validation key" property
    /// on this singleton would be the rollback reincarnated as shared mutable state.</para>
    /// </remarks>
    private AIProviderConfig BuildConfig(string model, AIProvider? providerOverride, string? reasoningChoice,
        string? candidateKey)
    {
        var provider = providerOverride ?? SelectedProvider;
        var providerKey = provider.ToString().ToLowerInvariant();
        // A candidate is validated as given. The stored-key guard below still applies to it, so a
        // malformed candidate is refused here exactly as a malformed stored key is.
        var apiKey = candidateKey ?? _apiKeys.GetApiKey(providerKey);
        var baseUrl = _settings.GetString($"{AppDefaults.AiBaseUrlPrefix}{providerKey}", "");

        // ENH-15: refuse a STORED key that cannot form a header, before any header is built.
        // Sited here because BuildConfig is the single choke point on BOTH the model-fetch and
        // the enhancement paths, so one guard covers both. An EMPTY key passes through
        // untouched — "no key set" is a legitimate state that callers handle by early return,
        // and throwing on it would break every not-yet-configured provider.
        if (!string.IsNullOrEmpty(apiKey))
        {
            var keyVerdict = Helpers.ApiKeyFormat.Validate(apiKey);
            if (keyVerdict != Helpers.ApiKeyFormatVerdict.Ok)
                throw new Helpers.InvalidApiKeyFormatException(provider.ToString(), keyVerdict);
        }

#if DEBUG
        var reasoning = Helpers.ReasoningEffortPolicy.Resolve(provider, model, reasoningChoice);
#else
        // ENH-23: the per-model dictation default applies in every build; only the per-prompt
        // override (ENH-8 phase A) stays Debug-only, so a Release build passes NO choice.
        var reasoning = Helpers.ReasoningEffortPolicy.Resolve(provider, model, rawChoice: null);
#endif

        return new AIProviderConfig
        {
            Provider = provider,
            ModelName = model,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Reasoning = reasoning
        };
    }

    private Task<string> CallProviderAsync(AIProviderConfig config, string systemPrompt, string userText, CancellationToken ct)
        => _providers.Get(config.Provider).EnhanceTextAsync(_httpFactory, config, systemPrompt, userText, ct);

    /// <summary>
    /// Text-enhancement provider call bounded by <see cref="_enhancementTimeout"/>
    /// (ENH-3). The guard is deliberately just "not the caller's ct": besides the
    /// deadline itself, any OTHER non-caller cancellation reaching here (e.g.
    /// HttpClient.Timeout, which cancels the handler-pipeline token, not this ct)
    /// is equally timeout-shaped — converting all of them keeps the generic
    /// catch-Exception branches (Error log → Sentry) free of stray OCEs.
    /// TimeoutException is already in the provider-shaped Warning set everywhere.
    /// </summary>
    private async Task<string> CallProviderWithDeadlineAsync(AIProviderConfig config, string systemPrompt, string userText, CancellationToken ct)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(_enhancementTimeout);
        try
        {
            return await CallProviderAsync(config, systemPrompt, userText, deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Enhancement timed out");
        }
    }

    /// <summary>Returns true if the provider supports image generation.</summary>
    internal bool IsImageCapableProvider(AIProvider provider) => _providers.SupportsImageGeneration(provider);

    /// <summary>
    /// Generate an image from a text description. Returns PNG image bytes.
    /// Routes to the provider descriptor's image generation client.
    /// With a <paramref name="reference"/> selection (ENH-6), the file is validated +
    /// read here (once, late) and rides the request per-provider (OpenAI edits /
    /// Gemini inline part / OpenRouter unified API).
    /// </summary>
    public async Task<ImageGenerationResult> GenerateImageAsync(
        string description,
        CustomPrompt? prompt = null,
        IReadOnlyList<Models.ReferenceImageSelection>? references = null,
        CancellationToken ct = default)
    {
        var imageProvider = ParseProviderOverride(prompt?.ProviderOverride) ?? SelectedImageProvider;
        var descriptor = _providers.Get(imageProvider);
        if (!descriptor.SupportsImageGeneration)
            throw new InvalidOperationException(
                $"Image generation is not supported by {imageProvider}. Switch to OpenAI, Gemini, or OpenRouter.");

        var model = ResolveModel(prompt);
        if (string.IsNullOrWhiteSpace(model))
            model = DefaultImageModelFor(imageProvider);

        EnsureModelStillSupported(model);

        var config = BuildConfig(model, imageProvider);
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new InvalidOperationException($"No API key configured for {imageProvider}");
        ThrowIfOffline(config);

        var referenceImages = await ReadReferenceImagesAsync(references, ct).ConfigureAwait(false);

        // IMG-2: normalize the requested options against the RESOLVED model's capabilities
        // BEFORE the request log line — the log, the provider request, the result's
        // Effective record, and (via it) History/redo all tell one consistent story.
        // IMG-5: with AskImageSize off there is no picker fetch, so this may be the first thing
        // that ever needs a snapshot. Single-flight + fail-soft (see EnsureImageCapabilitiesAsync).
        // Deliberately still AFTER the reference read: IMG-6 briefly hoisted it so a per-model count
        // gate could be resolved, which changed error precedence (a broken reference file no longer
        // failed first) and put a network fetch ahead of every local read. The count gate is no
        // longer per-model, so the original ordering stands.
        await EnsureImageCapabilitiesAsync(imageProvider, ct).ConfigureAwait(false);
        var effective = Helpers.ImageOptions.NormalizeForModel(
            imageProvider, model, prompt?.ImageAspect, prompt?.ImageSizeTier, prompt?.ImageQuality,
            ImageCapabilitiesFor(imageProvider, model));
        foreach (var adjustment in effective.Adjustments)
            Logger.Information("Image option adjusted for {Model}: {Adjustment}", model, adjustment);
        var referencesDescription = ReferenceLogFormat.Describe(referenceImages);
        Logger.Information("Image generation: provider={Provider}, model={Model}, aspect={Aspect}, tier={Tier}, quality={Quality}, references={References}, descriptionLength={DescriptionLength}",
            imageProvider, model, effective.Aspect ?? "auto", effective.SizeTier ?? "auto", effective.Quality ?? "auto",
            referencesDescription, description.Length);
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.ImageGeneration,
            new Helpers.TraceMeta(Provider: imageProvider.ToString(), Model: model),
            $"image generation · {imageProvider} · {model}",
            ("prompt", description),
            ("aspect", effective.Aspect ?? "auto"),
            ("size tier", effective.SizeTier ?? "auto"),
            ("quality", effective.Quality ?? "auto"),
            ("references", referencesDescription));

        var bytes = await descriptor.GenerateImageAsync(
            _httpFactory, config, description, effective.Aspect, effective.SizeTier, effective.Quality, referenceImages, ct).ConfigureAwait(false);
        return ClassifyGeneratedImage(bytes, referenceImages, effective);
    }

    /// <summary>
    /// The ONE place a generated payload becomes an <see cref="ImageGenerationResult"/>: classify the
    /// bytes once, here, and carry the answer downstream so the saver names the file truthfully.
    ///
    /// <para><b>Why the service and not each client:</b> <c>ImageGenerationClient</c> has
    /// <c>RejectUnstorableFormat</c> but <c>GeminiImageClient</c> sniffs nothing at all — it trusts
    /// the declared mime. Classifying per client left Gemini writing WebP bytes into <c>.png</c>
    /// names and its SVG unrejected. This boundary sits above both, so all three provider routes get
    /// one answer. Both <see cref="ImageGenerationResult"/> construction sites call this; adding a
    /// third without it would silently reopen that gap.</para>
    ///
    /// <para>SVG stays rejected here as well as at dispatch — the client check is retained as
    /// defence in depth, and there is no drift risk because both layers ask the same helper.</para>
    /// </summary>
    internal static ImageGenerationResult ClassifyGeneratedImage(
        byte[] bytes,
        IReadOnlyList<ReferenceImage> referenceImages,
        Helpers.EffectiveImageOptions effective)
    {
        var kind = Helpers.ImageBytesFormat.Sniff(bytes);
        if (kind == Helpers.ImageBytesKind.Svg)
            throw new InvalidOperationException(Helpers.ImageBytesFormat.SvgRejectedMessage);

        // Numbers and a classification only — never payload bytes, in any encoding. A hex prefix
        // would be reversible content and these logs feed Sentry breadcrumbs.
        Log.Information("Generated image classified: {Kind} ({Size} bytes)", kind, bytes.Length);
        return new ImageGenerationResult(bytes, referenceImages, effective) { Kind = kind };
    }

    /// <summary>
    /// ENH-6: the ONE site that turns a <see cref="Models.ReferenceImageSelection"/>
    /// into provider-ready bytes. Enforces the per-origin trust boundary (AppImages
    /// must re-validate MediaPathPolicy containment at READ time — never trust a
    /// selection made earlier), the extension/mime whitelist, and the size cap via
    /// stream length BEFORE reading. Thrown messages carry NO path text (Browse
    /// paths must not leak into logs/Sentry); logs carry only mime + size.
    /// ASYNC-ONLY since the Codex post-#167 audit: the sync read ran pre-first-await
    /// on the caller (UI) thread, where a cloud-placeholder hydration of a 50 MB
    /// Browse pick froze the app for its whole download. The open runs off the caller
    /// thread (narrow claim: a QUEUED open is not started once <paramref name="ct"/>
    /// is cancelled; an open already blocking inside the OS cannot be interrupted),
    /// and the read is genuinely cancellable (async handle + ReadExactlyAsync).
    /// A matching OperationCanceledException propagates unwrapped — a cancelled
    /// pipeline must exit through its cancel path.
    /// </summary>
    internal static Task<ReferenceImage?> ReadReferenceImageAsync(
        Models.ReferenceImageSelection? selection, CancellationToken ct = default)
        => ReadReferenceImageAsync(selection, Helpers.AppPaths.ImagesDir, Helpers.AppPaths.ReferencesDir, ct);

    /// <summary>
    /// ENH-6f: the multi-selection read — count cap first, then sequential per-item
    /// reads through the single-item gate with a RUNNING aggregate budget
    /// (<c>ReferenceImagePolicy.MaxTotalBytes</c>, checked pre-allocation inside the
    /// gate). Per-item VALIDATION failures are COLLECTED and thrown together as
    /// <see cref="ReferenceImagesUnavailableException"/> (index-based — sanitation
    /// drops exactly the dead items in one pass instead of one per redo attempt); the
    /// budget throw and a matching cancellation abort immediately (never collected).
    /// Returns the materialized references in selection order.
    /// </summary>
    internal static Task<IReadOnlyList<ReferenceImage>> ReadReferenceImagesAsync(
        IReadOnlyList<Models.ReferenceImageSelection>? selections, CancellationToken ct = default,
        int maxCount = Helpers.ReferenceImagePolicy.AbsoluteMaxReferenceCount)
        => ReadReferenceImagesAsync(selections, Helpers.AppPaths.ImagesDir, Helpers.AppPaths.ReferencesDir, ct,
            maxCount: maxCount);

    /// <summary>
    /// IMG-4: the batch's ONE shared read — today's exact validated multi-read with the
    /// count-scoped aggregate budget (<see cref="Helpers.ReferenceImagePolicy.MaxBatchTotalBytes"/>:
    /// N concurrent payload builds multiply the transient allocation, so the budget
    /// divides by the version count to keep the batch's peak at ~the single-call worst
    /// case). A budget overflow is rethrown with the COUNT-SPECIFIC limit in the message
    /// — the inner read's message names the single-call constant, which would misstate
    /// the batch's actual bound. Backstop only: the dialog confirm gate enforces the
    /// same policy before any spend.
    /// </summary>
    internal static async Task<IReadOnlyList<ReferenceImage>> ReadReferenceImagesForBatchAsync(
        IReadOnlyList<Models.ReferenceImageSelection>? selections, int versionCount, CancellationToken ct)
    {
        var budget = Helpers.ReferenceImagePolicy.MaxBatchTotalBytes(versionCount);
        try
        {
            return await ReadReferenceImagesAsync(
                selections, Helpers.AppPaths.ImagesDir, Helpers.AppPaths.ReferencesDir, ct,
                totalBudget: budget, transformedBudget: budget).ConfigureAwait(false);
        }
        catch (ReferenceImageBudgetExceededException)
        {
            throw new ReferenceImageBudgetExceededException(
                $"Reference images exceed {budget / (1024 * 1024)} MB total for {versionCount} versions");
        }
    }

    // totalBudget / transformedBudget are TEST SEAMS (defaulting to the real aggregate
    // bound): the running subtractions below are the invariants under test, and only a
    // sub-real budget can exercise them against small fixture files (Codex diff review
    // r1). ENH-6h split the aggregate in two: totalBudget measures ORIGINAL stream
    // lengths (pre-allocation, unchanged semantics), transformedBudget measures the
    // PROVIDER-READY bytes after a possible re-encode — normalization can EXPAND the
    // payload, and the inline transports copy the transformed bytes through
    // base64/JSON, so the 50 MB copy invariant must hold on what is uploaded. The
    // remaining transformed allowance THREADS into the normalizer (ENH-6i diff r1) so
    // a cumulative overflow rejects BEFORE the normalized output array allocates.
    // IMG-6 (owner: "NO CAP") — CORRECTED after the first attempt made this gate PER-MODEL (Codex
    // diff review, blocking). It is not a policy gate: its documented job is defense-in-depth
    // against corrupt or absurd state. A per-model bound here meant that attaching 16 references
    // under a 16-capable model and then switching to krea made VoiceWink reject the list before the
    // provider ever saw it — contradicting the advisory the same card shipped. maxCount therefore
    // defaults to the generous AbsoluteMaxReferenceCount; policy lives in the dialog's add bound
    // (guidance) and the provider's own answer (authoritative).
    internal static async Task<IReadOnlyList<ReferenceImage>> ReadReferenceImagesAsync(
        IReadOnlyList<Models.ReferenceImageSelection>? selections, string imagesDir, string referencesDir,
        CancellationToken ct = default, long totalBudget = Helpers.ReferenceImagePolicy.MaxTotalBytes,
        long transformedBudget = Helpers.ReferenceImagePolicy.MaxTotalBytes,
        int maxCount = Helpers.ReferenceImagePolicy.AbsoluteMaxReferenceCount)
    {
        if (selections == null || selections.Count == 0)
            return Array.Empty<ReferenceImage>();
        if (selections.Count > maxCount)
            throw new ReferenceImageUnavailableException(
                $"Too many reference images (max {maxCount})");

        var results = new ReferenceImage?[selections.Count];
        List<int>? failedIndices = null;
        string? singleFailureMessage = null;
        var remainingBudget = totalBudget;
        var remainingTransformed = transformedBudget;
        for (var i = 0; i < selections.Count; i++)
        {
            try
            {
                var read = await ReadReferenceImageCoreAsync(
                    selections[i], imagesDir, referencesDir, ct, remainingBudget, remainingTransformed).ConfigureAwait(false);
                var (image, originalBytes) = read!.Value;
                results[i] = image;
                remainingBudget -= originalBytes;
                // Passthrough items consume the transformed pool here (their bytes
                // already exist — the original read buffer); NORMALIZED items were
                // bounded pre-allocation inside the normalizer via remainingTransformed.
                remainingTransformed -= image.Bytes.Length;
                if (remainingTransformed < 0)
                    throw new ReferenceImageBudgetExceededException(
                        $"Reference images exceed {Helpers.ReferenceImagePolicy.MaxTotalBytes / (1024 * 1024)} MB total after processing");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // a cancelled pipeline exits through its cancel path — never a collected failure
            }
            catch (ReferenceImageBudgetExceededException)
            {
                throw; // aggregate overflow indicts the whole selection — abort, base-type sanitation applies
            }
            catch (ReferenceImageUnavailableException ex)
            {
                (failedIndices ??= new List<int>()).Add(i);
                // ENH-6h round-3: exactly ONE failed item surfaces its OWN message (the
                // processing-failure and too-large causes differ actionably); a second
                // failure degrades to the count message.
                singleFailureMessage = failedIndices.Count == 1 ? ex.Message : null;
            }
        }

        if (failedIndices != null)
            throw new ReferenceImagesUnavailableException(
                failedIndices.Count == 1 && singleFailureMessage != null
                    ? singleFailureMessage
                    : $"{failedIndices.Count} of {selections.Count} reference images unavailable",
                failedIndices);

        return results!;
    }

    // Root-dir seams for tests (the public entry pins the real app folders).
    // remainingBudget (ENH-6f): the multi-read's running aggregate allowance, checked at
    // the same pre-allocation point as the per-item cap; long.MaxValue = unbudgeted.
    internal static async Task<ReferenceImage?> ReadReferenceImageAsync(
        Models.ReferenceImageSelection? selection, string imagesDir, string referencesDir,
        CancellationToken ct = default, long remainingBudget = long.MaxValue)
    {
        var read = await ReadReferenceImageCoreAsync(
            selection, imagesDir, referencesDir, ct, remainingBudget).ConfigureAwait(false);
        return read?.Image;
    }

    // The tuple-returning core (ENH-6h/6i) — Image is the PROVIDER-READY reference
    // (the original, or its clean re-encode when the JPEG container is MPO),
    // OriginalBytes is the source stream length. Both running-budget loops (the
    // multi-read above and ReferencePersistence.PrepareFailedRetentionAsync) subtract
    // OriginalBytes so the original-size budget semantics survive the substitution;
    // remainingTransformed is the caller's live transformed-pool allowance, threaded
    // into the normalizer so a cumulative overflow rejects BEFORE the normalized
    // output array allocates (ENH-6i diff r1).
    internal static async Task<(ReferenceImage Image, long OriginalBytes)?> ReadReferenceImageCoreAsync(
        Models.ReferenceImageSelection? selection, string imagesDir, string referencesDir,
        CancellationToken ct = default, long remainingBudget = long.MaxValue,
        long remainingTransformed = long.MaxValue)
    {
        if (selection == null) return null;

        var mime = Helpers.ReferenceImagePolicy.MimeFromExtension(selection.Path);
        if (mime == null)
            throw new ReferenceImageUnavailableException(
                Helpers.ReferenceImagePolicy.UnsupportedReferenceTypeMessage);

        ReferenceImage original;
        long originalBytes;
        FileStream? stream = null;
        try
        {
            // The open itself (and the kernel-path verification behind it) can block on
            // network paths / cloud placeholders — off the caller thread.
            stream = await Task.Run(() =>
            {
                switch (selection.Origin)
                {
                    case Models.ReferenceImageOrigin.AppImages:
                    case Models.ReferenceImageOrigin.AppReferences:
                        // The upload gate for app-owned origins: open-then-verify via the
                        // kernel-resolved final path against the ORIGIN's own root
                        // (reparse points can't escape), then read from the SAME stream —
                        // no re-open, no TOCTOU window (Codex diff review 2026-07-11).
                        var root = selection.Origin == Models.ReferenceImageOrigin.AppImages
                            ? imagesDir
                            : referencesDir;
                        return Helpers.ReferenceImagePolicy.TryOpenVerifiedAppImage(selection.Path, root, asyncIo: true)
                            ?? throw new ReferenceImageUnavailableException("Reference image no longer available");

                    case Models.ReferenceImageOrigin.UserPicked:
                        // No File.Exists pre-probe (it would block this thread's caller and
                        // race the open anyway) — the open establishes existence and the
                        // not-found mapping below keeps the message contract.
                        return new FileStream(selection.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 4096, FileOptions.Asynchronous);

                    default:
                        // FAIL CLOSED: an unrecognized origin must never fall through to
                        // the permissive user-picked handling (ENH-6b plan round 2).
                        throw new ReferenceImageUnavailableException("Reference image no longer available");
                }
            }, ct).ConfigureAwait(false);

            var length = stream.Length;
            if (length == 0)
                throw new ReferenceImageUnavailableException("Reference image is empty");
            if (length > Helpers.ReferenceImagePolicy.MaxBytes)
                throw new ReferenceImageUnavailableException(
                    $"Reference image is too large (max {Helpers.ReferenceImagePolicy.MaxBytes / (1024 * 1024)} MB)");
            // ENH-6f aggregate budget — checked at the same pre-allocation point as the
            // per-item cap. NOT the collectable per-item type: overflow indicts the
            // selection as a whole (the multi-read aborts; whole-list sanitation).
            if (length > remainingBudget)
                throw new ReferenceImageBudgetExceededException(
                    $"Reference images exceed {Helpers.ReferenceImagePolicy.MaxTotalBytes / (1024 * 1024)} MB total");

            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            original = new ReferenceImage(bytes, ResolveReferenceMime(bytes, mime));
            originalBytes = length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Must precede the IOException filter (FileNotFound derives from it): a
            // vanished UserPicked file keeps the "no longer available" message the
            // File.Exists pre-probe used to produce.
            throw new ReferenceImageUnavailableException("Reference image no longer available");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReferenceImageUnavailableException("Reference image could not be read");
        }
        finally
        {
            // Closed BEFORE codec work (ENH-6h round-3 amendment 4) — a re-encode can
            // take time on a large source and must not pin the file handle open.
            stream?.Dispose();
        }

        var provider = await ApplyNormalizationAsync(original, remainingTransformed, ct).ConfigureAwait(false);
        return (provider, originalBytes);
    }

    /// <summary>
    /// The read-gate format matrix: what a reference's mime becomes once we have looked at the
    /// actual bytes instead of trusting the file name.
    ///
    /// <para>This exists because the extension lies in practice. Generated images were written to
    /// <c>.png</c> names regardless of content until 2026-08-03, so a WebP produced by riverflow was
    /// being sent BACK to providers as <c>image/png</c> on Iterate and "Use last image".</para>
    ///
    /// <para>The rule is PER KIND, deliberately not a blanket "content wins" (Codex caught that the
    /// general form is unsafe):</para>
    /// <list type="bullet">
    /// <item>PNG/JPEG/WebP — content wins. This is the fix.</item>
    /// <item>AVIF — keep <c>image/avif</c> here; <see cref="ApplyNormalizationAsync"/> converts it to
    /// PNG before any client sees it.</item>
    /// <item>SVG/GIF/BMP/HEIF — the typed, per-item-collectable rejection, and their mime is NEVER
    /// returned. Handing any of them onward would reach
    /// <see cref="Helpers.ImagePayloadWriter"/>, which throws a RAW <c>NotSupportedException</c>
    /// that escapes <see cref="ReadReferenceImagesAsync"/>'s per-item collection as an unexpected
    /// fault — strictly worse than the honest refusal.</item>
    /// <item>Unknown — keep the extension-derived mime (fail-open, matching
    /// <see cref="Helpers.ImageBytesFormat"/>'s recorded stance: an unrecognised raster format must
    /// not be blocked on a guess).</item>
    /// </list>
    /// </summary>
    private static string ResolveReferenceMime(byte[] bytes, string extensionMime)
    {
        switch (Helpers.ImageBytesFormat.Sniff(bytes))
        {
            case Helpers.ImageBytesKind.Png: return "image/png";
            case Helpers.ImageBytesKind.Jpeg: return "image/jpeg";
            case Helpers.ImageBytesKind.WebP: return "image/webp";
            case Helpers.ImageBytesKind.Avif: return "image/avif";
            case Helpers.ImageBytesKind.Svg:
            case Helpers.ImageBytesKind.Gif:
            case Helpers.ImageBytesKind.Bmp:
            case Helpers.ImageBytesKind.Heif:
                throw new ReferenceImageUnavailableException(
                    Helpers.ReferenceImagePolicy.UnsupportedReferenceTypeMessage);
            default: return extensionMime;
        }
    }

    // TEST SEAM (ENH-6h diff r1, kept through ENH-6i): the normalizer's
    // post-classification failure and mid-codec cancellation branches can't be forced
    // deterministically with real bytes, so tests swap this hook — with a
    // finally-restore; the suite runs sequentially. The long parameter is the
    // caller's remaining transformed-pool allowance (pre-allocation bound).
    internal static Func<ReferenceImage, long, CancellationToken, Task<ReferenceNormalizeResult?>> ReferenceNormalizeHook =
        (source, maxTransformedBytes, ct) => ReferenceImageNormalizer.TryNormalizeAsync(
            source, ct, maxTransformedBytes: maxTransformedBytes);

    /// <summary>
    /// ENH-6i: substitutes a clean re-encode for a non-plain JPEG container (MPO —
    /// the proven OpenAI rejection class). Null from the normalizer = passthrough
    /// (plain/undecidable images upload byte-identical); a POSITIVELY CLASSIFIED
    /// image whose codec work fails throws the typed, path-free, per-item-collectable
    /// error — uploading a known-MPO original would restore the provider 400 this
    /// fixes. The budget subtype (an expanding re-encode past the aggregate cap)
    /// propagates UNWRAPPED — it indicts the selection as a whole, never one item.
    /// A matching cancellation propagates unwrapped (the gate's standing contract).
    /// </summary>
    private static async Task<ReferenceImage> ApplyNormalizationAsync(
        ReferenceImage original, long remainingTransformed, CancellationToken ct)
    {
        ReferenceNormalizeResult? normalized;
        try
        {
            // The effective pre-allocation bound is the smaller of the static cap and
            // the caller's live allowance — a later item must not allocate an output
            // the cumulative pool has no room for (ENH-6i diff r1).
            var maxTransformedBytes = Math.Min(
                Helpers.ReferenceImagePolicy.MaxTotalBytes, remainingTransformed);
            normalized = await ReferenceNormalizeHook(original, maxTransformedBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReferenceImageBudgetExceededException)
        {
            throw; // ordered BEFORE the generic map — budget is non-collectable
        }
        catch (Exception ex)
        {
            Logger.Warning("Reference normalization failed: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            throw new ReferenceImageUnavailableException("Reference image could not be processed");
        }

        if (normalized == null)
            return original;

        Logger.Information("Reference re-encoded ({Kind}): {Width}x{Height}, {FromKb} -> {ToKb} KB",
            normalized.Kind, normalized.OrientedWidth, normalized.OrientedHeight,
            original.Bytes.Length / 1024, normalized.Image.Bytes.Length / 1024);
        return normalized.Image;
    }

    /// <summary>
    /// Block image generation for model families that VoiceWink no longer supports
    /// (DALL-E and Imagen, both deprecated by their providers). Catches persisted selections
    /// from older versions so the user gets a clear instruction instead of an opaque API error.
    /// </summary>
    private static void EnsureModelStillSupported(string model)
    {
        // Classify the BARE id (ImageOptions.Bare — the one shared slug-stripping rule) so a
        // persisted OpenRouter selection like openai/dall-e-3 is refused the same as dall-e-3
        // instead of slipping through to a live 4xx (F31).
        var bare = ImageOptions.Bare(model);
        if (bare.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DALL-E models are no longer supported. Switch to gpt-image-2 in AI Enhancement settings.");
        if (bare.StartsWith("imagen", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Imagen models are no longer supported. Switch to a Gemini-native image model (e.g. gemini-3.1-flash-image) in AI Enhancement settings.");
    }

    public List<CustomPrompt> GetPrompts()
    {
        if (_cachedPrompts != null)
            return _cachedPrompts;

        var json = _settings.GetString(AppDefaults.CustomPrompts, "");
        if (string.IsNullOrEmpty(json))
        {
            _cachedPrompts = new List<CustomPrompt>(PredefinedPrompts.All);
            return _cachedPrompts;
        }

        try
        {
            var prompts = global::System.Text.Json.JsonSerializer.Deserialize<List<CustomPrompt>>(json)
                ?? new List<CustomPrompt>(PredefinedPrompts.All);

            _cachedPrompts = prompts;
            return _cachedPrompts;
        }
        catch
        {
            _cachedPrompts = new List<CustomPrompt>(PredefinedPrompts.All);
            return _cachedPrompts;
        }
    }

    public void SavePrompts(List<CustomPrompt> prompts)
    {
        var json = global::System.Text.Json.JsonSerializer.Serialize(prompts);
        _settings.SetString(AppDefaults.CustomPrompts, json);
        _cachedPrompts = null; // invalidate cache so next GetPrompts() re-reads
    }

    public CustomPrompt? GetActivePrompt()
    {
        return GetPrompts().FirstOrDefault(p => p.IsActive);
    }


    /// <summary>
    /// Fetch available model IDs from the current provider's API.
    /// Returns an empty list on failure (no API key, network error, etc.).
    /// Filters out non-chat models (image, audio, embedding, etc.).
    /// Thin wrapper over <see cref="TryFetchAvailableModelsAsync"/> that drops the
    /// error text — callers that surface fetch failures use the tuple method.
    /// </summary>
    public async Task<List<string>> FetchAvailableModelsAsync(
        Providers.ModelCatalogQuery query = Providers.ModelCatalogQuery.Text,
        bool showAll = false, AIProvider? providerOverride = null, CancellationToken ct = default)
        => (await TryFetchAvailableModelsAsync(query, showAll, providerOverride, ct).ConfigureAwait(false)).Models;


    /// <summary>
    /// IMG-10b: the last successfully fetched display list for this exact
    /// (provider, query, showAll), or null. Serving from the cache is OPT-IN — today the
    /// options dialog is the only caller, which is what keeps a key-validation probe
    /// (RawCount semantics, PR #271) structurally unable to receive a stale answer. A hit
    /// returns the LIVE cache entry — a read-only snapshot (`ReadOnlyCollection`, stored as
    /// a detached copy of the fetch result), so neither the fetch caller mutating its own
    /// returned list nor a cache consumer casting can alter what the next open binds.
    /// </summary>
    public IReadOnlyList<string>? TryGetCachedModels(
        Providers.ModelCatalogQuery query, bool showAll, AIProvider provider)
    {
        lock (_modelListLock)
            return _modelListCache.TryGetValue((provider, query, showAll), out var list) ? list : null;
    }

    /// <summary>
    /// IMG-10b: refresh the cached list without a caller waiting on it — the dialog binds
    /// a cached list instantly and kicks this so the NEXT open is fresh; it is also what
    /// keeps IMG-5 capability discovery live, since the capture rides the same fetch.
    /// SINGLE-FLIGHT per key for BACKGROUND refreshes only (open/close/open joins one
    /// fetch rather than firing three); the dialog's foreground miss-path fetch is not
    /// joined — after a key change an epoch-fenced old refresh can briefly overlap the
    /// fresh foreground fetch, harmlessly, because the stale store is fenced. FAIL-SOFT:
    /// a refresh failure must never surface for a dialog that already bound a usable
    /// list, so failures log Warning and leave the previous cache entry standing
    /// (stale-while-revalidate's whole point). Returns the task for tests; production
    /// discards it. Deliberately NO completed-attempt latch (unlike
    /// <see cref="EnsureImageCapabilitiesAsync"/>) — every open should refresh.
    /// </summary>
    public Task RefreshModelListInBackgroundAsync(
        Providers.ModelCatalogQuery query, bool showAll, AIProvider provider, CancellationToken ct = default)
    {
        var key = (provider, query, showAll);
        lock (_modelListLock)
        {
            if (_modelListRefreshes.TryGetValue(key, out var inFlight))
                return inFlight.Task;
            var token = new object();
            var refresh = RunBackgroundModelRefreshAsync(key, token, query, showAll, provider, ct);
            _modelListRefreshes[key] = (token, refresh);
            return refresh;
        }
    }

    private async Task RunBackgroundModelRefreshAsync(
        (AIProvider, Providers.ModelCatalogQuery, bool) key, object token,
        Providers.ModelCatalogQuery query, bool showAll, AIProvider provider, CancellationToken ct)
    {
        // Yield first so the registration above completes before the body can finish —
        // a synchronously-completing fetch (no-key early return) would otherwise remove
        // the in-flight entry before it was added, leaving a husk that joins forever.
        await Task.Yield();
        try
        {
            await TryFetchAvailableModelsAsync(query, showAll, provider, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // TryFetch already converts provider failures into its tuple; what lands here
            // is the 401/403 rethrow or cancellation. Neither may surface — the dialog
            // that kicked this is already showing a usable list.
            Logger.Warning("Background model-list refresh failed for {Provider}: {ErrorType}",
                provider, ex.GetType().Name);
        }
        finally
        {
            lock (_modelListLock)
            {
                // Remove only OUR registration: an invalidation may already have dropped it and a
                // newer refresh registered under the same key, and unregistering that one would
                // let a third caller start a duplicate fetch.
                if (_modelListRefreshes.TryGetValue(key, out var registered) &&
                    ReferenceEquals(registered.Token, token))
                {
                    _modelListRefreshes.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Like <see cref="FetchAvailableModelsAsync"/> but reports WHY a fetch produced no
    /// models (ENH-1 — e.g. Gemini's EU geo-block 400 used to collapse into a silent
    /// empty dropdown beside a green "Key valid"). Contract:
    /// <list type="bullet">
    /// <item>401/403 still THROW — key-invalid semantics belong to the caller.</item>
    /// <item>Caller-requested cancellation propagates as OperationCanceledException.</item>
    /// <item>Every other failure → (empty, error) where error is UI-DESTINED text
    /// (<see cref="Helpers.ProviderApiException.UserMessage"/> when available, else the
    /// status-only message). Do not put the tuple's error in a log template — the
    /// exception itself is already logged here.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// ENH-17: validate a CANDIDATE key without writing it anywhere. The only public door to the
    /// candidate-key config build.
    /// </summary>
    /// <remarks>
    /// <para>Returns the same tuple as <see cref="TryFetchAvailableModelsAsync"/>, so callers keep
    /// reading <c>RawCount</c> for "does this key work" and <c>Error</c> for "the provider failed
    /// for some other reason" — the ENH-1 split is unchanged.</para>
    ///
    /// <para><b>Publishes nothing.</b> No model-list cache write, no image-capability snapshot.
    /// That is not tidiness: under validate-before-write a REJECTED candidate is never written, so
    /// no <c>KeyChanged</c> ever fires, so nothing would ever invalidate what its fetch had
    /// cached — a candidate answering HTTP 200 with an empty catalog would leave an empty list
    /// cached beside a perfectly good stored key, and the next Configure dialog would instant-bind
    /// it. The old write-then-rollback flow scrubbed that by accident, through the rollback's own
    /// <c>KeyChanged</c>; deleting the rollback deletes the accident (Kimi, ENH-17 plan review).</para>
    ///
    /// <para>Resolves <c>BaseUrl</c> and runs <c>ThrowIfOffline</c> exactly as a normal fetch does —
    /// validating against a different endpoint than the key will actually be used on would make the
    /// verdict a lie for custom-endpoint users.</para>
    /// </remarks>
    public async Task<(List<string> Models, int RawCount, string? Error)> ValidateCandidateKeyAsync(
        AIProvider provider, string candidateKey,
        Providers.ModelCatalogQuery query = Providers.ModelCatalogQuery.Text,
        bool showAll = false, CancellationToken ct = default)
    {
        try
        {
            // Normalize FIRST, so validation judges exactly the value the commit will store.
            // Without this a key pasted with a trailing newline passes the caller's format check
            // (which normalizes) and is then rejected here for the padding the save would have
            // trimmed anyway — a false "invalid key" on a perfectly good paste, which is the most
            // common real-world shape there is (Codex diff review).
            var config = BuildConfig(SelectedModel, provider, reasoningChoice: null,
                candidateKey: Helpers.ApiKeyFormat.Normalize(candidateKey));
            if (string.IsNullOrEmpty(config.ApiKey))
                return (new List<string>(), 0, null);
            ThrowIfOffline(config);

            var fetched = await _providers.Get(provider)
                .FetchAvailableModelsAsync(_httpFactory, config, query, showAll, ct).ConfigureAwait(false);

            // Display policy only — deliberately NO CaptureImageCapabilities and NO cache store.
            return (ModelDisplayPolicy.ApplyDisplayPolicy(fetched, query, showAll), fetched.RawCount, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                  or global::System.Net.HttpStatusCode.Forbidden)
        {
            Logger.Warning("Candidate API key rejected ({Status}) for {Provider}", ex.StatusCode, provider);
            throw; // callers distinguish an auth failure from a provider outage
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException or InvalidOperationException or TimeoutException
                or Helpers.InvalidApiKeyFormatException)
                Logger.Warning("Candidate key validation failed for {Provider}: {ErrorType}: {ErrorMessage}",
                    provider, ex.GetType().Name, ex.Message);
            else
                Logger.Warning(ex, "Candidate key validation failed for {Provider}", provider);
            var error = ex is OperationCanceledException
                ? $"{provider}: no response (timed out)"
                : Helpers.ProviderApiException.UserFacingMessage(ex);
            return (new List<string>(), 0, error);
        }
    }

    public async Task<(List<string> Models, int RawCount, string? Error)> TryFetchAvailableModelsAsync(
        Providers.ModelCatalogQuery query = Providers.ModelCatalogQuery.Text,
        bool showAll = false, AIProvider? providerOverride = null, CancellationToken ct = default)
    {
        var provider = providerOverride ?? SelectedProvider;
        // IMG-10b: epoch captured BEFORE the HTTP call, checked at store time — a fetch in
        // flight when the user saves a different key must not cache a list fetched with the
        // old one. Keyed on the RESOLVED provider (a null override falls back to
        // SelectedProvider above), never the parameter.
        int epochAtStart;
        lock (_modelListLock)
            epochAtStart = ModelListEpoch(provider);
        try
        {
            var config = BuildConfig(SelectedModel, provider);
            if (string.IsNullOrEmpty(config.ApiKey))
                return (new List<string>(), 0, null);
            ThrowIfOffline(config);

            var fetched = await _providers.Get(provider)
                .FetchAvailableModelsAsync(_httpFactory, config, query, showAll, ct).ConfigureAwait(false);
            // IMG-5: commit the capability snapshot this fetch carried (a no-op for every fetch
            // that carried none). Deliberately AFTER the await and BEFORE display policy — the
            // snapshot describes the whole catalog, not the filtered list, so ShowAllModels and
            // curation cannot change what gets cached.
            //
            // IMG-10b: the epoch fences this side effect too (Codex diff r2), and the check is
            // handed IN so it runs inside the publication lock rather than before it (Codex diff
            // r3 — a check-then-publish pair is a TOCTOU race, and the window is a thread
            // suspension, so calling it "microseconds" as an earlier revision did was wrong). An
            // old background fetch completing after a key change now provably cannot overwrite the
            // snapshot a fresh fetch captured under the new key.
            CaptureImageCapabilities(fetched, admit: () => ModelListEpochIsCurrent(provider, epochAtStart));
            var display = ModelDisplayPolicy.ApplyDisplayPolicy(fetched, query, showAll);
            // IMG-10b: cache the display list for instant dialog binds. Only HERE — after a real
            // provider round-trip — never on the no-key early return above, whose empty list is a
            // different fact from "this provider's catalog is empty" (plan review). An EMPTY list
            // from a real round-trip IS cached: an empty curated catalog is an honest answer.
            // The return value is unaffected; a stale epoch only skips the cache write.
            lock (_modelListLock)
            {
                if (epochAtStart == ModelListEpoch(provider))
                    // A genuinely READ-ONLY snapshot, never the returned instance: callers own
                    // what they get back (one future caller sorting in place would corrupt the
                    // cache), and the wrapper also closes the read side — a cache hit hands out
                    // this same object, and a bare List behind an IReadOnlyList is one cast away
                    // from mutable (both diff reviewers, round 2).
                    _modelListCache[(provider, query, showAll)] = global::System.Array.AsReadOnly(display.ToArray());
            }
            return (display, fetched.RawCount, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                  or global::System.Net.HttpStatusCode.Forbidden)
        {
            Logger.Warning("API key rejected ({Status}) for {Provider}", ex.StatusCode, provider);
            throw; // Let callers distinguish auth failures from other errors
        }
        catch (Helpers.InvalidApiKeyFormatException ex)
        {
            // ENH-15: same posture as the auth arm above and for the same reason — a malformed
            // STORED key is a key problem, not a provider problem, and only the caller can roll
            // back or prompt for re-entry. Returning it as a fetchError string instead would put
            // it in the generic "provider failed for a non-auth reason, so KEEP the key" branch,
            // which is precisely the defect this fix exists to close.
            Logger.Warning("Stored API key for {Provider} has an invalid format: {Verdict}",
                provider, ex.Verdict);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller-requested cancellation — not a fetch failure
        }
        catch (Exception ex)
        {
            // LOG-1: network/provider-shaped failures log type + message only — the
            // full stack added nothing for a handled fetch failure and flooded the
            // file log during outages. Unexpected types keep the stack.
            if (ex is HttpRequestException or InvalidOperationException or TimeoutException or OperationCanceledException)
                Logger.Warning("Failed to fetch models for {Provider}: {ErrorType}: {ErrorMessage}",
                    provider, ex.GetType().Name, ex.Message);
            else
                Logger.Warning(ex, "Failed to fetch models for {Provider}", provider);
            // HttpClient timeouts surface as TaskCanceledException WITHOUT ct being
            // signalled — an unreachable/stalling provider deserves an error line too.
            var error = ex is OperationCanceledException
                ? $"{provider}: no response (timed out)"
                : Helpers.ProviderApiException.UserFacingMessage(ex);
            return (new List<string>(), 0, error);
        }
    }

    /// <summary>
    /// Run text enhancement with a specific model override. Does not change the persisted
    /// SelectedModel setting. Used by the redo feature.
    /// </summary>
    public async Task<string> EnhanceWithModelAsync(
        string transcribedText,
        CustomPrompt? prompt,
        string modelId,
        IReadOnlyList<string>? vocabularyTerms = null,
        AIProvider? providerOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            Logger.Warning("EnhanceWithModelAsync: empty model ID, skipping");
            return transcribedText;
        }

        prompt ??= PredefinedPrompts.Default;

        // Same single composition site as EnhanceAsync — parity is test-pinned.
        var systemPrompt = AIPrompts.BuildSystemPrompt(prompt.PromptText, vocabularyTerms);
        var userPrompt = AIPrompts.BuildUserPrompt(transcribedText);

        var provider = providerOverride ?? SelectedProvider;
        Logger.Information("AI Enhancement (redo): provider={Provider}, model={Model} userTerm={Prompt}",
            provider, modelId,
            global::VoiceWink.Helpers.LogValueSanitizer.SingleLine(prompt.Title));

        try
        {
            var config = BuildConfig(modelId, provider, prompt.ReasoningOverride);
            // Same trace-after-config ordering as EnhanceAsync (ENH-8).
            Helpers.PromptTraceLog.WriteSections(Helpers.PromptTraceOp.TextEnhancement,
                new Helpers.TraceMeta(Provider: provider.ToString(), Model: modelId),
                $"enhancement (redo) · {provider} · {modelId} · {prompt.Title}",
                ("SYSTEM", systemPrompt),
                ("USER", userPrompt),
                ("REASONING", config.Reasoning.Describe()));
            ThrowIfOffline(config);
            var enhanced = await CallProviderWithDeadlineAsync(config, systemPrompt, userPrompt, ct).ConfigureAwait(false);
            // Same raw-output trace as EnhanceAsync — provider-boundary truth.
            Helpers.PromptTraceLog.WriteOutput(Helpers.PromptTraceOp.EnhancementOutput,
                new Helpers.TraceMeta(Provider: provider.ToString(), Model: modelId),
                $"enhancement (redo) output · {provider} · {modelId} · {prompt.Title}",
                enhanced);
            var filtered = AIEnhancementOutputFilter.Filter(enhanced);
            // Plain whitespace check, deliberately. A round of this change applied the content
            // rule here on the reasoning that model output is machine-authored — which is only
            // half true. The PROMPT is user-authored, and a custom prompt may legitimately ask
            // for a punctuation-only result (extract the punctuation, emit Morse, return an
            // emoticon). Rejecting those silently restores the original transcript and destroys
            // a correct answer — the same data-loss class this card set out to fix, one surface
            // over (Codex diff review r2). The comma incident is already stopped upstream, at
            // the machine/user boundary inside TextPipelineRunner, so nothing here needs it.
            return string.IsNullOrWhiteSpace(filtered) ? transcribedText : filtered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Same reasoning as EnhanceAsync: deliberate user action, never Error.
            Logger.Information("AI Enhancement (redo) cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Same split as EnhanceAsync above, InvalidApiKeyFormatException included — a redo
            // over a legacy malformed key would otherwise log Error on BOTH sides of the rethrow
            // (here and in MainViewModel's redo catch), i.e. two Sentry events per redo.
            if (ex is HttpRequestException or InvalidOperationException or TimeoutException
                or Helpers.InvalidApiKeyFormatException)
                Logger.Warning("AI Enhancement (redo) failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
            else
                Logger.Error(ex, "AI Enhancement (redo) failed");
            throw;
        }
    }

    /// <summary>
    /// Generate an image with a specific model override. Does not change the persisted
    /// SelectedImageModel setting. Used by the redo feature.
    /// </summary>
    public Task<ImageGenerationResult> GenerateImageWithModelAsync(
        string description,
        CustomPrompt? prompt,
        string modelId,
        AIProvider? providerOverride = null,
        IReadOnlyList<Models.ReferenceImageSelection>? references = null,
        CancellationToken ct = default)
        => GenerateImageWithModelCoreAsync(description, prompt, modelId, providerOverride, references, null, ct);

    /// <summary>
    /// IMG-4: the parallel-batch entry — same behavior as
    /// <see cref="GenerateImageWithModelAsync"/> plus the batch's shared call context
    /// (memoized reference read + response-materialization gate). Internal overload
    /// rather than a parameter on the public method: the context type is internal and
    /// must not ride a public signature.
    /// </summary>
    internal Task<ImageGenerationResult> GenerateImageWithModelForBatchAsync(
        string description,
        CustomPrompt? prompt,
        string modelId,
        AIProvider? providerOverride,
        IReadOnlyList<Models.ReferenceImageSelection>? references,
        ImageBatchCallContext batchContext,
        CancellationToken ct)
        => GenerateImageWithModelCoreAsync(description, prompt, modelId, providerOverride, references, batchContext, ct);

    private async Task<ImageGenerationResult> GenerateImageWithModelCoreAsync(
        string description,
        CustomPrompt? prompt,
        string modelId,
        AIProvider? providerOverride,
        IReadOnlyList<Models.ReferenceImageSelection>? references,
        ImageBatchCallContext? batchContext,
        CancellationToken ct)
    {
        var imageProvider = providerOverride ?? SelectedImageProvider;
        var descriptor = _providers.Get(imageProvider);
        if (!descriptor.SupportsImageGeneration)
            throw new InvalidOperationException(
                $"Image generation is not supported by {imageProvider}.");

        if (string.IsNullOrWhiteSpace(modelId))
            modelId = DefaultImageModelFor(imageProvider);

        EnsureModelStillSupported(modelId);

        var config = BuildConfig(modelId, imageProvider);
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new InvalidOperationException($"No API key configured for {imageProvider}");
        ThrowIfOffline(config);
        // IMG-4: the batch context rides the per-call config to the image clients
        // (materialization gate + buffer cap). Null on every non-batch call.
        config.BatchContext = batchContext;

        // IMG-4: a batch reads references ONCE through the context's memoized read —
        // invoked HERE, after the provider/model/key/offline validation above, so error
        // precedence is unchanged and no reference byte is read for a call that would
        // fail validation. Null context keeps today's per-call read byte-identical.
        var referenceImages = batchContext != null
            ? await batchContext.ReadAsync(ct).ConfigureAwait(false)
            : await ReadReferenceImagesAsync(references, ct).ConfigureAwait(false);

        // IMG-2: same normalization as the primary path — see GenerateImageAsync.
        // Same hydration as the primary path, and after the read for the same reason.
        await EnsureImageCapabilitiesAsync(imageProvider, ct).ConfigureAwait(false);
        var effective = Helpers.ImageOptions.NormalizeForModel(
            imageProvider, modelId, prompt?.ImageAspect, prompt?.ImageSizeTier, prompt?.ImageQuality,
            ImageCapabilitiesFor(imageProvider, modelId));
        foreach (var adjustment in effective.Adjustments)
            Logger.Information("Image option adjusted for {Model}: {Adjustment}", modelId, adjustment);
        var referencesDescription = ReferenceLogFormat.Describe(referenceImages);
        Logger.Information("Image generation (redo): provider={Provider}, model={Model}, aspect={Aspect}, tier={Tier}, quality={Quality}, references={References}",
            imageProvider, modelId, effective.Aspect ?? "auto", effective.SizeTier ?? "auto", effective.Quality ?? "auto",
            referencesDescription);
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.ImageGenerationRedo,
            new Helpers.TraceMeta(Provider: imageProvider.ToString(), Model: modelId),
            $"image generation (redo) · {imageProvider} · {modelId}",
            ("prompt", description),
            ("aspect", effective.Aspect ?? "auto"),
            ("size tier", effective.SizeTier ?? "auto"),
            ("quality", effective.Quality ?? "auto"),
            ("references", referencesDescription));

        var bytes = await descriptor.GenerateImageAsync(
            _httpFactory, config, description, effective.Aspect, effective.SizeTier, effective.Quality, referenceImages, ct).ConfigureAwait(false);
        return ClassifyGeneratedImage(bytes, referenceImages, effective);
    }
}
