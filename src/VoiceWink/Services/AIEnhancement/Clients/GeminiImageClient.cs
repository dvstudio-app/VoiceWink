using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>
/// Gemini-native image generation client. Calls the :generateContent endpoint and returns
/// PNG bytes. Imagen support was removed once Google scheduled Imagen-on-Gemini-API for
/// deprecation — old persisted Imagen selections are blocked at
/// <see cref="AIEnhancementService.GenerateImageAsync"/> with a clear error.
///
/// IMAGE_OTHER is a known intermittent Gemini API issue (catch-all for "model couldn't
/// produce the image"). The user can retry manually via the redo button.
/// </summary>
public sealed class GeminiImageClient
{
    private static ILogger Logger => Log.ForContext<GeminiImageClient>();

    private readonly HttpClient _http;
    private readonly AIProviderConfig _config;

    public GeminiImageClient(HttpClient http, AIProviderConfig config)
    {
        _http = http;
        _config = config;
        // IMG-4: a parallel batch caps buffering at the image-response limit so an
        // oversized (incl. chunked, no Content-Length) response fails DURING buffering —
        // never N concurrent full default-limit buffers. Ctor-set: the factory client is
        // fresh here, so the property is still settable. Null context = byte-identical.
        if (config.BatchContext != null)
            _http.MaxResponseContentBufferSize = HttpResponseExtensions.MaxImageResponseBytes;
    }

