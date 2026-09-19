using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Keeps a ComboBox's CLOSED selection box showing the row that is selected (UI-19, 2026-09-18).
///
/// <para><b>The defect.</b> In the image-options dialog a row picked from the dropdown sometimes
/// closes to an EMPTY box — the pick is real (every reader returns it, the run honours it), only
/// the selection-box presenter shows nothing. UI-18 fixed the same symptom for the pre-show
/// populate by re-gating on <c>ContentDialog.Opened</c>, and its account was an <c>ItemsSource</c>
/// swap landing on a detached control. The owner then saw it again on the FIRST open of a session,
/// after their OWN pick, on Size AND on Versions — and Versions is filled once with
/// <c>Items.Add</c> and never swapped, so the swap account cannot be the whole story: the closed
/// box can go blank after an ordinary pick, with no app code on the path (neither row has a
/// SelectionChanged handler). WinUI-internal, intermittent, and invisible to every test in this
/// repo.</para>
///
/// <para><b>What this does.</b> After every <c>DropDownClosed</c>, at Low dispatcher priority
/// (which is NOT ordered against the render tick — see "What it records" below), it re-asserts
/// the selection — <c>SelectedIndex</c>
/// to −1 and back — which is the same remedy UI-18's re-populate applies, minus the swap. It runs
/// unconditionally rather than only on a detected blank, because the detection below reads the
/// control's state and the owner's report is about the SCREEN; a nudge on a healthy box costs two
/// SelectionChanged events that nothing on these rows reacts to (the quality row's tracker is
/// scoped out via <paramref name="populateScope"/>).</para>
///
/// <para><b>What it records.</b> The state of the box BEFORE the nudge — read RAW and again after a
/// forced <c>UpdateLayout()</c>, both in the line, because a dispatcher callback is not ordered
/// against the layout tick: an unlaid-out presenter reads as blank, and a forced pass can itself
/// cure a missed one, so neither reading alone is evidence — classified by
/// <see cref="ComboSelectionBoxHealth.Decide"/>, at Information on every close — the healthy
/// verdict is as load-bearing as the blank one, since "Rendered" beside a box the owner saw empty
/// moves the defect below the presenter — and the state AFTER it when the box was blank. That is
/// the evidence the next report needs: whether the selection ever reached the control
/// (<c>SelectionBoxItem</c>), whether its visual was laid out, and whether re-asserting cured it.</para>
///
/// <para><b>Ordering with a deferred re-gate.</b> <see cref="ComboRegateDeferral"/> replays a
/// re-gate that arrived under an open dropdown at Normal priority from the same close, so the
/// swap lands first and this Low-priority re-assert stays the last word on the box.</para>
/// </summary>
internal static class ComboSelectionBoxGuard
{
    private static ILogger Logger => Log.ForContext(typeof(ComboSelectionBoxGuard));

