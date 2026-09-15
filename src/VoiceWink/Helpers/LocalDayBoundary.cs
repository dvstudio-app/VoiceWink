namespace VoiceWink.Helpers;

/// <summary>
/// Owns the "which local calendar day does a UTC instant belong to" question for the
/// metrics surfaces (G3/G4, 2026-07-14). "Today" means the USER'S local day — the old
/// UTC-date comparison rolled the Metrics tile at 01:00/02:00 Belgian time and
/// mid-afternoon for west-of-UTC users. The <see cref="global::System.TimeZoneInfo"/>
/// AND the clock are injected because a clock seam alone cannot make day-boundary tests
/// deterministic: <c>DateTime.ToLocalTime()</c> binds to the machine zone (Codex plan
/// review R1). Local dates returned by this type are date-only,
/// <see cref="global::System.DateTimeKind.Unspecified"/>.
/// </summary>
public sealed class LocalDayBoundary
{
    private readonly global::System.TimeZoneInfo _zone;
    private readonly Func<DateTime> _utcNow;

    public LocalDayBoundary(global::System.TimeZoneInfo zone, Func<DateTime> utcNow)
    {
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>Machine zone + real clock. Built per-consumer (not cached) so a zone
    /// change picked up by .NET after <c>TimeZoneInfo.ClearCachedData</c> is honored
    /// by newly-constructed services.</summary>
    public static LocalDayBoundary CreateSystem() =>
        new(global::System.TimeZoneInfo.Local, () => DateTime.UtcNow);

    /// <summary>The injected clock — exposed so consumers (the lifetime-metrics
    /// legacy-state migration) never mix a seamed local day with the real UTC day.</summary>
    public DateTime UtcNow() => _utcNow();

    /// <summary>The local calendar date the given UTC instant falls on. An
    /// <see cref="global::System.DateTimeKind.Unspecified"/> input is treated as UTC
    /// (SQLite round-trips stored UTC values as Unspecified); a Local input is converted
    /// first.</summary>
    public DateTime LocalDateOf(DateTime utc)
    {
        var instant = utc.Kind == global::System.DateTimeKind.Local
            ? utc.ToUniversalTime()
            : DateTime.SpecifyKind(utc, global::System.DateTimeKind.Utc);
        var local = global::System.TimeZoneInfo.ConvertTimeFromUtc(instant, _zone);
        return DateTime.SpecifyKind(local.Date, global::System.DateTimeKind.Unspecified);
    }

    /// <summary>Today's local calendar date under the injected clock + zone.</summary>
    public DateTime TodayLocalDate() => LocalDateOf(_utcNow());

    /// <summary>
    /// The half-open UTC interval [start, end) covering one local calendar day — the
    /// query shape for "records belonging to this local day" (half-open so future-dated
    /// rows are excluded and adjacent days never overlap). DST-safe: a day may be 23 or
    /// 25 hours long, and midnights skipped or repeated by a transition are handled
    /// explicitly below.
    /// </summary>
    public (DateTime StartUtc, DateTime EndUtcExclusive) DayIntervalUtc(DateTime localDate)
    {
        var start = DayStartUtc(localDate.Date);
        var end = DayStartUtc(localDate.Date.AddDays(1));
        return (start, end);
    }

    /// <summary>UTC instant at which the given local calendar day begins.</summary>
    private DateTime DayStartUtc(DateTime localDate)
    {
        var midnight = DateTime.SpecifyKind(localDate.Date, global::System.DateTimeKind.Unspecified);

        // Spring-forward AT midnight (real zones do this): local 00:00 does not exist —
        // the day begins at the first valid local time after the gap. Probe in 15-minute
        // steps up to a full 25 hours: civil-time skips can exceed a DST hour (Samoa
        // skipped an entire calendar day in 2011 — the probe then lands on the NEXT
        // day's midnight, yielding an empty [x, x) interval, which is correct for a day
        // that never existed). Verify validity before converting; fail loudly rather
        // than let ConvertTimeToUtc throw an unexplained ArgumentException.
        if (_zone.IsInvalidTime(midnight))
        {
            var candidate = midnight;
            for (var i = 0; i < 100 && _zone.IsInvalidTime(candidate); i++)
                candidate = candidate.AddMinutes(15);
            if (_zone.IsInvalidTime(candidate))
                throw new global::System.InvalidOperationException(
                    $"No valid local time within 25 hours of {localDate:yyyy-MM-dd} 00:00 in zone '{_zone.Id}'.");
            return global::System.TimeZoneInfo.ConvertTimeToUtc(candidate, _zone);
        }

        // Fall-back crossing midnight: local 00:00 occurs twice. Take the FIRST
        // occurrence (the larger offset = the earlier UTC instant) so consecutive day
        // intervals neither gap nor overlap. ConvertTimeToUtc would silently pick the
        // standard-time (second) occurrence.
        if (_zone.IsAmbiguousTime(midnight))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(midnight);
            var first = offsets[0];
            foreach (var offset in offsets)
                if (offset > first) first = offset;
            return DateTime.SpecifyKind(midnight - first, global::System.DateTimeKind.Utc);
        }

        return global::System.TimeZoneInfo.ConvertTimeToUtc(midnight, _zone);
    }
}
