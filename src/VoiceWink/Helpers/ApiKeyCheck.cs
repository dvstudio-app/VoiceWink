using System.Net;
using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>What a transcription provider's key check concluded.</summary>
public enum ApiKeyCheckOutcome
{
    /// <summary>The provider accepted the key.</summary>
    Valid,

    /// <summary>The provider refused the key. Never saved.</summary>
    Invalid,

    /// <summary>No answer about the key (network, timeout, rate limit, server error). Saved with a warning.</summary>
    Unverified
}

/// <summary>
/// A key check's verdict plus what it was decided on, for the log line. <see cref="Reason"/> is a
/// closed vocabulary (the constants on <see cref="ApiKeyCheck"/>) — never response text, never key
/// material. Public only because it rides the public <see cref="Services.Transcription.ITranscriptionService"/>.
/// </summary>
public readonly record struct ApiKeyCheckResult(ApiKeyCheckOutcome Outcome, int? HttpStatus, string Reason);

/// <summary>
/// The ONE rule for reading a provider's answer to a key check (2026-10-10). Before it, a check
/// returned false only on 401/403 and threw on every other status, and both key-entry pages saved
/// the key on any throw — so ElevenLabs, which answers a bad key with HTTP 400 and a structured
/// <c>authentication_error</c>, had a pasted key ID saved as "Could not validate — key saved".
///
/// <para>The rule: a 2xx accepts, every 4xx refuses except 408 and 429, and only a network
/// failure, a timeout, a rate limit or a 5xx leaves the key unverified (saved with a warning). A
/// probe that deliberately sends an incomplete request (ElevenLabs' speech-to-text check) names
/// the exact rejection a VALID key gets for it; nothing else counts as passing — an incomplete
/// request can also be rejected before the key is looked at (measured: an empty multipart form
/// gets 400 without any key check).</para>
/// </summary>
internal static class ApiKeyCheck
{
    /// <summary>
    /// A key check's own deadline. The "transcription" HttpClient allows 5 minutes because it
    /// uploads audio; a key check that hangs must not show "Validating..." that long.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    internal const string ReasonAccepted = "accepted";
    internal const string ReasonRejected = "rejected";
    internal const string ReasonAuthError = "auth-error";
    internal const string ReasonPostAuthRejection = "post-auth-rejection";
    internal const string ReasonClientError = "client-error";
    internal const string ReasonRateLimited = "rate-limited";
    internal const string ReasonTimeout = "timeout";
    internal const string ReasonServerError = "server-error";
    internal const string ReasonUnexpectedStatus = "unexpected-status";
    internal const string ReasonNetwork = "network";
    internal const string ReasonClientFault = "client-fault";

