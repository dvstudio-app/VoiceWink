using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.Clients;

/// <summary>
/// Image generation client for OpenAI and OpenRouter, returning the image as PNG bytes.
/// OpenAI rides /images/generations (no references) or /images/edits (references);
/// OpenRouter rides its unified Image API (<c>POST {base}/images</c>) for EVERY call since
/// 2026-07-31 — new OpenRouter image models are added exclusively to that API.
/// Supports gpt-image-1 / 1-mini / 1.5 / 2 / 2.5 and compatible APIs. DALL-E 2/3 support was
/// removed once OpenAI scheduled them for deprecation — old persisted DALL-E selections
/// are blocked at <see cref="AIEnhancementService.GenerateImageAsync"/> with a clear error.
///
/// Inputs are the user's three independent choices: aspect ratio, size tier, and quality
/// (detail). On the OpenAI routes the client synthesises the <c>size</c> parameter from
/// (aspect, tier) via <see cref="VoiceWink.Helpers.ImageOptions.SynthesizeGptImage2Size"/> for
/// every model <see cref="VoiceWink.Helpers.ImageOptions.HasFlexibleSize"/> admits (gpt-image-2
/// and the two gpt-image-2.5 ids), while gpt-image-1.x — and any OpenAI-family id the predicate
/// does not know — uses an enumerated preset that ignores the tier entirely; the
/// OpenRouter route sends the normalized <c>resolution</c>/<c>aspect_ratio</c> verbatim.
/// </summary>
public sealed class ImageGenerationClient
{
    private static ILogger Logger => Log.ForContext<ImageGenerationClient>();

    private readonly HttpClient _http;
    private readonly AIProviderConfig _config;

    public ImageGenerationClient(HttpClient http, AIProviderConfig config)
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

    /// <summary>
    /// Generate an image from a text prompt. Returns PNG image bytes.
    /// OpenRouter routes to the documented unified Image API for EVERY call
    /// (<c>POST {base}/images</c>; <c>input_references</c> only when references exist) —
    /// see <see cref="GenerateOpenRouterAsync"/>. OpenAI keeps its two routes: with
    /// <paramref name="references"/> (ENH-6/6f) the edits endpoint
    /// (<c>POST {base}/images/edits</c>, multipart), otherwise the legacy
    /// <c>/images/generations</c> path byte-for-byte (an empty list counts as none).
    /// </summary>
    public async Task<byte[]> GenerateAsync(
        string prompt,
        string? imageAspect = null,
        string? imageSizeTier = null,
        string? imageQuality = null,
        IReadOnlyList<ReferenceImage>? references = null,
        CancellationToken ct = default)
    {
        if (_config.Provider == AIProvider.OpenRouter)
        {
            var openRouterBytes = await GenerateOpenRouterAsync(
                prompt, imageAspect, imageSizeTier, imageQuality, references, ct).ConfigureAwait(false);
            return RejectUnstorableFormat(openRouterBytes);
        }

        if (references is { Count: > 0 })
        {
            var referenceBytes = await GenerateWithReferenceEditsAsync(prompt, imageAspect, imageSizeTier, imageQuality, references, ct).ConfigureAwait(false);
            return RejectUnstorableFormat(referenceBytes);
        }

        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/images/generations";

        var model = _config.ModelName;
        var size = ImageOptions.HasFlexibleSize(model)
            ? ImageOptions.SynthesizeGptImage2Size(imageAspect, imageSizeTier)
            : ImageOptions.SynthesizeGptImage1xSize(imageAspect);

        // gpt-image models: "low", "medium", "high", "auto" (default); the two 2.5 ids also take
        // "xhigh" and "max" (IMG-14). The map answers for any tag it knows — the NORMALIZER upstream
        // is what keeps the two top values off models that reject them.
        var quality = ImageOptions.QualityToGptImageParam(imageQuality);

        // Build request body — optional keys omitted when null. gpt-image models default to b64_json.
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["moderation"] = "low",
        };
        if (size != null) body["size"] = size;
        if (quality != null) body["quality"] = quality;
        var json = JsonSerializer.Serialize(body);
        Logger.Information("Image generation: model={Model}, aspect={Aspect}, tier={Tier}, size={Size}, quality={Quality}, prompt length={Length}",
            model, imageAspect ?? "auto", imageSizeTier ?? "auto", size ?? "auto", quality ?? "auto", prompt.Length);

