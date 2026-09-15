using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription.Clients;

/// <summary>
/// ElevenLabs cloud transcription client.
/// </summary>
public sealed class ElevenLabsClient : ITranscriptionService
{
    private static ILogger Logger => Log.ForContext<ElevenLabsClient>();
    private const string BaseUrl = "https://api.elevenlabs.io/v1/speech-to-text";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Func<bool> _keytermsEnabled;

    /// <summary>
    /// PRM-3 keyterm bounds. The documented per-term constraints are &lt;50 chars,
    /// ≤5 words, and no <c>&lt; &gt; { } [ ] \</c> characters — a term violating any
    /// of them is REJECTED (count-only log), never character-stripped: a mutated
    /// term is corrupted spelling data. The count cap is 100, deliberately far below
    /// the documented 1000, because ABOVE 100 ElevenLabs additionally applies a
    /// 20-second minimum billable duration per request — a hidden cost multiplier
    /// on short dictations (recorded BYOK decision).
    /// </summary>
    internal const int MaxKeyterms = 100;
    internal const int MaxKeytermChars = 49;
    internal const int MaxKeytermWords = 5;
    private static readonly char[] ForbiddenKeytermChars = ['<', '>', '{', '}', '[', ']', '\\'];

    public bool IsReady => !string.IsNullOrEmpty(_apiKey);

    // ElevenLabs keyterms are discriminative biasing — never echoed (PRM-4).
    public HintTransportKind HintTransport => HintTransportKind.Keyterms;

