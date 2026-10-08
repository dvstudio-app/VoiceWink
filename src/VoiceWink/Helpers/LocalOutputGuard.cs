using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>What <see cref="LocalOutputGuard.Check"/> found.</summary>
public enum LocalOutputVerdict
{
    Pass,
    /// <summary>Column (a): the reply grew past the class's line — an answer, not the edit asked for.</summary>
    Grew,
    /// <summary>Column (b): the reply shrank past the class's line — content was dropped.</summary>
    Shrank,
}

/// <summary>
/// LAI-9 + LAI-4: refuses a LOCAL model's reply whose length says it did something other than the
/// prompt asked, so the raw transcript pastes instead of the model's words. Applies to both keyless
/// providers (Local server and On this PC); cloud providers are never checked.
/// </summary>
/// <remarks>
/// <para><b>Why it exists (LAI-9).</b> On 2026-09-27 <c>qwen3.5:4b</c> — the planned default for PCs
/// without a dedicated graphics card — replied to 4 of 28 cleanup requests with a refusal
/// ("I cannot provide step-by-step instructions because…", once an invented safety refusal). All
/// four were short second-person commands with nothing after them to act on. The envelope's
/// firewall is prompt-only, so nothing stopped the refusal reaching the cursor.</para>
///
/// <para><b>The rules are length, measured, not a phrase list</b> (design plan §5.3's table, columns
/// a and b; column c "language changed" ships only with a measured detector, and none exists). The
/// class is <see cref="LocalPromptClassifier.Classify"/>'s:</para>
/// <list type="bullet">
/// <item>Cleanup — grew: <c>2·out &gt; 3·in + 16</c> (LAI-9: across 185 cleanups in the owner's
/// trace, <c>bench/lai9-trace-wordcounts.csv</c>, the 181 normal ones grew by at most 3 words, the 4
/// refusals by 20–54; the rule refuses exactly those 4). Shrank: <c>8·out &lt; in</c> with
/// <c>in ≥ 16</c>.</item>
/// <item>E-mail / Chat — grew: <c>out &gt; 4·in + 60</c>; shrank as Cleanup.</item>
/// <item>Translate — grew: <c>out &gt; 3·in + 30</c>; shrank as Cleanup. BOTH lines are SKIPPED when
/// either text holds a script written without spaces — <see cref="CountWords"/> counts those per
/// character, so a faithful Chinese→English translation reads 11 words → 3 and an English→Chinese
/// one grows (Codex plan review, LAI-4 B3).</item>
/// <item>Custom (an edited shipped prompt, the user's own) and Assistant (LAI-12) — never checked: an
/// answer or a summary may be any length.</item>
/// </list>
/// <para><b>The shrink line is deliberately far out: only near-total loss.</b> The envelope TELLS the
/// model to drop retracted wording ("…let's meet Tuesday in the big room, no scratch that, let's do
/// a call instead" — 23 words → 5) and to write spoken numbers and punctuation in their written form
/// ("four thousand two hundred and fifty euros and seventy five cents" → "€4,250.75", 11 → 1), so a
/// correct cleanup can be a small fraction of its dictation. A first version at <c>4·out &lt; in</c>,
/// <c>in ≥ 8</c> refused those (Opus self-review). At one eighth with sixteen words in, what is left
/// is the measured failures — a 17-word dictation answered "Banana.", a 136-word one cut to 16 — and
/// a residual no length rule can separate from them, refused and pasted raw: a long spelled-out
/// address ("d i e t e r dot …", 22 words → 1) and a long dictation retracted down to two words
/// (29 → 2).</para>
///
/// <para><b>Evidence for the LAI-4 lines</b> (2026-09-30, every committed bench result, scored by
/// <c>bench/score.py</c> and replayed with this counter by <c>bench/guard_thresholds.py</c>): no
/// passing reply of any class crosses a line (the lowest passing cleanup ratio is 0.46), the shrink
/// line catches the obeyed-injection rows ("Banana."), and the e-mail/chat and translate growth lines
/// are generous ceilings against an ANSWER that no row reached. Integer arithmetic throughout, so no
/// rounding decides a boundary.</para>
/// </remarks>
internal static class LocalOutputGuard
{
    /// <summary>
    /// The pill reason. Composed as "{reason} — {outcome}" by
    /// <c>MainViewModel.ComposeFallbackFailureStatus</c>, whose worst-case reason budget is 26.
    /// Deliberately plain (owner, 2026-09-28): the user needs to know only that the text was not
    /// enhanced; why lives in the log line <c>AIEnhancementService.ThrowIfOutputRefused</c> writes.
    /// </summary>
    internal const string RefusedReason = "AI enhancement failed";

    /// <summary>Only the keyless LOCAL providers are checked; the evidence is local.</summary>
    internal static bool Applies(AIProvider provider)
        => provider is AIProvider.LocalServer or AIProvider.OnThisPc;

    /// <summary>The class's two length checks over <paramref name="input"/> → <paramref name="output"/>.</summary>
    internal static LocalOutputVerdict Check(LocalPromptClass promptClass, string input, string output)
    {
        // An answer has no length relation to its question (LAI-12): "366" answers a 16-word
        // question, and a request for a list can run to paragraphs.
        if (promptClass is LocalPromptClass.Custom or LocalPromptClass.Assistant)
            return LocalOutputVerdict.Pass;
        // A translation to or from a script counted per character has no comparable word count.
        if (promptClass == LocalPromptClass.Translate && (HasUnspacedScript(input) || HasUnspacedScript(output)))
            return LocalOutputVerdict.Pass;
        long inWords = CountWords(input);
        long outWords = CountWords(output);
        var grew = promptClass switch
        {
            LocalPromptClass.Cleanup => 2 * outWords > 3 * inWords + 16,
            LocalPromptClass.EmailChat => outWords > 4 * inWords + 60,
            LocalPromptClass.Translate => outWords > 3 * inWords + 30,
            _ => false,
        };
        if (grew)
            return LocalOutputVerdict.Grew;
        return inWords >= 16 && 8 * outWords < inWords ? LocalOutputVerdict.Shrank : LocalOutputVerdict.Pass;
    }

    /// <summary>LAI-9's cleanup growth line on its own: past 1.5 × the input's words plus 8.</summary>
    internal static bool LooksLikeAnswer(string input, string output)
        => Check(LocalPromptClass.Cleanup, input, output) == LocalOutputVerdict.Grew;

    private static bool HasUnspacedScript(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text)
        {
            if (IsUnspacedScript(c))
                return true;
        }
        return false;
    }

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
            or >= '\u0E00' and <= '\u0E7F'    // Thai
            or >= '\u0E80' and <= '\u0FFF'    // Lao, Tibetan
            or >= '\u1000' and <= '\u109F'    // Myanmar
            or >= '\u1780' and <= '\u17FF';   // Khmer
}
