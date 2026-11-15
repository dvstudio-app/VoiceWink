using Serilog;

namespace VoiceWink.Helpers;

/// <summary>IMG-23 the single-image job's failure line, kept out of MainViewModel (hub rule).</summary>
internal static class ImageJobFailureLog
{
    /// <summary>Nothing for a timeout already logged where it was thrown; Warning without a stack for a provider-shaped failure; Error with the exception otherwise.</summary>
    public static void Write(ILogger logger, Exception ex)
    {
        if (ex is ImageGenerationTimeoutException)
            return;
        if (ex is HttpRequestException or InvalidOperationException or TimeoutException
            or InvalidApiKeyFormatException)
            logger.Warning("Image generation job failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);
        else
            logger.Error(ex, "Image generation job failed");
    }
}
