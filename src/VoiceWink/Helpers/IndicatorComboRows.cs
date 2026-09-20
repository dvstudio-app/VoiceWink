namespace VoiceWink.Helpers;

/// <summary>
/// One row of an indicator combo, as DATA. The combos bind to these through an
/// <c>ItemTemplate</c> instead of holding pre-built visuals.
///
/// <para><b>Why this type exists (2026-08-03, evidence-backed):</b> the rows used to be
/// <c>ComboBoxItem</c>s whose <c>Content</c> was a live <c>StackPanel</c>. A closed ComboBox renders
/// the selected item's content in its own selection-box presenter — the SAME visual instance the
/// dropdown's container owns — and opening the dropdown hands it back. That hand-off is what raised
/// <c>COMException 0x800F1000</c> "Element is already the child of another element", fatally, on
/// every attempt to open the aspect dropdown. With a template the framework materializes a separate
/// visual per presenter, so no element can ever have two parents.</para>
///
/// <para>The decisive evidence was a control group in the same dialog: the MODEL combo repopulates
/// on every provider switch exactly like this one and has never crashed — its items are plain
/// strings. Repopulation was never the variable; UIElement item content was. An earlier fix that
/// only made repopulation atomic did not stop the crash.</para>
///
/// <para>Public because <c>{Binding}</c> resolves the properties by reflection at runtime; an
/// accessibility formality inside the app assembly, not an API commitment.</para>
/// </summary>
public sealed class IndicatorRow
{
    public string Label { get; init; } = "";

    /// <summary>The persisted tag (aspect / tier / quality). Null is the always-present Auto row.</summary>
    public string? Tag { get; init; }

    public double IndicatorWidth { get; init; }
    public double IndicatorHeight { get; init; }
}

/// <summary>
/// The pure decisions behind the image-option indicator combos (aspect / size tier / quality):
/// which rows a model's capabilities allow, which row starts selected, and — since UI-19 — which
/// tag a repopulate carries forward.
/// <para>They live here rather than in <see cref="AppTheme"/> because that type's static
/// initializer needs the WinUI runtime, so nothing inside it can be reached from a unit test.
/// The row shape is the same named tuple <see cref="ImageOptions.AspectComboRows"/> already
/// uses, so the catalog stays the single source of truth for the aspect data.</para>
/// </summary>
internal static class IndicatorComboRows
{
    /// <summary>
    /// The subset of <paramref name="options"/> a model accepts: the Auto row (null tag) is ALWAYS
    /// shown, plus any entry whose tag is in <paramref name="allowed"/>. A null
    /// <paramref name="allowed"/> means "no gating" and returns everything.
    /// <para>Never mutates <paramref name="options"/> — and when <paramref name="allowed"/> is null
    /// it returns that same array instance. Both halves matter: the aspect path passes the shared
    /// static <see cref="ImageOptions.AspectComboRows"/>, so filtering in place would corrupt the
    /// catalog process-wide, while copying it on the ungated path would be pure waste.</para>
    /// </summary>
    internal static (string Label, string? Tag, double Width, double Height)[] Filter(
        (string Label, string? Tag, double Width, double Height)[] options,
        global::System.Collections.Generic.IReadOnlyCollection<string>? allowed)
    {
        if (allowed == null) return options;
        var keep = new global::System.Collections.Generic.List<(string, string?, double, double)>();
        foreach (var o in options)
        {
            if (o.Tag == null || allowed.Contains(o.Tag))
                keep.Add(o);
        }
        return keep.ToArray();
    }

    /// <summary>
    /// The index to pre-select: the row carrying <paramref name="selectedTag"/> if it survived
    /// filtering, else 0 — the Auto row, which <see cref="Filter"/> always keeps. Returns -1 for an
    /// empty set so the caller can skip assigning a selection at all.
    /// <para>The -1 case is unreachable today (every table leads with Auto), but a pure function
    /// gets a total contract rather than inheriting an unconditional <c>SelectedIndex = 0</c>
    /// against an empty source.</para>
    /// </summary>
    internal static int IndexForTag(
        (string Label, string? Tag, double Width, double Height)[] options,
        string? selectedTag)
    {
        if (options.Length == 0) return -1;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i].Tag == selectedTag) return i;
        }
        return 0;
    }

    /// <summary>
    /// The tag a repopulate of the aspect or size row carries forward (IMG-17, closed by UI-19):
    /// the row's CURRENT selection whenever one exists — an explicit Auto (null tag) included —
    /// and <paramref name="fallback"/> (the previous run's or the prompt's saved tag) only when
    /// the row has never been populated.
    /// <para>The old expression was <c>SelectedIndicatorTag(combo) ?? previous ?? saved</c>, and
    /// that reader answers null for BOTH "the user chose Auto" and "nothing selected", so an
    /// explicit Auto fell through to the saved tag on every re-gate. UI-19's deferred replay
    /// re-gates right after a pick, which made that fall-through the very next thing to happen to
    /// a just-cleared row. Callers feed <paramref name="rowSelected"/> from
    /// <c>AppTheme.TryGetSelectedIndicatorTag</c>, which does tell the two apart.</para>
    /// </summary>
    internal static string? TagToCarry(bool rowSelected, string? selectedTag, string? fallback)
        => rowSelected ? selectedTag : fallback;

    /// <summary>
    /// Whether a combo ALREADY holds exactly <paramref name="options"/>, in order — the test that
    /// decides whether a re-gate has to touch <c>ItemsSource</c> at all (UI-21, 2026-09-20).
    ///
    /// <para><b>Why it exists.</b> A WinUI ComboBox derives its CLOSED box from the item CONTAINER at
    /// <c>SelectedIndex</c> (<c>ComboBox::SetContentPresenter</c> reads <c>ComboBoxItem.Content</c>,
    /// generating and recycling a container when the popup has none realised), and every
    /// <c>ItemsSource</c> swap invalidates those containers. The option dialog re-gates three to four
    /// times per open — the builder, <c>ContentDialog.Opened</c>, the model combo's
    /// <c>SelectionChanged</c> when <c>BindModels</c> lands, any provider change — and on all but the
    /// first the resolved model, and therefore the row set, is IDENTICAL. Swapping anyway tore the
    /// containers down and rebuilt them for no change, which is the churn every reported symptom of
    /// this family sits on: a blank closed box, and a stale container left rendering as selected
    /// beside the real selection (owner, 2026-09-20 — two rows looked selected on a redo, and the
    /// stale one could not be clicked because the Selector already considered it deselected).</para>
    ///
    /// <para>Tags alone decide it: within one combo the tag determines the label and the indicator
    /// dimensions (both come from the same static table in <see cref="ImageOptions"/>), so an equal
    /// tag sequence IS an equal row set. Null is the Auto row and compares like any other tag.</para>
    /// </summary>
    internal static bool SameRows(
        global::System.Collections.Generic.IReadOnlyList<IndicatorRow>? current,
        (string Label, string? Tag, double Width, double Height)[] options)
    {
        if (current == null || current.Count != options.Length) return false;
        for (int i = 0; i < options.Length; i++)
        {
            if (current[i].Tag != options[i].Tag) return false;
        }
        return true;
    }
}
