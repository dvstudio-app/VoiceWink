using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceWink.Services.Updates;

/// <summary>
/// In-app surface for "is there a newer version of VoiceWink available?"
/// — wraps Velopack's <c>UpdateManager</c> behind a test seam
/// (<see cref="IVelopackUpdateSource"/>) and applies VoiceWink's gating
/// rules (UPDATE_CHECK_ENABLED build flag + the user-facing
/// <c>AutomaticUpdateCheckEnabled</c> toggle) before any network call.
///
/// <para>Surface: a manual "Check for updates" button (always available),
/// <see cref="ApplyAndRestartAsync"/> (UPD-1 Phase 3), and the automatic
/// background poll driven by <see cref="IUpdateScheduler"/> (UPD-1b — 30s
/// after launch, then every 24h). <see cref="ApplyAndRestartAsync"/> blocks
/// via <c>IMaintenanceGate</c> while recording / transcribing / downloading
/// so an apply can't interrupt active work.</para>
///
/// <para>The apply path uses Velopack's <c>WaitExitThenApplyUpdates</c> (NOT
/// <c>ApplyUpdatesAndRestart</c>) so VoiceWink's normal shutdown runs —
/// settings flush, DB checkpoint, etc. — before the updater swaps the binary.</para>
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Application version baked into the running build, in
    /// <c>major.minor.patch</c> form. Sourced from
    /// <see cref="System.Reflection.AssemblyName.Version"/> on the entry
    /// assembly so it stays in lockstep with the csproj <c>&lt;Version&gt;</c>.
    /// Surfaced by the Settings → Updates page as "Currently on version X.Y.Z".
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// UTC timestamp of the last attempted update check (success or error),
    /// or <c>null</c> if no check has run in this profile yet. Persisted via
    /// <see cref="Helpers.AppDefaults.LastUpdateCheckUtc"/> so it survives
    /// across launches.
    /// </summary>
    DateTime? LastCheckedUtc { get; }

    /// <summary>
    /// Version string of the update found by the most recent
    /// <see cref="CheckForUpdatesAsync"/> that returned
    /// <see cref="UpdateCheckOutcome.UpdateAvailable"/> and has not since been applied
    /// or superseded; <c>null</c> when nothing is pending.
    ///
    /// <para>This is the bridge that makes a <b>background</b>-discovered update
    /// actionable. The Updates page's <see cref="VoiceWink.ViewModels.UpdateViewModel"/>
    /// is transient (re-created on every navigation), so when a scheduler tick
    /// (UPD-1b) finds an update while the page is closed, the VM has no other way to
    /// learn about it on open — it seeds its "Apply" affordance from this property.
    /// Backed by the same memoized pending-update state the apply path keys off, so it
    /// is always consistent with what <see cref="ApplyAndRestartAsync"/> would act on.</para>
    /// </summary>
    string? PendingUpdateVersion { get; }

    /// <summary>
    /// True while an apply owns the apply guard (download → hand-off) — including the
    /// abandoned-download state, where a stalled Velopack download ignored cancellation and the
    /// guard is deliberately held until the orphaned task completes (or the process restarts).
    /// Transient VMs seed their "Applying…" UI from this so navigation can't lose in-flight
    /// state (UPD-2).
    /// </summary>
    bool IsApplyInFlight { get; }

    /// <summary>
    /// Download progress (0–100) of the in-flight apply, or null when idle / before the first
    /// progress callback. Snapshot companion to <see cref="ApplyProgressChanged"/>.
    /// </summary>
    int? ApplyProgressPercent { get; }

    /// <summary>
    /// Raised when the in-flight apply's download progress changes (percent, 0–100). May fire
    /// on thread-pool threads — subscribers marshal to the UI thread themselves.
    /// </summary>
    event Action<int>? ApplyProgressChanged;

    /// <summary>
    /// Raised exactly once for every <see cref="ApplyAndRestartAsync"/> call that WINS the apply
    /// guard and terminates with a result — Blocked (pre- or post-download), NotPending, Error
    /// (including both stall paths), and ApplyInitiated alike. NOT raised for a call the guard
    /// rejects (it never became in-flight), and not raised on caller cancellation
    /// (<see cref="OperationCanceledException"/> propagates instead;
    /// <see cref="ApplyInFlightChanged"/> still reports the release). May fire on thread-pool
    /// threads.
    /// </summary>
    event Action<ApplyResult>? ApplyCompleted;

    /// <summary>
    /// Raised on every <see cref="IsApplyInFlight"/> transition — true when an apply wins the
    /// guard, false when it releases. The false transition includes the late release performed
    /// by the abandoned-download observer when a stalled Velopack task finally completes, which
    /// is what lets an open Updates page re-enable without navigation (UPD-2). May fire on
    /// thread-pool threads.
    /// </summary>
    event Action<bool>? ApplyInFlightChanged;

    /// <summary>
    /// Raised when a stalled download attempt tore down cleanly and an automatic retry has been
    /// scheduled (UPD-3): <c>(attempt, maxAttempts)</c>, where <c>attempt</c> is the UPCOMING
    /// attempt number. Intermediate stalls raise this INSTEAD of <see cref="ApplyCompleted"/> —
    /// the exactly-once-per-guard-winner contract is unchanged; only the final terminal outcome
    /// completes. Abandoned downloads never retry and never raise this. May fire on thread-pool
    /// threads.
    /// </summary>
    event Action<int, int>? ApplyDownloadRetrying;

    /// <summary>
    /// Raised on every <see cref="PendingUpdateVersion"/> TRANSITION (UPD-3b): the new version
    /// string when a check discovers a (different) update, and <c>null</c> when the pending
    /// update is cleared — a check coming back up-to-date after one was pending, or the apply
    /// path finding the memoized update no longer available. NOT raised for check errors,
    /// gating (Disabled), or any apply outcome that leaves the pending update in place
    /// (Blocked / Error / cancellation / ApplyInitiated) — a still-actionable update must not
    /// lose its UI affordances to a transient failure. Deduped: no raise when the value doesn't
    /// change. May fire on thread-pool threads, and the payload is informational only —
    /// subscribers that need authoritative state re-read <see cref="PendingUpdateVersion"/>
    /// (the state-driven sidebar dot does exactly that).
    /// </summary>
    event Action<string?>? PendingUpdateVersionChanged;

    /// <summary>
    /// Asks the configured update source whether a newer build is available.
    ///
    /// <para>Gates (checked in order):</para>
    /// <list type="number">
    ///   <item><see cref="UpdateCheckFeature.IsEnabled"/> — compile-time
    ///     kill-switch. When <c>false</c>, returns
    ///     <see cref="UpdateCheckOutcome.Disabled"/> with no source call,
    ///     no settings write.</item>
    ///   <item><see cref="Helpers.AppDefaults.AutomaticUpdateCheckEnabled"/>
    ///     — user-facing toggle. Enforced ONLY when
    ///     <paramref name="isManualCheck"/> is <c>false</c> (scheduler
    ///     path). A user clicking "Check for updates" is itself explicit
    ///     consent, so the manual path bypasses this gate (per Plan 4D).</item>
    /// </list>
    ///
    /// <para>If both gates pass, <see cref="Helpers.AppDefaults.LastUpdateCheckUtc"/>
    /// is written to <c>DateTime.UtcNow</c> BEFORE the source call, so
    /// the "Last checked" UI line updates whether the call succeeds or
    /// throws. The source call is wrapped in try/catch; any exception
    /// surfaces as <see cref="UpdateCheckOutcome.Error"/> with the
    /// exception message — an unhandled exception from a background
    /// timer tick would otherwise crash the WinUI dispatcher loop.</para>
    ///
    /// <para><b>Exception contract.</b> Returns an
    /// <see cref="UpdateCheckOutcome.Error"/> result for every exception
    /// except one: <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> was actually cancelled. Cancellation is
    /// normal control flow (the caller initiated it), so it propagates
    /// so async composition works correctly — misreporting "user pressed
    /// Cancel" as "update server failed" would be wrong UX.</para>
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool isManualCheck,
        CancellationToken ct = default);

    /// <summary>
    /// Download and apply the update found by the most recent <see cref="CheckForUpdatesAsync"/>
    /// that returned <see cref="UpdateCheckOutcome.UpdateAvailable"/>, then request a graceful restart.
    ///
    /// <para>Flow (UPD-2): win the apply guard (a concurrent second call is rejected as
    /// <see cref="ApplyOutcome.Blocked"/> with no side effects) → check the maintenance gate
    /// (recording / transcription / model-download / paste-restore must be idle) → mark the app
    /// as applying an update → download under a stall watchdog (progress-driven; a dead
    /// connection fails bounded instead of hanging, with up to two automatic same-context
    /// retries for cleanly torn-down stalls — UPD-3) → re-check the gate once more → hand off to
    /// Velopack's <c>WaitExitThenApplyUpdates</c> and request a graceful quit so the app's
    /// normal shutdown (settings flush, DB checkpoint) runs before the binary swap.</para>
    ///
    /// <para>Returns <see cref="ApplyOutcome.Blocked"/> (with the active blockers) if a task is in
    /// flight, <see cref="ApplyOutcome.NotPending"/> if nothing is queued / the build is gated off,
    /// <see cref="ApplyOutcome.Error"/> on download/apply failure, or
    /// <see cref="ApplyOutcome.ApplyInitiated"/> once the updater is launched (after which the
    /// process is exiting). Only <see cref="OperationCanceledException"/> on a cancelled
    /// <paramref name="ct"/> propagates.</para>
    /// </summary>
    Task<ApplyResult> ApplyAndRestartAsync(CancellationToken ct = default);
}

