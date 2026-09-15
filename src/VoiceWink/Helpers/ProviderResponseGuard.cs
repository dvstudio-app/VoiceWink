using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Guarantees contract-drift visibility for provider response-shape extraction.
///
/// <para>The enhancement catch sites (<c>MainViewModel</c> / <c>AIEnhancementService</c>)
/// downgrade <see cref="InvalidOperationException"/> to Warning because clients throw it
/// for handled provider outcomes (safety blocks, size limits) — but <c>JsonElement</c>
/// accessors ALSO throw it on wrong-kind access (e.g. a 200 response with
/// <c>{"choices":{}}</c>), which is API drift or a parser defect and MUST reach Sentry
/// (the sub-logger forwards Error+ only). Wrapping the extraction in
/// <see cref="Run{T}"/> / <see cref="RunAsync{T}"/> logs Error with the truncated body
/// for ANY <see cref="InvalidOperationException"/> raised inside — wrong-kind access and
/// deliberate shape-failure throws alike — before rethrowing. The <c>Body</c> property
/// name is in <c>LogRedactionEnricher.RedactedPropertyNames</c>, so the Sentry path sees
/// <c>&lt;REDACTED&gt;</c> while the local file keeps the full (truncated) body.</para>
/// </summary>
internal static class ProviderResponseGuard
{
    public static T Run<T>(ILogger logger, string providerName, string responseJson, Func<T> extract)
    {
        try
        {
            return extract();
        }
        catch (InvalidOperationException ex)
        {
            logger.Error("{Provider} response shape unexpected: {Reason}; Body={Body}",
                providerName, ex.Message, responseJson.TruncateForLog());
            throw;
        }
    }

    public static async Task<T> RunAsync<T>(ILogger logger, string providerName, string responseJson, Func<Task<T>> extract)
    {
        try
        {
            return await extract().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.Error("{Provider} response shape unexpected: {Reason}; Body={Body}",
                providerName, ex.Message, responseJson.TruncateForLog());
            throw;
        }
    }
}
