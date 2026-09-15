namespace VoiceWink.Helpers;

/// <summary>
/// HIS-7 (owner UAT scratchpad 2026-08-04): pure decisions behind restoring the History list's
/// viewport after an EXTERNAL refresh — a new transcription arriving while the user is reading.
/// <para>The defect this exists for: <c>HistoryViewModel.RefreshAsync</c> correctly reloads every
/// page that was loaded (it asks for <c>loadedPages * PageSize</c>), but <c>HistoryPage.RefreshList</c>
/// then rendered only the first <c>PageSize</c> of them and reset its rendered count — so the DB
/// paging survived and the RENDER paging did not, collapsing the list, and clearing the panel threw
/// the scroll offset away with it.</para>
/// <para>Extracted from the page because the page is WinUI and this repo's tests are WinUI-free.
/// Both functions are total and side-effect free; the page keeps only the layout measurement.</para>
/// </summary>
internal static class HistoryViewportRestore
{
    /// <summary>How many rows to re-render: at least one page, at least what was on screen, and
    /// never more than is actually loaded. The clamp is the part that matters — a refresh can
    /// return FEWER rows than before (a delete, or retention trimming the history), and a rendered
    /// count claiming rows that were never added would leave <c>RenderMoreAsync</c> skipping a page
    /// (it resumes at <c>_renderedCount</c>).</summary>
    /// <param name="ceiling">The most rows an external refresh will re-render (HistoryPage's
    /// <c>PreserveRenderCeiling</c>). Bounds the per-event rebuild cost: HistoryChanged fires once
    /// per saved row, so an unbounded preserve turns a materialised multi-thousand-row list into a
    /// full teardown-and-rebuild per row. Applied to the PRESERVED portion only — a page is always
    /// rendered even if the ceiling were set below one.</param>
    internal static int RenderTarget(int pageSize, int previousRenderedCount, int loadedCount, int ceiling)
    {
        if (loadedCount <= 0) return 0;
        var preserved = previousRenderedCount > ceiling ? ceiling : previousRenderedCount;
        var wanted = preserved > pageSize ? preserved : pageSize;
        return wanted > loadedCount ? loadedCount : wanted;
    }

    /// <summary>How many rows were inserted ABOVE the row that used to be at the top, found by
    /// locating that row's id in the reloaded list. This is what keeps the restore honest: rows
    /// added above shift everything down, so holding the raw scroll offset would still slide the
    /// content under the user — the "loses their place" complaint in a second form.
    /// <para>Returns 0 — meaning "no shift, restore the raw offset and let the ScrollViewer clamp
    /// it" — whenever no meaningful anchor exists: no anchor captured, an empty list, or the anchor
    /// row GONE (deleted, or fallen off the end of the reloaded window). Fail-soft is deliberate:
    /// a restore that is wrong by a few pixels beats the jump to top it replaces.</para>
    /// <para>Only the RENDERED prefix is searched. A match beyond it could not have been on screen,
    /// so treating it as a shift would scroll to a row the user never saw.</para></summary>
    internal static int InsertedAbove(IReadOnlyList<int> reloadedIds, int? previousFirstId, int renderedCount)
    {
        if (previousFirstId is not { } anchor) return 0;
        var limit = renderedCount < reloadedIds.Count ? renderedCount : reloadedIds.Count;
        for (var i = 0; i < limit; i++)
        {
            if (reloadedIds[i] == anchor)
                return i; // 0 when the anchor is still first — nothing was inserted above it
        }
        return 0; // anchor gone
    }
}
