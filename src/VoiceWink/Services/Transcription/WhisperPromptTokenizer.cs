using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace VoiceWink.Services.Transcription;

/// <summary>Which Whisper vocabulary a model uses. Multilingual (multilingual.tiktoken)
/// for all standard + Groq models; English (gpt2.tiktoken) for the ggml-*.en family.
/// The two rank tables produce DIFFERENT counts for the same text.</summary>
public enum WhisperVocab { Multilingual, English }

/// <summary>
/// The ACTUAL Whisper tokenizer (PRM-4 follow-up, owner request 2026-07-18): exact
/// byte-level BPE over OpenAI's shipped rank files (<c>Assets/Tokenizer/*.tiktoken</c>,
/// MIT — see THIRD-PARTY-NOTICES), with openai/whisper's pre-tokenization regex. Lets
/// the bias-prompt budget count REAL tokens (~3–5 bytes each for typical vocabulary)
/// instead of the absolute-worst-case 1-byte-per-token proxy that fit only 16 of the
/// owner's 31 hint terms. Counting must never UNDER-count (the provider would
/// truncate and desynchronize the echo gate), so:
///
/// - the golden tests pin counts byte-for-byte against reference `tiktoken` output
///   built exactly like openai/whisper's tokenizer.py;
/// - <see cref="TryCountTokens"/> returns null (callers fall back to the byte budget)
///   when the rank file is unavailable OR the text contains a non-BMP LETTER/DIGIT —
///   the one documented spot where .NET's UTF-16 regex classes diverge from the Rust
///   engine tiktoken uses (astral letters classify as surrogates, splitting pieces
///   differently). Emoji and symbols classify identically and stay on the exact path.
/// </summary>
public static class WhisperPromptTokenizer
{
    private static ILogger Logger => Log.ForContext(typeof(WhisperPromptTokenizer));

