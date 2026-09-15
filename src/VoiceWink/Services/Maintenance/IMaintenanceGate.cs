using System;
using System.Collections.Generic;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// Central "is it safe to do something destructive right now?" gate.
///
/// <para>Used by maintenance operations that interrupt the running app
/// — currently planned consumers are:</para>
/// <list type="bullet">
///   <item><c>IUpdateService.ApplyAndRestartAsync</c> (UPD-1 follow-up;
///     blocks while a recording / transcription / paste-restore / model
///     download is in flight so the updater can't yank the binary out
///     from under live work).</item>
///   <item>The Reset-all-data Settings button (INS-1 follow-up; same
///     concern — don't wipe the SQLite DB while a transcription is
///     writing to it).</item>
/// </list>
///
/// <para><b>Design.</b> The gate is a pull-model aggregator. Subsystems
/// that have transient "I'm busy" state (the recorder, the audio-file
/// transcribe pipeline, the model-download manager, the paste-restore
/// helper, etc.) implement <see cref="IMaintenanceStatusSource"/> and
/// register with the gate at construction time. The gate iterates its
/// registered sources whenever <see cref="Check"/> is called and returns
/// a <see cref="MaintenanceStatus"/> snapshot summarising which sources,
/// if any, are currently blocking.</para>
///
/// <para>This avoids the alternative of having one giant
/// <c>MaintenanceGate</c> class that knows about every subsystem — that
/// design would either pull the entire dependency graph into the gate's
/// constructor, or fall back to a service-locator anti-pattern. The
/// pull-from-registered-sources shape keeps each subsystem's "am I busy?"
/// query encapsulated and unit-testable in isolation.</para>
///
/// <para><b>Thread safety.</b> <see cref="Register"/> and <see cref="Check"/>
/// are safe to call concurrently. <see cref="Check"/> takes a snapshot of
/// the registered sources under a lock, then queries each source outside
/// the lock — so a slow source can't block other registrations or other
/// <see cref="Check"/> callers. Source implementations are responsible
/// for their own thread safety.</para>
/// </summary>
public interface IMaintenanceGate
{
    /// <summary>
    /// Register a status source. Returns an <see cref="IDisposable"/> handle
    /// that removes the registration on dispose — useful for transient
    /// subsystems (e.g. a `using` block scoped to a particular operation),
    /// though long-lived singletons typically just hold the handle for the
    /// app lifetime.
    /// </summary>
    IDisposable Register(IMaintenanceStatusSource source);

    /// <summary>
    /// Query the current maintenance status by polling every registered
    /// source. Cheap to call — the gate itself does no I/O; cost is the sum
    /// of each source's own <see cref="IMaintenanceStatusSource.IsBlocking"/>
    /// implementation.
    ///
    /// <para>Caller is expected to act on the returned snapshot atomically.
    /// The gate does not hold any lock across the consumer's decision —
    /// a source could flip from blocking to non-blocking (or vice versa)
    /// between <see cref="Check"/> returning and the consumer acting on the
    /// result. Consumers that need stronger guarantees should either
    /// re-check after acquiring their own lock or design the consumer flow
    /// to tolerate this race (e.g. the Update apply path can call
    /// <c>WaitExitThenApplyUpdates</c> which has its own retry on busy).</para>
    /// </summary>
    MaintenanceStatus Check();

    /// <summary>
    /// Atomically try to enter the update-apply exclusive state. Fails (returns <c>false</c>,
    /// <paramref name="lease"/> <c>null</c>) while a data erasure holds its exclusive lease —
    /// the two destructive operations are mutually exclusive (F19, 2026-07-14). On success,
    /// start-paths refuse new work via <see cref="IsApplyingUpdate"/> for the whole apply.
    /// Reference-counted so the update path's compositions still nest; dispose the handle to
    /// clear one hold.
    /// </summary>
    bool TryBeginUpdateApply(out IDisposable? lease);

    /// <summary>
    /// Atomically try to enter the data-erasure exclusive state. Fails while an update apply
    /// is in flight OR another erasure holds the lease (single-holder). Acquired by
    /// <c>DataErasureService</c> BEFORE it polls subsystem blockers, so an apply can't start
    /// during the poll; the lease is held through the destructive commit + process exit and
    /// released only on a pre-commit abort.
    /// </summary>
    bool TryBeginErasure(out IDisposable? lease);

    /// <summary>
    /// Atomically try to start a background maintenance pass (history cleanup / reference
    /// orphan sweep). Fails while an update apply or erasure holds its exclusive lease, so a
    /// pass never starts inside the erase/apply window. Reference-counted: N overlapping
    /// passes keep the gate busy until the LAST releases. While any pass is outstanding,
    /// <see cref="Check"/> reports a "Background cleanup" blocker so an erasure's poll drains
    /// in-flight passes before committing.
    /// </summary>
    bool TryBeginCleanupPass(out IDisposable? lease);

