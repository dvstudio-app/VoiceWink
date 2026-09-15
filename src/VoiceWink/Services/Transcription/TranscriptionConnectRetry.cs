using Serilog;
using VoiceWink.Services.Http;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// NET-1: one bounded automatic retry of a whole transcription operation when — and only when —
/// the attempt failed while establishing the connection.
///
/// <para><b>The incident.</b> Dictating from a high-speed train (2026-07-31, Deepgram nova-3):
/// four consecutive <c>Connection timed out</c> failures at exactly 10.0 s before a fifth
/// attempt succeeded. Not an audio problem — those recordings measured −30..−38 dBFS with clean
/// 48 kHz capture. Each failure landed on the user as a manual Retry tap.</para>
///
/// <para><b>Why this is the ONE safe retry.</b> A connect-phase failure means DNS/TCP/TLS never
/// completed, so no audio was uploaded and no provider work started. Every other transcription
/// failure may have reached the provider, where a replay risks double-billing or duplicate work.
/// The rule "transcription NEVER retries" therefore stands for every class except this one, and
/// <see cref="ConnectTimeoutException"/> — emitted for one narrow runtime shape from one place —
/// is the boundary. Anything ambiguous arrives as a plain <see cref="HttpRequestException"/> and
/// is NOT retried: the carve-out fails closed.</para>
///
/// <para><b>Why it wraps the OPERATION, not the request.</b> The obvious fix — adding
/// <c>RetryingHandler</c> to the transcription pipeline — is unsafe: every client sends a
/// <c>StreamContent</c> over a live <c>FileStream</c>, which a handler cannot replay, and a
/// partially-read stream uploads truncated audio that does not fail loudly (it transcribes to
/// garbage). Re-invoking the whole operation sidesteps this entirely, because each client opens
/// its own <c>FileStream</c> INSIDE <c>TranscribeAsync</c> — a second invocation gets a fresh
/// stream, content and request by construction, with zero changes to any client (Codex plan
/// review: four client refactors were unnecessary).</para>
///
/// <para><b>Bound: two attempts.</b> One automatic retry, then the ordinary manual-Retry surface.
/// The observed streak was four, so this does not make that streak invisible — deliberately: a
/// longer silent stall is its own bad experience, and the retained-WAV manual Retry already
/// exists. Callers supply their own <c>onRetrying</c> surface because a shared route would show
/// a RECORDER pill for Audio Transcribe file work (both reviewers, independently).</para>
/// </summary>
internal static class TranscriptionConnectRetry
{
    private static ILogger Logger => Log.ForContext(typeof(TranscriptionConnectRetry));

    /// <summary>Total attempts, including the first. One automatic retry.</summary>
    internal const int MaxAttempts = 2;

    /// <summary>Backoff before the retry. Short by design: the connect budget already spent
    /// 10 s, and the user is waiting on a dictation.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Run <paramref name="operation"/>, retrying it once if it fails with
    /// <see cref="ConnectTimeoutException"/>.
    /// </summary>
    /// <param name="operation">The whole transcription call. Re-invoked verbatim on retry, so it
    /// must build its own request/stream per invocation — which every client already does.</param>
    /// <param name="onRetrying">Optional caller-owned notice, invoked ONCE before the retry.
    /// Fail-soft: a throwing notice must never cost a transcription.</param>
    /// <param name="delay">Test seam. Null uses <see cref="RetryDelay"/>.</param>
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Task>? onRetrying,
        CancellationToken ct,
        TimeSpan? delay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            // Catch the subtype UNCONDITIONALLY, then order the decisions inside. A `when` filter
            // excluding a cancelled token looks equivalent but is not: an unmatched filter lets
            // the ConnectTimeoutException escape, so a user who cancelled during the connect
            // would be shown "Network error — check your connection" instead of a cancellation
            // (Codex diff review — and the first version of this file's own test pinned that
            // wrong behaviour). Cancellation must beat BOTH the retry and the terminal throw.
            catch (ConnectTimeoutException)
            {
                // Caller cancellation wins first, always — before the notice, before the
                // backoff, and before re-throwing an exhausted budget as a network failure.
                ct.ThrowIfCancellationRequested();

                if (attempt >= MaxAttempts) throw;

                // Flags only — never the audio path, API key, hints, or any transcript text.
                Logger.Warning(
                    "Transcription connect timed out on attempt {Attempt} of {MaxAttempts} — retrying",
                    attempt, MaxAttempts);

                if (onRetrying != null)
                {
                    try { await onRetrying().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Logger.Warning("Transcription retry notice failed: {ErrorType}", ex.GetType().Name);
                    }
                }

                // Cancellation during the backoff propagates as OCE — the caller's own cancel.
                await Task.Delay(delay ?? RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
