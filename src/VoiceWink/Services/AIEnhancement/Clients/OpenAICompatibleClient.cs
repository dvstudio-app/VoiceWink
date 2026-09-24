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

    /// <summary>
    /// ENH-27: the refusal reason for a cleanup the provider cut off at the token ceiling. Shared
    /// by all THREE wire shapes that can report it — chat <c>finish_reason=length</c>, the
    /// Responses API's <c>status=incomplete</c> / <c>max_output_tokens</c>, and Anthropic's
    /// <c>stop_reason=max_tokens</c> — so the pill cannot say three different things about one
    /// cause. <b>20 code units, and the budget is why.</b>
    /// <c>MainViewModel.ComposeFallbackFailureStatus</c> renders <c>"{reason} — {outcome}"</c>
    /// inside 55 code units and truncates the REASON, never the suffix; the worst-case suffix
    /// (<c>" — transcription on clipboard"</c>) is 29 units, so the reason budget is <b>26</b>.
    /// A longer sentence would lose "token limit" to the ellipsis — the only part that tells the
    /// user what to change. Matches the sibling <c>"No answer: token limit"</c> (22 units) for the
    /// same reason. State the BUDGET, not the suffix length: reading "appends 26" and computing
    /// 55 − 26 makes a 29-unit reword look safe when it silently truncates.
    /// </summary>
    internal const string TruncatedReason = "Cut off: token limit";

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
        // LAI-1: a local server sends a key only when one is stored; every cloud provider keeps the
        // header it always sent, byte for byte.
        if (_config.Provider == AIProvider.LocalServer)
            LocalServerClient.AddAuthorization(request, _config);
        else
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
            // OpenAI only: that is the one provider EnhanceAsync retries on the Responses API. For
            // any other the (null, true) answer became a null result, which the output filter
            // turns into "" and the caller into a silent raw paste that looks like a successful
            // cleanup — reachable through a proxy relaying OpenAI's message (LAI-1 self-review).
            if (_config.Provider == AIProvider.OpenAI &&
                response.StatusCode == global::System.Net.HttpStatusCode.NotFound &&
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
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseJson);
        }
        catch (JsonException) when (_config.Provider == AIProvider.LocalServer)
        {
            // LAI-1 (Grok diff r1): a user-run server answering 200 with a web page is the user's
            // configuration, not contract drift. A raw JsonException is outside every enhancement
            // catch's Warning list, so it would file a Sentry event per dictation; this is the
            // same message and level LocalServerClient uses on the Ollama path. Cloud parsing is
            // unchanged.
            Logger.Warning("Local server reply is not JSON; Body={Body}", responseJson.TruncateForLog());
            throw new InvalidOperationException("No OpenAI-compatible server answered at this address");
        }
        using var parsedDoc = doc;

        // Only the HAPPY PATH sits inside the guard — the same split GeminiImageClient makes.
        // Wrong-kind JsonElement access here (e.g. {"choices":{}}) is API drift that must log
        // Error and reach Sentry, while the provider-outcome branch BELOW stays outside so its
        // deliberate throw keeps Warning semantics. A miss returns null instead of throwing:
        // the shape questions are re-asked below, where the outcome gets classified.
        // ENH-27, closing the second half of audit finding F12. Until 2026-09-15 a truncated
        // answer only LOGGED and then pasted: the user's document silently took a sentence that
        // stops mid-word, with nothing on screen to say so. It is now REFUSED, which hands the
        // caller the same outcome a FAILED cleanup already produces — the complete raw transcript
        // pastes, History marks [Enhancement failed], redo arms, red pill. A deliberate preference
        // for complete-but-rough over polished-but-cut: the rough text is recoverable by re-running
        // the cleanup, the missing tail is not, and an unnoticed truncation is the worse failure
        // precisely because it looks finished. This REMOVES a special case — the no-content shape
        // below (ENH-22) has always thrown for this same cause.
        //
        // OUTSIDE the guard, and that placement is the whole correctness of it: the guard logs
        // Error + body for any InvalidOperationException raised INSIDE it, because in there the
        // type means contract drift and must reach Sentry. Thrown inside, every truncated cleanup
        // would file a Sentry event for an ordinary provider outcome — caught by this change's own
        // test, which asserts no drift Error accompanies the refusal.
        //
        // Requires content to be PRESENT as a STRING or JSON null: a length finish with NO content
        // property is ENH-22's starvation shape, which keeps its own "No answer" message further
        // down, and any OTHER kind (the content-as-parts array some OpenAI-compatible proxies
        // emit) is a shape defect that must still reach the guard's GetString() and be reported as
        // drift — never told to the user as a token-limit cut-off, which would name a cause they
        // cannot act on.
        //
        // EVERY element is ValueKind-checked before a property is read off it, and that is the
        // correctness of running ahead of the guard rather than a belt-and-braces habit:
        // JsonElement.TryGetProperty THROWS InvalidOperationException on a non-object element, and
        // out here that throw carries no Error line, so drift that used to reach Sentry from
        // inside the guard would report nothing. This branch now makes the FIRST property access
        // on the response, so three elements the guard used to touch first are ours to check —
        // the root, choices[0], and message. Pinned by
        // EnhanceAsync_WrongKindBeforeTheTruncationBranch_StillReportsDrift, which fails on all
        // five shapes without these checks.
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("choices", out var truncationChoices)
            && truncationChoices.ValueKind == JsonValueKind.Array
            && truncationChoices.GetArrayLength() > 0
            && truncationChoices[0].ValueKind == JsonValueKind.Object
            && truncationChoices[0].TryGetProperty("message", out var truncationMessage)
            && truncationMessage.ValueKind == JsonValueKind.Object
            && truncationMessage.TryGetProperty("content", out var truncationContent)
            && truncationContent.ValueKind is JsonValueKind.String or JsonValueKind.Null
            && truncationChoices[0].TryGetProperty("finish_reason", out var truncationFinish)
            && truncationFinish.ValueKind == JsonValueKind.String
            && truncationFinish.GetString() == "length")
        {
            Logger.Warning("Enhancement output truncated at max_tokens (finish_reason=length, model={Model})", _config.ModelName);
            throw new InvalidOperationException(TruncatedReason);
        }

        var extracted = ProviderResponseGuard.Run<string?>(Logger, "Chat", responseJson, () =>
        {
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
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
            // Currently always true here — a PRESENT content has already been disposed of three
            // ways above: a string or JSON null with finish_reason=length was REFUSED by ENH-27's
            // truncation branch, any other kind threw inside the guard (from GetString()), and
            // anything left returned from the guard's happy path (string, or JSON null via ?? "").
            // Kept so this branch states its own precondition instead of inheriting correctness
            // from what three separate pieces of code upstream happen to do.
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

        // ENH-27: the Responses-API spelling of the same cause, refused the same way — see
        // TruncatedReason and the chat branch for why a cut-off cleanup no longer pastes. Thrown
        // from OUTSIDE ProviderResponseGuard deliberately: the guard logs Error + body for any
        // InvalidOperationException raised INSIDE it, because in there the type means contract
        // drift and must reach Sentry. Here it means an ordinary provider outcome, so it stays a
        // Warning and the outer enhancement catch turns it into the raw-transcript fallback.
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
            throw new InvalidOperationException(TruncatedReason);
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
