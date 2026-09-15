using Serilog;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Runs the 4-stage transcription text processing pipeline:
/// <see cref="TranscriptionOutputFilter"/> → <see cref="FillerWordManager"/>
/// → <see cref="TranscriptionTextFormatter"/> → <see cref="WordReplacementService"/>.
///
/// Failure in any single stage is logged and swallowed so a broken rule
/// in formatter/word-replacement doesn't break the whole transcription.
/// The early stages (output filter, filler words) are trusted to not throw —
/// if they do, the exception propagates because their failure means corrupt
/// output is more damaging than a missed transcription.
/// </summary>
public sealed class TextPipelineRunner
{
    private static ILogger Logger => Log.ForContext<TextPipelineRunner>();

    private readonly TranscriptionOutputFilter _outputFilter;
    private readonly FillerWordManager _fillerWordManager;
    private readonly TranscriptionTextFormatter _textFormatter;
    private readonly WordReplacementService _wordReplacement;

    public TextPipelineRunner(
        TranscriptionOutputFilter outputFilter,
        FillerWordManager fillerWordManager,
        TranscriptionTextFormatter textFormatter,
        WordReplacementService wordReplacement)
    {
        _outputFilter = outputFilter;
        _fillerWordManager = fillerWordManager;
        _textFormatter = textFormatter;
        _wordReplacement = wordReplacement;
    }

    /// <summary>
    /// Run the full pipeline. Returns the processed text — which may be empty if the
    /// input was empty or the pipeline removed all content.
    /// </summary>
    /// <param name="rawText">The transcriber's output.</param>
    /// <param name="language">The recognition language the user ASKED for on this attempt — the
    /// REQUESTED one (<c>en</c>, <c>nl</c>, <c>auto</c>…), never the effective one the engine was
    /// handed: Parakeet auto-detects only, so its effective language is <c>auto</c> for every
    /// attempt, which would re-apply the English list to a German recording. Null when the
    /// caller has none. Only the filler stage reads it (<see cref="FillerWordManager.AppliesTo"/>);
    /// null and <c>auto</c> both mean "unknown, apply".</param>
    public string Run(string rawText, string? language = null)
    {
        var text = _outputFilter.Filter(rawText);
        text = _fillerWordManager.Filter(text, language);
        try
        {
            text = _textFormatter.Format(text);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Text formatting failed, continuing with unformatted text");
        }

        // ── The machine/user boundary ───────────────────────────────────────────────────────
        // Everything above is MACHINE-owned: the transcriber's output, then three stages that
        // only ever remove or reshape. Everything below is USER-authored — WordReplacementService
        // applies replacements with no charset restriction. That distinction is what decides
        // whether punctuation-only text is an artifact or an intention, so the content check
        // belongs exactly here (Codex diff review r2).
        //
        // Checking only the RAW input was not enough, and this is the case that proved it: the
        // stages above can MANUFACTURE a contentless string from input that had words.
        // "[music]," loses its tag in TranscriptionOutputFilter and "um," loses its filler in
        // FillerWordManager — both leaving a bare comma from text the raw guard correctly passed.
        // That is the same paid-enhancement-then-commentary path the original incident took.
        //
        // Returning empty hands it to the caller's existing empty-transcript route (retain the
        // WAV, arm the amber Retry). Skipping the replacement stage costs nothing: there is no
        // word left for a replacement to match.
        if (Helpers.TranscriptContent.IsEmpty(text))
        {
            Logger.Warning("Pipeline stages left no content (raw was {Length} chars)", rawText?.Length ?? 0);
            return string.Empty;
        }

        try
        {
            text = _wordReplacement.ApplyReplacements(text);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Word replacement failed, continuing with unmodified text");
        }
        return text;
    }
}
