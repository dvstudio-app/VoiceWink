using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-10: the one fail-soft file adapter over <see cref="ZeroRunClassifier"/>, shared by both
/// <c>Audio level:</c> emit sites (<c>RecordingAudioPreparation</c> and <c>AudioFileProcessor</c>)
/// — it lives beside the classifier rather than inside either service because neither owns it
/// (the file-conversion side depending on the recording-preparation side for a static I/O adapter
/// was the wrong coupling; self-review, 2026-08-19).
///
/// <para><b>The size gate is the memory-safety argument.</b> <c>WavPcm.ReadMono16</c> holds the
/// raw byte payload and the full <c>float[]</c> simultaneously (~3× the payload transiently). The
/// Audio Transcribe envelope admits files to 512 MB (<c>MaxNormalizableBytes</c>), sized for two
/// plain byte-reads — an ungated scan there would allocate ~1.6 GB for one log line (self-review,
/// 2026-08-19). <see cref="MaxScanBytes"/> = 64 MiB ≈ 35 minutes of 16 kHz mono 16-bit: beyond
/// any dictation (the recording path never comes close), so the gate only ever skips the tokens
/// on large Audio Transcribe imports — where the file is external and the mic-dropout question
/// the counters answer barely applies.</para>
///
/// <para><b>Failure is never silent</b> — a Debug line names the skip or the failure, because a
/// support-bundle reader who greps <c>zeroCut=</c> and finds nothing must be able to distinguish
/// "scan failed/skipped" from "older build" (the TailRescue rule: a silent fail-soft recreates
/// the invisibility the feature exists to remove). Debug, not Warning: a missing diagnostic
/// token is an investigation detail, not an incident.</para>
/// </summary>
internal static class ZeroRunScan
{
    private static ILogger Logger => Log.ForContext(typeof(ZeroRunScan));

    /// <summary>64 MiB — see the class doc for the derivation.</summary>
    internal const long MaxScanBytes = 64L * 1024 * 1024;

    /// <summary>Scan a WAV for classified zero runs; null means "no tokens on the line" (too
    /// large, unreadable, or the scan failed) and the emit sites fall back to the pre-AUD-10
    /// template. Never throws.</summary>
    internal static ZeroRunCounts? TryScan(string wavPath)
    {
        try
        {
            var length = new global::System.IO.FileInfo(wavPath).Length;
            if (length > MaxScanBytes)
            {
                Logger.Debug("Zero-run scan skipped: {Length} bytes exceeds the {Max} byte gate", length, MaxScanBytes);
                return null;
            }
            var (samples, rate) = WavPcm.ReadMono16(wavPath);
            return ZeroRunClassifier.Classify(samples, rate);
        }
        catch (global::System.Exception ex)
        {
            try { Logger.Debug(ex, "Zero-run scan failed — Audio level line omits the tokens"); }
            catch { /* the diagnostic must never become a fault source */ }
            return null;
        }
    }
}
