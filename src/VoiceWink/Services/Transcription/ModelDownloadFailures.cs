namespace VoiceWink.Services.Transcription;

using global::System.IO;

/// <summary>
/// TRN-33 (Codex round-2 B2): marks a failure as originating from the DOWNLOAD SOURCE — the
/// remote host, the transfer, or the bytes it delivered — as opposed to a LOCAL fault (a locked
/// .download file, a full disk, a hash-read error). The distinction is load-bearing for privacy,
/// not style: the shipped privacy policy says Hugging Face is contacted ONLY when
/// models.voicewink.app cannot deliver the file correctly, so the mirror→upstream fallback may
/// engage only on failures that carry this marker (or are already remote-typed as
/// <see cref="global::System.Net.Http.HttpRequestException"/>). A local fault falling back would
/// send an undisclosed request to an independent third party — and could not succeed anyway.
///
/// <para>Each implementation derives from the exception type the pre-fallback code threw at the
/// same site, so the transient-retry classification (<c>IsTransientDownloadError</c>'s
/// <see cref="IOException"/> arm) and callers' exception-shape expectations are preserved; the
/// exact-type test assertions were updated deliberately where the concrete type refined.</para>
/// </summary>
internal interface IModelSourceFailure;

/// <summary>A remote transfer fault (send, response read, stall, resume-protocol violation) —
/// transient via the <see cref="IOException"/> arm, and a source failure for the fallback.</summary>
internal sealed class ModelSourceIOException : IOException, IModelSourceFailure
{
    public ModelSourceIOException(string message) : base(message) { }
    public ModelSourceIOException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The disk is FULL — and only that. Two raise classes: a local site (the partial's
/// open, a write, the flush) failing with <c>ERROR_DISK_FULL</c> or <c>ERROR_HANDLE_DISK_FULL</c>,
/// and the free-space PRE-CHECKS refusing before a byte is written (<c>DownloadModelAsync</c>'s
/// 2x-remaining test and <c>EnsureFreeSpaceForBundle</c> — the statistically dominant
/// manifestation, since the 2x margin fires first; until NET-5b they threw a plain IOException and
/// reached the view model's generic "Please try again" arm at Sentry-forwarded Error). Every other
/// local <see cref="IOException"/> keeps its pre-NET-5 classification, because a locked file is the
/// opposite class (a retry clears it) and a permissions denial is not even an IOException (it is
/// <see cref="UnauthorizedAccessException"/>, which this type never sees).
///
/// <para>Deliberately NOT <see cref="IModelSourceFailure"/>: the source did nothing wrong, so this
/// must never reach the mirror-to-upstream fallback (the privacy boundary <c>RemoteAsync</c>
/// exists to hold — a local fault must not cause a Hugging Face request). It is also NOT
/// transient, which is the point: retrying a write to a full disk cannot succeed, and before NET-5
/// a bare <see cref="IOException"/> from the write half was classified transient and retried three
/// times per source across two sources, logging "failed transiently" about a fault no retry will
/// ever fix.</para>
///
/// <para>Its partial is KEPT, unlike every other non-transient failure. The delete at the outer
/// catch exists so a POISONED partial cannot survive — bytes from a host that lied, or that fail
/// the SHA pin. A disk-full partial is neither: those bytes are valid and simply incomplete, so
/// deleting them would trade wasted retries for a lost 874 MB of progress and leave the user no
/// better off than the defect did. Single-file downloads only: a BUNDLE's staging directory is
/// cleared on any failure by its own path, and this type does not change that.</para></summary>
internal sealed class ModelLocalIOException : IOException
{
    /// <summary>The pre-check refusal: no underlying fault, the message carries the MB arithmetic.</summary>
    public ModelLocalIOException(string message) : base(message) { }
    public ModelLocalIOException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The server-declared total was not reached — the connection closed early. Derives
/// <see cref="EndOfStreamException"/> (itself an <see cref="IOException"/>): transient, resumable,
/// and a source failure.</summary>
internal sealed class ModelSourceEndOfStreamException : EndOfStreamException, IModelSourceFailure
{
    public ModelSourceEndOfStreamException(string message) : base(message) { }
}

// NOTE: the empty-body guard keeps throwing the plain sealed InvalidDataException — it cannot
// derive a marker (CS0509), it is thrown at exactly ONE site (a remote empty body, i.e. a source
// failure), and the fallback filter therefore allowlists the concrete type instead.

/// <summary>The source delivered COMPLETED bytes that miss the catalog's SHA-256 pin — wrong
/// bytes are a source failure (the fallback may try), and the type is what lets the source loop's
/// clean-retry arm (Codex round-2 B1: a carried transient mirror partial welded to a valid
/// upstream suffix fails verification through no fault of the upstream) distinguish a checksum
/// failure from every other <see cref="InvalidOperationException"/>.</summary>
internal sealed class ModelChecksumMismatchException : InvalidOperationException, IModelSourceFailure
{
    public ModelChecksumMismatchException(string message) : base(message) { }
}
