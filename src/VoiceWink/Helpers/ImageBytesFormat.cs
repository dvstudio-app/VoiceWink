namespace VoiceWink.Helpers;

/// <summary>What a generated-image payload actually is, by magic bytes.</summary>
internal enum ImageBytesKind
{
    /// <summary>Nothing recognised. Treated as raster and allowed through — fail-open.</summary>
    Unknown,
    Png,
    Jpeg,
    WebP,
    /// <summary>SVG text. Positively rejected at receipt — see the type doc.</summary>
    Svg,
    /// <summary>Raster, storable, decodable. NOT accepted as a reference by any provider.</summary>
    Gif,
    /// <summary>Raster, storable, decodable. NOT accepted as a reference by any provider.</summary>
    Bmp,
    /// <summary>
    /// AVIF still image (<c>ftyp</c> brand <c>avif</c>). Storable; usable as a reference only
    /// because the read gate converts it to PNG. An AVIF image SEQUENCE (<c>avis</c>) is deliberately
    /// NOT this kind — it is an animation, and treating it as a still would silently drop every frame
    /// but one.
    /// </summary>
    Avif,
    /// <summary>
    /// HEIF family (<c>heic</c>/<c>heix</c>/<c>hevc</c>/<c>mif1</c>/<c>msf1</c>) and AVIF sequences.
    /// Storable; never usable as a reference — only Gemini documents acceptance and the read gate is
    /// provider-blind, so it cannot be forwarded selectively.
    /// </summary>
    Heif,
}

