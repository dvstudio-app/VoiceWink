namespace VoiceWink.Helpers;

/// <summary>
/// General-purpose string extension methods.
/// </summary>
internal static class StringExtensions
{
    /// <summary>Truncate a string for safe log output (avoids flooding logs with huge error bodies).</summary>
    internal static string TruncateForLog(this string text, int maxLength = 200)
        => text.Length > maxLength ? text[..maxLength] + "..." : text;
}
