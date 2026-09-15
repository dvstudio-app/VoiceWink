using System.Buffers;
using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VoiceWink.Helpers;

/// <summary>
/// IMG-7: builds the image-generation request bodies as UTF-8 DIRECTLY, so a reference payload is
/// never materialized as a UTF-16 string.
///
/// <para><b>Why.</b> <c>tools/reference-payload-memory/</c> measured the old path — bytes →
/// <c>Convert.ToBase64String</c> (UTF-16) → data URL (UTF-16) → <c>JsonSerializer.Serialize</c>
/// (UTF-16) → <c>StringContent</c> (UTF-8) — at <b>2,284 MiB</b> peak managed for a 150 MiB
/// reference set, 15.2x (now 5.0x) the input. The owner chose to fix the cause rather than accept the risk
/// or lower the byte budget.</para>
///
/// <para><b>Wire contract: SEMANTIC equivalence, with exactly ONE permitted delta.</b> Base64's
/// <c>+</c> is emitted raw where <c>JsonSerializer</c> escaped it as <c>+</c>. Those are the
/// same JSON string value; <c>+</c> is only special in form/query decoding, and neither client signs
/// or hashes request bytes (Codex plan review). Byte-identity was pursued and abandoned: preserving
/// it requires an escaping pass whose worst-case scratch buffer approaches 400 MiB for a 50 MiB
/// reference — the exact allocation this class exists to remove.</para>
///
/// <para><b>Why raw emission is safe.</b> RFC 4648 base64 uses only <c>A–Z a–z 0–9 + /</c> and
/// <c>=</c>, and the prefix is the fixed ASCII <c>data:&lt;mime&gt;;base64,</c>. None of that
/// contains <c>"</c>, <c>\</c>, or a control character — the only three things JSON string escaping
/// exists for. <see cref="Utf8JsonWriter"/> still owns commas, nesting, property order and the
/// escaping of every ordinary string; this class supplies exactly one token it can prove is safe.
/// </para>
/// </summary>
internal static class ImagePayloadWriter
{
    /// <summary>
    /// Pinned so the writer's escaping can never drift from what <c>JsonSerializer</c> did. The two
    /// agree TODAY only because both default to this encoder — a coincidence, not a guarantee
    /// (Kimi plan review), and one a future options change could silently break.
    /// </summary>
    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>
    /// The mime types permitted in a RAW-emitted data URL. This is a RUNTIME GUARD, not merely a
    /// test (both reviewers): <c>ReferenceImage.MimeType</c> is an unrestricted string on a public
    /// record, so a future caller — or a direct test — could pass <c>"image/svg";drop table</c> and
    /// produce invalid JSON at request time. An unlisted mime raises
    /// <see cref="NotSupportedException"/> rather than emitting a broken body.
    ///
    /// <para><b>This is the set providers ACCEPT, not a mirror of what the picker offers.</b> Those
    /// two diverged deliberately on 2026-08-03: <c>ReferenceImagePolicy</c> also accepts
    /// <c>.avif</c>, but <c>ReferenceImageNormalizer</c> converts AVIF to PNG inside the read gate,
    /// so <c>image/avif</c> can never reach this writer. <b>Do NOT "repair" the apparent mismatch by
    /// adding <c>image/avif</c> here</b> — that would silently permit an un-converted AVIF onto the
    /// wire, which no provider documents accepting, and would remove the only thing forcing the
    /// convert-first invariant.</para>
    /// </summary>
    private static readonly string[] RawEmittableMimeTypes = { "image/png", "image/jpeg", "image/webp" };

    private const string DataUrlPrefix = "data:";
    private const string DataUrlBase64Marker = ";base64,";

    internal static bool IsRawEmittableMime(string? mimeType)
        => mimeType is not null && Array.IndexOf(RawEmittableMimeTypes, mimeType) >= 0;