    /// <summary>
    /// <paramref name="keytermsEnabled"/> is read PER REQUEST (never cached) so the
    /// Models-page toggle takes effect without reconstructing the client — the registry
    /// caches clients on (model, key blob) and must not need a cache key for a preference.
    /// The constructor FALLBACK is OFF (a safety default so validation-only constructions,
    /// which pass no Func, never emit paid keyterms); the live app preference is default-ON
    /// (AppDefaults.ElevenLabsKeytermsEnabled), supplied by the registry.
    /// </summary>
    public ElevenLabsClient(HttpClient http, string apiKey, string model = "scribe_v2", Func<bool>? keytermsEnabled = null)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
        _keytermsEnabled = keytermsEnabled ?? (static () => false);
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, Models.TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default)
    {
        Logger.Information("ElevenLabs transcription: {Model}, diarize={Diarize}", _model, diarize);

        using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        content.Add(new StringContent(_model), "model_id");
        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language_code");
        if (diarize)
            content.Add(new StringContent("true"), "diarize");
        // Snapshot the per-request opt-in ONCE so the multipart body and the trace can never
        // disagree, and the delegate is invoked exactly once per request (Codex diff r1).
        var keytermsEnabled = _keytermsEnabled();
        var sentKeyterms = AddKeytermFields(content, hints, keytermsEnabled);

        // Full request trace (DEBUG-only, toggle-gated) — model, language, diarize, and the
        // keyterms actually sent (or none). Unconditional so a no-keyterms request is traced
        // too. The API key rides the xi-api-key header, never the multipart body.
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.ElevenLabsTranscription,
            new Helpers.TraceMeta(Provider: "ElevenLabs", Model: _model),
            $"elevenlabs transcription · {_model}",
            ("model_id", _model),
            ("language_code", !string.IsNullOrEmpty(language) && language != "auto" ? language : "auto (detect)"),
            ("diarize", diarize ? "true" : "false"),
            // Transport gate, not the master Dictionary toggle — see DeepgramClient's note.
            ("keyterms enabled", keytermsEnabled ? "yes" : "no"),
            ("keyterms", sentKeyterms.Count > 0 ? string.Join(", ", sentKeyterms) : null));

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        request.Headers.Add("xi-api-key", _apiKey);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("ElevenLabs", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        TraceDetectedLanguage(doc, language);

        // When diarization is enabled, group consecutive words by speaker_id
        if (diarize &&
            doc.RootElement.TryGetProperty("words", out var words) &&
            words.GetArrayLength() > 0)
        {
            var lines = new List<string>();
            string? currentSpeaker = null;
            var currentWords = new List<string>();

            foreach (var word in words.EnumerateArray())
            {
                if (word.TryGetProperty("type", out var typeProp) && typeProp.GetString() != "word")
                    continue;

                var speakerId = word.TryGetProperty("speaker_id", out var sid) ? sid.GetString() : null;

                if (speakerId != currentSpeaker && currentWords.Count > 0)
                {
                    var label = FormatSpeakerLabel(currentSpeaker);
                    lines.Add($"[{label}]: {string.Join(" ", currentWords)}");
                    currentWords.Clear();
                }

                currentSpeaker = speakerId;
                currentWords.Add(word.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "");
            }

            if (currentWords.Count > 0)
            {
                var label = FormatSpeakerLabel(currentSpeaker);
                lines.Add($"[{label}]: {string.Join(" ", currentWords)}");
            }

            if (lines.Count > 0)
                return string.Join("\n", lines);
        }

        return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
    }

    /// <summary>
    /// Optional-metadata observability (owner request 2026-07-26): scribe reports the
    /// language it recognized in (language_code, language_probability) at the response
    /// root. Logged + DEBUG-traced ONLY in auto mode — with a pinned language_code the
    /// echo is the request's own value, and labeling it "detected" would mislead.
    /// Same fail-soft optional-metadata rules as DeepgramClient.TraceDetectedLanguage.
    /// </summary>
    private void TraceDetectedLanguage(JsonDocument doc, string? requestedLanguage)
    {
        try
        {
            if (!string.IsNullOrEmpty(requestedLanguage) && requestedLanguage != "auto")
                return;
            if (!doc.RootElement.TryGetProperty("language_code", out var detected) ||
                detected.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var lang = detected.GetString();
            if (string.IsNullOrEmpty(lang))
                return;
            double? probabilityValue = null;
            if (doc.RootElement.TryGetProperty("language_probability", out var prob) &&
                prob.ValueKind == JsonValueKind.Number)
            {
                probabilityValue = prob.GetDouble();
            }
            var probability = probabilityValue?.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture);
            Logger.Information("ElevenLabs detected language: {DetectedLanguage} (probability {LanguageProbability})",
                lang, probability ?? "n/a");
            Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.DetectedLanguage,
                new Helpers.TraceMeta(Provider: "ElevenLabs", Model: _model,
                    Language: lang, Confidence: probabilityValue),
                $"elevenlabs detected language · {_model}",
                probability == null ? lang : $"{lang} (probability {probability})");
        }
        catch
        {
            // Fail-soft: optional metadata must never break the pipeline.
        }
    }

    /// <summary>"speaker_1" → "Speaker 1"</summary>
    private static string FormatSpeakerLabel(string? speakerId)
    {
        if (string.IsNullOrEmpty(speakerId)) return "Speaker";
        return speakerId.Replace("speaker_", "Speaker ").Replace("_", " ");
    }

    /// <summary>
    /// PRM-3: append Dictionary terms as repeated <c>keyterms</c> multipart fields
    /// (the OpenAPI multipart array encoding, matching the official SDK's form-data
    /// list) — only when the per-request opt-in gate is ON. Terms are
    /// whitespace-normalized then validated against the documented constraints;
    /// invalid terms are dropped count-only.
    /// </summary>
    /// <summary>Returns the keyterms actually attached (empty when hints are absent or the
    /// opt-in gate is off) so the caller can fold them into the single full-request trace.</summary>
    private IReadOnlyList<string> AddKeytermFields(MultipartFormDataContent content, Models.TranscriptionHints? hints, bool keytermsEnabled)
    {
        var sent = new List<string>();
        if (hints == null || hints.Terms.Count == 0 || !keytermsEnabled)
            return sent;

        var included = 0;
        var dropped = 0;
        foreach (var term in hints.Terms)
        {
            if (included >= MaxKeyterms)
            {
                dropped++;
                continue;
            }
            var normalized = NormalizeKeyterm(term);
            if (normalized == null)
            {
                dropped++;
                continue;
            }
            content.Add(new StringContent(normalized), "keyterms");
            sent.Add(normalized);
            included++;
        }

        if (dropped > 0)
        {
            Logger.Information(
                "ElevenLabs keyterms: sent {Included} of {Total} Dictionary terms (limits)",
                included, included + dropped);
        }

        return sent;
    }

    /// <summary>
    /// Whitespace-normalize (trim + collapse runs), then REJECT (null) rather than
    /// mutate on any documented-constraint violation.
    /// </summary>
    internal static string? NormalizeKeyterm(string term)
    {
        var parts = term.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > MaxKeytermWords)
            return null;
        var normalized = string.Join(' ', parts);
        if (normalized.Length > MaxKeytermChars)
            return null;
        return normalized.IndexOfAny(ForbiddenKeytermChars) >= 0 ? null : normalized;
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct = default) =>
        _http.ValidateAuthorizedGetAsync(
            "https://api.elevenlabs.io/v1/user",
            req => req.Headers.Add("xi-api-key", _apiKey),
            ct);
}
