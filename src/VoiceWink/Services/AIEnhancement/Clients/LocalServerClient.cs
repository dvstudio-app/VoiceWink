using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.Providers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>
/// LAI-1: talks to a user-run AI server — Ollama through its native API, anything else through the
/// OpenAI-compatible surface (<see cref="LocalServerApi"/> says why the two differ).
/// </summary>
internal sealed class LocalServerClient
{
    private static ILogger Logger => Log.ForContext<LocalServerClient>();

    /// <summary>
    /// The context window asked of Ollama. Its default (4096 on current releases, 2048 on older
    /// ones) truncates the prompt FROM THE FRONT once the ~1,000-token system envelope plus the
    /// dictation exceed it, so the cleanup rules are what gets cut. 8192 holds the envelope and
    /// ~4,000 words of dictation with room for the answer — the same figure the plan picks for the
    /// bundled engine (LAI-4).
    /// </summary>
    internal const int OllamaContextTokens = 8192;

    /// <summary>
    /// LAI-8: how long Ollama keeps the model loaded after a request. Its default is 5 minutes,
    /// after which the next dictation pays the whole model load (21 s on an Intel Arc 140V,
    /// owner UAT 233). Sent on every request rather than asked of the user as an environment
    /// variable; a request value overrides OLLAMA_KEEP_ALIVE.
    /// </summary>
    internal const string OllamaKeepAlive = "30m";

    private readonly HttpClient _http;
    private readonly AIProviderConfig _config;

    public LocalServerClient(HttpClient http, AIProviderConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <summary>
    /// One automatic retry on a server error (HTTP 5xx). The incident (2026-09-29): a graphics
    /// driver update mid-session killed Ollama's model process, the next dictation got HTTP 500
    /// ("connection forcibly closed"), and Ollama starts a fresh model process on the next request.
    /// Safe to repeat because the server is the user's own — nothing is billed. Once only, inside the
    /// caller's deadline; a refused connection, a 4xx and a malformed reply are not retried.
    /// </summary>
    public async Task<string> EnhanceAsync(string systemPrompt, string userText, CancellationToken ct)
    {
        try
        {
            return await EnhanceOnceAsync(systemPrompt, userText, ct).ConfigureAwait(false);
        }
        // Caught UNCONDITIONALLY, cancellation checked inside: a stop-to-skip or supersede that lands
        // with the 5xx must surface as the cancellation it is, never as "AI enhancement failed".
        catch (ProviderApiException ex) when ((int?)ex.StatusCode is >= 500 and <= 599)
        {
            ct.ThrowIfCancellationRequested();
            Logger.Warning("Local server answered HTTP {Status} - retrying once", (int)ex.StatusCode!);
        }
        ct.ThrowIfCancellationRequested();
        return await EnhanceOnceAsync(systemPrompt, userText, ct).ConfigureAwait(false);
    }

    private Task<string> EnhanceOnceAsync(string systemPrompt, string userText, CancellationToken ct)
        => _config.LocalApi == LocalServerApi.Ollama
            ? OllamaChatAsync(systemPrompt, userText, ct)
            // The OpenAI-compatible body is the generic one OpenAICompatibleClient already builds
            // for every non-OpenAI provider; only the HttpClient differs.
            : new OpenAICompatibleClient(_http, _config).EnhanceAsync(systemPrompt, userText, ct);

    /// <summary>
    /// The Ollama <c>/api/chat</c> request body. <c>think:false</c> turns reasoning off on a
    /// thinking model (Ollama only REQUIRES the thinking capability when think is true, so a
    /// non-thinking model accepts it); <c>num_predict</c> is the output cap; <c>stream:false</c>
    /// returns one JSON object.
    /// </summary>
    internal static string BuildOllamaChatBody(AIProviderConfig config, string systemPrompt, string userText)
        => JsonSerializer.Serialize(new
        {
            model = config.ModelName,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            },
            stream = false,
            think = false,
            keep_alive = OllamaKeepAlive,
            options = new
            {
                temperature = config.Temperature,
                num_ctx = OllamaContextTokens,
                num_predict = config.MaxTokens
            }
        });

