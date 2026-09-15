namespace VoiceWink.Helpers;

/// <summary>
/// DCT-1 (2026-09-13) — the vocabulary VoiceWink puts in every user's Dictionary before they add
/// a word of their own: the product's own name, the providers a user configures keys for, the
/// local models by their display names, and three replacement rules. Pure data plus one
/// membership test; <see cref="Services.Data.DefaultDictionarySeed"/> is what writes it.
///
/// <para><b>What earns a place here.</b> A vocabulary word must be a proper noun the recognizers
/// do NOT already spell — ordinary English ("hotkey", "push-to-talk") boosts nothing as a keyterm
/// and dilutes the enhancement prompt's vocabulary block, so it is left out. Bare common-word
/// model names ("Whisper", "Nova") are left out for the same reason with a sharper edge: the
/// enhancement block asks the model to prefer a listed spelling for similar-sounding words, and
/// "a whisper" sounds identical. The full display names are safe. <b>"Parakeet" is the one
/// common-word name kept</b>: it is the default engine, so it is the model name users actually
/// say, and the cost is a capital letter on a bird. <b>Two provider names are deliberately
/// absent because they have an EXACT homophone that is a different word</b> — a keyterm-biased
/// recognizer spells the homophone the listed way, and the enhancement block pushes the same
/// rewrite: "Claude" would pull "cloud", and "Groq" would pull "Grok" (another AI product this
/// user base dictates about) and the verb "grok" (self-review lens B, 2026-09-13). A user with a
/// key for either adds the word once; the owner keeps both in their PERSONAL dictionary.</para>
///
/// <para><b>Replacements are held to a stricter bar</b> because they rewrite every user's every
/// transcript, on every engine: an original must have NO natural-language reading, so nothing
/// correctly dictated is ever changed. "open AI" and "open router" failed that bar ("an open AI
/// ecosystem", "an open router"); "eleven labs"/"11 labs" failed it on a count; "VoiceInk" is a
/// different product; and <b>"deep gram" failed it on clinical dictation</b> — "a deep
/// gram-negative infection" would become "a Deepgram-negative infection", because "-" is a word
/// boundary (self-review lens B). The rules that passed are evidence-based, not guessed: one week
/// of the owner's Parakeet history (551 rows, 2026-09-06 → 13) produced "VoiceWink" correctly 2
/// times in 26 — the dominant mishearing is "wing" (18 rows: Voicewing / voice wing / VoiceWing /
/// Voice Wing), then "voice wink" (6); "DeepGram" appeared once (the casing rule); "Parkit" /
/// "Parakit" are the owner's own rules of 2026-08-23/30 for a Flemish-accented "Parakeet".</para>
///
/// <para><b>Vocabulary only reaches SOME engines</b>, which is why the replacements matter more
/// than the list: Whisper ignores hints (TRN-11), Parakeet has no hint transport, so the words
/// bias only Deepgram / ElevenLabs keyterms and gpt-transcribe keywords — and the enhancement
/// prompt's vocabulary block, on every provider. A replacement corrects the output of every
/// engine.</para>
/// </summary>
public static class DefaultDictionary
{
    /// <summary>Vocabulary words, in the order they are offered. Each is a proper noun the app
    /// itself puts in front of the user — the product, the providers, the local models.</summary>
    public static readonly IReadOnlyList<string> VocabularyWords = new[]
    {
        // Product
        "VoiceWink",
        "DV Studio",
        // Providers — the names a user configures an API key for (transcription + AI enhancement).
        // Not "Groq" (homophone of Grok / grok) and not "Claude" (homophone of cloud) — see above.
        "OpenAI",
        "Anthropic",
        "Gemini",
        "Deepgram",
        "ElevenLabs",
        "Mistral",
        "OpenRouter",
        "Cerebras",
        // Local models — the display names the Models page shows, never the file stems.
        // Pinned against PredefinedModels + ParakeetCatalog by DefaultDictionaryTests so a
        // renamed or added local row cannot leave this list stale.
        "Parakeet",
        "Whisper Tiny",
        "Whisper Base",
        "Whisper Small",
        "Whisper Medium",
        "Whisper Large V3 Turbo",
        // The app's own term for its key model; recognizers spell it "B Y O K" / "buy OK"
        "BYOK",
    };

    /// <summary>One shipped replacement rule — the same shape as the user's own rows:
    /// comma-separated original variants, matched whole-word and case-insensitively by
    /// <see cref="Services.TextProcessing.WordReplacementService"/>.</summary>
    public sealed record ReplacementRule(string OriginalText, string ReplacementText);

    /// <summary>The three replacement rules. Every original variant is a string with no
    /// natural-language reading (see the type comment for the ones that failed that bar). The
    /// exact correct form ("voicewink" ↔ "VoiceWink") is included on purpose: the regex is
    /// case-insensitive, so it also normalises "Voicewink" and "DeepGram". A rule's IDENTITY for
    /// the offer-once record is its <see cref="ReplacementRule.ReplacementText"/>, never its
    /// variant list — adding an observed mishearing to a rule must not re-offer it to an install
    /// that deleted it (self-review lens A).</summary>
    public static readonly IReadOnlyList<ReplacementRule> Replacements = new[]
    {
        new ReplacementRule("voice wing, voicewing, voice-wing, voice wink, voicewink, voice-wink", "VoiceWink"),
        new ReplacementRule("deepgram", "Deepgram"),
        new ReplacementRule("parkit, parakit", "Parakeet"),
    };

    private static readonly HashSet<string> VocabularySet =
        new(VocabularyWords, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="word"/> is one of the shipped vocabulary words (case-insensitive,
    /// trimmed). <see cref="Services.Data.CustomVocabularyService.GetVocabularyContextAsync"/>
    /// uses it to rank the user's OWN words ahead of these when a provider's hint budget
    /// truncates from the tail — by membership rather than by a stored flag, so it needs no
    /// column and gives the same answer whether the row was seeded or typed by the user (an
    /// identical string ranks the same either way, which is the only case where the two could
    /// differ).
    /// </summary>
    public static bool IsDefaultWord(string? word)
        => !string.IsNullOrWhiteSpace(word) && VocabularySet.Contains(word.Trim());
}
