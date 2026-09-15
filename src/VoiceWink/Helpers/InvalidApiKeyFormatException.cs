namespace VoiceWink.Helpers;

/// <summary>
/// ENH-15: the STORED key for a provider cannot form an HTTP header, so no request was built.
///
/// <para><b>This is a typed boundary, not a shape guess</b> — the same reasoning as NET-1's
/// connect-phase carve-out. Classifying the resulting exception instead was tried on paper and
/// fails twice over: a malformed key surfaces as an <see cref="HttpRequestException"/> with a
/// <b>null</b> <c>StatusCode</c>, which is indistinguishable from a DNS or TLS failure — so
/// treating that shape as "invalid key" would roll back a good, just-saved key on a network
/// blip, collapsing the deliberate keep-on-outage posture. And CR/LF keys never produce that
/// exception at all; they throw <see cref="FormatException"/> at header construction. One
/// boundary before the header is built covers both, and lets a genuine outage stay an outage.</para>
///
/// <para>Thrown only for a key already in storage. A key entered TODAY cannot reach here —
/// <c>ApiKeyManager.SetApiKey</c> refuses to persist it. The reachable case is a key written by a
/// build that predates that check, which is exactly how this defect was found.</para>
///
/// <para><see cref="Message"/> is app-authored from the provider and the verdict and <b>never
/// contains the key</b>. That matters beyond tidiness: this message can reach
/// <c>ProviderApiException.UserFacingMessage</c> and from there the on-screen error line, while
/// the accompanying Warning rides to Sentry as a breadcrumb.</para>
/// </summary>
public sealed class InvalidApiKeyFormatException : Exception
{
    public string Provider { get; }

    public ApiKeyFormatVerdict Verdict { get; }

    public InvalidApiKeyFormatException(string provider, ApiKeyFormatVerdict verdict)
        : base($"{provider}: the stored API key has an invalid format ({verdict}).")
    {
        Provider = provider;
        Verdict = verdict;
    }

    /// <summary>The copy to show the user — app-authored, never the key.</summary>
    public string UserMessage => ApiKeyFormat.Describe(Verdict);
}
