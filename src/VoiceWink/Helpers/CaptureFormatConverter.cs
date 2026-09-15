using System.Buffers;
using NAudio.Wave;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Converts captured audio (any WASAPI shared-mode format) to the app's target recording format:
/// 16 kHz mono 16-bit PCM (Whisper's expected input). Extracted verbatim from
/// <c>AudioRecorderService</c> for AUD-6 so the standing warm capture and the cold recording
/// path share ONE conversion — behavior unchanged, <see cref="AudioResampler"/> included. That one
/// is the PRE-AUD-8 path (what <c>-p:AntiAliasEnabled=false</c> restores), not AUD-8's own; AUD-8
/// arrives as the <c>downsampler</c> parameter below.
/// </summary>
internal static class CaptureFormatConverter
{
    // Target format for Whisper: 16kHz mono 16-bit PCM
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    /// <summary>Bytes of converted audio per millisecond (16 kHz mono 16-bit = 32 bytes/ms).
    /// The ring buffer's cut-point math derives chunk spans from this.</summary>
    public const int TargetBytesPerMillisecond = TargetSampleRate * (TargetBitsPerSample / 8) * TargetChannels / 1000;

    /// <summary>
    /// True when <see cref="ConvertToTarget"/> can decode <paramref name="format"/> — the exact
    /// set <c>ExtractFloatSampleCount</c> accepts. The standing capture consults this BEFORE
    /// subscribing (an unsupported negotiated format would otherwise fire the per-buffer warning
    /// below at driver rate for the app's lifetime while producing nothing; Kimi diff r2 #2).
    /// </summary>
    public static bool IsSupportedFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32)
        || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

    /// <summary>
    /// Convert captured audio (any format) to 16kHz mono 16-bit PCM.
    /// Handles both IEEE float and PCM source formats with arbitrary sample rates and channel counts.
    /// Uses ArrayPool for intermediate float[] allocations to reduce GC pressure on the audio thread.
    ///
    /// <para>AUD-8: <paramref name="downsampler"/> carries the anti-alias filter and the read phase
    /// across buffers. When it is null (lever off) step 3 takes the pre-AUD-8 path verbatim,
    /// including <see cref="AudioResampler.ResampleInto"/>'s per-buffer restart — a kill switch has
    /// to restore today's bytes, not a variant of the new path. The parameter has no default: every
    /// caller must decide (the recorder and the standing capture each bind ONE instance per capture
    /// session into their subscription closures, so a straggler can never reach a newer session's
    /// filter state — the AUD-8 ∩ AUD-6 merge, 2026-08-05).</para>
    /// </summary>
    public static byte[] ConvertToTarget(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, CaptureDownsampler? downsampler)
    {
        // Step 1: Parse source samples as float (pooled)
        int sourceSampleCount = ExtractFloatSampleCount(bytesRecorded, sourceFormat);
        if (sourceSampleCount == 0) return [];

        var sourceSamples = ArrayPool<float>.Shared.Rent(sourceSampleCount);
        float[]? monoRented = null;
        float[]? resampledRented = null;
        try
        {
            ExtractFloatSamplesInto(buffer, bytesRecorded, sourceFormat, sourceSamples);

            // Step 2: Downmix to mono if needed
            float[] monoSamples;
            int monoLength;
            if (sourceFormat.Channels == 1)
            {
                monoSamples = sourceSamples;
                monoLength = sourceSampleCount;
            }
            else
            {
                monoLength = sourceSampleCount / sourceFormat.Channels;
                monoRented = ArrayPool<float>.Shared.Rent(monoLength);
                DownmixToMonoInto(sourceSamples, sourceSampleCount, sourceFormat.Channels, monoRented);
                monoSamples = monoRented;
            }

            // Step 3: Resample to 16kHz if needed
            float[] resampled;
            int resampledLength;
            if (downsampler != null)
            {
                // AUD-8 path. Anti-alias filtered where the rate is actually being reduced, with
                // the filter state and read phase carried across buffers — so the output count
                // VARIES per buffer, and that variation is the fix for the remainder drop. Take the
                // returned count; never a precomputed length.
                if (downsampler.IsPassthrough)
                {
                    resampled = monoSamples;
                    resampledLength = monoLength;
                }
                else
                {
                    resampledRented = ArrayPool<float>.Shared.Rent(downsampler.MaxOutputFor(monoLength));
                    resampledLength = downsampler.Process(monoSamples, monoLength, resampledRented);
                    resampled = resampledRented;
                }
            }
            else if (sourceFormat.SampleRate == TargetSampleRate)
            {
                resampled = monoSamples;
                resampledLength = monoLength;
            }
            else
            {
                // Pre-AUD-8 path, byte-for-byte. Do not "improve" this branch: it exists so
                // -p:AntiAliasEnabled=false reproduces shipped behaviour exactly, remainder drop
                // included.
                double ratio = (double)sourceFormat.SampleRate / TargetSampleRate;
                resampledLength = (int)(monoLength / ratio);
                resampledRented = ArrayPool<float>.Shared.Rent(resampledLength);
                AudioResampler.ResampleInto(monoSamples, monoLength, sourceFormat.SampleRate, TargetSampleRate, resampledRented, resampledLength);
                resampled = resampledRented;
            }

            // Step 4: Convert to 16-bit PCM bytes (not pooled — caller writes to WAV)
            var result = new byte[resampledLength * 2];
            for (int i = 0; i < resampledLength; i++)
            {
                var clamped = Math.Clamp(resampled[i], -1f, 1f);
                var sample = (short)(clamped * 32767f);
                result[i * 2] = (byte)(sample & 0xFF);
                result[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sourceSamples);
            if (monoRented != null) ArrayPool<float>.Shared.Return(monoRented);
            if (resampledRented != null) ArrayPool<float>.Shared.Return(resampledRented);
        }
    }

    /// <summary>
    /// Compute how many float samples will be extracted from the raw audio bytes.
    /// Returns 0 for unsupported formats.
    /// </summary>
    private static int ExtractFloatSampleCount(int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return bytesRecorded / 4;

        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            return format.BitsPerSample switch
            {
                16 => bytesRecorded / 2,
                24 => bytesRecorded / 3,
                32 => bytesRecorded / 4,
                _ => 0
            };
        }

        if (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
        {
            // Extensible 32-bit can be either IEEE float or integer PCM — both produce 4 bytes/sample
            return bytesRecorded / 4;
        }

        Log.Warning("Unsupported audio format: {Encoding} {Bits}bit", format.Encoding, format.BitsPerSample);
        return 0;
    }

    /// <summary>
    /// Extract interleaved float samples from raw audio bytes into a pre-allocated destination.
    /// Caller must ensure <paramref name="dest"/> has at least <see cref="ExtractFloatSampleCount"/> elements.
    /// </summary>
    private static void ExtractFloatSamplesInto(byte[] buffer, int bytesRecorded, WaveFormat format, float[] dest)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            Buffer.BlockCopy(buffer, 0, dest, 0, bytesRecorded);
            return;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            if (format.BitsPerSample == 16)
            {
                var sampleCount = bytesRecorded / 2;
                for (int i = 0; i < sampleCount; i++)
                {
                    var shortVal = BitConverter.ToInt16(buffer, i * 2);
                    dest[i] = shortVal / 32768f;
                }
                return;
            }

            if (format.BitsPerSample == 24)
            {
                var sampleCount = bytesRecorded / 3;
                for (int i = 0; i < sampleCount; i++)
                {
                    int val = buffer[i * 3] | (buffer[i * 3 + 1] << 8) | (buffer[i * 3 + 2] << 16);
                    if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                    dest[i] = val / 8388608f;
                }
                return;
            }

            if (format.BitsPerSample == 32)
            {
                var sampleCount = bytesRecorded / 4;
                for (int i = 0; i < sampleCount; i++)
                {
                    var intVal = BitConverter.ToInt32(buffer, i * 4);
                    dest[i] = intVal / 2147483648f;
                }
                return;
            }
        }

        // Extensible format (common with WASAPI) — check SubFormat to determine float vs integer
        if (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32)
        {
            // IEEE float SubFormat GUID: 00000003-0000-0010-8000-00aa00389b71
            var ieeeFloatGuid = new Guid("00000003-0000-0010-8000-00aa00389b71");
            var isFloat = format is NAudio.Wave.WaveFormatExtensible ext
                && ext.SubFormat == ieeeFloatGuid;
            if (isFloat)
            {
                Buffer.BlockCopy(buffer, 0, dest, 0, bytesRecorded);
            }
            else
            {
                // 32-bit integer PCM
                var sampleCount = bytesRecorded / 4;
                for (int i = 0; i < sampleCount; i++)
                {
                    var intVal = BitConverter.ToInt32(buffer, i * 4);
                    dest[i] = intVal / 2147483648f;
                }
            }
        }
    }

    /// <summary>
    /// Downmix interleaved multi-channel samples to mono by averaging channels into a pre-allocated destination.
    /// </summary>
    private static void DownmixToMonoInto(float[] samples, int sampleCount, int channels, float[] dest)
    {
        var frameCount = sampleCount / channels;
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += samples[i * channels + ch];
            }
            dest[i] = sum / channels;
        }
    }
}
