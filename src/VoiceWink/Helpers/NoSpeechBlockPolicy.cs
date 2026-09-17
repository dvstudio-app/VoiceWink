using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-36 (2026-09-17): whether a no-speech BLOCK is final, or is deferred to the engine that
/// would have transcribed the recording. The gate's verdict is a SUSPICION — two Silero builds
/// scoring a conditioned copy — while an engine's decode is the FACT, and on 2026-09-17 the two
/// disagreed 4 times in one afternoon: open-office whispering at −54…−58 dBFS blocked through
/// BOTH runtimes; three were retried and every Retry pasted (203, 102 and 21 characters after
/// enhancement — the retry skips the gate and changes nothing else), and the fourth decodes to 24
/// characters through the shipped path in the harness. The owner's rule from that afternoon: a
/// recording the same model can transcribe must never be shown as "no speech".
///
/// <para><b>Parakeet decides, nothing else does — a measured scope, not symmetry.</b> The gate
/// exists for engines that INVENT text on non-speech, and for Whisper that is MEASURED rather than
/// inherited from REL-15 (2026-09-17, the same 10 must-block files through the shipped decode path):
/// `ggml-large-v3-turbo-q8_0` answered "Thank you." on 6 of 7 — digital silence, white noise,
/// clicks, a 300 ms burst, typing, hum — while tiny and small returned annotations like
/// `[BLANK_AUDIO]`, which are 13 characters that PASS <see cref="TranscriptContent.IsEmpty"/> and
/// would be pasted verbatim. No Whisper row returns nothing, so the exclusion is earned on all of
/// them. A cloud upload of silence additionally costs money and can echo its hints — structural,
/// not measured. Parakeet does the opposite: it decoded 0 characters on
/// every must-block file (real train noise, digital silence, flatline, synthetic silence, white
/// noise — the AUD-24 C corpus through the shipped pcpp path: +12/+13 dB decode gain on the two
/// noise files, +0 on the three silent ones, which sit under the decode floor) and on TRN-10b's
/// +30 dB-boosted synthetic noise (sherpa era), and it was silent on the digital-silence clip
/// where four Whisper models hallucinated (TRN-1). One measured counter-example is on the record:
/// a 3 s near-silent take on a dead default device decoded a plausible two-word phrase — the
/// bound the decision record and the AUD-36 card state. So for Parakeet a block costs a real
/// whispered dictation and protects almost nothing; deferring costs one decode on genuine silence
/// (107–1242 ms on the silence and noise files, 2.5–32 s) and the same "No speech detected" pill
/// afterwards. The evidence is pcpp-only, so the sherpa-era path returns EMPTY from the
/// gate-blocked entry without decoding. A future engine earns this scope with its own
/// silence-and-noise measurement, never by being local.</para>
///
/// <para><b>An ESTABLISHED all-zero recording is never deferred.</b> AUD-21/34's digital-silence
/// verdict is a fact about the capture stream, not a suspicion about the speaker: the decode
/// would return nothing, and the block path is what re-arms the standing capture. False means
/// "not established" (<c>PreparedRecording</c>'s contract) as well as "not silent", so the
/// uncertain case takes the cheap decode.</para>
///
/// Pure; pinned by <c>NoSpeechBlockPolicyTests</c>.
/// </summary>
internal static class NoSpeechBlockPolicy
{
    /// <summary>True when a block should be handed to the engine — the recording is transcribed
    /// and an EMPTY result is what shows "No speech detected". <paramref name="attemptModel"/> is
    /// the model this attempt would transcribe with, resolved through the catalog's ONE
    /// name→engine lookup (<see cref="PredefinedModels.RuntimeOf"/>, shared with
    /// <see cref="EmptyTranscriptMessage"/> so the two cannot disagree about which engine is
    /// Parakeet); any unknown, blank or cloud name keeps the block.</summary>
    internal static bool EngineDecides(string? attemptModel, bool digitallySilent)
    {
        if (digitallySilent) return false;
        return PredefinedModels.RuntimeOf(attemptModel) == LocalRuntimeKind.Parakeet;
    }
}
