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
/// <para>Cached once a probe completes (or keeps faulting), since the codec set is fixed per process — IMG-12.</para>
/// </summary>
internal static class ImageCodecSupport
{
    /// <summary>The session's one AVIF probe.</summary>
    internal static readonly ProbeLatch Avif = new(ProbeAvifAsync);

    /// <summary>True when this machine can decode AVIF pixels, proven by decoding a sample.</summary>
    // Decoding a 507-byte image is a few milliseconds; 5 s is headroom for a cold codec load.
    internal static bool CanDecodeAvif => Avif.Read(TimeSpan.FromSeconds(5));

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

    /// <summary>Decodes the embedded sample to pixels; throws on a machine without the codec.</summary>
    internal static async Task<bool> ProbeAvifAsync()
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

    /// <summary>IMG-12: caches a probe's answer only once it COMPLETED; any other read fails closed for that read alone.</summary>
    internal sealed class ProbeLatch
    {
        // A codec-less machine faults on every probe, so repeated faults become the answer.
        // shortcut: three transient faults also refuse AVIF until restart, upgrade if the faults log shows a capable machine hitting the cap.
        // shortcut: a probe that never returns is never restarted, upgrade if a wedged codec is ever seen in the field.
        internal const int MaxFaults = 3;

        private readonly Func<Task<bool>> _probe;
        private readonly object _gate = new();
        private Task<bool>? _current;
        private bool? _known;
        private int _faults;
        private bool _timedOut;

        internal ProbeLatch(Func<Task<bool>> probe) => _probe = probe;

        /// <summary>True once the answer is cached for the session.</summary>
        internal bool IsSettled { get { lock (_gate) return _known.HasValue; } }

        /// <summary>Why the last read could not answer; tests surface it, since no log sink runs there.</summary>
        internal string? LastFailure { get { lock (_gate) return _lastFailure; } }
        private string? _lastFailure;

        internal bool Read(TimeSpan wait)
        {
            lock (_gate)
            {
                if (_known is bool known) return known;

                // Task.Run is LOAD-BEARING, not tidiness. Callers include UI-thread code building a
                // file picker, and blocking a dispatcher thread on a WinRT IAsyncOperation whose
                // continuations want that same thread is the classic sync-over-async deadlock.
                // Hopping to the thread pool first means the awaits resume on a pool thread and the
                // blocking wait is safe. (Observed live: the picker silently omitted .avif because
                // the probe never completed on the UI thread.)
                var started = _current is null;
                _current ??= Task.Run(_probe);

                // BOUNDED, and paid once per session: a wedged codec would otherwise freeze Browse or a History row on every read.
                if (!HasEnded(_current, started && !_timedOut ? wait : TimeSpan.Zero))
                {
                    if (started && !_timedOut)
                    {
                        _timedOut = true;
                        _lastFailure = $"timed out after {wait.TotalSeconds:0.###} s";
                        Log.Warning("AVIF decode probe timed out; AVIF unavailable until it completes");
                    }
                    return false;
                }

                if (_current.IsCompletedSuccessfully)
                {
                    _known = _current.Result;
                    Log.Information("AVIF decode probe: {Supported}", _known.Value ? "supported" : "unsupported");
                    return _known.Value;
                }

                var ex = _current.Exception?.GetBaseException();
                _lastFailure = ex is null ? "cancelled" : $"{ex.GetType().Name}: {ex.Message}";
                _current = null;
                if (++_faults >= MaxFaults)
                {
                    _known = false;
                    Log.Warning(ex, "AVIF decode probe failed {Faults} times; treating AVIF as unsupported", _faults);
                }
                else
                {
                    Log.Warning(ex, "AVIF decode probe failed ({Faults} of {MaxFaults}); will ask again", _faults, MaxFaults);
                }
                return false;
            }
        }

        private static bool HasEnded(Task task, TimeSpan wait)
        {
            try { return task.Wait(wait); }
            catch (AggregateException) { return true; }
        }

        private static Serilog.ILogger Log => Serilog.Log.ForContext(typeof(ImageCodecSupport));
    }
}
