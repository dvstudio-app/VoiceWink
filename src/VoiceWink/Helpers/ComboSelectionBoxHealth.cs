namespace VoiceWink.Helpers;

/// <summary>
/// What a ComboBox's CLOSED selection box is doing, read once the dropdown has closed and the
/// layout pass after it has run. The pure half of <see cref="ComboSelectionBoxGuard"/> (UI-19).
/// </summary>
public enum ComboSelectionBoxState
{
    /// <summary>No row is selected, so an empty box is correct.</summary>
    NoSelection,

    /// <summary>A row is selected and its content visual is laid out in the box.</summary>
    Rendered,

    /// <summary>
    /// A row is selected but the control's own <c>SelectionBoxItem</c> is null — the closed box
    /// never received the selection. The reader that feeds the run (<c>SelectedItem</c>) still
    /// returns the row, which is exactly the owner-observed shape: value applied, box empty.
    /// </summary>
    LogicallyBlank,

    /// <summary>
    /// <c>SelectionBoxItem</c> is set, but the presenter that renders it has no laid-out content
    /// visual — the selection reached the control and stopped short of the screen.
    /// </summary>
    VisuallyBlank,

    /// <summary>
    /// A row is selected and <c>SelectionBoxItem</c> is set, but the presenter part could not be
    /// found in the visual tree (a template this reader does not know), so nothing can be said
    /// about the screen.
    /// </summary>
    Unmeasured,
}

/// <summary>
/// The verdict behind <see cref="ComboSelectionBoxGuard"/>'s log line, kept pure so its case
/// table lives in the test suite: the WinUI reads that feed it need a live control.
/// </summary>
public static class ComboSelectionBoxHealth
{
    /// <param name="selectedIndex">The control's <c>SelectedIndex</c>; negative = nothing selected.</param>
    /// <param name="hasSelectionBoxItem">Whether <c>SelectionBoxItem</c> is non-null.</param>
    /// <param name="contentVisualLaidOut">
    /// Whether the selection-box presenter holds a content visual with a positive width after
    /// layout; null when the presenter part was not found.
    /// </param>
    public static ComboSelectionBoxState Decide(
        int selectedIndex, bool hasSelectionBoxItem, bool? contentVisualLaidOut)
    {
        if (selectedIndex < 0) return ComboSelectionBoxState.NoSelection;
        if (!hasSelectionBoxItem) return ComboSelectionBoxState.LogicallyBlank;
        return contentVisualLaidOut switch
        {
            null => ComboSelectionBoxState.Unmeasured,
            false => ComboSelectionBoxState.VisuallyBlank,
            true => ComboSelectionBoxState.Rendered,
        };
    }

    /// <summary>
    /// True for the two states where a selected row is not on screen — the ones worth an
    /// Information-level line, because each is an occurrence of the defect the guard exists for.
    /// </summary>
    public static bool IsBlank(ComboSelectionBoxState state)
        => state is ComboSelectionBoxState.LogicallyBlank or ComboSelectionBoxState.VisuallyBlank;
}
