namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision rule for the VAD no-speech gate: a recording is "no speech" when the
/// total detected speech falls below <see cref="VadTuning.MinTotalSpeech"/> (zero
/// segments trivially block). Overlapping/touching segments are unioned first so
/// pathological overlap can never overcount toward passing. Works on plain tuples so
/// the rule and its tests stay free of Whisper.net types. Pinned by
/// <c>NoSpeechGateTests</c>.
/// </summary>
internal static class NoSpeechGate
{
    internal static bool IsNoSpeech(
        IEnumerable<(TimeSpan Start, TimeSpan End)> segments,
        TimeSpan? minTotalSpeech = null)
    {
        var min = minTotalSpeech ?? VadTuning.MinTotalSpeech;

        // Clamp inverted intervals to empty, order by start, then union.
        var ordered = segments
            .Select(s => (s.Start, End: s.End < s.Start ? s.Start : s.End))
            .OrderBy(s => s.Start)
            .ToList();

        var total = TimeSpan.Zero;
        var i = 0;
        while (i < ordered.Count)
        {
            var start = ordered[i].Start;
            var end = ordered[i].End;
            i++;
            while (i < ordered.Count && ordered[i].Start <= end)
            {
                if (ordered[i].End > end) end = ordered[i].End;
                i++;
            }
            total += end - start;
        }

        return total < min;
    }
}