/// <summary>
/// Coarse-grained outcome of <see cref="IUpdateService.CheckForUpdatesAsync"/>,
/// shaped to drive the four UI states the Settings → Updates page renders
/// (Disabled → "Auto-updates not enabled in this build", UpToDate → "You're
/// on the latest version", UpdateAvailable → "v X.Y.Z available", Error →
/// "Couldn't reach update server: ...").
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>
    /// One of the gates blocked the check (UPDATE_CHECK_ENABLED off, or
    /// scheduler trigger + AutomaticUpdateCheckEnabled false). No source
    /// call was made; <see cref="UpdateCheckResult.CheckedUtc"/> is null.
    /// </summary>
    Disabled,

    /// <summary>
    /// Source reported no update available. Current build is the latest.
    /// </summary>
    UpToDate,

    /// <summary>
    /// Source reported a newer version. <see cref="UpdateCheckResult.AvailableVersion"/>
    /// carries the version string (provider-formatted; usually
    /// <c>major.minor.patch</c>).
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// Source threw or returned a malformed response.
    /// <see cref="UpdateCheckResult.ErrorMessage"/> carries the diagnostic
    /// text suitable for display (the exception message, not the full
    /// stack trace).
    /// </summary>
    Error,
}

/// <summary>
/// DTO returned by <see cref="IUpdateService.CheckForUpdatesAsync"/>.
/// Sealed so consumers can pattern-match on <see cref="Outcome"/> exhaustively.
/// </summary>
public sealed class UpdateCheckResult
{
    /// <summary>What happened. See <see cref="UpdateCheckOutcome"/>.</summary>
    public required UpdateCheckOutcome Outcome { get; init; }