    public async Task<byte[]> GenerateAsync(
        string prompt,
        string? imageAspect = null,
        string? imageSizeTier = null,
        string? imageQuality = null,
        IReadOnlyList<ReferenceImage>? references = null,
        CancellationToken ct = default)
    {
        var hasReferences = references is { Count: > 0 };
        var model = _config.ModelName;

        // IMG-2: per-model normalization — DEFENSE-IN-DEPTH (the service already
        // normalizes; direct-client callers get the same guarantee). Gemini VALIDATES
        // imageConfig hard (flash-lite rejects 2K with an API error), so unsupported
        // values clamp/drop here instead of erroring. Subsumes the old legacy-2.x
        // imageSize strip: 2.x has an empty supported-tier set, so any tier drops.
        // Gemini has no separate "quality" knob — its imageSize covers what would be
        // both Tier and Quality on OpenAI; the normalizer nulls imageQuality.
        var normalized = ImageOptions.NormalizeForModel(
            AIProvider.Gemini, model, imageAspect, imageSizeTier, imageQuality);
        foreach (var adjustment in normalized.Adjustments)
            Logger.Information("Image option adjusted for {Model}: {Adjustment}", model, adjustment);
        var aspectRatio = normalized.Aspect;
        var geminiImageSize = normalized.SizeTier;

        Logger.Information("Gemini image generation: model={Model}, aspect={Aspect}, tier={Tier}, references={References}, prompt length={Length}",
            model, aspectRatio ?? "auto", geminiImageSize ?? "auto",
            ReferenceLogFormat.Describe(references), prompt.Length);

        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/models/{model}:generateContent";
        // ENH-6: with reference images the request follows the DOCUMENTED editing
        // shape — responseModalities ["TEXT","IMAGE"] (the extraction below loops
        // parts and picks the first inlineData, so interleaved text parts are
        // harmless). The generation-only ["IMAGE"] path is live-proven — untouched.
        var generationConfig = new Dictionary<string, object>
        {
            ["responseModalities"] = hasReferences ? new[] { "TEXT", "IMAGE" } : new[] { "IMAGE" }
        };
        if (aspectRatio != null || geminiImageSize != null)
        {
            var imageConfig = new Dictionary<string, object>();
            if (aspectRatio != null) imageConfig["aspectRatio"] = aspectRatio;
            if (geminiImageSize != null) imageConfig["imageSize"] = geminiImageSize;
            generationConfig["imageConfig"] = imageConfig;
        }

        // ENH-6/6f: each reference image rides as an inline_data part after the text
        // part, in selection order (mime from the file's real extension — never
        // defaulted). Building the payload copies every reference through base64
        // (+33%), the JSON string, and its UTF-8 bytes — transiently several times the
        // aggregate size at the 50 MB total budget — so with references the build runs
        // on the thread pool, off the calling thread (the UI thread for
        // dialog-initiated generations).
        // IMG-7: written as UTF-8 DIRECTLY (see ImagePayloadWriter). Gemini's inline_data.data is
        // BARE base64 with no prefix, so WriteBase64String writes it straight from the byte span —
        // no intermediate string at all, and no raw-emission question to answer here. Property and
        // array ORDER below reproduce the old anonymous types exactly; the wire must not change.
        HttpContent BuildContent()
        {
            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(
                Helpers.ImagePayloadWriter.EstimateGeminiCapacity(prompt, references));
            using (var writer = new Utf8JsonWriter(buffer, Helpers.ImagePayloadWriter.WriterOptions))
            {
                writer.WriteStartObject();

                writer.WriteStartArray("contents");
                writer.WriteStartObject();
                writer.WriteStartArray("parts");
                writer.WriteStartObject();
                writer.WriteString("text", prompt);
                writer.WriteEndObject();
                if (hasReferences)
                {
                    foreach (var reference in references!)
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteStartObject("inline_data");
                        writer.WriteString("mime_type", reference.MimeType);
                        writer.WriteBase64String("data", reference.Bytes);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WritePropertyName("generationConfig");
                JsonSerializer.Serialize(writer, generationConfig);

                writer.WriteStartArray("safetySettings");
                foreach (var category in new[]
                         {
                             "HARM_CATEGORY_HARASSMENT",
                             "HARM_CATEGORY_HATE_SPEECH",
                             "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                             "HARM_CATEGORY_DANGEROUS_CONTENT",
                             "HARM_CATEGORY_CIVIC_INTEGRITY",
                         })
                {
                    writer.WriteStartObject();
                    writer.WriteString("category", category);
                    writer.WriteString("threshold", "BLOCK_NONE");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            return Helpers.ImagePayloadWriter.AsJsonContent(buffer);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", _config.ApiKey);
        request.Content = hasReferences
            ? await Task.Run(BuildContent, ct).ConfigureAwait(false)
            : BuildContent();

        // Timeout diagnostics describe the NORMALIZED request (what was actually sent),
        // not the caller's pre-clamp values (Codex diff r1).
        using var response = await _http.SendWithImageTimeoutTranslationAsync(
            request, Logger, $"model={LogValueSanitizer.IdentifierOrShape(model)}, aspect={aspectRatio ?? "auto"}, tier={geminiImageSize ?? "auto"}", ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Gemini image", Logger, ct).ConfigureAwait(false);

        // IMG-4: a parallel batch serializes the alloc-heavy materialization phase
        // (body string → JsonDocument → base64 decode ≈ 5× the body transiently) through
        // the batch's shared gate; requests stay concurrent. The JsonDocument disposes
        // inside the gated body, before the gate releases. Null gate = today's path.
        var gate = _config.BatchContext?.ResponseMaterializationGate;
        return await ImageBatchCallContext.RunGatedAsync(gate,
            () => MaterializeResponseAsync(response, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Alloc-heavy response materialization — body string → JsonDocument →
    /// base64 decode, plus the safety/finish-reason verdicts that need the parsed doc.
    /// Split out so the IMG-4 batch gate serializes exactly this section; the
    /// JsonDocument disposes inside it, before the gate releases.</summary>
    private static async Task<byte[]> MaterializeResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var responseJson = await response.Content.ReadAsStringImageLimitedAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);

        // Response: { "candidates": [{ "content": { "parts": [{ "inlineData": { "mimeType": "image/png", "data": "..." } }] } }] }
        // Only the happy-path extraction sits inside the guard: wrong-kind JsonElement
        // access there (e.g. {"candidates":{}}) is API drift that must log Error and
        // reach Sentry, while the safety-block / finish-reason branches BELOW stay
        // outside so their deliberate provider-outcome throws keep Warning semantics.
        var imageBytes = ProviderResponseGuard.Run<byte[]?>(Logger, "Gemini image", responseJson, () =>
        {
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.GetArrayLength() > 0 &&
                candidates[0].TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inlineData) &&
                        inlineData.TryGetProperty("data", out var data) &&
                        data.GetString() is string base64)
                    {
                        Logger.Information("Gemini native image received ({Length} chars base64)", base64.Length);
                        return Convert.FromBase64String(base64);
                    }
                }
            }

            return null;
        });
        if (imageBytes != null)
            return imageBytes;

        // Safety blocks and non-STOP finish reasons are Gemini's verdict on the user's
        // content, not app defects — Warning keeps them out of Sentry events (the
        // sub-logger forwards Error+; VOICEWINK-4/6 were 32 events of IMAGE_SAFETY /
        // NO_IMAGE). Only the unexplained fallthrough below stays Error.

        // Check for prompt-level safety block. Message stays SHORT — it lands on the pill,
        // whose ~55-char truncation must never eat the meaningful part (owner 2026-07-10).
        if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback))
        {
            Logger.Warning("Gemini image generation was blocked by safety filter; Feedback={Feedback}",
                feedback.GetRawText().TruncateForLog());
            throw new InvalidOperationException("Image blocked by safety filter");
        }

        // Extract finishReason + finishMessage for a meaningful error
        if (doc.RootElement.TryGetProperty("candidates", out var cands) &&
            cands.GetArrayLength() > 0 &&
            cands[0].TryGetProperty("finishReason", out var finishReason))
        {
            // ToString(), not GetString(): never throws on a non-string finishReason
            // kind, so drifted shapes still route through this Warning branch instead
            // of escaping as a wrong-kind InvalidOperationException.
            var reason = finishReason.ToString();
            Logger.Warning("Gemini image generation finished with reason: {Reason}; Body={Body}",
                reason, cands[0].GetRawText().TruncateForLog());
            // Reason-first with a minimal prefix: the old 44-char
            // "Image generation failed with finish reason: " prefix left ~11 chars of the
            // pill budget for the ACTUAL reason (e.g. IMAGE_SAFETY), truncating it away.
            throw new InvalidOperationException($"No image: {reason}");
        }

        // 200 with no image AND no safety/finish-reason explanation — a parse gap or an
        // API shape change on our side of the contract, so this one belongs in Sentry.
        Logger.Error("Gemini image API response did not contain expected image data; Body={Body}", responseJson.TruncateForLog());
        throw new InvalidOperationException("Gemini image API response did not contain image data");
    }

}
