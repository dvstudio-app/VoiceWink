using Serilog;
using VoiceWink.Services.Maintenance;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// LIC-23 (owner decision 2026-09-06): a daily in-session licence check while VoiceWink stays
/// open — the SAME forced check as the launch reconcile (LIC-22), once every
/// <see cref="Interval"/>.
///
/// <para><b>Why it exists.</b> LIC-22 validates against Lemon Squeezy on every start after setup,
/// but nothing re-checked on a schedule while the app ran. The hotkey reads the cached verdict
/// only; after 24 h without a server check the cached state is <c>OfflineGrace</c>, which still
/// records for up to 30 days before <c>GraceExpired</c> blocks — so a key disabled in the dashboard
/// kept working on an always-on machine that never opened the License page for up to 30 days.
/// Auto-updates restart the app each release and shorten that in practice; that is incidental,
/// not design.</para>
///
/// <para><b>Loop shape.</b> One re-armed <c>Task.Delay</c> loop on the pool (the
/// <c>UpdateScheduler</c> shape, UPD-1b) — not a <c>DispatcherTimer</c>, so it is testable through
/// the injected delay seam and decoupled from the WinUI pump. <b>The first tick is a full
/// <see cref="Interval"/> after <see cref="Start"/>, never at start:</b> on an onboarded start the
/// launch reconcile has just run (and a first run has just activated a key or started the trial at
/// the wizard), so a tick at start would be the second call per launch the LIC-22 plan round
/// removed. Every iteration waits, then ticks.</para>
///
/// <para><b>The tick is FORCED, and that is the whole point.</b> A machine that validated at
/// start always holds a cache younger than 24 h at its first tick, so an unforced check would
/// short-circuit on the fresh cache forever — the defect this type closes. Force skips the
/// persisted-verdict flags and the fresh-cache return, never the no-key and fingerprint-mismatch
/// gates, so a free-trial machine and a key that no longer matches this device make no request
/// (the privacy policy's "while a license key is stored on this device and matches it").</para>
///
/// <para><b>What it does NOT do.</b> No UI: the recording gate and the License page already read
/// the outcome from the cache (the page's 5 s reprojection tick), and there is still no
/// mid-session redirect, no dialog, no pill. No network-availability listener (the LIC-13
/// paragraph's reasons stand): a reconnect heals at the next tick or the next start, never at the
/// moment of reconnecting. No new write path: the tick writes the cache only, through
/// <see cref="LicenseService.CheckAsync"/>, whose forced-path catch chain leaves an offline
/// machine exactly where the cached state puts it. No coordination with a user command holding
/// the License page's busy slot — that gate is per-page and irrelevant to a service-level timer;
/// the service's request-generation fence keeps the later-STARTED verdict, and its identity
/// re-check discards a response for a key replaced meanwhile.</para>
///
/// <para><b>Lifecycle.</b> Started from <c>App.StartGatedRuntimeServices</c> — behind the LGL-1
/// legal/onboarding gate, like every other network-egress service, so a tick can never run ahead
/// of consent or before setup is complete — and stopped from <c>App.Cleanup</c>.</para>
///
/// <para><b>Erasure admission (Codex plan round, Blocker).</b> Every tick is admitted through
/// <see cref="IMaintenanceGate.TryBeginLicenseRevalidation"/>, which refuses atomically once a
/// data erasure holds its lease: a delay expiring after the user pressed "Delete all my data" must
/// not carry the stored key and instance identifier to Lemon Squeezy — suppressed persistence
/// stops the settings file resurrecting, not the request. The lease is held across the check, so
/// <c>MaintenanceGate.Check()</c> reports "License check" and the erasure's poll drains an
/// already-admitted check within its own gate budget before it suppresses persistence and deletes
/// (a validate slower than that budget makes the erasure refuse pre-commit — retryable, nothing
/// deleted). A refused tick is skipped and the loop re-parks; a plain <c>IsErasing</c> probe was
/// rejected as a check-then-send race. The same lease is refused while an update apply holds its
/// lease (self-review, correctness lens): a tick admitted during the download would make the
/// apply's post-download re-check see "License check" and discard the download; the reverse
/// overlap — an apply or a TRN-59 restart clicked while a tick is in flight — is turned away for
/// the length of one validate, the cleanup-pass behaviour.</para>
/// </summary>
public sealed class LicenseRevalidationScheduler : IDisposable
{
    private static ILogger Logger => Log.ForContext<LicenseRevalidationScheduler>();

