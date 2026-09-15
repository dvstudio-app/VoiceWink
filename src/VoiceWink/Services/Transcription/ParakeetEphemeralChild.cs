using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-50: one THROWAWAY `parakeet-server` child, born and killed inside a single call — the
/// mechanics <see cref="GpuWarmup"/> carried inline since TRN-49, extracted so a second caller
/// (the coordinator's CPU re-decode of an empty GPU whole-call) inherits every property the
/// warm-up already pins rather than re-implementing them:
/// <list type="bullet">
/// <item><b>Gated per spawn</b> — <see cref="ParakeetSpawnGate.Check"/> runs before every launch,
/// so escaping the storm fuse can never escape the TRN-34 provenance gate (the warm-up's own
/// pre-diff Blocker; the exe is user-writable under a per-user Velopack install).</item>
/// <item><b>Invisible to the storm fuse and the launch-mode latch</b> — the child goes through
/// the LAUNCHER seam and never through <see cref="ParakeetServerProcess"/>; its death changes
/// nothing there.</item>
/// <item><b>Kill and CONFIRM</b> — TerminateProcess is asynchronous, and a resident child must
/// never initialize its GPU device against ~1 GB of dying predecessor. A confirmed exit disposes
/// the child here; an UNCONFIRMED one is handed back undisposed for the caller to retain and
/// observe (the warm-up's lingering-child protocol, TRN-57).</item>
/// <item><b>Bounded</b> — the health wait carries the production budget, and the caller's body
/// runs on the same cancellation token, so recording admission ends a warm-up and a user cancel
/// ends a re-decode.</item>
/// </list>
/// Nothing here logs a transcript or a raw native line: the log carries the caller's label, the
/// skip reason, and counts.
/// </summary>
internal static class ParakeetEphemeralChild
{
    private static ILogger Logger => Log.ForContext(typeof(ParakeetEphemeralChild));

    internal enum Outcome
    {
        /// <summary>The provenance gate refused the exe; nothing spawned.</summary>
        GateRefused,
        /// <summary>The launcher returned no child.</summary>
        LaunchFailed,
        /// <summary>The child died before answering health.</summary>
        ExitedBeforeHealth,
        /// <summary>No healthy answer within the budget (port never opened, or a silent listener).</summary>
        NeverHealthy,
        /// <summary>The body ran to its end; its own verdict is <see cref="Result.BodySucceeded"/>.</summary>
        Ran,
        /// <summary>The caller's token was cancelled (before, during health, or inside the body).</summary>
        Cancelled,
        /// <summary>Something threw; logged once at Warning under the caller's label.</summary>
        Faulted,
        /// <summary>TRN-68: the child's own rows asked for a pinned respawn, but the integrated-adapter
        /// child's exit was NOT confirmed within <see cref="ParakeetServerPolicy.RetireWait"/> —
        /// nothing else may spawn against it, so the run ends here and that child IS
        /// <see cref="Result.UnconfirmedChild"/> (undisposed, detached from the finally's own
        /// kill-and-confirm) for the TRN-57 lingering protocol.</summary>
        RespawnBlocked,
    }

    /// <summary>What one run did. <see cref="UnconfirmedChild"/> is non-null ONLY when the kill
    /// was not confirmed within <see cref="ParakeetServerPolicy.RetireWait"/> — the child is then
    /// still undisposed and the caller owns its observation. The two observed fields are the
    /// child's own parsed device lines (TRN-60), read AFTER the run, and are the only basis on
    /// which a caller may claim the run computed on a GPU (<see cref="ParakeetGpuEvidence"/>).</summary>
    internal readonly record struct Result(
        Outcome Outcome,
        bool BodySucceeded,
        IParakeetServerChild? UnconfirmedChild,
        string? ObservedBackend,
        string? ObservedGpuName);

