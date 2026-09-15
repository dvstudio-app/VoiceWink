using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription.Clients;

/// <summary>
/// Deepgram cloud transcription client.
/// </summary>
public sealed class DeepgramClient : ITranscriptionService
{
    private static ILogger Logger => Log.ForContext<DeepgramClient>();
    private const string BaseUrl = "https://api.deepgram.com/v1/listen";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Func<bool>? _keytermsEnabled;

    public bool IsReady => !string.IsNullOrEmpty(_apiKey);

    // Deepgram keyterm/keywords are discriminative biasing — never echoed (PRM-4).
    public HintTransportKind HintTransport => HintTransportKind.Keyterms;

    /// <summary>
    /// <paramref name="keytermsEnabled"/> is the per-request "Send dictionary and
    /// trigger words" read (owner request 2026-07-18 — the Models-page toggle; the
    /// cached client must react to a flip without reconstruction, mirroring
    /// ElevenLabs). Null = enabled, the default.
    /// </summary>
    public DeepgramClient(HttpClient http, string apiKey, string model = "nova-2", Func<bool>? keytermsEnabled = null)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
        _keytermsEnabled = keytermsEnabled;
    }

    /// <summary>
    /// PRM-3 vocabulary budgets. Deepgram's keyterm docs state a HARD 500-token
    /// aggregate limit that ERRORS the whole request when exceeded, without
    /// publishing a tokenizer — so the client enforces a conservative PROXY, not a
    /// guarantee: at most 100 terms AND at most <see cref="KeytermByteBudget"/>
    /// raw UTF-8 bytes (pre-URL-encoding), counting ONE estimated token per byte.
    /// That over-estimates real tokenization by several× for ASCII and ≥1× for
    /// CJK/emoji, keeping the provider cap unreachable. Keywords models have a
    /// documented flat 100-term limit.
    /// </summary>
    internal const int MaxVocabularyTerms = 100;
    internal const int KeytermByteBudget = 450;

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, Models.TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default)
    {
        Logger.Information("Deepgram transcription: {Model}, diarize={Diarize}", _model, diarize);

        var queryParams = $"?model={Uri.EscapeDataString(_model)}&smart_format=true";
        if (!string.IsNullOrEmpty(language) && language != "auto")
            queryParams += $"&language={Uri.EscapeDataString(language)}";
        else
            queryParams += "&detect_language=true";
        if (diarize)
            queryParams += "&diarize=true&utterances=true";
        var keytermsEnabled = _keytermsEnabled?.Invoke() ?? true;
        if (keytermsEnabled)
            queryParams += BuildVocabularyParams(_model, hints);

        // Full request trace (DEBUG-only, toggle-gated): the assembled query string is the
        // entire request Deepgram receives — model, smart_format, language/detect_language,
        // diarize/utterances, and any keyterm/keywords. The API key rides the Authorization
        // header (never the query), so logging the full URL can't leak it. Unconditional so a
        // no-Dictionary request is traced too.
        // "keyterms enabled" = the per-provider TRANSPORT gate (Models page). With the
        // master Dictionary toggle off the payload can be triggers-only, so the old
        // "dictionary enabled" label would have read misleadingly.
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.DeepgramTranscription,
            new Helpers.TraceMeta(Provider: "Deepgram", Model: _model),
            $"deepgram transcription · {_model}",
            ("endpoint", BaseUrl),
            ("query", queryParams),
            ("keyterms enabled", keytermsEnabled ? "yes" : "no"));

        using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var content = new StreamContent(fileStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + queryParams);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Deepgram", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        TraceDetectedLanguage(doc);

        if (diarize)
        {
            var hasUtterances = doc.RootElement.TryGetProperty("results", out var dr)
                && dr.TryGetProperty("utterances", out var du) && du.GetArrayLength() > 0;
            Logger.Debug("Deepgram diarization: hasUtterances={Has}, responseKeys={Keys}",
                hasUtterances, string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name)));
        }

        // When diarization is enabled, use utterances for speaker-labeled output
        if (diarize &&
            doc.RootElement.TryGetProperty("results", out var diarResults) &&
            diarResults.TryGetProperty("utterances", out var utterances) &&
            utterances.GetArrayLength() > 0)
        {
            var lines = new List<string>();
            foreach (var utt in utterances.EnumerateArray())
            {
                var speaker = utt.TryGetProperty("speaker", out var spProp) ? spProp.GetInt32() + 1 : 0; // 0-indexed → 1-indexed
                var text = utt.TryGetProperty("transcript", out var txProp) ? txProp.GetString() ?? "" : "";
                lines.Add($"[Speaker {speaker}]: {text}");
            }
            return string.Join("\n", lines);
        }

        if (doc.RootElement.TryGetProperty("results", out var results) &&
            results.TryGetProperty("channels", out var channels) &&
            channels.GetArrayLength() > 0 &&
            channels[0].TryGetProperty("alternatives", out var alternatives) &&
            alternatives.GetArrayLength() > 0 &&
            alternatives[0].TryGetProperty("transcript", out var transcript))
        {
            return transcript.GetString() ?? "";
        }

        throw new InvalidOperationException("Deepgram API response missing 'results.channels[0].alternatives[0].transcript'");
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct = default) =>
        _http.ValidateAuthorizedGetAsync(
            "https://api.deepgram.com/v1/projects",
            req => req.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey),
            ct);

    /// <summary>
    /// Optional-metadata observability (owner request 2026-07-26): when the request ran
    /// with detect_language (auto mode), Deepgram reports the language it chose in
    /// results.channels[0].detected_language (+ language_confidence). Surface it in the
    /// main log + DEBUG prompt trace — a misdetection (live incident same day: short
    /// English clips decoded as Portuguese/Spanish text) is otherwise invisible.
    /// Absence stays silent (pinned-language responses carry no field — deliberately
    /// NOT ProviderResponseGuard territory, this is optional metadata, not contract),
    /// and the whole read is fail-soft: diagnostics never fail a good transcription.
    /// </summary>
    private void TraceDetectedLanguage(JsonDocument doc)
    {
        try
        {
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != JsonValueKind.Array ||
                channels.GetArrayLength() == 0)
            {
                return;
            }
            var channel = channels[0];
            if (!channel.TryGetProperty("detected_language", out var detected) ||
                detected.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var lang = detected.GetString();
            if (string.IsNullOrEmpty(lang))
                return;
            double? confidenceValue = null;
            if (channel.TryGetProperty("language_confidence", out var conf) &&
                conf.ValueKind == JsonValueKind.Number)
            {
                confidenceValue = conf.GetDouble();
            }
            var confidence = confidenceValue?.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture);
            Logger.Information("Deepgram detected language: {DetectedLanguage} (confidence {LanguageConfidence})",
                lang, confidence ?? "n/a");
            Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.DetectedLanguage,
                new Helpers.TraceMeta(Provider: "Deepgram", Model: _model,
                    Language: lang, Confidence: confidenceValue),
                $"deepgram detected language · {_model}",
                confidence == null ? lang : $"{lang} (confidence {confidence})");
        }
        catch
        {
            // Fail-soft: optional metadata must never break the pipeline.
        }
    }

    /// <summary>
    /// Compose the Dictionary-vocabulary query params for <paramref name="model"/>
    /// (PRM-3): nova-3/flux → repeated plain <c>keyterm</c>; nova-2/nova-1/enhanced/
    /// base → repeated plain <c>keywords</c> (no intensifier). Any other family gets
    /// NOTHING — an unclassified model must never risk a 400 over a hint. Terms are
    /// included in order until either cap would be exceeded, then composition STOPS
    /// (deterministic retained prefix); drops are logged count-only — term values are
    /// Dictionary content and never appear in log templates.
    /// </summary>
    internal static string BuildVocabularyParams(string model, Models.TranscriptionHints? hints)
    {
        if (hints == null || hints.Terms.Count == 0)
            return "";

        // Both families share the byte budget: keyterm because of the hard-erroring
        // token cap, keywords because Dictionary terms have no length limit and an
        // unbounded request URI would repeatedly fail nova-2 transcription outright
        // (Codex diff review — the vocabulary hint must never be able to break the
        // transcription it decorates).
        string paramName;
        if (model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("flux", StringComparison.OrdinalIgnoreCase))
        {
            paramName = "keyterm";
        }
        else if (model.StartsWith("nova-2", StringComparison.OrdinalIgnoreCase) ||
                 model.StartsWith("nova-1", StringComparison.OrdinalIgnoreCase) ||
                 model.StartsWith("enhanced", StringComparison.OrdinalIgnoreCase) ||
                 model.StartsWith("base", StringComparison.OrdinalIgnoreCase))
        {
            paramName = "keywords";
        }
        else
        {
            return "";
        }
        var byteBudget = KeytermByteBudget;

        var builder = new global::System.Text.StringBuilder();
        var included = 0;
        var usedBytes = 0;
        foreach (var term in hints.Terms)
        {
            if (included >= MaxVocabularyTerms)
                break;
            var termBytes = global::System.Text.Encoding.UTF8.GetByteCount(term);
            if (usedBytes + termBytes > byteBudget)
                break;
            builder.Append('&').Append(paramName).Append('=').Append(Uri.EscapeDataString(term));
            included++;
            usedBytes += termBytes;
        }

        if (included < hints.Terms.Count)
        {
            Logger.Information(
                "Deepgram vocabulary: sent {Included} of {Total} Dictionary terms ({Param} budget)",
                included, hints.Terms.Count, paramName);
        }

        return builder.ToString();
    }
}
