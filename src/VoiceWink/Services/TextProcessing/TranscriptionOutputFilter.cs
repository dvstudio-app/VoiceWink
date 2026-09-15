using System.Text.RegularExpressions;
using Serilog;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Removes hallucinated brackets, tags, and other transcription artifacts.
/// Pipeline step 1: Raw transcription → TranscriptionOutputFilter.
/// </summary>
public sealed class TranscriptionOutputFilter
{
    private static ILogger Logger => Log.ForContext<TranscriptionOutputFilter>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    // Matches [...] Whisper artifacts, but preserves diarization labels like [Speaker 1]
    private static readonly Regex BracketPattern = new(@"\[(?!Speaker\s+\d+\]).*?\]", RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);
    // Only strip known Whisper hallucination patterns in parentheses — not arbitrary user speech
    private static readonly Regex ParenPattern = new(@"\(\s*(?:silence|music|applause|laughter|blank[_ ]?audio|inaudible|background noise|no speech)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex BracePattern = new(@"\{.*?\}", RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex XmlTagPattern = new(@"<[^>]+>.*?</[^>]+>|<[^>]+/?>", RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex MultiSpacePattern = new(@"\s{2,}", RegexOptions.Compiled, RegexTimeout);

    public string Filter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        try
        {
            var result = text;

            result = BracketPattern.Replace(result, " ");
            result = ParenPattern.Replace(result, " ");
            result = BracePattern.Replace(result, " ");
            result = XmlTagPattern.Replace(result, " ");

            // Collapse multiple whitespace to single space
            result = MultiSpacePattern.Replace(result, " ");

            result = result.Trim();

            if (result != text.Trim())
            {
                Logger.Debug("Output filter applied: {OrigLen} → {NewLen} chars", text.Length, result.Length);
            }

            return result;
        }
        catch (RegexMatchTimeoutException ex)
        {
            Logger.Warning(ex, "Regex timeout in output filter — returning unfiltered text ({Length} chars)", text.Length);
            return text;
        }
    }

    /// <summary>
    /// Analyze a 16-bit PCM WAV file for silence. Returns true if the audio RMS
    /// is below the threshold (effectively no speech). Call before transcription
    /// to avoid sending silent audio to the transcriber.
    /// </summary>
    /// <param name="wavPath">Path to 16kHz mono 16-bit WAV file.</param>
    /// <param name="rmsThresholdDb">RMS threshold in dB. Default -60 dB sits well
    /// below normal speech (~-45 dB) but above true silence (~-78 dB).</param>
    public static bool IsSilentWav(string wavPath, double rmsThresholdDb = -60)
    {
        try
        {
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= 44) return true; // WAV header only

            // Skip 44-byte WAV header
            fs.Seek(44, SeekOrigin.Begin);

            var buffer = new byte[4096];
            long sumOfSquares = 0;
            long sampleCount = 0;

            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) >= 2)
            {
                for (int i = 0; i + 1 < bytesRead; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    sumOfSquares += (long)sample * sample;
                    sampleCount++;
                }
            }

            if (sampleCount == 0) return true;

            var rms = Math.Sqrt((double)sumOfSquares / sampleCount);
            var rmsDb = rms > 0 ? 20 * Math.Log10(rms / 32768.0) : -160;

            var isSilent = rmsDb < rmsThresholdDb;
            if (isSilent)
                Logger.Information("Silent recording detected: RMS={Rms:F1} dB (threshold={Threshold} dB)", rmsDb, rmsThresholdDb);
            else
                Logger.Debug("Audio level: RMS={Rms:F1} dB", rmsDb);

            return isSilent;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to analyze WAV for silence — proceeding with transcription");
            return false; // On error, don't skip transcription
        }
    }
}
