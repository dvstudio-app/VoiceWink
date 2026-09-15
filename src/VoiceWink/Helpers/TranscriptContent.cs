namespace VoiceWink.Helpers;

/// <summary>
/// Is there anything in this transcript worth acting on?
///
/// <para><b>Born from a live incident (2026-08-03).</b> A Bluetooth headset lying on a desk produced
/// ~3 s of sustained −30..−45 dBFS room noise. Silero scored it as speech (correctly, in the sense
/// that it is indistinguishable at that level — VoiceWink's own whisper corpus sits LOWER, at
/// −46..−59 dBFS, which is why AUD-2 exists and why tightening the VAD is the wrong lever), Whisper
/// transcribed it as a single <c>","</c>, and that comma was sent to a paid enhancement provider
/// with the instruction to clean it up. There is no correct output for that request, and the model
/// did the most predictable available thing: it described its input, returning
/// "The transcript contains only a comma with no other content…" — 100 characters of commentary
/// pasted into the user's browser.</para>
///
/// <para>The pipeline's two guards both asked <c>string.IsNullOrWhiteSpace</c>, and a comma is
/// neither null nor whitespace, so it passed both. This is the missing rung: a transcript that
/// carries no CONTENT is treated as empty even when it carries characters.</para>
///
/// <para><b>Why "whitespace or punctuation", and not "contains no letter or digit".</b> The
/// letter-or-digit form is the obvious rule and it is WRONG here, because it also swallows most
/// emoji and every currency/math symbol — none of which are letters. Only whitespace and
/// punctuation are treated as contentless, which is the class a transcriber emits when it hears
/// nothing.</para>
///
/// <para><b>APPLY THIS ONLY TO TRANSCRIBER OUTPUT AND THE MACHINE-OWNED STAGES THAT FOLLOW IT.</b>
/// The three production callers, all on that side of the line: <c>MainViewModel</c> on the RAW
/// transcript before anything touches it, <c>TextPipelineRunner</c> after the
/// filter/filler/formatter stages and before <c>WordReplacementService</c>, and the equivalent
/// point in <c>AudioTranscribePage</c> (which re-implements those stages inline rather than
/// calling the runner — a pre-existing divergence).</para>
///
/// <para><b>Two places it must NOT run, each for its own reason.</b></para>
///
/// <para>(1) <b>After <c>WordReplacementService</c></b>, which applies USER-authored replacements
/// with no charset restriction: a mapping like <c>comma -&gt; ,</c> is supported and its output is
/// punctuation-only on purpose. Discarding that made the dictation permanently un-actionable,
/// because retry re-runs the replacement and produces the same result.</para>
///
/// <para>(2) <b>On ENHANCEMENT-MODEL output.</b> An earlier version of this comment recommended
/// exactly that, on the reasoning that model output is machine-authored — which is only half true.
/// The PROMPT is the user's, and a custom prompt may legitimately ask for a punctuation-only
/// result (extract the punctuation, emit Morse, return an emoticon). Rejecting those silently
/// restores the pre-enhancement transcript and destroys a correct answer. Those four call sites
/// stay on <c>string.IsNullOrWhiteSpace</c>; see <c>AIEnhancementService</c> for the note at the
/// site. Both of these were caught at diff review, one round apart.</para>
///
/// <para><b>The emoji rationale, corrected.</b> An earlier version of this comment claimed emoji are
/// always symbols. They are not: <c>‼</c> (U+203C), <c>⁉</c> (U+2049) and <c>〰</c> (U+3030) are
/// emoji-presentable yet classify as PUNCTUATION, so they read as empty here. That is the right
/// answer for machine output — a transcriber emitting a lone <c>‼</c> has heard nothing — and it is
/// only safe because this no longer runs after user replacements.</para>
///
/// <para>Invisible characters are contentless too. <c>char.IsWhiteSpace</c> is false for zero-width
/// space (U+200B) and friends, which are <c>Format</c>; control characters are likewise neither
/// whitespace nor punctuation. Without them a transcript of pure invisibles would read as content
/// and reach a provider.</para>
///
/// <para>Deliberately NOT a length or confidence heuristic. "One character" would reject a genuine
/// single-letter dictation; a no-speech probability is the VAD's job and it already ran. This asks
/// one narrow question about the STRING, so it cannot misjudge audio it never saw.</para>
/// </summary>
internal static class TranscriptContent
{
    /// <summary>True when <paramref name="text"/> carries nothing to transcribe, enhance, or paste:
    /// null, empty, or composed entirely of whitespace, punctuation, and invisibles.
    ///
    /// <para>Unicode-aware across the whole range, and the iteration unit is what makes that true.
    /// It walks RUNES, not <c>char</c>: a <c>char</c> loop sees UTF-16 code units, so an astral
    /// character arrives as two surrogate halves (category <c>Cs</c>) that match no contentless
    /// class — which made every supplementary code point read as content by accident. An earlier
    /// version of this comment described that as intentional. It was not: supplementary
    /// PUNCTUATION (U+10100) and FORMAT (U+E0001) are contentless and now classify correctly, while
    /// emoji remain content because they are <c>So</c>, on their merits.</para>
    ///
    /// <para>Scripts are safe by the same mechanism — CJK, Cyrillic and Arabic letters are letters
    /// in every category test here, so a non-Latin transcript is never mistaken for empty.</para></summary>
    internal static bool IsEmpty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // RUNES, not chars. Iterating `char` walks UTF-16 CODE UNITS, so an astral character
        // arrives as two surrogate halves — category Cs, which matches none of the contentless
        // classes, making every supplementary character read as content by accident. That was
        // right for emoji and wrong for everything else: supplementary PUNCTUATION (U+10100
        // AEGEAN WORD SEPARATOR LINE) and supplementary FORMAT (U+E0001 LANGUAGE TAG) bypassed
        // the rule while the doc claimed to be Unicode-aware (Codex diff review r3). Runes make
        // the claim true; emoji still count as content because they are So, on their merits.
        foreach (var rune in text!.EnumerateRunes())
        {
            if (!IsContentless(rune)) return false;
        }

        return true;
    }

    /// <summary>One code point's verdict. Format and Control join whitespace and punctuation
    /// because they render as nothing — a zero-width space is not whitespace to
    /// <c>Rune.IsWhiteSpace</c> (it is <c>Cf</c>), so without this a string of invisibles would
    /// count as content.</summary>
    private static bool IsContentless(global::System.Text.Rune rune)
    {
        if (global::System.Text.Rune.IsWhiteSpace(rune)) return true;

        var category = global::System.Text.Rune.GetUnicodeCategory(rune);
        return category is global::System.Globalization.UnicodeCategory.Control
            or global::System.Globalization.UnicodeCategory.Format
            or global::System.Globalization.UnicodeCategory.ConnectorPunctuation
            or global::System.Globalization.UnicodeCategory.DashPunctuation
            or global::System.Globalization.UnicodeCategory.OpenPunctuation
            or global::System.Globalization.UnicodeCategory.ClosePunctuation
            or global::System.Globalization.UnicodeCategory.InitialQuotePunctuation
            or global::System.Globalization.UnicodeCategory.FinalQuotePunctuation
            or global::System.Globalization.UnicodeCategory.OtherPunctuation;
    }
}
