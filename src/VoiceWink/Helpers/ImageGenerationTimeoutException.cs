namespace VoiceWink.Helpers;

/// <summary>IMG-23 an image request's timeout, already logged as one Warning where it was thrown, so later catch sites log nothing more.</summary>
internal sealed class ImageGenerationTimeoutException : TimeoutException
{
    public ImageGenerationTimeoutException(string message, Exception? inner) : base(message, inner) { }
}
