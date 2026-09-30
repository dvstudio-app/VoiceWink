using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-60: drains a parakeet-server child's captured stdout/stderr to EOF, handing every line
/// that <see cref="GgmlVulkanDeviceLine.TryParse"/> recognises to <c>onMatch</c> and dropping the
/// rest. Two properties are load-bearing and pinned by <c>ParakeetServerOutputPumpTests</c>:
///
/// <para><b>It reads to EOF whatever happens in <c>onMatch</c>.</b> An anonymous pipe has a small
/// kernel buffer; a child whose output nobody drains blocks on its next write, and for the
/// resident server that is a transcription that never returns. So a throw from <c>onMatch</c> —
/// production hands it Serilog, which can throw on a locked or full log — drops that one match
/// and keeps reading (Grok plan r1, B4).</para>
///
/// <para><b>No raw line leaves this method.</b> The server's banner carries the model path; only
/// the parsed device fields reach the caller. The returned line count is the one fact about the
/// raw stream that is exposed, so a test can prove the pipe carried something.</para>
///
/// <para>Since LAI-2 the drain loop itself is <see cref="NativeChildOutputPump"/>, shared with the
/// llama-server child; this type binds the Parakeet parser to it.</para>
/// </summary>
internal static class ParakeetServerOutputPump
{
    /// <summary>Read <paramref name="reader"/> to EOF. Returns the number of lines seen. A read
    /// failure — the handle closed under the reader by the child's Dispose, or a broken pipe —
    /// ends the pump quietly rather than propagating from a background thread.</summary>
    internal static int Pump(TextReader reader, Action<GgmlVulkanDeviceLine> onMatch)
        => NativeChildOutputPump.Pump<GgmlVulkanDeviceLine>(reader, GgmlVulkanDeviceLine.TryParse, onMatch);
}
