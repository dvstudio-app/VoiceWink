using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// What a MiniRecorder stop-button tap does. Public (like <c>MainViewModel.StopTapAction</c>)
/// so test theory rows can name members in InlineData; the router itself stays internal.
/// </summary>
public enum MiniRecorderStopAction
{
    /// <summary>Deliberately inert — Idle with nothing to cancel, or an unknown state.</summary>
    None,
    CancelImageJob,
    CancelPipeline,
    ToggleRecording
}

/// <summary>
/// Pure routing for the pill's stop-button tap — WHICH MainViewModel entry the tap invokes
/// (the Transcribing/Enhancing arm routes to <c>CancelPipeline</c>, whose skip-vs-full-cancel
/// refinement stays <c>MainViewModel.ClassifyStopTap</c>'s job). Extracted from the inline
/// Tapped handler after the 2026-07-18 phantom-recording incident: the handler's Idle
/// fall-through called ToggleRecord, so the instant a stop tap cancelled the background image
/// job (4 ms), the SAME button pixels flipped from "cancel the job" to "start a recording"
/// and the user's follow-up tap started one. At Idle the button now cancels the running image
/// job or is inert — and an unknown/future state is inert too, never a recording trigger
/// (the obsolete, never-transitioned-to Busy lands there). Pinned by
/// <c>MiniRecorderStopRoutingTests</c> incl. a RecordingState count pin so a new state gets a
/// conscious arm.
/// </summary>
internal static class MiniRecorderStopRouting
{
    public static MiniRecorderStopAction Decide(RecordingState state, bool isImageJobRunning)
        => state switch
        {
            RecordingState.Idle when isImageJobRunning => MiniRecorderStopAction.CancelImageJob,
            RecordingState.Idle => MiniRecorderStopAction.None,
            RecordingState.Starting or RecordingState.Recording => MiniRecorderStopAction.ToggleRecording,
            RecordingState.Transcribing or RecordingState.Enhancing => MiniRecorderStopAction.CancelPipeline,
            _ => MiniRecorderStopAction.None
        };

    /// <summary>
    /// The stop button's tooltip and accessible name: what a tap does now, read off
    /// <see cref="Decide"/> so the label and the routing cannot disagree (2026-10-14 tooltip pass).
    /// "Stop enhancing" rather than "Skip": a tap skips a text enhancement and keeps the transcript,
    /// but cancels the whole run where there is nothing to fall back to (MainViewModel.ClassifyStopTap).
    /// Null when a tap does nothing.
    /// </summary>
    public static string? TooltipFor(RecordingState state, bool isImageJobRunning)
        => Decide(state, isImageJobRunning) switch
        {
            MiniRecorderStopAction.ToggleRecording => "Stop recording",
            MiniRecorderStopAction.CancelPipeline => state == RecordingState.Enhancing ? "Stop enhancing" : "Cancel transcription",
            MiniRecorderStopAction.CancelImageJob => "Cancel image",
            _ => null
        };
}