    private async Task<string> OllamaChatAsync(string systemPrompt, string userText, CancellationToken ct)
    {
        var endpoint = $"{_config.GetBaseUrl().TrimEnd('/')}/api/chat";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AddAuthorization(request, _config);
        request.Content = new StringContent(BuildOllamaChatBody(_config, systemPrompt, userText), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Local server", Logger, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        return ParseOllamaChat(json, _config.ModelName);
    }

    /// <summary>
    /// Reads an Ollama <c>/api/chat</c> reply. A cleanup cut off at <c>num_predict</c>
    /// (<c>done_reason:"length"</c>) is REFUSED with the shared ENH-27 reason, like every other
    /// provider, so the raw transcript pastes instead of a sentence that stops mid-word — decided
    /// OUTSIDE the guard, because inside it that throw would read as contract drift.
    /// </summary>
    internal static string ParseOllamaChat(string json, string model)
    {
        // Warning, never Error, for every malformed reply, and deliberately NOT through
        // ProviderResponseGuard (which logs Error so contract drift reaches Sentry): a user-run
        // server answering in the wrong shape — an address aimed at a different server, a web
        // page — is the user's configuration, and would otherwise file a Sentry event per dictation.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw NotThisKindOfServer(LocalServerApi.Ollama, "message", json);
        }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("done_reason", out var doneReason)
            && doneReason.ValueKind == JsonValueKind.String
            && doneReason.GetString() == "length")
        {
            Logger.Warning("Enhancement output truncated at num_predict (done_reason=length, model={Model})", model);
            throw new InvalidOperationException(OpenAICompatibleClient.TruncatedReason);
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content)
            && content.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            return content.GetString() ?? "";
        throw NotThisKindOfServer(LocalServerApi.Ollama, "message", json);
    }

    /// <summary>
    /// LAI-8: an Ollama load-only request — <c>/api/generate</c> with no prompt loads the model and
    /// returns. <c>num_ctx</c> MUST equal the chat request's: Ollama reloads a model whose context
    /// changes, so a mismatched preload would make the dictation's own request load it again.
    /// </summary>
    internal static string BuildOllamaLoadBody(AIProviderConfig config)
        => JsonSerializer.Serialize(new
        {
            model = config.ModelName,
            stream = false,
            keep_alive = OllamaKeepAlive,
            options = new { num_ctx = OllamaContextTokens }
        });

    /// <summary>
    /// LAI-8: loads the model while the user is still speaking, then reads <c>/api/ps</c> to learn
    /// whether it runs on the processor. Returns that answer, or null when it cannot tell. Every
    /// failure is the caller's to swallow — a preload never affects the dictation it precedes.
    /// </summary>
    public async Task<bool?> PreloadOllamaModelAsync(CancellationToken ct)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        using (var load = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/generate"))
        {
            AddAuthorization(load, _config);
            load.Content = new StringContent(BuildOllamaLoadBody(_config), Encoding.UTF8, "application/json");
            using var loaded = await _http.SendAsync(load, ct).ConfigureAwait(false);
            if (!loaded.IsSuccessStatusCode)
            {
                Logger.Debug("Local server preload refused: HTTP {Status}", (int)loaded.StatusCode);
                return null;
            }
        }

        using var ps = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/ps");
        AddAuthorization(ps, _config);
        using var running = await _http.SendAsync(ps, ct).ConfigureAwait(false);
        if (!running.IsSuccessStatusCode)
            return null;
        var json = await running.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        return ParseRunsOnCpu(json, _config.ModelName);
    }

    /// <summary>True when an Ollama server answers <c>/api/version</c> at the configured address.</summary>
    public async Task<bool> PingOllamaAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.GetBaseUrl().TrimEnd('/')}/api/version");
        AddAuthorization(request, _config);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Reads Ollama's <c>/api/ps</c>: true when the named model is loaded with nothing in video
    /// memory (<c>size_vram</c> 0), false when any of it is, null when the model is not listed or
    /// the reply has another shape. Matches <c>name</c> or <c>model</c>, case-insensitive.
    /// </summary>
    internal static bool? ParseRunsOnCpu(string json, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("models", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !NamesModel(row, model))
                    continue;
                if (!row.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                    || !row.TryGetProperty("size_vram", out var vram) || vram.ValueKind != JsonValueKind.Number)
                    return null;
                if (!size.TryGetInt64(out var total) || total <= 0 || !vram.TryGetInt64(out var inVram))
                    return null;
                return inVram == 0;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool NamesModel(JsonElement row, string model)
        => HasString(row, "name", model) || HasString(row, "model", model);

    private static bool HasString(JsonElement row, string property, string value)
        => row.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
           && string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's model list: Ollama <c>/api/tags</c>, else <c>/models</c>.</summary>
    public async Task<ProviderModelList> FetchModelsAsync(bool unfiltered, CancellationToken ct)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var url = _config.LocalApi == LocalServerApi.Ollama ? $"{baseUrl}/api/tags" : $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorization(request, _config);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Local server models", Logger, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        var list = ParseModelList(json, _config.LocalApi, unfiltered);
        Logger.Information("Local server models ({Api}): total={Total} shown={Shown}",
            _config.LocalApi, list.RawCount, list.Models.Count);
        return list;
    }

    /// <summary>
    /// Parses a model list. Ollama: <c>models[].name</c>; OpenAI-compatible: <c>data[].id</c>.
    /// Hides embedding models unless <paramref name="unfiltered"/> (Show all models): they appear in
    /// both servers' lists and cannot answer a chat request. That is the ONLY rule — the cloud
    /// catalogs' display policy is not applied to a server the user chose to run. A malformed row
    /// is skipped rather than failing the list; <see cref="ProviderModelList.RawCount"/> is the
    /// array length before filtering.
    /// </summary>
    internal static ProviderModelList ParseModelList(string json, LocalServerApi api, bool unfiltered)
    {
        var (arrayName, idName) = api == LocalServerApi.Ollama ? ("models", "name") : ("data", "id");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw NotThisKindOfServer(api, arrayName, json);
        }

        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty(arrayName, out var rows)
            || rows.ValueKind != JsonValueKind.Array)
            throw NotThisKindOfServer(api, arrayName, json);

        var models = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty(idName, out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
                continue;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!unfiltered && IsEmbeddingModel(id))
                continue;
            models.Add(id);
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        return new ProviderModelList(models, rows.GetArrayLength(), Curated: true);
    }

    /// <summary>
    /// A reply that is not the expected model list almost always means the address points at a
    /// different server — an Ollama preset aimed at LM Studio's port, a web page — which is the
    /// user's configuration, not our contract drifting: Warning (no Sentry event), and a message
    /// that says what to check. The body goes only to the local log, under the redacted
    /// <c>{Body}</c> name.
    /// </summary>
    private static InvalidOperationException NotThisKindOfServer(LocalServerApi api, string arrayName, string json)
    {
        Logger.Warning("Local server reply has no '{Field}' of the expected shape; Body={Body}", arrayName, json.TruncateForLog());
        return new InvalidOperationException(api == LocalServerApi.Ollama
            ? "No Ollama server answered at this address"
            : "No OpenAI-compatible server answered at this address");
    }

    /// <summary>
    /// "embed" anywhere in the id, case-insensitive: nomic-embed-text, mxbai-embed-large,
    /// text-embedding-nomic-embed-text-v1.5 (LM Studio); bge-m3 is a known miss. Display-only —
    /// Show all models still lists it, and a selected embedding model fails loudly with the
    /// server's own error rather than silently.
    /// </summary>
    internal static bool IsEmbeddingModel(string id) => id.Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A key is sent only when one is stored: LM Studio can require one, Ollama never does, and an
    /// empty "Bearer " header is a malformed credential some servers reject outright.
    /// </summary>
    internal static void AddAuthorization(HttpRequestMessage request, AIProviderConfig config)
    {
        if (!string.IsNullOrEmpty(config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }
}
