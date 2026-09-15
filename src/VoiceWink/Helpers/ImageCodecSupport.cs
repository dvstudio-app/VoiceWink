using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace VoiceWink.Helpers;

/// <summary>
/// Which image formats THIS machine's WIC can actually decode.
///
/// <para><b>Why this is not a constant.</b> AVIF decoding comes from optional OS components — the
/// HEIF Image Extension plus the AV1 Video Extension. Both ship with most Windows 11 installs but
/// neither is guaranteed — either can be absent, and is then a Store download. A build
/// that offers <c>.avif</c> unconditionally would let a user attach a reference that cannot be read
/// — turning "cannot attach" into "attaches, then fails at generation", which is the exact trade the
/// picker exists to avoid.</para>
///
/// <para><b>The probe DECODES PIXELS. It does not ask what the codec claims.</b> Two weaker probes
/// were tried and both give false positives on this machine:</para>
/// <list type="number">
/// <item><c>BitmapDecoder.CreateAsync</c> SUCCEEDS for an AVIF it cannot decode — it recognises the
/// container and only fails later at <c>GetSoftwareBitmapAsync</c>.</item>
/// <item><c>GetDecoderInformationEnumerator</c> reports advertised metadata and file extensions, not
/// working codecs — a decoder can list <c>.avif</c> and still fail to produce pixels (Codex diff
/// review r2; the behaviour in (1) is direct evidence the two can disagree).</item>
/// </list>
/// <para>So the only honest test is the operation itself, run once against a tiny embedded sample.</para>
///
/// <para>Cached: the installed codec set does not change within a process run, and this is consulted
/// while building a file picker and while deciding which references are usable.</para>
/// </summary>
internal static class ImageCodecSupport
{
    private static bool? _avif;
    private static readonly object Gate = new();

    /// <summary>True when this machine can decode AVIF pixels, proven by decoding a sample.</summary>
    internal static bool CanDecodeAvif
    {
        get
        {
            lock (Gate) { return _avif ??= ProbeAvif(); }
        }
    }

    /// <summary>
    /// A 64x48 AVIF with an alpha channel (507 bytes). Embedded rather than read from disk so the
    /// probe cannot be defeated by a missing or unreadable file, and so it behaves identically in a
    /// packaged build.
    ///
    /// <para><b>The DIMENSIONS are load-bearing.</b> The first version of this probe used a 4x2
    /// sample, and WIC refuses to decode it — AV1 cannot represent an image that small. The probe
    /// therefore reported "no AVIF support" on a machine with every codec installed, and would have
    /// done so on EVERY machine, silently disabling the feature for everyone. Measured 2026-08-03:
    /// 64x48 decodes, 4x2 does not, on the same machine, lossy and lossless alike. If this sample is
    /// ever regenerated, keep it at a realistic size and re-verify that it decodes.</para>
    /// </summary>
    private const string AvifProbeSampleBase64 =
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAGGbWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAA" +
        "AAAAAAAOcGl0bQAAAAAAAQAAACxpbG9jAAAAAEQAAAIAAQAAAAEAAAHNAAAALgACAAAAAQAAAa4AAAAfAAAAQmlpbmYAAAAAAAIA" +
        "AAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAABppbmZlAgAAAAACAABhdjAxQWxwaGEAAAAAGmlyZWYAAAAAAAAADmF1eGwAAgAB" +
        "AAEAAADDaXBycAAAAJ1pcGNvAAAAFGlzcGUAAAAAAAAAQAAAADAAAAAQcGl4aQAAAAADCAgIAAAADGF2MUOBAAwAAAAAE2NvbHJu" +
        "Y2x4AAEADQAGgAAAAA5waXhpAAAAAAEIAAAADGF2MUOBABwAAAAAOGF1eEMAAAAAdXJuOm1wZWc6bXBlZ0I6Y2ljcDpzeXN0ZW1z" +
        "OmF1eGlsaWFyeTphbHBoYQAAAAAeaXBtYQAAAAAAAAACAAEEAQKDBAACBAEFhgcAAABVbWRhdBIACgYYFX+9hUAyExQADDEAwz9K" +
        "PMCaC3KSe2VelcASAAoGGBV/vYEIMiIUAAMMMMQAtINbH+A3NPWtc/mJqVpExKvdb3I/qrTwNjcm";

    private static bool ProbeAvif()
    {
        try
        {
            // Task.Run is LOAD-BEARING, not tidiness. Callers include UI-thread code building a file
            // picker, and blocking a dispatcher thread on a WinRT IAsyncOperation whose
            // continuations want that same thread is the classic sync-over-async deadlock. Hopping
            // to the thread pool first means the awaits resume on a pool thread and the blocking
            // wait is safe. (Observed live: the picker silently omitted .avif because the probe
            // never completed on the UI thread.)
            // BOUNDED. Task.Run removes the deadlock, but a blocking wait with no deadline still
            // freezes whatever called us — Browse, or a History row — if a codec wedges. The catch
            // below cannot fail closed on an operation that simply never returns, so the timeout is
            // what makes "fail closed" true rather than aspirational. Decoding a 507-byte image is
            // a few milliseconds; 5 s is pure headroom for a cold codec load.
            var probe = Task.Run(ProbeAsync);
            if (!probe.Wait(TimeSpan.FromSeconds(5)))
            {
                Serilog.Log.ForContext(typeof(ImageCodecSupport))
                    .Warning("AVIF decode probe timed out; treating AVIF as unsupported");
                return false; // cached by the caller — one slow probe must not be paid repeatedly
            }

            var result = probe.GetAwaiter().GetResult();
            Serilog.Log.ForContext(typeof(ImageCodecSupport))
                .Information("AVIF decode probe: {Supported}", result ? "supported" : "unsupported");
            return result;
        }
        catch (Exception ex)
        {
            // Fail CLOSED: if we cannot tell, do not advertise the format. Offering something we
            // cannot read is worse than not offering it.
            Serilog.Log.ForContext(typeof(ImageCodecSupport))
                .Warning(ex, "AVIF decode probe failed; treating AVIF as unsupported");
            return false;
        }

        static async Task<bool> ProbeAsync()
        {
            var bytes = Convert.FromBase64String(AvifProbeSampleBase64);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // The operation that actually matters — construction alone proves nothing.
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;
        }
    }
}
