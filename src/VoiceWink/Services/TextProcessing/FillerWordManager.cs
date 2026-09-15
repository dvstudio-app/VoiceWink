using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Removes filler words (uh, um, hmm, etc.) from transcribed text.
/// Pipeline step 2: TranscriptionOutputFilter → FillerWordManager.
///
/// <para><b>The list is English, so the removal is gated on the recognition language</b>
/// (2026-09-13). The filter is whole-word and case-insensitive, and the pipeline runs it on every
/// transcript — so until this gate existed, a Dutch or German dictation lost every "er"
/// ("Er is een probleem" → "Is een probleem", "Er kommt morgen" → "Kommt morgen") and a French one
/// its "eh", from a default the user never chose. The rule (<see cref="AppliesTo"/>): an English
/// or UNKNOWN language (null, empty, <c>auto</c>) applies the list; any other explicit language
/// skips the stage. Unknown still applies because the engines never hand the detected language
/// back to this pipeline, and the list itself was trimmed to entries that are fillers everywhere
/// (<see cref="AppDefaults.DefaultFillerWords"/> carries the per-word reasoning, including the one
/// residual, German "um" under Auto-detect).</para>
/// </summary>
public sealed class FillerWordManager
{
    private static ILogger Logger => Log.ForContext<FillerWordManager>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SentenceStartRegex = new(@"(?<=^|[.!?]\s+)[a-z]", RegexOptions.Compiled, RegexTimeout);

    private readonly SettingsService _settings;
    private readonly object _regexLock = new();
    private Regex? _fillerRegex;
    private string? _lastWordList;

    public FillerWordManager(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Whether filler removal runs for a transcript recognised as <paramref name="language"/>
    /// (a whisper.cpp-style code — <c>en</c>, <c>nl</c>, <c>auto</c> — or null when the caller
    /// has none). Pure; the case table lives in <c>FillerWordManagerTests</c>.
    /// </summary>
    /// <remarks>
    /// English applies, including a regioned form (<c>en-US</c>). Unknown applies: null, empty,
    /// whitespace and <c>auto</c> — the detected language never reaches this stage, so refusing
    /// here would switch the feature off for every Auto-detect user, English ones included. Any
    /// other explicit code skips: the list is English and its entries are ordinary words in
    /// other languages. Trimmed and case-insensitive, like every other language comparison in
    /// the app (an imported <c>"NL"</c> must not slip past the gate).
    /// </remarks>
    internal static bool AppliesTo(string? language)
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
    /// Remove the configured fillers from <paramref name="text"/> when the stage applies to
    /// <paramref name="language"/> (see <see cref="AppliesTo"/>; null = unknown = applies).
    /// </summary>
    public string Filter(string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var removeFillers = _settings.GetBool(AppDefaults.RemoveFillerWords, true);
        if (!removeFillers)
            return text;

        if (!AppliesTo(language))
        {
            Logger.Debug("Filler removal skipped for recognition language {Language}", language);
            return text;
        }

        var wordList = _settings.GetString(AppDefaults.FillerWords, AppDefaults.DefaultFillerWords);

        Regex? regex;
        lock (_regexLock)
        {
            EnsureRegex(wordList);
            regex = _fillerRegex;
        }

        if (regex == null)
            return text;

        var result = regex.Replace(text, " ");

        // Collapse multiple spaces
        result = MultiSpaceRegex.Replace(result, " ").Trim();

        // Fix capitalization after removal at sentence start
        result = SentenceStartRegex.Replace(result, m => m.Value.ToUpperInvariant());

        if (result != text.Trim())
        {
            Logger.Debug("Filler words removed: {OrigLen} → {NewLen} chars", text.Length, result.Length);
        }

        return result;
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

        // Build word-boundary regex: \b(word1|word2)\b with case insensitive
        var escaped = words.Select(Regex.Escape);
        var pattern = $@"\b({string.Join("|", escaped)})\b";

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
