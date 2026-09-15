using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>
/// REL-22 defect 1: does this provider error body say "you are out of credits"?
///
/// <para><b>Why this exists.</b> ElevenLabs answers an exhausted credit balance with <b>HTTP 401</b>.
/// <c>MainViewModel.DescribeTranscriptionFailure</c>'s Unauthorized arm was first and unconditional —
/// which is SEC-1's fix, not a mistake: a 401 body is credential prose and OpenAI echoes a masked key
/// fragment into it, so that body must never reach the pill. The defect was that the mask treated
/// EVERY 401 as a credential failure, so a valid key with no credits sent the user off to regenerate
/// a working key. This turns the rule into "a 401 we cannot CLASSIFY is a bad key".</para>
///
/// <para><b>It returns a bool and never text, and that is the whole safety argument.</b> Nothing read
/// here can reach a user surface: the callers emit app-authored copy on a true verdict. That is also
/// why it matches a structured code FIELD and never message prose — a code token is an API contract,
/// a message is copy, and ENH-1's recorded declination forbids normalising provider prose into app
/// copy because providers rephrase without notice.</para>
///
/// <para><b>EXACTLY ONE (field, token) PAIR IS RECOGNISED, and that is a deliberate retreat.</b> The
/// first version accepted three tokens across five field positions — a Cartesian product in which most
/// combinations had no evidence behind them at all. Codex's diff review found the reachable
/// consequence: a custom OpenAI-compatible endpoint answering 401 with <c>{"error":{"code":
/// "quota_exceeded"}}</c> for a PLAN quota would have been diagnosed as depleted credits, i.e. wrong
/// guidance produced from provider-controlled input. The same review also disputed, with citations,
/// the vendor-doc claims behind the other tokens (Deepgram documenting <c>ASR_PAYMENT_REQUIRED</c>
/// rather than <c>INSUFFICIENT_CREDITS</c>; OpenAI's <c>insufficient_quota</c> being the <c>type</c> on
/// a 429 rather than the <c>code</c>). Those claims could not be checked from here, and an unverifiable
/// source is not a source — so rather than adjudicate them, everything unproven was removed.</para>
///
/// <para><b>The one pair: <c>detail.code == "quota_exceeded"</c>, VERIFIED live (ElevenLabs,
/// 2026-08-11)</b> — the shape observed in the capture that opened REL-22.</para>
///
/// <para><b>Nothing is lost by the retreat.</b> Every other provider signals credit exhaustion on a
/// status this code never sees: the 401 mask is the only thing that suppresses a provider's own
/// message, and on 402/403/429 that message already surfaces through
/// <see cref="ProviderErrorMessage"/> and is more specific than any generic copy of ours. So the
/// narrow pair fixes the entire observed defect.</para>
///
/// <para><b>Adding a pair requires evidence, not a vendor-doc recollection:</b> a captured response
/// (status + field + token) from that provider, cited on the row. Add the PAIR, never the field or the
/// token alone, and pin every off-diagonal combination as false — those rows are what stop this
/// drifting back into a product.</para>
/// </summary>
internal static class ProviderQuotaClassifier
{
    /// <summary>
    /// The one verified token, matched case-insensitively and never as a substring —
    /// <c>quota_exceeded_soon</c> must not match, and a bare "quota" in prose must not match anything.
    /// </summary>
    private const string QuotaExceededToken = "quota_exceeded";

    /// <summary>
    /// True only for the one verified (field, token) pair. Total: null, empty, non-JSON, a non-object
    /// root, wrong value kinds and array-form <c>detail</c> all return false rather than throwing.
    /// Absence of evidence is never a verdict — false means "not classified", which the callers treat
    /// as "keep today's behaviour" (the 401 mask).
    /// </summary>
    public static bool IsOutOfCredits(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            // ElevenLabs (FastAPI-shaped): {"detail":{"type":"...","code":"quota_exceeded",...}}
            // The OBJECT form only. FastAPI's validation-error body makes `detail` an ARRAY; that shape
            // carries no quota token, and the kind guard is what stops it being mis-read — TryGetProperty
            // THROWS on a non-object, so the guard is load-bearing rather than defensive.
            if (doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object
                && detail.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return string.Equals(code.GetString(), QuotaExceededToken, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
