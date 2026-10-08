using System.Net;
using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>
/// ENH-29: does this provider error say "the API key is invalid" on a status other than 401/403?
///
/// <para><b>Why this exists.</b> Every key check in the app treats 401/403 as "this key is
/// rejected" and every other failure as "the provider is down, the key may be fine — keep it".
/// Google answers an invalid key with <b>HTTP 400</b> <c>INVALID_ARGUMENT</c>, so a wrong Gemini key
/// was saved and shown as "Key saved — not verified" where Groq's was refused. A 400 alone cannot be
/// read as a bad key: Gemini's EU geo-block is a 400 too, and that key may be perfectly good.</para>
///
/// <para><b>What is matched: the structured <c>google.rpc.ErrorInfo</c> reason, never the message.</b>
/// <c>error.details[].reason == "API_KEY_INVALID"</c> on a 400 — the answer of Gemini's native
/// <c>/models</c> endpoint to a synthetic invalid key, captured live 2026-10-07. A reason token is an
/// API contract; the message is copy that providers rephrase (the rule
/// <see cref="ProviderQuotaClassifier"/> states). The OpenAI-compatible <c>/openai/models</c> endpoint
/// answers the same key with prose only and no reason, so it is NOT classified here —
/// <c>GeminiDescriptor</c> asks the native endpoint first for that reason.</para>
///
/// <para>Returns a bool and never text, so nothing read here reaches a user surface.</para>
/// </summary>
internal static class ProviderInvalidKeyClassifier
{
    /// <summary>Matched exactly — a substring or a different case is not this token.</summary>
    private const string ApiKeyInvalidReason = "API_KEY_INVALID";

    /// <summary>
    /// True only for a 400 whose body carries the invalid-key reason. Total: null, empty, non-JSON,
    /// a non-object root and wrong value kinds all return false rather than throwing — false means
    /// "not classified", which keeps the status the provider sent.
    /// </summary>
    public static bool IsInvalidKey(HttpStatusCode status, string? body)
    {
        if (status != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            // The kind guards are load-bearing: TryGetProperty THROWS on a non-object.
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String
                    && string.Equals(reason.GetString(), ApiKeyInvalidReason, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
