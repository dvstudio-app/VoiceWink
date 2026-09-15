namespace VoiceWink.Helpers;

/// <summary>
/// TRN-50: the golden clip the GPU self-test decodes — a few seconds of speech whose words are
/// known, bundled beside the app so every machine tests the same input. Loaded fail-soft: an
/// absent, unreadable, mis-formatted or over-long asset yields <c>null</c> and one reason string
/// for the caller's log line, after which every consumer behaves exactly as it did before this
/// clip existed (the warm-up decodes silence, no verdict is ever formed). The feature is inert
/// without the asset by design — the clip's SOURCE is an owner decision recorded on the TRN-50
/// card, and the code must not depend on it being present.
///
/// <para><b>Two files, one contract.</b> <c>gpu-check.wav</c>: 16 kHz mono PCM16, at most
/// <see cref="MaxSeconds"/> (the Parakeet warm-up's short pipeline SHAPE is 4 s, and the clip is
/// zero-padded to exactly that length before it is decoded — Codex plan round, Blocker 3 — so a
/// longer clip would silently become a third shape). <c>gpu-check.txt</c>: the words spoken,
/// whitespace-separated, any case. Both read through the app's own parsers
/// (<see cref="WavPcm.ReadMono16(string)"/> checks the format tag and the real sample rate).</para>
/// </summary>
internal sealed class GpuSelfTestClip
{
    /// <summary>The app's canonical rate; a clip at any other rate is refused rather than resampled.</summary>
    internal const int SampleRate = 16000;

    /// <summary>The longest clip the contract admits — the short warm shape's length.</summary>
    internal const double MaxSeconds = 4.0;

    /// <summary>The exact buffer length the Parakeet self-test submits: the 4 s warm shape.</summary>
    internal const int ParakeetShapeSamples = SampleRate * 4;

    internal static string DefaultWavPath => Path.Combine(AppContext.BaseDirectory, "Assets", "SelfTest", "gpu-check.wav");
    internal static string DefaultWordsPath => Path.Combine(AppContext.BaseDirectory, "Assets", "SelfTest", "gpu-check.txt");

    private GpuSelfTestClip(float[] samples, IReadOnlyList<string> expectedWords)
    {
        Samples = samples;
        ExpectedWords = expectedWords;
    }

    /// <summary>The clip's samples as read — never longer than <see cref="ParakeetShapeSamples"/>.</summary>
    internal float[] Samples { get; }

    /// <summary>The words the clip says, as authored.</summary>
    internal IReadOnlyList<string> ExpectedWords { get; }

    /// <summary>The samples zero-padded to exactly the 4 s warm shape, in a FRESH array (the
    /// caller may hand it to a transport that writes around it). Whisper takes
    /// <see cref="Samples"/> unpadded — whisper.cpp pads every input into fixed 30 s windows.</summary>
    internal float[] PaddedForParakeet()
    {
        var padded = new float[ParakeetShapeSamples];
        Samples.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>Load the bundled clip, or explain in one short reason why there is none. Never
    /// throws: a self-test asset must never take the warm-up — let alone the app — down.</summary>
    internal static GpuSelfTestClip? TryLoad(string wavPath, string wordsPath, out string reason)
    {
        try
        {
            if (!File.Exists(wavPath))
            {
                reason = "no golden clip bundled";
                return null;
            }
            if (!File.Exists(wordsPath))
            {
                reason = "no expected-words file bundled";
                return null;
            }

            var words = File.ReadAllText(wordsPath)
                .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                reason = "expected-words file is empty";
                return null;
            }

            var (samples, sampleRate) = WavPcm.ReadMono16(wavPath);
            if (sampleRate != SampleRate)
            {
                reason = $"sample rate {sampleRate} Hz, expected {SampleRate}";
                return null;
            }
            if (samples.Length == 0)
            {
                reason = "clip carries no samples";
                return null;
            }
            if (samples.Length > ParakeetShapeSamples)
            {
                reason = $"clip is {samples.Length / (double)SampleRate:F2} s, longer than the {MaxSeconds:F0} s shape";
                return null;
            }

            reason = string.Empty;
            return new GpuSelfTestClip(samples, words);
        }
        catch (Exception ex)
        {
            // Type name only: the paths are ours, but the rule that no exception text reaches a
            // log from a file parser stays uniform.
            reason = $"clip unreadable ({ex.GetType().Name})";
            return null;
        }
    }

    /// <summary>The production load — the bundled paths.</summary>
    internal static GpuSelfTestClip? TryLoadBundled(out string reason)
        => TryLoad(DefaultWavPath, DefaultWordsPath, out reason);
}
