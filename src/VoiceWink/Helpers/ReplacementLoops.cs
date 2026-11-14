using System.Globalization;
using VoiceWink.Services.Data;

namespace VoiceWink.Helpers;

/// <summary>DCT-5: rules run once in row order, so a loop (colour → color, color → colour) makes one rule silently undo the other.</summary>
// shortcut: only whole-variant loops are found, not a replacement containing another rule's word inside a phrase; upgrade if a user reports one.
internal static class ReplacementLoops
{
    /// <summary>One stored rule: comma-separated originals and the text they become.</summary>
    public readonly record struct Rule(int Id, string OriginalText, string ReplacementText);

    /// <summary>The rules, in path order, that would lead the candidate's replacement back to one of its variants; empty when none.</summary>
    public static IReadOnlyList<Rule> FindLoop(IEnumerable<Rule> rules, string original, string replacement, int? excludeId = null)
        => FindLoop(Graph.Build(rules, excludeId), original, replacement);

    /// <summary>Ids of every rule that is part of a loop with the others.</summary>
    public static IReadOnlySet<int> LoopingRuleIds(IReadOnlyCollection<Rule> rules)
    {
        // One graph serves every rule: a path back from a rule's target reaches its variant before it could use the rule itself.
        var graph = Graph.Build(rules, excludeId: null);
        var ids = new HashSet<int>();
        foreach (var rule in rules)
        {
            if (FindLoop(graph, rule.OriginalText, rule.ReplacementText).Count > 0)
                ids.Add(rule.Id);
        }
        return ids;
    }

    // The matcher's IgnoreCase follows the current culture (Turkish I ↔ ı), so words are compared by that culture's lower case.
    private static string Key(string word) => word.ToLower(CultureInfo.CurrentCulture);

    private static IReadOnlyList<Rule> FindLoop(Graph graph, string original, string replacement)
    {
        var target = replacement.Trim();
        if (target.Length == 0)
            return [];
        var start = Key(target);
        var sources = DictionaryMerge.SplitVariants(original).Select(Key).ToHashSet(StringComparer.Ordinal);

        // Two case-only rules on one word with different targets undo each other.
        if (sources.Remove(start) && graph.CaseOnly.TryGetValue(start, out var caseRules))
        {
            foreach (var rule in caseRules)
            {
                if (!string.Equals(rule.ReplacementText.Trim(), target, StringComparison.Ordinal))
                    return [rule];
            }
        }
        if (sources.Count == 0)
            return [];

        // Breadth-first from the replacement; each reached word remembers the rule that reached it.
        var reachedBy = new Dictionary<string, (string From, Rule Rule)>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var word = queue.Dequeue();
            if (!graph.Edges.TryGetValue(word, out var outgoing)) continue;
            foreach (var (to, rule) in outgoing)
            {
                if (!visited.Add(to)) continue;
                reachedBy[to] = (word, rule);
                if (sources.Contains(to))
                    return PathTo(to, start, reachedBy);
                queue.Enqueue(to);
            }
        }
        return [];
    }

    private static List<Rule> PathTo(string end, string start, Dictionary<string, (string From, Rule Rule)> reachedBy)
    {
        var path = new List<Rule>();
        for (var word = end; word != start; word = reachedBy[word].From)
            path.Add(reachedBy[word].Rule);
        path.Reverse();
        return path;
    }

    /// <summary>Words as nodes keyed by <see cref="Key"/>, one edge per variant; case-only rules kept apart.</summary>
    private sealed class Graph
    {
        public Dictionary<string, List<(string To, Rule Rule)>> Edges { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Rule>> CaseOnly { get; } = new(StringComparer.Ordinal);

        public static Graph Build(IEnumerable<Rule> rules, int? excludeId)
        {
            var graph = new Graph();
            foreach (var rule in rules)
            {
                if (rule.Id == excludeId) continue;
                var to = rule.ReplacementText.Trim();
                if (to.Length == 0) continue;
                var toKey = Key(to);
                foreach (var from in DictionaryMerge.SplitVariants(rule.OriginalText))
                {
                    var fromKey = Key(from);
                    if (fromKey == toKey)
                        Add(graph.CaseOnly, fromKey, rule);
                    else
                        Add(graph.Edges, fromKey, (toKey, rule));
                }
            }
            return graph;
        }

        private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }
    }
}
