namespace VoiceWink.Helpers;

/// <summary>What the History detail pane shows in the image-preview slot (HIS-1).</summary>
public enum HistoryImagePreview
{
    /// <summary>Not an image row, or a failed generation (no image ever existed) — no slot content.</summary>
    None,
    /// <summary>The image resolved and decoded — show the preview + the Copy-Image button.</summary>
    Preview,
    /// <summary>A SUCCESSFUL image row whose file no longer resolves — show the missing note.</summary>
    MissingNote,
    /// <summary>The file resolves but could not be decoded/displayed — show the undisplayable note.</summary>
    UndisplayableNote,
}

/// <summary>
/// HIS-1 (2026-07-15): pure decisions for the History detail pane's image slot and the
/// row copy-button feedback. Owner report: a successful image row whose file was
/// deleted from the Images folder showed an EMPTY preview with no explanation, and the
/// row's copy glyph silently copied the prompt text. The page itself is untestable
/// WinUI code-behind — this helper pins the matrix.
/// </summary>
public static class HistoryImagePresentation
{
    /// <summary>Shown in the preview slot when a successful row's image file is gone.</summary>
    public const string MissingImageNote =
        "The generated image is no longer available — its file was deleted from the Images folder.";

    /// <summary>Shown when the file exists but WIC/XAML could not display it.</summary>
    public const string UndisplayableImageNote =
        "The generated image file could not be displayed.";

    /// <summary>
    /// The preview-slot decision. <paramref name="decodeFailed"/> is only meaningful
    /// when <paramref name="fileResolvable"/> is true (the preview-load catch arm).
    /// Failed generations never had an image, so they get NO note — the red badge
    /// already explains the row.
    /// </summary>
    public static HistoryImagePreview DecidePreview(
        bool isImageItem, bool isFailedGeneration, bool fileResolvable, bool decodeFailed)
    {
        if (!isImageItem || isFailedGeneration)
            return HistoryImagePreview.None;
        if (!fileResolvable)
            return HistoryImagePreview.MissingNote;
        return decodeFailed ? HistoryImagePreview.UndisplayableNote : HistoryImagePreview.Preview;
    }

    /// <summary>
    /// The row copy button's confirmation label: it copies the PROMPT for image rows
    /// (the generated marker would be useless) and the text otherwise — the feedback
    /// must say which, because with a missing image "copy" silently yielding text was
    /// the confusing half of the owner report.
    /// </summary>
    public static string CopyFeedbackLabel(bool isImageItem)
        => isImageItem ? "Prompt copied" : "Text copied";

    /// <summary>
    /// Shown when SetClipboard reports failure (contention / native set failure) —
    /// a success checkmark on a failed copy would be the same silent lie the feature
    /// exists to fix (Codex diff review). Cause-first, pill-budget-friendly.
    /// </summary>
    public const string CopyFailedLabel = "Couldn't copy — clipboard busy";
}
