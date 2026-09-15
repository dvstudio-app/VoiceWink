using VoiceWink.Models.Enums;

namespace VoiceWink.Models;

/// <summary>
/// Describes a transcription model (name, provider, download URL, file size).
/// </summary>
public class TranscriptionModelInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required ModelProvider Provider { get; init; }
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// TRN-33 fallback source (owner decision, 2026-08-27): the pinned immutable UPSTREAM URL the
    /// mirror copy was built from. Tried only after <see cref="DownloadUrl"/>'s whole retry budget
    /// is exhausted or it failed non-transiently — including a completed download whose bytes miss
    /// the <see cref="Sha256Hash"/> pin, which gates BOTH sources equally. Single-file layout only;
    /// bundle members carry their own <see cref="ModelFile.FallbackUrl"/>.
    /// </summary>
    public string? FallbackUrl { get; init; }
    public long FileSizeBytes { get; init; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; init; }
    public bool SupportsStreaming { get; init; }

    /// <summary>
    /// Optional SHA-256 hash for verifying model integrity after download.
    /// When set, the download manager will validate the file against this hash.
    /// </summary>
    public string? Sha256Hash { get; init; }

    /// <summary>
    /// The files of a MULTI-FILE model bundle (TRN-1 step 2), or <c>null</c> for the single-file
    /// layout every model shipped before it uses.
    ///
    /// <para><b>This property is the discriminator, and null is the whole backward-compatibility
    /// story:</b> a null <c>Files</c> takes the byte-identical legacy download path, so the fourteen
    /// Whisper <c>.bin</c> models already on users' disks are untouched. Non-null selects the
    /// transactional bundle install — staging directory, per-file SHA, completion manifest written
    /// last, atomic commit.</para>
    ///
    /// <para>A bundle entry must NOT also set <see cref="DownloadUrl"/> or <see cref="Sha256Hash"/>;
    /// those describe the single-file layout, and carrying both would leave which one wins to
    /// whichever branch happened to be checked first. Validated at install time.</para>
    ///
    /// <para><see cref="FileSizeBytes"/> on a bundle entry is the AGGREGATE of
    /// <see cref="ModelFile.FileSizeBytes"/> — it feeds both the Models-page size display and the
    /// free-space precheck, and having one source keeps them from disagreeing.</para>
    /// </summary>
    public IReadOnlyList<ModelFile>? Files { get; init; }

    /// <summary>True when this entry describes a multi-file bundle. **Layout only** — see
    /// <see cref="Runtime"/> for which engine serves it. The two are independent facts.</summary>
    public bool IsBundle => Files is { Count: > 0 };

    /// <summary>
    /// Which local inference stack serves this model, or <c>null</c> for a cloud row (TRN-1 step 3).
    ///
    /// <para><b>Engine, not layout.</b> The obvious shortcut is to route on <see cref="Files"/> —
    /// single-file ⇒ whisper.cpp, bundle ⇒ the new engine — and it is wrong: that is STORAGE SHAPE.
    /// A future single-file non-Whisper model would be claimed by whisper.cpp and fail inside native
    /// code, which is the exact silent substitution the local-runtime seam was built to end. It also
    /// disagrees with <see cref="IsBundle"/>, which additionally requires a non-empty list.</para>
    ///
    /// <para><b>Deliberately nullable rather than defaulted.</b> A default would silently classify
    /// every future local model — and all 20+ CLOUD rows, which share this type — as Whisper, making
    /// the enum the next wrong-guess classifier. Cloud rows carry <c>null</c>; every LOCAL row must
    /// declare a value, which <c>ModelsPageTests</c> enforces.</para>
    /// </summary>
    public LocalRuntimeKind? Runtime { get; init; }
}

/// <summary>
/// The local inference stacks. A model names the engine that serves it; the engine never infers
/// ownership from what the files look like.
/// </summary>
public enum LocalRuntimeKind
{
    /// <summary>whisper.cpp via Whisper.net — every local model shipped before TRN-1.</summary>
    Whisper,
    /// <summary>sherpa-onnx. Declared here in the prerequisite step so routing and copy can branch
    /// on it before the engine itself exists; no native dependency is implied by the value.</summary>
    Parakeet,
}
