namespace VoiceWink.Services.Audio;

/// <summary>
/// Thrown when an audio capture operation cannot complete within its bounded
/// time budget — typically because the Windows audio service is wedged under
/// system contention (heavy CPU load from another process). Distinguishes
/// audio-specific timeouts from generic recording failures so the caller
/// (e.g. <c>MainViewModel.StartRecordingAsync</c>) can surface an audio-
/// specific actionable error to the user ("Microphone unavailable — try
/// again") instead of the generic recording-failure copy.
/// </summary>
public sealed class AudioCaptureUnavailableException : InvalidOperationException
{
    public AudioCaptureUnavailableException(string message) : base(message) { }
}
