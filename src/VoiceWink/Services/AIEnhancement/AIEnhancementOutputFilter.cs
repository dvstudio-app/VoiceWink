using System.Text.RegularExpressions;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Strips reasoning tags from AI output.
/// </summary>
public static class AIEnhancementOutputFilter
{
    private const string ThinkTagName = "think";

    // Reasoning-block tag vocabulary. <think> is what Qwen/DeepSeek-R1-family chat
    // templates actually emit (live incident 2026-07-17: Groq qwen/qwen3.6-27b pasted
    // its full chain-of-thought); the other three are the defensive prompt-convention
    // set this filter has always stripped.
    private static readonly string[] TagNames =
        { "thinking", "reasoning", "internal_monologue", ThinkTagName };

    private static readonly string[] OpenTags = Array.ConvertAll(TagNames, n => $"<{n}>");

    private static readonly Regex[] PairedPatterns = Array.ConvertAll(TagNames, n =>
        new Regex($"<{n}>.*?</{n}>", RegexOptions.Compiled | RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)));

    private static readonly string ThinkOpenTag = $"<{ThinkTagName}>";
    private static readonly string ThinkCloseTag = $"</{ThinkTagName}>";

    public static string Filter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // PRE-unwrap (Codex diff r1, High): an echoed <TRANSCRIPT> wrapper would
        // otherwise MASK a truncated reasoning block from the start-anchored scan
        // below — "<TRANSCRIPT>\n<think>unfinished…" must return empty (raw-text
        // fallback), never paste chain-of-thought.
        var result = UnwrapEchoedTranscriptTags(text);
        foreach (var pattern in PairedPatterns)
            result = pattern.Replace(result, "");

        // Real reasoning emissions are always a PREFIX of the content. The two scans
        // below handle the prefix shapes the paired patterns miss, deliberately
        // conservative so an answer that merely QUOTES a tag survives intact.

        // Consumed opening tag (<think>-only shape): some serving stacks pre-seed
        // "<think>\n" in the chat template, so the completion is reasoning terminated
        // by a bare </think>. Structurally that response contains no opening tag at
        // all — gating on that protects answers that legitimately mention the tags.
        // (Accepted residual: prose quoting ONLY the closing tag, with no <think>
        // anywhere in the same response, still loses everything before it.)
        if (!text.Contains(ThinkOpenTag, StringComparison.Ordinal))
        {
            var orphanClose = result.IndexOf(ThinkCloseTag, StringComparison.Ordinal);
            if (orphanClose >= 0)
                result = result[(orphanClose + ThinkCloseTag.Length)..];
        }

        // Truncated reasoning (any tag family): max_tokens cut the block before its
        // closing tag, so the content STARTS with an opening tag that never closes —
        // the whole remainder is reasoning. Returning empty lets the caller fall back
        // to the raw transcript. Anchored to the start so a mid-text literal mention
        // never truncates the answer.
        var trimmed = result.TrimStart();
        foreach (var openTag in OpenTags)
        {
            if (trimmed.StartsWith(openTag, StringComparison.Ordinal))
                return string.Empty;
        }

        // POST-unwrap: reasoning removal can EXPOSE wrapper tags that were not at
        // the edges before it ran (e.g. "<think>…</think><TRANSCRIPT>\ntext…").
        return UnwrapEchoedTranscriptTags(result).Trim();
    }

    private const string TranscriptOpenTag = "<TRANSCRIPT>";
    private const string TranscriptCloseTag = "</TRANSCRIPT>";

    /// <summary>
    /// PRM-1b: small models echo the user-turn structure — the enhanced text arrives
    /// wrapped in the transcript tags (live 2026-07-17, llama-3.1-8b-instant pasted
    /// "&lt;TRANSCRIPT&gt;…&lt;/TRANSCRIPT&gt;" into the target app). Strip a LEADING
    /// open tag and a TRAILING close tag, each independently (full wrap and either
    /// orphan). Safe because legitimate content cannot contain these tags:
    /// TranscriptionOutputFilter strips tag shapes before any model sees the text,
    /// and BuildUserPrompt escapes a typed closing tag — so at the edges they are
    /// always an echo artifact. Mid-text occurrences are left untouched.
    /// Called TWICE by <see cref="Filter"/>: pre (so the wrapper can't mask the
    /// start-anchored truncated-reasoning scan) and post (reasoning removal can
    /// expose wrapper tags that weren't at the edges).
    /// </summary>
    private static string UnwrapEchoedTranscriptTags(string text)
    {
        var start = text.TrimStart();
        if (start.StartsWith(TranscriptOpenTag, StringComparison.Ordinal))
            text = start[TranscriptOpenTag.Length..];

        var end = text.TrimEnd();
        if (end.EndsWith(TranscriptCloseTag, StringComparison.Ordinal))
            text = end[..^TranscriptCloseTag.Length];

        return text;
    }
}
