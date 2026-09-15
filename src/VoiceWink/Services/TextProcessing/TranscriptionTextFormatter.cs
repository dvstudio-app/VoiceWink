using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Paragraph chunking for long transcriptions.
/// Pipeline step 3: FillerWordManager → TranscriptionTextFormatter.
/// </summary>
public sealed class TranscriptionTextFormatter
{
    private static ILogger Logger => Log.ForContext<TranscriptionTextFormatter>();

    private const int TargetWordsPerParagraph = 50;
    private const int MaxSentencesPerParagraph = 4;

    // Sentence-ending patterns: . ! ? followed by space or end
    private static readonly Regex SentenceEndPattern = new(
        @"(?<=[.!?])\s+(?=[A-Z])", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private readonly SettingsService _settings;

    public TranscriptionTextFormatter(SettingsService settings)
    {
        _settings = settings;
    }

    public string Format(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var enabled = _settings.GetBool(AppDefaults.IsTextFormattingEnabled, true);
        if (!enabled)
            return text;

        // Split into sentences
        var sentences = SplitSentences(text);
        if (sentences.Count <= MaxSentencesPerParagraph)
            return text; // Short enough, no chunking needed

        // Group sentences into paragraphs
        var paragraphs = new List<string>();
        var currentParagraph = new StringBuilder();
        int currentWordCount = 0;
        int currentSentenceCount = 0;

        foreach (var sentence in sentences)
        {
            var wordCount = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (currentSentenceCount > 0 &&
                (currentWordCount + wordCount > TargetWordsPerParagraph ||
                 currentSentenceCount >= MaxSentencesPerParagraph))
            {
                paragraphs.Add(currentParagraph.ToString().Trim());
                currentParagraph.Clear();
                currentWordCount = 0;
                currentSentenceCount = 0;
            }

            if (currentParagraph.Length > 0)
                currentParagraph.Append(' ');
            currentParagraph.Append(sentence);
            currentWordCount += wordCount;
            currentSentenceCount++;
        }

        if (currentParagraph.Length > 0)
            paragraphs.Add(currentParagraph.ToString().Trim());

        var result = string.Join("\n\n", paragraphs);

        if (paragraphs.Count > 1)
        {
            Logger.Debug("Text formatted into {Count} paragraphs", paragraphs.Count);
        }

        return result;
    }

    private static List<string> SplitSentences(string text)
    {
        string[] parts;
        try
        {
            parts = SentenceEndPattern.Split(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return [text];
        }

        var sentences = new List<string>();

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                sentences.Add(trimmed);
        }

        return sentences;
    }
}