    /// <param name="label">The log prefix — "parakeet GPU warm-up", "parakeet CPU re-decode".</param>
    /// <param name="body">Runs against the healthy child; returns its own success. A throw is
    /// <see cref="Outcome.Faulted"/>; a cancellation on <paramref name="ct"/> is <see cref="Outcome.Cancelled"/>.</param>
    /// <param name="healthBudgetOverride">Test seam only: the health budget is 30 s wall-clock,
    /// and the never-healthy branch is untestable at that cost. Production callers pass nothing.</param>
    internal static async Task<Result> RunAsync(
        string label,
        IParakeetServerLauncher launcher,
        ParakeetServerTranscriptionClient client,
        string exePath,
        string expectedExeSha256,
        string ggufPath,
        ParakeetLaunchMode mode,
        Func<IParakeetServerChild, Uri, CancellationToken, Task<bool>> body,
        CancellationToken ct,
        TimeSpan? healthBudgetOverride = null)
    {
        IParakeetServerChild? child = null;
        var outcome = Outcome.Faulted;
        var bodySucceeded = false;
        IParakeetServerChild? unconfirmed = null;
        try
        {
            (outcome, bodySucceeded) = await RunCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.Cancelled;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "{Label} failed", label);
            outcome = Outcome.Faulted;
        }
        finally
        {
            if (child is not null)
            {
                // Kill and CONFIRM, mirroring ParakeetServerProcess.Retire's reasoning: a successor
                // must never initialize against ~1 GB of dying predecessor. Dispose alone only
                // closes handles and lets KILL_ON_JOB_CLOSE race the successor.
                child.Kill();
                if (child.WaitForExit(ParakeetServerPolicy.RetireWait))
                {
                    child.Dispose();
                }
                else
                {
                    // Exit NOT confirmed: hand the undisposed child back; the caller retains and
                    // observes it (TRN-57's lingering protocol) and decides what may spawn.
                    unconfirmed = child;
                    Logger.Warning("{Label}: the child's exit was not confirmed within {Seconds:F0}s - handed back for a later observation",
                        label, ParakeetServerPolicy.RetireWait.TotalSeconds);
                }
            }
        }

        // Assembled AFTER the finally, on purpose: a `return` inside the try evaluates its value
        // before the finally runs, which is exactly how the first version reported an unconfirmed
        // exit as confirmed (the two TRN-57 rows caught it).
        return new Result(outcome, bodySucceeded, unconfirmed, child?.ObservedBackend, child?.ObservedGpuName);

