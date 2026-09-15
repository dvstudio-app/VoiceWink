namespace VoiceWink.Helpers;

/// <summary>
/// Decides what the Report-a-problem dialog shows for its two OPTIONAL attachments — raw
/// prompt logs and audio recordings — including <b>why</b> one cannot be selected.
///
/// <para><b>Why this exists (owner UAT, 2026-08-10).</b> Both options used to be
/// <see cref="Microsoft.UI.Xaml.Visibility.Collapsed"/> whenever there was nothing to send,
/// so a user who had never switched on prompt tracing or keep-recordings saw no row at all —
/// and therefore could not tell whether the report carried that data, nor what to change to
/// include it. The owner's words: *"clarify what happens if prompt logging and audio
/// recording is not switched on"*. The rows are now always present and explain their own
/// unavailability.</para>
///
/// <para><b>Precedence is load-bearing.</b> The raw-prompt row can be unselectable for two
/// unrelated reasons — nothing was ever recorded, or "Include logs" is unticked (the raw
/// trace rides WITH the logs, a rule <c>SupportBundle</c> enforces independently). Those
/// need different sentences, and "nothing recorded" WINS: telling someone to tick a box that
/// would still leave the option empty is worse than saying nothing. Pinned by
/// <c>ReportOptionAvailabilityTests</c>.</para>
///
/// <para>Pure and total by design — no WinUI, no file system, every input a value — so the
/// case table runs in the test host, which the dialog itself cannot (its controls need a
/// XAML runtime).</para>
/// </summary>
internal static class ReportOptionAvailability
{
    /// <summary>
    /// Shown when raw prompt logs CAN be included. Unchanged wording — it was never the
    /// confusing one, and it is the consent text the REL-17 review settled.
    /// </summary>
    internal const string RawPromptsPrivacy =
        "Raw prompt logs contain everything you dictated and the AI prompts/results, " +
        "verbatim. Include them only if you're comfortable sharing that with support.";

    /// <summary>Traces exist, but the parent "Include logs" box is unticked.</summary>
    internal const string RawPromptsNeedsLogs =
        "Tick \"Include logs\" first — raw prompt logs are sent together with them.";

    /// <summary>
    /// The sentence under the logs box. A const so its exact wording is pinnable — the
    /// previous version of it is the whole reason this card exists.
    ///
    /// <para><b>Scoped to "any dictated text IN IT" deliberately (Codex diff r1).</b> An
    /// earlier draft said "everything you dictated appears as a character count", which is
    /// FALSE by default: with image-prompt tracing off — the shipped default — a dictated
    /// image description has no RAW entry at all (since 2026-09-13 its always-written sidecar
    /// line carries only its length), so the raw log never holds everything you said. The
    /// claim describes what the file contains, not what you said.</para>
    /// </summary>
    internal const string LogsAdvisory =
        "Log files can include provider responses for troubleshooting. When a redacted " +
        "prompt log is available it is included too — any dictated text in it appears as a " +
        "character count like “<masked 42 chars>”, never the words themselves.";

    /// <summary>
    /// Nothing is available to attach. **Says nothing about WHY**, and that is the fix for
    /// two separate Codex blockers, not brevity:
    ///
    /// <list type="number">
    /// <item>Absence of files is NOT evidence the setting is off. Enable the toggle and open
    /// this dialog before dictating and there is still no trace — "turn it on" would then be
    /// telling the user to enable something already enabled.</item>
    /// <item>For recordings the same sentence was outright false: <c>SelectRecordings</c>
    /// SKIPS any file over <c>RecordingsBudgetBytes</c> (15 MB), so one oversized WAV yields
    /// an empty selection while retention is on and the file is sitting on disk.</item>
    /// </list>
    ///
    /// <para>Naming the controlling setting still answers the owner's ask ("clarify what
    /// happens if prompt logging is not switched on") without asserting its current value —
    /// which this type is deliberately not given, so it cannot get it wrong.</para>
    /// </summary>
    internal const string RawPromptsNoneAvailable =
        "No prompt logs are available to attach. Settings → Diagnostics → " +
        "\"Log prompts and outputs\" controls whether they are recorded; recorded logs are " +
        "deleted after 7 days.";

    /// <summary>
    /// Shown when recordings CAN be included. REL-25 item 4 (owner UAT 2026-08-17, §32.1 Partial):
    /// carries the SAME closing sentence as <see cref="RawPromptsPrivacy"/> — owner: *"the sentence
    /// should get the same, exact same warning"*. Both options attach content the user authored and
    /// may not want to hand over, so stating the consequence for one and not the other made the
    /// quieter one look safer than it is.
    /// <para>Kept as ONE wrapped block rather than a forced second line (the owner left that to us):
    /// it is the shape the raw-prompts advisory already has, and the report dialog renders every
    /// advisory through the same wrapping TextBlock — a hard break here would make these two
    /// siblings render differently for no reason a reader could infer.</para>
    /// </summary>
    internal const string RecordingsPrivacy =
        "These are your actual voice recordings. Include them only if you're comfortable " +
        "sharing that with support.";

    /// <summary>
    /// The recordings twin of <see cref="RawPromptsNoneAvailable"/> — same reasoning, and it
    /// is the one the oversized-file case makes load-bearing. "Available to attach" is true
    /// whether nothing was kept, or something was kept and did not fit the budget.
    /// </summary>
    internal const string RecordingsNoneAvailable =
        "No audio recordings are available to attach. Settings → Diagnostics → " +
        "\"Keep audio recordings\" controls whether they are kept; kept recordings are " +
        "deleted after 7 days.";

    /// <summary>
    /// True only when the user can actually tick the raw-prompt box. Both conditions are
    /// real gates, not belt-and-braces: without traces there is nothing to attach, and
    /// without the logs the bundle refuses the trace anyway.
    /// </summary>
    internal static bool RawPromptsSelectable(bool tracesExist, bool logsIncluded)
        => tracesExist && logsIncluded;

    /// <summary>
    /// The sentence under the raw-prompt box. See the precedence note on the class: the
    /// "nothing recorded" case outranks the "tick logs first" case.
    /// </summary>
    internal static string RawPromptsAdvisory(bool tracesExist, bool logsIncluded)
        => !tracesExist ? RawPromptsNoneAvailable
        : !logsIncluded ? RawPromptsNeedsLogs
        : RawPromptsPrivacy;

    /// <summary>
    /// True when at least one recording was snapshotted at consent time. Written as
    /// <c>&gt; 0</c> rather than <c>!= 0</c> so a nonsensical negative count is refused
    /// rather than treated as "plenty".
    /// </summary>
    internal static bool RecordingsSelectable(int count) => count > 0;

    /// <summary>The sentence under the recordings box.</summary>
    internal static string RecordingsAdvisory(int count)
        => RecordingsSelectable(count) ? RecordingsPrivacy : RecordingsNoneAvailable;

    /// <summary>
    /// The recordings checkbox label. With nothing to send it carries NO counts — a row
    /// reading "(0 file(s), 0.0 MB)" states the obvious twice and reads like a defect.
    /// </summary>
    internal static string RecordingsLabel(int count, double totalMb)
        => RecordingsSelectable(count)
            ? $"Include audio recordings ({count} file(s), {totalMb:0.0} MB)"
            : "Include audio recordings";
}
