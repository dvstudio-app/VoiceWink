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
/// The two pure decisions behind the image-option indicator combos (aspect / size tier / quality):
/// which rows a model's capabilities allow, and which row starts selected.
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
}
