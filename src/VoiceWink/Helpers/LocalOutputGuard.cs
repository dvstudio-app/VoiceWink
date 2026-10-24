namespace VoiceWink.Helpers;

/// <summary>What <see cref="LocalOutputGuard.Check"/> found.</summary>
public enum LocalOutputVerdict
{
    Pass,
    /// <summary>Column (a): the reply grew past the class's line — an answer, not the edit asked for.</summary>
    Grew,
    /// <summary>Column (b): the reply shrank past the class's line — content was dropped.</summary>
    Shrank,
    /// <summary>LAI-17: the reply is mostly words the dictation does not contain — a refusal or an
    /// answer about as long as the dictation, or a reply in another language.</summary>
    Replaced,
    /// <summary>2026-10-21: an E-mail or Chat reply in English to a dictation that was not.</summary>
    SwitchedToEnglish,
}

/// <summary>
/// LAI-9 + LAI-4 + LAI-17: refuses a model's reply whose length or wording says it did something
/// other than the prompt asked, so the raw transcript pastes instead of the model's words. Applies
/// to every provider since LAI-17 (owner decision 2026-10-18); the name predates that.
/// </summary>
/// <remarks>
/// <para><b>Why it exists (LAI-9).</b> On 2026-09-27 <c>qwen3.5:4b</c> — the planned default for PCs
/// without a dedicated graphics card — replied to 4 of 28 cleanup requests with a refusal
/// ("I cannot provide step-by-step instructions because…", once an invented safety refusal). All
/// four were short second-person commands with nothing after them to act on. The envelope's
/// firewall is prompt-only, so nothing stopped the refusal reaching the cursor.</para>
///
/// <para><b>The rules are length, measured, not a phrase list</b> (design plan §5.3's table, columns
/// a and b; column c "language changed" ships only with a measured detector — the language rule at
/// the end of these remarks is that, for a reply turned into English). The
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
///
/// <para><b>LAI-17 (2026-10-18): a third rule, new words.</b> A refusal about as long as its dictation
/// passed every length line and pasted (owner report). A cleanup keeps the speaker's words; a refusal
/// or an answer does not. A Cleanup reply is refused when at least <see cref="NewWordFloor"/> of its
/// words, and at least half of them, are not in the dictation (<see cref="CountNewWords"/>). Only
/// Cleanup: E-mail and Chat legitimately rewrite (a greeting and closing around a short dictation, a
/// formal note made casual), Translate changes every word, and Custom and Assistant are never
/// checked. Evidence: every committed bench reply replayed by <c>bench/guard_thresholds.py</c> — none
/// of 4,463 passing cleanups is refused (the most a passing reply changed was 40 new words of 118, a
/// long paraphrase), while failing cleanups the length lines let through are caught, most of them
/// replies in another language (the script prints the count). 1,447 replies from six cloud providers
/// trip neither rule, which is why the guard runs for every provider.</para>
///
/// <para><b>A fourth rule, E-mail and Chat only: a reply turned into English (2026-10-21).</b> A
/// Dutch dictation into Teams came back from the built-in Qwen3.5 2B as an English chat message
/// (owner report). The new-word rule cannot judge E-mail or Chat — "I regret that I am unable to
/// attend…" made casual keeps none of its words and is correct — so this rule asks what a
/// translation changes and a rewrite does not, all three at once: the reply keeps under two fifths
/// of the dictation's distinct words (at least <see cref="LanguageDistinctFloor"/> of them), Windows'
/// language detector (<see cref="TextLanguageDetector"/>) reads the reply as English, and it reads
/// the dictation, repeated words removed (<see cref="DedupeForDetection"/>), as another language.
/// No answer on either side abstains. Drift INTO English is what small models do; another pair of
/// languages is not checked, and the chosen recognition language is not consulted (with English
/// chosen over Dutch speech it would switch the rule off exactly where it is needed). The first
/// version judged by word lists of eight languages and missed every other one; the detector
/// replaced it (owner decision, 2026-10-21). Evidence, <c>tools/language-guard-compare</c> (its
/// README states the method and limits): on 70 correct rewrites and 48 switches in 16 languages it
/// refuses 2 rewrites and catches 46 switches — the lists caught 14 — and on the committed bench it
/// refuses none of 230 correct replies and catches all 16 switches. The 2 refused are English
/// dictations the detector reads as another language ("Width six centimeters height ten
/// centimeters depth nine centimeters" as Polish, "Antes doubled, antes tripled…" as Spanish), whose
/// replies keep few words; the owner accepted them, and they are pinned as a known residual. Prompt
/// wording was measured first and rejected: no rewording lowered the 2B's switches reliably, and the
/// strongest made them more frequent (bench README, "Language retention").</para>
/// </remarks>
internal static class LocalOutputGuard
{
    /// <summary>
    /// The pill reason. Composed as "{reason} — {outcome}" by
    /// <c>MainViewModel.ComposeFallbackFailureStatus</c>, whose worst-case reason budget is 26.
    /// Plain (owner, 2026-09-28; worded 2026-10-18): the user needs to know only that the reply was
    /// not used; why lives in the log line <c>AIEnhancementService.ThrowIfOutputRefused</c> writes.
    /// The pill shows it amber, not red (<c>MainViewModel.EnhancementFallbackTone</c>).
    /// </summary>
    internal const string RefusedReason = "AI enhancement unusable";

