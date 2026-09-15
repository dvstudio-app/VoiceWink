using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// The no-speech block's choreography (the PRM-4 echo-block idiom applied to the VAD
/// gate), extracted so tests can pin the sequence without a 20-dependency
/// <c>MainViewModel</c>. RETENTION RUNS FIRST — before the idle transition and every
/// other observable callback (Codex diff rounds 2 + 3): the retain callback
/// (keepWavForRetry + ledger-track) must complete before ANY code that can reach a
/// throwing PropertyChanged subscriber, event handler, or UI surface, so the
/// pipeline's finally can never delete a WAV that an armed (or half-armed) Retry
/// points at. Then: the terminal-presentation fence + Idle transition, status BEFORE
/// arming (ArmRetry captures StatusText into the pill's own text), amber arm (a
/// suspicion, not a failure — the echo-gate owner-UAT precedent), pill, hotkey reset.
/// Pinned by <c>NoSpeechBlockActionTests</c> incl. fault-injection rows.
/// </summary>
internal static class NoSpeechBlockAction
{
    // CAUSE ONLY — the "— Retry to transcribe anyway" tail was dropped (owner, 2026-08-01).
    // This message is only ever shown on a pill that carries the Retry button, so the tail
    // spent half the line restating the control sitting next to it. (The tail itself had
    // replaced "…Retry to keep it" on 2026-07-31, because "keep it" named what happens to
    // the FILE rather than what Retry buys; that reading is now carried by the button.)
    // This supersedes the 2026-07-25 pill-copy-consistency rule that all three retry pills
    // share one tail: this one and MainViewModel's empty-transcript pill are cause-only,
    // while the echo pill ("Possible prompt echo — Retry to transcribe anyway") keeps its
    // tail — the owner scoped the change to these two. Retry still bypasses the gate
    // (TranscriptionRetryContext.SkipNoSpeechGate); only the copy changed.
    internal const string StatusMessage = "No speech detected";

    // AUD-21. "No speech detected" blames the user's voice, and on 2026-08-25 it said so twice about
    // a capture stream that had produced 20 s of exact digital zeros while the microphone was
    // provably working — the user retried, believing they had not been heard. When every sample is
    // zero the honest cause is the capture, not the speaker. CAUSE ONLY, per the 2026-08-01 owner
    // rule: no "— Retry to …" tail, because this message only ever appears on a pill that already
    // carries the Retry button.
    /// <summary>AUD-21. Deliberately does NOT accuse the microphone: the incident's actual fault
    /// was the app's own capture stream while the endpoint was provably live, so "from the
    /// microphone" would send the user to Windows sound settings where everything looks fine —
    /// recreating the very experience this message exists to end. Also deliberately distinct from
    /// AUD-11's stall copy ("No audio — check microphone", buffers STOPPED arriving) so a support
    /// reader can tell the two faults apart from a user's paraphrase (self-review L3).</summary>
    internal const string SilentCaptureStatusMessage = "No audio captured";

    /// <summary>Which cause to name. <paramref name="digitallySilent"/> false means "not
    /// established" as well as "not silent" (see <c>PreparedRecording</c>: unmeasurable, a retry,
    /// or — AUD-34 — all-zero but too short to judge), so the ordinary message is the default in
    /// every uncertain case — this must never assert a microphone fault it cannot prove.</summary>
    internal static string MessageFor(bool digitallySilent)
        => digitallySilent ? SilentCaptureStatusMessage : StatusMessage;

    internal static void Execute<TContext>(
        Action retainWav,
        Action transitionToIdle,
        TContext retryContext,
        Action<string> setStatus,
        Action<TContext, MiniRecorderTone> armRetry,
        Action showPill,
        Action resetHotkey,
        string statusMessage)
    {
        retainWav();
        transitionToIdle();
        setStatus(statusMessage);
        armRetry(retryContext, MiniRecorderTone.Warning);
        showPill();
        resetHotkey();
    }
}
