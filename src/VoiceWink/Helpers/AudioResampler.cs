namespace VoiceWink.Helpers;

/// <summary>
/// The capture path's sample-rate conversion, moved out of <c>AudioRecorderService</c> so
/// <c>tools/resampler-aliasing</c> can LINK and measure the shipped code rather than a copy of it
/// (the harness rule: a harness that measures a re-implementation is worse than nothing).
/// Pure float math with no dependencies — that is what makes the single-file link possible, and
/// it is the reason this stayed a plain static rather than moving behind an interface.
/// </summary>
internal static class AudioResampler
{
    /// <summary>
    /// Simple linear interpolation resampler into a pre-allocated destination.
    /// For higher quality, NAudio's WdlResampler could be used.
    /// </summary>
    internal static void ResampleInto(float[] samples, int sampleCount, int sourceRate, int targetRate, float[] output, int outputLength)
    {
        double ratio = (double)sourceRate / targetRate;

        for (int i = 0; i < outputLength; i++)
        {
            double srcIndex = i * ratio;
            int idx = (int)srcIndex;
            float frac = (float)(srcIndex - idx);

            if (idx + 1 < sampleCount)
            {
                output[i] = samples[idx] * (1f - frac) + samples[idx + 1] * frac;
            }
            else if (idx < sampleCount)
            {
                output[i] = samples[idx];
            }
        }
    }
}
