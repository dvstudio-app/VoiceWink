using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Production adapter over Velopack's <c>UpdateManager</c> for the feed at
/// <c>updates.voicewink.app/&lt;channel&gt;/</c> (UPD-1).
///
/// <para>The TESTER ring is gated by Cloudflare Access; PUBLIC channels are ungated (UPD-7). Every
/// operation (check / download / apply) is built over a <see cref="SimpleWebSource"/> with a
/// <see cref="CloudflareAccessFileDownloader"/>, which injects the service-token headers only when
/// both credentials were baked — on a public build they are empty, so the same downloader sends no
/// CF-Access header at all (pinned by <c>CloudflareAccessFileDownloaderTests</c>). Manifest routing
/// through the custom downloader is proven by <c>VelopackManifestAuthProbeTests</c>. The bare
/// <c>new UpdateManager(string)</c> ctor is deliberately NOT used: it would bypass the custom
/// downloader and 401 against Access on the gated ring.</para>
///
/// <para><b>Check → Apply correlation.</b> Velopack's <c>UpdateInfo</c> can't be reconstructed
/// outside the library, so this adapter caches it from the last check and hands the apply path an
/// opaque token (the same <c>UpdateInfo</c>, type-erased) — keeping Velopack types off the seam.</para>
///
/// <para><b>Inert unless built for the feed.</b> The channel, the gated/ungated flag and — for a
/// gated ring — the credentials are baked at build time into <c>UpdateFeedConfig</c> (empty in dev
/// builds). <see cref="UpdateFeedConfig.IsConfigured"/> means "a channel, plus both credentials when
/// the feed is gated"; when it is false, check returns <c>null</c> (nothing cached) and download
/// returns <c>null</c>.</para>
///
/// <para>Velopack's API surface is pinned to 0.0.1298 — don't upgrade without re-validating against
/// the clean-VM signed-install matrix.</para>
/// </summary>
internal sealed class VelopackUpdateSource : IVelopackUpdateSource
{
    // Guards the UpdateInfo cached between Check and Download/Apply.
    private readonly object _lock = new();
    private UpdateInfo? _cachedUpdateInfo;

    public Task<RemoteUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct)
    {
        // Builds that didn't go through the release script (all dev/Debug builds) have an empty
        // UpdateFeedConfig → no channel → no-op (nothing cached, no network call). A gated tester
        // build without its credentials is inert the same way; a public build needs none (UPD-7).
        if (!UpdateFeedConfig.IsConfigured)
        {
            return Task.FromResult<RemoteUpdateInfo?>(null);
        }

        return CheckCoreAsync(ct);
    }

    private async Task<RemoteUpdateInfo?> CheckCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Velopack 0.0.1298's CheckForUpdatesAsync has no CancellationToken overload, so `ct` is
        // honoured only at entry; a cancel after the network call starts falls back to the
        // SimpleWebSource HTTP timeout. Acceptable for the manual "Check for updates" button.
        var info = await BuildManager().CheckForUpdatesAsync().ConfigureAwait(false);

        lock (_lock)
        {
            // Cache for a later Apply; null (up-to-date) also clears any stale pending update.
            _cachedUpdateInfo = info;
        }

        return info is null
            ? null
            : new RemoteUpdateInfo { Version = info.TargetFullRelease.Version.ToString() };
    }

    public async Task<object?> DownloadPendingUpdateAsync(RemoteUpdateInfo info, Action<int>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        ct.ThrowIfCancellationRequested();

        UpdateInfo? pending;
        lock (_lock)
        {
            pending = _cachedUpdateInfo;
        }

        if (pending is null)
        {
            return null; // never checked, or up-to-date since the last check
        }

        // Correlation guard: a concurrent re-check could have replaced _cachedUpdateInfo with a
        // DIFFERENT release between the UpdateService pending marker and this download. If the cached
        // version no longer matches the version the apply was authorized for, treat it as
        // no-longer-pending rather than silently downloading/applying a different build. (The primary
        // defense is VM-level serialization — CheckForUpdates is disabled while applying — but this
        // keeps the source correct if it's ever driven from elsewhere, e.g. the future scheduler.)
        if (!VersionMatches(pending.TargetFullRelease.Version.ToString(), info.Version))
        {
            return null;
        }

        // Stages the package(s) in Velopack's locator dir; a fresh (stateless) manager on the same
        // channel finds them at apply time. The progress callback flows through to Velopack
        // verbatim (percent 0–100); ct threads cancellation.
        await BuildManager(DownloadTimeoutMinutes).DownloadUpdatesAsync(pending, progress, ct).ConfigureAwait(false);
        return pending; // opaque token = the cached UpdateInfo
    }

    public void ApplyDownloadedUpdate(object downloadToken)
    {
        ArgumentNullException.ThrowIfNull(downloadToken);

        if (downloadToken is not UpdateInfo info)
        {
            throw new ArgumentException(
                "Token is not a Velopack UpdateInfo from DownloadPendingUpdateAsync.",
                nameof(downloadToken));
        }

        // Launches a separate Update.exe that waits (≤60s) for THIS process to exit, then applies the
        // staged update and relaunches. Returns immediately (void) — the caller then triggers the
        // graceful shutdown (IAppLifetime.RequestQuitForUpdate) so Cleanup runs before the binary swap.
        BuildManager().WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true);
    }

    /// <summary>
    /// Whether the cached release version still matches the version the apply was authorized for.
    /// Extracted as a pure helper so the correlation guard is unit-testable without constructing a
    /// Velopack <c>UpdateInfo</c> (the type the seam exists to avoid). Ordinal — both strings come
    /// from the same <c>SemanticVersion.ToString()</c> source, so a culture- or parse-sensitive
    /// compare would add only risk; the guard must fire only on a genuine release swap.
    /// </summary>
    internal static bool VersionMatches(string cachedVersion, string requestedVersion) =>
        string.Equals(cachedVersion, requestedVersion, StringComparison.Ordinal);

    // Velopack's timeout unit is MINUTES (SimpleWebSource.Timeout doc). Manifest checks and the
    // local apply step fail fast; the package download gets a generous HTTP-layer ceiling — the
    // authoritative bound for a wedged download is UpdateService's stall watchdog (UPD-2: a dead
    // connection sat at a 0-byte partial for 30+ min because the body read outlived the old
    // 1-minute value, which evidently only bounds the request/headers phase).
    private const double CheckTimeoutMinutes = 1.0;
    private const double DownloadTimeoutMinutes = 30.0;

    // Velopack's UpdateManager is stateless; rebuild it (same channel; CF-Access auth only where the
    // credentials were baked) for check / download / apply. The base URL MUST exactly equal vpk's
    // --channel/--prefix (UpdateFeedConfig).
    private static UpdateManager BuildManager(double timeoutMinutes = CheckTimeoutMinutes)
    {
        var downloader = new CloudflareAccessFileDownloader(
            UpdateFeedConfig.CfAccessClientId,
            UpdateFeedConfig.CfAccessClientSecret);
        var source = new SimpleWebSource(
            $"https://updates.voicewink.app/{UpdateFeedConfig.Channel}/",
            downloader,
            timeout: timeoutMinutes);
        return new UpdateManager(source);
    }
}
