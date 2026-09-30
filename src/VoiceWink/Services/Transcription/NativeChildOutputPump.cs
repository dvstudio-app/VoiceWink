namespace VoiceWink.Services.Transcription;

/// <summary>A line parser: true and the parsed value for a recognised line, false otherwise.</summary>
internal delegate bool LineParser<T>(string? line, out T parsed);

/// <summary>
/// Drains a local-engine child's captured stdout/stderr to EOF, handing every line the engine's
/// parser recognises to <c>onMatch</c> and dropping the rest (TRN-60 for parakeet-server, reused by
/// LAI-2's llama-server). Two properties are load-bearing and pinned by
/// <c>ParakeetServerOutputPumpTests</c>:
///
/// <para><b>It reads to EOF whatever happens in the parser or <c>onMatch</c>.</b> An anonymous pipe
/// has a small kernel buffer; a child whose output nobody drains blocks on its next write, and for a
/// resident server that is a request that never returns. So a throw costs that one line and the
/// drain keeps reading (Grok plan r1, B4).</para>
///
/// <para><b>No raw line leaves this method.</b> Both servers print the model path; only parsed fields
/// reach the caller. The returned line count is the one fact about the raw stream that is exposed,
/// so a test can prove the pipe carried something.</para>
/// </summary>
internal static class NativeChildOutputPump
{
    /// <summary>Read <paramref name="reader"/> to EOF. Returns the number of lines seen. A read
    /// failure — the handle closed under the reader by the child's Dispose, or a broken pipe —
    /// ends the pump quietly rather than propagating from a background thread.</summary>
    internal static int Pump<T>(TextReader reader, LineParser<T> parse, Action<T> onMatch)
    {
        var lines = 0;
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines++;
                try
                {
                    // The parse sits INSIDE the per-line guard on purpose: a parser throw on one
                    // line must cost that line, never the drain (self-review, privacy lens).
                    if (parse(line, out var parsed))
                    {
                        onMatch(parsed);
                    }
                }
                catch
                {
                    // A parse or logging failure costs one line, never the drain.
                }
            }
        }
        catch
        {
            // The read end was closed (the child's Dispose) or the pipe broke: the child is going
            // away either way, and a background thread has nowhere useful to throw to.
        }
        return lines;
    }
}
