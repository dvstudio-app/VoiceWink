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
