namespace VoiceWink.Models.Enums;

/// <summary>
/// Presentation tone for the MiniRecorder pill's message states. Tone is about SEMANTICS
/// (what color/styling the message deserves); the display lifetime is derived from it by
/// <c>MiniRecorderTimings.DeadlineFor</c> (Success and Warning both timed — equal since the
/// 2026-08-02 warning cut, both 10 s since 2026-08-23 — Error never auto-dismisses at all,
/// ERR-PERSIST). The pinned invariant is `Attention >= Success`, so "Warning longer" — which this
/// line claimed until 2026-08-23 — was already wrong the moment the two converged.
/// </summary>
public enum MiniRecorderTone
{
    /// <summary>Green: everything worked (the redo pill's success arm).</summary>
    Success,

    /// <summary>Amber: an expected/normal condition that needs a user action — an auto-paste
    /// was declined but the content is safely on the clipboard (owner 2026-07-09: declined
    /// pastes are a normal Windows occurrence and must not read as malfunctions).</summary>
    Warning,

    /// <summary>Red: something actually failed (provider/transcription/audio errors, or a
    /// clipboard set failure — the content did NOT reach the clipboard).</summary>
    Error,
}
