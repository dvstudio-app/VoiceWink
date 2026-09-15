using System.Net;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Safe HTTP response reading with size limits to prevent OOM from malicious/broken endpoints.
/// </summary>
internal static class HttpResponseExtensions
{
    /// <summary>
    /// Issue an authorized GET to <paramref name="url"/> and return whether the credentials are valid.
    /// 401/403 → <c>false</c>. Any other non-2xx → throws via <c>EnsureSuccessStatusCode</c>.
    /// Used by transcription provider clients to implement <c>ValidateKeyAsync</c> uniformly —
    /// each provider differs only in URL and how its auth header is applied.
    /// </summary>
    public static async Task<bool> ValidateAuthorizedGetAsync(
        this HttpClient http,
        string url,
        Action<HttpRequestMessage> applyAuth,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        applyAuth(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Send an image-generation request, translating an HttpClient-timeout
    /// <see cref="TaskCanceledException"/> (thrown with the CALLER's token unsignaled —
    /// .NET surfaces total-timeout expiry this way) into an explicit
    /// <see cref="TimeoutException"/>. Without the translation, OperationCanceledException
    /// catch sites up the pipeline mistake a provider timeout for a user cancel and
    /// swallow it silently (live incident 2026-07-10: a hung Gemini 4K generation ended
    /// after exactly 5 minutes as "Redo enhancement cancelled" with no user-facing error).
    /// Caller-requested cancellation propagates untouched.
    /// </summary>
    public static Task<HttpResponseMessage> SendWithImageTimeoutTranslationAsync(
        this HttpClient http, HttpRequestMessage request, ILogger logger, string requestDescription, CancellationToken ct)
        => RunWithImageTimeoutTranslationAsync(() => http.SendAsync(request, ct), http, logger, requestDescription, ct);

    /// <summary>
    /// GET counterpart of <see cref="SendWithImageTimeoutTranslationAsync"/> for the
    /// URL-fallback image download. With <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// the translation covers connect + headers (the phase HttpClient.Timeout bounds);
    /// the subsequent body stream read is bounded by the caller's token only.
    /// </summary>
    public static Task<HttpResponseMessage> GetWithImageTimeoutTranslationAsync(
        this HttpClient http, Uri uri, HttpCompletionOption completionOption, ILogger logger, string requestDescription, CancellationToken ct)
        => RunWithImageTimeoutTranslationAsync(() => http.GetAsync(uri, completionOption, ct), http, logger, requestDescription, ct);

    // requestDescription is LOGGED verbatim under {Detail} on both failure arms below — a property
    // the root ModelIdEnricher does not key on and the Sentry name allowlist does not cover. So a
    // caller that interpolates the model into it must gate the value itself
    // (LogValueSanitizer.IdentifierOrShape); ModelIdEnricherTests pins every such site (Codex diff r1).
    private static async Task<HttpResponseMessage> RunWithImageTimeoutTranslationAsync(
        Func<Task<HttpResponseMessage>> send, HttpClient http, ILogger logger, string requestDescription, CancellationToken ct)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            // IMG-4: the batch path caps HttpClient.MaxResponseContentBufferSize at the
            // image-response limit, so an oversized (incl. chunked, no Content-Length)
            // response fails HERE during buffering. Translate ONLY this specific error to
            // the app-authored copy — every other HttpRequestException (network failures)
            // must propagate untouched for the existing retry/offline handling.
            logger.Warning(ex, "Image response exceeded the {LimitMb} MB buffer limit ({Detail})",
                MaxImageResponseBytes / (1024 * 1024), requestDescription);
            throw new InvalidOperationException(
                $"Image response too large (over {MaxImageResponseBytes / (1024 * 1024)} MB)", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var timeoutSeconds = http.Timeout.TotalSeconds;
            // Warning: a provider timeout is environmental and fully handled (translated
            // to TimeoutException, surfaced to the user, redo armed) — keep it out of
            // Sentry events (the sub-logger forwards Error+).
            logger.Warning(ex, "Image generation timed out after {Timeout:F0}s ({Detail})",
                timeoutSeconds, requestDescription);
            throw new TimeoutException(
                $"Image generation timed out after {timeoutSeconds:F0}s. Try a smaller size or lower quality, or rephrase the prompt.",
                ex);
        }
    }

    public static async Task EnsureSuccessOrLogAndThrowAsync(
        this HttpResponseMessage response, string providerName, ILogger logger, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode) return;

        // Read the body for diagnostics. The local file sink shows the truncated body
        // verbatim; the Sentry sub-logger's LogRedactionEnricher scrubs <see cref="LogRedactionEnricher.RedactedPropertyNames"/>
        // (Body is in that set) before it becomes a breadcrumb. If the read itself fails,
        // fall back to the status-only log so we still throw the original HTTP error.
        // Warning, not Error: a non-success provider status is environmental (outage,
        // quota, key, content policy), not an app defect — the Sentry sub-logger forwards
        // Error+ as events, and provider noise was burning the event quota (VOICEWINK-2/3,
        // 322 events of 520s/safety-400s). Don't re-elevate.
        string? body = null;
        try { body = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false); }
        // Let cancellation propagate — callers expect OperationCanceledException, not HttpRequestException,
        // when the user cancels mid-request.
        catch (Exception readEx) when (readEx is not OperationCanceledException)
        {
            logger.Warning("{Provider} API returned {Status}; body unavailable ({ReadError}, ContentLength={ContentLength}); ReasonPhrase={ReasonPhrase}",
                providerName, (int)response.StatusCode, readEx.Message, response.Content.Headers.ContentLength,
                response.ReasonPhrase?.TruncateForLog());
            // SEC-2 (Kimi plan round, B1): this used to be `response.EnsureSuccessStatusCode()`,
            // whose FRAMEWORK-built message is "Response status code does not indicate success:
            // 503 (<server-supplied prose>)" — the reason phrase again, in a string we do not
            // compose, landing in {ErrorMessage} at MainViewModel/AIEnhancementService and from
            // there into a Sentry breadcrumb. Reachable whenever the body read itself fails (a
            // network reset mid-body). Throwing the typed exception instead closes that half AND
            // unifies the type, so every caller's catch filters keep working; body is null here,
            // so UserMessage is null and IsOutOfCredits false, exactly as EnsureSuccessStatusCode
            // carried neither.
            throw ProviderApiException.From(providerName, response, null);
        }

