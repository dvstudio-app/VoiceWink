namespace VoiceWink.Helpers;

/// <summary>Outcome of a voice-activity evaluation over a recording.</summary>
public enum NoSpeechVerdict
{
    /// <summary>Speech detected — proceed to transcription.</summary>
    SpeechDetected,

    /// <summary>No (or too little) speech — block, retain the WAV, offer Retry.</summary>
    NoSpeech,

    /// <summary>VAD unavailable (model missing/corrupt, native failure) — the caller
    /// falls back to the legacy RMS gate for this recording.</summary>
    Unavailable,
}

/// <summary>
/// Pure routing for the pre-transcription no-speech gate. Takes the evaluation as a
/// CALLBACK so a bypassing retry provably never runs VAD at all (a pre-computed-verdict
/// shape could not pin the short-circuit): skip → proceed with NEITHER callback invoked;
/// <see cref="NoSpeechVerdict.NoSpeech"/> → block; <see cref="NoSpeechVerdict.Unavailable"/>
/// → the legacy RMS fallback decides; <see cref="NoSpeechVerdict.SpeechDetected"/> →
/// proceed. Pinned by <c>NoSpeechGateRoutingTests</c> (invocation counters).
/// </summary>
internal static class NoSpeechGateRouting
{
    internal static async Task<bool> ShouldBlockAsync(
        bool skipNoSpeechGate,
        Func<Task<NoSpeechVerdict>> evaluateAsync,
        Func<bool> rmsFallbackIsSilent)
    {
        if (skipNoSpeechGate) return false;

        var verdict = await evaluateAsync().ConfigureAwait(false);
        return verdict switch
        {
            NoSpeechVerdict.NoSpeech => true,
            NoSpeechVerdict.Unavailable => rmsFallbackIsSilent(),
            _ => false,
        };
    }
}
