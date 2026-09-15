using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>
/// Anthropic Messages API client.
/// </summary>
public sealed class AnthropicClient
{
    private static ILogger Logger => Log.ForContext<AnthropicClient>();

    private readonly HttpClient _http;
    private readonly AIProviderConfig _config;

    public AnthropicClient(HttpClient http, AIProviderConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<string> EnhanceAsync(string systemPrompt, string userText, CancellationToken ct = default)
    {
        var messages = new[]
        {
            new { role = "user", content = userText }
        };

        // ENH-8: the None path keeps the EXISTING anonymous-type body untouched — the
        // Default request is byte-identical to a pre-ENH-8 build by construction. Only a
        // resolved output_config.effort directive takes the second shape.
        var json = _config.Reasoning is { Kind: ReasoningWireKind.AnthropicOutputEffort, Effort: { } effort }
            ? JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                system = systemPrompt,
                messages,
                output_config = new { effort }
            })
            : JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                system = systemPrompt,
                messages
            });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.GetBaseUrl()}/messages");
        request.Headers.Add("x-api-key", _config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Anthropic", Logger, ct).ConfigureAwait(false);

        var responseJson = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        // Safety refusal (thinking-generation models): HTTP 200 with
        // stop_reason == "refusal" and an EMPTY content array. A provider
        // content-verdict, not contract drift — handled BEFORE the guard at
        // Warning (like GeminiImageClient's safety-block branch), so it never
        // ships to Sentry as an Error. Every access here is non-throwing
        // (root kind checked first: TryGetProperty throws on a non-object root,
        // and that case must stay inside the guard as contract drift).
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("stop_reason", out var stopReason)
            && stopReason.ValueKind == JsonValueKind.String
            && stopReason.GetString() == "refusal")
        {
            Logger.Warning("Anthropic declined the request (refusal)");
            // 40 chars — fits the pill's redo-status budget under both suffixes.
            throw new InvalidOperationException("Anthropic declined the request (refusal)");
        }

        // Observability only (PRM-2, audit F12): a max_tokens-truncated answer is not
        // rejected — see the matching finish_reason=length log in
        // OpenAICompatibleClient. Warning (never Error): provider outcome, not a defect.
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("stop_reason", out var stopReasonTruncated)
            && stopReasonTruncated.ValueKind == JsonValueKind.String
            && stopReasonTruncated.GetString() == "max_tokens")
        {
            Logger.Warning("Enhancement output truncated at max_tokens (stop_reason=max_tokens, model={Model})", _config.ModelName);
        }

        // The guard logs Error + body for ANY InvalidOperationException raised during
        // extraction — the deliberate throw below AND wrong-kind JsonElement access
        // (e.g. {"content":{}}) — because the outer enhancement catch downgrades that
        // type to Warning and 200-with-unexpected-shape must reach Sentry.
        return ProviderResponseGuard.Run(Logger, "Anthropic", responseJson, () =>
        {
            // Thinking-generation models (Claude Fable 5, Sonnet 5 with adaptive
            // thinking) lead the content array with "thinking" blocks; the answer
            // lives in later "text" blocks. Contract: select text-typed blocks,
            // never index content[0]. Multiple text blocks concatenate in order.
            if (doc.RootElement.TryGetProperty("content", out var contentArray)
                && contentArray.ValueKind == JsonValueKind.Array)
            {
                var text = new StringBuilder();
                var foundTextBlock = false;
                foreach (var block in contentArray.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "text"
                        && block.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String)
                    {
                        foundTextBlock = true;
                        text.Append(textElement.GetString());
                    }
                }

                // An empty-but-present text block is a valid shape (the enhancement
                // caller treats whitespace-only as fall-back-to-original) — only a
                // response with ZERO text-typed blocks is contract drift.
                if (foundTextBlock)
                    return text.ToString();
            }

            throw new InvalidOperationException("Anthropic API response contained no text content block");
        });
    }
}
