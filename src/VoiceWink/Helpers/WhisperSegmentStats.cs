using global::System.Globalization;

namespace VoiceWink.Helpers;

/// <summary>
/// Formats the local-Whisper no-speech-probability observability line — numbers only,
/// never transcript content. Returns null for zero segments so the caller logs nothing
/// and an empty transcription can't divide by zero. Observability-only: the value is
/// deliberately NOT a gate signal (confident hallucinations carry deceptively low
/// no-speech probabilities — researched 2026-07-23). Pinned by
/// <c>WhisperSegmentStatsTests</c>.
/// </summary>
internal static class WhisperSegmentStats
{
    internal static string? Format(IReadOnlyList<float> noSpeechProbabilities)
    {
        if (noSpeechProbabilities.Count == 0) return null;

        var max = noSpeechProbabilities.Max();
        var avg = noSpeechProbabilities.Average();
        return string.Create(CultureInfo.InvariantCulture,
            $"max={max:F2} avg={avg:F2} segments={noSpeechProbabilities.Count}");
    }
}
