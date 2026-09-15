using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>
/// OpenAI-compatible chat completions client (supports OpenAI, Groq, Gemini, Mistral, OpenRouter, Cerebras).
/// </summary>
public sealed class OpenAICompatibleClient
{
    private static ILogger Logger => Log.ForContext<OpenAICompatibleClient>();

    private readonly HttpClient _http;
    private readonly AIProviderConfig _config;

    public OpenAICompatibleClient(HttpClient http, AIProviderConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<string> EnhanceAsync(string systemPrompt, string userText, CancellationToken ct = default)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');

        // Try chat completions first; if the model requires the Responses API, fall back.
        if (_config.Provider == AIProvider.OpenAI)
        {
            var (result, needsResponsesApi) = await TryChatCompletionsAsync(baseUrl, systemPrompt, userText, ct).ConfigureAwait(false);
            if (needsResponsesApi)
            {
                Logger.Information("Model {Model} requires Responses API — retrying", _config.ModelName);
                return await CallResponsesApiAsync(baseUrl, systemPrompt, userText, ct).ConfigureAwait(false);
            }
            return result!;
        }

        var (text, _) = await TryChatCompletionsAsync(baseUrl, systemPrompt, userText, ct).ConfigureAwait(false);
        return text!;
    }

    /// <summary>
    /// Call the /chat/completions endpoint. Returns (result, needsResponsesApi).
    /// If the model returns a 404 indicating it only supports /v1/responses,
    /// returns (null, true) so the caller can retry with the Responses API.
    /// </summary>
    private async Task<(string? result, bool needsResponsesApi)> TryChatCompletionsAsync(
        string baseUrl, string systemPrompt, string userText, CancellationToken ct)
    {
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userText }
        };

        // Build request body -- newer OpenAI models (gpt-4o+) require max_completion_tokens
        // instead of max_tokens. Use max_completion_tokens for OpenAI, max_tokens for others.
        // Temperature is omitted for OpenAI — newer models (gpt-5-mini etc.) only accept the
        // default value (1) and reject custom values. Other providers still support it.
        // ENH-8: the None path keeps the EXISTING anonymous-type bodies untouched (a request for
        // a model with no reasoning row is byte-identical to a pre-ENH-8 build; since ENH-23 the
        // Default choice itself resolves a row for listed models); only a resolved directive
        // takes a reasoning-bearing shape. Kinds that don't match this client's provider are
        // ignored defensively — the policy already provider-scopes them.
        var directive = _config.Reasoning;
        string json;
        if (_config.Provider == AIProvider.OpenAI)
        {
            json = directive is { Kind: ReasoningWireKind.OpenAIEffort, Effort: { } chatEffort }
                ? JsonSerializer.Serialize(new
                {
                    model = _config.ModelName,
                    max_completion_tokens = _config.MaxTokens,
                    reasoning_effort = chatEffort,
                    messages
                })
                : JsonSerializer.Serialize(new
                {
                    model = _config.ModelName,
                    max_completion_tokens = _config.MaxTokens,
                    messages
                });
        }
        else if (_config.Provider == AIProvider.Gemini
                 && directive is { Kind: ReasoningWireKind.GeminiEffort, Effort: { } geminiEffort })
        {
            json = JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                reasoning_effort = geminiEffort,
                messages
            });
        }
        else if (_config.Provider == AIProvider.OpenRouter
                 && directive is { Kind: ReasoningWireKind.OpenRouterEffort, Effort: { } routerEffort })
        {
            json = JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                reasoning = new { effort = routerEffort },
                messages
            });
        }
        else if (_config.Provider is AIProvider.Groq or AIProvider.Cerebras
                 && directive is { Kind: ReasoningWireKind.CompatEffort, Effort: { } compatEffort })
        {
            // ENH-23: the two OpenAI-compatible providers take reasoning_effort on the plain
            // max_tokens + temperature body — the generic shape below plus one field.
            json = JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                reasoning_effort = compatEffort,
                messages
            });
        }
        else
        {
            json = JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                messages
            });
        }

        // Gemini uses different endpoint
        var endpoint = _config.Provider == AIProvider.Gemini
            ? $"{baseUrl}/openai/chat/completions"
            : $"{baseUrl}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                errorBody = string.Empty;
            }

            // Detect models that require the Responses API (e.g. gpt-5-pro, o3-pro)
            if (response.StatusCode == global::System.Net.HttpStatusCode.NotFound &&
                errorBody.Contains("v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            // Warning, not Error: non-success provider statuses are environmental —
            // mirrors HttpResponseExtensions.EnsureSuccessOrLogAndThrowAsync.
            // SEC-2: {ReasonPhrase} is the redacted-by-name property — the phrase is server-supplied
            // and no longer rides the exception Message. Without this line the phrase would be lost
            // entirely on this path, since it is the SECOND ProviderApiException creation site and
            // does not go through EnsureSuccessOrLogAndThrowAsync.
            Logger.Warning("API returned {Status}; Body={Body}; ReasonPhrase={ReasonPhrase}",
                (int)response.StatusCode, errorBody.TruncateForLog(), response.ReasonPhrase?.TruncateForLog());
            // ENH-1: this inline branch is the text-enhancement path (Gemini et al.) —
            // throw the same provider-message-carrying exception as
            // EnsureSuccessOrLogAndThrowAsync so pill/status surfaces can show WHY.
            throw ProviderApiException.From(_config.Provider.ToString(), response, errorBody);
        }

        var responseJson = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        // Only the HAPPY PATH sits inside the guard — the same split GeminiImageClient makes.
        // Wrong-kind JsonElement access here (e.g. {"choices":{}}) is API drift that must log
        // Error and reach Sentry, while the provider-outcome branch BELOW stays outside so its
        // deliberate throw keeps Warning semantics. A miss returns null instead of throwing:
        // the shape questions are re-asked below, where the outcome gets classified.
        var extracted = ProviderResponseGuard.Run<string?>(Logger, "Chat", responseJson, () =>
        {
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                // Observability only (PRM-2, audit F12): a length-truncated answer is
                // NOT rejected — the think-tag filter's empty-result fallback covers
                // tagged truncation, but a non-empty partial answer still pastes.
                // Warning (never Error): provider outcome, not a defect.
                if (choices[0].TryGetProperty("finish_reason", out var finishReason)
                    && finishReason.ValueKind == JsonValueKind.String
                    && finishReason.GetString() == "length")
                {
                    Logger.Warning("Enhancement output truncated at max_tokens (finish_reason=length, model={Model})", _config.ModelName);
                }

                return content.GetString() ?? "";
            }

            return null;
        });
        // Empty-but-present content is a RESULT (""), not a miss — null means only that the
        // happy path did not match, never that the model returned nothing.
        if (extracted != null)
            return (extracted, false);

        // A reasoning model that spends its whole max_tokens budget thinking answers 200 with
        // finish_reason=length and NO content property at all (Cerebras qwen-3.8-27b, observed
        // twice in 14 days). That is an ordinary provider outcome, not contract drift, so it
        // logs Warning and never becomes a Sentry event. Every read below the guard is
        // non-throwing on purpose: a wrong-kind throw out here carries no Error line, so it
        // would be drift that reports nothing — hence ToString() over GetString(), and a
        // ValueKind check before GetArrayLength().
        if (doc.RootElement.TryGetProperty("choices", out var outcomeChoices)
            && outcomeChoices.ValueKind == JsonValueKind.Array
            && outcomeChoices.GetArrayLength() > 0
            && outcomeChoices[0].TryGetProperty("message", out var outcomeMessage)
            // Currently always true here — a PRESENT content either returned above (string, or
            // JSON null via ?? "") or threw inside the guard (any other kind, from GetString()).
            // Kept so this branch states its own precondition instead of inheriting correctness
            // from what the guard's happy path happens to do with GetString().
            && !outcomeMessage.TryGetProperty("content", out _)
            && outcomeChoices[0].TryGetProperty("finish_reason", out var outcomeFinish)
            && outcomeFinish.ToString() == "length")
        {
            // reasoningPresent is a BOOLEAN and the reasoning text is never logged — it echoes
            // the user's dictation, and no {Reasoning} property exists in the redaction allowlist.
            Logger.Warning(
                "Enhancement produced no content: finish_reason=length with no message.content (model={Model}, reasoningPresent={ReasoningPresent}) — the completion hit max_tokens before emitting an answer; Body={Body}",
                _config.ModelName,
                outcomeMessage.TryGetProperty("reasoning", out _),
                responseJson.TruncateForLog());
            // 22 units. ComposeFallbackFailureStatus's worst-case reason budget is 26
            // (" — transcription on clipboard"), and it truncates the REASON, so a longer
            // sentence loses "token limit" to the ellipsis — the one part worth saying.
            throw new InvalidOperationException("No answer: token limit");
        }

        // Unexplained 200-with-unexpected-shape: contract drift, reported exactly as before.
        // The guard's OWN message template and reason string, verbatim, so the shapes that used
        // to reach the explicit throw (missing/empty choices, no message, no content) keep the
        // fingerprint they already had. Wrong-kind access ({"choices":{}}) still throws INSIDE
        // the guard and is NOT an example of the shared reason string — the guard logs
        // ex.Message there, which is JsonElement's kind error, not this one.
        Logger.Error("{Provider} response shape unexpected: {Reason}; Body={Body}",
            "Chat",
            "API response missing 'choices[0].message.content'",
            responseJson.TruncateForLog());
        throw new InvalidOperationException("API response missing 'choices[0].message.content'");
    }

    /// <summary>
    /// Call the OpenAI Responses API (/v1/responses) for models that don't support chat completions.
    /// </summary>
    private async Task<string> CallResponsesApiAsync(
        string baseUrl, string systemPrompt, string userText, CancellationToken ct)
    {
        // ENH-8: same None-keeps-existing-shape rule as the chat branch; the Responses
        // API carries the effort as a nested reasoning object.
        var json = _config.Reasoning is { Kind: ReasoningWireKind.OpenAIEffort, Effort: { } effort }
            ? JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                instructions = systemPrompt,
                input = userText,
                max_output_tokens = _config.MaxTokens,
                reasoning = new { effort }
            })
            : JsonSerializer.Serialize(new
            {
                model = _config.ModelName,
                instructions = systemPrompt,
                input = userText,
                max_output_tokens = _config.MaxTokens
            });

        var endpoint = $"{baseUrl}/responses";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Responses", Logger, ct).ConfigureAwait(false);

        var responseJson = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        // Observability only (PRM-2, audit F12): mirror the chat path's truncation
        // warning — an incomplete response still returns whatever text arrived.
        // Non-throwing reads, outside the guard (provider outcome, not drift).
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && status.GetString() == "incomplete"
            && doc.RootElement.TryGetProperty("incomplete_details", out var incompleteDetails)
            && incompleteDetails.ValueKind == JsonValueKind.Object
            && incompleteDetails.TryGetProperty("reason", out var incompleteReason)
            && incompleteReason.ValueKind == JsonValueKind.String
            && incompleteReason.GetString() == "max_output_tokens")
        {
            Logger.Warning("Enhancement output truncated at max_output_tokens (Responses API, model={Model})", _config.ModelName);
        }

        // Same guard rationale as the chat path above.
        return ProviderResponseGuard.Run(Logger, "Responses", responseJson, () =>
        {
            // Responses API format: { output: [{ type: "message", content: [{ type: "output_text", text: "..." }] }] }
            if (doc.RootElement.TryGetProperty("output", out var output))
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                        item.TryGetProperty("content", out var contentArr))
                    {
                        foreach (var block in contentArr.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "output_text" &&
                                block.TryGetProperty("text", out var text))
                            {
                                return text.GetString() ?? "";
                            }
                        }
                    }
                }
            }

            // Fallback: some responses may use output_text at top level
            if (doc.RootElement.TryGetProperty("output_text", out var directText))
            {
                return directText.GetString() ?? "";
            }

            throw new InvalidOperationException("Responses API response missing output text");
        });
    }
}