    /// <summary>
    /// Pure verdict over a response. <paramref name="isExpectedPostAuthRejection"/> is the probe's
    /// own positive evidence that a 4xx came AFTER the key was accepted; null for probes that
    /// expect a 2xx from a valid key.
    /// </summary>
    public static ApiKeyCheckResult Classify(int status, string? body, Func<string?, bool>? isExpectedPostAuthRejection = null)
    {
        if (status is >= 200 and < 300)
            return new(ApiKeyCheckOutcome.Valid, status, ReasonAccepted);
        if (status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
            return new(ApiKeyCheckOutcome.Invalid, status, ReasonRejected);
        if (status == (int)HttpStatusCode.RequestTimeout)
            return new(ApiKeyCheckOutcome.Unverified, status, ReasonTimeout);
        if (status == (int)HttpStatusCode.TooManyRequests)
            return new(ApiKeyCheckOutcome.Unverified, status, ReasonRateLimited);
        if (status is >= 400 and < 500)
        {
            if (IsAuthErrorBody(body))
                return new(ApiKeyCheckOutcome.Invalid, status, ReasonAuthError);
            if (isExpectedPostAuthRejection?.Invoke(body) == true)
                return new(ApiKeyCheckOutcome.Valid, status, ReasonPostAuthRejection);
            return new(ApiKeyCheckOutcome.Invalid, status, ReasonClientError);
        }
        if (status is >= 500 and < 600)
            return new(ApiKeyCheckOutcome.Unverified, status, ReasonServerError);
        return new(ApiKeyCheckOutcome.Unverified, status, ReasonUnexpectedStatus);
    }

    /// <summary>
    /// Sends one key-check request under <see cref="Deadline"/> and classifies the answer. Never
    /// throws except when <paramref name="ct"/> itself is cancelled: a transport failure or a
    /// timeout is <see cref="ApiKeyCheckOutcome.Unverified"/>. The body is read only for a 4xx,
    /// size-limited, and never leaves this method.
    /// </summary>
    public static Task<ApiKeyCheckResult> CheckApiKeyAsync(this HttpClient http, HttpRequestMessage request,
        Func<string?, bool>? isExpectedPostAuthRejection = null, CancellationToken ct = default)
        => CheckApiKeyAsync(http, request, isExpectedPostAuthRejection, Deadline, ct);

    /// <summary>Test seam: the same check under an explicit <paramref name="deadline"/>.</summary>
    internal static async Task<ApiKeyCheckResult> CheckApiKeyAsync(HttpClient http, HttpRequestMessage request,
        Func<string?, bool>? isExpectedPostAuthRejection, TimeSpan deadlineAfter, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(deadlineAfter);
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            string? body = null;
            if (status is >= 400 and < 500)
            {
                // The body decides between a valid and an invalid key, so a body that cannot be read
                // decides nothing. HttpClient has normally buffered it already (both rows in
                // ApiKeyCheckTests land Unverified either way); this keeps it so if that changes.
                try
                {
                    body = await response.Content.ReadAsStringLimitedAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new(ApiKeyCheckOutcome.Unverified, status, ReasonTimeout);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new(ApiKeyCheckOutcome.Unverified, status, ReasonNetwork);
                }
            }
            return Classify(status, body, isExpectedPostAuthRejection);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ApiKeyCheckOutcome.Unverified, null, ReasonTimeout);
        }
        catch (HttpRequestException)
        {
            return new(ApiKeyCheckOutcome.Unverified, null, ReasonNetwork);
        }
        catch (IOException)
        {
            return new(ApiKeyCheckOutcome.Unverified, null, ReasonNetwork);
        }
    }

    /// <summary>
    /// A structured "this key is not valid" answer on a non-401/403 status. ElevenLabs' shape
    /// (measured 2026-10-10: HTTP 400, <c>detail.type = "authentication_error"</c>,
    /// <c>detail.code = "invalid_api_key"</c>, <c>detail.status</c> = <c>"invalid_api_key"</c> for a
    /// made-up key and <c>"api_key_id_used_as_api_key"</c> for a key ID) and the OpenAI-style
    /// <c>error.code = "invalid_api_key"</c>. Field values only — never message prose.
    /// </summary>
    internal static bool IsAuthErrorBody(string? body)
        => HasStringFields(body, "detail", ("type", "authentication_error"))
           || HasStringFields(body, "detail", ("code", "invalid_api_key"))
           || HasStringFields(body, "detail", ("status", "invalid_api_key"))
           || HasStringFields(body, "error", ("code", "invalid_api_key"));

    /// <summary>
    /// True when <paramref name="body"/> is a JSON object whose <paramref name="objectName"/> member
    /// is an object carrying EVERY one of <paramref name="fields"/> as an exact (ordinal) string.
    /// The one matcher behind <see cref="IsAuthErrorBody"/> and each probe's measured valid-key
    /// rejection: field values only, never message prose; anything unparseable is false.
    /// </summary>
    internal static bool HasStringFields(string? body, string objectName, params (string Name, string Value)[] fields)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(objectName, out var obj)
                || obj.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var (name, expected) in fields)
            {
                if (!obj.TryGetProperty(name, out var value)
                    || value.ValueKind != JsonValueKind.String
                    || !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
