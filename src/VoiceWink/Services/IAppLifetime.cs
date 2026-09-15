namespace VoiceWink.Services;

/// <summary>Why a graceful quit was requested. Named in the quit's log line so a support bundle
/// tells an update apply from a user-requested restart (TRN-59) from the uninstaller (INS-4).</summary>
public enum GracefulQuitReason
{
    /// <summary>A staged Velopack update applies on exit (<c>UpdateService.ApplyAndRestartAsync</c>).</summary>
    UpdateApply,
    /// <summary>The uninstaller asked a running instance to quit (INS-4).</summary>
    Uninstall,
    /// <summary>The app is restarting itself; a successor is already started and waits for this
    /// process to exit (<c>AppRestartService</c>, TRN-59).</summary>
    Restart,
}

/// <summary>
/// Seam for requesting a graceful application shutdown from non-UI code — the update-apply path
/// (UPD-1 Phase 3), the uninstaller's quit signal (INS-4) and the self-restart (TRN-59). Implemented
/// by <c>App</c>, which marshals the request to the UI dispatcher and reuses the normal quit
/// sequence (set the quitting flag → close the main window → <c>Cleanup()</c> →
/// <c>Environment.Exit(0)</c>), so settings flush + DB checkpoint run before Velopack swaps the
/// binary — or before a successor claims the single-instance mutex.
/// </summary>
public interface IAppLifetime
{
    /// <summary>
    /// Request a graceful shutdown so a staged Velopack update can be applied on exit. Safe to call
    /// from any thread (marshals to the UI dispatcher) and idempotent (repeated calls quit once).
    /// Fire-and-forget: returns immediately; the actual exit happens on the dispatcher.
    /// Equivalent to <see cref="RequestQuit"/> with <see cref="GracefulQuitReason.UpdateApply"/>.
    /// </summary>
    void RequestQuitForUpdate();

    /// <summary>
    /// The same graceful shutdown, naming why. One quit sequence for every reason — the ordering
    /// (flush, mutex release, log close) is the update path's and is not re-implemented per caller.
    /// </summary>
    void RequestQuit(GracefulQuitReason reason);
}
