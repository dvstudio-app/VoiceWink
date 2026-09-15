namespace VoiceWink.Services.Http;

/// <summary>
/// Translates a connect-phase timeout into <see cref="HttpRequestException"/> (ENH-3).
/// On .NET 8, <c>SocketsHttpHandler.ConnectTimeout</c> expiry surfaces as a
/// <see cref="TaskCanceledException"/> with the handler-pipeline token NOT cancelled
/// (HttpClient.Timeout and caller cancellation both cancel that token, so genuine
/// cancellations pass through untouched). Without this translation the stray
/// OperationCanceledException leaks to catch sites that mislabel it: the transcribe
/// path's outer OCE catch reads it as "cancelled by user" (silent loss), and
/// <see cref="RetryingHandler"/> — which retries only HttpRequestException — would
/// not retry it. Sits BELOW RetryingHandler on the "ai"/"images" pipelines (so a
/// connect-timeout is retried like any other network error) and alone on
/// "transcription", whose HTTP pipeline still has no retry handler — upload bodies
/// are not replayable. Since NET-1 that path retries at the OPERATION level instead
/// (<c>TranscriptionConnectRetry</c>), gated on the <see cref="ConnectTimeoutException"/>
/// this handler emits; see the type-vs-shape reasoning at the throw site below.
/// </summary>
internal sealed class ConnectTimeoutTranslatingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Static message: log-safe ({ErrorMessage} → Sentry breadcrumbs) and
            // pill-safe (20 chars — fits the 55-char pill cap under both artifact-naming
            // suffixes, " — pasted transcription" / " — transcription on clipboard").
            // Identical for BOTH branches below — only the exception TYPE differs, so no
            // user-facing copy or failure-description mapping moves.
            const string message = "Connection timed out";

            // NET-1: the TYPED signal is emitted only for the strong shape a real ConnectTimeout
            // expiry produces — TaskCanceledException wrapping a TimeoutException. That type is
            // what permits an automatic transcription retry, so it must mean "the failure
            // happened BEFORE any upload", not merely "some non-caller cancellation reached this
            // handler". Anything ambiguous keeps the plain translation and is therefore never
            // retried: the carve-out fails CLOSED. Behaviour for ai/images is unchanged either
            // way — RetryingHandler catches HttpRequestException, and the subtype is one.
            throw IsConnectTimeoutShape(ex)
                ? new ConnectTimeoutException(message, ex)
                : new HttpRequestException(message, ex);
        }
    }

    /// <summary>The observed .NET 8 <c>SocketsHttpHandler.ConnectTimeout</c> shape: a
    /// <see cref="TaskCanceledException"/> whose inner exception is a
    /// <see cref="TimeoutException"/>. Pinned by <c>ConnectTimeoutTranslatingHandlerTests</c>,
    /// including a negative row proving an ambiguous non-caller cancellation does NOT qualify.</summary>
    private static bool IsConnectTimeoutShape(OperationCanceledException ex)
        => ex is TaskCanceledException { InnerException: TimeoutException };
}
