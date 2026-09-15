using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// PRM-4 Option B: pure decisions that defend against a GENERATIVE transcription
/// model echoing its bias prompt on near-silence. Trigger phrases now ride that
/// prompt, so an echo like "VoiceWink, generate an image" is textually identical to a
/// real utterance — no classifier is perfect. The policy:
///
/// - <see cref="IsLikelyGenerativeEcho"/> — STRICT: the raw transcript is a contiguous
///   run of COMPLETE terms from the exact prompt sent. Conservative on purpose (only
///   clear echoes), so real non-image speech is not dropped. When true the caller
///   blocks the transcript (no override, paste, enhancement, or Enter) but RETAINS
///   the WAV and arms Retry with SkipEchoGate — a false positive (a real dictation
///   of one hinted name) costs one click, never the content (Codex round 4).
/// - <see cref="ImageActivationAllowed"/> — FAIL-CLOSED for the costly/irreversible
///   image action: a trigger-selected image generation fires only when the trigger
///   boundary-matches the RAW text AND the raw remainder carries a real description
///   word (one not part of any hint term or the trigger). Deliberately over-blocks
///   the ambiguous case (e.g. a "description" made only of hint-word fragments) —
///   the Retry escape hatch makes over-blocking cheap.
///
/// All matching is token-based (lowercased runs split only by whitespace/ASR
/// punctuation), so inserted punctuation and the exact comma joins never matter,
/// while symbol-bearing vocabulary ("C++", "C#") stays distinct.
/// </summary>
public static class TriggerEchoGate
{
    /// <summary>
    /// True when <paramref name="rawTranscript"/> is a contiguous run of complete
    /// <paramref name="includedTerms"/> (each term matched whole, in order) — the
    /// shape of a bias-prompt echo. Meaningful for any transport that puts hint text in front of
    /// a GENERATIVE model — <c>GenerativePrompt</c> (Whisper/Groq) and, since gpt-transcribe,
    /// <c>StructuredKeywords</c>; the caller gates on that and passes an empty term list
    /// otherwise.
    ///
    /// <para>The rule was derived from the shape a comma-joined Whisper prompt echoes in:
    /// contiguous, complete, in order. A <c>keywords[]</c> echo has never been observed, so
    /// whether it takes that same shape — rather than, say, one keyword repeated or keywords
    /// scattered among real words — is genuinely unknown. Treat a pass here as "did not match the
    /// known echo shape", not as "is not an echo".</para>
    /// </summary>
    public static bool IsLikelyGenerativeEcho(string? rawTranscript, IReadOnlyList<string>? includedTerms)
    {
        var text = Tokenize(rawTranscript);
        if (text.Count == 0 || includedTerms == null || includedTerms.Count == 0)
            return false;

        var termTokens = new List<List<string>>(includedTerms.Count);
        foreach (var term in includedTerms)
        {
            var toks = Tokenize(term);
            if (toks.Count > 0)
                termTokens.Add(toks);
        }
        if (termTokens.Count == 0)
            return false;

        // Try every start term: greedily consume complete terms and see if the run
        // exactly covers the transcript (complete-term alignment at BOTH ends).
        for (var start = 0; start < termTokens.Count; start++)
        {
            var pos = 0;
            var k = start;
            var matched = true;
            while (pos < text.Count && k < termTokens.Count)
            {
                var term = termTokens[k];
                if (pos + term.Count > text.Count) { matched = false; break; }
                for (var i = 0; i < term.Count; i++)
                {
                    if (!string.Equals(text[pos + i], term[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = false;
                        break;
                    }
                }
                if (!matched) break;
                pos += term.Count;
                k++;
            }
            if (matched && pos == text.Count)
                return true; // consumed the whole transcript as complete terms
        }
        return false;
    }

    /// <summary>
    /// FAIL-CLOSED gate for a TRIGGER-SELECTED image generation (hotkey / App-Mode /
    /// globally-active image prompts don't call this — they need no spoken trigger).
    /// Returns true only when <paramref name="detectedTrigger"/> boundary-matches the
    /// RAW transcript AND the raw remainder (after stripping that trigger) contains
    /// at least one token that appears in NO <paramref name="hintTerms"/> entry and
    /// not in the trigger — a word the recognizer wasn't biased toward, i.e. a real
    /// description. TOKEN-UNION cover, deliberately stricter than the terminal echo
    /// gate's complete-term rule (Codex round 4 reversed round 1's partial-word
    /// relaxation): a remainder built only from hint fragments — full terms, partial
    /// multiword terms, or keyterm-biased words — is ambiguous, and for the costly
    /// irreversible action ambiguity refuses. Over-blocking is cheap: the blocked
    /// path arms Retry, whose re-run bypasses these gates. The caller passes the
    /// FULL composed hint list on every transport — bias-induced false recognition
    /// is not exclusive to generative prompts. False ⇒ refuse the activation.
    /// </summary>
    /// <summary>The connector words a LEADING image trigger naturally attracts
    /// ("generate an image OF/SHOWING/ABOUT …"). SHARED single source (Codex round 5):
    /// the detection strip, the trigger-only pre-scan, and the image gate must agree,
    /// or a stripped connector re-appears elsewhere as phantom content. "for" is
    /// deliberately absent — it marks purpose, not content.</summary>
    internal static readonly HashSet<string> LeadingImageConnectors =
        new(StringComparer.Ordinal) { "of", "showing", "depicting", "about", "with" };

    public static bool ImageActivationAllowed(string? rawTranscript, string? detectedTrigger, IReadOnlyList<string>? hintTerms)
    {
        if (string.IsNullOrWhiteSpace(detectedTrigger))
            return false;

        var remainder = TryStripTriggerFromRaw(rawTranscript, detectedTrigger!);
        if (remainder == null)
            return false; // trigger not boundary-present in RAW (e.g. word-replacement created it) → fail closed

        var remainderWords = Tokenize(remainder);
        // A leading connector is part of the trigger PHRASE, not the description —
        // without this, "generate an image of VoiceWink" passed because "of" looked
        // like a novel description word while the real residue was entirely
        // hint-derived (Codex round 5 High).
        if (remainderWords.Count > 0 && LeadingImageConnectors.Contains(remainderWords[0]))
            remainderWords.RemoveAt(0);
        if (remainderWords.Count == 0)
            return false; // nothing but the trigger (+ connector) → no description

        var coverWords = new HashSet<string>(StringComparer.Ordinal);
        if (hintTerms != null)
            foreach (var term in hintTerms)
                foreach (var w in Tokenize(term))
                    coverWords.Add(w);
        foreach (var w in Tokenize(detectedTrigger))
            coverWords.Add(w);

        return remainderWords.Any(w => !coverWords.Contains(w));
    }

    /// <summary>
    /// Strip <paramref name="trigger"/> from <paramref name="rawText"/> at a word
    /// boundary (leading or trailing) and return the remainder, or null when the
    /// trigger is not boundary-present. Token-based so punctuation is irrelevant.
    /// </summary>
    internal static string? TryStripTriggerFromRaw(string? rawText, string trigger)
    {
        var textTokens = Tokenize(rawText);
        var trigTokens = Tokenize(trigger);
        if (trigTokens.Count == 0 || textTokens.Count < trigTokens.Count)
            return null;

        // Leading match.
        if (SequenceMatchesAt(textTokens, trigTokens, 0))
            return string.Join(" ", textTokens.Skip(trigTokens.Count));
        // Trailing match.
        var tailStart = textTokens.Count - trigTokens.Count;
        if (SequenceMatchesAt(textTokens, trigTokens, tailStart))
            return string.Join(" ", textTokens.Take(tailStart));

        return null;
    }

    private static bool SequenceMatchesAt(List<string> text, List<string> seq, int at)
    {
        for (var i = 0; i < seq.Count; i++)
            if (!string.Equals(text[at + i], seq[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static List<string> Tokenize(string? value)
    {
        var spans = TokenizeSpans(value);
        var tokens = new List<string>(spans.Count);
        foreach (var s in spans)
            tokens.Add(s.Token);
        return tokens;
    }

    /// <summary>
    /// A lowercased alphanumeric word with its [Start, End) char range in the source
    /// string — lets a matcher find token positions and then SLICE the original text,
    /// preserving the remainder's exact punctuation/casing.
    /// </summary>
    internal readonly record struct TokenSpan(string Token, int Start, int End);

    // The ONLY characters that split tokens: whitespace plus the punctuation an ASR
    // actually inserts around/inside spoken phrases (incl. hyphen — Whisper
    // hyphenates compounds like "push-to-talk"). Every OTHER symbol BINDS into its
    // token (Codex round 4: splitting on any non-alphanumeric collapsed "C++",
    // "C#", and "C" into the same token, silently colliding distinct vocabulary).
    private static bool IsTokenSeparator(char ch)
        => char.IsWhiteSpace(ch) || ",.!?;:…—–-'\"“”‘’()[]¿¡".IndexOf(ch) >= 0;

    /// <summary>
    /// Tokenize with character spans. Shared with <c>PromptDetectionService</c>'s
    /// trigger matcher (UAT 2026-07-18: literal substring matching failed on
    /// transcription-inserted punctuation — "Hey, assistant." never matched the
    /// trigger "hey assistant"); token matching makes ASR punctuation irrelevant
    /// while span slicing hands back the original remainder text (which the matcher
    /// then trims/capitalizes like before).
    /// </summary>
    internal static List<TokenSpan> TokenizeSpans(string? value)
    {
        var tokens = new List<TokenSpan>();
        if (string.IsNullOrEmpty(value))
            return tokens;
        var current = new StringBuilder();
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!IsTokenSeparator(ch))
            {
                if (current.Length == 0)
                    start = i;
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(new TokenSpan(current.ToString(), start, i));
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(new TokenSpan(current.ToString(), start, value.Length));
        return tokens;
    }
}
