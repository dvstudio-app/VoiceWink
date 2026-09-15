using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// The free-trial window as DEVICE STATE (LIC-21 PR A, owner decision 2026-09-06 — VoiceInk's rule):
/// when the trial started on this device, when the device was last seen running, and the derivation
/// of "never started / active with N remaining / ended" from those two instants. Licensing never
/// writes it: activating a key inside the window neither extends nor cancels it, deactivating
/// returns the remaining days, and no clear path touches it.
///
/// <para><b>This is the orchestrator LIC-11 shipped the pieces for and deliberately did not
/// assemble</b> — <c>read → merge → clock-guard → reseed → write</c> over TWO stores: the settings
/// file (two keys: the first-run stamp, and a last-seen stamp that makes the settings leg a real
/// <see cref="FirstRunTattoo.Reading"/> — synthesising <c>(firstRun, firstRun)</c> would make
/// <see cref="FirstRunTattoo.NeedsReseed"/> true forever, a settings write per hotkey press; the
/// LIC-11 trap) and a durable store behind <see cref="ITrialStampStore"/> (the registry in
/// production) that survives a folder wipe (pinned by <c>TrialWindowTests</c>; measured as owner UAT
/// 180.11 on the signed v1.78.370, 2026-09-07) and a reinstall (owner UAT 180.12, same build — the
/// trial resumed; INS-1 keeps the data folder across an uninstall, so the wipe is the proof for the
/// registry store and the reinstall the proof for the window). Read = earliest first-run wins, latest last-seen wins
/// (<see cref="FirstRunTattoo.MergeStored"/>); every store found missing or behind is re-seeded,
/// which is what makes wiping any single store useless.</para>
///
/// <para><b>Locking (Codex plan round, PR A).</b> The merged window is an immutable
/// <see cref="Snapshot"/> behind a volatile field: <see cref="Read"/> is lock-free, so the
/// per-hotkey <c>GetCachedStatus</c> classification never takes a lock, and — the point — the
/// classification that <c>LicenseService</c> runs INSIDE <c>_stateWriteLock</c> (validate
/// application, activation persist, rollback) never nests this type's lock inside it. Writes
/// (<see cref="Start"/>, the hourly last-seen persist, initialisation) take <c>_lock</c>, and
/// <see cref="Observe"/> is called only from the UNLOCKED entry points, so the two locks are never
/// held together in either order.</para>
///
/// <para><b>Clock guard.</b> <c>EffectiveNow = max(now, lastSeen)</c>: winding the clock back cannot
/// stretch the window. <see cref="Observe"/> advances last-seen on every status read whether or not a
/// key is stored (PR B plan round: activate day 2, use to day 8, roll the clock to day 3, deactivate
/// ⇒ ended, not five days of trial), and persists it at most once per
/// <see cref="LastSeenPersistInterval"/>. <b>The clamp of a future first-run against <c>now</c> is
/// applied at READ time only</b> (<see cref="FirstRunTattoo.Elapsed"/>) — the snapshot and both
/// stores hold the RAW merged reading (<see cref="FirstRunTattoo.MergeStored"/>), so a boot under a
/// backdated clock reads Ended while the clock is wrong and resumes when it is fixed, instead of
/// writing the clamped first-run into the stores and ending the trial for good (PR A self-review,
/// correctness lens). Two accepted residuals, both fail-CLOSED and both the same class as LIC-11's:
/// a forward clock fault while the app runs stamps a far-future last-seen; and a trial STARTED
/// under a backdated clock is measured from that start once the clock is corrected. Recovery for
/// either is manual and clears BOTH legs with the app closed — <see cref="FirstRunTattoo.ClearAll"/>
/// for the registry store plus the two settings keys; <see cref="FirstRunTattoo.ClearAll"/> alone
/// leaves the settings leg, which re-seeds the registry at the next launch, and a running instance
/// re-persists its snapshot at the next hourly write.</para>
///
/// <para><b>Legacy migration — the owner's policy (a), confirmed 2026-09-06.</b> Pre-PR-A releases
/// cleared the trial stamp at activation and at deactivation and never wrote the durable store, so a
/// machine that used the old window, activated a key and later deactivates would present as a fresh
/// device and receive a NEW 7-day window (the fail-open the PR B plan round named). At the first
/// observation with NO reading in any store, settings carrying any activation evidence (a key, an
/// instance id, a last-validated stamp or a machine id) seed the window CONSUMED (first-run =
/// now − duration). <b>A durable marker records that the decision was taken, whichever way</b>
/// (Codex plan round, PR A): without it a post-PR-A user who activated a paid key before ever
/// starting the trial would present the same evidence at their next launch and lose an untouched
/// trial. Once marked, the legacy rule never applies again; a fresh device records only the marker.
/// Residual, stated: a marker whose flush fails under CR-1 protect mode is retried at the next
/// launch, so that one paid-first user could still be seeded consumed — a lost free trial beside a
/// paid key, in the safe direction.</para>
///
/// <para><b>Failure memo.</b> A durable store that answers <see cref="TrialStampReadOutcome.Unavailable"/>
/// or refuses a write logs ONE Warning (it rides to Sentry once) and is not touched again this
/// process; the settings leg keeps the trial working. Missing is silent: it is what a fresh device
/// and a reinstalled Windows both look like.</para>
/// </summary>
internal sealed class TrialWindow
{
    private static ILogger Logger => Log.ForContext<TrialWindow>();

