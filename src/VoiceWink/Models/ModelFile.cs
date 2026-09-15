namespace VoiceWink.Models;

/// <summary>
/// One file inside a multi-file model bundle (TRN-1 step 2).
///
/// <para>A bundle is all-or-nothing: Parakeet needs encoder + decoder + joiner + tokens installed
/// together, and a half-bundle that presents as installed surfaces as a native crash inside the
/// runtime rather than as a download error. That is why every field here is required and why
/// <see cref="Sha256Hash"/> is NOT nullable — unlike the legacy single-file rows, where it is
/// optional. Optional integrity in a bundle defeats the point of installing transactionally.</para>
/// </summary>
public sealed record ModelFile
{
    /// <summary>
    /// Where the file lands inside the bundle directory. Validated by
    /// <see cref="Helpers.BundleRelativePathGuard"/> BEFORE any byte is written — a bad entry fails
    /// the whole install rather than leaving N−1 files on disk.
    /// </summary>
    public required string RelativePath { get; init; }

    public required string Url { get; init; }

    /// <summary>
    /// TRN-33 fallback source (owner decision, 2026-08-27): the pinned immutable UPSTREAM URL the
    /// mirror copy of this file was built from. Tried only after <see cref="Url"/>'s whole retry
    /// budget is exhausted or it failed non-transiently — including a completed download whose
    /// bytes miss the <see cref="Sha256Hash"/> pin, which gates BOTH sources equally. Null means
    /// no fallback exists and the primary is the only source.
    /// </summary>
    public string? FallbackUrl { get; init; }

    /// <summary>Exact byte count. Feeds the free-space precheck AND the installed-state check, where
    /// a size mismatch means a truncated payload and therefore "not installed".</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>Mandatory — see the type's remarks.</summary>
    public required string Sha256Hash { get; init; }
}
