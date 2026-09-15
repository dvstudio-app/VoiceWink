using Microsoft.Extensions.DependencyInjection;

namespace VoiceWink.Services.Http;

/// <summary>
/// Named-HttpClient registration table, extracted from <c>App.ConfigureServices</c>
/// so the per-client policy (Timeout / ConnectTimeout / handler pipeline) is
/// unit-pinnable (<c>VoiceWinkHttpClientsTests</c>). Policy summary:
/// <list type="bullet">
/// <item><c>ai</c> — 5 min Timeout, retried (small JSON bodies safe to resend),
/// connect-bounded, connect-timeouts translated below the retry handler so they are
/// retried like any other network error.</item>
/// <item><c>images</c> — 10 min Timeout (owner request 2026-07-13; high-quality runs
/// were brushing the previous 5-min budget), otherwise identical policy to <c>ai</c>.</item>
/// <item><c>transcription</c> — 5 min Timeout, NO retry handler (audio uploads are
/// consumed streams that cannot be replayed), connect-bounded + translated so a dead
/// network fails as an honest HttpRequestException instead of an OCE the transcribe
/// path would mislabel "cancelled by user". NET-1 did NOT change this pipeline: its
/// one automatic retry lives at the OPERATION level (<c>TranscriptionConnectRetry</c>),
/// where re-invoking <c>TranscribeAsync</c> yields a fresh stream by construction —
/// which is exactly what a handler-level retry cannot do.</item>
/// <item><c>downloads</c> — 10 min Timeout, untouched: ModelDownloadManager owns its
/// bounded-retry + HTTP-Range-resume + stall machinery.</item>
/// <item><c>licensing</c> — 30 s Timeout, retry handler attached but disabled
/// per-request by LicenseService (the user-facing error must reflect the LATEST
/// server state — do not re-enable, see CLAUDE.md).</item>
/// </list>
/// </summary>
internal static class VoiceWinkHttpClients
{
    /// <summary>
    /// Connection-establishment budget (DNS + TCP + TLS) for provider-facing clients
    /// (ENH-3). A connect that cannot finish in 10 s means the network is down or
    /// flapping — the 2026-07-07 incident spent 11–70 s per attempt in this phase.
    /// Response streaming is unaffected, so long LLM/image responses keep their
    /// full Timeout budget.
    /// </summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    internal static void Register(IServiceCollection services)
    {
        services.AddTransient<RetryingHandler>();
        services.AddTransient<ConnectTimeoutTranslatingHandler>();

        services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler<RetryingHandler>()
            .AddHttpMessageHandler<ConnectTimeoutTranslatingHandler>()
            .UseSocketsHttpHandler((h, _) => h.ConnectTimeout = ConnectTimeout);
        // gpt-image-2 high-quality runs routinely take 2–4 min and were brushing the
        // previous 5-min budget (owner request 2026-07-13: 10 min). The user-facing
        // timeout message derives from this value (SendWithImageTimeoutTranslationAsync).
        services.AddHttpClient("images", c => c.Timeout = TimeSpan.FromMinutes(10))
            .AddHttpMessageHandler<RetryingHandler>()
            .AddHttpMessageHandler<ConnectTimeoutTranslatingHandler>()
            .UseSocketsHttpHandler((h, _) => h.ConnectTimeout = ConnectTimeout);
        services.AddHttpClient("transcription", c => c.Timeout = TimeSpan.FromMinutes(5))
            .AddHttpMessageHandler<ConnectTimeoutTranslatingHandler>()
            .UseSocketsHttpHandler((h, _) => h.ConnectTimeout = ConnectTimeout);
        // TRN-29: the resident parakeet-server on loopback. NEITHER handler, on purpose: no
        // RetryingHandler (a local failure is a PROCESS event, never a network blip), and no
        // ConnectTimeoutTranslatingHandler — without the translator no ConnectTimeoutException
        // exists on this path, so TranscriptionConnectRetry (which callers wrap around every
        // transcription) structurally cannot retry it. No proxy and no redirects: localhost
        // must never route through an egress proxy or follow a redirect off the loopback.
        services.AddHttpClient("parakeet-local", c => c.Timeout = TimeSpan.FromMinutes(5))
            .UseSocketsHttpHandler((h, _) => { h.UseProxy = false; h.AllowAutoRedirect = false; });
        services.AddHttpClient("downloads", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient("licensing", c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler<RetryingHandler>();
    }
}