    /// <summary>
    /// Wraps written UTF-8 as request content. <see cref="ReadOnlyMemoryContent"/> does NOT set a
    /// Content-Type (Kimi plan review), and the old <c>StringContent(json, UTF8, "application/json")</c>
    /// sent <c>application/json; charset=utf-8</c> — so the header is attached explicitly here, or
    /// every client would start sending requests with no declared media type.
    ///
    /// <para>No owner handle is returned: <see cref="ReadOnlyMemoryContent"/> stores the
    /// <see cref="ReadOnlyMemory{T}"/>, which strongly references its backing array, so the
    /// <see cref="ArrayBufferWriter{T}"/> need not stay alive. An earlier revision returned an owner
    /// tuple for this; Codex refuted it from the runtime source.</para>
    /// </summary>
    internal static HttpContent AsJsonContent(ArrayBufferWriter<byte> buffer)
    {
        var content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>
    /// Pre-sizes the output buffer so <see cref="ArrayBufferWriter{T}"/> never doubles a large LOH
    /// array mid-write (Kimi plan review) — a 200 MiB buffer growing would transiently hold the old
    /// and new arrays together, which is the allocation this class exists to avoid.
    ///
    /// <para>Deliberately GENEROUS: overshooting wastes address space once, undershooting by a
    /// single byte triggers exactly the doubling being prevented (Codex). Free-form strings are
    /// costed at their worst-case escaped length (6 UTF-8 bytes per char, the <c>\uXXXX</c> form).
    /// </para>
    /// </summary>
    internal static int EstimateOpenRouterCapacity(
        string prompt, string model, IReadOnlyList<Services.AIEnhancement.ReferenceImage>? references,
        string? tier = null, string? aspect = null, string? quality = null)
    {
        // CHECKED, and every emitted string costed at worst-case escaping (Codex diff review): the
        // helper's contract does not restrict its inputs, so option strings must be counted too --
        // production normalizes them, but the estimate must not DEPEND on that. 6 bytes per UTF-16
        // code unit is the \uXXXX form, which also covers non-BMP surrogate pairs correctly.
        // Overflow would otherwise reach ArrayBufferWriter as a negative capacity and throw with a
        // confusing message; checked makes it an honest one.
        checked
        {
            var total = (prompt.Length + model.Length) * 6 + 512;
            if (references is not null)
            {
                foreach (var r in references)
                {
                    total += Base64.GetMaxEncodedToUtf8Length(r.Bytes.Length)
                        + DataUrlPrefix.Length + r.MimeType.Length * 6 + DataUrlBase64Marker.Length
                        + 128; // per-entry JSON scaffolding
                }
            }
            // resolution / aspect_ratio / quality costed from their ACTUAL lengths (Codex round 2):
            // fixed slack silently assumed the caller normalizes them, and a direct client caller
            // can pass an unbounded aspect string -- triggering exactly the LOH growth this estimate
            // exists to prevent.
            total += (tier?.Length ?? 0) * 6 + (aspect?.Length ?? 0) * 6 + (quality?.Length ?? 0) * 6 + 128;
            return total;
        }
    }

    /// <summary>Gemini's sibling of <see cref="EstimateOpenRouterCapacity"/> — bare base64, no prefix.</summary>
    internal static int EstimateGeminiCapacity(
        string prompt, IReadOnlyList<Services.AIEnhancement.ReferenceImage>? references)
    {
        // safetySettings is five fixed objects; generationConfig is small. 2 KiB covers both with
        // room. Mime charged at worst-case escaping for the same reason as the OpenRouter estimate.
        checked
        {
            var total = prompt.Length * 6 + 2048;
            if (references is not null)
            {
                foreach (var r in references)
                    total += Base64.GetMaxEncodedToUtf8Length(r.Bytes.Length) + r.MimeType.Length * 6 + 128;
            }
            return total;
        }
    }

    /// <summary>
    /// Writes <c>"data:&lt;mime&gt;;base64,&lt;base64&gt;"</c> — quotes included — as a raw JSON
    /// token. The token is assembled in a POOLED buffer and returned on every path.
    /// </summary>
    internal static void WriteDataUrlValue(
        Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, string mimeType)
    {
        if (!IsRawEmittableMime(mimeType))
            throw new NotSupportedException(
                $"Refusing to raw-emit an unrecognised reference mime type. Only {string.Join(", ", RawEmittableMimeTypes)} " +
                "are known to contain no character requiring JSON escaping.");

        var prefixLength = DataUrlPrefix.Length + mimeType.Length + DataUrlBase64Marker.Length;
        var base64Length = Base64.GetMaxEncodedToUtf8Length(bytes.Length);
        // +2 for the surrounding quotes — WriteRawValue receives a COMPLETE token.
        var rented = ArrayPool<byte>.Shared.Rent(prefixLength + base64Length + 2);
        try
        {
            var written = 0;
            rented[written++] = (byte)'"';
            written += Encoding.ASCII.GetBytes(DataUrlPrefix, rented.AsSpan(written));
            written += Encoding.ASCII.GetBytes(mimeType, rented.AsSpan(written));
            written += Encoding.ASCII.GetBytes(DataUrlBase64Marker, rented.AsSpan(written));

            var status = Base64.EncodeToUtf8(bytes, rented.AsSpan(written), out _, out var encoded);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException($"Base64 encoding did not complete ({status}).");
            written += encoded;

            rented[written++] = (byte)'"';
            writer.WriteRawValue(rented.AsSpan(0, written), skipInputValidation: false);
        }
        finally
        {
            // CLEARED before return (Codex diff review). The rented buffer holds base64 of a user's
            // reference PHOTO, and ArrayPool is process-wide -- an uncleared return leaves that image
            // readable by the next unrelated renter for the life of the process. Not an external
            // exposure, but this app handles personal photographs and the clear costs one memset on
            // a path that just avoided gigabytes of copying.
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
