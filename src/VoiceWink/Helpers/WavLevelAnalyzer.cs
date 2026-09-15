using System.IO;

namespace VoiceWink.Helpers;

/// <summary>Measured levels of a canonical recording WAV. <see cref="PeakDbfs"/> is the absolute
/// sample peak; <see cref="ActiveRmsDbfs"/> approximates the speech-portion level (windowed RMS
/// over the louder windows), which is what provider detection floors react to — whole-file RMS
/// under-reads recordings that are mostly pauses (AUD-2). <see cref="DurationSeconds"/> (AUD-34)
/// is how much audio the measurement covered — the fact the digital-silence verdict now keys on;
/// callers that construct levels by hand (the gain-policy tests) may leave it at zero.</summary>
internal readonly record struct WavLevels(double PeakDbfs, double ActiveRmsDbfs, double DurationSeconds = 0)
{
    /// <summary>
    /// AUD-21: true iff every sample was exactly zero — a capture that produced no audio at all,
    /// which is a different fact from "quiet" and gets a different message.
    ///
    /// <para><b>Exact only on the canonical 16-bit byte path</b> (<see cref="WavLevelAnalyzer.Measure(byte[])"/>,
    /// which is the only domain <see cref="WavLevelAnalyzer.MeasureFile"/> and therefore
    /// <c>RecordingAudioPreparation</c> ever reach). There the smallest non-zero sample is
    /// <c>1/32768</c> → −90.3 dBFS, comfortably above the −120 sentinel, so the sentinel is
    /// reachable from all-zero input and nothing else — the biconditional this feature rests on.</para>
    ///
    /// <para><b>NOT exact on the float decode-input path</b> (<see cref="WavLevelAnalyzer.Measure(ReadOnlySpan{float}, int)"/>,
    /// TRN-10b): there an arbitrarily tiny peak — 1e-25 gives −500 dB — is CLAMPED to the sentinel
    /// by <c>Math.Max</c> while the samples are genuinely non-zero. A reader in that domain must
    /// take this as "peak ≤ −120 dBFS", never as an all-zeros proof. Nothing consumes it there
    /// today; this note exists so a future consumer does not inherit a guarantee that does not hold
    /// (Kimi plan review A1).</para>
    /// </summary>
    internal bool IsDigitallySilent => PeakDbfs <= WavLevelAnalyzer.SilenceSentinelDbfs;
}

/// <summary>
/// AUD-2: level measurement for the recorder's canonical output (16 kHz mono 16-bit PCM).
/// Anything else — other formats, malformed RIFF — measures as null and the caller skips
/// normalization; this code only ever adjusts files the app itself wrote.
///
/// <para>Active-RMS spec (plan-pinned): 100 ms windows (1600 samples), full windows plus a
/// trailing partial of ≥ 50 ms; per-window MEAN POWER (power domain throughout); the active set
/// is windows with power above the 20th-percentile window (index <c>floor(0.2·(N−1))</c> of the
/// ascending sort), falling back to all windows when that leaves none (uniform audio);
/// <c>ActiveRmsDbfs = 10·log10(mean active power)</c>. Samples scale by 1/32768, so
/// <c>short.MinValue</c> is exactly −1.0. Zero signal reports the −120 sentinel.</para>
/// </summary>
internal static class WavLevelAnalyzer
{
    internal const double SilenceSentinelDbfs = -120.0;
    private const int WindowSamples = 1600;           // 100 ms at 16 kHz
    private const int MinTrailingWindowSamples = 800; // 50 ms

    internal static WavLevels? MeasureFile(string path)
    {
        try
        {
            return Measure(File.ReadAllBytes(path));
        }
        catch (global::System.Exception ex) when (ex is not global::System.OperationCanceledException)
        {
            return null; // unreadable file — caller skips normalization
        }
    }

    /// <summary>
    /// The same measurement over in-memory float samples at an arbitrary rate — the decode-input
    /// domain (TRN-10b: <see cref="DecodeInputGain"/> conditions what Parakeet decodes, and the
    /// file path can present any rate <c>WavPcm</c> read).
    ///
    /// <para>Deliberately a SEPARATE implementation, not the byte path rewritten to call it: the
    /// byte path is AUD-2's calibrated instrument and stays byte-identical. The two are pinned to
    /// agree by a parity test in <c>WavLevelAnalyzerTests</c>, which is what keeps this from being
    /// spec drift rather than deduplication debt.</para>
    /// </summary>
    internal static WavLevels? Measure(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate <= 0) return null;

        // 100 ms windows / 50 ms trailing floor, scaled from the rate rather than the byte path's
        // 16 kHz constants. A rate under 10 Hz has no expressible window — unmeasurable, not 1.
        var windowSamples = sampleRate / 10;
        if (windowSamples < 1) return null;
        var minTrailing = global::System.Math.Max(1, sampleRate / 20);

        var peak = 0.0;
        var windowPowers = new List<double>();
        var windowSumSquares = 0.0;
        var windowFill = 0;

        foreach (var f in samples)
        {
            double s = f;
            var abs = global::System.Math.Abs(s);
            if (abs > peak) peak = abs;
            windowSumSquares += s * s;
            windowFill++;
            if (windowFill == windowSamples)
            {
                windowPowers.Add(windowSumSquares / windowFill);
                windowSumSquares = 0.0;
                windowFill = 0;
            }
        }
        if (windowFill >= minTrailing)
            windowPowers.Add(windowSumSquares / windowFill);

