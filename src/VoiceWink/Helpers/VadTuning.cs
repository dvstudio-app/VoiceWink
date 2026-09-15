namespace VoiceWink.Helpers;

/// <summary>
/// The Silero-VAD no-speech gate's tuning, set EXPLICITLY on the
/// <c>WhisperVadProcessorBuilder</c> so a Whisper.net / whisper.cpp bump can never
/// silently drift the product gate. Values are whisper.cpp's stock VAD defaults — the
/// same set upstream VoiceInk ships — chosen deliberately, not inherited.
/// Structurally pinned by <c>VadTuningTests</c>.
/// </summary>
internal static class VadTuning
{
    /// <summary>Per-frame probability above which audio counts as speech.</summary>
    internal const float Threshold = 0.5f;

    /// <summary>Minimum duration for a detected segment to count as speech at all.</summary>
    internal static readonly TimeSpan MinSpeechDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Silence shorter than this joins adjacent speech into one segment.</summary>
    internal static readonly TimeSpan MinSilenceDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>Padding added around each detected speech segment.</summary>
    internal static readonly TimeSpan SpeechPadding = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Minimum TOTAL detected speech for a recording to pass the gate
    /// (<see cref="NoSpeechGate.IsNoSpeech"/>). Matches <see cref="MinSpeechDuration"/>:
    /// any segment the VAD emits already meets the per-segment minimum, so this bound
    /// exists to be robust against padding-only slivers, not to demand a second word.
    /// </summary>
    internal static readonly TimeSpan MinTotalSpeech = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Native detect thread count. 1 is LOAD-BEARING (REL-15, 2026-07-24): the Silero
    /// detect dispatches one tiny ggml graph compute per 512-sample window
    /// (~31/audio-second), and with more than one thread every compute forks, joins,
    /// and immediately frees a disposable OpenMP team — the vcomp140 hot loop
    /// SUSPECTED behind the 0xc0000005 access violation that killed the app right
    /// after a detect (no dump existed; this removes the path, it does not prove the
    /// race). In
    /// the vendored ggml (whisper.cpp f24588a) n_threads == 1 bypasses both the
    /// "#pragma omp parallel" region (ggml-cpu.c:3331-3354) and ggml_barrier's
    /// "#pragma omp barrier" (566-573), removing the VAD graph executor's OpenMP
    /// team/barrier path; the per-window graph is overhead-bound, so parallelism
    /// bought nothing. Full record: docs/plans/2026-07-24-rel15-vad-vcomp-crash/.
    /// </summary>
    internal const int DetectThreads = 1;
}