        logger.Warning("{Provider} API returned {Status} (ContentLength={ContentLength}); Body={Body}; ReasonPhrase={ReasonPhrase}",
            providerName, (int)response.StatusCode, response.Content.Headers.ContentLength, body.TruncateForLog(),
            response.ReasonPhrase?.TruncateForLog());
        // ENH-1: carry the provider's structured error text for UI surfaces. Message stays free of
        // response-BODY text; the extracted text rides UserMessage. SEC-2: it is also free of the
        // server-supplied ReasonPhrase now — that rides the redacted {ReasonPhrase} property above
        // and the exception's own ReasonPhrase, so the local log keeps full fidelity.
        throw ProviderApiException.From(providerName, response, body);
    }


    /// <summary>
    /// Maximum response body size (10 MB). Transcription/AI text responses should never exceed this.
    /// </summary>
    private const long MaxResponseBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum response body size for image generation (50 MB). 4K images can be 20-30 MB base64.
    /// Internal (IMG-4): the ONE image-response limit — the batch path also applies it as
    /// <c>HttpClient.MaxResponseContentBufferSize</c> so an oversized/chunked response fails
    /// DURING buffering instead of after N concurrent full buffers, and the URL-fallback
    /// download's manual cap reuses it.
    /// </summary>
    internal const long MaxImageResponseBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Read response content as string with size limit enforcement.
    /// Throws InvalidOperationException if response exceeds MaxResponseBytes.
    /// </summary>
    public static Task<string> ReadAsStringLimitedAsync(
        this HttpContent content, CancellationToken ct = default)
        => ReadWithLimitAsync(content, MaxResponseBytes, ct);

    /// <summary>
    /// Read response content with a higher limit for image generation (4K images can be 20-30 MB base64).
    /// </summary>
    public static Task<string> ReadAsStringImageLimitedAsync(
        this HttpContent content, CancellationToken ct = default)
        => ReadWithLimitAsync(content, MaxImageResponseBytes, ct);

    private static async Task<string> ReadWithLimitAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidOperationException(
                $"Response size {content.Headers.ContentLength} exceeds limit of {maxBytes} bytes.");

        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new LimitedStream(stream, maxBytes);
        using var reader = new StreamReader(limited);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _totalRead;

        public LimitedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _totalRead += read;
            if (_totalRead > _maxBytes)
                throw new InvalidOperationException(
                    $"Response exceeded size limit of {_maxBytes} bytes.");
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
            _totalRead += read;
            if (_totalRead > _maxBytes)
                throw new InvalidOperationException(
                    $"Response exceeded size limit of {_maxBytes} bytes.");
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            _totalRead += read;
            if (_totalRead > _maxBytes)
                throw new InvalidOperationException(
                    $"Response exceeded size limit of {_maxBytes} bytes.");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
