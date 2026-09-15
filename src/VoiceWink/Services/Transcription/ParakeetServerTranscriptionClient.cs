using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace VoiceWink.Services.Transcription;

/// <summary>The transport's outcome — typed, because the caller (slice 4's routing) must
/// distinguish an EMPTY transcript (a legitimate decode result that flows into the existing
/// empty-transcription handling) from a transport/contract failure (a whole-call policy
/// decision). Never both, never a null-means-something convention.</summary>
public readonly record struct ParakeetServerDecodeResult(bool Success, string Text, string? FailureClass)
{
    public static ParakeetServerDecodeResult Ok(string text) => new(true, text, null);
    public static ParakeetServerDecodeResult Fail(string failureClass) => new(false, string.Empty, failureClass);
}

/// <summary>
/// TRN-29 slice 3: the minimal loopback transport to the resident `parakeet-server` — one
/// multipart POST per slice, nothing else. PURE transport by design: no lease acquisition, no
/// cancellation escalation, no retry, no fallback — those are the routing layer's whole-call
/// decisions (slice 4), made under the service's lock where they are coherent.
///
/// <para><b>The privacy contract (slice-3 plan round, challenge 4): a response BODY is never
/// logged, on any path.</b> A local server's error body can carry transcript or model-path
/// content, and the no-raw-transcript logging rule does not except localhost. Failures log the
/// STATUS and BYTE COUNTS only, carried in <c>FailureClass</c> as app-authored text.</para>
///
/// <para><b>Empty vs missing is a real distinction:</b> a present-but-empty <c>"text"</c> is a
/// SUCCESSFUL empty decode (silence decodes empty, correctly); a missing field, non-string
/// value, malformed JSON, or over-budget body is a CONTRACT failure. Collapsing the two would
/// turn transport breakage into "you said nothing".</para>
/// </summary>
public sealed class ParakeetServerTranscriptionClient
{
    private static ILogger Logger => Log.ForContext<ParakeetServerTranscriptionClient>();

    /// <summary>Response bodies above this are a contract failure — a transcript of a ≤35 s
    /// chunk is a few KB; anything near this bound is not a transcript.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan? _deadlineOverride;

    /// <param name="deadlineOverride">Test seam for the whole-operation deadline; production
    /// uses the named client's own Timeout.</param>
    public ParakeetServerTranscriptionClient(IHttpClientFactory httpClientFactory, TimeSpan? deadlineOverride = null)
    {
        _httpClientFactory = httpClientFactory;
        _deadlineOverride = deadlineOverride;
    }

