using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Transcription.Clients;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The ONE key check behind both places a transcription provider's API key is entered — the
/// Models page and setup's cloud step (2026-10-10). Both used to build the client and classify
/// its answer themselves, and both saved a key on any exception; the classification now lives in
/// <see cref="ApiKeyCheck"/> and the pages only act on the outcome.
/// </summary>
internal static class TranscriptionKeyCheck
{
    private static ILogger Logger => Log.ForContext(typeof(TranscriptionKeyCheck));

    /// <summary>
    /// Checks <paramref name="key"/> for <paramref name="providerKey"/> ("groq", "deepgram",
    /// "elevenlabs", "openai"); any other provider answers Valid, as it always did. Never throws
    /// except on caller cancellation: an exception from building or sending the check (a key that
    /// cannot form a header, for one) is Unverified, and the page's save then reports the key's
    /// format. Logs one line per check — outcome, reason, HTTP status or exception TYPE; never the
    /// key, never the response body.
    /// </summary>
    public static async Task<ApiKeyCheckResult> RunAsync(HttpClient http, string providerKey, string key,
        CancellationToken ct = default)
    {
        ITranscriptionService? client = providerKey switch
        {
            "groq" => new GroqClient(http, key),
            "deepgram" => new DeepgramClient(http, key),
            "elevenlabs" => new ElevenLabsClient(http, key),
            "openai" => new OpenAITranscriptionClient(http, key),
            _ => null
        };
        if (client is null)
        {
            Logger.Information("API key check for {Provider}: no check for this provider", providerKey);
            return new ApiKeyCheckResult(ApiKeyCheckOutcome.Valid, null, ApiKeyCheck.ReasonAccepted);
        }

        ApiKeyCheckResult result;
        try
        {
            result = await client.CheckKeyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("API key check for {Provider}: {Outcome} ({Reason}, {ErrorType})",
                providerKey, ApiKeyCheckOutcome.Unverified, ApiKeyCheck.ReasonClientFault, ex.GetType().Name);
            return new ApiKeyCheckResult(ApiKeyCheckOutcome.Unverified, null, ApiKeyCheck.ReasonClientFault);
        }

        Logger.Information("API key check for {Provider}: {Outcome} ({Reason}, HTTP {HttpStatus})",
            providerKey, result.Outcome, result.Reason, result.HttpStatus?.ToString() ?? "none");
        return result;
    }
}