    /// <summary>
    /// Available version string when <see cref="Outcome"/> is
    /// <see cref="UpdateCheckOutcome.UpdateAvailable"/>; <c>null</c> otherwise.
    /// </summary>
    public string? AvailableVersion { get; init; }

    /// <summary>
    /// Human-readable error description when <see cref="Outcome"/> is
    /// <see cref="UpdateCheckOutcome.Error"/>; <c>null</c> otherwise.
    /// Sourced from the underlying exception's <c>Message</c> — never
    /// the full stack trace, which is logged separately.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// UTC timestamp of the check, or <c>null</c> when the outcome is
    /// <see cref="UpdateCheckOutcome.Disabled"/> (no check was attempted).
    /// </summary>
    public DateTime? CheckedUtc { get; init; }
}

/// <summary>Coarse outcome of <see cref="IUpdateService.ApplyAndRestartAsync"/>.</summary>
public enum ApplyOutcome
{
    /// <summary>Updater launched; the process is exiting to apply + restart.</summary>
    ApplyInitiated,

    /// <summary>A maintenance task is in flight; nothing was applied. See <see cref="ApplyResult.ActiveBlockers"/>.</summary>
    Blocked,

    /// <summary>No update queued, or updates are disabled in this build.</summary>
    NotPending,

    /// <summary>Download or apply failed. See <see cref="ApplyResult.Detail"/>.</summary>
    Error,
}

/// <summary>DTO returned by <see cref="IUpdateService.ApplyAndRestartAsync"/>.</summary>
public sealed class ApplyResult
{
    /// <summary>What happened. See <see cref="ApplyOutcome"/>.</summary>
    public required ApplyOutcome Outcome { get; init; }

    /// <summary>Human-readable error or block reason for the UI/log; <c>null</c> on the happy path.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Active blocker descriptions when <see cref="Outcome"/> is <see cref="ApplyOutcome.Blocked"/>;
    /// <c>null</c> otherwise.
    /// </summary>
    public IReadOnlyList<string>? ActiveBlockers { get; init; }
}
