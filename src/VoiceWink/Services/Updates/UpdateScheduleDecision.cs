using System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Pure cadence constants + decision for the UPD-1b background poll. No timers or I/O, so the
/// rule is unit-testable — same pattern as <see cref="VoiceWink.Helpers.MiniRecorderRecreateGate"/>.
///
/// <para><b>Cadence:</b> the scheduler checks <b>~30s after every app start</b>
/// (<see cref="InitialDelay"/> — deferred so it never competes with startup I/O, model
/// preload, or hotkey registration; 60s→30s per user request 2026-07-05, UPD-3b) and then
/// <b>every 24h</b> (<see cref="Interval"/>) for
/// the rest of a long-running session. There is <b>no cross-restart suppression</b>: each
/// launch re-checks. (Revised 2026-06-03 from the original "at most once per 24h across
/// restarts" — startups are infrequent for a desktop dictation app, a prompt check on every
/// launch is the expected behavior, and the manifest fetch is a tiny CF-Access-gated GET.
/// <see cref="IUpdateService.LastCheckedUtc"/> is still written by the service for the
/// "Last checked" UI line; it just no longer gates the schedule.)</para>
/// </summary>
internal static class UpdateScheduleDecision
{
    /// <summary>Deferral before the first check of a session (so app launch isn't impacted).</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>Spacing between checks within a single long-running session.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Delay before the next automatic check: <see cref="InitialDelay"/> for the first check
    /// of a session (~30s after launch — on every launch), then <see cref="Interval"/> (24h)
    /// for each subsequent check while the app keeps running.
    /// </summary>
    public static TimeSpan NextDelay(bool isFirstCheck) => isFirstCheck ? InitialDelay : Interval;
}
