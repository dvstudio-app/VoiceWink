namespace VoiceWink.Models.Entities;

/// <summary>
/// Transcription history record.
/// </summary>
public class TranscriptionRecord
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? EnhancedText { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double DurationSeconds { get; set; }
    public string? AudioFilePath { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string? Language { get; set; }
    public bool WasEnhanced { get; set; }
    public string? PromptUsed { get; set; }
    public string? EnhancementModelName { get; set; }

    /// <summary>
    /// Path to the generated image file (PNG). Null for text-only transcriptions.
    /// Images are stored in %LOCALAPPDATA%/VoiceWink/Images/.
    /// </summary>
    public string? ImageFilePath { get; set; }

    /// <summary>
    /// Aspect ratio the user picked at generation time (e.g. "1:1", "16:9"). Null = Auto.
    /// Captured for redo so we can re-pick the same option in the picker.
    /// </summary>
    public string? ImageAspect { get; set; }

    /// <summary>
    /// Size tier the user picked at generation time ("1K", "2K", "4K"). Null = Auto.
    /// </summary>
    public string? ImageSizeTier { get; set; }

    /// <summary>
    /// Quality tier the generation actually used — or, on a failure row, the one requested (the tags in <c>ImageOptions.AllQualities</c>:
    /// "standard", "enhanced", "maximum" = wire <c>high</c>, and since IMG-14 "xhigh"/"max").
    /// Null = Auto. Only meaningful for OpenAI gpt-image generations.
    /// </summary>
    public string? ImageQuality { get; set; }

    /// <summary>
    /// How many versions the run that produced this row requested (1–4). Completes the
    /// set of image knobs the row already records — aspect / size tier / quality exist
    /// so History's Redo can restore the EXACT request, and the version count was the
    /// one missing member (owner, 2026-07-29: redo "should take over the versions number
    /// from the settings that were used to create that history entry").
    ///
    /// <para>NULL for every row written before this column existed, and for non-image
    /// rows; <c>ImageBatchPolicy.SeedCount(null)</c> maps that to 1, i.e. exactly the
    /// pre-existing behaviour — there is nothing to back-fill because the information
    /// was never recorded. Note this does NOT contradict the standing "the count is
    /// never persisted" rule: that rule is about the PROMPT and settings (a count must
    /// not become a sticky per-prompt default); a history row recording what produced
    /// it is the same pattern as the three knobs above.</para>
    /// </summary>
    public int? ImageVersionCount { get; set; }

    /// <summary>
    /// ENH-6b: this row's reference-image copies (stored in
    /// %LOCALAPPDATA%/VoiceWink/References/). Since ENH-6f the column holds ONE OR MORE
    /// paths encoded via <c>Helpers.ReferencePathList</c> ('|'-joined; a single path is
    /// stored raw, so pre-6f rows decode unchanged — the column NAME stays singular for
    /// migration-free compat). Null for text rows, no-reference generations, and
    /// history-disabled runs. Since ENH-6g copies are CONTENT-ADDRESSED (keyed hash
    /// names) and may be SHARED by any number of rows — a file deletes only when the
    /// LAST row referencing it in any DECODED list (and the last live claim) is gone;
    /// duplicate/corrupt pointers must not cause premature deletion. History
    /// Regenerate re-seeds the decoded paths as AppReferences selections.
    /// </summary>
    public string? ReferenceImagePath { get; set; }

    /// <summary>Sentinel EnhancedText value for successful image generations.</summary>
    public const string ImageGeneratedMarker = "[Image generated]";
    /// <summary>Sentinel EnhancedText value for failed image generations.</summary>
    public const string ImageFailedMarker = "[Image generation failed]";
}
