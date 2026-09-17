namespace VoiceWink.Services.Transcription;

/// <summary>
/// AUD-36 (2026-09-17): a transcriber that JUDGES no-speech itself — one the pipeline may hand a
/// recording the no-speech gate blocked, on the understanding that an EMPTY result is the
/// expected answer for silence and not evidence of anything else. Only an engine measured silent
/// on the must-block class implements it (today: Parakeet — 0 characters on real train noise,
/// digital silence, flatline, synthetic silence and white noise through the shipped pcpp path;
/// <see cref="Helpers.NoSpeechBlockPolicy"/> holds the record). Whisper and every cloud client
/// deliberately do NOT: they are the engines the gate exists for.
///
/// <para>Why a per-call entry rather than a flag on <see cref="ITranscriptionService.TranscribeAsync"/>:
/// the knowledge is per REQUEST and belongs to one engine. Parakeet's TRN-50 safety net re-decodes
/// an empty GPU whole-call on non-silent audio through a throwaway CPU child ONCE per session; a
/// gate-blocked recording that decodes empty would otherwise spend that single attempt on the
/// audio's fault and leave a later real GPU failure unprotected — the fail-open this entry
/// prevents. Audio Transcribe has no gate and never takes this entry; a Retry from a no-speech
/// block skips the gate and takes the ordinary one.</para>
/// </summary>
public interface INoSpeechAwareTranscriber : ITranscriptionService
{
    /// <summary>Transcribe a recording the no-speech gate BLOCKED. Same contract as
    /// <see cref="ITranscriptionService.TranscribeAsync"/> — an empty string is a successful empty
    /// decode — except that the engine treats emptiness as the audio's answer: no device-fault
    /// recovery is spent on it and no collapse tripwire fires for it.</summary>
    Task<string> TranscribeSuspectedNoSpeechAsync(
        string audioFilePath, string? language, Models.TranscriptionHints? hints, CancellationToken ct);
}