    /// <param name="combo">A non-editable ComboBox (the editable kind renders through its Text).</param>
    /// <param name="name">How the row is named in the log: "aspect", "size", "quality", "versions".</param>
    /// <param name="populateScope">
    /// Opened around the re-assert so a SelectionChanged handler that tracks USER picks ignores it
    /// (the quality row's <see cref="QualitySelectionTracker"/>); null for rows with no such handler.
    /// </param>
    internal static void Attach(
        ComboBox combo, string name, global::System.Func<global::System.IDisposable>? populateScope = null)
    {
        combo.DropDownClosed += (_, _) =>
            combo.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low, () => Reassert(combo, name, populateScope));
    }

    private static void Reassert(
        ComboBox combo, string name, global::System.Func<global::System.IDisposable>? populateScope)
    {
        // A diagnostic must never take the app down: this runs inside a dispatcher callback, where
        // an exception is unhandled, and the layout pass below is framework code that can run
        // during a dialog's teardown (DropDownClosed fires on Hide()). Fail soft, say so.
        try
        {
            ReassertCore(combo, name, populateScope);
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning(ex, "Closed box {Combo}: re-assert failed", name);
        }
    }

    private static void ReassertCore(
        ComboBox combo, string name, global::System.Func<global::System.IDisposable>? populateScope)
    {
        // A row COLLAPSED between the close and this dispatch — the prompt editor's type switched
        // to Text, or a model switch hid the row — has no box to judge: its presenter measures 0
        // and would be recorded as VisuallyBlank, a false occurrence in the very log this guard
        // exists to make trustworthy (Kimi, UI-19 round 1). Nothing to re-assert either.
        if (IsCollapsed(combo))
        {
            Logger.Debug("Closed box {Combo}: row collapsed before the re-assert ran; skipped", name);
            return;
        }

        var index = combo.SelectedIndex;
        if (index < 0)
        {
            Logger.Debug("Closed box {Combo}: no selection after the dropdown closed", name);
            return;
        }

        // Two readings, both logged. A DispatcherQueue callback is not ordered against the render
        // tick that measures and arranges, so the RAW read may see content the tick has not laid
        // out yet — the owner's first UAT (2026-09-18 23:53) logged a Versions pick as VisuallyBlank
        // and "still VisuallyBlank" in the SAME millisecond. The LAID-OUT read forces that pass
        // first. Logging only the second would hide the case the forced pass itself cures (a box the
        // owner saw empty reading Rendered here would send the next fix below the presenter on
        // evidence this instrument manufactured); logging only the first is what proved nothing.
        var raw = Inspect(combo);
        combo.UpdateLayout();
        var before = Inspect(combo);

        // Information for EVERY close, not only a blank one: "Rendered before a box the owner saw
        // empty" is the verdict the card stakes the next fix on, and the default level is
        // Information (LogLevelControl) — at Debug it would reach no sink unless verbose logging
        // happened to be on. A handful of lines per dialog; nothing else emits on these closes.
        Logger.Information(
            "Closed box {Combo}: {State} after the dropdown closed (raw {Raw}, index {Index}); re-asserting",
            name, before, raw, index);

        using (populateScope?.Invoke())
        {
            // The restore is a finally: a SelectionChanged subscriber throwing on the -1 phase
            // would otherwise escape to Reassert's fail-soft catch with the row left at -1 — and
            // the confirm path reads the row, so the user's pick would be lost (Gemini, round 1).
            // No subscriber throws today; the shape is what keeps that true of the next one.
            try
            {
                combo.SelectedIndex = -1;
            }
            finally
            {
                combo.SelectedIndex = index;
            }
        }

        if (!ComboSelectionBoxHealth.IsBlank(before)) return;
        // Only a blank box earns the second read: it says whether the nudge is a cure or merely
        // a report, which is what decides the next fix.
        combo.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (IsCollapsed(combo))
                {
                    Logger.Debug("Closed box {Combo}: row collapsed before the after-read ran; skipped", name);
                    return;
                }
                var afterRaw = Inspect(combo);
                combo.UpdateLayout();
                var after = Inspect(combo);
                if (ComboSelectionBoxHealth.IsBlank(after))
                    Logger.Information(
                        "Closed box {Combo} is still {State} after re-asserting index {Index} (raw {Raw})",
                        name, after, combo.SelectedIndex, afterRaw);
                else
                    Logger.Information(
                        "Closed box {Combo} recovered: {State} after re-asserting index {Index} (raw {Raw})",
                        name, after, combo.SelectedIndex, afterRaw);
            }
            catch (global::System.Exception ex)
            {
                Logger.Warning(ex, "Closed box {Combo}: after-read failed", name);
            }
        });
    }

    private static bool IsCollapsed(ComboBox combo)
        => combo.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The verdict as the control stands NOW — no layout forced; callers decide that.</summary>
    private static ComboSelectionBoxState Inspect(ComboBox combo)
        => ComboSelectionBoxHealth.Decide(
            combo.SelectedIndex,
            combo.SelectionBoxItem != null,
            ContentVisualLaidOut(combo));

    /// <summary>
    /// Whether the template's selection-box presenter (the part WinUI names
    /// <c>ContentPresenter</c>) holds a laid-out content visual; null when no such part is found.
    /// Bounded walk — the part sits a few levels under the control's root.
    /// </summary>
    private static bool? ContentVisualLaidOut(ComboBox combo)
    {
        var presenter = FindPresenter(combo, depth: 0);
        if (presenter == null) return null;
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0) return false;
        return VisualTreeHelper.GetChild(presenter, 0) is Microsoft.UI.Xaml.FrameworkElement fe
            && fe.ActualWidth > 0;
    }

    private static ContentPresenter? FindPresenter(Microsoft.UI.Xaml.DependencyObject node, int depth)
    {
        if (depth > 8) return null;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ContentPresenter { Name: "ContentPresenter" } presenter) return presenter;
            // The dropdown's own popup content is not in this subtree, so nothing below a
            // ComboBoxItem is ever visited; the walk stays inside the closed control's template.
            var found = FindPresenter(child, depth + 1);
            if (found != null) return found;
        }
        return null;
    }
}

