using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-29 slice 4: Step 0's table at RUNTIME — the one owner of the GGUF artifact, the resident
/// server's inputs, the migration download, the per-install verification, and the delete flow,
/// so no caller special-cases the second bundle
/// (design: <c>docs/plans/2026-08-23-trn29-parakeetcpp-swap/18-slice4-routing-coordinator.md</c>).
/// Registered ONLY when <c>PcppFeature.IsEnabled</c> — the flag's single composition-root read.
///
/// <para><b>The GGUF descriptor lives HERE, not in <c>PredefinedModels.Models</c></b> — that
/// array is the Models-page render list and this row must not render until the rollout PR.
/// "The manager needs no changes for a non-catalog descriptor" is VERIFIED, not assumed:
/// install/delete are descriptor-driven, the disk scan reads <c>*.bin</c> only, bundle
/// enumeration is catalog-driven (the hidden row is simply absent), and the retired sweep is a
/// closed allowlist whose <c>File.Delete</c> throws on directories.</para>
///
/// <para><b>Install identity binds the resident server to the INSTALL, not the path</b> (plan
/// round, challenge 3): the completion manifest's last-write ticks change on every install, so
/// a delete + re-download at the same path retires the prior resident child instead of serving
/// its stale model.</para>
///
/// <para><b>The delete tombstone is DURABLE and survives partial failure</b> (challenge 4): a
/// marker file beside the bundles. Clearing it after "legacy remains + GGUF gone" would let
/// <c>AutoDownloadGguf</c> re-fetch the bytes the user just asked to remove; it clears only
/// when both artifacts (and staging) are verifiably gone.</para>
///
/// <para><b>Lock ordering:</b> <c>_state</c> (this type) → the server manager's gate, always in
/// that direction; the verification task takes only the dedicated ledger lock (order
/// <c>_state</c> → ledger), so no task the delete joins can ever need <c>_state</c>.
/// <c>_state</c> is never held across the server acquire's health wait. The UI-thread delete is
/// BOUNDED, not instant: ≤10 s per background join, a 2 s gate bound on the retire, and every
/// timeout ABORTS with the tombstone kept — the delete never proceeds past a step it could not
/// confirm, and never blocks on a lock another operation holds unboundedly. The accepted race:
/// an acquire past derivation can respawn during the kill-to-file-delete window, in which case
/// the locked GGUF fails its delete, the tombstone STAYS, and the user's retry completes it —
/// a failed delete with an honest retry, never a wrong-model serve.</para>
/// </summary>
public sealed class ParakeetBackendCoordinator : IParakeetPcppBackend
{
    private static ILogger Logger => Log.ForContext<ParakeetBackendCoordinator>();

    /// <summary>The GGUF payload file name — single-sourced in <see cref="ParakeetCatalog"/>
    /// since the G6 flip made the catalog row config-conditional; aliased here so the
    /// coordinator's call sites and tests read unchanged.</summary>
    internal const string GgufFileName = ParakeetCatalog.GgufFileName;

    /// <summary>The bundle (directory) name under the Models root.</summary>
    internal const string GgufModelName = ParakeetCatalog.GgufName;

    /// <summary>Measured from the exact binary every G-gate ran (evidence/artifact-pins.md) —
    /// pinning the VALIDATED configuration, not merely a version number.</summary>
    internal const long GgufFileSizeBytes = ParakeetCatalog.GgufFileSizeBytes;
    internal const string GgufSha256 = ParakeetCatalog.GgufSha256;

    /// <summary>parakeet-server.exe v0.5.0 win-VULKAN-x64 (TRN-51, 2026-09-01) — one binary
    /// serving GPU and CPU (device via the child's PARAKEET_DEVICE; CPU path measured
    /// byte-identical to the retired win-cpu-x64 build, 0/56 clips on two machines). Measured
    /// from the vendored bytes; MUST equal installer/runtime/parakeet/EXPECTED_SHA256 — update
    /// both together (that file's own rule, and a parity test enforces it).</summary>
    internal const string ServerExeSha256 = "5b2da797198ee0ef9ce4438f2be71cf77fa7089efe7617d956cb6fb75ec54f73";

    /// <summary>Free-space slack over the exact payload size for the migration download.</summary>
    internal const long DiskSlackBytes = 256L * 1024 * 1024;

    /// <summary>The durable delete tombstone's file name, in the Models root. Invisible to the
    /// manager by construction (not <c>*.bin</c>, not a catalog bundle name).</summary>
    internal const string TombstoneFileName = ".vw-parakeet-delete-pending";

    /// <summary>TRN-57: how long the STARTUP preload's resident spawn lets a queued GPU warm-up
    /// finish on its own before cancelling it. The warm-up is bounded by construction (30 s
    /// health + two 120 s decodes + kill-and-confirm), and the measured cold compile is 15.5 s on
    /// the desktop's RTX 3080; 120 s covers a cold integrated GPU's two shapes while keeping a
    /// pathological run from holding the preload for its full worst case. Only the startup
    /// preload reaches the quiesce with a live warm-up — every user-initiated path cancels first
    /// (recording admission, Audio Transcribe, a model selection, a language reload) — so this
    /// grace never delays a dictation. A product budget, to be measured on the cold Arc laptop; on
    /// expiry the warm-up is cancelled and the first dictation pays the compile, once per version.</summary>
    internal static readonly TimeSpan ParakeetWarmupSpawnGrace = TimeSpan.FromSeconds(120);

    /// <summary>The GGUF bundle descriptor the manager installs/deletes by — since the G6 flip
    /// this is the CATALOG row in a PCPP_ENABLED build and the auxiliary artifact in a
    /// kill-switch rebuild; either way the definition lives in <see cref="ParakeetCatalog"/>.</summary>
    internal static readonly TranscriptionModelInfo GgufDescriptor = ParakeetCatalog.GgufDescriptor;

    private readonly ModelDownloadManager _downloads;
    private readonly ParakeetServerProcess _server;
    private readonly ParakeetServerTranscriptionClient _client;
    private readonly string _modelsDirectory;
    private readonly string _serverExePath;

    /// <summary>TRN-57: the warm-up whose quiesce guards every resident spawn and the delete.
    /// Production is the process-wide <see cref="GpuWarmup.Instance"/>; tests inject an instance
    /// so the unconfirmed-exit contract (no spawn on <see cref="ParakeetQuiesceOutcome.Unconfirmed"/>)
    /// is a test row rather than a promise.</summary>
    private readonly GpuWarmup _warmup;
    private readonly TranscriptionModelInfo _gguf;
    private readonly string _serverExeSha256;
    private readonly TranscriptionModelInfo _sherpaDescriptor;
    private readonly Func<long> _availableFreeBytes;

    private readonly SemaphoreSlim _state = new(1, 1); // never disposed — the standing shutdown scar.

    // Per-install verification ledger: identity -> hash passed. An entry exists only for a
    // COMPLETED hash; an I/O failure records nothing so the next derivation retries, because
    // marking VerifyFailed on a transient read error would trigger a 940 MB auto re-download.
    // Guarded by _ledgerLock, never by _state: the verification task writes its verdict from a
    // background thread, and routing that write through _state deadlocked against TryDeleteBoth
    // (which holds _state while JOINING the task — self-review F1: a completed hash queued on
    // _state could never finish, freezing the UI-thread delete for the full 10 s join). Lock
    // order where both are held: _state -> _ledgerLock, and the verify task takes ONLY the
    // ledger lock.
    private readonly object _ledgerLock = new();
    private readonly Dictionary<string, bool> _verifiedByIdentity = new(StringComparer.Ordinal);
    private Task? _verification;
    private string? _verifyingIdentity;
    private CancellationTokenSource? _verifyCts;

    private Task? _migration;
    private CancellationTokenSource? _migrationCts;

