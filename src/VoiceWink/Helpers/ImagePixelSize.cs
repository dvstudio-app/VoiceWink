namespace VoiceWink.Helpers;

/// <summary>
/// An image's size in pixels, the way a viewer shows it — see <see cref="ImagePixelSize"/>.
/// Public only because <see cref="ImageOptions.NormalizeForModel"/> takes one.
/// </summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// IMG-21: the pixel size of a PNG, JPEG or WebP read from its header — no decode, no WIC.
///
/// <para>Two callers. <c>AIEnhancementService</c> reads the FIRST reference image's size so
/// <see cref="ImageOptions.NormalizeForModel"/> can send that shape as the aspect when the user chose
/// a size and left Aspect on Auto (owner report 2026-10-11: OpenRouter sized a 4K request with a
/// 1776×2368 reference from the reference — 1344×1792 — and dropped the tier). And
/// <c>ClassifyGeneratedImage</c> logs each returned image's size beside the tier that was sent, so
/// a provider ignoring the size shows in the log.</para>
///
/// <para><b>The size is the DISPLAYED one:</b> a JPEG whose EXIF orientation is 5–8 (rotated a
/// quarter turn) reports its stored width and height swapped — a phone portrait stored landscape
/// is a portrait to the user, in the reference strip and in Explorer, and that is the shape they
/// meant. Whether a provider applies EXIF itself is not known; the user's view is the one we can
/// name. Only JPEG's EXIF is read: PNG <c>eXIf</c> and WebP <c>EXIF</c> chunks are rare and ignored.
/// A JPEG reference that the read gate re-encoded (MPO) already has its rotation baked in.</para>
///
/// <para>Pure and fail-soft: anything it cannot vouch for — another format, a truncated header, a
/// zero edge, a JPEG with no frame header before its scan — is null, never a guess. Malformed EXIF
/// reads as "no rotation" rather than failing the size.</para>
/// </summary>
internal static class ImagePixelSize
{
    internal static PixelSize? TryRead(byte[]? bytes)
        => bytes is null ? null : TryRead(bytes, ImageBytesFormat.Sniff(bytes));

    /// <summary>The same read when the caller has already sniffed the bytes.</summary>
    internal static PixelSize? TryRead(byte[] bytes, ImageBytesKind kind) => kind switch
    {
        ImageBytesKind.Png => ReadPng(bytes),
        ImageBytesKind.Jpeg => ReadJpeg(bytes),
        ImageBytesKind.WebP => ReadWebP(bytes),
        _ => null,
    };

