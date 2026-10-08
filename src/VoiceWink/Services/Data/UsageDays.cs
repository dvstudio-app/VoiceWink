using System.Globalization;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// How many distinct local calendar days VoiceWink has been used on — a day counts once a dictation
/// or an image finished on it (LNC-14). It decides when the one-time Microsoft Store rating line and
/// the License page's "Leave a review" link appear ("after about seven / five days of real use").
///
/// <para>Recorded by <see cref="LifetimeMetricsService.RecordTranscriptionAsync"/>, the one place every
/// completed dictation and image is already recorded whether or not History is on, inside that
/// service's lock. Counted forward from the first build that has it: History can be off, auto-deleted
/// or wiped, so it is no source to backfill from. Local only — nothing reads it but the two links.</para>
///
/// <para>Two app-managed settings keys (<see cref="AppDefaults.UsageDayCount"/>,
/// <see cref="AppDefaults.UsageLastDay"/>). A day counts when it is LATER than the last one recorded, and
/// the same day never counts twice. A day EARLIER than the last one recorded — the clock is behind it —
/// re-anchors the last day without counting: otherwise a clock that once ran ahead (a dead CMOS battery,
/// a wrong manual date) would leave a future day behind and freeze the count until real time caught up,
/// which can be years. The price is at most one extra day per clock excursion, the harmless direction
/// for a count that only decides when a link appears. An unreadable last day is treated as none.</para>
/// </summary>
public sealed class UsageDays
{
    private const string DayFormat = "yyyy-MM-dd";

    private readonly SettingsService _settings;
    private readonly LocalDayBoundary _boundary;

    public UsageDays(SettingsService settings) : this(settings, LocalDayBoundary.CreateSystem())
    {
    }

    /// <summary>Test seam: an injected zone + clock (see <see cref="LocalDayBoundary"/>).</summary>
    internal UsageDays(SettingsService settings, LocalDayBoundary boundary)
    {
        _settings = settings;
        _boundary = boundary;
    }

    /// <summary>The number of days counted so far; 0 when none, or when the stored value is unreadable.</summary>
    public int Count => Math.Max(0, _settings.GetInt(AppDefaults.UsageDayCount, 0));

    /// <summary>
    /// Count the local day <paramref name="completedAtUtc"/> falls on, if it is later than the last
    /// recorded day; an earlier day only becomes the new last day (see the type's remarks). Called once
    /// per finished dictation or image.
    /// </summary>
    public void Record(DateTime completedAtUtc)
    {
        var day = _boundary.LocalDateOf(completedAtUtc);
        var hasLast = TryReadLastDay(out var last);
        if (hasLast && day == last) return;

        if (!hasLast || day > last)
            _settings.SetInt(AppDefaults.UsageDayCount, Count + 1);
        _settings.SetString(AppDefaults.UsageLastDay, day.ToString(DayFormat, CultureInfo.InvariantCulture));
    }

    private bool TryReadLastDay(out DateTime last) =>
        DateTime.TryParseExact(
            _settings.GetString(AppDefaults.UsageLastDay, string.Empty),
            DayFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out last);
}
