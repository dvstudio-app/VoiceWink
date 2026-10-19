using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>How a local server frames a streamed reply.</summary>
internal enum LocalStreamFraming
{
    /// <summary>OpenAI-compatible server-sent events: <c>data: {json}</c> lines, <c>data: [DONE]</c>.</summary>
    Sse,
    /// <summary>Ollama's <c>/api/chat</c>: one JSON object per line, the last with <c>done: true</c>.</summary>
    Ndjson,
}

/// <summary>A streamed reply, assembled. <see cref="ContentSeen"/>: a content field arrived at all (an
/// empty or null one included) — what tells ENH-27's "cut off" from ENH-22's "no answer" apart.</summary>
internal sealed record LocalStreamedReply(
    string Content, string? FinishReason, bool ContentSeen, bool ReasoningSeen, JsonElement? FinalObject, int ThinkingRawChars);

/// <summary>Either the assembled stream, or — for a server that ignored <c>stream: true</c> and answered
/// one JSON object — that whole body, for the caller's non-streamed parser.</summary>
internal sealed record LocalReplyResult(LocalStreamedReply? Streamed, string? WholeBody);

/// <summary>
/// LAI-15: sends a local chat request and reads the streamed reply under two clocks (see
/// <see cref="LocalReplyWindows"/>), so a stuck model fails within its window instead of holding the paste
/// for the whole deadline (owner incident 2026-10-16: two dictations ran 60 s on a built-in model whose
/// process was alive and not generating). No HTTP policy of its own: the caller's client, its token (the
/// service deadline) and its success check.
/// <para><b>Progress</b> is a chunk carrying text or reasoning; framing, role-only and empty deltas do not
/// reset the stall clock, so a server sending keep-alives while stuck still trips it. A stream that ends
/// before its completion marker is a failure, never a reply. The reader bounds what it reads at
/// <see cref="HttpResponseExtensions.MaxTextResponseBytes"/> — a header-read stream is outside
/// <c>MaxResponseContentBufferSize</c>.</para>
/// </summary>
internal static class LocalReplyStream
{
    /// <summary>The pill reason when the server reports an error inside the stream — the same words
    /// <see cref="LocalOutputGuard.RefusedReason"/> uses; the server's own text goes only to the log.</summary>
    internal const string StreamErrorReason = "AI enhancement failed";

