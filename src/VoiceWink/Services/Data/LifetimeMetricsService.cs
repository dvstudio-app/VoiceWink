using System.Threading;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// Tracks lifetime usage metrics independently from deletable transcription history.
/// Seeds once from existing history so upgrades keep prior totals. "Today" is the USER'S
/// LOCAL calendar day via <see cref="LocalDayBoundary"/> (G3, 2026-07-14 — the old UTC-date
/// comparison rolled the tile at 01:00/02:00 Belgian time).
/// </summary>
public sealed class LifetimeMetricsService
{
    private static ILogger Logger => Log.ForContext<LifetimeMetricsService>();

    private readonly SettingsService _settings;
    private readonly TranscriptionHistoryService _history;
    private readonly LocalDayBoundary _boundary;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public event Action? MetricsChanged;

    public LifetimeMetricsService(SettingsService settings, TranscriptionHistoryService history)
        : this(settings, history, LocalDayBoundary.CreateSystem())
    {
    }

    /// <summary>Test seam: injected zone + clock (see <see cref="LocalDayBoundary"/>).</summary>
    internal LifetimeMetricsService(SettingsService settings, TranscriptionHistoryService history, LocalDayBoundary boundary)
    {
        _settings = settings;
        _history = history;
        _boundary = boundary;
    }

    public Task InitializeAsync(CancellationToken ct = default) => EnsureInitializedAsync(ct);

    public async Task<MetricsAggregate> GetAggregateStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = Snapshot();
            var raw = ReadStateUnsafe(snap);
            var current = NormalizeToday(MigrateIfLegacy(raw, snap), snap);
            if (current != raw)
                WriteStateUnsafe(current);
            return ToAggregate(current);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task RecordTranscriptionAsync(string metricsText, DateTime timestampUtc, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = Snapshot();
            var state = NormalizeToday(MigrateIfLegacy(ReadStateUnsafe(snap), snap), snap);
            var metricsTimestampUtc = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();

            state = state with
            {
                Total = state.Total + 1,
                TotalCharacters = state.TotalCharacters + metricsText.Length,
                TotalWords = state.TotalWords + CountWords(metricsText),
                Today = _boundary.LocalDateOf(metricsTimestampUtc) == snap.TodayLocal
                    ? state.Today + 1
                    : state.Today
            };

            WriteStateUnsafe(state);
        }
        finally
        {
            _stateLock.Release();
        }

        MetricsChanged?.Invoke();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (TryReadState(out _))
            return;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryReadState(out _))
                return;

            var seeded = await SeedFromHistoryAsync(ct).ConfigureAwait(false);
            WriteStateUnsafe(seeded);
            Logger.Information("Initialized lifetime metrics from existing history ({Count} records)", seeded.Total);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<LifetimeMetricsState> SeedFromHistoryAsync(CancellationToken ct)
    {
        // ONE day snapshot for the whole seed: the same local date feeds the history
        // query AND the persisted stamp, so a midnight crossing during the DB awaits
        // can't store day D's count under day D+1 (Codex diff review R1). The boundary
        // overload, NOT the public production-clock one — the seed's Today must agree
        // with this service's injected zone/clock (Codex plan review R2).
        var snap = Snapshot();
        var aggregate = await _history.GetAggregateStatsAsync(_boundary, snap.TodayLocal, ct).ConfigureAwait(false);
        return new LifetimeMetricsState
        {
            TodayDateLocal = snap.TodayLocal,
            Total = aggregate.Total,
            Today = aggregate.Today,
            TotalCharacters = aggregate.TotalCharacters,
            TotalWords = aggregate.TotalWords
        };
    }

    /// <summary>One clock read per locked operation — the UTC instant and the local day
    /// it falls on, taken together so migration, normalization, membership tests, and
    /// persisted stamps can never straddle a midnight (Codex diff review R1).</summary>
    private DaySnapshot Snapshot()
    {
        var utcNow = _boundary.UtcNow();
        return new DaySnapshot(utcNow, _boundary.LocalDateOf(utcNow));
    }

    private readonly record struct DaySnapshot(DateTime UtcNow, DateTime TodayLocal);

    private static MetricsAggregate ToAggregate(LifetimeMetricsState state)
    {
        return new MetricsAggregate
        {
            Total = state.Total,
            Today = state.Today,
            TotalCharacters = state.TotalCharacters,
            TotalWords = state.TotalWords
        };
    }

    /// <summary>
    /// One-time upgrade of a pre-local-day persisted state (only <c>TodayDateUtc</c>
    /// stamped). The Today count is preserved iff the old state was still counting
    /// "today" BY ITS OWN UTC RULE; the new local stamp is written either way, and the
    /// legacy field is left as-is (harmless — never read again once the local stamp
    /// exists). Within the hours where the UTC date and the local date disagree this
    /// preserves a count local rules would have split differently — accepted one-time
    /// imprecision on a metrics counter.
    /// </summary>
    private static LifetimeMetricsState MigrateIfLegacy(LifetimeMetricsState state, DaySnapshot snap)
    {
        if (state.TodayDateLocal != DateTime.MinValue)
            return state; // already local-day format
        if (state.TodayDateUtc == DateTime.MinValue)
            return state; // empty/fresh — seeding stamps it

        var oldRuleStillToday = state.TodayDateUtc.Date == snap.UtcNow.Date;
        return state with
        {
            TodayDateLocal = snap.TodayLocal,
            Today = oldRuleStillToday ? state.Today : 0
        };
    }

    private static LifetimeMetricsState NormalizeToday(LifetimeMetricsState state, DaySnapshot snap)
    {
        if (state.TodayDateLocal == snap.TodayLocal)
            return state;

        return state with
        {
            TodayDateLocal = snap.TodayLocal,
            Today = 0
        };
    }

    private bool TryReadState(out LifetimeMetricsState state)
    {
        state = _settings.Get<LifetimeMetricsState?>(AppDefaults.LifetimeMetricsState, null)
            ?? LifetimeMetricsState.Empty;
        return state != LifetimeMetricsState.Empty;
    }

    private LifetimeMetricsState ReadStateUnsafe(DaySnapshot snap)
    {
        if (TryReadState(out var state))
            return state;

        return new LifetimeMetricsState
        {
            TodayDateLocal = snap.TodayLocal
        };
    }

    private void WriteStateUnsafe(LifetimeMetricsState state)
    {
        _settings.Set(AppDefaults.LifetimeMetricsState, state);
    }

    private static long CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed record LifetimeMetricsState
    {
        public static LifetimeMetricsState Empty { get; } = new()
        {
            TodayDateUtc = DateTime.MinValue,
            TodayDateLocal = DateTime.MinValue
        };

        /// <summary>Legacy day stamp (UTC rule, pre-2026-07-14). Kept ONLY so old
        /// persisted JSON deserializes for <see cref="MigrateIfLegacy"/>; never written
        /// meaningfully again.</summary>
        public DateTime TodayDateUtc { get; init; }

        /// <summary>The local calendar day <see cref="Today"/> counts toward
        /// (Kind Unspecified, date-only). MinValue = legacy or empty state.</summary>
        public DateTime TodayDateLocal { get; init; }

        public int Total { get; init; }
        public int Today { get; init; }
        public long TotalCharacters { get; init; }
        public long TotalWords { get; init; }
    }
}
