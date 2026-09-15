namespace VoiceWink.Services.Transcription;

/// <summary>Which on-disk shape a model takes.
/// <para>Public only because it rides <c>ModelDownloadManager</c>'s public surface — an
/// accessibility formality inside the app assembly, not an API commitment (same reasoning as
/// <c>MiniRecorderPresentation</c> and <c>ProviderModelList</c>).</para></summary>
public enum ModelLocationKind
{
    /// <summary>A single <c>.bin</c> file — every model shipped before TRN-1 step 2.</summary>
    SingleFile,
    /// <summary>A directory of files plus a completion manifest.</summary>
    Bundle,
}

/// <summary>
/// Where an INSTALLED model lives, and in what shape (TRN-1 step 2).
///
/// <para>Typed rather than a bare string because <c>DownloadModelAsync</c> and the install-state
/// query previously returned a path that sometimes meant a file and sometimes a directory — the
/// caller could not tell which, and <c>File.Delete</c> on a directory throws. The kind travels WITH
/// the path so a caller cannot forget to ask.</para>
/// </summary>
public sealed record ModelLocation(ModelLocationKind Kind, string Path);

/// <summary>
/// What a delete actually accomplished. Phase-aware because deletion is not atomic: the manifest is
/// removed FIRST so the bundle stops reading as installed, and only then is the payload swept.
///
/// <para>The distinction is load-bearing, not cosmetic. Once the marker is gone the model is no
/// longer installed, so a cleanup failure after that point must NOT leave the row claiming the model
/// is present — while a failure BEFORE invalidation must, because the model really is still there.
/// Both reviewers reached this independently.</para>
/// </summary>
internal enum ModelDeleteOutcome
{
    /// <summary>Nothing on disk. A success: the caller wanted it gone and it is gone.</summary>
    NotPresent,
    /// <summary>Marker removed and payload swept.</summary>
    Deleted,
    /// <summary>Failed while the marker was still intact — the model IS still installed.</summary>
    FailedBeforeInvalidation,
    /// <summary>
    /// Marker removed, payload sweep failed. The model is NOT installed; debris remains and is
    /// cleared by the next install's step 0, which deletes any non-installed destination.
    /// </summary>
    InvalidatedButCleanupFailed,
}
