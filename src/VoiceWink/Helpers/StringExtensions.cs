namespace VoiceWink.Helpers;

/// <summary>
/// General-purpose string extension methods.
/// </summary>
internal static class StringExtensions
{
    /// <summary>Truncate a string for safe log output (avoids flooding logs with huge error bodies).</summary>
    internal static string TruncateForLog(this string text, int maxLength = 200)
        => text.Length > maxLength ? text[..maxLength] + "..." : text;

    /// <summary>A provider's error-response body logs up to this many characters: 200 cut OpenRouter's 652-byte routing reason in half.</summary>
    internal const int ErrorBodyLogChars = 4000;

    /// <summary>Bound an error-response body for the local log; still capped, since a broken endpoint can send a whole HTML page.</summary>
    internal static string TruncateErrorBodyForLog(this string text) => text.TruncateForLog(ErrorBodyLogChars);
}
