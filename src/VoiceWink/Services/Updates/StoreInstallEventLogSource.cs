using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Reads the Microsoft Store's install-state events for a time window. The seam that keeps every test
/// away from the real Windows event log.
/// </summary>
public interface IStoreInstallEventSource
{
    /// <summary>The id-5 rows Windows recorded between <paramref name="fromUtc"/> and <paramref name="toUtc"/>.
    /// May throw; the caller treats any exception as "not detected".</summary>
    IReadOnlyList<StoreInstallEvent> Read(DateTime fromUtc, DateTime toUtc);
}

/// <summary>
/// Production <see cref="IStoreInstallEventSource"/> over <c>Microsoft-Windows-Store/Operational</c>.
///
/// <para>The query filters on the event id AND the time window, so the read is a handful of records out
/// of a log that holds tens of thousands (measured 2026-09-24: 20 MB circular, ~25k records, about 2.5
/// days of history on a machine whose Store updates its apps). Other products' events in the window
/// are skipped as they are read, so a Store update burst cannot crowd VoiceWink's line out of
/// <see cref="MaxRows"/>; <see cref="MaxScanned"/> bounds the work regardless. Readable by a normal,
/// non-elevated user (owner-measured). Only the first data value is taken — the Store's own raw line
/// (see <see cref="InstallSourceClassifier"/>) — never the rendered message.</para>
///
/// <para>A read that fails part-way (a circular log can overwrite the query's position mid-read)
/// still throws: the caller then saves nothing and tries again at the next automatic check, rather
/// than recording "not detected" for good on a transient fault.</para>
/// </summary>
internal sealed class StoreInstallEventLogSource : IStoreInstallEventSource
{
    internal const string LogName = "Microsoft-Windows-Store/Operational";
    internal const int MaxRows = 200;
    internal const int MaxScanned = 5000;

    public IReadOnlyList<StoreInstallEvent> Read(DateTime fromUtc, DateTime toUtc)
    {
        var query = new EventLogQuery(LogName, PathType.LogName, BuildXPath(fromUtc, toUtc));
        var rows = new List<StoreInstallEvent>();
        using var reader = new EventLogReader(query);
        for (var scanned = 0; scanned < MaxScanned && rows.Count < MaxRows; scanned++)
        {
            using var record = reader.ReadEvent();
            if (record is null) break;
            if (record.TimeCreated is not { } created) continue;
            var props = record.Properties;
            var data0 = props is { Count: > 0 } ? props[0].Value as string : null;
            if (data0 is null
                || data0.IndexOf(InstallSourceClassifier.StoreProductId, StringComparison.OrdinalIgnoreCase) < 0)
                continue; // another product's line: inspected here, never kept
            rows.Add(new StoreInstallEvent(created.ToUniversalTime(), record.Id, data0));
        }
        return rows;
    }

    /// <summary>The XPath filter: event id 5, created inside the window (both edges inclusive).</summary>
    internal static string BuildXPath(DateTime fromUtc, DateTime toUtc)
    {
        static string Stamp(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return "*[System[(EventID=" + InstallSourceClassifier.InstallStateEventId.ToString(CultureInfo.InvariantCulture)
            + ") and TimeCreated[@SystemTime>='" + Stamp(fromUtc) + "' and @SystemTime<='" + Stamp(toUtc) + "']]]";
    }
}
