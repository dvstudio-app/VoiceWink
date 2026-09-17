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
/// <item><c>images</c> — 5 min Timeout (measured, 2026-09-16 — see the registration
/// comment), otherwise identical policy to <c>ai</c>.</item>
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
        // 5 min, cut from 10 on 2026-09-16 and set by MEASUREMENT plus the retry arithmetic
        // below — not by headroom guessing, and not by the observed maximum alone.
        //
        // MEASUREMENT. Over a 14-day local corpus (196 COMPLETED image jobs, 17 models,
        // 4 providers) the slowest successful job was 189 s and p99 was 183 s; nothing completed
        // past 190 s, including the case the 2026-07-13 raise to 10 min was made for (OpenAI
        // high-quality gpt-image topped out at 155 s). What the 600 s wall actually bought was
        // dead waiting: 25 timeouts all at exactly 600 s, plus 6 jobs the owner cancelled by hand
        // at a median of 357 s. The models that hang (riverflow / seedream via OpenRouter) DO
        // complete when they work, inside that same ~190 s — they hang or they finish, so no
        // budget between 4 and 10 min rescues one.
        //
        // ARITHMETIC, and it is why this is 300 s and not the 240 s a first pass chose.
        // HttpClient.Timeout is a WHOLE-OPERATION budget: RetryingHandler's attempts and its
        // sleeps run inside it. With MaxAttempts = 4, at most THREE attempts fail before the one
        // that generates, so the fixed-schedule worst case is 3 x 10 s ConnectTimeout + (1+3+6) s
        // = 40 s of overhead BEFORE that attempt, plus its own connect (<= 10 s) = 50 s of connect
        // and sleep across all four. Against the slowest observed generation that is 239 s, and
        // slightly conservative at that: the 189 s corpus figure is measured end to end, so it
        // already contains the final attempt's connect. At 240 s that is ONE SECOND of margin; at
        // 300 s it is ~61 s. Redo this sum before moving this value, MaxAttempts, BackoffSchedule
        // or ConnectTimeout — the same warning http-policy.md carries for the ai client's 60 s
        // enhancement deadline. (Both diff reviewers independently corrected the first wording,
        // which read as though all four attempts burn a connect ahead of the generating one.)
        //
        // STATED RESIDUAL: ComputeDelay PREFERS a Retry-After header, clamped at MaxDelay = 60 s,
        // so three such sleeps could burn 180 s of this budget and leave a slow generation unable
        // to finish. Not observed — all 39 retries in the corpus (12 of them on 429) took a
        // fixed-schedule delay, none honoured a Retry-After — and no budget under ~6.5 min
        // survives that chain, so it is recorded rather than designed around.
        //
        // The user-facing timeout message derives from this value
        // (SendWithImageTimeoutTranslationAsync), so it follows with no copy change.
        services.AddHttpClient("images", c => c.Timeout = TimeSpan.FromMinutes(5))
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