    /// <summary>The liveness probe (slice 4): OPTIONS against the transcription route — the
    /// exact shape the G3 smoke validated against the real binary. ANY answer means the server
    /// is up; semantic health is proven by real decodes, and a status check here would couple
    /// liveness to a route policy the upstream server may change. Never throws.</summary>
    public async Task<bool> ProbeAsync(Uri baseUri, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("parakeet-local");
            using var request = new HttpRequestMessage(
                HttpMethod.Options, new Uri(baseUri, "v1/audio/transcriptions"));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false; // no answer of any kind = not alive; the caller decides what that means.
        }
    }

    /// <summary>TRN-48 (2026-09-01): silence written around every slice at the WAV build. The
    /// measured defect: parakeet.cpp decodes a short, ordinary-level utterance whose speech runs
    /// to the clip edges as EMPTY while reporting success — B119 (1.69 s, −23.3 dBFS, five words
    /// Whisper transcribes verbatim; `GroundTruth/asrbench-corpus/B119.wav`) reproduces it on
    /// demand, and 300 ms of silence on EITHER side alone recovers the full sentence. Measured
    /// before shipping: lead-only, tail-only, both-300 and both-1000 all recover B119; a padded
    /// digital-silence clip and a padded tone clip STAY empty (the pad wakes nothing — those
    /// empties are correct, see the TRN-48 card's eight-clip census); two healthy short clips
    /// decode identically padded vs raw except one ambiguous filler variant (um→uh), a class the
    /// pipeline's filler stripper removes. 300 ms is the measured-sufficient value, not a tuned
    /// one. Applied to EVERY slice uniformly — interior chunk boundaries included — and the
    /// 56-clip accuracy corpus re-run is the gate that validates the multi-chunk case (the
    /// per-slice design keeps the seam signature unchanged). Note `tools/parakeet-long-audio`
    /// mirrors this WAV build by inspection and deliberately does NOT pad — its committed numbers
    /// predate the pad and its arms measure the raw decode.</summary>
    internal const double DecodeEdgePadSeconds = 0.3;

    /// <summary>POST one 16 kHz mono float slice to the leased server and return its
    /// transcript. Cancellation is a plain HTTP abort — the caller owns any escalation.</summary>
    public async Task<ParakeetServerDecodeResult> DecodeAsync(
        Uri baseUri, float[] samples, int sampleRate, CancellationToken ct)
    {
        var pad = (int)(sampleRate * DecodeEdgePadSeconds);
        var wav = WriteWav16Mono(samples, sampleRate, leadPadSamples: pad, tailPadSamples: pad);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new global::System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "chunk.wav");
        form.Add(new StringContent("parakeet"), "model");

        var client = _httpClientFactory.CreateClient("parakeet-local");
        // The WHOLE-OPERATION deadline. With ResponseHeadersRead, HttpClient.Timeout governs
        // only up to the headers - a server sending 200 and then stalling its body would hang
        // until user cancel (Codex diff r2 Blocker). One linked token spans send AND every
        // body read; ct.IsCancellationRequested stays the user-vs-deadline discriminator.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadlineOverride ?? client.Timeout);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/audio/transcriptions"))
            {
                Content = form,
            };
            // ResponseHeadersRead is the bound's PRECONDITION: the default completion mode
            // buffers the ENTIRE body before returning, so a broken server could force an
            // arbitrary allocation before any size check ran (Codex diff r1 Blocker). With
            // headers-only completion, the bounded stream read below is the only body read.
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the CALLER's cancellation; escalation is its decision.
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout fires as a cancellation the caller never requested - a
            // stalled server is a TRANSPORT failure, and slice 4 must be able to tell the two
            // apart (Codex diff r1).
            Logger.Warning("parakeet-server request timed out (client deadline)");
            return ParakeetServerDecodeResult.Fail("transport");
        }
        catch (HttpRequestException ex)
        {
            // Status-only, never a body: there is no body here, but the message can carry
            // socket detail — log the exception TYPE, keep the class app-authored.
            Logger.Warning("parakeet-server transport failure: {Kind}", ex.GetType().Name);
            return ParakeetServerDecodeResult.Fail("transport");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("parakeet-server answered {Status} ({Bytes} bytes) - body deliberately not logged",
                    (int)response.StatusCode, response.Content.Headers.ContentLength ?? -1);
                return ParakeetServerDecodeResult.Fail($"status-{(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                Logger.Warning("parakeet-server response over budget ({Bytes} bytes)",
                    response.Content.Headers.ContentLength);
                return ParakeetServerDecodeResult.Fail("oversize");
            }

            byte[] body;
            try
            {
                // ReadAsByteArrayAsync honors neither our budget nor chunked bodies by itself;
                // bound the read explicitly so a mis-behaving server cannot balloon memory.
                using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var bounded = new MemoryStream();
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (bounded.Length + read > MaxResponseBytes)
                    {
                        Logger.Warning("parakeet-server chunked response exceeded budget");
                        return ParakeetServerDecodeResult.Fail("oversize");
                    }
                    bounded.Write(buffer, 0, read);
                }
                body = bounded.ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                Logger.Warning("parakeet-server body read timed out (client deadline)");
                return ParakeetServerDecodeResult.Fail("transport");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                Logger.Warning("parakeet-server body read failed: {Kind}", ex.GetType().Name);
                return ParakeetServerDecodeResult.Fail("transport");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    return ParakeetServerDecodeResult.Ok(textEl.GetString() ?? string.Empty);
                }
                Logger.Warning("parakeet-server response missing a string 'text' field ({Bytes} bytes)", body.Length);
                return ParakeetServerDecodeResult.Fail("contract");
            }
            catch (JsonException)
            {
                Logger.Warning("parakeet-server response was not JSON ({Bytes} bytes) - body deliberately not logged", body.Length);
                return ParakeetServerDecodeResult.Fail("contract");
            }
        }
    }

    /// <summary>16-bit PCM mono WAV, in memory — the exact canonical format the pipeline's
    /// slices are in. Internal for the round-trip test (parseable by <c>WavPcm</c>). The optional
    /// pads write zero SAMPLES around the payload inside the same single buffer (TRN-48) — no
    /// second array, no copy of the floats; zero pads produce byte-identical output to the
    /// pre-TRN-48 writer, which is what keeps the round-trip test's old rows meaningful.</summary>
    internal static byte[] WriteWav16Mono(
        ReadOnlySpan<float> samples, int sampleRate, int leadPadSamples = 0, int tailPadSamples = 0)
    {
        // Fail loud: a negative pad would make the header UNDER-declare the payload (the loops
        // skip, the subtraction does not), so a parser silently drops the END of the audio —
        // wrong output, not an error. Unreachable from the one production caller; guarded anyway
        // because the surface is two caller-supplied ints with no other contract (self-review).
        global::System.ArgumentOutOfRangeException.ThrowIfNegative(leadPadSamples);
        global::System.ArgumentOutOfRangeException.ThrowIfNegative(tailPadSamples);
        var totalSamples = samples.Length + leadPadSamples + tailPadSamples;
        var dataBytes = totalSamples * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataBytes);
        for (var i = 0; i < leadPadSamples; i++) bw.Write((short)0);
        foreach (var f in samples)
        {
            bw.Write((short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue));
        }
        for (var i = 0; i < tailPadSamples; i++) bw.Write((short)0);
        bw.Flush();
        return ms.ToArray();
    }
}