    /// <summary>
    /// Sends <paramref name="request"/> and reads the reply. <paramref name="malformed"/> builds the
    /// provider's own "not the reply we expect" exception (and logs it at the provider's level);
    /// <paramref name="ensureSuccess"/> is the caller's status check, run before any body is read, so a
    /// 5xx still means nothing was received.
    /// </summary>
    internal static async Task<LocalReplyResult> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        LocalStreamFraming framing,
        LocalReplyWindows? windows,
        int promptChars,
        LocalReplyProgress? progress,
        Func<HttpResponseMessage, CancellationToken, Task> ensureSuccess,
        Func<string, string, Exception> malformed,
        Func<string, Exception> streamError,
        CancellationToken ct)
    {
        windows ??= LocalReplyWindows.Unarmed;
        progress ??= new LocalReplyProgress();
        using var stallCts = new CancellationTokenSource();
        // Watch-only windows never reach the request: the clock only records that it ran out.
        using var linked = windows.Enforce
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, stallCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = new Clock(stallCts, windows, progress);
        clock.Start(promptChars);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            await ensureSuccess(response, linked.Token).ConfigureAwait(false);

            if (IsWholeJson(response))
            {
                // One object, no chunks: the clocks have nothing to measure, so they stop here (Gemini diff r1).
                stallCts.CancelAfter(Timeout.InfiniteTimeSpan);
                return new LocalReplyResult(null, await response.Content.ReadAsStringLimitedAsync(linked.Token).ConfigureAwait(false));
            }

            var assembler = new Assembler(framing, clock, malformed, streamError);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await ReadLinesAsync(stream, assembler.OnLine, malformed, linked.Token).ConfigureAwait(false);
            return new LocalReplyResult(assembler.Finish(), null);
        }
        catch (OperationCanceledException) when (windows.Enforce && stallCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LocalReplyStalledException(clock.Phase, clock.SinceSendMs);
        }
        catch (IOException ex) when (!linked.IsCancellationRequested)
        {
            // The server went away mid-reply. A buffered read reported this as an HttpRequestException
            // with no status (SocketsHttpHandler wraps the copy failure); a streamed read surfaces the
            // raw IOException, which no enhancement catch expects. Same type as before, so every catch
            // that classifies a lost connection still does - and nothing that arrived is used.
            throw new HttpRequestException(ConnectionLostReason, ex);
        }
    }

    /// <summary>A local server that went away mid-reply. 24 units, inside the pill's 26-unit reason budget.</summary>
    internal const string ConnectionLostReason = "Local AI connection lost";

    /// <summary>A server that ignored <c>stream: true</c> answers one JSON object; event streams and
    /// NDJSON carry their own media types.</summary>
    private static bool IsWholeJson(HttpResponseMessage response)
        => string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits the body into lines (LF, CR stripped), bounded in total size. Stops early when the
    /// handler returns false.</summary>
    internal static async Task ReadLinesAsync(Stream stream, Func<string, bool> onLine, Func<string, string, Exception> malformed, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var line = new MemoryStream();
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > HttpResponseExtensions.MaxTextResponseBytes)
                throw malformed("reply larger than the response limit", "");
            if (!Feed(buffer, read, line, onLine))
                return;
        }
        if (line.Length > 0)
            onLine(TakeLine(line));
    }

    /// <summary>Splits one read into lines; false once the handler asks to stop.</summary>
    private static bool Feed(byte[] buffer, int read, MemoryStream line, Func<string, bool> onLine)
    {
        var span = buffer.AsSpan(0, read);
        while (!span.IsEmpty)
        {
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                line.Write(span);
                return true;
            }
            line.Write(span[..newline]);
            span = span[(newline + 1)..];
            if (!onLine(TakeLine(line)))
                return false;
        }
        return true;
    }

    private static string TakeLine(MemoryStream line)
    {
        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        line.SetLength(0);
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    /// <summary>The two clocks on one token source: the first-text wait from the send, then the stall
    /// window re-armed at every chunk carrying text. Also keeps the timing the logs report.</summary>
    private sealed class Clock(CancellationTokenSource stallCts, LocalReplyWindows windows, LocalReplyProgress progress)
    {
        private readonly Stopwatch _sinceSend = new();
        private long _lastProgressMs;

        internal LocalReplyPhase Phase { get; private set; } = LocalReplyPhase.FirstText;

        internal long SinceSendMs => _sinceSend.ElapsedMilliseconds;

        internal void Start(int promptChars)
        {
            _sinceSend.Start();
            if (!windows.Enforce)
                stallCts.Token.Register(() => progress.MarkWouldHaveStalled(Phase, _sinceSend.ElapsedMilliseconds));
            if (windows.FirstTextFor(promptChars) is { } first)
                stallCts.CancelAfter(first);
        }

        internal void OnProgress()
        {
            var now = _sinceSend.ElapsedMilliseconds;
            if (progress.TextChunks == 0)
                progress.FirstTextMs = now;
            else
                progress.LongestGapMs = Math.Max(progress.LongestGapMs ?? 0, now - _lastProgressMs);
            _lastProgressMs = now;
            progress.TextChunks++;
            Phase = LocalReplyPhase.BetweenChunks;
            stallCts.CancelAfter(windows.Stall ?? Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Turns lines into the assembled reply for one framing.</summary>
    private sealed class Assembler(
        LocalStreamFraming framing, Clock clock, Func<string, string, Exception> malformed, Func<string, Exception> streamError)
    {
        private readonly StringBuilder _content = new();
        private string? _finish;
        private bool _contentSeen;
        private bool _reasoningSeen;
        private bool _complete;
        private JsonElement? _final;
        private int _thinkingRawChars;

        /// <summary>Returns false once the reply is complete.</summary>
        internal bool OnLine(string line)
            => framing == LocalStreamFraming.Sse ? OnSseLine(line) : OnNdjsonLine(line);

        internal LocalStreamedReply Finish()
        {
            if (!_complete)
                throw malformed("stream ended before its completion marker", "");
            return new LocalStreamedReply(_content.ToString(), _finish, _contentSeen, _reasoningSeen, _final, _thinkingRawChars);
        }

        private bool OnSseLine(string line)
        {
            if (line.Length == 0 || line[0] == ':')
                return true;
            if (line.StartsWith("error:", StringComparison.Ordinal))
                throw streamError(line);
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                // event:, id:, retry: are SSE fields we have no use for; anything else is not an event stream.
                if (line.StartsWith("event:", StringComparison.Ordinal) || line.StartsWith("id:", StringComparison.Ordinal)
                    || line.StartsWith("retry:", StringComparison.Ordinal))
                    return true;
                throw malformed("not an event stream", line);
            }

            var payload = line.AsSpan(5).Trim();
            if (payload.SequenceEqual("[DONE]"))
            {
                _complete = true;
                return false;
            }

            using var doc = Parse(payload.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw malformed("event is not a JSON object", payload.ToString());
            if (root.TryGetProperty("error", out _))
                throw streamError(payload.ToString());
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object)
                return true; // a usage or keep-alive event
            var choice = choices[0];

            var progressed = false;
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content))
                {
                    // Content in any other shape (parts, an object) is not text we can assemble: refuse the
                    // reply rather than paste the text before it (Codex diff r1).
                    if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw malformed("content is not text", payload.ToString());
                    _contentSeen = true;
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _content.Append(text);
                        progressed = true;
                    }
                }
                if (HasText(delta, "reasoning_content") || HasText(delta, "reasoning"))
                {
                    _reasoningSeen = true;
                    progressed = true;
                }
            }
            if (progressed)
                clock.OnProgress();
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                _finish = finish.GetString();
                _complete = true; // [DONE] may follow; a stream that ends here is complete.
            }
            return true;
        }

        private bool OnNdjsonLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return true;
            using var doc = Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw malformed("line is not a JSON object", line);
            if (root.TryGetProperty("error", out _))
                throw streamError(line);

            var progressed = false;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw malformed("content is not text", line);
                    _contentSeen = true;
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _content.Append(text);
                        progressed = true;
                    }
                }
                if (message.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                {
                    // The raw (escaped) length without its two quotes: GetString() throws on a lone
                    // surrogate escape, and a diagnostic must never fail a good reply.
                    var raw = thinking.GetRawText().Length - 2;
                    if (raw > 0)
                    {
                        _thinkingRawChars += raw;
                        _reasoningSeen = true;
                        progressed = true;
                    }
                }
            }
            if (progressed)
                clock.OnProgress();
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                _final = root.Clone();
                if (root.TryGetProperty("done_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    _finish = reason.GetString();
                _complete = true;
                return false;
            }
            return true;
        }

        private JsonDocument Parse(string json)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                throw malformed("event is not JSON", json);
            }
        }

        /// <summary>A non-empty string field, judged by its raw length (the quotes are 2) so a lone
        /// surrogate escape in reasoning text can never fail a good reply.</summary>
        private static bool HasText(JsonElement delta, string name)
            => delta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
               && value.GetRawText().Length > 2;
    }
}