    // Signature (8) + IHDR length (4) + "IHDR" (4) + width (4, BE) + height (4, BE). IHDR is
    // required to be the first chunk.
    private static PixelSize? ReadPng(byte[] b)
    {
        if (b.Length < 24) return null;
        if (b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R') return null;
        return Size(ReadUInt32(b, 16, littleEndian: false), ReadUInt32(b, 20, littleEndian: false));
    }

    // "RIFF" size "WEBP", then the first chunk's FourCC at 12 and its data at 20.
    private static PixelSize? ReadWebP(byte[] b)
    {
        if (b.Length < 25) return null;
        if (b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8')
        {
            switch (b[15])
            {
                case (byte)' ': // lossy: 3-byte frame tag, start code 9D 01 2A, then 14-bit edges
                    if (b.Length < 30 || b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return null;
                    return Size((uint)((b[26] | (b[27] << 8)) & 0x3FFF), (uint)((b[28] | (b[29] << 8)) & 0x3FFF));
                case (byte)'L': // lossless: signature 0x2F, then edges-minus-one packed 14 + 14 bits
                    if (b[20] != 0x2F) return null;
                    var bits = ReadUInt32(b, 21, littleEndian: true);
                    return Size((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                case (byte)'X': // extended: flags (1) + reserved (3), then 24-bit canvas edges minus one
                    if (b.Length < 30) return null;
                    return Size(ReadUInt24(b, 24) + 1, ReadUInt24(b, 27) + 1);
            }
        }
        return null;
    }

    private static PixelSize? ReadJpeg(byte[] b)
    {
        PixelSize? stored = null;
        var sawExif = false;
        var orientation = 1;
        JpegSegmentWalker.Walk(b, (marker, offset, length) =>
        {
            // The FIRST Exif APP1 only — XMP also rides APP1, under its own namespace header.
            if (marker == 0xE1 && !sawExif && IsExifHeader(b, offset, length))
            {
                sawExif = true;
                orientation = ReadExifOrientation(b, offset + 6, length - 6) ?? 1;
            }
            if (IsStartOfFrame(marker))
            {
                // P (1) + Y (2) + X (2). Y = 0 means "defined later by a DNL marker" — unknown here.
                if (length >= 5)
                {
                    var height = (uint)((b[offset + 1] << 8) | b[offset + 2]);
                    var width = (uint)((b[offset + 3] << 8) | b[offset + 4]);
                    stored = Size(width, height);
                }
                return false; // the frame header is the last thing needed; EXIF precedes it
            }
            return true;
        });

        if (stored is not { } size) return null;
        return orientation is >= 5 and <= 8 ? new PixelSize(size.Height, size.Width) : size;
    }

    // SOF0–SOF15 minus DHT (C4), JPG (C8) and DAC (CC), which share the range but are not frames.
    private static bool IsStartOfFrame(byte marker)
        => marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

    private static bool IsExifHeader(byte[] b, int offset, int length)
        => length >= 6
           && b[offset] == (byte)'E' && b[offset + 1] == (byte)'x' && b[offset + 2] == (byte)'i'
           && b[offset + 3] == (byte)'f' && b[offset + 4] == 0 && b[offset + 5] == 0;

    /// <summary>
    /// Tag 0x0112 from IFD0 of the TIFF structure at <paramref name="tiff"/>, or null when it is
    /// absent or anything about the structure is out of bounds or not as the EXIF spec lays it out.
    /// Every offset is checked against the segment, in 64-bit arithmetic, before it is read.
    /// </summary>
    private static int? ReadExifOrientation(byte[] b, int tiff, int tiffLength)
    {
        if (tiffLength < 8) return null;
        bool le;
        if (b[tiff] == (byte)'I' && b[tiff + 1] == (byte)'I') le = true;
        else if (b[tiff] == (byte)'M' && b[tiff + 1] == (byte)'M') le = false;
        else return null;
        if (ReadUInt16(b, tiff + 2, le) != 42) return null;

        long ifd = ReadUInt32(b, tiff + 4, le);
        if (ifd < 8 || ifd + 2 > tiffLength) return null;
        var count = ReadUInt16(b, tiff + (int)ifd, le);
        for (var k = 0; k < count; k++)
        {
            var entry = ifd + 2 + 12L * k;
            if (entry + 12 > tiffLength) return null;
            var at = tiff + (int)entry;
            if (ReadUInt16(b, at, le) != 0x0112) continue;
            if (ReadUInt16(b, at + 2, le) != 3) return null; // SHORT — the only type the spec allows
            var value = ReadUInt16(b, at + 8, le);
            return value is >= 1 and <= 8 ? value : null;
        }
        return null;
    }

    // Edges are positive and fit an int — every format above can encode more than either holds.
    private static PixelSize? Size(uint width, uint height)
        => width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new PixelSize((int)width, (int)height)
            : null;

    private static int ReadUInt16(byte[] b, int at, bool littleEndian)
        => littleEndian ? b[at] | (b[at + 1] << 8) : (b[at] << 8) | b[at + 1];

    private static uint ReadUInt24(byte[] b, int at)
        => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16));

    private static uint ReadUInt32(byte[] b, int at, bool littleEndian)
        => littleEndian
            ? (uint)b[at] | ((uint)b[at + 1] << 8) | ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 24)
            : ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];
}