    /// <summary>
    /// Atomically try to admit an automatic licence re-validation (LIC-23's daily in-session
    /// check). Fails while a data erasure holds its exclusive lease, so the timer can never send
    /// the stored key and instance identifier to Lemon Squeezy after the user began erasing their
    /// data — the erasure path's "every start-path sees <see cref="IsErasing"/>" invariant, made
    /// atomic here rather than as a check-then-send race on the flag (Codex plan round) — and while
    /// an update apply holds its lease, because a check admitted during the download would make
    /// the apply's post-download re-check discard that download (the cleanup-pass rule, verbatim).
    /// Reference-counted; while any check is outstanding, <see cref="Check"/> reports a
    /// "License check" blocker: an erasure's poll drains an already-admitted check within its own
    /// gate budget (a validate slower than that makes the erasure refuse pre-commit, retryable,
    /// nothing deleted), and an apply's pre-check or a restart's busy check is turned away for the
    /// length of one validate. Dispose the handle when the check returns.
    /// </summary>
    bool TryBeginLicenseRevalidation(out IDisposable? lease);

    /// <summary>
    /// True while an update is being applied (one or more <see cref="TryBeginUpdateApply"/> handles
    /// are outstanding). Independent of <see cref="Check"/>, which reports only subsystem busy-state
    /// — so the apply path's own re-check via <see cref="Check"/> is not self-blocked by this flag.
    /// </summary>
    bool IsApplyingUpdate { get; }

    /// <summary>True while a data erasure holds its exclusive lease.</summary>
    bool IsErasing { get; }
}

/// <summary>
/// One subsystem's contribution to the <see cref="IMaintenanceGate"/>'s
/// aggregate view. Implementations are typically thin: a `bool` field on the
/// subsystem becomes the implementation's <see cref="IsBlocking"/> return
/// value, optionally annotated with a human-readable detail string.
///
/// <para>Examples (planned):</para>
/// <list type="bullet">
///   <item><c>RecorderMaintenanceSource</c> — blocking while
///     <c>MainViewModel.RecordingState</c> is not <c>Idle</c>.</item>
///   <item><c>AudioTranscribeMaintenanceSource</c> — blocking while the
///     <c>AudioTranscribePage</c> batch transcription is in flight (per
///     the Codex round-2 override item that explicitly called this out).</item>
///   <item><c>ModelDownloadMaintenanceSource</c> — blocking while
///     <c>ModelDownloadManager</c> reports an active download.</item>
///   <item><c>PasteRestoreMaintenanceSource</c> — blocking while
///     <c>ClipboardService</c> has a pending clipboard restore queued.</item>
/// </list>
/// </summary>
public interface IMaintenanceStatusSource
{
    /// <summary>
    /// Short, human-readable name of the subsystem this source reports on.
    /// Surfaced in <see cref="MaintenanceStatus.ActiveBlockers"/> and ends
    /// up in UI / log strings, so keep it concise and user-facing —
    /// "Recording in progress", "Model downloading", not internal class
    /// names like <c>AudioRecorderService</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Report whether this source is currently blocking maintenance.
    /// Implementations should be cheap and non-throwing — the gate does
    /// not catch exceptions here. If a source has a meaningful detail
    /// (e.g. "downloading large-v3 (43%)"), set <paramref name="detail"/>;
    /// otherwise leave it <c>null</c>.
    /// </summary>
    bool IsBlocking(out string? detail);
}

/// <summary>
/// Snapshot returned by <see cref="IMaintenanceGate.Check"/>. Sealed so
/// consumers can pattern-match exhaustively on the two fields.
/// </summary>
public sealed record MaintenanceStatus
{
    /// <summary>
    /// <c>true</c> when no registered source is blocking — i.e. it is safe
    /// to proceed with the maintenance operation.
    /// </summary>
    public required bool CanProceed { get; init; }

    /// <summary>
    /// Human-readable descriptions of every currently-blocking source. Each
    /// entry is the source's <see cref="IMaintenanceStatusSource.Name"/>
    /// followed by ": " and its detail string, or just the name when the
    /// source provided no detail. Empty when <see cref="CanProceed"/> is
    /// <c>true</c>.
    /// </summary>
    public required IReadOnlyList<string> ActiveBlockers { get; init; }

    /// <summary>
    /// Convenience for the empty / safe case.
    /// </summary>
    public static MaintenanceStatus Clear { get; } = new()
    {
        CanProceed = true,
        ActiveBlockers = Array.Empty<string>(),
    };
}
