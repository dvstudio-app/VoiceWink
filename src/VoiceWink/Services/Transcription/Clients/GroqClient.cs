using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription.Clients;

/// <summary>
/// Groq cloud transcription client.
/// Uses Groq's Whisper API (OpenAI-compatible endpoint).
/// </summary>
public sealed class GroqClient : ITranscriptionService
{
    private static ILogger Logger => Log.ForContext<GroqClient>();
    private const string BaseUrl = "https://api.groq.com/openai/v1/audio/transcriptions";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public bool IsReady => !string.IsNullOrEmpty(_apiKey);

    // TRN-11 (2026-08-11): NO hints. Groq's Whisper endpoint does take a generative
    // `prompt` field, and this client used to fill it with the composed hint terms —
    // measured as destroying the transcript, not merely polluting it. Same WAV, same
    // model, 3 runs each: with the shipped 57-term prompt every run lost the content
    // (10 chars / 442 chars of hallucination / 2 chars); with no prompt all three
    // returned the same correct 124-character sentence. Decomposed, the 49 Dictionary
    // words carry it — 4/4 runs contaminated, 1 catastrophic — while the 8 trigger
    // phrases alone were substantively correct 4/4 but still added invented text in 2
    // of them (a fabricated trailing clause, and a leading discourse marker), which
    // reads as something the user said. So neither half rides the wire. See
    // ITranscriptionService's GenerativePrompt remarks for why nothing declares that
    // transport any more. Evidence is described rather than quoted throughout: the
    // measurement ran on the owner's own dictation against their private Dictionary,
    // and this repository goes public.
    public HintTransportKind HintTransport => HintTransportKind.None;

    public GroqClient(HttpClient http, string apiKey, string model = "whisper-large-v3-turbo")
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, Models.TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default)
    {
        Logger.Information("Groq transcription: {Model}", _model);

        using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        content.Add(new StringContent(_model), "model");
        content.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");
        // NO `prompt` field — see HintTransport. `hints` is accepted and ignored, which is
        // the contract for HintTransportKind.None (Parakeet does the same); the parameter
        // stays because ITranscriptionService defines it for every transport.

        // Full request trace (opt-in gated, all builds since REL-17) — every field sent.
        // The API key rides the Authorization header, never the multipart body. The
        // `prompt` row is retained and reports the literal "(not sent)" rather than being
        // dropped: an existing support bundle can then DISTINGUISH a post-TRN-11 build
        // from a pre-TRN-11 one on the evidence, instead of a missing row meaning either
        // "not sent" or "this line was never traced".
        Helpers.PromptTraceLog.Write(Helpers.PromptTraceOp.GroqTranscription,
            new Helpers.TraceMeta(Provider: "Groq", Model: _model),
            $"groq transcription · {_model}",
            ("model", _model),
            ("response_format", "json"),
            ("language", !string.IsNullOrEmpty(language) && language != "auto" ? language : "auto (detect)"),
            ("prompt", "(not sent)"));

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Groq", Logger, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct = default) =>
        _http.ValidateAuthorizedGetAsync(
            "https://api.groq.com/openai/v1/models",
            req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey),
            ct);
}
