namespace VoiceWink.Services.Transcription;

/// <summary>
/// The install refused to proceed because it could not determine what is already on disk
/// (TRN-1 step 3, PR A) — a file lock, an ACL denial, a reparse point, or a manifest written by a
/// NEWER build. Nothing was changed.
///
/// <para><b>Typed so it does not read as a crash.</b> This is an expected, environmental,
/// fully-handled condition, and the generic download catch does two wrong things with a bare
/// <see cref="IOException"/>: it replaces the specific guidance with "Download failed. Please try
/// again.", and it logs at Error — which the Sentry sub-logger forwards, so a locked folder becomes
/// a crash report. That is the provider-noise class the diagnostics rules already warn about.</para>
///
/// <para><see cref="IsRetryable"/> separates "close the thing holding it and retry" from "retrying
/// cannot help" — a durable ACL denial or a newer-schema manifest is not a transient condition, and
/// telling the user to try again would be advice that can never work.</para>
/// </summary>
public sealed class ModelInstallBlockedException : IOException
{
    public ModelInstallBlockedException(string message, bool isRetryable)
        : base(message) => IsRetryable = isRetryable;

    /// <summary>Whether trying again could plausibly succeed without the user changing something.</summary>
    public bool IsRetryable { get; }
}