    /// <summary>The fewest new words that can refuse a reply: a short cleanup legitimately
    /// changes a few (a fixed mishearing, digits for spoken numbers, a German "einmal" for "mal").</summary>
    internal const int NewWordFloor = 6;

    /// <summary>The class's checks over <paramref name="input"/> → <paramref name="output"/>:
    /// the two length lines first, then the new-word rule (Clean-up) or the language rule
    /// (E-mail / Chat, judged only with a <paramref name="topLanguage"/> detector).</summary>
    internal static LocalOutputVerdict Check(LocalPromptClass promptClass, string input, string output,
        Func<string, string?>? topLanguage = null)
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
        if (inWords >= 16 && 8 * outWords < inWords)
            return LocalOutputVerdict.Shrank;
        // Clean-up only: its rules keep the speaker's words. E-mail and Chat legitimately rewrite —
        // a greeting and closing around a short dictation, or a formal note made casual, can be
        // mostly new words (self-review: a 9-word "tell Anna the parcel arrived…" e-mail).
        if (promptClass is LocalPromptClass.Cleanup)
        {
            var (newWords, replyWords) = CountNewWords(input, output);
            if (newWords >= NewWordFloor && 2 * newWords >= replyWords)
                return LocalOutputVerdict.Replaced;
        }
        if (promptClass is LocalPromptClass.EmailChat && MeasureLanguage(input, output, topLanguage).SwitchedToEnglish)
            return LocalOutputVerdict.SwitchedToEnglish;
        return LocalOutputVerdict.Pass;
    }

    /// <summary>The fewest distinct dictation words the language rule judges: a shorter dictation keeps
    /// or loses a word by chance. Its own constant, not <see cref="NewWordFloor"/> (Kimi diff r1), so
    /// tuning one rule never re-tunes the other.</summary>
    internal const int LanguageDistinctFloor = 6;

    /// <summary>
    /// The facts behind the language rule, also what its log line reports: how many of the
    /// dictation's distinct words the reply keeps (<see cref="Words"/>' tokens, so both sides are
    /// split alike), and the language the detector reads for each side — null when it was not asked
    /// (the reply keeps enough words, or no detector) or gave no answer.
    /// </summary>
    internal readonly record struct LanguageMeasure(
        int KeptWords, int DistinctWords, string? DictationLanguage, string? ReplyLanguage)
    {
        internal bool LittleKept => DistinctWords >= LanguageDistinctFloor && 5 * KeptWords < 2 * DistinctWords;

        internal bool SwitchedToEnglish =>
            LittleKept && ReplyLanguage == "en" && DictationLanguage is { } source && source != "en";
    }

    /// <summary>Measures <paramref name="input"/> → <paramref name="output"/>, asking
    /// <paramref name="topLanguage"/> only when the reply keeps little of the dictation — the reply
    /// first, the dictation (<see cref="DedupeForDetection"/>) only when the reply reads as English.</summary>
    internal static LanguageMeasure MeasureLanguage(string? input, string? output, Func<string, string?>? topLanguage)
    {
        var distinct = new HashSet<string>(Words(input), StringComparer.Ordinal);
        var reply = new HashSet<string>(Words(output), StringComparer.Ordinal);
        var kept = distinct.Count(reply.Contains);
        var measure = new LanguageMeasure(kept, distinct.Count, null, null);
        if (!measure.LittleKept || topLanguage is null)
            return measure;
        measure = measure with { ReplyLanguage = topLanguage(output ?? "") };
        return measure.ReplyLanguage == "en"
            ? measure with { DictationLanguage = topLanguage(DedupeForDetection(input)) }
            : measure;
    }

    /// <summary>
    /// The dictation with repeated words removed (first occurrence kept, compared case-insensitively
    /// with punctuation trimmed), the text the detector reads: repetition amplified one ambiguous word
    /// in the plan's measurement — an English dictation repeating a word that is also French read as French.
    /// </summary>
    internal static string DedupeForDetection(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = token.Trim(".,;:!?\"'()[]—-".ToCharArray()).ToLowerInvariant();
            if (key.Length > 0 && seen.Add(key))
                kept.Add(token);
        }
        return string.Join(' ', kept);
    }

    /// <summary>
    /// How many of the reply's words do not occur in the dictation, and how many words the reply
    /// has. Words here are lower-cased whitespace tokens with punctuation trimmed from both ends
    /// (so "Thursday." matches "thursday"), a bare list marker skipped and every character of a
    /// script written without spaces a word of its own — the splitting <see cref="CountWords"/>
    /// does, so a bulleted cleanup adds no new words, except that a token that is only punctuation
    /// ("—", "&amp;") is not a word here, typographic apostrophes read as straight ones, and a reply
    /// word holding a digit is skipped together with the word right after it (spoken numbers and
    /// their units written in written form — "2 cm", "4 PM" — are the clean-up's job).
    /// </summary>
    internal static (int NewWords, int ReplyWords) CountNewWords(string? input, string? output)
    {
        var source = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in Words(input))
            source.Add(word);
        int newWords = 0, replyWords = 0;
        var afterNumber = false;
        foreach (var word in Words(output))
        {
            // A word holding a digit is neither new nor counted: the envelope tells the model to
            // write spoken numbers as digits, so "zero four seven five" → "0475" is the job done.
            // Nor is the word right after one — its unit or suffix ("2 cm", "4 PM", "3 kg"), which
            // the written form abbreviates (Codex diff r1: a list of measurements read 6 of 6 new).
            if (word.Any(char.IsDigit))
            {
                afterNumber = true;
                continue;
            }
            if (afterNumber)
            {
                afterNumber = false;
                continue;
            }
            replyWords++;
            if (!source.Contains(word))
                newWords++;
        }
        return (newWords, replyWords);
    }

    private static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        var buffer = new global::System.Text.StringBuilder();
        // Typographic apostrophes match straight ones: a model that sets "I’m" has not changed "I'm".
        var normalized = text.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
        foreach (var token in normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsListMarker(token))
                continue;
            buffer.Clear();
            foreach (var c in token)
            {
                if (IsUnspacedScript(c))
                {
                    if (TrimmedWord(buffer) is { } before)
                        yield return before;
                    buffer.Clear();
                    yield return c.ToString();
                }
                else
                {
                    buffer.Append(c);
                }
            }
            if (TrimmedWord(buffer) is { } word)
                yield return word;
        }
    }

    /// <summary>The buffer with non-word characters trimmed from both ends; null when nothing is left.</summary>
    private static string? TrimmedWord(global::System.Text.StringBuilder buffer)
    {
        int start = 0, end = buffer.Length - 1;
        while (start <= end && !IsWordChar(buffer[start]))
            start++;
        while (end >= start && !IsWordChar(buffer[end]))
            end--;
        return start > end ? null : buffer.ToString(start, end - start + 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
