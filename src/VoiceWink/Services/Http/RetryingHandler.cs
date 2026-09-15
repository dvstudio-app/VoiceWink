using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace VoiceWink.Services.Http;

/// <summary>
/// Retries transient HTTP failures (408, 429, 5xx, network errors) on a fixed delay
/// TABLE, with Retry-After preferred over it. Buffers request content into memory to enable
/// resend, so only apply this handler to clients sending small bodies (JSON chat
/// requests) — not file uploads or large streaming downloads.
/// Hard-down exception (ENH-3): a DNS-dead / link-down failure while NO network
/// interface is up is not retried — the 2026-07-07 incident spent 2+ min retrying a
/// dead link. Fail-open: any other failure, or the same failure while an interface
/// is still up (VPN flap, captive portal), keeps the normal retry behavior.
/// </summary>
internal sealed class RetryingHandler : DelegatingHandler
{
    private static ILogger Logger => Log.ForContext<RetryingHandler>();

    /// <summary>
    /// Internal, not private, because <c>ImagePayloadOracleTests</c> pins that a
    /// <c>ReadOnlyMemoryContent</c> image body survives one re-read per attempt — a coupling
    /// that lived as a hard-coded 3 until this constant moved (Codex plan round). Deriving that
    /// loop from here is what stops it going stale silently: over-reading still PASSES, so a
    /// stale copy under-tests the guarantee instead of failing.
    /// </summary>
    internal const int MaxAttempts = 4;

    /// <summary>
    /// Delay BEFORE attempt n+1, so the last entry is used and the array length is the whole
    /// answer to "is the final delay reachable" (it must be <c>MaxAttempts - 1</c> long).
    /// A table, not <c>base * 2^(n-1)</c>: 1/3/6 is not exponential, and the arithmetic form
    /// carried a comment claiming "200ms, 400ms, 800ms" while 800 ms was unreachable at three
    /// attempts — the kind of dead value a table makes impossible to write.
    /// Owner decision 2026-09-09: Cerebras sheds load with a 429 whose body says
    /// "please try again soon" and carries NO Retry-After, and the previous 200/400 ms schedule
    /// answered that inside ~1 second (measured end to end at 1.02 s), three times in 14 days.
    /// The cost is bounded and accepted: a transient failure now takes ~10 s to fall back.
    /// </summary>
    /// <remarks>
    /// Internal so <c>RetryingHandlerTests</c> can pin <c>Length == MaxAttempts - 1</c> directly.
    /// The exhaustion rows assert the three delays that RAN, which catches a raised
    /// <c>MaxAttempts</c> (mutation-verified) but NOT a table that grew a fourth entry while
    /// <c>MaxAttempts</c> stayed 4 — that records the same triple, stays green, and leaves a dead
    /// entry behind (Grok diff r1). That is exactly how the "800 ms" the old arithmetic advertised
    /// came to be unreachable, so the invariant is pinned rather than described.
    /// </remarks>
    internal static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(6)
    };

    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    internal static readonly HttpRequestOptionsKey<bool> DisableRetryOption = new("VoiceWink.DisableRetry");

    private readonly Func<bool> _networkAvailable;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryingHandler() : this(NetworkInterface.GetIsNetworkAvailable)
    {
    }

    /// <summary>Test seam: hard-down rows must not depend on the machine's real NICs.</summary>
    internal RetryingHandler(Func<bool> networkAvailable)
        : this(networkAvailable, static (d, ct) => Task.Delay(d, ct))
    {
    }

    /// <summary>
    /// Test seam for the SCHEDULE, same idiom as <paramref name="networkAvailable"/>. Without it
    /// the widened backoff is paid in wall-clock on every run: the delay-incurring rows in
    /// <c>RetryingHandlerTests</c> go from roughly 3.8 s to roughly 40 s, and the suite runs
    /// sequentially (parallelism is disabled in <c>AssemblyInfo.cs</c>) — about the whole win
    /// VEL-1 measured and then DECLINED to chase, handed straight back to sleeping.
    /// Schedule rows record the requested delays and return a completed task; the cancellation
    /// row deliberately keeps the production delay, because only a real <c>Task.Delay</c> proves
    /// the caller's token reaches it.
    /// </summary>
    internal RetryingHandler(Func<bool> networkAvailable, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _networkAvailable = networkAvailable;
        _delay = delay;
    }

    internal static void DisableRetries(HttpRequestMessage request)
    {
        request.Options.Set(DisableRetryOption, true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(DisableRetryOption, out var disableRetry) && disableRetry)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var attemptRequest = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // LOG-1 (owner report 2026-07-15): type + message only, never the
                // exception OBJECT — a classified, fully-handled network failure was
                // writing its complete stack (× attempts × requests) into the file
                // log during every outage; the SocketError/type already say it all.
                if (IsHardOffline(ex) && !_networkAvailable())
                {
                    Logger.Warning("HTTP attempt {Attempt}/{Max} failed ({SocketError}) with no network interface up — not retrying: {ErrorType}: {ErrorMessage}",
                        attempt, MaxAttempts, ((SocketException)ex.InnerException!).SocketErrorCode, ex.GetType().Name, ex.Message);
                    throw;
                }
                var delay = ComputeDelay(attempt, retryAfter: null);
                Logger.Warning("HTTP attempt {Attempt}/{Max} failed with network error; retrying after {DelayMs}ms: {ErrorType}: {ErrorMessage}",
                    attempt, MaxAttempts, (int)delay.TotalMilliseconds, ex.GetType().Name, ex.Message);
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt == MaxAttempts || !IsTransientStatus(response.StatusCode))
                return response;

            var retryDelay = ComputeDelay(attempt, GetRetryAfter(response));
            Logger.Warning("HTTP attempt {Attempt}/{Max} returned {Status}; retrying after {DelayMs}ms",
                attempt, MaxAttempts, (int)response.StatusCode, (int)retryDelay.TotalMilliseconds);
            response.Dispose();
            await _delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("RetryingHandler: retry loop exited without returning (unreachable).");
    }

    /// <summary>
    /// DNS-dead / link-down socket signatures. Only meaningful combined with the
    /// "no interface up" probe — alone they can be transient (resolver hiccup).
    /// </summary>
    internal static bool IsHardOffline(HttpRequestException ex)
        => ex.InnerException is SocketException se && se.SocketErrorCode is
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
            or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown;

    /// <summary>
    /// The repo's single definition of a TRANSIENT HTTP status: worth retrying, and worth
    /// telling the user to try again. Internal (was private) so the transcription-failure copy
    /// in <c>MainViewModel.DescribeTranscriptionFailure</c> derives "try again" advice from the
    /// same rule the retry policy uses, instead of a second hand-maintained list that could
    /// promise a retry this handler would never make (Codex diff review round 3, 2026-07-25).
    /// </summary>
    internal static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || ((int)status >= 500 && (int)status < 600);

    private static TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra < MaxDelay ? ra : MaxDelay;

        // attempt is 1-based and only ever reaches MaxAttempts - 1 here (the loop returns at the
        // last attempt rather than sleeping), so every entry is reachable and the clamp is a
        // belt-and-braces bound rather than live logic.
        var index = Math.Clamp(attempt - 1, 0, BackoffSchedule.Length - 1);
        return BackoffSchedule[index];
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header == null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        if (request.Content != null)
        {
            var buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(buffer);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
