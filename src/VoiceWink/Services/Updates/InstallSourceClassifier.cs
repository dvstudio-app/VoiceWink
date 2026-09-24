using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoiceWink.Services.Updates;

/// <summary>How this installation of VoiceWink was installed, as far as the app can tell.</summary>
public enum InstallSource
{
    /// <summary>No matching Store event was found, or it could not be looked for (every failure lands here).
    /// Not evidence of any other install source.</summary>
    NotDetected,

    /// <summary>The Microsoft Store recorded VoiceWink's product finishing its installation at this install's time.</summary>
    MsStore,
}

/// <summary>One row read from the Windows Store's own event log.</summary>
/// <param name="TimeCreatedUtc">When Windows recorded the event, UTC.</param>
/// <param name="EventId">The event id; only 5 is the install-state transition.</param>
/// <param name="Data0">The event's FIRST data value — the Store's own raw line, e.g.
/// <c>Install state changing for product XP9K0LN0WBCPT0: Installing =&gt; Completed</c>.</param>
public readonly record struct StoreInstallEvent(DateTime TimeCreatedUtc, int EventId, string? Data0);

/// <summary>
/// The pure decision behind the install-source signal (dvstudio-metrics #85): was THIS installation
/// done by the Microsoft Store?
///
/// <para><b>Why the event log.</b> The Store runs the Velopack installer with <c>--silent</c>, which
/// does not launch the app, so no installer argument can ever reach it, and the installed copy is
/// byte-identical to a website install. What exists is the Store's own log,
/// <c>Microsoft-Windows-Store/Operational</c>: event id 5 carries
/// <c>Install state changing for product &lt;id&gt;: &lt;from&gt; =&gt; &lt;to&gt;</c>. Id 4 entries for the
/// same product are written by merely VIEWING the Store page, so only the id-5 transition to
/// <c>Completed</c> counts.</para>
///
/// <para><b>Why <see cref="StoreInstallEvent.Data0"/> and not the rendered message.</b> Measured
/// 2026-09-24: the event's first data value IS the whole line, a raw string from the Store's own code;
/// the rendered message wraps it in a localizable template. Matching the data value keeps the rule
/// independent of the Windows display language.</para>
///
/// <para><b>The window.</b> The Store downloads, then runs Setup (which creates the install root), then
/// logs <c>Completed</c> when Setup exits — so the event follows the root's creation by the Setup's
/// duration. <see cref="WindowBefore"/> absorbs clock and rename slop; <see cref="WindowAfter"/> a slow
/// install. Generous on purpose: a false positive needs a Store install of VoiceWink and a separate
/// install from elsewhere inside the same hour.</para>
/// </summary>
public static class InstallSourceClassifier
{
    /// <summary>VoiceWink's Microsoft Store product ID.</summary>
    public const string StoreProductId = "XP9K0LN0WBCPT0";

    /// <summary>The event id of the Store's install-state transition line.</summary>
    public const int InstallStateEventId = 5;

    public static readonly TimeSpan WindowBefore = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WindowAfter = TimeSpan.FromMinutes(60);

    // \z, never $: .NET's $ also matches before a trailing newline.
    private static readonly Regex TransitionLine = new(
        @"^Install state changing for product (?<id>\S+): (?<from>\S+) => Completed\z",
        RegexOptions.CultureInvariant);

    /// <summary>The event-log time range worth reading for an install root created at <paramref name="installRootCreatedUtc"/>.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) Window(DateTime installRootCreatedUtc)
        => (installRootCreatedUtc - WindowBefore, installRootCreatedUtc + WindowAfter);

    /// <summary>
    /// <see cref="InstallSource.MsStore"/> iff some row is an id-5 transition of VoiceWink's product to
    /// <c>Completed</c> inside the window (both edges inclusive); <see cref="InstallSource.NotDetected"/> otherwise.
    /// </summary>
    public static InstallSource Classify(DateTime installRootCreatedUtc, IEnumerable<StoreInstallEvent> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var (from, to) = Window(installRootCreatedUtc);
        foreach (var row in rows)
        {
            if (row.EventId != InstallStateEventId) continue;
            if (row.TimeCreatedUtc < from || row.TimeCreatedUtc > to) continue;
            if (row.Data0 is not { } line) continue;
            var m = TransitionLine.Match(line);
            if (m.Success && string.Equals(m.Groups["id"].Value, StoreProductId, StringComparison.OrdinalIgnoreCase))
                return InstallSource.MsStore;
        }
        return InstallSource.NotDetected;
    }
}