    // openai/whisper tokenizer.py pat_str, verbatim. CultureInvariant: \p classes are
    // Unicode-property based in .NET; no culture-sensitive constructs are used.
    private static readonly Regex PreTokenizer = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        global::System.TimeSpan.FromSeconds(5));

    /// <summary>Test seam: directory containing the .tiktoken rank files. Defaults to
    /// the shipped <c>Assets/Tokenizer</c> next to the executable.</summary>
    internal static string RanksDirectory { get; set; } =
        Path.Combine(global::System.AppContext.BaseDirectory, "Assets", "Tokenizer");

    private static readonly object LoadLock = new();
    private static Dictionary<byte[], int>? _multilingualRanks;
    private static Dictionary<byte[], int>? _englishRanks;
    private static bool _multilingualLoadFailed;
    private static bool _englishLoadFailed;

    /// <summary>
    /// Which vocabulary a model name/path uses. The English-only ggml family carries
    /// an <c>.en</c> segment (<c>ggml-base.en</c>, <c>ggml-small.en</c>); everything
    /// else — multilingual ggml models and every cloud Whisper id — uses the
    /// multilingual table. Deterministic on the SAME model string the caller used
    /// to resolve the transcriber, so the pipeline's echo set and the client's wire
    /// prompt can never disagree.
    ///
    /// <para><b>The catalogue ships no English-only row since 2026-08-03</b>, so this returns
    /// <see cref="WhisperVocab.Multilingual"/> for every shipped model today. It is still the right
    /// rule, not dead weight: it accepts a PATH, and the English rank table is golden-pinned against
    /// reference tiktoken output, so the branch stays correct for a restored <c>.en</c> row or a
    /// hand-placed file. (This comment has now been wrong in BOTH directions — it once said
    /// "VoiceWink ships none" when the q8 English builds existed, then said the opposite after they
    /// were removed. The rule below is the thing to read; the prose keeps rotting.)</para>
    ///
    /// <para>The test itself lives in <see cref="Helpers.EnglishOnlyModelNaming.IsEnglishOnly"/> —
    /// the ONE definition, shared with <c>EffectiveTranscriptionLanguage</c>.</para>
    ///
    /// <para>This site used a bare <c>Contains(".en")</c>, which happened to answer correctly for
    /// the new names and was still the last rule not delegating (Kimi diff review). Bare Contains
    /// is also broader than intended: it would classify a hypothetical <c>ggml-v.enhanced</c> as
    /// English. Accepts a full PATH as well as a name, so matching stays substring-based on the
    /// segment rule rather than anchored to the end of the string.</para>
    /// </summary>
    public static WhisperVocab VariantFor(string? modelNameOrPath)
    {
        if (modelNameOrPath == null) return WhisperVocab.Multilingual;

        // Strip a trailing ".bin" SPECIFICALLY — never Path.GetFileNameWithoutExtension, which
        // treats ".en" as the extension and turns "ggml-base.en" into "ggml-base", i.e. reports
        // an English-only model as multilingual. That is the exact defect this delegation exists
        // to prevent, and the first version of this line shipped it; the existing tokenizer test
        // caught it.
        var name = global::System.IO.Path.GetFileName(modelNameOrPath);
        if (name.EndsWith(".bin", global::System.StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return Helpers.EnglishOnlyModelNaming.IsEnglishOnly(name)
            ? WhisperVocab.English
            : WhisperVocab.Multilingual;
    }

    /// <summary>
    /// Exact Whisper token count for <paramref name="text"/>, or null when exact
    /// counting is unavailable (missing/corrupt rank file) or not provably identical
    /// to the reference tokenizer (astral letter/digit present) — callers must fall
    /// back to a byte-budget upper bound. Empty text counts as 0.
    /// </summary>
    public static int? TryCountTokens(string text, WhisperVocab vocab)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (ContainsAstralLetterOrDigit(text))
            return null;

        var ranks = GetRanks(vocab);
        if (ranks == null)
            return null;

        var count = 0;
        foreach (Match piece in PreTokenizer.Matches(text))
        {
            if (piece.Length == 0)
                continue;
            count += CountPieceTokens(Encoding.UTF8.GetBytes(piece.Value), ranks);
        }
        return count;
    }

    /// <summary>True when the text contains a non-BMP code point whose category is a
    /// LETTER or NUMBER — the classes where .NET's surrogate-based regex splits
    /// pieces differently from the reference engine. Emoji/symbols (So etc.) split
    /// identically and return false.</summary>
    internal static bool ContainsAstralLetterOrDigit(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                continue;
            var category = global::System.Globalization.CharUnicodeInfo.GetUnicodeCategory(
                char.ConvertToUtf32(text[i], text[i + 1]));
            switch (category)
            {
                case global::System.Globalization.UnicodeCategory.UppercaseLetter:
                case global::System.Globalization.UnicodeCategory.LowercaseLetter:
                case global::System.Globalization.UnicodeCategory.TitlecaseLetter:
                case global::System.Globalization.UnicodeCategory.ModifierLetter:
                case global::System.Globalization.UnicodeCategory.OtherLetter:
                case global::System.Globalization.UnicodeCategory.DecimalDigitNumber:
                case global::System.Globalization.UnicodeCategory.LetterNumber:
                case global::System.Globalization.UnicodeCategory.OtherNumber:
                    return true;
            }
            i++; // consumed the pair
        }
        return false;
    }

    /// <summary>Standard byte-level BPE: start from single bytes, repeatedly merge the
    /// adjacent pair with the LOWEST rank until no mergeable pair remains; the number
    /// of remaining parts is the token count. Pieces are short (words), so the O(n²)
    /// scan per merge is negligible.</summary>
    private static int CountPieceTokens(byte[] piece, Dictionary<byte[], int> ranks)
    {
        if (piece.Length == 0)
            return 0;
        if (piece.Length == 1 || ranks.ContainsKey(piece))
            return 1;

        // parts[i] = (start, length) into piece.
        var parts = new List<(int Start, int Length)>(piece.Length);
        for (var i = 0; i < piece.Length; i++)
            parts.Add((i, 1));

        while (parts.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < parts.Count - 1; i++)
            {
                var length = parts[i].Length + parts[i + 1].Length;
                var key = new byte[length];
                global::System.Array.Copy(piece, parts[i].Start, key, 0, length);
                if (ranks.TryGetValue(key, out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
                break;
            parts[bestIndex] = (parts[bestIndex].Start, parts[bestIndex].Length + parts[bestIndex + 1].Length);
            parts.RemoveAt(bestIndex + 1);
        }
        return parts.Count;
    }

    private static Dictionary<byte[], int>? GetRanks(WhisperVocab vocab)
    {
        lock (LoadLock)
        {
            if (vocab == WhisperVocab.Multilingual)
            {
                if (_multilingualRanks == null && !_multilingualLoadFailed)
                    _multilingualRanks = TryLoad("multilingual.tiktoken", ref _multilingualLoadFailed);
                return _multilingualRanks;
            }
            if (_englishRanks == null && !_englishLoadFailed)
                _englishRanks = TryLoad("gpt2.tiktoken", ref _englishLoadFailed);
            return _englishRanks;
        }
    }

    /// <summary>Test seam: drop cached rank tables so a changed <see cref="RanksDirectory"/>
    /// takes effect.</summary>
    internal static void ResetCacheForTests()
    {
        lock (LoadLock)
        {
            _multilingualRanks = null;
            _englishRanks = null;
            _multilingualLoadFailed = false;
            _englishLoadFailed = false;
        }
    }

    private static Dictionary<byte[], int>? TryLoad(string fileName, ref bool loadFailed)
    {
        try
        {
            var path = Path.Combine(RanksDirectory, fileName);
            var ranks = new Dictionary<byte[], int>(52000, ByteArrayComparer.Instance);
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var space = line.IndexOf(' ');
                if (space <= 0)
                    continue;
                var b64 = line[..space];
                // Upstream quirk: multilingual.tiktoken's last line is "= 50256" — an
                // EMPTY token (reference tiktoken decodes it leniently to b""). An
                // empty key can never participate in a merge or a whole-piece lookup,
                // so skipping it is count-equivalent; .NET's strict decoder would
                // otherwise throw and needlessly kill the exact path.
                if (b64.AsSpan().IndexOfAnyExcept('=') < 0)
                    continue;
                ranks[global::System.Convert.FromBase64String(b64)] =
                    int.Parse(line[(space + 1)..], global::System.Globalization.CultureInfo.InvariantCulture);
            }
            if (ranks.Count < 50000)
                throw new InvalidDataException($"rank table suspiciously small ({ranks.Count})");
            return ranks;
        }
        catch (global::System.Exception ex)
        {
            // Fail-SOFT to the byte budget (never fail-open): a missing asset must
            // not break transcription, and the byte path keeps the token guarantee.
            loadFailed = true;
            Logger.Warning("Whisper tokenizer ranks unavailable ({File}): {Error} — falling back to byte budget",
                fileName, ex.Message);
            return null;
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null || x.Length != y.Length) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            // FNV-1a
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in obj)
                    hash = (hash ^ b) * 16777619;
                return hash;
            }
        }
    }
}
