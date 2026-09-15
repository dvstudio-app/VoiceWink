using System.Text.RegularExpressions;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Support;

/// <summary>One changelog version block.</summary>
public sealed record ChangelogEntry(string Version, string? Date, IReadOnlyList<ChangelogSection> Sections);

/// <summary>A titled group of bullet lines within an entry (e.g. "Added", "Fixed").</summary>
public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

/// <summary>
/// Parses the bundled <c>CHANGELOG.md</c> (Keep-a-Changelog format) into structured entries for
/// the What's-new dialog (REL-4). Fail-soft: a missing or malformed file yields an empty list and
/// a logged warning — never an exception (so a corrupt bundle can't crash startup). The file path
/// is a constructor parameter (defaulting to the binary-adjacent copy) so it is unit-testable,
/// mirroring <c>LegalAcceptanceService</c>'s reader seam.
/// </summary>
public sealed class ChangelogParser
{
    private static ILogger Logger => Log.ForContext<ChangelogParser>();

    // "## [1.23.284] - 2026-06-13"  (date optional)
    private static readonly Regex VersionHeader =
        new(@"^##\s*\[(?<ver>[^\]]+)\]\s*(?:-\s*(?<date>.+))?$", RegexOptions.Compiled);
    private static readonly Regex SectionHeader =
        new(@"^###\s+(?<title>.+)$", RegexOptions.Compiled);
    private static readonly Regex BulletItem =
        new(@"^[-*]\s+(?<text>.+)$", RegexOptions.Compiled);

    private readonly string _path;

    public ChangelogParser(string? changelogPath = null)
        => _path = changelogPath ?? Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");

    /// <summary>Parse all entries (newest first, as authored). Returns empty on missing/unreadable file.</summary>
    public IReadOnlyList<ChangelogEntry> Parse()
    {
        string[] lines;
        try
        {
            if (!File.Exists(_path))
            {
                Logger.Warning("CHANGELOG.md not found at {Path}", _path);
                return Array.Empty<ChangelogEntry>();
            }
            lines = File.ReadAllLines(_path);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to read CHANGELOG.md");
            return Array.Empty<ChangelogEntry>();
        }

        var entries = new List<ChangelogEntry>();
        ChangelogEntryBuilder? entry = null;
        SectionBuilder? section = null;

        void FlushSection() { if (entry != null && section != null) { entry.Sections.Add(section.Build()); section = null; } }
        void FlushEntry() { FlushSection(); if (entry != null) { entries.Add(entry.Build()); entry = null; } }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var vh = VersionHeader.Match(line);
            if (vh.Success)
            {
                FlushEntry();
                var date = vh.Groups["date"].Success ? vh.Groups["date"].Value.Trim() : null;
                entry = new ChangelogEntryBuilder(vh.Groups["ver"].Value.Trim(), string.IsNullOrWhiteSpace(date) ? null : date);
                continue;
            }

            if (entry == null) continue; // skip the file's top matter before the first version

            var sh = SectionHeader.Match(line);
            if (sh.Success) { FlushSection(); section = new SectionBuilder(sh.Groups["title"].Value.Trim()); continue; }

            var bi = BulletItem.Match(line.TrimStart());
            if (bi.Success)
            {
                section ??= new SectionBuilder(string.Empty); // bullets before any ### header
                section.Items.Add(bi.Groups["text"].Value.Trim());
            }
        }
        FlushEntry();

