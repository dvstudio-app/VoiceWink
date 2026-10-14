namespace VoiceWink.Helpers;

/// <summary>
/// What the main window's sidebar lists, in which group, and which pages are reachable without a
/// sidebar entry (UI-25, tester feedback 2026-10-13). Pure, so the shape is pinned by
/// <c>SidebarLayoutTests</c> rather than by eye.
///
/// <para>Owner decisions, 2026-10-13: the everyday pages stay in the main list (App Mode
/// included); Settings, Metrics, License, Updates and About go into a collapsed "More" group
/// (Settings joined it after the owner saw the first build); the Log Viewer leaves the sidebar and
/// opens from Settings → Diagnostics. <c>MainWindow</c> builds the sidebar
/// from <see cref="Main"/> and <see cref="More"/>, and its <c>NavigateToPage</c> allowlist is
/// <see cref="NavigableTags"/>, so the two cannot drift; the page-factory switch is pinned to the
/// same set by a source test.</para>
/// </summary>
internal static class SidebarLayout
{
    internal sealed record Entry(string Label, string Glyph, string Tag);

    public static readonly IReadOnlyList<Entry> Main =
    [
        new("Home",            "\uE80F", "home"),
        new("History",         "\uE81C", "history"),
        new("Models",          "\uE8F1", "models"),
        new("AI Enhancement",  "\uE945", "enhancement"),
        new("Dictionary",      "\uE82D", "dictionary"),
        new("App Mode",        "\uE7AC", "appmode"),
        new("Transcribe File", "\uE8E6", "transcribe"),
    ];

    public const string MoreLabel = "More";
    public const string MoreGlyph = "\uE712";

    public static readonly IReadOnlyList<Entry> More =
    [
        new("Settings", "\uE713", "settings"),
        new("Metrics",  "\uE9D2", "metrics"),
        new("License",  "\uE192", "license"),
        new("Updates",  "\uE895", "updates"),
        new("About",    "\uE946", "about"),
    ];

    /// <summary>Pages with no sidebar entry, each mapped to the entry that stays highlighted while
    /// it is open — the page it is reached from.</summary>
    private static readonly Dictionary<string, string> OffSidebarParents = new(StringComparer.Ordinal)
    {
        ["logviewer"] = "settings",
        ["legal"] = "about",
    };

    /// <summary>Every tag <c>MainWindow.NavigateToPage</c> accepts: the sidebar's entries plus the
    /// pages reached from inside another page.</summary>
    public static IReadOnlyList<string> NavigableTags { get; } =
        Main.Concat(More).Select(e => e.Tag).Concat(OffSidebarParents.Keys).ToArray();

    /// <summary>The sidebar entry to show as active while <paramref name="tag"/> is open: the
    /// entry itself, an off-sidebar page's parent, or null for a tag the sidebar does not know.</summary>
    public static string? HighlightTagFor(string? tag)
    {
        if (tag == null) return null;
        if (OffSidebarParents.TryGetValue(tag, out var parent)) return parent;
        return Main.Concat(More).Any(e => e.Tag == tag) ? tag : null;
    }

    /// <summary>True when the page's highlighted entry sits in the "More" group, so navigating to
    /// it must open the group — a jump to License (the trial-ended redirect, LNC-11) or a startup
    /// license redirect must never land with its entry hidden.</summary>
    public static bool IsInMoreGroup(string? tag)
    {
        var highlight = HighlightTagFor(tag);
        return highlight != null && More.Any(e => e.Tag == highlight);
    }
}
