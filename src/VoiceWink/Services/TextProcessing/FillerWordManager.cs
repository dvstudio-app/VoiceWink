using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Removes filler words (uh, um, hmm, etc.) from transcribed text.
/// Pipeline step 2: TranscriptionOutputFilter → FillerWordManager.
///
/// <para><b>Which words are removed depends on the recognition language</b>
/// (<see cref="EffectiveWords"/>). The filter is whole-word and case-insensitive, so a list entry
/// that is a real word in the dictated language deletes that word: until 2026-09-13 the default
/// carried "er" and "eh", and a Dutch or German dictation lost every "er" ("Er is een probleem" →
/// "Is een probleem"). The list was then trimmed to sounds (<see cref="AppDefaults.DefaultFillerWords"/>),
/// and since 2026-09-30 (owner) those sounds are removed in EVERY language, minus <c>um</c> where it
/// is a word — German ("um acht"), Portuguese ("um carro"), Icelandic, Faroese and Luxembourgish. A list holding any entry the shipped
/// list does not is the user's own and is applied only for English or an unknown language, the rule
/// every list followed before: the app cannot know its entries are sounds everywhere.</para>
/// </summary>
public sealed class FillerWordManager
{
    private static ILogger Logger => Log.ForContext<FillerWordManager>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly object _regexLock = new();
    private Regex? _fillerRegex;
    private string? _lastWordList;

    public FillerWordManager(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Languages in which a shipped filler is an ordinary word, and that word.</summary>
    private static readonly (string Language, string Word)[] ShippedWordCollisions =
    [
        ("de", "um"),   // "um acht" — at eight
        ("pt", "um"),   // "um carro" — a car
        ("is", "um"),   // Icelandic — about
        ("fo", "um"),   // Faroese — about; if
        ("lb", "um"),   // Luxembourgish — on the
    ];

    /// <summary>
    /// The fillers removed from a transcript recognised as <paramref name="language"/> (a
    /// whisper.cpp-style code — <c>en</c>, <c>nl</c>, <c>auto</c> — or null when the caller has
    /// none), given the configured comma-separated <paramref name="wordList"/>. Empty = the stage
    /// does nothing. Pure; the case table lives in <c>FillerWordManagerTests</c>.
    /// </summary>
    /// <remarks>
    /// English or unknown (<see cref="IsEnglishOrUnknown"/>): the whole list. Any other language:
    /// a list made only of shipped entries (any subset, order or case) minus that language's
    /// <see cref="ShippedWordCollisions"/>; a list with any other entry, nothing — the user's own
    /// words may be real words in that language.
    /// </remarks>
    internal static string[] EffectiveWords(string? language, string wordList)
    {
        var words = wordList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0)
            .ToArray();
        if (IsEnglishOrUnknown(language))
            return words;

        var shipped = AppDefaults.DefaultFillerWords.Split(',', StringSplitOptions.TrimEntries);
        if (!words.All(w => shipped.Contains(w, StringComparer.OrdinalIgnoreCase)))
            return [];

        var baseCode = language!.Trim().Replace('_', '-').Split('-')[0];
        return words
            .Where(w => !ShippedWordCollisions.Any(c =>
                c.Language.Equals(baseCode, StringComparison.OrdinalIgnoreCase)
                && c.Word.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// English, including a regioned form (<c>en-US</c>), or unknown: null, empty, whitespace and
    /// <c>auto</c> — the detected language never reaches this stage. Trimmed and case-insensitive,
    /// like every other language comparison in the app (an imported <c>"NL"</c> is not English).
    /// </summary>
    internal static bool IsEnglishOrUnknown(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return true;
        var code = language.Trim();
        if (code.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return true;
        return code.Equals("en", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remove the fillers that apply to <paramref name="language"/> from <paramref name="text"/>
    /// (see <see cref="EffectiveWords"/>; null = unknown).
    /// </summary>
    public string Filter(string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var removeFillers = _settings.GetBool(AppDefaults.RemoveFillerWords, true);
        if (!removeFillers)
            return text;

        var words = EffectiveWords(language,
            _settings.GetString(AppDefaults.FillerWords, AppDefaults.DefaultFillerWords));
        if (words.Length == 0)
        {
            Logger.Debug("Filler removal skipped for recognition language {Language}", language);
            return text;
        }
        var wordList = string.Join(",", words);

        Regex? regex;
        lock (_regexLock)
        {
            EnsureRegex(wordList);
            regex = _fillerRegex;
        }

        if (regex == null)
            return text;

        var result = TranscriptionOutputFilter.CollapseHorizontalSpace(RemoveFillers(regex, text));

        if (result != text.Trim())
        {
            Logger.Debug("Filler words removed: {OrigLen} → {NewLen} chars", text.Length, result.Length);
        }

        return result;
    }

    /// <summary>Removes every filler; only the word after a filler that opened a sentence is capitalised (TRN-94).</summary>
    private static string RemoveFillers(Regex regex, string text)
    {
        var sb = new StringBuilder(text.Length);
        var capitalizeNext = false;
        var last = 0;
        foreach (Match m in regex.Matches(text))
        {
            AppendSegment(sb, text.AsSpan(last, m.Index - last), ref capitalizeNext);
            if (EndsAtSentenceStart(sb))
                capitalizeNext = true;
            // A filler before . ! ? goes with no gap ("yes, um." → "yes."); elsewhere a space keeps the words apart.
            if (!m.Groups["end"].Success)
                sb.Append(' ');
            last = m.Index + m.Length;
        }
        AppendSegment(sb, text.AsSpan(last), ref capitalizeNext);
        return sb.ToString();
    }

    /// <summary>Appends <paramref name="segment"/>, upper-casing its first letter when a removed filler opened the sentence.</summary>
    private static void AppendSegment(StringBuilder sb, ReadOnlySpan<char> segment, ref bool capitalizeNext)
    {
        if (capitalizeNext)
        {
            var i = 0;
            while (i < segment.Length && char.IsWhiteSpace(segment[i]))
                i++;
            if (i < segment.Length)
            {
                capitalizeNext = false;
                if (char.IsLower(segment[i]))
                {
                    sb.Append(segment[..i]).Append(char.ToUpperInvariant(segment[i]));
                    segment = segment[(i + 1)..];
                }
            }
        }
        sb.Append(segment);
    }

    /// <summary>True when the text so far is empty or ends with . ! ? (trailing whitespace ignored).</summary>
    private static bool EndsAtSentenceStart(StringBuilder sb)
    {
        var i = sb.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i]))
            i--;
        return i < 0 || sb[i] is '.' or '!' or '?';
    }

    private void EnsureRegex(string wordList)
    {
        if (wordList == _lastWordList && _fillerRegex != null)
            return;

        _lastWordList = wordList;

        var words = wordList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0)
            .OrderByDescending(w => w.Length) // Match longer phrases first
            .ToArray();

        if (words.Length == 0)
        {
            _fillerRegex = null;
            return;
        }

        // Each match takes only the commas and gaps touching that filler, never punctuation elsewhere (TRN-93).
        var alternation = string.Join("|", words.Select(Regex.Escape));
        var pattern = $@"(?:[ \t]*,)?[ \t]*\b(?:{alternation})\b[ \t]*(?=[.!?])(?<end>)|\b(?:{alternation})\b[ \t]*,?";

        try
        {
            _fillerRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to compile filler word regex");
            _fillerRegex = null;
        }
    }
}
