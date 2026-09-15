using VoiceWink.Models;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Helpers;

/// <summary>
/// Binds the OpenAI <c>keywords[]</c> payload to a request and yields the matching echo set —
/// as ONE decision, so the two can never be made separately and drift.
///
/// <para>Extracted from <c>MainViewModel</c> rather than written inline: the transcribe path is
/// already a hub, and the property that matters here (wire payload ≡ echo set) is exactly the
/// kind of thing that deserves a test which does not need twenty-four constructor dependencies
/// to run.</para>
///
/// <para>The binding happens ONCE per recording, BEFORE the NET-1 connect retry wraps the call.
/// That ordering is the whole safety argument: <c>TranscriptionConnectRetry</c> re-invokes
/// <c>TranscribeAsync</c> with the same hints instance, so a user flipping the Models-page toggle
/// between attempt 1 and attempt 2 cannot change what attempt 2 sends, and the gate that runs
/// after the response is still matching against precisely those terms.</para>
/// </summary>
internal static class KeywordHintBinding
{
    /// <summary>
    /// The bound hints to send and the terms a generative recognizer could echo back.
    /// <paramref name="EchoableTerms"/> is empty whenever no keywords ride the request, so the
    /// caller can hand it to the echo gate unconditionally.
    /// </summary>
    internal readonly record struct Result(
        TranscriptionHints? Hints,
        IReadOnlyList<string> EchoableTerms);

    /// <summary>
    /// Attach a keyword projection when — and only when — the resolved transcriber transports
    /// hints as structured keywords AND the user has opted in.
    ///
    /// <para>Both conditions are evaluated here, together, and produce both outputs together.
    /// Splitting them was the flaw in the first design of this change: the pipeline decided the
    /// echo set from the transport while the client separately re-read a mutable toggle, which
    /// is two reads of changeable state around a retryable operation.</para>
    ///
    /// <para>A caller that never invokes this — the Audio Transcribe page — leaves
    /// <see cref="TranscriptionHints.KeywordProjection"/> null and therefore cannot send
    /// keywords. That page has no echo gate, so structural suppression is the correct answer
    /// rather than a flag someone must remember to pass.</para>
    /// </summary>
    internal static Result Bind(
        TranscriptionHints? hints,
        HintTransportKind transport,
        bool keywordsEnabled)
    {
        if (hints is null ||
            transport != HintTransportKind.StructuredKeywords ||
            !keywordsEnabled)
        {
            // Deliberately returns the hints UNCHANGED rather than stripping an existing
            // projection: this is the only place that attaches one, so there is never a stale
            // projection to clear, and silently blanking a caller's object would be a surprise.
            return new Result(hints, global::System.Array.Empty<string>());
        }

        var projection = hints.BuildKeywordProjection();
        return new Result(hints.WithKeywordProjection(projection), projection.IncludedTerms);
    }
}
