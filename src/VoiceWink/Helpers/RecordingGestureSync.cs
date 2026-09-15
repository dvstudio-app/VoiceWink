using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>
/// Pure decisions for keeping <c>HotkeyService</c>'s semantic gesture state in sync with the
/// recording pipeline (the 2026-07-17 push-to-talk inversion fix). The hands-free latch and
/// the current gesture's started-recording mark must never outlive the recording they
/// describe: a latch with nothing recording permanently inverts the tap/hold machine —
/// press-while-idle takes the StopHandsFree branch and arms no gesture, so a hold's release
/// is inert (PTT dead) while tap-tap keeps limping along by accident.
/// </summary>
internal static class RecordingGestureSync
{
    /// <summary>
    /// True when a pipeline transition means the semantic gesture state must be invalidated:
    /// leaving the recording-active region (Starting/Recording), or ANY arrival at Idle — the
    /// terminal every pipeline path funnels through. The Idle clause sweeps busy-state taps:
    /// <c>ToggleRecordAsync</c> ignores presses during Transcribing/Enhancing, so their brief
    /// releases could otherwise arm the latch with nothing recording.
    /// </summary>
    internal static bool ShouldInvalidate(RecordingState oldValue, RecordingState newValue)
        => ((oldValue is RecordingState.Starting or RecordingState.Recording)
                && newValue is not (RecordingState.Starting or RecordingState.Recording))
           || (newValue == RecordingState.Idle && oldValue != RecordingState.Idle);

    /// <summary>
    /// Transition-hook body: invokes <paramref name="invalidate"/> exactly when
    /// <see cref="ShouldInvalidate"/> says so. Action-taking so tests pin the
    /// predicate-to-invocation contract without constructing a MainViewModel.
    /// </summary>
    internal static void OnTransition(RecordingState oldValue, RecordingState newValue, Action invalidate)
    {
        if (ShouldInvalidate(oldValue, newValue))
            invalidate();
    }
}
