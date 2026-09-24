namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// LAI-1: which wire protocol a <see cref="AIProvider.LocalServer"/> speaks.
/// </summary>
/// <remarks>
/// <para><b>Ollama gets its NATIVE API, not its OpenAI-compatible one.</b> Only the native
/// <c>/api/chat</c> takes <c>options.num_ctx</c> and <c>think</c>: through <c>/v1</c> a model runs
/// at the server's default context, which silently TRUNCATES the prompt from the front once the
/// ~1,000-token system envelope plus a long dictation exceed it — the cleanup rules are what gets
/// cut — and a thinking model (Qwen3 and later) spends 5–10x the time reasoning about a cleanup.</para>
/// <para>Everything else — LM Studio, llama-server, vLLM, LocalAI, a proxy — speaks
/// <c>/chat/completions</c> + <c>/models</c> under a base URL that already ends in <c>/v1</c>.</para>
/// </remarks>
public enum LocalServerApi
{
    Ollama,
    OpenAICompatible
}

/// <summary>
/// LAI-1: pure mapping between the two settings a Local server stores (<c>localServerApi</c> +
/// <c>aiBaseUrl_localserver</c>) and what the settings card shows. No I/O, no settings access.
/// </summary>
public static class LocalServerEndpoints
{
    /// <summary>Ollama's documented default listen address.</summary>
    public const string OllamaDefaultUrl = "http://127.0.0.1:11434";

    /// <summary>
    /// LM Studio's documented default server address, including the <c>/v1</c> base — the default
    /// for every OpenAI-compatible server, since it is the one such server most users run.
    /// </summary>
    public const string OpenAICompatibleDefaultUrl = "http://127.0.0.1:1234/v1";

    /// <summary>Settings token for <see cref="LocalServerApi.Ollama"/>.</summary>
    public const string OllamaToken = "ollama";

    /// <summary>Settings token for <see cref="LocalServerApi.OpenAICompatible"/>.</summary>
    public const string OpenAICompatibleToken = "openai";

    /// <summary>
    /// The stored token → API. Anything other than the OpenAI-compatible token reads as Ollama,
    /// the default — an unreadable value must land on a working configuration.
    /// </summary>
    public static LocalServerApi ParseApi(string? token)
        => string.Equals(token?.Trim(), OpenAICompatibleToken, StringComparison.OrdinalIgnoreCase)
            ? LocalServerApi.OpenAICompatible
            : LocalServerApi.Ollama;

    public static string TokenFor(LocalServerApi api)
        => api == LocalServerApi.OpenAICompatible ? OpenAICompatibleToken : OllamaToken;

    /// <summary>The address a server type starts on when the user picks it.</summary>
    public static string DefaultUrlFor(LocalServerApi api)
        => api == LocalServerApi.Ollama ? OllamaDefaultUrl : OpenAICompatibleDefaultUrl;

    /// <summary>The base URL a request goes to: the stored one, or the API's default.</summary>
    public static string EffectiveBaseUrl(LocalServerApi api, string? storedUrl)
        => !string.IsNullOrWhiteSpace(storedUrl) ? storedUrl.Trim() : DefaultUrlFor(api);

    /// <summary>
    /// Validates a user-typed address with the SAME rules <see cref="AIProviderConfig.GetBaseUrl"/>
    /// enforces at request time (absolute; http only on loopback; http or https), so the card can
    /// refuse an address before saving it rather than have every dictation fail on it — plus no
    /// credentials in the address, which would otherwise sit in settings and in log lines. Returns
    /// null when valid, else the user-facing reason.
    /// </summary>
    public static string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Enter the server address.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Enter an address starting with http:// or https://.";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "A server on another computer needs an https:// address.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Leave the user name and password out of the address.";
        // The request path is APPENDED to this address, so a ?query or #fragment would swallow
        // it ("…?token=x/api/chat") — and a token there would sit in settings.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "Leave out anything after ? or # in the address.";
        return null;
    }

    /// <summary>
    /// The host the text goes to when the address is NOT this PC, else null. The card then says
    /// so, because "Local server" would otherwise read as "stays on this PC".
    /// </summary>
    public static string? RemoteHost(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && !uri.IsLoopback ? uri.Host : null;
}
