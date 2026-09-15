using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// PRM-4: composes the transcription-hint term list — the user's Dictionary words
/// merged with the trigger phrases of every prompt. Trigger words are exact-match
/// gates on transcription OUTPUT, so the recognizer must be told they exist (live
/// failure 2026-07-17: "Translate to Dutch" misheard as "Firstly, to Dutch" — the
/// trigger never matched). COMPOSE-DON'T-STORE: the merge happens at request time,
/// never written into the Dictionary data (no ghost entries in the DB, exports, or
/// the Dictionary UI — disclosed by one line on the Dictionary page).
/// </summary>
public static class HintTermComposition
{
    /// <summary>
    /// Trigger phrases FIRST (in prompt order), then Dictionary terms, both
    /// deduplicated case-insensitively. Triggers outrank Dictionary words when a
    /// provider budget truncates from the tail (UAT 2026-07-18: with the old
    /// Dictionary-first order, 23 Dictionary words exhausted the Whisper byte budget
    /// and ALL 8 trigger phrases were silently dropped — the feature this class
    /// exists for never reached the recognizer). Triggers are ACTION phrases — a
    /// missed trigger silently changes behavior, a missed Dictionary word is a
    /// spelling error — and there are few of them, so Dictionary loses little.
    /// Null/whitespace entries are dropped by
    /// <see cref="TranscriptionHints.FromTerms"/> downstream.
    /// </summary>
    public static IReadOnlyList<string> Compose(
        IReadOnlyList<string> dictionaryTerms,
        IEnumerable<CustomPrompt> prompts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(dictionaryTerms.Count);

        foreach (var prompt in prompts)
        {
            foreach (var trigger in prompt.TriggerWords)
            {
                var trimmed = trigger?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                    result.Add(trimmed);
            }
        }

        foreach (var term in dictionaryTerms)
        {
            var trimmed = term?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }
}
