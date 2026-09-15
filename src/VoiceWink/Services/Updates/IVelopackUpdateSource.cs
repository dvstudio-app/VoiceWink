using System.Threading;
using System.Threading.Tasks;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Test seam over Velopack's <c>UpdateManager.CheckForUpdatesAsync</c>.
/// The production implementation (<see cref="VelopackUpdateSource"/>)
/// wraps Velopack; tests substitute a fake to exercise
/// <see cref="UpdateService"/>'s gating / error / success branches without
/// touching the network or the Velopack assembly.
///
/// <para>Why an interface rather than calling <c>UpdateManager</c>
/// directly from <see cref="UpdateService"/>: Velopack's
/// <c>UpdateInfo</c> type is non-trivial to construct from outside the
/// library (it carries delta-package metadata, signatures, etc.), so
/// tests would otherwise have to instantiate the real
/// <c>UpdateManager</c> against a fake HTTP source. The adapter pattern
/// keeps Velopack's types confined to <see cref="VelopackUpdateSource"/>
/// — everything above the seam works with the small POCO
/// <see cref="RemoteUpdateInfo"/>.</para>
/// </summary>
internal interface IVelopackUpdateSource
{
    /// <summary>
    /// Asks the configured update source whether a newer build is available.
    /// Returns <c>null</c> when the current build is the latest, or when
    /// no source is configured (the v1 stub case until R2 + UPDATE_CHECK_ENABLED
    /// flip). Throws on network/source failure — <see cref="UpdateService"/>
    /// catches and reports as <see cref="UpdateCheckOutcome.Error"/>.
    /// </summary>
    Task<RemoteUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>
    /// Download the update found by the most recent <see cref="CheckForUpdatesAsync"/>. Returns an
    /// OPAQUE token (the cached Velopack <c>UpdateInfo</c>, type-erased to <see cref="object"/>) to
    /// pass to <see cref="ApplyDownloadedUpdate"/>, or <c>null</c> if nothing is pending. Throws on
    /// download failure. Velopack's <c>UpdateInfo</c> never crosses this seam as a concrete type.
    /// <paramref name="progress"/> receives download percent (0–100) on arbitrary threads (UPD-2);
    /// it is a plain BCL delegate so the seam stays Velopack-type-free.
    /// </summary>
    Task<object?> DownloadPendingUpdateAsync(RemoteUpdateInfo info, Action<int>? progress, CancellationToken ct);

    /// <summary>
    /// Apply the downloaded update via Velopack's <c>WaitExitThenApplyUpdates</c>: launches a
    /// separate updater that waits (≤60s) for this process to exit, then swaps the binary and
    /// relaunches. Returns immediately — the caller must then trigger a graceful shutdown so the
    /// app's <c>Cleanup</c> runs before the swap. <paramref name="downloadToken"/> is the token
    /// returned by <see cref="DownloadPendingUpdateAsync"/>.
    /// </summary>
    void ApplyDownloadedUpdate(object downloadToken);
}

/// <summary>
/// Minimal POCO carrying the bits <see cref="UpdateService"/> needs from
/// a Velopack <c>UpdateInfo</c>. Add fields here (release notes URL,
/// release size, etc.) as the Settings → Updates page surfaces them.
/// </summary>
internal sealed class RemoteUpdateInfo
{
    /// <summary>
    /// Version string of the available update, in the format Velopack
    /// emits (semver-ish, usually <c>major.minor.patch</c>). Surfaced
    /// to the user verbatim, so the source is responsible for any
    /// cleanup.
    /// </summary>
    public required string Version { get; init; }
}