        async Task<(Outcome, bool)> RunCoreAsync()
        {
            // The caller may have been cancelled while resolving its inputs — do not even hash after it.
            ct.ThrowIfCancellationRequested();

            var gate = ParakeetSpawnGate.Check(exePath, expectedExeSha256);
            if (!gate.Spawnable)
            {
                Logger.Warning("{Label}: refusing to spawn - {Reason}", label, LogValueSanitizer.SingleLine(gate.RefusalReason));
                return (Outcome.GateRefused, false);
            }

            // Hashing 59 MB takes real time; a hotkey pressed during it must not cost a ~1 GB
            // child that dies one line later.
            ct.ThrowIfCancellationRequested();

            child = launcher.TryLaunch(exePath, ggufPath, mode);
            if (child is null)
            {
                Logger.Information("{Label}: launch failed - skipped", label);
                return (Outcome.LaunchFailed, false);
            }

            var healthBudget = healthBudgetOverride ?? ParakeetServerPolicy.HealthBudget;
            var spawnedUtc = DateTime.UtcNow;
            Uri? baseUri = null;
            var healthy = false;
            string? pin = null; // TRN-68: the adapter token the CURRENT child was spawned with
            // The per-await bound the production loop carries: without it a listening-but-silent
            // child parks this task on the named client's 5-minute timeout and the budget never fires.
            using (var health = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                health.CancelAfter(healthBudget);
                try
                {
                    while (DateTime.UtcNow - spawnedUtc <= healthBudget)
                    {
                        health.Token.ThrowIfCancellationRequested();
                        if (child.HasExited)
                        {
                            Logger.Information("{Label}: child exited before health - skipped", label);
                            return (Outcome.ExitedBeforeHealth, false);
                        }
                        // TRN-68: the child's OWN rows decide, before health (the same rule the resident
                        // spawn applies). Kill-and-CONFIRM the integrated-adapter child, then spawn the
                        // pinned one under the SAME health budget: the rows print in the child's first
                        // few hundred ms, so the pinned child normally has nearly the whole budget; rows
                        // that arrive late leave it less, and the worst case is a NeverHealthy warm-up
                        // (no verdict, no mark this launch) — a stated residual, not a hazard. The
                        // confirm wait is the same synchronous RetireWait the resident Retire pays, so a
                        // recording admitted during it can be delayed by up to that wait. An
                        // unconfirmed exit ends the run: the child is handed back for the TRN-57
                        // lingering protocol and nothing spawns against it.
                        if (pin is null && mode == ParakeetLaunchMode.Auto)
                        {
                            var decision = GpuAdapterPreference.Decide(
                                child.ObservedDeviceCount, child.ObservedDevices,
                                ParakeetGpuEvidence.TryDeviceIndex(child.ObservedBackend, out var selected) ? selected : null);
                            if (decision.Kind == GpuAdapterDecisionKind.Pin)
                            {
                                pin = GpuAdapterPreference.DeviceToken(decision.Index);
                                Logger.Information("{Label}: the child selected {GpuName} while {PinnedGpuName} is available - respawning pinned to {Pin} (TRN-68)",
                                    label,
                                    LogValueSanitizer.SingleLine(decision.SelectedName ?? "?"),
                                    LogValueSanitizer.SingleLine(decision.PinnedName ?? "?"),
                                    pin);
                                child.Kill();
                                if (!child.WaitForExit(ParakeetServerPolicy.RetireWait))
                                {
                                    // Hand the UNCONFIRMED child back here and detach it from the finally,
                                    // which would otherwise kill-and-wait it a second time (another
                                    // RetireWait) before reaching the same handoff.
                                    Logger.Warning("{Label}: the child's exit was not confirmed within {Seconds:F0}s - pinned respawn blocked, handed back for a later observation (TRN-68)",
                                        label, ParakeetServerPolicy.RetireWait.TotalSeconds);
                                    unconfirmed = child;
                                    child = null;
                                    return (Outcome.RespawnBlocked, false);
                                }
                                child.Dispose();
                                ct.ThrowIfCancellationRequested(); // a cancelled warm-up must not pay the pinned spawn
                                child = launcher.TryLaunch(exePath, ggufPath, mode, pin);
                                if (child is null)
                                {
                                    Logger.Information("{Label}: pinned launch failed - skipped", label);
                                    return (Outcome.LaunchFailed, false);
                                }
                                baseUri = null;
                                continue;
                            }
                        }
                        if (baseUri is null && child.TryReadListeningPort() is { } port)
                        {
                            baseUri = new Uri($"http://127.0.0.1:{port}/");
                        }
                        if (baseUri is not null
                            && await client.ProbeAsync(baseUri, health.Token).WaitAsync(health.Token).ConfigureAwait(false))
                        {
                            healthy = true;
                            break;
                        }
                        await Task.Delay(ParakeetServerPolicy.HealthPollInterval, health.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The bound fired mid-await — same disposition as walking out of the loop.
                }
            }
            if (!healthy)
            {
                // Explicitly NOT "baseUri is null": a child whose port is known but which never
                // answered health must take this exit too, not fall through into the body.
                Logger.Information("{Label}: no healthy child within budget - skipped", label);
                return (Outcome.NeverHealthy, false);
            }

            // `child` is a captured local the TRN-68 respawn reassigns inside the loop; every null
            // relaunch returned above, so it is non-null here — flow analysis cannot see that across
            // the capture, hence the `!`.
            var succeeded = await body(child!, baseUri!, ct).ConfigureAwait(false);
            return (Outcome.Ran, succeeded);
        }
    }
}
