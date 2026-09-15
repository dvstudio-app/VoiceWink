using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// Background housekeeping that runs once per launch and that nothing waits on.
///
/// <para><b>Why this type exists rather than another block in <c>App.OnLaunched</c>.</b>
/// <c>AGENTS.md</c> forbids adding responsibilities to the hub classes — <c>App.xaml.cs</c> named
/// first — and forbids new <c>App.Services</c> service-locator usages. The first version of the
/// retired-model sweep was ~60 lines inline in <c>OnLaunched</c> with two fresh provider lookups.
/// The second moved them behind a static <c>Start(IServiceProvider)</c>, which Codex correctly
/// refused as the same violation relocated: a static method taking the provider and resolving its
/// own dependencies is service location, not injection.</para>
///
/// <para>So the dependencies are CONSTRUCTOR-INJECTED and this is a DI singleton, returned by
/// <see cref="MaintenanceSourcesWiring.Resolve"/> — the startup-composition call <c>App</c> already
/// makes — so the hub gains no lookup of its own. <see cref="Start"/> stays separate from that
/// resolve deliberately: this pass takes a cleanup lease, and starting it inside the seam made the
/// seam's own "all probes false ⇒ gate clean" tests race against it.</para>
///
/// <para><b><see cref="IMaintenanceGate"/> is required, not optional.</b> The earlier version
/// treated a missing gate as permission to proceed, which fails OPEN on a destructive operation.
/// A non-nullable constructor parameter makes an uncoordinated delete unconstructable rather than
/// merely unlikely.</para>
///
/// <para><b>The download manager arrives as a FACTORY, not an instance, and that is a crash fix
/// rather than a style choice.</b> Its DI registration calls <c>AppPaths.EnsureModels()</c>, which
/// creates a directory — and injecting the instance made that run during <c>GetRequiredService</c>
/// on the LAUNCH thread. An inaccessible <c>Models</c> directory (occupied by a file, an ACL, a
/// full disk) would then throw where <see cref="RunOnceAsync"/>'s handler could never see it, and
/// <c>OnLaunched</c> would rethrow fatally: housekeeping nobody waits on could stop the app
/// starting. Deferring construction into the guarded body puts that failure back inside the
/// try/catch, which is where every other failure in this pass already lives (Codex, diff review
/// round 3).</para>
/// </summary>
internal sealed class StartupMaintenance
{
    private static ILogger Logger => Log.ForContext<StartupMaintenance>();

    private readonly IMaintenanceGate _gate;
    private readonly Func<ModelDownloadManager> _downloads;

    internal StartupMaintenance(IMaintenanceGate gate, Func<ModelDownloadManager> downloads)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
    }

    /// <summary>
    /// Kick off every once-per-launch background pass. Returns immediately; nothing depends on the
    /// work finishing.
    /// </summary>
    internal void Start() => _ = Task.Run(RunOnceAsync);

    /// <summary>
    /// The whole pass, awaitable — <see cref="Start"/> is only this plus a <c>Task.Run</c>.
    ///
    /// <para>Internal and awaitable so gate refusal, lease lifetime and the failure paths are
    /// deterministically testable. The previous version kept this private behind a <c>void</c>
    /// Start and claimed in a comment to be "a seam a test can drive", which was false: nothing
    /// could observe the untracked task, so the lease behaviour around a DESTRUCTIVE operation had
    /// no coverage at all (Codex, diff review round 2).</para>
    /// </summary>
    internal async Task RunOnceAsync()
    {
        try
        {
            await Task.Yield();   // never run the body synchronously on a caller's thread
            SweepRetiredModels();
        }
        catch (Exception ex)
        {
            // Structural, not per-name: with Start() there is no awaiting caller, so anything
            // escaping is an unobserved task fault. The per-name catches inside the sweep cover the
            // deletes themselves; this covers the lease, aggregation and the logging.
            Logger.Warning(ex, "Startup maintenance failed");
        }
    }

    /// <summary>
    /// Delete the on-disk files of models the catalogue no longer ships.
    ///
    /// <para><c>LocalModelMigration</c> rewrites persisted model NAMES at launch; the retired
    /// models' bytes stay behind, unreachable — <c>InstalledCatalogModels</c> drops on-disk names
    /// the catalogue does not know, and the Models page iterates the catalogue — and unbounded: a
    /// user who sampled the old tiers can hold several GB no surface can see or delete. Owner
    /// instruction 2026-08-03.</para>
    ///
    /// <para><b>Not reachable from the settings-import path.</b> <c>LocalModelMigration</c>'s other
    /// caller is <c>ImportExportService</c>, and restoring a settings backup must never destroy
    /// downloaded models. The sweep hangs off launch only, which is why it lives here and not
    /// inside the migration it shares an allowlist with.</para>
    ///
    /// <para><b>On ordering against <c>LocalModelMigration.RunOnSettings</c>:</b> the migration does
    /// complete first — <c>App</c> calls it earlier in <c>OnLaunched</c> — but nothing depends on
    /// that. An earlier version claimed the sweep ran "deliberately after" it so the selection
    /// could no longer name a deleted file, which sounded careful and was not load-bearing: a
    /// retired name is unusable either way (<c>TranscriptionServiceRegistry</c> throws on it), so
    /// deleting the bytes before or after the name is rewritten reaches the same end state. The two
    /// touch disjoint state — files versus settings — and this pass is fire-and-forget, so the
    /// completion order was never guaranteed regardless.</para>
    ///
    /// <para><b>F19 lease.</b> <c>DataErasureService</c> recursively deletes <c>{root}/Models</c>
    /// under the exclusive erasure lease, and an outstanding cleanup pass is what its pre-commit
    /// <c>Check()</c> poll drains. The lease is taken and released INSIDE the pass — disposing
    /// around a fire-and-forget launch instead would reopen that race, the Codex R4 finding on the
    /// sibling reference-orphan sweep.</para>
    /// </summary>
    private void SweepRetiredModels()
    {
        if (!_gate.TryBeginCleanupPass(out var lease))
            return;   // exclusive maintenance in progress — the next launch sweeps instead

        try
        {
            // Constructed HERE, inside the guard — see the factory note on the type. Its DI
            // registration creates the models directory, and doing that on the launch thread made
            // an inaccessible Models folder a fatal startup error.
            var result = _downloads().DeleteRetiredModelFiles(LocalModelMigration.RetiredArtifacts);

            // Only when something happened: this runs on EVERY launch, forever, and a no-op line
            // would be pure noise.
            if (result.FilesDeleted > 0)
            {
                Logger.Information(
                    "Reclaimed {Megabytes:F0} MB from {Count} model file(s) the catalogue no longer ships",
                    result.BytesFreed / 1024d / 1024d, result.FilesDeleted);
            }

            // ONE line, not one per name: a permanently undeletable file (read-only, ACL'd, held
            // open by third-party software) would otherwise warn on every launch for good.
            if (result.SkippedNames.Count > 0)
            {
                Logger.Warning("Could not remove {Count} retired model file(s): {Names}",
                    result.SkippedNames.Count, string.Join(", ", result.SkippedNames));
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