/// <summary>
/// Sniffs a generated-image payload so the pipeline never persists something it cannot store.
///
/// <para>Born from a verified live failure (2026-07-30): <c>recraft/recraft-v4.1-vector</c> returns
/// HTTP 200 with a <c>b64_json</c> payload that decodes to <c>&lt;svg …</c>. Nothing downstream
/// noticed — <c>ImageGenerationClient</c> read <c>b64_json</c> without inspecting the response's
/// <c>media_type</c>, and <c>GeneratedImageSaver</c> WROTE every payload to a <c>.png</c> filename
/// (true until 2026-08-03; the saver now names files from <see cref="PersistedExtension"/>).
/// The result would be a corrupt History entry reported as a SUCCESS, which is worse than a failure:
/// the user is billed, the row looks fine, and the file will not open.</para>
///
/// <para><b>Deliberately asymmetric.</b> Only SVG is rejected, because SVG is the only format with
/// live evidence. Unknown bytes pass — a provider returning a raster format this sniffer has not
/// seen must not be blocked on a guess (the same evidence bar as
/// <see cref="ImageModelGate"/>'s verified-text-only set). JPEG and WebP pass: they are raster and
/// the app's decode paths handle them — and since 2026-08-03 they are also SAVED under their real
/// extension via <see cref="PersistedExtension"/>, so the file no longer claims to be a PNG.</para>
///
/// Pure. Pinned by <c>ImageBytesFormatTests</c>.
/// </summary>
internal static class ImageBytesFormat
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>How far into the payload to look for an SVG/XML opening token.</summary>
    private const int SvgProbeBytes = 256;

    /// <summary>
    /// Hard ceiling on the <c>ftyp</c> brand scan, applied even when the box declares more. Real
    /// boxes carry a handful of brands; the cap is what stops a malformed size on a large provider
    /// response from turning classification into a walk of the whole payload.
    /// </summary>
    private const int MaxFtypScanBytes = 4096;

    internal static ImageBytesKind Sniff(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
            return ImageBytesKind.Unknown;

        if (StartsWith(bytes, PngMagic))
            return ImageBytesKind.Png;

        // JPEG: SOI + first marker byte.
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ImageBytesKind.Jpeg;

        // WebP: "RIFF" .... "WEBP" — the size field sits between the two tags.
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return ImageBytesKind.WebP;
        }

        // GIF: "GIF87a" / "GIF89a".
        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
        {
            return ImageBytesKind.Gif;
        }

        // BMP: "BM". Two bytes is a weak signature, so also require the header's declared file size
        // to match the payload — a two-letter prefix alone would misclassify arbitrary text.
        if (bytes.Length >= 6 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            var declared = (uint)(bytes[2] | (bytes[3] << 8) | (bytes[4] << 16) | (bytes[5] << 24));
            if (declared == (uint)bytes.Length) return ImageBytesKind.Bmp;
        }

        var isoBmff = SniffIsoBmff(bytes);
        if (isoBmff != ImageBytesKind.Unknown) return isoBmff;

        return LooksLikeSvg(bytes) ? ImageBytesKind.Svg : ImageBytesKind.Unknown;
    }

    /// <summary>
    /// ISO base media file format (<c>ftyp</c>) brands — AVIF and the HEIF family share this
    /// container with MP4, so the BRAND decides, never the box alone.
    ///
    /// <para><b>Box layout:</b> <c>[0..3]</c> size, <c>[4..7]</c> "ftyp", <c>[8..11]</c> major brand,
    /// <c>[12..15]</c> <b>minor_version</b> (a NUMBER, not a brand — reading it as one is a real
    /// misclassification risk), then zero or more compatible brands from <c>[16]</c>.</para>
    ///
    /// <para><b>Brands are collected, then resolved by precedence</b>, because a single file
    /// legitimately carries several: AVIF files routinely list <c>mif1</c> among their compatible
    /// brands, so first-match-wins would classify a perfectly ordinary AVIF as HEIF. Order:
    /// sequence beats still, still beats the generic HEIF family.</para>
    ///
    /// <para><b>Scanning is hard-capped</b> at <see cref="MaxFtypScanBytes"/> regardless of what the
    /// box claims. A malformed size on a 50 MB provider response would otherwise walk the whole
    /// payload four bytes at a time. Brands are compared as bytes for the same reason — no string
    /// is allocated per candidate.</para>
    /// </summary>
    private static ImageBytesKind SniffIsoBmff(byte[] bytes)
    {
        // Need at least size + "ftyp" + major brand + minor_version to be a well-formed box.
        if (bytes.Length < 16) return ImageBytesKind.Unknown;
        if (bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p')
            return ImageBytesKind.Unknown;

        // Unsigned big-endian: a size with the high bit set must not read as negative.
        var boxSize = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        // The declared size BOUNDS the box, so brands outside it are not this box's brands and must
        // not decide the format. Sizes 0 ("extends to end of file") and 1 ("64-bit size follows")
        // are legal ISO-BMFF but carry no usable in-box bound here, so they take the ceiling; a size
        // below the 16-byte minimum is structurally impossible and the box is refused outright.
        // An earlier version mapped EVERY invalid size to the ceiling, which let an `avif` token
        // sitting outside a small declared box pick the format (Codex diff review r2).
        int limit;
        if (boxSize is 0 or 1)
            limit = Math.Min(bytes.Length, MaxFtypScanBytes);
        else if (boxSize < 16)
            return ImageBytesKind.Unknown;
        else
            limit = Math.Min(bytes.Length, (int)Math.Min(boxSize, MaxFtypScanBytes));

        var sawAvifStill = false;
        var sawSequence = false;
        var sawHeifFamily = false;

        // Major brand at 8; compatible brands from 16 — 12..15 is minor_version and is SKIPPED.
        Classify(bytes, 8);
        for (var offset = 16; offset + 4 <= limit; offset += 4)
            Classify(bytes, offset);

        // Sequence first: an AVIF that is also an image sequence is an animation, and treating it as
        // a still would silently drop every frame but one.
        if (sawSequence) return ImageBytesKind.Heif;
        if (sawAvifStill) return ImageBytesKind.Avif;
        if (sawHeifFamily) return ImageBytesKind.Heif;

        void Classify(byte[] b, int at)
        {
            if (Is(b, at, "avif")) sawAvifStill = true;
            else if (Is(b, at, "avis")) sawSequence = true;
            else if (Is(b, at, "heic") || Is(b, at, "heix") || Is(b, at, "hevc")
                  || Is(b, at, "mif1") || Is(b, at, "msf1")) sawHeifFamily = true;
        }

        static bool Is(byte[] b, int at, string brand)
            => b[at] == brand[0] && b[at + 1] == brand[1] && b[at + 2] == brand[2] && b[at + 3] == brand[3];

        return ImageBytesKind.Unknown;
    }

    private static bool StartsWith(byte[] bytes, byte[] magic)
    {
        if (bytes.Length < magic.Length) return false;
        for (var i = 0; i < magic.Length; i++)
            if (bytes[i] != magic[i]) return false;
        return true;
    }

    /// <summary>
    /// SVG has no single magic number: a document may open with an XML declaration, a comment, a
    /// DOCTYPE, or the <c>&lt;svg</c> tag itself, optionally behind a BOM and whitespace. So scan a
    /// bounded prefix for an <c>&lt;svg</c> token, and accept a leading XML declaration only when the
    /// same window also contains <c>&lt;svg</c> — an arbitrary XML payload is not our business, and
    /// a raster file will not contain either token this early.
    /// </summary>
    private static bool LooksLikeSvg(byte[] bytes)
    {
        var start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3; // UTF-8 BOM

        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;

        if (start >= bytes.Length || bytes[start] != (byte)'<')
            return false;

        var window = global::System.Text.Encoding.ASCII.GetString(
            bytes, start, Math.Min(SvgProbeBytes, bytes.Length - start));
        return window.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// User-facing copy for the one rejected kind. App-authored, ≤55 code units.
    /// </summary>
    internal const string SvgRejectedMessage = "Model returned SVG — not a supported image";

    /// <summary>
    /// The extension a payload of this kind is PERSISTED under — the single source of truth shared by
    /// <c>GeneratedImageSaver</c> (what it writes) and <c>TranscriptionCleanupService</c> (what the
    /// orphan sweep looks for).
    ///
    /// <para><b>Why one table and not two lists plus a test:</b> a test comparing two independently
    /// maintained lists pins nothing — it passes right up until someone edits one side and the test
    /// with it. Sharing the table makes the agreement structural, and a sweep that misses an
    /// extension leaks those files forever, silently.</para>
    ///
    /// <para><see cref="ImageBytesKind.Svg"/> has no entry: it is rejected at receipt and never
    /// reaches disk. <see cref="ImageBytesKind.Unknown"/> gets a neutral <c>.img</c> — the payload is
    /// still SAVED (the user paid for those bytes), but naming it <c>.png</c> would be a lie that
    /// breaks a double-click anyway, with a more confusing error than no association at all.</para>
    /// </summary>
    internal static string PersistedExtension(ImageBytesKind kind) => kind switch
    {
        ImageBytesKind.Png => ".png",
        ImageBytesKind.Jpeg => ".jpg",
        ImageBytesKind.WebP => ".webp",
        ImageBytesKind.Gif => ".gif",
        ImageBytesKind.Bmp => ".bmp",
        ImageBytesKind.Avif => ".avif",
        ImageBytesKind.Heif => ".heif",
        ImageBytesKind.Svg => throw new global::System.InvalidOperationException(
            "SVG is rejected at receipt and must never be persisted."),
        _ => ".img",
    };

    /// <summary>
    /// Every extension the saver can emit. The orphan sweep enumerates exactly this set, so the two
    /// cannot drift.
    /// </summary>
    internal static global::System.Collections.Generic.IReadOnlyList<string> PersistedExtensions { get; } = new[]
    {
        ".png", ".jpg", ".webp", ".gif", ".bmp", ".avif", ".heif", ".img",
    };
}