        // IMG-4 (diff r3): the send + gated extraction live in their OWN async method so
        // the buffered response envelope is UNREACHABLE — not merely disposed — once it
        // returns. A `using var response` here would hoist the envelope into THIS
        // method's state machine, which stays rooted across the fallback awaits; and on
        // .NET 8, Dispose() does not drop HttpContent's buffered MemoryStream backing
        // array — only unreachability frees the ~50 MB. Only the decoded bytes / parsed
        // URL / bounded excerpt escape the helper.
        var gate = _config.BatchContext?.ResponseMaterializationGate;
        var extraction = await SendAndExtractGenerationAsync(endpoint, json, model, size, quality, gate, ct).ConfigureAwait(false);

        if (extraction.Bytes != null)
            return RejectUnstorableFormat(extraction.Bytes);

        // URL fallback. Phase 2 (NOT gated): the request/header wait — another HTTP
        // round-trip must never serialize under the materialization gate. Phase 3
        // (gated again): body streaming + MemoryStream growth + ToArray — that IS
        // materialization (IMG-4 round-6 sequence).
        //
        // ONE linked deadline spans the WHOLE download (headers AND gated body): with
        // ResponseHeadersRead, HttpClient.Timeout stops covering the exchange once the
        // headers arrive, so a server that stalls its body would otherwise hang the
        // call on the caller's token alone — and under IMG-4 hold the batch's shared
        // materialization gate forever, wedging every sibling (diff review r1). Expiry
        // translates to the same TimeoutException shape as the send-side translation.
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
            downloadCts.CancelAfter(_http.Timeout);
        var downloadCt = downloadCts.Token;
        try
        {
            return RejectUnstorableFormat(await ProviderResponseGuard.RunAsync(Logger, "Image", extraction.Excerpt, async () =>
            {
                Logger.Information("Image received as URL, downloading...");
                using var imgResponse = await _http.GetWithImageTimeoutTranslationAsync(
                    extraction.Url!, HttpCompletionOption.ResponseHeadersRead, Logger, $"model={LogValueSanitizer.IdentifierOrShape(model)}, url download", downloadCt).ConfigureAwait(false);
                imgResponse.EnsureSuccessStatusCode();

                // Re-validate after redirects — HttpClient follows 302s by default
                var finalUri = imgResponse.RequestMessage?.RequestUri;
                if (finalUri != null && finalUri.Scheme != "https")
                    throw new InvalidOperationException("Image URL redirected to non-HTTPS endpoint");

                return await ImageBatchCallContext.RunGatedAsync(gate, async () =>
                {
                    // Stream with size enforcement even when Content-Length is missing (chunked transfer)
                    const long maxImageBytes = HttpResponseExtensions.MaxImageResponseBytes;
                    var contentLength = imgResponse.Content.Headers.ContentLength;
                    if (contentLength > maxImageBytes)
                        throw new InvalidOperationException($"Image too large: {contentLength} bytes");

                    using var stream = await imgResponse.Content.ReadAsStreamAsync(downloadCt).ConfigureAwait(false);
                    using var ms = new global::System.IO.MemoryStream(contentLength.HasValue ? (int)contentLength.Value : 4 * 1024 * 1024);
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, downloadCt).ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead > maxImageBytes)
                            throw new InvalidOperationException($"Image download exceeded {maxImageBytes / (1024 * 1024)} MB");
                        ms.Write(buffer, 0, read);
                    }
                    return ms.ToArray();
                }, downloadCt).ConfigureAwait(false);
            }).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && downloadCts.IsCancellationRequested)
        {
            var timeoutSeconds = _http.Timeout.TotalSeconds;
            Logger.Warning(ex, "Image URL download timed out after {Timeout:F0}s (model={Model})",
                timeoutSeconds, model);
            throw new TimeoutException(
                $"Image generation timed out after {timeoutSeconds:F0}s. Try a smaller size or lower quality, or rephrase the prompt.",
                ex);
        }
    }

    /// <summary>
    /// Sends the generation request and runs the gated materialization (body string →
    /// JsonDocument → inline base64 decode OR URL extraction). OWNS the request and the
    /// buffered response: both become unreachable when this method's state machine
    /// completes, so the caller's URL fallback can never retain a capped envelope
    /// across its download (diff r3 — on .NET 8 disposal does not free HttpContent's
    /// buffered backing array; unreachability does). The batch gate serializes the
    /// alloc-heavy phase (≈5× the body transiently); requests stay concurrent; the
    /// JsonDocument disposes inside the gated body, before the gate releases. Null
    /// gate = today's single-image path.
    /// </summary>
    private async Task<(byte[]? Bytes, Uri? Url, string Excerpt)> SendAndExtractGenerationAsync(
        string endpoint, string json, string model, string? size, string? quality,
        SemaphoreSlim? gate, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendWithImageTimeoutTranslationAsync(
            request, Logger, $"model={LogValueSanitizer.IdentifierOrShape(model)}, size={size ?? "auto"}, quality={quality ?? "auto"}", ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Image", Logger, ct).ConfigureAwait(false);

        return await ImageBatchCallContext.RunGatedAsync(gate, async () =>
        {
            // responseJson stays LOCAL to this gated closure (diff r2): only the parsed
            // result plus a bounded log excerpt survive it, so the full UTF-16 body can
            // never be retained across a fallback download.
            var responseJson = await response.Content.ReadAsStringImageLimitedAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);

            // The guard logs Error + body for ANY InvalidOperationException raised during
            // extraction — the deliberate shape-failure throws below AND wrong-kind
            // JsonElement access (e.g. {"data":{}}) — because the outer enhancement catch
            // downgrades that exception type to Warning (provider-outcome semantics) and
            // 200-with-unexpected-shape is API drift that must reach Sentry.
            var parsed = ProviderResponseGuard.Run(Logger, "Image", responseJson, () =>
            {
                if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
                    dataArray.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("Image API returned empty data array");
                }

                var data = dataArray[0];

                // Try b64_json first (inline), then fall back to URL
                if (data.TryGetProperty("b64_json", out var b64) && b64.GetString() is string base64)
                {
                    Logger.Information("Image received as base64 ({Length} chars)", base64.Length);
                    var bytes = Convert.FromBase64String(base64);
                    if (bytes.Length == 0)
                        throw new InvalidOperationException("Image API returned empty image data");
                    return (Bytes: (byte[]?)bytes, Url: (Uri?)null);
                }

                if (data.TryGetProperty("url", out var urlProp) && urlProp.GetString() is string imageUrl)
                {
                    if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme != "https")
                        throw new InvalidOperationException("Image URL must use HTTPS");
                    return (Bytes: null, Url: imageUri);
                }

                throw new InvalidOperationException("Image API response contained neither b64_json nor url");
            });
            return (parsed.Bytes, parsed.Url, Excerpt: responseJson.TruncateForLog());
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ENH-6, OpenAI route: reference-guided generation via the edits endpoint
    /// (<c>POST {base}/images/edits</c>, multipart/form-data). Same size/quality
    /// synthesis as generations; <c>input_fidelity=high</c> only where the model
    /// supports the parameter (<see cref="ImageOptions.SupportsInputFidelity"/> —
    /// gpt-image-2 REJECTS it, it always runs high-fidelity); NO <c>moderation</c>
    /// field (generations-only parameter). GPT models return base64.
    /// ENH-6f: ONE reference keeps the live-verified single shape (field <c>image</c>,
    /// filename <c>reference.&lt;ext&gt;</c>); two or more use OpenAI's documented array
    /// form — one part per reference under the repeated field name <c>image[]</c>,
    /// filenames <c>reference1.&lt;ext&gt;</c>…, selection order preserved.
    /// </summary>
    private async Task<byte[]> GenerateWithReferenceEditsAsync(
        string prompt,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage> references,
        CancellationToken ct)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/images/edits";

        var model = _config.ModelName;
        var size = ImageOptions.HasFlexibleSize(model)
            ? ImageOptions.SynthesizeGptImage2Size(imageAspect, imageSizeTier)
            : ImageOptions.SynthesizeGptImage1xSize(imageAspect);
        var quality = ImageOptions.QualityToGptImageParam(imageQuality);

        Logger.Information(
            "Image edit (reference): model={Model}, size={Size}, quality={Quality}, references={References}, prompt length={Length}",
            model, size ?? "auto", quality ?? "auto", ReferenceLogFormat.Describe(references), prompt.Length);

        var form = new MultipartFormDataContent();
        for (var i = 0; i < references.Count; i++)
        {
            var reference = references[i];
            var imagePart = new ByteArrayContent(reference.Bytes);
            imagePart.Headers.ContentType = new MediaTypeHeaderValue(reference.MimeType);
            // Filename extension matched to the mime — some servers sniff it.
            var ext = reference.MimeType switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png",
            };
            // Single reference keeps the live-verified name/field; multi uses the
            // documented repeated-array field and numbered filenames.
            form.Add(imagePart,
                references.Count == 1 ? "image" : "image[]",
                references.Count == 1 ? $"reference{ext}" : $"reference{i + 1}{ext}");
        }
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(model), "model");
        if (size != null) form.Add(new StringContent(size), "size");
        if (quality != null) form.Add(new StringContent(quality), "quality");
        if (ImageOptions.SupportsInputFidelity(model))
            form.Add(new StringContent("high"), "input_fidelity");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = form;

        using var response = await _http.SendWithImageTimeoutTranslationAsync(
            request, Logger, $"model={LogValueSanitizer.IdentifierOrShape(model)}, edit, size={size ?? "auto"}", ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Image edit", Logger, ct).ConfigureAwait(false);

        // IMG-4: batch materialization (read + parse + decode) serializes via the gate.
        return await ImageBatchCallContext.RunGatedAsync(
            _config.BatchContext?.ResponseMaterializationGate, async () =>
            {
                var responseJson = await response.Content.ReadAsStringImageLimitedAsync(ct).ConfigureAwait(false);
                return ExtractB64Image(responseJson, "Image edit");
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// OpenRouter route (ENH-6; the ONLY OpenRouter route since 2026-07-31): generation via
    /// the documented unified Image API (<c>POST {base}/images</c>), with and without
    /// references — OpenRouter adds new image models exclusively to this API, and the
    /// retired /images/generations shim sent OpenAI-shaped <c>size</c>/<c>quality</c> that
    /// non-OpenAI models could ignore (the option-fidelity gap recorded 2026-07-30).
    /// Field vocabulary maps 1:1 onto ours: <c>resolution</c> = size tier (512/1K/2K/4K),
    /// <c>aspect_ratio</c>, <c>quality</c> (existing gpt-image mapping). OMIT-when-Auto
    /// rule: a null user choice means the field is absent — JSON null is never serialized
    /// (plan review round 2) — and no/empty references mean <c>input_references</c> itself
    /// is absent. No <c>moderation</c> (undocumented on this endpoint) and no URL fallback:
    /// the API documents b64_json responses, so a URL-shaped answer is contract drift and
    /// reaches Sentry through the guard in <see cref="ExtractB64Image"/>.
    /// </summary>
    private async Task<byte[]> GenerateOpenRouterAsync(
        string prompt,
        string? imageAspect,
        string? imageSizeTier,
        string? imageQuality,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/images";
        var model = _config.ModelName;
        var quality = ImageOptions.QualityToGptImageParam(imageQuality);
        var hasReferences = references is { Count: > 0 };

        if (hasReferences)
        {
            Logger.Information(
                "Image generation (reference, OpenRouter): model={Model}, aspect={Aspect}, tier={Tier}, quality={Quality}, references={References}, prompt length={Length}",
                model, imageAspect ?? "auto", imageSizeTier ?? "auto", quality ?? "auto",
                ReferenceLogFormat.Describe(references!), prompt.Length);
        }
        else
        {
            Logger.Information(
                "Image generation (OpenRouter): model={Model}, aspect={Aspect}, tier={Tier}, quality={Quality}, prompt length={Length}",
                model, imageAspect ?? "auto", imageSizeTier ?? "auto", quality ?? "auto", prompt.Length);
        }

        // IMG-7: written as UTF-8 DIRECTLY. The old path built base64 as a UTF-16 string, then the
        // data URL as another, then the whole JSON as a third, then converted to UTF-8 — measured at
        // 15.2x (now 5.0x) the input bytes (tools/reference-payload-memory/). Utf8JsonWriter keeps the
        // structure and the escaping of ordinary strings; only the data-URL token is raw-emitted,
        // which is safe because base64's alphabet and the whitelisted mime types contain nothing
        // JSON escaping exists for. Property ORDER below is deliberately the old dictionary
        // insertion order — the wire must not change.
        //
        // Still off the calling thread (the UI thread for dialog-initiated generations): this
        // reduces the allocation, it does not make it free. The no-reference body is tiny; one build
        // path beats two.
        var content = await Task.Run(() =>
        {
            var buffer = new global::System.Buffers.ArrayBufferWriter<byte>(
                Helpers.ImagePayloadWriter.EstimateOpenRouterCapacity(
                    prompt, model, references, imageSizeTier, imageAspect, quality));
            using (var writer = new Utf8JsonWriter(buffer, Helpers.ImagePayloadWriter.WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteString("prompt", prompt);
                writer.WriteNumber("n", 1);
                if (hasReferences)
                {
                    // input_references is the documented array — one entry per reference,
                    // selection order preserved (ENH-6f).
                    writer.WriteStartArray("input_references");
                    foreach (var r in references!)
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString("type", "image_url");
                        writer.WriteStartObject("image_url");
                        writer.WritePropertyName("url");
                        Helpers.ImagePayloadWriter.WriteDataUrlValue(writer, r.Bytes, r.MimeType);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (!string.IsNullOrEmpty(imageSizeTier)) writer.WriteString("resolution", imageSizeTier);
                if (!string.IsNullOrEmpty(imageAspect)) writer.WriteString("aspect_ratio", imageAspect);
                if (quality != null) writer.WriteString("quality", quality);
                writer.WriteEndObject();
            }
            return Helpers.ImagePayloadWriter.AsJsonContent(buffer);
        }, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Content = content;

        using var response = await _http.SendWithImageTimeoutTranslationAsync(
            request, Logger, $"model={LogValueSanitizer.IdentifierOrShape(model)}, via OpenRouter unified API", ct).ConfigureAwait(false);
        await response.EnsureSuccessOrLogAndThrowAsync("Image", Logger, ct).ConfigureAwait(false);

        // IMG-4: batch materialization (read + parse + decode) serializes via the gate.
        return await ImageBatchCallContext.RunGatedAsync(
            _config.BatchContext?.ResponseMaterializationGate, async () =>
            {
                var responseJson = await response.Content.ReadAsStringImageLimitedAsync(ct).ConfigureAwait(false);
                return ExtractB64Image(responseJson, "Image");
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared base64 extraction for the reference routes (both the OpenAI edits
    /// endpoint and OpenRouter's unified API return <c>data[].b64_json</c>; neither
    /// documents a URL variant for these flows, so no URL fallback here). Guarded
    /// like the main path so contract drift reaches Sentry at Error.
    /// </summary>
    private static byte[] ExtractB64Image(string responseJson, string providerLabel)
    {
        using var doc = JsonDocument.Parse(responseJson);
        return ProviderResponseGuard.Run(Logger, providerLabel, responseJson, () =>
        {
            if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
                dataArray.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"{providerLabel} API returned empty data array");
            }

            if (dataArray[0].TryGetProperty("b64_json", out var b64) && b64.GetString() is string base64)
            {
                Logger.Information("Image received as base64 ({Length} chars)", base64.Length);
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length == 0)
                    throw new InvalidOperationException($"{providerLabel} API returned empty image data");
                return bytes;
            }

            throw new InvalidOperationException($"{providerLabel} API response contained no b64_json");
        });
    }

    /// <summary>
    /// Rejects a payload the pipeline cannot store, BEFORE it is persisted.
    ///
    /// <para>Verified live 2026-07-30: <c>recraft/recraft-v4.1-vector</c> returns HTTP 200 with a
    /// <c>b64_json</c> body that decodes to <c>&lt;svg …</c>. Nothing downstream would notice — this
    /// client never inspected the response's format, and <c>GeneratedImageSaver</c> writes every
    /// payload to a <c>.png</c> filename. The user would be billed for a History entry that looks
    /// successful and contains a file that will not open. A refusal here is the honest outcome, and
    /// it reaches the pill through the normal provider-failure path.</para>
    ///
    /// <para>Catalog curation keeps known-vector models out of DISCOVERY, but discovery is not the
    /// only route to a model id: the combos are editable, prompt overrides bypass them, and a
    /// persisted selection predates any rule. So the check lives at DISPATCH, where every route
    /// converges. Only SVG is rejected — see <see cref="Helpers.ImageBytesFormat"/> for why unknown
    /// bytes deliberately pass.</para>
    ///
    /// <para><b>Called from the PUBLIC boundary, deliberately outside every
    /// <c>ProviderResponseGuard</c> scope</b> (Codex diff review). The guard classifies exceptions as
    /// contract drift and logs them at Error, which reaches Sentry — but a model returning SVG is an
    /// EXPECTED provider outcome, and the repo's policy is Warning for those. Calling it here also
    /// covers all four routes in one place: inline base64, both reference routes, and the URL
    /// fallback, whose downloaded bytes previously bypassed the check entirely.</para>
    /// </summary>
    private static byte[] RejectUnstorableFormat(byte[] bytes)
    {
        if (Helpers.ImageBytesFormat.Sniff(bytes) == Helpers.ImageBytesKind.Svg)
            throw new InvalidOperationException(Helpers.ImageBytesFormat.SvgRejectedMessage);
        return bytes;
    }
}
