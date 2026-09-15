using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>
/// Non-success provider API response as an exception (ENH-1).
/// <para><b>Body/message split:</b> <see cref="Exception.Message"/> carries NO response-BODY text —
/// only provider name and numeric status ("Gemini: HTTP 400").
/// The provider's structured error text (e.g. "User location is not supported for the API use.") rides
/// <see cref="UserMessage"/> instead.</para>
/// <para><b>SEC-2 (shipped 2026-08-18): the HTTP reason phrase is no longer in this message.</b> It is
/// <b>server-supplied</b> — a custom OpenAI-compatible endpoint can put arbitrary prose in it — and this
/// message flows into <c>{ErrorMessage}</c> log properties, which the Sentry sub-logger records as
/// breadcrumbs and which <c>LogRedactionEnricher</c>'s property-name allowlist deliberately does NOT
/// cover (21 emit sites across 7 source files carry ordinary local exception text that is genuinely
/// useful in Sentry; blinding them all to close one channel was the rejected option 2). The phrase now rides
/// <see cref="ReasonPhrase"/> and is logged only as a <c>{ReasonPhrase}</c> property, which IS
/// allowlisted — so the local file sink keeps a BOUNDED EXCERPT and Sentry gets
/// <c>&lt;REDACTED&gt;</c>. Bounded, NOT full, and the first wording here said "full fidelity" which
/// was false: all three log sites pass the phrase through <c>TruncateForLog</c> (200 chars — the same
/// bound <c>{Body}</c> takes, deliberately, because a hostile endpoint can send an unbounded phrase).
/// The untruncated value survives only on <see cref="ReasonPhrase"/>.</para>
/// <para><b>Residual, stated rather than assumed:</b> the support zip and the GDPR export scrub
/// RENDERED log lines through <c>RedactString</c>, which has no reason-phrase token, so the phrase
/// survives into both. That is the same accepted class as <c>{Body}</c> — device-local and
/// user-initiated — not a Sentry channel.</para>
/// <para><b>Framework-built messages are a SEPARATE remaining channel</b> — see backlog SEC-5.
/// <c>EnsureSuccessStatusCode()</c> composes "Response status code does not indicate success: 503
/// (&lt;phrase&gt;)" itself, which we cannot sanitise. The provider path no longer calls it
/// (<c>EnsureSuccessOrLogAndThrowAsync</c>'s body-read-failure fallback throws this type instead), but
/// <c>ImageGenerationClient</c>'s image-URL download and <c>ModelDownloadManager</c> still do — as does
/// <c>HttpResponseExtensions.ValidateAuthorizedGetAsync</c> in this very file, which backs
/// <c>ValidateKeyAsync</c> for all four transcription clients. That last one is a DEAD channel today
/// (both callers swallow the exception without logging it), which is why it is recorded on SEC-5
/// rather than fixed here — but it is on the provider path, so do not read this paragraph as saying
/// the provider path is entirely clear.</para>
/// <para>Pill surfaces are already protected: they route through
/// <c>MainViewModel.ProviderPillSafeText</c>, which never shows a status-bearing exception message.</para>
/// Subclasses <see cref="HttpRequestException"/> with <c>StatusCode</c>
/// preserved, so every existing 401/403 catch filter keeps working.
/// </summary>
internal sealed class ProviderApiException : HttpRequestException
{
    /// <summary>
    /// Concise provider error text extracted from the response body's structured
    /// envelope, or null when none was extractable. UI surfaces ONLY (pill status
    /// lines, page error lines) — never put this in a log template; it can contain
    /// provider-echoed content and bypasses the Body redaction property set.
    /// </summary>
    public string? UserMessage { get; }

    /// <summary>
    /// SEC-2: the SERVER-SUPPLIED HTTP reason phrase, kept off <see cref="Exception.Message"/> so it
    /// cannot ride <c>{ErrorMessage}</c> into Sentry breadcrumbs. Log it ONLY as a
    /// <c>{ReasonPhrase}</c> property — that name is in
    /// <c>LogRedactionEnricher.RedactedPropertyNames</c>, so the local file sink keeps it and the
    /// Sentry sub-logger replaces it. Never <c>{@}</c>-destructure it: the enricher does not recurse.
    /// <para><b>Rarely null, and NOT null on HTTP/2 as one would expect.</b>
    /// <c>HttpResponseMessage.ReasonPhrase</c>'s getter falls back to the canonical description for
    /// the status code when the wire carried none, so an HTTP/2 500 yields the .NET-local constant
    /// "Internal Server Error" rather than null. It is null only for a status code .NET has no
    /// description for. Measured, not assumed — <c>ProviderApiExceptionTests</c> asserts it, after a
    /// first version of that row asserted null and failed. So a <c>ReasonPhrase is null</c> guard is
    /// dead code for every standard status, and on HTTP/2 what gets redacted is a local constant
    /// rather than server prose.</para>
    /// </summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>
    /// REL-22: the provider labelled this response as a credit/quota exhaustion in a STRUCTURED field
    /// (<see cref="ProviderQuotaClassifier"/>). A verdict, never text — it is what lets the pill layer
    /// distinguish "out of credits" from "bad key" on a 401 without any part of the body crossing the
    /// boundary, which is what keeps SEC-1's guarantee intact.
    /// <para>False means "not classified", never "definitely has credits": callers treat it as
    /// "keep today's behaviour".</para>
    /// </summary>
    public bool IsOutOfCredits { get; }

    public ProviderApiException(string message, HttpStatusCode statusCode, string? userMessage)
        : this(message, statusCode, userMessage, isOutOfCredits: false)
    {
    }