        return entries;
    }

    /// <summary>
    /// Entries strictly newer than <paramref name="sinceVersion"/> (normalized 3-component
    /// compare). <paramref name="treatUnreleasedAs"/> maps a Keep-a-Changelog
    /// <c>[Unreleased]</c> block to the given version (in practice: the RUNNING build's) —
    /// "Unreleased" never parses as a version, so it silently vanished from every What's-new
    /// dialog, and a release whose changelog wasn't rolled showed nothing at all after an
    /// update. In a shipped build the bundled Unreleased items ARE that build's changes, so
    /// the mapping is honest; an Unreleased block with no items stays excluded (an empty
    /// version block would render as a blank entry).
    /// </summary>
    public IReadOnlyList<ChangelogEntry> EntriesNewerThan(string? sinceVersion, string? treatUnreleasedAs = null)
    {
        var entries = MapUnreleased(Parse(), treatUnreleasedAs);
        if (string.IsNullOrWhiteSpace(sinceVersion)) return entries.ToList();
        return entries.Where(e => ChangelogVersion.IsNewer(e.Version, sinceVersion)).ToList();
    }

    /// <summary>
    /// Full history for presentation (the About page's "What's new" link) with the same
    /// <c>[Unreleased]</c> handling as <see cref="EntriesNewerThan"/> — a raw <see cref="Parse"/>
    /// would render a literal blank "vUnreleased" heading once the bump script's roll leaves a
    /// fresh empty Unreleased header on top of every released changelog.
    /// </summary>
    public IReadOnlyList<ChangelogEntry> ParseForDisplay(string? treatUnreleasedAs = null)
        => MapUnreleased(Parse(), treatUnreleasedAs).ToList();

    private static IEnumerable<ChangelogEntry> MapUnreleased(
        IReadOnlyList<ChangelogEntry> parsed, string? treatUnreleasedAs)
    {
        // Conditional mapping: when a versioned entry already represents the running build
        // (a rolled changelog that ALSO still has a content-bearing Unreleased block on top —
        // i.e. work committed after the release cut), remapping Unreleased onto that version
        // would attribute next-release items to the current one and show it twice.
        var map = !string.IsNullOrWhiteSpace(treatUnreleasedAs)
            && !parsed.Any(e => !IsUnreleased(e) && ChangelogVersion.AreEqual(e.Version, treatUnreleasedAs));
        IEnumerable<ChangelogEntry> entries = parsed;
        if (map)
        {
            entries = entries.Select(e =>
                IsUnreleased(e) && e.Sections.Any(s => s.Items.Count > 0)
                    ? e with { Version = treatUnreleasedAs!, Date = null }
                    : e);
        }
        return entries.Where(e => !IsUnreleased(e)); // unmapped/empty Unreleased never renders
    }

    private static bool IsUnreleased(ChangelogEntry e)
        => string.Equals(e.Version, "Unreleased", StringComparison.OrdinalIgnoreCase);

    private sealed class ChangelogEntryBuilder(string version, string? date)
    {
        public List<ChangelogSection> Sections { get; } = new();
        public ChangelogEntry Build() => new(version, date, Sections);
    }

    private sealed class SectionBuilder(string title)
    {
        public List<string> Items { get; } = new();
        public ChangelogSection Build() => new(title, Items);
    }
}

/// <summary>
/// Version comparison for the What's-new gate. The running app's
/// <c>Assembly.GetName().Version</c> is 4-component (e.g. <c>1.23.284.0</c>) while CHANGELOG
/// headers and the persisted "last seen" value are 3-component (<c>1.23.284</c>); both are
/// normalized to Major.Minor.Build before comparing, so a same-release relaunch never re-shows
/// the dialog, and <c>1.10 &gt; 1.9</c> compares numerically (not as strings).
/// </summary>
public static class ChangelogVersion
{
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>True when <paramref name="candidate"/> is a strictly newer version than <paramref name="baseline"/>.</summary>
    public static bool IsNewer(string? candidate, string? baseline)
    {
        if (!Version.TryParse(candidate, out var c) || !Version.TryParse(baseline, out var b)) return false;
        return Normalize(c) > Normalize(b);
    }

    /// <summary>Normalized equality (<c>1.30.314 == 1.30.314.0</c>); unparseable never equals.</summary>
    public static bool AreEqual(string? a, string? b)
    {
        if (!Version.TryParse(a, out var va) || !Version.TryParse(b, out var vb)) return false;
        return Normalize(va) == Normalize(vb);
    }
}