    // SD2b (self-review): a migration refused with a NON-RETRYABLE install block (NewerSchema —
    // a newer VoiceWink installed this bundle) latches OFF for the session; without this, every
    // preparation re-attempted a download that can never succeed, logging a Warning per
    // recording. Cleared only by process restart, which is when the binary can have changed.
    private bool _migrationPermanentlyBlocked;

    // NotifyServed bookkeeping: which install the last lease belonged to, and which install has
    // proven itself. Plain string fields — reference assignments are atomic, the consumer is a
    // decision derived fresh under the state lock, and a benignly stale read costs one deferred
    // auto-cleanup attempt (the next successful serve retries).
    private string? _lastLeaseIdentity;
    private string? _servedIdentity;

    /// <summary>K1 (Kimi diff r1): consecutive whole-call decode failures. The storm fuse only
    /// sees INVOLUNTARY exits, so a server that spawns healthy and fails every decode (upstream
    /// regression, model/server mismatch) is killed voluntarily each time and never charges it
    /// — with no sherpa left, the user would loop "Retry will restart the engine" forever.
    /// At <see cref="MaxConsecutiveWholeCallFailures"/> the derivation reads the backend
    /// unrunnable for the session; a successful decode resets. Interlocked — the writers run
    /// outside <c>_state</c> (the decode path must not take it).</summary>
    private int _consecutiveWholeCallFailures;

    internal const int MaxConsecutiveWholeCallFailures = 3;

    /// <summary>Test seam ONLY (TRN-57): the warm-up instance whose quiesce guards the resident
    /// spawn and the delete. An <c>internal</c> overload rather than a parameter on the public
    /// constructor because <see cref="GpuWarmup"/> is internal; production always takes the
    /// process-wide <see cref="GpuWarmup.Instance"/> through the public constructor.</summary>
    internal ParakeetBackendCoordinator(
        ModelDownloadManager downloads,
        ParakeetServerProcess server,
        ParakeetServerTranscriptionClient client,
        string modelsDirectory,
        string serverExePath,
        GpuWarmup warmup,
        Func<long>? availableFreeBytes = null,
        TranscriptionModelInfo? ggufDescriptorOverride = null,
        TranscriptionModelInfo? sherpaDescriptorOverride = null,
        string? serverExeSha256Override = null)
        : this(downloads, server, client, modelsDirectory, serverExePath,
               availableFreeBytes, ggufDescriptorOverride, sherpaDescriptorOverride, serverExeSha256Override)
    {
        _warmup = warmup;
    }

