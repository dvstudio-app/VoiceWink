using System.Runtime.InteropServices.WindowsRuntime;
using VoiceWink.Helpers;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Why a reference was transformed. Public because <see cref="ReferenceNormalizeResult"/> is —
/// an internal enum on a public record would not compile.
/// </summary>
public enum ReferenceNormalizeKind
{
    /// <summary>MPO — the multi-picture JPEG container phones produce. Re-encoded as clean JPEG.</summary>
    MultiPictureJpeg,

    /// <summary>
    /// AVIF. Converted to PNG because NO provider documents AVIF acceptance and
    /// <c>ImagePayloadWriter</c> refuses to emit any mime outside png/jpeg/webp — so the choice is
    /// convert or refuse, never passthrough.
    /// </summary>
    Avif,
}

/// <summary>
/// Outcome of a normalization that TRANSFORMED the image (ENH-6i) — a passthrough returns null
/// instead, so this record never carries a redundant "not transformed" state. <c>Image</c> is the
/// provider-ready result: a clean JPEG for <see cref="ReferenceNormalizeKind.MultiPictureJpeg"/>, a
/// PNG for <see cref="ReferenceNormalizeKind.Avif"/>. The dimensions are the source's (native and
/// post-EXIF-oriented — the output's pixel dims equal the oriented pair, with the rotation baked in).
/// </summary>
public sealed record ReferenceNormalizeResult(
    ReferenceImage Image,
    ReferenceNormalizeKind Kind,
    int NativeWidth,
    int NativeHeight,
    int OrientedWidth,
    int OrientedHeight);

/// <summary>
/// ENH-6i: re-encodes non-plain JPEG containers before upload. The proven rejection
/// class is MPO — the multi-picture container phones produce (portrait/depth shots);
/// OpenAI's edits validator rejects it outright regardless of resolution ("Invalid
/// image file or mode", live incident 2026-07-15: a 3.1 MP MPO failed while 28.7 MP
/// plain JPEGs passed at full resolution). Supersedes ENH-6h's dimension-based
/// downscale (owner decision "drop it", same day): the 32 MP failure that motivated
/// 3840-px downscaling was reattributed to the container — the resized twin that
/// "proved" the resolution theory had also been rewritten to a clean JPEG by the
/// resize. Contract:
/// <list type="bullet">
/// <item><c>image/jpeg</c> sources classified <see cref="JpegContainerKind.MultiPicture"/> by
/// <see cref="ReferenceImagePolicy.ClassifyJpegContainer"/> transform, and <c>image/avif</c> ALWAYS
/// transforms (2026-08-03 — no provider documents AVIF and <c>ImagePayloadWriter</c> refuses to emit
/// it, so there is no legal passthrough). Everything else — including arbitrarily large plain
/// images and all png/webp — returns NULL and uploads byte-identical (never touches WIC).</item>
/// <item>A POSITIVELY CLASSIFIED image that fails anywhere in the codec work THROWS
/// — the caller maps it to a typed, path-free error; uploading a known-MPO original
/// would restore the exact provider 400 this exists to eliminate.</item>
/// <item>WIC decodes an MPO's PRIMARY frame (the container is a JPEG stream whose
/// extra pictures trail the first EOI); <c>RespectExifOrientation</c> bakes phone
/// rotation upright and <c>ColorManageToSRgb</c> converts embedded color spaces;
/// the output re-encodes JPEG at <c>ImageQuality</c> 0.9.</item>
/// <item>Resource bounds: decode refuses sources past <see cref="MaxNormalizePixels"/>
/// (BGRA8 costs 4 bytes/pixel — a 50 MB compressed input must not become unbounded
/// decoded allocation), and an encoded output larger than the 50 MB aggregate cap
/// throws <see cref="ReferenceImageBudgetExceededException"/> BEFORE the output
/// array is allocated (re-encode can expand).</item>
/// </list>
/// Every WinRT async op is awaited via <c>AsTask(ct)</c>; a matching cancellation
/// propagates unwrapped.
/// </summary>
public static class ReferenceImageNormalizer
{
    /// <summary>
    /// Decode-allocation bound (ENH-6i plan round 2): sources whose native pixel
    /// count exceeds this refuse normalization with the typed error instead of
    /// decoding (~400 MB transient BGRA at the bound). Far above any real phone MPO.
    /// </summary>
    internal const long MaxNormalizePixels = 100_000_000;

