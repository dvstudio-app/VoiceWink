namespace VoiceWink.Models.Enums;

/// <summary>
/// Recording pipeline state machine.
/// </summary>
public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Transcribing,
    Enhancing,
    [System.Obsolete("Unused — state machine never transitions to Busy")] Busy
}