        if (windowPowers.Count == 0)
            return null; // shorter than 50 ms — nothing the active-set spec can say

        return FromWindowPowers(peak, windowPowers, samples.Length / (double)sampleRate);
    }

    /// <summary>Active-set selection + dB conversion, shared verbatim by both domains — the p20
    /// index formula is the part of the spec that is easiest to "simplify" into drift.</summary>
    private static WavLevels FromWindowPowers(double peak, List<double> windowPowers, double durationSeconds)
    {
        var sorted = windowPowers.OrderBy(p => p).ToList();
        var p20 = sorted[(int)global::System.Math.Floor(0.2 * (sorted.Count - 1))];
        var active = windowPowers.Where(p => p > p20).ToList();
        if (active.Count == 0)
            active = windowPowers; // uniform audio — every window equals the percentile

        var meanActivePower = active.Average();
        return new WavLevels(ToDb(peak * peak), ToDb(meanActivePower), durationSeconds);

        static double ToDb(double power) => power <= 0
            ? SilenceSentinelDbfs
            : global::System.Math.Max(SilenceSentinelDbfs, 10.0 * global::System.Math.Log10(power));
    }

    internal static WavLevels? Measure(byte[] wav)
    {
        if (!TryLocateCanonicalData(wav, out var dataOffset, out var dataLength))
            return null;

        var sampleCount = dataLength / 2;
        if (sampleCount == 0) return null;

        var peak = 0.0;
        var windowPowers = new List<double>();
        var windowSumSquares = 0.0;
        var windowFill = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var s = global::System.BitConverter.ToInt16(wav, dataOffset + 2 * i) / 32768.0;
            var abs = global::System.Math.Abs(s);
            if (abs > peak) peak = abs;
            windowSumSquares += s * s;
            windowFill++;
            if (windowFill == WindowSamples)
            {
                windowPowers.Add(windowSumSquares / windowFill);
                windowSumSquares = 0.0;
                windowFill = 0;
            }
        }
        if (windowFill >= MinTrailingWindowSamples)
            windowPowers.Add(windowSumSquares / windowFill);

        if (windowPowers.Count == 0)
            return null; // shorter than 50 ms — the pipeline's short-recording guard fires first anyway

        // The windowing loops above stay per-domain (this one is AUD-2's calibrated instrument and
        // must remain byte-identical); the SELECTION is pure over (peak, windowPowers), so sharing
        // it is arithmetic-identical by construction, not a behavior change. The canonical
        // recorder rate is CanonicalSampleRate (TryLocateCanonicalData admits nothing else), so
        // the sample count IS the duration.
        return FromWindowPowers(peak, windowPowers, sampleCount / (double)CanonicalSampleRate);
    }

    /// <summary>The ONE canonical recorder rate: what <see cref="TryLocateCanonicalData"/> admits
    /// and what the byte path's duration divides by. One constant for both so the two cannot
    /// drift — a duration divided by a rate the gate no longer enforces would silently rescale
    /// AUD-34's verdict (self-review, correctness lens).</summary>
    internal const int CanonicalSampleRate = 16000;

    /// <summary>
    /// Locates the <c>data</c> chunk of a canonical recorder WAV: RIFF/WAVE, PCM (format 1),
    /// mono, 16-bit, 16 kHz. Chunk walk honors odd-length padding and clamps every length to the
    /// buffer, so a truncated final chunk yields the readable prefix rather than a throw. Any
    /// deviation from the canonical format returns false — shared with
    /// <see cref="WavGainApplier"/> so measurement and mutation can never disagree on scope.
    /// </summary>
    internal static bool TryLocateCanonicalData(byte[] wav, out int dataOffset, out int dataLength)
    {
        dataOffset = 0;
        dataLength = 0;
        if (wav.Length < 12) return false;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return false;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return false;

        var pos = 12;
        var fmtValid = false;
        while (pos + 8 <= wav.Length)
        {
            var length = global::System.BitConverter.ToInt32(wav, pos + 4);
            if (length < 0) return false;
            var body = pos + 8;
            var available = global::System.Math.Min(length, wav.Length - body);

            if (wav[pos] == 'f' && wav[pos + 1] == 'm' && wav[pos + 2] == 't' && wav[pos + 3] == ' ')
            {
                if (available < 16) return false;
                var format = global::System.BitConverter.ToInt16(wav, body);
                var channels = global::System.BitConverter.ToInt16(wav, body + 2);
                var sampleRate = global::System.BitConverter.ToInt32(wav, body + 4);
                var bits = global::System.BitConverter.ToInt16(wav, body + 14);
                fmtValid = format == 1 && channels == 1 && sampleRate == CanonicalSampleRate && bits == 16;
                if (!fmtValid) return false;
            }
            else if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
            {
                if (!fmtValid) return false; // data before fmt — not our writer's layout
                dataOffset = body;
                dataLength = available - (available % 2); // whole 16-bit samples only
                return dataLength > 0;
            }

            pos = body + length + (length % 2); // odd-length chunks carry a padding byte
        }
        return false;
    }
}
