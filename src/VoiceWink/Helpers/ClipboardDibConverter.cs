using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace VoiceWink.Helpers;

/// <summary>A decoded image ready to hand to the clipboard as CF_DIB.</summary>
internal sealed record ClipboardDib(byte[] Bytes, int Width, int Height);

/// <summary>
/// Turns encoded image bytes into a CF_DIB payload.
///
/// <para><b>Why this exists (2026-08-03).</b> <c>ClipboardService</c> decoded with
/// <c>System.Drawing.Bitmap</c> — GDI+, which reads BMP/GIF/JPEG/PNG/TIFF and nothing else. When
/// OpenRouter's riverflow models started returning WebP, every copy failed with
/// <c>ArgumentException: Parameter is not valid.</c> while History rendered the same file happily,
/// because WinUI decodes through WIC. Swapping to WIC fixes WebP and generalizes to AVIF/HEIF as
/// providers adopt them, with no re-encode and no format assumptions.</para>
///
/// <para>Extracted rather than inlined per the hub-responsibility rule: <c>ClipboardService</c> keeps
/// the clipboard mechanics (open/empty/set, the write lease, CF_DIB ownership) and this owns pixels.
/// It is also the half that can be tested — the clipboard itself cannot.</para>
/// </summary>
internal static class ClipboardDibConverter
{
    /// <summary>
    /// Decoded-pixel bound, deliberately NOT shared with
    /// <c>ReferenceImageNormalizer.MaxNormalizePixels</c> (100 MP) — that bound is sized for a
    /// single decode, and this operation holds several buffers at once.
    ///
    /// <para><b>Peak accounting at this bound, honestly (4 bytes/pixel):</b> the decoded
    /// <c>SoftwareBitmap</c> ~64 MB, the pixel copy ~64 MB, the DIB ~64 MB, and then
    /// <c>ClipboardService</c>'s <c>GlobalAlloc</c> block another ~64 MB — plus the encoded bytes
    /// the caller already holds. So roughly <b>260 MB + encoded</b>, not the ~128 MB a single buffer
    /// would suggest. An earlier comment here claimed 32 MP capped the operation near 256 MB by
    /// counting only two of the four buffers (caught in review).</para>
    ///
    /// <para>16 MP is still ~2× a 4K generation (8.3 MP), which is the realistic ceiling for
    /// provider output, while keeping the whole chain inside a few hundred MB. Generated payloads
    /// come from arbitrary provider models, so this is a real bound rather than a formality.</para>
    /// </summary>
    internal const long MaxClipboardPixels = 16_000_000;

    private const int BitmapInfoHeaderSize = 40;

    /// <summary>
    /// Decode <paramref name="imageBytes"/> and build the CF_DIB payload.
    ///
    /// <para><b>Straight alpha, not premultiplied.</b> The GDI+ path this replaces locked
    /// <c>Format32bppArgb</c>, which is straight — and a BI_RGB CF_DIB carries no alpha semantics, so
    /// consumers read the channels as-is. Handing over premultiplied pixels darkens every
    /// semi-transparent edge, which is why <c>ReferenceImageNormalizer</c>'s Premultiplied decode is
    /// NOT the precedent to copy here despite being the nearest WIC code in the repo.</para>
    ///
    /// <para><c>IgnoreExifOrientation</c> for the same reason: GDI+ never applied EXIF rotation, so
    /// respecting it now would silently rotate images that used to paste upright.</para>
    /// </summary>
    internal static async Task<ClipboardDib> ConvertAsync(byte[] imageBytes)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(imageBytes.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        var width = checked((int)decoder.PixelWidth);
        var height = checked((int)decoder.PixelHeight);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Image has no pixels");
        if ((long)width * height > MaxClipboardPixels)
            throw new InvalidOperationException(
                $"Image dimensions ({width}x{height}) exceed the clipboard processing bound");

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyToBuffer(pixels.AsBuffer());

        return new ClipboardDib(BuildDib(pixels, width, height), width, height);
    }

    /// <summary>
    /// BITMAPINFOHEADER + bottom-up 32bpp BI_RGB rows — byte-for-byte the layout the GDI+ path
    /// produced, so nothing downstream had to change. Split out so the arithmetic is testable
    /// without a decoder: <c>checked</c> throughout, because a width×height×4 that silently wraps
    /// would produce a short buffer and a torn paste rather than a failure.
    /// </summary>
    internal static byte[] BuildDib(byte[] bgraTopDown, int width, int height)
    {
        var rowSize = checked(width * 4);
        var dib = new byte[checked(BitmapInfoHeaderSize + (rowSize * height))];

        BitConverter.GetBytes(BitmapInfoHeaderSize).CopyTo(dib, 0); // biSize
        BitConverter.GetBytes(width).CopyTo(dib, 4);                // biWidth
        BitConverter.GetBytes(height).CopyTo(dib, 8);               // biHeight (positive = bottom-up)
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);            // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);           // biBitCount
        // biCompression = 0 (BI_RGB); the remaining header fields stay zero.

        for (var y = 0; y < height; y++)
        {
            var src = y * rowSize;
            var dst = BitmapInfoHeaderSize + ((height - 1 - y) * rowSize);
            Array.Copy(bgraTopDown, src, dib, dst, rowSize);
        }

        return dib;
    }
}