    /// <summary>
    /// PRIVATE on purpose: <see cref="IsOutOfCredits"/> may only ever be set by <see cref="From"/>,
    /// i.e. derived from a real body by <see cref="ProviderQuotaClassifier"/>. An optional public
    /// parameter (which this type briefly had) would let a caller — or a test — assert the flag's
    /// downstream behaviour while the classifier was never consulted, which is exactly the kind of
    /// green-but-unwired pin a security-sensitive flag must not allow (Codex diff review, 2026-08-11).
    /// </summary>
    private ProviderApiException(string message, HttpStatusCode statusCode, string? userMessage, bool isOutOfCredits)
        : base(message, inner: null, statusCode)
    {
        UserMessage = userMessage;
        IsOutOfCredits = isOutOfCredits;
    }

    /// <summary>
    /// Build from a non-success response. <paramref name="body"/> is the already-read
    /// (and already-logged, under a redacted <c>Body=</c> property) response body;
    /// pass null/empty when unavailable.
    /// </summary>
    public static ProviderApiException From(string providerName, HttpResponseMessage response, string? body)
    {
        // SEC-2: the reason phrase is SERVER-SUPPLIED and is deliberately NOT in this message.
        // `Message` flows into {ErrorMessage} log properties, and {ErrorMessage} is not in
        // LogRedactionEnricher's allowlist (it cannot be — 21 emit sites across 9 files carry
        // ordinary local exception text that is useful in Sentry). The phrase moves to a dedicated
        // property that IS allowlisted, so the local file sink keeps it and Sentry does not.
        var message = $"{providerName}: HTTP {(int)response.StatusCode}";
        return new ProviderApiException(message, response.StatusCode, ProviderErrorMessage.Extract(body),
            ProviderQuotaClassifier.IsOutOfCredits(body))
        {
            ReasonPhrase = response.ReasonPhrase,
        };
    }

    /// <summary>
    /// UI-surface text selection for a caught provider-path exception: the structured
    /// provider text when this exception type carried one, else the exception's own
    /// (status-only / generic) message. Same UI-only constraint as <see cref="UserMessage"/>.
    /// <para><b>Does NOT mask a 401.</b> A provider's 401 body is credential prose and can echo a key
    /// fragment, so <b>MiniRecorder pill callers must not call this directly</b> — they go through
    /// <c>MainViewModel.ProviderPillSafeText</c> (and its four per-surface wrappers), which replaces a
    /// typed 401's message with app copy first. Three pill paths called this directly until
    /// 2026-07-26 and surfaced those bodies verbatim. Non-pill surfaces (e.g. the model-fetch error
    /// line, which cannot receive 401/403 — <c>AIEnhancementService</c> rethrows them) may keep using
    /// it.</para>
    /// </summary>
    public static string UserFacingMessage(Exception ex)
        => (ex as ProviderApiException)?.UserMessage ?? ex.Message;
}

/// <summary>
/// Extracts a concise human-usable message from a provider error body.
/// STRUCTURED ENVELOPE FIELDS ONLY — <c>error.message</c> (OpenAI/Gemini/Groq style),
/// <c>error</c> as a JSON string, top-level <c>message</c>, Deepgram's <c>err_msg</c>, or
/// ElevenLabs' FastAPI-shaped <c>detail.message</c> / bare-string <c>detail</c>. Anything
/// else (non-JSON, unexpected shape, empty) returns null: raw body snippets must
/// never become exception/user text (privacy — see <see cref="ProviderApiException"/>).
/// <para>The <c>detail</c> arms joined in REL-22 — without them ElevenLabs could not surface its own
/// error text on ANY status. <b>Array-form <c>detail</c> returns null on purpose:</b> FastAPI's
/// validation-error body makes it a list of per-field objects with no single usable sentence, and the
/// kind guard is what stops it being mis-read.</para>
/// <para>This does NOT weaken SEC-1. A 401's prose still cannot reach a pill: both consumers
/// (<c>MainViewModel.ProviderPillSafeText</c> and <c>DescribeTranscriptionFailure</c>) put their
/// Unauthorized arms ahead of every <c>UserMessage</c> arm, which is pinned by test.</para>
/// </summary>
internal static class ProviderErrorMessage
{
    private const int MaxLength = 160;

    public static string? Extract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // {"error":{"message":"..."}} — OpenAI / Gemini / Groq envelope
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                    return Normalize(nested.GetString());

                // {"error":"..."}
                if (error.ValueKind == JsonValueKind.String)
                    return Normalize(error.GetString());
            }

            // {"message":"..."}
            if (doc.RootElement.TryGetProperty("message", out var top)
                && top.ValueKind == JsonValueKind.String)
                return Normalize(top.GetString());

            // {"err_code":"SLOW_UPLOAD","err_msg":"Request upload timeout."} — Deepgram envelope
            if (doc.RootElement.TryGetProperty("err_msg", out var errMsg)
                && errMsg.ValueKind == JsonValueKind.String)
                return Normalize(errMsg.GetString());

            // {"detail":{"code":"...","message":"..."}} — ElevenLabs / FastAPI envelope (REL-22).
            // Object form only; the ARRAY form (FastAPI validation errors) deliberately falls through
            // to null rather than being flattened into an invented sentence.
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("message", out var detailMessage)
                    && detailMessage.ValueKind == JsonValueKind.String)
                    return Normalize(detailMessage.GetString());

                // {"detail":"..."} — FastAPI's HTTPException default
                if (detail.ValueKind == JsonValueKind.String)
                    return Normalize(detail.GetString());
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxLength ? flat : flat[..MaxLength].TrimEnd() + "…";
    }
}
