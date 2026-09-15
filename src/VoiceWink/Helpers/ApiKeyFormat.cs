namespace VoiceWink.Helpers;

/// <summary>
/// Why a candidate API key was rejected. <see cref="NonAscii"/> and
/// <see cref="ControlCharacter"/> share one boundary test but stay distinct so the log line
/// names the shape without ever naming the value.
/// </summary>
public enum ApiKeyFormatVerdict
{
    Ok,
    Empty,
    NonAscii,
    ControlCharacter
}

/// <summary>
/// ENH-15: is this string capable of being an HTTP header value at all?
///
/// <para>Every provider ships its key in a header — 18 sites across five shapes
/// (<c>Authorization: Bearer</c>, <c>Authorization: Token</c>, <c>x-api-key</c>,
/// <c>xi-api-key</c>, <c>x-goog-api-key</c>) and no query-string transport exists — so one
/// boundary covers all of them.</para>
///
/// <para><b>The boundary is U+0020..U+007E, and it was measured, not assumed.</b> A probe ran
/// every candidate shape through both header APIs the app uses. Two results shaped this file:</para>
/// <list type="bullet">
/// <item>Non-ASCII, bell, tab and DEL are all <b>accepted</b> at construction and fail later at
/// SEND, as <c>HttpRequestException: Request headers must contain only ASCII characters</c> —
/// the failure the owner hit on 2026-08-18.</item>
/// <item>CR and LF <b>throw <see cref="FormatException"/> at construction</b>, from both header
/// APIs, before the request exists. A different type at a different stage, which is why
/// classifying the resulting EXCEPTION could never have caught both.</item>
/// </list>
///
/// <para><b>Interior spaces are legal and are NOT rejected.</b> The same probe proved both header
/// APIs accept them. Rejecting a key a provider would have accepted is worse than the bug this
/// file fixes, so the rule is a blacklist of what provably cannot work — never a charset
/// whitelist, which would reject the first provider to issue a JWT or base64url key.</para>
/// </summary>
public static class ApiKeyFormat
{
    /// <summary>
    /// Trim surrounding whitespace. <see cref="string.Trim()"/> is Unicode-aware, so a key padded
    /// with U+00A0 from a web console is healed rather than rejected. Only INTERIOR characters
    /// survive to <see cref="Validate"/>.
    /// </summary>
    public static string Normalize(string? raw) => raw?.Trim() ?? "";

    /// <summary>
    /// Judge an ALREADY-NORMALIZED value. The first offending character decides, so the verdict
    /// is deterministic for a string carrying more than one kind of offender.
    /// </summary>
    public static ApiKeyFormatVerdict Validate(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
            return ApiKeyFormatVerdict.Empty;

        foreach (var c in normalized)
        {
            if (c < ' ' || c == (char)0x7F)
                return ApiKeyFormatVerdict.ControlCharacter;
            if (c > '~')
                return ApiKeyFormatVerdict.NonAscii;
        }

        return ApiKeyFormatVerdict.Ok;
    }

    /// <summary>
    /// Normalize, validate and describe a RAW candidate in one call — the shape every UI save
    /// site needs after <c>SetApiKey</c> reports <c>InvalidFormat</c>, so no site re-spells the
    /// three-step chain. The return is app-authored copy, never the candidate.
    /// </summary>
    public static string DescribeCandidate(string? raw) => Describe(Validate(Normalize(raw)));

    /// <summary>
    /// One-line user-facing reason. App-authored in full: no verdict path can carry key material
    /// into the UI, a log line, or a Sentry breadcrumb.
    /// </summary>
    /// <remarks>
    /// Kept to roughly the length of the sibling copy already on these surfaces
    /// ("Could not save the key on this device — try again"), because it renders in the same
    /// compact status lines on the Enhancement, Models and Onboarding pages. Names the CAUSE
    /// first and the remedy second, per the app-authored message rule in ENH-1.
    /// </remarks>
    public static string Describe(ApiKeyFormatVerdict verdict) => verdict switch
    {
        ApiKeyFormatVerdict.NonAscii =>
            "Key contains characters no provider accepts — paste it again as plain text.",
        ApiKeyFormatVerdict.ControlCharacter =>
            "Key contains a line break — paste it again as plain text.",
        ApiKeyFormatVerdict.Empty => "Enter an API key.",
        _ => ""
    };
}
