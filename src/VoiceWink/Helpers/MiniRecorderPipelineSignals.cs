using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-26: which of the pipeline pill's two RECORDING-LIVENESS signals — the animated waveform and
/// the elapsed timer — are on screen, given the pipeline state and whether a transient pipeline
/// notice (AUD-1 mic-fallback, the poor-connection notice) currently owns the status line.
///
/// <para><b>Why a rule and not four inline assignments.</b> The status text is a FULL-SPAN overlay
/// (<c>ColumnSpan 7</c>, hit-test-transparent) centred on the pill, and the waveform is stretched
/// across the star column underneath it — so the two occupy the same pixels by construction. The
/// notice used to leave the waveform live and animating under its own text, and the owner reported
/// exactly the predictable outcome (2026-08-25): "visually it gets mixed up with the normal waveform
/// and recording visuals". Nothing in the renderer flagged it, because the collision lives in the
/// GAP between the state arm (shows the waveform) and the notice block (writes the text) — two code
/// paths that never mention each other. This rule closes the gap by owning both elements for every
/// combination, so the next edit to either path cannot silently re-open it.</para>
///
/// <para><b>The waveform yields; the timer does not.</b> They differ in the one way that matters
/// here: the waveform is UNDER the text, the timer is BESIDE it (its own grid column, kept clear of
/// the text by <see cref="MiniRecorderStatusPlacement"/>'s zone margins). So the waveform is the
/// only one that has to go, and the timer is what carries the liveness the waveform stops carrying
/// — visibly counting up for the notice's 8 s, alongside the still-pulsing dot. Collapsing it too
/// (which the notice block also used to do) would have bought the text the timer column's width and
/// told the user nothing about whether their recording was still running.</para>
/// </summary>
internal static class MiniRecorderPipelineSignals
{
    /// <summary>Is the live audio waveform shown?</summary>
    /// <param name="state">The pipeline state being rendered.</param>
    /// <param name="hasNotice">Is a transient pipeline notice occupying the status line?</param>
    internal static bool ShowsWaveform(RecordingState state, bool hasNotice)
        => state == RecordingState.Recording && !hasNotice;

    /// <summary>
    /// Is the elapsed timer shown? Deliberately independent of <paramref name="hasNotice"/> — see
    /// the type remarks: it does not overlap the notice text, and it is the liveness signal that
    /// replaces the withdrawn waveform. The parameter stays on the signature so the two elements
    /// read as one decision at the call site, and so a future notice state that DOES need the
    /// timer's column has somewhere to say so.
    /// </summary>
    /// <param name="state">The pipeline state being rendered.</param>
    /// <param name="hasNotice">Is a transient pipeline notice occupying the status line?</param>
    internal static bool ShowsElapsedTimer(RecordingState state, bool hasNotice)
        => state == RecordingState.Recording;
}