    /// <summary>How often the advancing last-seen stamp is written back to both stores.</summary>
    internal static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromHours(1);

    internal enum State { NeverStarted, Active, Ended }

    /// <summary>Immutable merged window; a new instance replaces the field on every change.</summary>
    private sealed record Snapshot(DateTimeOffset FirstRun, DateTimeOffset LastSeen)
    {
        public FirstRunTattoo.Reading AsReading => new(FirstRun, LastSeen);
    }

    private readonly SettingsService _settings;
    private readonly ITrialStampStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _duration;

    private readonly object _lock = new();
    private Snapshot? _window;
    private DateTimeOffset _lastPersistedSeen;
    private bool _storeFaulted;

    /// <param name="store">The durable leg, or <c>null</c> for settings only (tests, the LS harness).</param>
    internal TrialWindow(SettingsService settings, ITrialStampStore? store, Func<DateTimeOffset> clock, TimeSpan duration)
    {
        _settings = settings;
        _store = store;
        _clock = clock;
        _duration = duration;
        Initialize();
    }

    /// <summary>
    /// The window's state at <paramref name="now"/> — lock-free, no I/O, no write. Safe to call
    /// inside <c>LicenseService._stateWriteLock</c>. <c>Remaining</c> is non-null only while Active.
    /// </summary>
    internal (State State, TimeSpan? Remaining) Read(DateTimeOffset now)
    {
        var w = Volatile.Read(ref _window);
        if (w is null) return (State.NeverStarted, null);
        var remaining = _duration - FirstRunTattoo.Elapsed(w.AsReading, now);
        return remaining > TimeSpan.Zero ? (State.Active, remaining) : (State.Ended, null);
    }

    /// <summary>
    /// Record that the device was seen running at <paramref name="now"/> — the clock guard's input.
    /// Called from every UNLOCKED status read; a no-op while no window exists and whenever the
    /// clock has not moved forward past the recorded last-seen. Persists at most hourly.
    /// </summary>
    internal void Observe(DateTimeOffset now)
    {
        var w = Volatile.Read(ref _window);
        if (w is null || now <= w.LastSeen) return;
        lock (_lock)
        {
            w = _window;
            if (w is null || now <= w.LastSeen) return;
            _window = w with { LastSeen = now };
            if (now - _lastPersistedSeen >= LastSeenPersistInterval) Persist();
        }
    }