    // maxPixels / maxTransformedBytes are TEST SEAMS (defaulting to the real bounds):
    // a 100 MP fixture or a >50 MB output can't be produced cheaply in a unit test.
    public static async Task<ReferenceNormalizeResult?> TryNormalizeAsync(
        ReferenceImage source, CancellationToken ct = default,
        long maxPixels = MaxNormalizePixels,
        long maxTransformedBytes = ReferenceImagePolicy.MaxTotalBytes)
    {
        ct.ThrowIfCancellationRequested();

        ReferenceNormalizeKind kind;
        switch (source.MimeType)
        {
            case "image/jpeg":
                if (ReferenceImagePolicy.ClassifyJpegContainer(source.Bytes) == JpegContainerKind.Plain)
                    return null;
                kind = ReferenceNormalizeKind.MultiPictureJpeg;
                break;

            // AVIF is MANDATORY-transform: there is no passthrough branch, because passing it
            // through reaches ImagePayloadWriter's mime whitelist and throws a raw
            // NotSupportedException at request time. Convert or throw — never null.
            case "image/avif":
                kind = ReferenceNormalizeKind.Avif;
                break;

            default:
                return null;
        }

        // KNOWN transform-required from here — failures PROPAGATE (typed at the caller).
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.Bytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
        var nativeWidth = checked((int)decoder.PixelWidth);
        var nativeHeight = checked((int)decoder.PixelHeight);
        var orientedWidth = checked((int)decoder.OrientedPixelWidth);
        var orientedHeight = checked((int)decoder.OrientedPixelHeight);
        if ((long)nativeWidth * nativeHeight > maxPixels)
            throw new InvalidOperationException(
                $"Reference image dimensions ({nativeWidth}x{nativeHeight}) exceed the processing bound");

        // AVIF carries alpha and PNG preserves it, so that path decodes STRAIGHT — premultiplied
        // pixels written into a PNG darken every semi-transparent edge. JPEG has no alpha, so its
        // long-standing Premultiplied decode is left exactly as it was.
        var alphaMode = kind == ReferenceNormalizeKind.Avif
            ? BitmapAlphaMode.Straight
            : BitmapAlphaMode.Premultiplied;

        // No BitmapTransform scaling (ScaledWidth/Height 0 = original size) — ENH-6i
        // re-encodes at NATIVE resolution; only the container is rewritten.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, alphaMode, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

        using var output = new InMemoryRandomAccessStream();

        // PNG for AVIF: lossless (no generational loss on a re-encode the user did not ask for), and
        // its encoder is IN-BOX. WebP would be smaller but WinRT documents no WebP ENCODER at all, so
        // a WebP target would need a runtime probe and a fallback ladder to do what PNG does
        // unconditionally.
        //
        // NOT chosen for alpha, despite PNG being alpha-capable: WIC's AVIF decoder reports
        // BitmapAlphaMode.Ignore and returns opaque pixels whatever mode is requested (measured
        // 2026-08-03), so AVIF transparency is already gone before we pick an encoder. Pinned by
        // Avif_AlphaIsLostByTheDecoder_KnownLimitation so a future Windows update that fixes this
        // surfaces as a failing test rather than going unnoticed.
        var encoderId = kind == ReferenceNormalizeKind.Avif
            ? BitmapEncoder.PngEncoderId
            : BitmapEncoder.JpegEncoderId;
        var options = new BitmapPropertySet();
        if (kind == ReferenceNormalizeKind.MultiPictureJpeg)
        {
            // Documented 0–1 Single; 0.9 trades a little size for fidelity — the
            // provider re-processes the image anyway (ENH-6h recorded decision).
            options.Add("ImageQuality", new BitmapTypedValue(0.9f, PropertyType.Single));
        }
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output, options)
            .AsTask(ct).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

        // Budget check BEFORE the output allocation (plan round 2): an expanding
        // re-encode past the aggregate cap is a BUDGET outcome, not a codec failure.
        if ((long)output.Size > maxTransformedBytes)
            throw new ReferenceImageBudgetExceededException(
                $"Reference images exceed {ReferenceImagePolicy.MaxTotalBytes / (1024 * 1024)} MB total after processing");

        var bytes = new byte[checked((int)output.Size)];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None)
            .AsTask(ct).ConfigureAwait(false);

        var outputMime = kind == ReferenceNormalizeKind.Avif ? "image/png" : "image/jpeg";
        return new ReferenceNormalizeResult(new ReferenceImage(bytes, outputMime),
            kind, nativeWidth, nativeHeight, orientedWidth, orientedHeight);
    }
}