    /// <param name="availableFreeBytes">Free bytes on the Models volume; injected so the disk
    /// gate is a test row rather than a machine fact.</param>
    /// <param name="ggufDescriptorOverride">Test seam ONLY: the real payload is 940 MB, which no
    /// test can fabricate for the manager's exact-size installed check; a tiny descriptor makes
    /// every tier derivable from real files. Production always uses <see cref="GgufDescriptor"/>.</param>
    /// <param name="sherpaDescriptorOverride">Test seam ONLY, same reason (the legacy bundle is
    /// ~670 MB across four files).</param>
    /// <param name="serverExeSha256Override">Test seam ONLY: lets a test's stub exe pass the
    /// per-launch provenance check so acquire-success rows are reachable.</param>
    public ParakeetBackendCoordinator(
        ModelDownloadManager downloads,
        ParakeetServerProcess server,
        ParakeetServerTranscriptionClient client,
        string modelsDirectory,
        string serverExePath,
        Func<long>? availableFreeBytes = null,
        TranscriptionModelInfo? ggufDescriptorOverride = null,
        TranscriptionModelInfo? sherpaDescriptorOverride = null,
        string? serverExeSha256Override = null)
    {
        _downloads = downloads;
        _server = server;
        _client = client;
        _modelsDirectory = modelsDirectory;
        _serverExePath = serverExePath;
        _gguf = ggufDescriptorOverride ?? GgufDescriptor;
        _serverExeSha256 = serverExeSha256Override ?? ServerExeSha256;
        _warmup = GpuWarmup.Instance;
        // EXPLICIT descriptor, never "the first catalog row whose runtime is Parakeet" — the G6
        // flip made that row the GGUF bundle, so the positional default would silently re-target
        // every sherpa-keyed operation (installed derivation, tombstone probes, delete, the
        // ServeSherpa path accessor) at the WRONG artifact. The plan round named the old line the
        // flip's most dangerous; ParakeetBackendCoordinatorTests pins this default's name.
        _sherpaDescriptor = sherpaDescriptorOverride ?? ParakeetCatalog.SherpaDescriptor;
        _availableFreeBytes = availableFreeBytes
            ?? (() =>
            {
                try
                {
                    return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(modelsDirectory))!)
                        .AvailableFreeSpace;
                }
                catch
                {
                    return 0; // unreadable disk state gates the DOWNLOAD closed, nothing else.
                }
            });
    }

    private string GgufBundleDir => Path.Combine(_modelsDirectory, _gguf.Name);
    internal string GgufFilePath => Path.Combine(GgufBundleDir, _gguf.Files![0].RelativePath);
    private string ManifestPath => Path.Combine(GgufBundleDir, BundleRelativePathGuard.ManifestFileName);
    private string TombstonePath => Path.Combine(_modelsDirectory, TombstoneFileName);

    /// <summary>One derivation: the facts + the decision + the identity, taken under
    /// <c>_state</c> so the ledger and tombstone reads are coherent.</summary>
    internal readonly record struct Snapshot(
        ParakeetTransitionState State,
        ParakeetTransitionDecision Decision,
        string? InstallIdentity);

    internal async Task<Snapshot> DeriveAsync(CancellationToken ct)
    {
        await _state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return DeriveUnderState();
        }
        finally
        {
            _state.Release();
        }
    }

    /// <param name="forRead">True for UI-read derivations (RowInstalled/HasAnyArtifact): the
    /// background STARTERS are suppressed — without this, merely OPENING the Models page
    /// started the 940 MB migration and the hash task (Kimi diff r2, challenge 3: the render
    /// trigger was new surface from the reachability fix, never a planned entry point).
    /// Preparation and acquire remain the only migration triggers.</param>
    private Snapshot DeriveUnderState(bool forRead = false)
    {
        var sherpaInstalled = _downloads.TryGetInstalledLocation(_sherpaDescriptor) is not null;
        var ggufInstalled = _downloads.TryGetInstalledLocation(_gguf) is not null;

        string? identity = null;
        GgufTier tier;
        if (ggufInstalled)
        {
            identity = ReadInstallIdentity();
            if (identity is null)
            {
                // Manifest vanished between the installed check and the identity read: not
                // installed after all.
                tier = _downloads.HasStagingFor(_gguf.Name) ? GgufTier.Staging : GgufTier.Absent;
            }
            else if (TryGetVerdict(identity) is { } passed)
            {
                tier = passed ? GgufTier.PcppEligible : GgufTier.VerifyFailed;
            }
            else
            {
                tier = GgufTier.Installed;
                if (!forRead)
                {
                    EnsureVerificationStartedUnderState(identity);
                }
            }
        }
        else
        {
            tier = _downloads.HasStagingFor(_gguf.Name) ? GgufTier.Staging : GgufTier.Absent;
        }

        // CX1 (Codex diff r1 Blocker): File.Exists collapses an ACCESS failure into "absent",
        // and an unreadable tombstone deriving as absent re-enables the exact 940 MB
        // auto-download the tombstone prohibits. Unknown fails CLOSED: DeletePending stays true.
        var deletePending = ProbePresence(TombstonePath) ?? true;

        // Lazy tombstone self-heal, two clears (slice-4 self-review, state F1):
        //
        // (a) keyed on the BYTES being gone (directory absence), never on the installed flag: a
        //     phase-2 delete failure leaves a model UNINSTALLED (manifest gone) with its
        //     multi-hundred-MB payload still on disk, and an installed-state check declared that
        //     "complete" and cleared the tombstone over live debris (caught by this row's own
        //     test). The tombstone means "delete not finished"; finished means the disk is free.
        //
        // (b) keyed on a bundle manifest NEWER than the tombstone: the user re-downloaded a
        //     Parakeet artifact AFTER the delete, which supersedes the delete's intent — without
        //     this clear, a partial delete followed by a reinstall left the tombstone PERMANENT
        //     (the directory-absence clear can never fire once a bundle exists again), silently
        //     killing the migration forever. The anti-redownload trap is untouched: the
        //     AUTOMATIC download is blocked while the tombstone stands, so only a USER install
        //     can mint the newer manifest.
        if (deletePending)
        {
            var healed = false;
            // POSITIVE absence only (CX1): Directory.Exists reads unreadable as gone, and a
            // self-heal over a merely-unreadable bundle would clear the tombstone above live
            // bytes. ProbePresence == false is the one answer that may say "gone"; unknown
            // keeps the tombstone. (HasStagingFor fails closed the same way on its side.)
            if (ProbePresence(Path.Combine(_modelsDirectory, _sherpaDescriptor.Name)) == false
                && ProbePresence(GgufBundleDir) == false
                && !_downloads.HasStagingFor(_gguf.Name))
            {
                healed = true;
            }
            else if (AnyManifestNewerThanTombstone())
            {
                healed = true;
            }
            if (healed)
            {
                // The local flag follows the PHYSICAL clear (Codex diff r2): reporting
                // not-pending over a marker that persists would let AutoDownloadGguf fire on
                // this derivation and flip back next derivation — behavior keyed to a file the
                // report claims is gone.
                deletePending = !TryClearTombstone();
            }
        }

        var state = new ParakeetTransitionState(
            PcppFeatureOn: true, // this type only exists in a flag-on composition.
            SherpaInstalled: sherpaInstalled,
            Gguf: tier,
            PcppRunnable: File.Exists(_serverExePath) && !_server.IsStormTripped
                          && Volatile.Read(ref _consecutiveWholeCallFailures) < MaxConsecutiveWholeCallFailures,
            PcppHasServed: identity is not null
                           && string.Equals(_servedIdentity, identity, StringComparison.Ordinal),
            DiskAllowsGguf: _availableFreeBytes() >= _gguf.FileSizeBytes + DiskSlackBytes,
            DeletePending: deletePending,
            // != false, deliberately: an unreadable probe keeps the auto-cleanup REACHABLE —
            // the same fail-open-toward-delete direction as HasAnyArtifact. Over genuinely
            // absent bytes the delete core returns NotPresent → done and the latch settles, so
            // the cost of a wrong true is one no-op background attempt.
            SherpaBytesRemain: ProbePresence(Path.Combine(_modelsDirectory, _sherpaDescriptor.Name)) != false);

        var decision = ParakeetEngineTransition.Decide(state);

        if (!forRead && decision.AutoDownloadGguf && !_migrationPermanentlyBlocked)
        {
            EnsureMigrationDownloadStartedUnderState(tier);
        }

        return new Snapshot(state, decision, identity);
    }

    /// <summary>Tri-state occupancy probe: true = something is at the path, false = POSITIVE
    /// absence (FileNotFound/DirectoryNotFound), null = unreadable — the caller decides the
    /// safe direction, and every caller here treats unknown as "cannot proceed" (CX1).</summary>
    private static bool? ProbePresence(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Does any Parakeet bundle manifest post-date the tombstone? Unreadable times
    /// answer false (the safe direction: the tombstone stays).</summary>
    private bool AnyManifestNewerThanTombstone()
    {
        try
        {
            var tombstone = new FileInfo(TombstonePath);
            if (!tombstone.Exists) return false;
            var cutoff = tombstone.LastWriteTimeUtc;
            foreach (var dir in new[] { Path.Combine(_modelsDirectory, _sherpaDescriptor.Name), GgufBundleDir })
            {
                var manifest = new FileInfo(Path.Combine(dir, BundleRelativePathGuard.ManifestFileName));
                if (manifest.Exists && manifest.LastWriteTimeUtc > cutoff)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The install identity: the completion manifest's last-write ticks — written LAST
    /// in the transactional install, so it changes on every (re)install and exists only for a
    /// committed one.</summary>
    private string? ReadInstallIdentity()
    {
        try
        {
            var manifest = new FileInfo(ManifestPath);
            return manifest.Exists ? manifest.LastWriteTimeUtc.Ticks.ToString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void EnsureVerificationStartedUnderState(string identity)
    {
        if (_verification is { IsCompleted: false }
            && string.Equals(_verifyingIdentity, identity, StringComparison.Ordinal))
        {
            return; // already verifying THIS install.
        }

        _verifyCts?.Cancel();
        _verifyCts = new CancellationTokenSource();
        _verifyingIdentity = identity;
        var token = _verifyCts.Token;
        var path = GgufFilePath;
        _verification = Task.Run(async () =>
        {
            bool passed;
            try
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(
                    await global::System.Security.Cryptography.SHA256.HashDataAsync(stream, token)
                        .ConfigureAwait(false));
                passed = string.Equals(hash, _gguf.Files![0].Sha256Hash, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                return; // superseded or shutting down: record nothing, the next derivation retries.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transient read failure is NOT a verify failure — recording false here would
                // auto-trigger a 940 MB re-download over a file lock. Record nothing; retry later.
                Logger.Warning(ex, "parakeet gguf verification could not read the file; will retry");
                return;
            }

            lock (_ledgerLock)
            {
                _verifiedByIdentity[identity] = passed;
            }

            if (passed)
            {
                Logger.Information("parakeet gguf verified for install {Identity} - pcpp eligible", identity);
            }
            else
            {
                Logger.Error("parakeet gguf hash MISMATCH for install {Identity} - artifact broken, re-download is the repair", identity);
            }
        }, token);
    }

    private void EnsureMigrationDownloadStartedUnderState(GgufTier tier)
    {
        if (_migration is { IsCompleted: false })
        {
            return;
        }

        _migrationCts?.Dispose();
        _migrationCts = new CancellationTokenSource();
        var token = _migrationCts.Token;
        _migration = Task.Run(async () =>
        {
            try
            {
                // KV1 (Kimi diff r2 Blocker): a VerifyFailed artifact must be DELETED before the
                // re-download — the manager's install short-circuits on its cheap SIZE-ONLY
                // Installed classification (that cheapness is the Installed tier's own design
                // premise), so a same-size-corrupted bundle returned "already installed" and the
                // bytes were never replaced: the repair arm the per-install hash exists for was
                // a permanent no-op loop. Delete is the existing marker-first primitive under
                // the per-model lock. A delete failing BEFORE invalidation leaves VerifyFailed
                // standing for the next preparation's retry; one failing AFTER it
                // (InvalidatedButCleanupFailed) converges by a different path - the manifest is
                // gone, the next derivation reads Absent, and the plain migration's own install
                // sweeps the debris (Kimi r3, nit 4).
                if (tier == GgufTier.VerifyFailed)
                {
                    var cleared = _downloads.DeleteModel(_gguf);
                    if (cleared is not (ModelDeleteOutcome.Deleted or ModelDeleteOutcome.NotPresent))
                    {
                        Logger.Warning(
                            "parakeet gguf corrupt-refetch: could not remove the broken bundle ({Outcome}) - will retry",
                            cleared);
                        return;
                    }
                    Logger.Information("parakeet gguf corrupt-refetch: broken bundle removed; re-downloading");
                }

                Logger.Information("parakeet gguf migration download starting ({Bytes} bytes)", _gguf.FileSizeBytes);
                await _downloads.DownloadModelAsync(_gguf, progress: null, token).ConfigureAwait(false);
                Logger.Information("parakeet gguf migration download complete");
            }
            catch (OperationCanceledException)
            {
                Logger.Information("parakeet gguf migration download cancelled");
            }
            catch (ModelInstallBlockedException ex) when (!ex.IsRetryable)
            {
                // NewerSchema: retrying cannot succeed until the binary changes. Latch for the
                // session so this does not become a Warning per recording (SD2b).
                _migrationPermanentlyBlocked = true;
                Logger.Warning(ex, "parakeet gguf migration blocked (non-retryable); not retrying this session");
            }
            catch (Exception ex)
            {
                // Fail-soft: sherpa keeps serving; the next preparation's derivation re-decides
                // whether to try again (stale staging resumes as a fresh download).
                Logger.Warning(ex, "parakeet gguf migration failed; sherpa keeps serving");
            }
        }, CancellationToken.None);
    }

    public Task<PcppPrepareOutcome> PrepareAsync(CancellationToken ct)
        => PrepareCoreAsync(ct, awaitPendingVerification: true);

    private async Task<PcppPrepareOutcome> PrepareCoreAsync(CancellationToken ct, bool awaitPendingVerification)
    {
        var snapshot = await DeriveAsync(ct).ConfigureAwait(false);

        switch (snapshot.Decision.Serve)
        {
            case ParakeetBackend.Pcpp:
                // TRN-49: the warm-up child must be CONFIRMED dead before the resident spawns —
                // TerminateProcess is asynchronous, and an Auto child initializing its GPU
                // device against ~1 GB of dying warm child can fail into the uncharged CPU
                // retry and latch Cpu for the whole session (self-review, concurrency lens).
                //
                // TRN-57: with a GRACE, not cancel-first. This is the STARTUP preload's path (the
                // only prepare that reaches here with a live warm-up — every user-initiated
                // prepare cancels first), and it arrives 3 ms after the warm-up was queued; the
                // pre-TRN-57 cancel-first quiesce killed the warm-up on every Parakeet-default
                // machine and logged it as "recording admission". The preload now waits for the
                // warm-up to finish (bounded by ParakeetWarmupSpawnGrace), which is also the order
                // that makes its first dictation fast. Unconfirmed = a warm child may be alive:
                // NO resident spawn this time (the row-10 fallback below, for this preparation
                // only); the next preparation re-runs the quiesce.
                var quiesce = await _warmup.WaitForParakeetQuiesceAsync(
                        ParakeetWarmupSpawnGrace, GpuWarmupCancelReason.ResidentSpawnGraceExpired)
                    .ConfigureAwait(false);

                // Warm the server NOW so the decode at recording stop finds it resident — the
                // sherpa analogue is LoadModelAsync's 3.1 s build riding this same path.
                ParakeetServerProcess.ServerLease? lease = null;
                if (quiesce == ParakeetQuiesceOutcome.Unconfirmed)
                {
                    Logger.Warning("parakeet: the GPU warm-up child's exit is unconfirmed - not spawning the resident server for this preparation; the next preparation re-checks");
                }
                else
                {
                    lease = await _server.TryAcquireAsync(
                        _serverExePath, _serverExeSha256, GgufFilePath, snapshot.InstallIdentity!, ct)
                        .ConfigureAwait(false);
                }
                if (lease is not null)
                {
                    _lastLeaseIdentity = snapshot.InstallIdentity;
                    return PcppPrepareOutcome.ServePcpp;
                }
                // Row 10 at runtime: acquire failed AFTER the decision said pcpp. With a legacy
                // bundle the call proceeds on sherpa; without one, bytes exist but nothing can
                // serve — Unavailable, never NotDownloaded (a download is not the repair).
                return snapshot.State.SherpaInstalled
                    ? PcppPrepareOutcome.ServeSherpa
                    : PcppPrepareOutcome.Unavailable;

            case ParakeetBackend.Sherpa:
                return PcppPrepareOutcome.ServeSherpa;

            default:
                // A GGUF-only install at Installed (hash PENDING) is transitional by design —
                // but a fresh install's FIRST preparation is also the very derivation that
                // STARTS the 940 MB hash, so answering Unavailable here made every fresh
                // install's first recording fail with "can't run on this version" while the
                // real answer was seconds away (self-review, config lens). Nothing can serve
                // in this state anyway, so a bounded wait for the in-flight verification costs
                // only the wait; ONE re-entry, and a still-pending, failed, or verify-failed
                // outcome falls through to the honest answer below.
                if (awaitPendingVerification
                    && snapshot.State.Gguf == GgufTier.Installed
                    && _verification is { IsCompleted: false } pendingVerification)
                {
                    try
                    {
                        await pendingVerification.WaitAsync(TimeSpan.FromSeconds(20), ct)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Still hashing (a slow disk, AV interference) — fall through honestly.
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The verification task logs its own failures; the re-derive below
                        // reads whatever tier it recorded.
                        Logger.Debug(ex, "pending-verification wait surfaced the task's own failure");
                    }
                    return await PrepareCoreAsync(ct, awaitPendingVerification: false)
                        .ConfigureAwait(false);
                }

                return snapshot.State.Gguf is GgufTier.Installed or GgufTier.PcppEligible
                    ? PcppPrepareOutcome.Unavailable
                    : PcppPrepareOutcome.NotDownloaded;
        }
    }

    public async Task<PcppAcquire> TryAcquireForTranscriptionAsync(CancellationToken ct)
    {
        // Every new transcription invalidates any unconsumed proof from the previous one (Codex
        // diff r2): the ViewModel's usable-text signal must only ever commit the pcpp success it
        // directly followed — never a stale one behind a sherpa fall-through or another engine.
        _pendingProofIdentity = null;

        var snapshot = await DeriveAsync(ct).ConfigureAwait(false);

        switch (snapshot.Decision.Serve)
        {
            case ParakeetBackend.Pcpp:
                // TRN-49: same confirmation as PrepareCoreAsync — see the comment there — but with
                // ZERO grace (TRN-57, Codex plan round): this is the live transcription acquire,
                // and admission normally cancelled the warm-up already; a dictation never waits.
                var quiesce = await _warmup.WaitForParakeetQuiesceAsync(
                        TimeSpan.Zero, GpuWarmupCancelReason.TranscriptionAcquire)
                    .ConfigureAwait(false);
                if (quiesce == ParakeetQuiesceOutcome.Unconfirmed)
                {
                    Logger.Warning("parakeet: the GPU warm-up child's exit is unconfirmed - not spawning the resident server for this transcription");
                    return snapshot.State.SherpaInstalled
                        ? new PcppAcquire(PcppServe.Sherpa, null, 0)
                        : new PcppAcquire(PcppServe.FailCall, null, 0);
                }

                var lease = await _server.TryAcquireAsync(
                    _serverExePath, _serverExeSha256, GgufFilePath, snapshot.InstallIdentity!, ct)
                    .ConfigureAwait(false);
                if (lease is { } l)
                {
                    _lastLeaseIdentity = snapshot.InstallIdentity;
                    return new PcppAcquire(PcppServe.Pcpp, l.BaseUri, l.Generation);
                }
                return snapshot.State.SherpaInstalled
                    ? new PcppAcquire(PcppServe.Sherpa, null, 0)
                    : new PcppAcquire(PcppServe.FailCall, null, 0);

            case ParakeetBackend.Sherpa:
                return new PcppAcquire(PcppServe.Sherpa, null, 0);

            default:
                // Serve == None with sherpa installed is unreachable (the decision serves sherpa
                // whenever it exists); without sherpa the call fails as a normal transcription
                // failure — challenge 2's specified outcome.
                return new PcppAcquire(PcppServe.FailCall, null, 0);
        }
    }

    public string DecodeSlice(Uri baseUri, float[] slice, int sampleRate, CancellationToken ct)
    {
        // Sync-over-async on the service's decode worker (inside Task.Run) — no sync context to
        // deadlock, and the sherpa decode it replaces is equally synchronous native work.
        var result = _client.DecodeAsync(baseUri, slice, sampleRate, ct).GetAwaiter().GetResult();
        if (!result.Success)
        {
            throw new PcppDecodeException(result.FailureClass ?? "unknown");
        }
        return result.Text;
    }

    /// <summary>TRN-50: the once-per-session bound on the CPU re-decode — consumed by the FIRST
    /// attempt that actually SPAWNS, whatever its outcome, so a machine whose audio decodes empty
    /// on both devices pays the ~1 GB throwaway child exactly once. Not consumed by the no-GGUF
    /// refusal (a delete tombstone mid-flight; nothing spawned) nor by a USER cancel before the
    /// verdict — the safety net is for exactly the machine where a cancelled first attempt is
    /// followed by a second empty GPU decode (self-review, concurrency lens). A provenance-gate
    /// refusal and a launch failure DO consume it although they spawn nothing: neither heals
    /// in-session, so a per-dictation retry would only repeat the refusal (Kimi diff r1).</summary>
    private int _cpuFallbackAttempted;

    /// <summary>TRN-50: the per-chunk bound on the CPU re-decode. The body runs under the
    /// transcription service's lock, so a WEDGED throwaway child that passed its health probe
    /// would otherwise park every queued dictation on the named client's 5-minute deadline per
    /// chunk (self-review Blocker: 16 chunks of an 8-minute file ≈ 80 min holding the lock). One
    /// chunk is at most ~35 s of audio; the slowest CPU path measured (the owner's ARM64 laptop
    /// under emulation) decodes at ~0.23× real time, so 60 s is ~7× that and still 1.7× a
    /// real-time decode. A wedge costs one budget, not N: the first expiry ends the whole
    /// re-decode. Same shape as <c>GpuWarmup</c>'s per-decode bound.</summary>
    internal static readonly TimeSpan CpuFallbackChunkBudget = TimeSpan.FromSeconds(60);

    /// <summary>Test seam only: the chunk budget is 60 s wall-clock and the expiry branch is
    /// untestable at that cost. Production never sets it.</summary>
    internal TimeSpan? CpuFallbackChunkBudgetOverride { get; set; }

    public async Task<string?> TryDecodeOnCpuFallbackAsync(
        int generation, IReadOnlyList<DecodeChunk> plan, float[] samples, int sampleRate, double gainDb,
        CancellationToken ct)
    {
        // Positive GPU evidence only (Codex plan round, Blocker 2): the lease's child must have
        // printed a Vulkan selection. An Auto child serving CPU on a loader-present/no-device
        // machine, or one that never printed the line, is refused — nothing to re-decode
        // differently, and a "GPU failed" verdict there would be false.
        var observed = _server.ObservedFor(generation);
        var backend = observed?.Backend;
        if (!ParakeetGpuEvidence.IsGpu(backend))
        {
            Logger.Information("parakeet CPU re-decode: generation {Gen} is not GPU-confirmed ({Backend}) - not attempted",
                generation, LogValueSanitizer.SingleLine(backend ?? "no device line"));
            return null;
        }
        var gpuName = observed!.Value.GpuName;
        var ggufPath = GgufFilePath;
        if (!File.Exists(ggufPath))
        {
            // Before the latch: nothing spawned, so nothing was paid — a transient miss (a delete
            // tombstone mid-flight) must not burn the session's one attempt.
            Logger.Information("parakeet CPU re-decode: no GGUF on disk - not attempted");
            return null;
        }
        if (Interlocked.Exchange(ref _cpuFallbackAttempted, 1) != 0)
        {
            Logger.Information("parakeet CPU re-decode: already attempted this session - not attempted");
            return null;
        }

        var chunkBudget = CpuFallbackChunkBudgetOverride ?? CpuFallbackChunkBudget;
        string? joined = null;
        var result = await ParakeetEphemeralChild.RunAsync(
            "parakeet CPU re-decode",
            _server.Launcher, _client, _serverExePath, _serverExeSha256, ggufPath, ParakeetLaunchMode.Cpu,
            async (child, baseUri, token) =>
            {
                // The SAME plan at the SAME first-attempt gain, joined by the shipped join — the
                // one variable between the two decodes is the device. Native work off this thread,
                // the sync-over-async shape DecodeSlice already uses. Each chunk is bounded: the
                // expiry throws out of the join as a cancellation the caller's token did not
                // request, which RunAsync reports as Cancelled and the classification below
                // separates from a user cancel.
                joined = await Task.Run(() => ChunkedDecode.Run(plan, samples, slice =>
                {
                    var input = DecodeInputGain.Apply(slice, gainDb, out _);
                    using var decode = CancellationTokenSource.CreateLinkedTokenSource(token);
                    decode.CancelAfter(chunkBudget);
                    var r = _client.DecodeAsync(baseUri, input, sampleRate, decode.Token).GetAwaiter().GetResult();
                    if (!r.Success)
                    {
                        throw new PcppDecodeException(r.FailureClass ?? "unknown");
                    }
                    return r.Text.Trim();
                }, token), token).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);

        if (result.UnconfirmedChild is { } lingering)
        {
            // A throwaway CPU child whose kill was not confirmed: it is not the GPU-init hazard the
            // resident's quiesce guards against (no device to initialize against), so it is not
            // retained — closing its job handle is the kernel-level kill (KILL_ON_JOB_CLOSE).
            lingering.Dispose();
        }
        var text = joined;
        if (result.Outcome == ParakeetEphemeralChild.Outcome.Cancelled)
        {
            if (ct.IsCancellationRequested)
            {
                // The USER cancelled before a verdict: nothing was learned, and the next empty GPU
                // decode on this machine deserves its attempt.
                Interlocked.Exchange(ref _cpuFallbackAttempted, 0);
                Logger.Information("parakeet CPU re-decode: cancelled by the caller before a verdict - the attempt is not consumed");
                return null;
            }
            // The chunk budget fired: a child that answered health but does not decode. The
            // attempt STAYS consumed — a wedged CPU child must not be re-spawned per dictation.
            Logger.Warning("parakeet CPU re-decode: a chunk exceeded {Seconds:F0}s - abandoned; the empty GPU result stands (TRN-50)",
                chunkBudget.TotalSeconds);
            return null;
        }
        if (!result.BodySucceeded || text is null)
        {
            Logger.Information("parakeet CPU re-decode: did not complete ({Outcome}) - the empty GPU result stands", result.Outcome);
            return null;
        }
        if (text.Length == 0)
        {
            Logger.Information("parakeet CPU re-decode: also empty - the audio, not the GPU; nothing latched");
            return text;
        }

        // Text on the CPU where the GPU produced none: this session's remaining spawns go CPU
        // (consumed by the next acquire — never a gate wait from here), and the verdict persists
        // so the next launch of this app version starts on CPU and the Models page row says why.
        _server.RequestCpu("GPU decode returned nothing on non-silent audio and the CPU re-decode produced text (TRN-50)");
        var persisted = _warmup.RecordParakeetVerdict(GpuSelfTestOutcome.Fail, gpuName);
        // REL-30: the same failure class as the warm-up self-test, reported the same way — Error
        // (so it reaches Sentry for opted-in users) only once the verdict is durable. The adapter
        // and driver are what make a field report actionable; the decoded text never appears.
        Logger.Write(Helpers.GpuSelfTestReport.LevelFor(persisted),
            "Parakeet GPU decode was empty on non-silent audio on {GpuName}; the CPU re-decode produced {Chars} chars - requesting CPU for this session and app version (TRN-50) [display drivers on this PC: {DriverSignature}]",
            gpuName ?? Helpers.GpuToggleAvailability.UnnamedGpu,
            text.Length,
            Helpers.GpuSelfTestReport.DriverOrUnknown(Helpers.GpuWarmupMarker.CurrentDriverSignature()));
        return text;
    }

    public Task OnWholeCallFailedAsync(int generation)
    {
        // Retire the generation so a Retry spawns FRESH instead of replaying a wedged child. A
        // CRASHED server charges the storm fuse through its involuntary exits; an ALIVE-but-
        // broken one is covered by the consecutive-failure counter (K1) — this retirement is
        // voluntary and the fuse never sees it.
        var failures = Interlocked.Increment(ref _consecutiveWholeCallFailures);
        if (failures == MaxConsecutiveWholeCallFailures)
        {
            Logger.Error(
                "parakeet-server: {Count} consecutive whole-call decode failures - pcpp backend unrunnable until app restart",
                failures);
        }
        // Bounded gate entry (Kimi diff r2, challenge 4): this runs under the service lock,
        // same parking class as the cancellation escalation - an unbounded wait behind a
        // concurrent acquire's 30 s health hold parks every queued transcription. Skip-on-busy
        // is sound by the same argument (any acquire mints a fresh generation) and the K1
        // tripwire covers convergence if the kill is skipped.
        return _server.KillGenerationAsync(generation, ParakeetServerPolicy.EscalationProbeBudget);
    }

    public async Task OnCancelledInFlightAsync(int generation)
    {
        // Non-user tokens throughout: the user's token is cancelled by definition here, and the
        // escalation must run BECAUSE of that, not be skipped by it.
        await Task.Delay(ParakeetServerPolicy.CancelKillGrace, CancellationToken.None)
            .ConfigureAwait(false);
        var answered = await _server.ProbeGenerationAsync(
            generation, ParakeetServerPolicy.EscalationProbeBudget).ConfigureAwait(false);
        if (!answered)
        {
            Logger.Information(
                "parakeet-server: generation {Gen} unresponsive after cancel grace - killing", generation);
            // Bounded gate entry (K2): the whole escalation runs while the SERVICE lock is
            // held, so an unbounded wait behind a concurrent acquire's 30 s health hold would
            // park the next transcription for ~40 s. Skipping on timeout is sound - a busy
            // gate means an acquire is interacting, and any acquire (spawn OR reuse) mints a
            // fresh generation, turning this stale kill into a no-op regardless.
            await _server.KillGenerationAsync(
                generation, ParakeetServerPolicy.EscalationProbeBudget).ConfigureAwait(false);
        }
    }

    // Auto-cleanup coordination (owner, 2026-08-24): 0 = idle, 1 = a cleanup task is in flight.
    // At most one runs at a time; overlap protection, not throughput.
    private int _autoCleanupInFlight;

    // Latched once a run finds nothing left to clean (deleted, never present, or the transition
    // says cleanup is not due), so steady-state serves stop spawning probe tasks. Process-scoped
    // on purpose: every launch re-derives fresh, and nothing in an ON build can re-install the
    // sherpa bundle mid-process (the catalog row is the GGUF).
    private volatile bool _autoCleanupSettled;

    // The fire-and-forget task, exposed for tests (InternalsVisibleTo) — the ONLY way a test can
    // await a trigger-started cleanup instead of sleeping.
    internal Task? AutoCleanupTask;

    // The pending proof (Codex diff r2): armed by NotifyWholeCallSucceeded with the identity of
    // the install that just served, consumed by NotifyUsableTranscription, and cleared on every
    // new acquire — so usable text produced by the sherpa fall-through, or by another engine
    // after a stale pcpp success, can never commit "pcpp has proven itself". Plain field: the
    // arm and the consume both happen inside one serialized transcription flow (SingleFlight +
    // the service lock), and a benignly stale read costs one deferred cleanup.
    private string? _pendingProofIdentity;

    public void NotifyWholeCallSucceeded()
    {
        Volatile.Write(ref _consecutiveWholeCallFailures, 0); // a working decode resets K1's tripwire.

        // Arm the proof, do NOT commit it (Codex diff r1+r2): protocol success is not usable
        // text — the raw decode can be empty, and raw text like a bracketed artifact can be
        // EMPTIED by the machine-owned text pipeline afterwards. "pcpp has proven itself" means
        // PIPELINE-SURVIVING ENGINE OUTPUT (the settled criterion is engine health, not user
        // receipt — owner, 2026-08-24), or the first upgraded dictation coming back unusable
        // would delete the only fallback on the strength of nothing. The ViewModel decides that
        // after the pipeline and consumes this arm via NotifyUsableTranscription.
        _pendingProofIdentity = _lastLeaseIdentity;
    }

    public void NotifyUsableTranscription()
    {
        var proof = _pendingProofIdentity;
        if (proof is null) return; // no armed pcpp success behind this text — not pcpp's proof.
        _pendingProofIdentity = null;
        _servedIdentity = proof;

        // AUTO-CLEANUP (owner directive, 2026-08-24: "no delete button or confirmation dialogue —
        // just do it automatically"): after a USABLE pcpp transcription, remove the legacy
        // sherpa bundle in the background. This replaces the Models-page "Free 670 MB" offer.
        // Detached via Task.Run so the caller never waits on a 670 MB delete, and running
        // entirely OFF the UI thread — strictly better than the button path, whose delete held
        // the state lock on the UI thread. A partial failure (a file briefly locked by an AV
        // scan) leaves bytes behind, keeps the latch open, and naturally retries on the NEXT
        // usable transcription — no timer, no new persistent state.
        if (_autoCleanupSettled) return;
        if (Interlocked.CompareExchange(ref _autoCleanupInFlight, 1, 0) != 0) return;
        AutoCleanupTask = Task.Run(() =>
        {
            try
            {
                TryDeleteLegacyCore(out var remainsDue);
                _autoCleanupSettled = !remainsDue;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex,
                    "parakeet auto-cleanup failed - will retry after the next usable transcription");
            }
            finally
            {
                Interlocked.Exchange(ref _autoCleanupInFlight, 0);
            }
        });
    }

    /// <summary>
    /// TRN-38: how long a UI-THREAD reader may wait for <see cref="_state"/> before answering
    /// "unknown". Both readers below are called from the Models page's Loaded refresh and the
    /// Parakeet row's Select gate, and the lock's long holders are the deletes — <see
    /// cref="TryDeleteBoth"/>'s ~20 s worst case (10 s migration join + 10 s verify join + 2 s
    /// retire) and the auto-cleanup's 670 MB sweep. Before this bound they waited unbounded, so
    /// TRN-37's off-thread delete turned them into freeze doors for exactly that window.
    ///
    /// <para>250 ms is chosen to ride out the ORDINARY holder — another reader's
    /// <see cref="DeriveUnderState"/>, which is file probes and a manifest read (single-digit ms
    /// warm, tens of ms on a cold or AV-scanned disk) — while bounding the worst case to a hitch
    /// rather than a hang. Verification and the migration download do NOT hold the lock (both
    /// are <c>Task.Run</c>), so they are not what this is sized against. `internal` so tests can
    /// shorten it (the <c>VoiceActivityDetectionService.DisposeLockTimeout</c> precedent) — do
    /// not read it as a tuning knob for production.</para>
    /// </summary>
    internal TimeSpan UiReaderStateWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: hold <see cref="_state"/> so a contention row is deterministic rather
    /// than a race. Production has no caller — the real holders are the deletes and the derive
    /// paths, none of which a test can park for a controlled interval without also driving a
    /// 940 MB artifact.</summary>
    internal IDisposable HoldStateForTests()
    {
        _state.Wait();
        return new StateHold(_state);
    }

    private sealed class StateHold(SemaphoreSlim state) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) state.Release();
        }
    }

    public bool? RowInstalled()
    {
        // Bounded: a miss returns WITHOUT releasing (releasing a semaphore this call never took
        // would raise its count and let two writers in — the VoiceActivityDetectionService
        // dispose-timeout shape).
        if (!_state.Wait(UiReaderStateWait))
        {
            Logger.Warning(
                "parakeet coordinator state busy after {TimeoutMs} ms - row presentation unknown, keeping the catalog-derived state",
                (int)UiReaderStateWait.TotalMilliseconds);
            return null;
        }
        try
        {
            return DeriveUnderState(forRead: true).Decision.RowInstalled;
        }
        finally
        {
            _state.Release();
        }
    }

    public bool? HasAnyArtifact()
    {
        if (!_state.Wait(UiReaderStateWait))
        {
            // The delete gate proceeds on the catalog resolution alone. Fail direction, with the
            // state named correctly (self-review lens B — an earlier draft said "the GGUF-only
            // state", which post-G6 cannot reach this reader at all: the GGUF IS the catalog row,
            // so its install resolves non-null and the caller never asks). The state that pays is
            // the MIGRATION WINDOW — sherpa installed, GGUF not yet — where the catalog resolves
            // nothing and the row presents installed off RowInstalled(). There a miss HIDES the
            // Delete affordance while the lock is held; the dominant holder IS a delete, so
            // refusing a second one is right, and a derive-path holder costs a transient miss the
            // next page visit corrects.
            Logger.Warning(
                "parakeet coordinator state busy after {TimeoutMs} ms - artifact presence unknown, delete gate falls back to the catalog resolution",
                (int)UiReaderStateWait.TotalMilliseconds);
            return null;
        }
        try
        {
            // Unknown probes count as "bytes may exist" — the delete must stay REACHABLE for
            // anything that might need deleting (the fail-open direction here would hide the
            // Delete action over live bytes). Honest bound (Kimi diff r2, challenge 5): this
            // keeps the delete reachable only where the ROW presents installed — a
            // VerifyFailed-and-GGUF-only state presents NOT-installed, so its Delete gate
            // ignores the row regardless of this answer; the recovery there is Download (the
            // sherpa catalog row installs, the row returns, its delete routes both). The
            // visible surface for that sub-state is the rollout PR's.
            return _downloads.TryGetInstalledLocation(_sherpaDescriptor) is not null
                   || ProbePresence(Path.Combine(_modelsDirectory, _sherpaDescriptor.Name)) != false
                   || ProbePresence(GgufBundleDir) != false
                   || _downloads.HasStagingFor(_gguf.Name);
        }
        finally
        {
            _state.Release();
        }
    }

    public string? SherpaBundleDir()
    {
        // One manager read against a readonly descriptor — no coordinator state is consulted, so
        // taking _state here would only add a lock-ordering surface for nothing. The caller
        // handles the null-after-ServeSherpa race (interleaved delete) as NotDownloaded.
        return _downloads.TryGetInstalledLocation(_sherpaDescriptor)?.Path;
    }

    /// <summary>The auto-cleanup's delete core (the button path's body until 2026-08-24, when
    /// the owner removed the button and dialog — <see cref="NotifyServed"/> is the sole caller
    /// now, from a background task). Returns true when the bundle was removed or was already
    /// gone; <paramref name="remainsDue"/> is true only when bytes remain AND the transition
    /// still wants them cleaned — the caller's retry-next-serve signal.</summary>
    internal bool TryDeleteLegacyCore(out bool remainsDue)
    {
        _state.Wait();
        try
        {
            // Re-derive UNDER the lock before deleting (self-review, concurrency lens): the
            // trigger fired on the decode path and a Task.Run hop sits between it and this
            // call — the GGUF can go VerifyFailed, the storm fuse can trip (making sherpa the
            // LIVE fallback the transition would serve), or a TryDeleteBoth can write the
            // tombstone, all before this task is scheduled. If cleanup is no longer due,
            // refuse — and report NOT-due, so the caller latches quiet instead of retrying
            // into the same refusal forever.
            if (!DeriveUnderState(forRead: true).Decision.CleanupLegacyDue)
            {
                Logger.Information("parakeet auto-cleanup: no longer due - skipping");
                remainsDue = false;
                return false;
            }

            // NO tombstone, deliberately: the tombstone means "the user wants Parakeet gone —
            // never auto-re-download", and this path runs precisely while the GGUF keeps
            // serving. The manager's per-model lock serializes against a concurrent install of
            // the same bundle. A file still held open (an AV scan, an Explorer preview) fails
            // the delete PARTIALLY — manifest gone, payload stranded
            // (InvalidatedButCleanupFailed) — which is exactly why the due-check keys on
            // SherpaBytesRemain rather than the installed flag: this reports remainsDue, and
            // the retry's DeleteModel treats the missing manifest as a no-op and finishes the
            // sweep once the handle is gone.
            //
            // The IN-PROCESS sherpa recognizer is NOT such a holder — MEASURED, 2026-08-24
            // (Kimi diff r1 asked for exactly this measurement): a live OfflineRecognizer
            // built on the real bundle permits FileShare.None exclusive opens on all four
            // model files, i.e. onnxruntime reads them at construction and closes — so
            // dictating on sherpa during the migration and then cleaning up deletes cleanly
            // even with the recognizer still cached, and no unload is needed here.
            var outcome = _downloads.DeleteModel(_sherpaDescriptor);
            if (outcome is ModelDeleteOutcome.Deleted or ModelDeleteOutcome.NotPresent)
            {
                Logger.Information(
                    "parakeet auto-cleanup: legacy sherpa bundle removed ({Outcome}) - ~670 MB freed",
                    outcome);
                remainsDue = false;
                return true;
            }
            Logger.Warning(
                "parakeet auto-cleanup: delete outcome {Outcome} - will retry after the next successful transcription",
                outcome);
            remainsDue = true;
            return false;
        }
        finally
        {
            _state.Release();
        }
    }

    public bool RetryRequiresAppRestart()
    {
        // Both latches are process-lifetime: the storm fuse never resets, and the whole-call
        // tripwire resets only on a successful serve — unreachable while it holds. With no
        // sherpa bundle to fall back to, a Retry replays the same dead end until app restart.
        var latched = _server.IsStormTripped
                      || Volatile.Read(ref _consecutiveWholeCallFailures) >= MaxConsecutiveWholeCallFailures;
        return latched && _downloads.TryGetInstalledLocation(_sherpaDescriptor) is null;
    }

    public bool TryDeleteBoth()
    {
        _state.Wait();
        try
        {
            // 1. Tombstone FIRST — durable, so a crash anywhere below leaves the delete
            //    resumable and the migration download refused.
            try
            {
                File.WriteAllText(TombstonePath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Error(ex, "parakeet delete: could not write the tombstone - refusing to start");
                return false;
            }

            // 2. Stop background work that would race the deletes: the migration download and
            //    the verification hash both hold the file open. The joins ABORT the delete on
            //    timeout rather than proceeding (self-review, concurrency F3): a migration that
            //    has not unwound still HOLDS the per-model lock, and proceeding parked the UI
            //    thread on that lock with no bound at all. Aborting is safe by construction —
            //    the tombstone is already durable, so the retry resumes.
            _migrationCts?.Cancel();
            _verifyCts?.Cancel();
            var joined = true;
            try { joined = _migration?.Wait(TimeSpan.FromSeconds(10)) ?? true; }
            catch { /* a faulted task is a completed task; its failure logged itself */ }
            if (joined)
            {
                // Short-circuited when the migration join already failed (Kimi diff r1): a
                // second 10 s wait on an already-doomed delete only lengthens the UI freeze.
                try { joined = _verification?.Wait(TimeSpan.FromSeconds(10)) ?? true; }
                catch { /* ditto */ }
            }
            if (!joined)
            {
                Logger.Warning("parakeet delete: background work did not stop in time - aborting; retry later");
                return false;
            }

            // 3a. TRN-49: the warm-up child ALSO holds the GGUF open during its window; a delete
            //     that only retires the resident child fails on the lock and confuses the user
            //     (self-review, three lenses independently). TRN-57: the quiesce is bounded, with
            //     zero grace and an explicit reason (never inferred from the zero); an Unconfirmed
            //     child still holds the file — abort like the other "retry later" branches, and
            //     the tombstone keeps the retry honest.
            var quiesce = _warmup.WaitForParakeetQuiesceAsync(TimeSpan.Zero, GpuWarmupCancelReason.ModelDelete)
                .GetAwaiter().GetResult();
            if (quiesce == ParakeetQuiesceOutcome.Unconfirmed)
            {
                Logger.Warning("parakeet delete: the GPU warm-up child's exit is unconfirmed - aborting; retry later");
                return false;
            }

            // 3. Retire the resident server — it holds the GGUF open. CONFIRM-OR-PARK through
            //    the bounded mid-session API, never ShutdownAsync (self-review, state F4: its
            //    unconditional dispose assumes app exit, where no successor can spawn; here one
            //    can, and an unconfirmed kill must park in _retiring so the next acquire
            //    refuses). False = gate busy (an acquire's health wait) or kill unconfirmed —
            //    abort; the file may still be open and the tombstone makes the retry honest.
            if (!_server.TryRetireResidentAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult())
            {
                Logger.Warning("parakeet delete: could not retire the resident server - aborting; retry later");
                return false;
            }

            // 4. The artifacts, GGUF first (the delete the tombstone chiefly guards), staging
            //    swept through the manager's containment-safe API.
            var allGone = true;
            try
            {
                _downloads.CleanupStagingFor(_gguf.Name);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "parakeet delete: staging sweep failed");
                allGone = false;
            }

            var ggufOutcome = _downloads.DeleteModel(_gguf);
            if (ggufOutcome is not (ModelDeleteOutcome.Deleted or ModelDeleteOutcome.NotPresent))
            {
                Logger.Warning("parakeet delete: gguf delete outcome {Outcome}", ggufOutcome);
                allGone = false;
            }

            var sherpaOutcome = _downloads.DeleteModel(_sherpaDescriptor);
            if (sherpaOutcome is not (ModelDeleteOutcome.Deleted or ModelDeleteOutcome.NotPresent))
            {
                Logger.Warning("parakeet delete: legacy delete outcome {Outcome}", sherpaOutcome);
                allGone = false;
            }

            // 5. The tombstone clears ONLY on full completion — partial failure keeps it, so
            //    the migration download cannot re-fetch bytes the user asked to remove
            //    (challenge 4's re-download trap). The user's retry, or the lazy self-heal in
            //    derivation, is what clears it later. "Complete" is judged on the BYTES (the
            //    same rule as the self-heal): an InvalidatedButCleanupFailed leaves the model
            //    uninstalled with its payload still on disk, and that is NOT a finished delete.
            allGone = allGone
                      && ProbePresence(Path.Combine(_modelsDirectory, _sherpaDescriptor.Name)) == false
                      && ProbePresence(GgufBundleDir) == false;
            if (allGone)
            {
                // The seam's contract: true only when both artifacts are gone AND the
                // tombstone cleared. A failed clear reports INCOMPLETE (Codex diff r2) — the
                // row self-corrects via RowInstalled() on the next refresh, and the retry (or
                // either self-heal) finishes the clear when the marker becomes deletable.
                allGone = TryClearTombstone();
            }
            if (allGone)
            {
                lock (_ledgerLock)
                {
                    _verifiedByIdentity.Clear();
                }
                _servedIdentity = null;
                _lastLeaseIdentity = null;
            }
            return allGone;
        }
        finally
        {
            _state.Release();
        }
    }

    /// <summary>True = the marker is CONFIRMED absent (deleted now, or never there —
    /// <c>File.Delete</c> returns silently on a missing file and throws on a real failure).
    /// False = it persists, and every caller must keep reporting DeletePending/incomplete
    /// (Codex diff r2 Blocker: swallowing this let a delete report success with the durable
    /// marker still on disk, permanently killing the migration with no honest signal).</summary>
    private bool TryClearTombstone()
    {
        try
        {
            File.Delete(TombstonePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept tombstone = kept refusal to auto-download; safe direction, retried later.
            Logger.Warning(ex, "parakeet delete: tombstone could not be cleared yet");
            return false;
        }
    }

    private bool? TryGetVerdict(string identity)
    {
        lock (_ledgerLock)
        {
            return _verifiedByIdentity.TryGetValue(identity, out var passed) ? passed : null;
        }
    }

    /// <summary>
    /// TRN-39: how long APP EXIT may wait for <see cref="_state"/> before giving up on the
    /// capture below. <c>App.Cleanup</c> runs <see cref="ShutdownAsync"/> as
    /// <c>.GetAwaiter().GetResult()</c> from the <c>Closed</c> handler — the UI thread — and this
    /// lock's long holders are the deletes (<see cref="TryDeleteBoth"/>'s ~20 s worst case) and
    /// the auto-cleanup's 670 MB sweep, so an unbounded wait parked the exiting process for that
    /// whole window with the window already gone.
    ///
    /// <para>Same 250 ms as <see cref="UiReaderStateWait"/> and for the same reason — it rides out
    /// the ordinary holder while bounding the worst case to a hitch — but a SEPARATE field on
    /// purpose: that one is a reader's test seam which rows shorten to 40 ms, and borrowing it
    /// would make a reader-contention test silently redefine what app exit waits for.</para>
    ///
    /// <para><b>What the bound GIVES UP, said out loud (self-review, concurrency lens).</b> Both
    /// long holders run on the POOL — <see cref="TryDeleteBoth"/> off the Models page's
    /// <c>Task.Run</c>, the auto-cleanup off <c>NotifyServed</c>'s — so the unbounded wait was in
    /// effect a JOIN: exit parked until the delete finished. Timing out means the process can now
    /// exit mid-sweep and strand the payload with its manifest already gone (the
    /// <c>InvalidatedButCleanupFailed</c> shape). That is acceptable because every end state it
    /// can produce is one the design ALREADY converges from rather than a new hazard — the
    /// tombstone is written first and durably and clears only on confirmed-gone bytes, and the
    /// due-check keys on <c>SherpaBytesRemain</c>, so the next successful transcription retries
    /// and finishes it. A quit that hangs is a defect; a quit that defers 670 MB of housekeeping
    /// to the next launch is the design working.</para>
    /// </summary>
    internal TimeSpan ShutdownStateWait = TimeSpan.FromMilliseconds(250);

    public async Task ShutdownAsync()
    {
        // The CTS fields are replaced (old one DISPOSED) under _state by the background-work
        // starters, so a bare read here races app exit against a preparation: Cancel() on the
        // disposed instance throws ObjectDisposedException and — because the server kill is the
        // LAST statement — silently skips the graceful kill, leaving the child to the job
        // object (self-review F2). Capture under _state; the residual race (a replacement
        // landing after capture) is swallowed per-cancel, and the process is exiting anyway.
        //
        // TRN-39: the capture is BOUNDED, and on a miss we skip the CAPTURE, never the kill.
        // Cancelling the background work is a courtesy to tasks the process is about to end
        // anyway; _server.ShutdownAsync() is the graceful child kill and is what must always
        // run — which is the same "the server kill is the LAST statement" reasoning F2 above
        // records, applied to a timeout instead of an exception.
        CancellationTokenSource? migration = null, verify = null;
        if (await _state.WaitAsync(ShutdownStateWait).ConfigureAwait(false))
        {
            // Taken: release in the finally. A MISS must not reach it, and the guard is worth
            // MORE than the RowInstalled shape it borrows: _state is new(1, 1), so releasing a
            // semaphore this call never took either raises the count and lets two writers in
            // (while a holder is still inside) or, once that holder has released, throws
            // SemaphoreFullException — which App.Cleanup's catch(Exception) swallows, skipping
            // _server.ShutdownAsync() below. That is the F2 failure above arriving by a second
            // route: the graceful child kill silently not happening.
            try
            {
                migration = _migrationCts;
                verify = _verifyCts;
            }
            finally
            {
                _state.Release();
            }
        }
        else
        {
            Logger.Warning(
                "parakeet coordinator state busy after {TimeoutMs} ms at shutdown - skipping background-work cancellation, proceeding to the child kill",
                (int)ShutdownStateWait.TotalMilliseconds);
        }
        try { migration?.Cancel(); } catch (ObjectDisposedException) { }
        try { verify?.Cancel(); } catch (ObjectDisposedException) { }
        await _server.ShutdownAsync().ConfigureAwait(false);
    }
}