    /// <summary>
    /// Start the window at <paramref name="now"/>. Returns <c>false</c> (writing nothing) when a
    /// window already exists — Active or Ended alike; a corrupt settings stamp reads as never started
    /// everywhere, so a real start overwrites it.
    /// </summary>
    internal bool Start(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_window is not null) return false;
            _window = new Snapshot(now, now);
            Persist();
            return true;
        }
    }

    // ── Initialisation: read → merge → (legacy seed) → reseed ─────────────────

    private void Initialize()
    {
        lock (_lock)
        {
            var now = _clock();
            var settingsReading = ReadSettings();
            var storeReading = ReadStore();
            // The RAW merge: what the stores held is what they are re-seeded from. The clamp
            // against `now` is applied at read time (FirstRunTattoo.Elapsed), never here — a
            // persisted clamp would let one backdated boot end the trial for good (type doc).
            var merged = FirstRunTattoo.MergeStored(new[] { settingsReading, storeReading.Value });

            if (merged is null)
            {
                // No reading anywhere. Take the legacy decision exactly once per device; a fresh
                // device on any later launch writes nothing until Start.
                if (MigrationDecided()) return;
                if (HasActivationEvidence())
                {
                    _window = new Snapshot(now - _duration, now);
                    Logger.Information("Free trial seeded as used: this device activated a license before the trial window was tracked durably");
                    // The seed BEFORE the marker: the marker's flush then carries the seed's settings
                    // write with it. The other order left a window where the marker was durable and
                    // the seed was not (the debounce, with the registry leg unavailable), so the next
                    // launch skipped the legacy rule and handed the pre-PR-A user a fresh window.
                    Persist();
                }
                RecordMigrationDecided(now);
                return;
            }

            _window = new Snapshot(merged.Value.FirstRun, merged.Value.LastSeen);
            _lastPersistedSeen = merged.Value.LastSeen;
            // Self-healing: every store that is missing or behind the merged truth is re-seeded,
            // which is what makes wiping any single store useless. The settings leg is a real
            // Reading (two keys), so a settings file that already matches is left alone.
            var settingsNeeds = FirstRunTattoo.NeedsReseed(settingsReading, merged.Value);
            var storeNeeds = storeReading.Outcome != TrialStampReadOutcome.Unavailable
                             && FirstRunTattoo.NeedsReseed(storeReading.Value, merged.Value);
            if (settingsNeeds) WriteSettings(merged.Value);
            if (storeNeeds) WriteStore(merged.Value);
            if (!MigrationDecided()) RecordMigrationDecided(now); // a live stamp: nothing to seed, still decided
        }
    }

    private FirstRunTattoo.Reading? ReadSettings()
    {
        // The first-run stamp is the pre-PR-A key, so an old profile's live window carries over
        // (earliest wins against the durable store). A stamp that does not parse reads as never
        // started — the polarity every reader has always had: a corrupt value offers the trial
        // rather than refusing it. The last-seen key is new; absent, it defaults to the first-run.
        var rawFirst = _settings.GetString(AppDefaults.LicenseFirstRunGraceStartedUtc, "");
        if (!DateTimeOffset.TryParse(rawFirst, null, global::System.Globalization.DateTimeStyles.RoundtripKind, out var first))
            return null;
        var rawSeen = _settings.GetString(AppDefaults.LicenseFirstRunGraceLastSeenUtc, "");
        var seen = DateTimeOffset.TryParse(rawSeen, null, global::System.Globalization.DateTimeStyles.RoundtripKind, out var parsedSeen)
            ? parsedSeen
            : first;
        return new FirstRunTattoo.Reading(first, seen);
    }

    private TrialStampReading ReadStore()
    {
        if (_store is null) return TrialStampReading.Missing;
        TrialStampReading reading;
        try { reading = _store.Read(); }
        catch (Exception ex)
        {
            Logger.Debug("Trial stamp store read threw ({ExType})", ex.GetType().Name);
            reading = TrialStampReading.Unavailable;
        }
        if (reading.Outcome == TrialStampReadOutcome.Unavailable) NoteStoreFaulted("read");
        return reading;
    }

    private void WriteStore(FirstRunTattoo.Reading reading)
    {
        if (_store is null || _storeFaulted) return;
        bool ok;
        try { ok = _store.Write(reading); }
        catch (Exception ex)
        {
            Logger.Debug("Trial stamp store write threw ({ExType})", ex.GetType().Name);
            ok = false;
        }
        if (!ok) NoteStoreFaulted("write");
    }

    private void NoteStoreFaulted(string operation)
    {
        if (_storeFaulted) return;
        _storeFaulted = true;
        // Once per process: the same failure would otherwise Warn — and reach Sentry — at every
        // hourly persist and every start for the rest of the session (the LIC-11 trap).
        Logger.Warning("The durable trial stamp store is unavailable ({Operation}); the free trial continues from settings alone for this session", operation);
    }

    private void WriteSettings(FirstRunTattoo.Reading reading)
    {
        _settings.SetString(AppDefaults.LicenseFirstRunGraceStartedUtc, reading.FirstRun.ToString("O"));
        _settings.SetString(AppDefaults.LicenseFirstRunGraceLastSeenUtc, reading.LastSeen.ToString("O"));
    }

    // Must be called under _lock with a non-null window.
    private void Persist()
    {
        var reading = _window!.AsReading;
        WriteSettings(reading);
        WriteStore(reading);
        _lastPersistedSeen = reading.LastSeen;
    }

    private bool HasActivationEvidence() =>
        !string.IsNullOrEmpty(_settings.GetString(AppDefaults.LicenseKey, ""))
        || !string.IsNullOrEmpty(_settings.GetString(AppDefaults.LicenseInstanceId, ""))
        || !string.IsNullOrEmpty(_settings.GetString(AppDefaults.LicenseLastValidatedUtc, ""))
        || !string.IsNullOrEmpty(_settings.GetString(AppDefaults.LicenseMachineId, ""));

    private bool MigrationDecided() =>
        !string.IsNullOrEmpty(_settings.GetString(AppDefaults.LicenseTrialMigrationDecidedUtc, ""));

    private void RecordMigrationDecided(DateTimeOffset now)
    {
        _settings.SetString(AppDefaults.LicenseTrialMigrationDecidedUtc, now.ToString("O"));
        // Best-effort durability: the debounced save is fail-quiet, and the decision must not be
        // re-taken next launch for a post-PR-A paid-first user. Flush() rather than FlushOrThrow():
        // this runs from the service constructor, where a throw would take the app down for a
        // marker; the residual is stated in the type doc.
        try { _settings.Flush(); }
        catch (Exception ex) { Logger.Debug("Trial migration marker flush failed ({ExType})", ex.GetType().Name); }
    }
}
