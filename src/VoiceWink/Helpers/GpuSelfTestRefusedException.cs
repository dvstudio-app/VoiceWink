namespace VoiceWink.Helpers;

/// <summary>
/// TRN-64: a local Whisper decode was REFUSED because the GPU it would run on failed — or never
/// finished — its self-test, in this process. Thrown by <c>WhisperTranscriptionService.TranscribeAsync</c>
/// before it takes the model lock, so it reaches the REL-12 failure surface exactly like any other
/// transcription-stage failure: the WAV is retained, the retry pill arms, and the TRN-17 picker
/// offers the other runnable engines. Picking Whisper again refuses again with the same sentence.
///
/// <para><b>Why a refusal and not a fallback.</b> The loaded Vulkan runtime is process-frozen —
/// there is no in-process CPU path for Whisper — and the alternative measured this week was 13
/// minutes of garbage pasted as a transcript. Wrong output must produce nothing; the restart
/// (offered once per session) is the way to the CPU path.</para>
///
/// <para><see cref="Reason.Unstable"/> is the one kind that records nothing and offers no
/// restart: the loaded model changed twice under a waiting decode — a benign reload race, not a
/// GPU fact (Kimi r2 C2 — this is deliberately NOT <see cref="GpuSelfTestOutcome.Inconclusive"/>).</para>
///
/// <para><see cref="Message"/> IS the user sentence: app-authored, pill-sized, never carrying a
/// transcript, a path, or anything but the adapter model name.</para>
/// </summary>
public sealed class GpuSelfTestRefusedException : InvalidOperationException
{
    public enum Reason
    {
        /// <summary>The self-test decoded the golden clip WRONG on the GPU.</summary>
        Fail,
        /// <summary>The self-test did not finish within its budget.</summary>
        Inconclusive,
        /// <summary>The model changed twice while the decode waited for a verdict.</summary>
        Unstable,
    }

    public Reason Kind { get; }

    /// <summary>The adapter the verdict names, or null.</summary>
    public string? GpuName { get; }

    public GpuSelfTestRefusedException(Reason kind, string? gpuName)
        : base(UserMessageFor(kind))
    {
        Kind = kind;
        GpuName = gpuName;
    }

    /// <summary>The pill sentence (app-authored; the pill budget is ~55 code units).</summary>
    public string UserMessage => Message;

    /// <summary>Whether the restart dialog is the right follow-up: a verdict about the GPU is; a
    /// reload race is not.</summary>
    public bool OffersRestart => Kind != Reason.Unstable;

    internal static string UserMessageFor(Reason kind) => kind switch
    {
        Reason.Fail => "Whisper failed the GPU check \u2014 restart to use the CPU",
        Reason.Inconclusive => "GPU check didn't finish \u2014 restart to use the CPU",
        _ => "Model changed during the GPU check \u2014 try again",
    };
}
