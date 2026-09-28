using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-9: refuses a Local server reply to the cleanup prompt that is shaped like an answer
/// rather than a cleanup, so the raw transcript pastes instead of the model's own words.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> On 2026-09-27 <c>qwen3.5:4b</c> — the planned default for PCs
/// without a dedicated graphics card — replied to 4 of 28 cleanup requests with a refusal
/// ("I cannot provide step-by-step instructions because…", once an invented safety refusal). All
/// four were short second-person commands with nothing after them to act on; questions were never
/// affected. The envelope's firewall is prompt-only, so nothing stopped the refusal reaching the
/// cursor. The local AI plan (§5.3) always meant an output guard to be the second line; LAI-1
/// moved it to LAI-4, which left the Local server provider without one.</para>
///
/// <para><b>The rule is length growth, measured, not a phrase list.</b> Across 185 cleanups in the
/// owner's prompt trace (7 models, 2026-09-24 → 27; word counts committed as
/// <c>docs/plans/2026-09-23-local-ai-enhancement/bench/lai9-trace-wordcounts.csv</c>) the 181 normal
/// ones were never more than 3 words longer than the input and the four refusals were 20–54 words
/// longer; the rule refuses exactly those four. A cleanup removes
/// fillers and fixes words — it has no reason to grow — so "more than 1.5 × the input plus 8
/// words" leaves a wide margin on both sides. Phrase matching ("I cannot…") was rejected: it is
/// English-only, and a refusal can be phrased any way.</para>
///
/// <para><b>Scope is deliberately narrow.</b> Only the Local server provider, and only the plain
/// cleanup prompt — its text unchanged from <see cref="PromptTemplates.ImproveAccuracyText"/>
/// (which also covers <see cref="PredefinedPrompts.Default"/> and a renamed but unedited copy).
/// E-mail, Chat, translation, Assistant and every edited or custom prompt may legitimately grow the
/// text, so they are never checked (plan §5.3, finding B3). Cloud providers keep their current
/// behaviour; the evidence is local.</para>
/// </remarks>
internal static class CleanupOutputGuard
{
    /// <summary>
    /// The pill reason. Composed as "{reason} — {outcome}" by
    /// <c>MainViewModel.ComposeFallbackFailureStatus</c>, whose worst-case reason budget is 26.
    /// Deliberately plain (owner, 2026-09-28): the user needs to know only that the text was not
    /// enhanced; why lives in the log line <c>AIEnhancementService.ThrowIfAnswerShaped</c> writes.
    /// </summary>
    internal const string RefusedReason = "AI enhancement failed";

    /// <summary>Whether this reply is checked at all: Local server + the unedited cleanup prompt.</summary>
    internal static bool Applies(AIProvider provider, CustomPrompt prompt)
        => provider == AIProvider.LocalServer && IsCleanupPrompt(prompt);

    /// <summary>
    /// The prompt is the shipped cleanup prompt: its text equals the template, compared with line
    /// endings normalised (CRLF, LF or a bare CR) and outer whitespace trimmed. Title and <c>SeedKey</c> are not consulted —
    /// an edited seeded prompt is a custom prompt (plan R3-B2), and the fallback prompt has no key.
    /// </summary>
    internal static bool IsCleanupPrompt(CustomPrompt prompt)
        => Normalize(prompt.PromptText) == Normalize(PromptTemplates.ImproveAccuracyText);

    /// <summary>
    /// True when the reply grew past 1.5 × the input's word count plus 8 words. Integer form
    /// (2·out &gt; 3·in + 16) so no rounding decides a boundary case.
    /// </summary>
    internal static bool LooksLikeAnswer(string input, string output)
        => 2L * CountWords(output) > 3L * CountWords(input) + 16;

    /// <summary>
    /// Whitespace-separated tokens, with two adjustments so that FORMATTING never reads as growth
    /// (the envelope tells the model to format obvious lists): a bare list marker (<c>-</c>,
    /// <c>*</c>, <c>•</c>, <c>1.</c>, <c>2)</c>) is not a word, and every character of a script
    /// written without spaces (Chinese, Japanese kana, Thai) counts as a word of its own. Without
    /// the first, 17 dictated items bulleted one per line cross the line (Codex diff r1); without
    /// the second, a Chinese dictation is ONE token and any bulleted cleanup of it would be refused.
    /// </summary>
    internal static int CountWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsListMarker(token))
                continue;
            var inWord = false;
            foreach (var c in token)
            {
                if (IsUnspacedScript(c))
                {
                    count++;
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }
        }
        return count;
    }

    private static bool IsListMarker(string token)
    {
        if (token is "-" or "*" or "\u2022")
            return true;
        if (token.Length < 2 || (token[^1] != '.' && token[^1] != ')'))
            return false;
        for (var i = 0; i < token.Length - 1; i++)
        {
            if (!char.IsAsciiDigit(token[i]))
                return false;
        }
        return true;
    }

    private static bool IsUnspacedScript(char c)
        => c is >= '\u4E00' and <= '\u9FFF'   // CJK Unified Ideographs
            or >= '\u3400' and <= '\u4DBF'    // CJK Extension A
            or >= '\uF900' and <= '\uFAFF'    // CJK Compatibility Ideographs
            or >= '\u3040' and <= '\u30FF'    // Hiragana + Katakana
            or >= '\u0E00' and <= '\u0E7F';   // Thai

    /// <summary>
    /// Line endings to <c>\n</c> — including a bare <c>\r</c>, which is what the prompt editor's
    /// multi-line WinUI <c>TextBox</c> writes back on Save — then trimmed.
    /// </summary>
    private static string Normalize(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
