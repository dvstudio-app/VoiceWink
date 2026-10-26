namespace VoiceWink.Services.Data;

/// <summary>A replacement rule as it travels: what is matched, what it becomes, and whether it is on.</summary>
public sealed record DictionaryRule(string OriginalText, string ReplacementText, bool IsEnabled = true);

/// <summary>
/// The ONE rule for adding Dictionary entries next to the ones already there — shared by the
/// shipped-defaults seed (<see cref="DefaultDictionarySeed"/>) and a backup import
/// (<see cref="DictionaryTransfer"/>), so the two can never disagree about what "already there"
/// means. Pure: it decides, the callers write.
///
/// <para><b>Words</b> are compared trimmed and case-insensitively ("Openrouter" is "OpenRouter"),
/// against the present rows AND the earlier incoming ones. <b>Rules</b> keep only the spellings
/// (comma-separated variants) that no present rule matches yet — a DISABLED rule included: the
/// user switched it off on purpose, and an added rule for the same spelling would override that —
/// nor an earlier incoming rule. So a rule for a word this PC already corrects keeps this PC's
/// version, and a rule with nothing left is not added.</para>
/// </summary>
public static class DictionaryMerge
{
    public sealed record Plan(IReadOnlyList<string> WordsToAdd, IReadOnlyList<DictionaryRule> RulesToAdd, int AlreadyPresent);

    public static string[] SplitVariants(string originalText)
        => originalText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Plan Decide(
        IEnumerable<string> presentWords,
        IEnumerable<string> presentRuleOriginals,
        IEnumerable<string> incomingWords,
        IEnumerable<DictionaryRule> incomingRules)
    {
        var alreadyPresent = 0;

        var words = new HashSet<string>(presentWords.Select(w => w.Trim()), StringComparer.OrdinalIgnoreCase);
        var wordsToAdd = new List<string>();
        foreach (var raw in incomingWords)
        {
            var word = raw.Trim();
            if (word.Length == 0) continue;
            if (words.Add(word)) wordsToAdd.Add(word);
            else alreadyPresent++;
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in presentRuleOriginals)
            foreach (var variant in SplitVariants(original))
                covered.Add(variant);
        var rulesToAdd = new List<DictionaryRule>();
        foreach (var rule in incomingRules)
        {
            var replacement = rule.ReplacementText.Trim();
            var variants = SplitVariants(rule.OriginalText);
            if (replacement.Length == 0 || variants.Length == 0) continue;
            var remaining = variants.Where(v => covered.Add(v)).ToArray();
            if (remaining.Length == 0)
            {
                alreadyPresent++;
                continue;
            }
            rulesToAdd.Add(rule with { OriginalText = string.Join(", ", remaining), ReplacementText = replacement });
        }

        return new Plan(wordsToAdd, rulesToAdd, alreadyPresent);
    }
}
