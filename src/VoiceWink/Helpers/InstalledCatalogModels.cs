using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// Which on-disk model files the local catalog actually claims — the single answer shared by every
/// picker that lists installed models.
///
/// <para><c>ModelDownloadManager.GetDownloadedModels()</c> returns any stem the name guard accepts,
/// which is a weaker statement than "this is a model the app can run":
/// <c>LocalModelPreparer.CanServe</c> is catalog MEMBERSHIP, so a hand-placed <c>foo.bin</c> is
/// listable but throws <c>UnknownTranscriptionModelException</c> at the first recording. It is also
/// a filename, and filenames come off disk carrying whatever characters someone put there.</para>
///
/// <para>So this projects disk names onto catalog entries: unknown files drop out, and the caller
/// renders the catalog's <c>DisplayName</c> and persists the catalog's <c>Name</c> — never the
/// on-disk spelling, which would otherwise write a case variant back into settings and spread the
/// mismatch. Matching is case-insensitive because the runtime seam resolves that way.</para>
///
/// <para>Deliberately answers "which are usable", NOT "which one to pick". Choosing on the user's
/// behalf needs a real policy — catalog order is PRESENTATION order, best-first (Parakeet, then
/// Whisper largest to smallest), so "the first one" hands the user the heaviest model they happen to
/// hold however modest their machine is. The hazard is the same one in the opposite direction: the
/// order changed from smallest-first on 2026-08-04, and before that "the first one" meant Tiny
/// regardless of how capable the machine was. Either way it is presentation, not a recommendation
/// for this user. (An earlier example named <c>ggml-tiny.en</c>, which was English-only too; that
/// row is gone since 2026-08-03.)</para>
/// </summary>
internal static class InstalledCatalogModels
{
    /// <summary>Catalog entries present in <paramref name="downloaded"/>, in catalog order.
    ///
    /// <para>The Parakeet row alone asks <see cref="ParakeetCatalog.HoldsServableArtifact"/>
    /// instead of an exact stem match (TRN-29): during the migration window the disk holds the
    /// legacy sherpa bundle while the catalog row is the GGUF, and the engine IS serving — an
    /// exact match would drop Parakeet from the retry picker and the App Mode dropdown at
    /// precisely the moment a user is most likely retrying. One direction only, config-aware
    /// (an OFF build's orphaned GGUF does not count) — this is NOT general disk-name
    /// canonicalization, which stays deliberately absent here.</para></summary>
    internal static IReadOnlyList<TranscriptionModelInfo> Known(IReadOnlyList<string>? downloaded)
    {
        if (downloaded is null || downloaded.Count == 0) return [];
        return PredefinedModels.Models
            .Where(m => m.Runtime == LocalRuntimeKind.Parakeet
                ? ParakeetCatalog.HoldsServableArtifact(downloaded)
                : downloaded.Contains(m.Name, ModelDiskReconciliation.NameComparer))
            .ToList();
    }
}
