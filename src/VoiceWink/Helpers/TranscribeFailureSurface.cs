namespace VoiceWink.Helpers;

/// <summary>
/// Pure routing decisions for the transcription pipeline's failure paths (REL-12).
/// Extracted so the precedence rules are test-pinned without the 20-dependency
/// <c>MainViewModel</c>: which surface a pipeline exception gets, and whether a
/// user-cancelled attempt keeps the recording.
/// </summary>
internal static class TranscribeFailureSurface
{
    internal enum Kind
    {
        /// <summary>Transcription+enhancement succeeded; only the paste (or later
        /// fail-soft bookkeeping) failed and the text is on the clipboard — the
        /// PILL-3 clipboard-fallback message wins over everything.</summary>
        ClipboardFallback,

        /// <summary>Failed at-or-before transcription with the WAV still on disk —
        /// retain the recording and arm the retry pill.</summary>
        RetryPill,

        /// <summary>No recoverable recording (post-transcription failure, or the
        /// WAV vanished) — today's plain error pill.</summary>
        PlainErrorPill,
    }

    /// <summary>
    /// Route the pipeline's generic exception catch. Precedence: clipboard-fallback
    /// (the failure happened AFTER the user's text was secured) > retry (a
    /// transcription-stage failure with recoverable audio) > plain error.
    /// </summary>
    internal static Kind Decide(bool clipboardFallbackAvailable, bool retryCandidateArmed, bool wavStillExists)
    {
        if (clipboardFallbackAvailable) return Kind.ClipboardFallback;
        if (retryCandidateArmed && wavStillExists) return Kind.RetryPill;
        return Kind.PlainErrorPill;
    }

    /// <summary>
    /// Whether a user-cancelled (OCE) pipeline run re-arms retry. Cancelling a RETRY
    /// attempt must not destroy the recording the feature exists to protect (the stop
    /// button is visible throughout Transcribing) — it returns to the retry-armed
    /// state. Cancelling a NORMAL recording keeps today's discard semantics.
    /// </summary>
    internal static bool ShouldReArmOnCancel(bool wasRetryRun, bool wavStillExists)
        => wasRetryRun && wavStillExists;

    /// <summary>
    /// REL-13: whether an EMPTY transcript outcome (provider returned 200 with no
    /// words, or the local text pipeline emptied the raw text) retains the recording
    /// and arms the retry pill. Same shape as the failure-path rule: only when the
    /// retry window is still open AND the WAV survives. Promoted from P3 by the live
    /// 2026-07-17 23:09 incident — a real 9-second dictation came back empty from
    /// Deepgram and the recording was deleted before it could be retried or diagnosed.
    /// </summary>
    internal static bool ShouldRetainEmptyTranscript(bool retryCandidateArmed, bool wavStillExists)
        => retryCandidateArmed && wavStillExists;

    /// <summary>
    /// Classify an <see cref="OperationCanceledException"/> escaping the transcription
    /// pipeline. `HttpClient.Timeout` expiry surfaces as `TaskCanceledException` with
    /// the CALLER token NOT cancelled (repo-documented: `AudioTranscribePage`'s catch,
    /// `ImageGenerationClient`'s translation) — the transcription clients send with the
    /// raw pipeline token and a 5-minute client budget, so a client-side upload timeout
    /// reaches the pipeline as an OCE. Treating it as a user cancel silently discarded
    /// the recording (Codex diff review) — it is a TIMEOUT failure and must route
    /// through the retry-retention path like any <see cref="TimeoutException"/>.
    /// </summary>
    internal static bool IsUserCancel(bool pipelineTokenCancelled)
        => pipelineTokenCancelled;
}
