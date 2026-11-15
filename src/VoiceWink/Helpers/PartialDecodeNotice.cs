namespace VoiceWink.Helpers;

/// <summary>TRN-96b: what the pill says, and whether Retry is offered, when part of a delivered dictation decoded empty; it outranks the AI redo pill, failed or not, because Retry re-runs both steps.</summary>
public static class PartialDecodeNotice
{
    /// <summary>The pill sentence: the cause only, within the 55-unit pill budget.</summary>
    public const string Message = "Part of the recording wasn't transcribed";

    /// <summary>Offer Retry only for a paste that landed on a recording still on disk; a declined paste keeps its own pill.</summary>
    public static bool ShouldOfferRetry(int lostChunks, bool pasteLanded, bool wavStillExists)
        => lostChunks > 0 && pasteLanded && wavStillExists;

    /// <summary>The Retry this notice arms: the recording already passed the no-speech gate, and the first paste already sent any Enter.</summary>
    public static ViewModels.MainViewModel.TranscriptionRetryContext RetryContextFor(
        ViewModels.MainViewModel.TranscriptionRetryContext candidate)
        => candidate with { SkipNoSpeechGate = true, WasPushToTalk = false };
}
