using VoiceWink.Models;
using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// Which transcription models this user can run RIGHT NOW — downloaded local models, plus cloud
/// models whose provider has a stored API key (TRN-17).
///
/// <para><b>Why it exists.</b> The two halves already existed and had never been put together.
/// <see cref="InstalledCatalogModels.Known"/> answers the local half; key presence was inlined at
/// every call site as <c>_apiKeyManager.HasApiKey(provider.ToString().ToLowerInvariant())</c>
/// (<c>ModelsPage</c>, <c>EnhancementViewModel</c>, <c>OnboardingPage</c>). The union had no owner,
/// and the one screen that came closest — App Mode's cloud combo — lists every cloud row with no
/// key filtering at all, so it offers models that cannot run.</para>
///
/// <para><b>Returns catalogue rows, deliberately, not a name/display pair.</b> The retry picker has
/// to ask <see cref="ModelLanguageSupport.SupportedCodesFor"/> and
/// <see cref="EffectiveTranscriptionLanguage.For"/> which languages the selected model recognises,
/// and both are keyed on the descriptor. A projection would have to be un-projected to answer that.
/// It also means the caller renders the CATALOGUE's <c>DisplayName</c> and persists the CATALOGUE's
/// <c>Name</c> by construction — the owner's never-render-a-raw-id rule (UI-2) then holds
/// structurally rather than by scrubbing, and no on-disk case variant can reach settings.</para>
///
/// <para><b>Answers "which are usable", NOT "which one to pick"</b> — the same boundary
/// <see cref="InstalledCatalogModels"/> draws. Order here is PRESENTATION order and nothing may
/// derive a recommendation from it; see <see cref="Resolve"/> for why locals lead.</para>
///
/// <para>Pure: the key lookup arrives as a delegate, so this is testable without DPAPI, without a
/// settings file and without the four real providers.</para>
/// </summary>
internal static class RunnableTranscriptionModels
{
    /// <summary>
    /// The catalogue rows that can serve a transcription attempt right now, locals first.
    /// </summary>
    /// <param name="downloadedStems">
    /// <c>ModelDownloadManager.GetDownloadedModels()</c> — on-disk stems, which is WEAKER than
    /// runnable (a hand-placed <c>foo.bin</c> is listable and throws at the first recording), hence
    /// the projection through <see cref="InstalledCatalogModels.Known"/>.
    /// </param>
    /// <param name="hasApiKey">
    /// Whether a cloud client for this provider could be constructed. Injected rather than taken as
    /// an <c>ApiKeyManager</c> so the decision stays pure — and because the provider-string
    /// convention belongs to <c>TranscriptionServiceRegistry.HasKeyFor</c>, beside the
    /// <c>GetCloudService</c> that shares it.
    /// </param>
    /// <param name="isLocalRuntimeHealthy">
    /// Whether the engine that serves this LOCAL row can actually execute here —
    /// <c>ILocalTranscriptionRuntime.IsAvailable</c>, which is a build lever plus a CPU floor.
    ///
    /// <para><b>Ownership and health are two questions, and this helper needs both.</b>
    /// <see cref="InstalledCatalogModels.Known"/> answers the first: <c>CanServe</c> is catalogue
    /// membership and deliberately does NOT depend on health, because a runtime that stopped
    /// claiming its rows when disabled would report them as <c>UnknownModel</c>. So a downloaded
    /// Parakeet on a build with the lever off, or on a CPU below the SSE2 floor, is "installed" and
    /// cannot run — and <c>PrepareOutcome.Unavailable</c> only arrives at prepare time, after the
    /// user has picked it. <c>ILocalTranscriptionRuntime</c>'s own doc names this as the question
    /// download UI must ask; a picker whose whole promise is "these can run right now" is the same
    /// kind of consumer (Kimi plan review).</para>
    ///
    /// <para>Applied to LOCAL rows only. Cloud rows have no local runtime, and passing them through
    /// a resolver keyed on the local seam would answer false for every one of them.</para>
    /// </param>
    /// <remarks>
    /// <b>Locals first is a product decision, not an accident.</b> The driving scenario for the
    /// retry picker is "the cloud provider is unreachable, switch to something local", so what works
    /// offline leads. Within each half the catalogue's own order is preserved: best-first for both
    /// (<c>PredefinedModels</c> since 2026-08-04, <c>CloudModels</c> per its ORDER-IS-LOAD-BEARING
    /// rule), so neither half is re-sorted here.
    /// </remarks>
    internal static IReadOnlyList<TranscriptionModelInfo> Resolve(
        IReadOnlyList<string>? downloadedStems,
        Func<ModelProvider, bool> hasApiKey,
        Func<TranscriptionModelInfo, bool> isLocalRuntimeHealthy)
    {
        ArgumentNullException.ThrowIfNull(hasApiKey);
        ArgumentNullException.ThrowIfNull(isLocalRuntimeHealthy);

        var runnable = new List<TranscriptionModelInfo>(
            InstalledCatalogModels.Known(downloadedStems).Where(isLocalRuntimeHealthy));
        runnable.AddRange(CloudModels.Models.Where(m => hasApiKey(m.Provider)));
        return runnable;
    }

    /// <summary>
    /// The catalogue name to pre-select, or <c>null</c> when <paramref name="requested"/> is not in
    /// <paramref name="runnable"/>.
    ///
    /// <para>Returns the CATALOGUE's spelling for a case-variant request, so a settings value of
    /// <c>GGML-SMALL-Q8_0</c> pre-selects the row it actually names instead of matching nothing.
    /// Comparison goes through <see cref="ModelDiskReconciliation.NameComparer"/> — the one
    /// definition of "these two names are the same model" — because the local-runtime seam resolves
    /// that way.</para>
    ///
    /// <para><b>Null is a real answer and callers must not substitute index 0.</b> A model the user
    /// can no longer run (deleted from disk, key removed) must leave the picker unselected, which is
    /// the UX-1 rule: never silently commit a model the pre-select could not vouch for. Handing over
    /// "the first one" would pick whatever presentation order happens to put at the top.</para>
    /// </summary>
    internal static string? Preselect(
        IReadOnlyList<TranscriptionModelInfo> runnable, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;

        // Either Parakeet bundle spelling matches the active row (TRN-29 flip) — the failed
        // attempt's captured model name may be a pre-flip spelling, and the retry picker's
        // same-model pre-select must still find it.
        requested = ParakeetCatalog.CanonicalName(requested);

        return runnable.FirstOrDefault(
            m => ModelDiskReconciliation.IsSameModel(m.Name, requested))?.Name;
    }
}