    /// <summary>
    /// The cadence — <see cref="LicenseService.RevalidationInterval"/> (24 h), so the timer, the
    /// service's cache short-circuit and the privacy policy's "about once a day" derive from ONE
    /// constant. A second constant here would go stale the day that one moved.
    /// </summary>
    internal static readonly TimeSpan Interval = LicenseService.RevalidationInterval;

    private readonly LicenseService _license;
    private readonly IMaintenanceGate _gate;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;
    private bool _started;
    private int _generation;

    public LicenseRevalidationScheduler(LicenseService license, IMaintenanceGate gate)
        : this(license, gate, static (d, ct) => Task.Delay(d, ct))
    {
    }

    // Internal test seam (InternalsVisibleTo VoiceWink.Tests): a controllable delay so the loop is
    // a step-able state machine in tests rather than a 24 h wait.
    internal LicenseRevalidationScheduler(LicenseService license, IMaintenanceGate gate, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _license = license;
        _gate = gate;
        _delay = delay;
    }

    /// <summary>Arm the loop. Idempotent: a second call while started does nothing.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started) return;
            _started = true;
            StartLoopLocked();
        }
    }

    /// <summary>Cancel the loop. Idempotent; a tick in flight is cancelled with it.</summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_started) return;
            _started = false;
            CancelLoopLocked();
        }
    }

    // Must be called under _lifecycleLock.
    private void StartLoopLocked()
    {
        CancelLoopLocked();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var generation = ++_generation;
        _ = Task.Run(() => RunLoopAsync(generation, ct), ct);
    }

    // Must be called under _lifecycleLock. Bumps the generation so an in-flight loop sees it is
    // stale at its next checkpoint even before the token cancellation propagates.
    private void CancelLoopLocked()
    {
        _generation++;
        if (_cts is { } cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
            cts.Dispose();
            _cts = null;
        }
    }

    private async Task RunLoopAsync(int generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && generation == Volatile.Read(ref _generation))
            {
                await _delay(Interval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                    return;
                await TickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on Stop() — nothing to do.
        }
        catch (Exception ex)
        {
            // Defense-in-depth: the tick already contains its own exceptions, so reaching here
            // means something outside it (the delay seam) faulted. Log and let the loop end rather
            // than crash a background thread.
            Logger.Warning(ex, "Daily licence check loop terminated unexpectedly");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            // Atomic admission: refused while an erasure or an update apply holds its lease (see
            // the type doc). The lease is held across the check so the erasure's poll can drain
            // an admitted one. A gate answering "admitted" with no handle is treated as refused —
            // a `using` over a null lease would leak the counter and turn every later erasure and
            // apply away for the process lifetime (self-review, correctness lens).
            if (!_gate.TryBeginLicenseRevalidation(out var lease) || lease is null)
            {
                Logger.Information("Daily licence check skipped: a data erasure or an update is in progress");
                return;
            }
            LicenseStatus status;
            using (lease)
            {
                // Forced: see the type doc. The service's own catch chain turns an unreachable
                // server into the cached-state answer and writes nothing; a server error is "no
                // verdict".
                status = await _license.CheckAsync(force: true, ct).ConfigureAwait(false);
            }
            // The service catches TaskCanceledException as "unreachable" and returns a status, so
            // a Stop() during the check comes back here as an ordinary result — re-check the token
            // before claiming a completion the shutdown cut short (Codex plan round, advisory).
            ct.ThrowIfCancellationRequested();
            // The HTTP status + verdict of the validate itself is already on the service's
            // "Validate completed" line; this names the CACHED status after the tick (a superseded
            // response reports whatever the fence kept), never a key.
            Logger.Information("Daily licence check completed; cached licence status is now {Status}", status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate so RunLoopAsync exits cleanly
        }
        catch (Exception ex)
        {
            // CheckAsync catches expected network / JSON errors internally, so anything reaching
            // here is unexpected — and it must not kill the loop, or one bad tick would silently
            // end the daily check for the rest of the session.
            Logger.Warning(ex, "Daily licence check failed");
        }
    }

    public void Dispose() => Stop();
}
