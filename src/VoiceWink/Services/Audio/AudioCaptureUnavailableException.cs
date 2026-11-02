namespace VoiceWink.Services.Audio;

/// <summary>
/// Thrown when an audio capture operation cannot complete within its bounded
/// time budget — typically because the Windows audio service is wedged under
/// system contention (heavy CPU load from another process) — or, with
/// <see cref="NoMicrophone"/> set, when Windows reports no capture endpoint at all.
/// Distinguishes audio-specific failures from generic recording failures so the caller
/// (e.g. <c>MainViewModel.StartRecordingAsync</c>) can surface an audio-
/// specific actionable error to the user ("Microphone unavailable — try
/// again") instead of the generic recording-failure copy.
/// </summary>
public sealed class AudioCaptureUnavailableException : InvalidOperationException
{
    public AudioCaptureUnavailableException(string message, bool noMicrophone = false) : base(message)
        => NoMicrophone = noMicrophone;

    /// <summary>AUD-35: Windows answered "not found" for the default capture endpoint — positive evidence of no microphone.</summary>
    public bool NoMicrophone { get; }
}