/// <summary>
/// Holds an image-option re-gate back while one of its rows has its dropdown OPEN, and replays it
/// once that dropdown closes (UI-19).
///
/// <para><b>The scenario.</b> On the FIRST options dialog of a session the model list is not
/// cached, so <c>BindModels</c> lands whenever the network answers — after the dialog is up, at a
/// moment the user may be inside a dropdown. It then re-gates the aspect / size / quality rows,
/// which swaps their <c>ItemsSource</c> under the open popup. The dialog's own comment on the
/// cache-hit branch already names a swap under a live popup as the <c>0x800F1000</c> crash
/// surface and keeps the BACKGROUND refresh away from it; the miss path had no such fence. Every
/// later open binds from the cache, synchronously, before the dialog shows — which is why the
/// owner met this on the first open only.</para>
///
/// <para>Deferred, not dropped: the replay runs at Normal priority from the closing dropdown's
/// <c>DropDownClosed</c>, so the user's pick has landed and the re-gate carries it forward — an
/// explicit Auto included, which is why both callers read the row through
/// <c>AppTheme.TryGetSelectedIndicatorTag</c> (IMG-17): a replay right after a pick is exactly
/// the state where the old <c>SelectedIndicatorTag ?? saved</c> chain re-seeded a just-cleared
/// row from the saved tag. <see cref="ComboSelectionBoxGuard"/>'s Low-priority re-assert still
/// runs after the replay. Repeat arrivals while the popup stays open coalesce into one replay,
/// and each caller fences the replay on its dialog's closed flag — best-effort ordering, not a
/// proven interlock: during <c>Hide()</c> the dropdown's <c>DropDownClosed</c> plausibly precedes
/// <c>ContentDialog.Closed</c>, so a replay can still re-gate a dialog that is tearing down; the
/// cost is one wasted repopulate of controls about to detach (Kimi, UI-19 round 1).</para>
/// </summary>
internal sealed class ComboRegateDeferral
{
    private readonly ComboBox?[] _rows;
    private bool _pending;

    internal ComboRegateDeferral(params ComboBox?[] rows) => _rows = rows;

    /// <returns>True when the re-gate was deferred (the caller returns without re-gating).</returns>
    internal bool TryDefer(global::System.Action regate)
    {
        ComboBox? open = null;
        foreach (var row in _rows)
        {
            if (row is { IsDropDownOpen: true }) { open = row; break; }
        }
        if (open == null) return false;
        if (_pending) return true;

        _pending = true;
        void OnClosed(object? _, object __)
        {
            open.DropDownClosed -= OnClosed;
            _pending = false;
            open.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => regate());
        }
        open.DropDownClosed += OnClosed;
        return true;
    }
}
