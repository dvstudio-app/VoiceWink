using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>
/// An image-generation reply parsed straight from its UTF-8 bytes
/// (<see cref="HttpResponseExtensions.ReadImageJsonAsync"/>), plus the start of the body for
/// the logs. A 4K image is 20–50 MB of base64; reading it into a string first held several
/// copies of it at once, and a garbage collection under that load froze the app on 2026-10-20.
/// </summary>
internal sealed record ImageReplyJson(JsonDocument Doc, string Excerpt) : IDisposable
{
    public void Dispose() => Doc.Dispose();

    /// <summary>
    /// Decodes a base64 image from the bytes the document already holds, without the string
    /// <c>GetString()</c> would allocate. A JSON null reads as absent, as <c>GetString()</c> did;
    /// any other non-string kind still throws <see cref="InvalidOperationException"/> for the
    /// drift guard.
    /// </summary>
    public static byte[]? Base64OrNull(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? null : value.GetBytesFromBase64();
}
