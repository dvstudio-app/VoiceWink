using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription.Clients;

/// <summary>
/// OpenAI audio transcription client — uses the /v1/audio/transcriptions endpoint.
/// Supports gpt-transcribe, gpt-4o-transcribe and gpt-4o-mini-transcribe.
///
/// <para>Two request shapes live behind one endpoint. <c>gpt-transcribe</c> (2026-07-31) replaced
/// the singular <c>language</c> field with a <c>languages[]</c> array and added a structured
/// <c>keywords[]</c> biasing field; every earlier model keeps the singular field and gets no
/// keywords. <see cref="Helpers.OpenAITranscriptionParameters"/> owns that split — the wrong
/// choice is silent, not loud: the request still returns 200, having ignored the language the
/// user pinned.</para>
/// </summary>
public sealed class OpenAITranscriptionClient : ITranscriptionService
{
    private static ILogger Logger => Log.ForContext<OpenAITranscriptionClient>();
    private const string BaseUrl = "https://api.openai.com/v1/audio/transcriptions";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public bool IsReady => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Model-scoped, not provider-scoped: only <c>gpt-transcribe</c> takes <c>keywords[]</c>.
    /// The legacy models stay <see cref="HintTransportKind.None"/> — <c>gpt-4o-transcribe</c> is
    /// the model whose prompt echo on near-silent audio this project actually observed, and
    /// <c>keywords</c> is not documented for it.
    /// </summary>
    public HintTransportKind HintTransport =>
        Helpers.OpenAITranscriptionParameters.SupportsKeywords(_model)
            ? HintTransportKind.StructuredKeywords
            : HintTransportKind.None;

    /// <summary>Stays null: <c>keywords[]</c> is a discrete field with its own budget, so nothing
    /// here is sized with the Whisper tokenizer. Stamping a Whisper vocabulary for this client
    /// would be meaningless, not merely unused.</summary>
    public string? GenerativeModelId => null;

    public OpenAITranscriptionClient(HttpClient http, string apiKey, string model = "gpt-4o-mini-transcribe")
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, Models.TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default)
    {
        Logger.Information("OpenAI transcription: {Model}", _model);

        using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        content.Add(new StringContent(_model), "model");
        content.Add(new StringContent("json"), "response_format");

        // ONE language field or the other, never both — OpenAI documents that sending both is
        // wrong, and the failure is silent (200 with the pin ignored).
        var usesLanguagesArray = Helpers.OpenAITranscriptionParameters.UsesLanguagesArray(_model);
        var languageField = usesLanguagesArray ? "languages[]" : "language";
        var hasLanguage = !string.IsNullOrEmpty(language) && language != "auto";
        if (hasLanguage)
            content.Add(new StringContent(language!), languageField);

        // The 'prompt' parameter stays deliberately unsent for EVERY OpenAI model. The API
        // documents it, but we OBSERVED gpt-4o-transcribe echoing the prompt back on
        // empty/near-silent audio, and VoiceWink has no natural "context about this recording"
        // to put there anyway. Vocabulary now travels as `keywords[]` instead — a discrete
        // field, on gpt-transcribe only.
        //
        // The projection is HANDED to us, never computed here: the pipeline built it once,
        // before the NET-1 connect retry, and the echo gate matches against that same object.
        // Deciding anything here would let a retry send what the gate is not watching for — and
        // would let the file-transcription path (no echo gate) send keywords at all.
        var keywords = hints?.KeywordProjection;
        IReadOnlyList<string> sentKeywords = usesLanguagesArray && keywords is { HasTerms: true }
            ? keywords.IncludedTerms
            : global::System.Array.Empty<string>();
        foreach (var keyword in sentKeywords)
            content.Add(new StringContent(keyword), "keywords[]");

        // Serilog diagnostics: COUNTS ONLY. Term values are user vocabulary and this sink is
        // Sentry-reachable. (The opt-in prompt trace below is the wire-exact record — a
        // different sink with a different contract; its redacted sidecar masks the values.)
        if (keywords is not null && (keywords.RejectedCount > 0 || keywords.DroppedCount > 0))
        {
            Logger.Information(
                "OpenAI keywords: {SentCount} sent, {RejectedCount} rejected (forbidden character), {DroppedCount} dropped (budget)",
                sentKeywords.Count, keywords.RejectedCount, keywords.DroppedCount);
        }

        // Full request trace (opt-in gated, all builds since REL-17) — unconditional, and
        // WIRE-EXACT by design: field labels match the multipart names actually sent, so a
        // reader can tell `language` from `languages[]` at a glance. Keyterm VALUES ride this
        // raw trace exactly as they do for Deepgram/ElevenLabs; the redacted sidecar is what
        // masks them. The API key rides the Authorization header, never the multipart body.
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.OpenAITranscription,
            new Helpers.TraceMeta(Provider: "OpenAI", Model: _model),
            $"openai transcription · {_model}",
            ("model", _model),
            ("response_format", "json"),
            (languageField, hasLanguage ? language : "auto (detect)"),
            // Two entries on purpose: the raw trace gets the verbatim terms (its whole job is
            // the exact payload, as it already is for Deepgram/ElevenLabs), while the COUNT is
            // separately labelled so the redacted sidecar — which masks the values — can still
            // answer "did keywords go out, and how many?" for a support bundle.
            ("keywords[]", sentKeywords.Count > 0 ? string.Join(", ", sentKeywords) : "(none sent)"),
            ("keywords sent", sentKeywords.Count.ToString(global::System.Globalization.CultureInfo.InvariantCulture)),
            ("prompt", "(not sent — OpenAI transcribe echoes prompts on near-silent audio)"));

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("OpenAI", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct = default) =>
        _http.ValidateAuthorizedGetAsync(
            "https://api.openai.com/v1/models",
            req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey),
            ct);
}
