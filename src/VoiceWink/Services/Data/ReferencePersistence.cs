using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Services.Data;

/// <summary>
/// Outcome of <see cref="ReferencePersistence.PersistWithRowAsync"/>: the history id the
/// row write produced (null = not committed / history disabled / write failed), the
/// selections the redo context should re-arm with — per item, the row's AppReferences
/// copy when ownership is CONFIRMED, otherwise the caller's original in-memory
/// selection (ENH-6f made this plural; selection order preserved) — and, when copies
/// were retained, ONE composite LIVE fresh-copy claim protecting ALL of them from
/// deletion until the caller has armed the re-arm selections (ENH-6e). The caller MUST
/// dispose the claim in a <c>finally</c> AFTER any <c>ArmRedo</c>; disposal is
/// idempotent, removes every claim atomically, and schedules ONE batch release-time
/// cleanup, which retains each file while the armed slot or a row still references it.
/// </summary>
public sealed record ReferencePersistOutcome(
    int? HistoryId,
    IReadOnlyList<Models.ReferenceImageSelection>? RearmSelections,
    IDisposable? FreshCopyClaim = null);

/// <summary>
/// ENH-6b: the SINGLE owner of reference-image retention policy — the per-row copy
/// (successful runs, and since ENH-6c failed runs whose reference is still usable),
/// commit reconciliation, deletion, and the orphan sweep. One instance (DI singleton,
/// production root = <c>AppPaths.EnsureReferences()</c>); injected into
/// <c>TranscriptionHistoryService</c> so row deletion and this class can never apply
/// divergent containment or remaining-reference rules. Tests construct it with a temp
/// root + <c>TestDbContextFactory</c> and exercise the PUBLIC surface end-to-end.
///
/// <para>Design invariants (plan rounds 1–3, 2026-07-11/12):</para>
/// <list type="bullet">
/// <item>Copies are written ONLY here, ONLY for history-enabled runs — successful
/// ones from the already-validated in-memory bytes (never by re-reading the user's
/// original file: TOCTOU), and FAILED ones (ENH-6c — retry keeps the reference) from
/// a fresh read through the same validation gate, since the run's own bytes die with
/// the exception. Never inside <c>AIEnhancementService</c> (a history-disabled user
/// must retain nothing). ENH-6g: copies are CONTENT-ADDRESSED (per-install keyed
/// hash names) and SHARED — identical bytes land once; an in-root AppReferences
/// selection is reused with zero IO (the redo/Regenerate chain); a file deletes only
/// when its LAST referencing row and LAST claim are gone.</item>
/// <item>Copy failure is FAIL-SOFT: the generation still writes its row (without a
/// reference path) and the pipeline never sees an exception; logging is path-free.</item>
/// <item>A null/failed row write does NOT prove the row wasn't committed (a throwing
/// HistoryChanged subscriber used to convert committed saves into nulls) — ownership
/// is reconciled BY QUERY, and ambiguity always RETAINS the copy (sweep-eligible)
/// rather than risking deletion of a committed row's file.</item>
/// <item>Deletion is permanent (<c>File.Delete</c>, never the Recycle Bin), gated on
/// MediaPathPolicy containment against the references root, and only executes when no
/// REMAINING row references the path — compared on canonical full paths
/// case-insensitively, because SQLite string equality is not Windows path identity
/// (duplicate/corrupt pointers must not cause premature deletion).</item>
/// <item>Cancellation boundary (Codex post-#167 audit): the failed-retention source
/// read and the copy's temp-file write honor the caller's token and RETHROW a matching
/// OperationCanceledException (never mapped to a fail-soft result — a cancelled
/// pipeline must exit through its cancel path); the final <c>File.Move</c> and
/// ownership reconciliation run to completion with no token, so a cancel can never
/// strand a committed row without its copy or orphan a moved copy unreconciled.</item>
/// <item>Live claims (ENH-6e, plan round 4): rows are NOT the only referrers — an
/// armed redo context, an in-flight regeneration, and a just-persisted copy all hold
/// LIVE CLAIMS in a registry guarded by <c>_liveGate</c>. Deletion (row-delete path
/// AND sweep) performs its live check and the <c>File.Delete</c> under that same lock,
/// so claim acquisition can never interleave with a delete decision. When a claim is
/// released (context replaced/cleared, lease disposed), a release-time cleanup
/// re-checks rows + claims and removes the file only when nothing references it; a
/// cleanup interrupted by shutdown is retried by the startup sweep.</item>
/// <item>GDPR erasure quiesce (ENH-6e): every SQLite-touching operation here is
/// admission-gated — the quiesced check and the in-flight-op count increment happen
/// atomically under <c>_liveGate</c> — so <c>DataErasureService</c> can quiesce this
/// class (bounded drain) before deleting the database; a late background cleanup could
/// otherwise hold or even RECREATE <c>voicewink.db</c> after the allowlist pass. A
/// quiesce timeout aborts the erasure (never proceed-and-hope).</item>
/// </list>
/// </summary>
public sealed class ReferencePersistence
{
    private static ILogger Logger => Log.ForContext<ReferencePersistence>();

    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly string _referencesDir;

    // ENH-6e live-claims registry. ONE lock guards the claim slots, the quiesce flag,
    // and the in-flight DB-operation count; deletion holds it across check+delete.
    private readonly object _liveGate = new();
    private readonly List<string> _armedLivePaths = new();    // canonical; the armed redo context's references (ENH-6f: plural)
    private readonly List<string> _inFlightLivePaths = new(); // canonical; pipeline + fresh-copy leases
    private bool _quiesced;
    private int _activeDbOps;
    private TaskCompletionSource<bool>? _drainSignal;

    // Async-write test seam (mirrors the SettingsService file-op seams): the
    // deterministic mid-write cancellation test injects partial-write-then-OCE here.
    internal Func<string, byte[], CancellationToken, Task> _writeAllBytesAsync =
        static (path, bytes, ct) => File.WriteAllBytesAsync(path, bytes, ct);

    // Release-cleanup seam: production fire-and-forgets (a released claim's cleanup
    // must never block the UI-thread transition that released it); tests run it inline
    // for deterministic assertions. A cleanup abandoned at process exit is reaped by
    // the startup sweep. BATCH-shaped since ENH-6f: one release (armed swap, composite
    // lease dispose) schedules ONE cleanup pass over all its paths — one DB snapshot,
    // not one scan per path (Codex plan round 2).
    internal Action<IReadOnlyList<string>> _scheduleReleasedCleanup;

    // Barrier hook for the deterministic delete-vs-claim race test: invoked between
    // the row query and taking _liveGate in TryDeleteIfUnreferencedAsync. Assigned
    // only from the test assembly (InternalsVisibleTo).
    internal Action? _onBeforeLiveGateForTests = null;

    // ENH-6g: per-install HMAC key for content-addressed copy names — resolved ONCE
    // for the singleton, lazily, under its own lock (process-atomic get-or-create:
    // concurrent first use must not mint two keys, or the two-writer dedup guarantee
    // dies). Production wires the sensitive-classified settings value via
    // Helpers.ReferenceDedupKey; the null default (legacy tests) mints an ephemeral
    // per-instance key, which only means no dedup across instances.
    private readonly Func<byte[]> _dedupKeyProvider;
    private readonly object _dedupKeyLock = new();
    private byte[]? _dedupKey;

    // Existence-probe seam (ENH-6g): the claim-ordering race test parks HERE — i.e.
    // provably AFTER the speculative claim was taken — and runs a delete during the
    // pause, pinning that the claim (not luck) protects the reuse decision.
    internal Func<string, bool> _probeFinalPathExists = File.Exists;

    public ReferencePersistence(
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        string referencesDir,
        Func<byte[]>? dedupKeyProvider = null)
    {
        _dbFactory = dbFactory;
        _referencesDir = referencesDir;
        _dedupKeyProvider = dedupKeyProvider
            ?? (() => global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(Helpers.ReferenceDedupKey.KeyBytes));
        _scheduleReleasedCleanup = paths => _ = Task.Run(() => TryCleanupReleasedReferencesAsync(paths));
    }

    private byte[] DedupKey
    {
        get
        {
            lock (_dedupKeyLock)
            {
                return _dedupKey ??= _dedupKeyProvider();
            }
        }
    }

    // ── ENH-6e live-claims surface ─────────────────────────────────────────────

    /// <summary>Single-reference wrapper over <see cref="SetArmedLiveReferences"/>.</summary>
    public void SetArmedLiveReference(string? path)
        => SetArmedLiveReferences(path == null ? null : new[] { path });

    /// <summary>
    /// Replace the ARMED live claims — the references of the currently armed redo
    /// context (or the context parked in the open options dialog). Callers claim
    /// BEFORE publishing the context (claim-before-publish) so a concurrent history
    /// delete can never observe the context without its claims. Null/empty clears
    /// the slot. The swap is atomic under <c>_liveGate</c>; displaced paths (the set
    /// difference) get ONE batch release-time cleanup.
    /// </summary>
    public void SetArmedLiveReferences(IReadOnlyList<string?>? paths)
    {
        var canonicals = CanonicalizeAll(paths);
        List<string>? displaced = null;
        lock (_liveGate)
        {
            foreach (var old in _armedLivePaths)
            {
                if (!canonicals.Contains(old, StringComparer.OrdinalIgnoreCase))
                    (displaced ??= new List<string>()).Add(old);
            }
            _armedLivePaths.Clear();
            _armedLivePaths.AddRange(canonicals);
        }
        // Outside the lock — the seam may run the cleanup inline (tests).
        if (displaced != null)
            _scheduleReleasedCleanup(displaced);
    }

    /// <summary>Single-reference wrapper over <see cref="BeginInFlightLiveReferences"/>.</summary>
    public IDisposable BeginInFlightLiveReference(string? path)
        => BeginInFlightLiveReferences(path == null ? null : new[] { path });

    /// <summary>
    /// Take ONE composite IN-FLIGHT live claim on <paramref name="paths"/> for the
    /// duration of a pipeline run (or, inside <see cref="PersistWithRowAsync"/>, for
    /// just-written copies until the caller arms them). Null/blank/uncanonicalizable
    /// entries are skipped; an effectively-empty set returns a no-op lease. All paths
    /// are claimed atomically; dispose is idempotent, releases them atomically, and
    /// schedules ONE batch release-time cleanup.
    /// </summary>
    public IDisposable BeginInFlightLiveReferences(IReadOnlyList<string?>? paths)
    {
        var canonicals = CanonicalizeAll(paths);
        if (canonicals.Count == 0)
            return NoopLease.Instance;
        return AddInFlightLease(canonicals);
    }

    private static IReadOnlyList<string> CanonicalizeAll(IReadOnlyList<string?>? paths)
    {
        if (paths == null || paths.Count == 0)
            return Array.Empty<string>();
        var canonicals = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var canonical = string.IsNullOrWhiteSpace(path) ? null : TryCanonicalize(path);
            if (!string.IsNullOrWhiteSpace(canonical))
                canonicals.Add(canonical);
        }
        return canonicals;
    }

    private LiveReferenceLease AddInFlightLease(IReadOnlyList<string> canonicals)
    {
        lock (_liveGate)
        {
            _inFlightLivePaths.AddRange(canonicals);
        }
        return new LiveReferenceLease(this, canonicals);
    }

    /// <summary>Single-path wrapper over <see cref="TryCleanupReleasedReferencesAsync"/>.</summary>
    public Task TryCleanupReleasedReferenceAsync(string? path)
        => TryCleanupReleasedReferencesAsync(path == null ? Array.Empty<string>() : new[] { path });

    /// <summary>
    /// Batch release-time cleanup: delete each of <paramref name="paths"/> if nothing
    /// references it anymore — ONE DB snapshot for the whole batch. Silent no-op for
    /// paths outside the references root — release sites also pass
    /// UserPicked/AppImages selections, which are not this class's to manage (and must
    /// not produce containment warnings). Never throws (fire-and-forget safe).
    /// </summary>
    public async Task TryCleanupReleasedReferencesAsync(IReadOnlyList<string?> paths)
    {
        try
        {
            List<string>? contained = null;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                if (!Helpers.MediaPathPolicy.TryResolveWithin(path, _referencesDir, out _))
                    continue;
                (contained ??= new List<string>()).Add(path);
            }
            if (contained != null)
                await TryDeleteIfUnreferencedAsync(contained).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning("Released-reference cleanup failed: {ErrorType} — sweep backstop applies", ex.GetType().Name);
        }
    }

    /// <summary>
    /// GDPR-erasure quiesce: atomically stop admitting new SQLite-touching operations
    /// (delete checks, release cleanups, sweep runs — they become no-ops) and wait,
    /// bounded by <paramref name="timeout"/>, for the already-admitted ones to drain.
    /// Returns false on timeout — the caller must ABORT the erasure (and call
    /// <see cref="ResumeCleanup"/>): proceeding could let a parked operation recreate
    /// the just-deleted database and turn a reported success into a lie.
    /// </summary>
    public async Task<bool> QuiesceAsync(TimeSpan timeout)
    {
        Task drained;
        lock (_liveGate)
        {
            _quiesced = true;
            if (_activeDbOps == 0)
                return true;
            // Always a FRESH signal: a stale completed one (from an earlier timed-out
            // quiesce) would report a false drain while operations are still active.
            _drainSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drainSignal.Task;
        }
        var winner = await Task.WhenAny(drained, Task.Delay(timeout)).ConfigureAwait(false);
        if (winner == drained)
            return true;
        Logger.Warning("Reference-cleanup quiesce timed out — erasure must abort");
        return false;
    }

    /// <summary>Undo a quiesce after an ABORTED erasure so the session cleans normally again.</summary>
    public void ResumeCleanup()
    {
        lock (_liveGate)
        {
            _quiesced = false;
            _drainSignal = null;
        }
    }

    /// <summary>Quiesce-gated admission: check + count atomically (caller must pair with <see cref="ExitDbOp"/>).</summary>
    private bool TryEnterDbOp()
    {
        lock (_liveGate)
        {
            if (_quiesced)
                return false;
            _activeDbOps++;
            return true;
        }
    }

    private void ExitDbOp()
    {
        TaskCompletionSource<bool>? drain = null;
        lock (_liveGate)
        {
            _activeDbOps--;
            if (_activeDbOps == 0)
                drain = _drainSignal;
        }
        drain?.TrySetResult(true);
    }

    /// <summary>Armed / in-flight membership. Caller holds <c>_liveGate</c>.</summary>
    private bool IsLiveLocked(string canonicalPath) =>
        _armedLivePaths.Contains(canonicalPath, StringComparer.OrdinalIgnoreCase)
        || _inFlightLivePaths.Contains(canonicalPath, StringComparer.OrdinalIgnoreCase);

    internal sealed class LiveReferenceLease : IDisposable
    {
        private readonly ReferencePersistence _owner;
        private readonly IReadOnlyList<string> _canonicalPaths;
        private int _released;

        internal LiveReferenceLease(ReferencePersistence owner, IReadOnlyList<string> canonicalPaths)
        {
            _owner = owner;
            _canonicalPaths = canonicalPaths;
        }

        public void Dispose() => Release(scheduleCleanup: true);

        /// <summary>
        /// Idempotent. All paths release under ONE lock acquisition (a delete check
        /// can never observe a half-released composite), then — when
        /// <paramref name="scheduleCleanup"/> — ONE batch cleanup covers the whole
        /// set. <paramref name="scheduleCleanup"/> = false is for
        /// <see cref="PersistWithRowAsync"/>'s internal exits, which settle the
        /// copies themselves (inline, claim-respecting) instead of scheduling.
        /// </summary>
        internal void Release(bool scheduleCleanup)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_owner._liveGate)
            {
                foreach (var path in _canonicalPaths)
                    _owner._inFlightLivePaths.Remove(path); // one occurrence — refcount semantics for duplicates
            }
            if (scheduleCleanup && _canonicalPaths.Count > 0)
                _owner._scheduleReleasedCleanup(_canonicalPaths);
        }
    }

    private sealed class NoopLease : IDisposable
    {
        internal static readonly NoopLease Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// The coordinator: copies → row write → reconciliation → re-arm selections, in one
    /// operation so the orchestration policy is testable without constructing
    /// MainViewModel. <paramref name="usedReferences"/> pairs each run reference's
    /// validated bytes with the caller's original selection EXPLICITLY (index pairing
    /// was implicit pre-ENH-6f), in selection order. <paramref name="writeRow"/>
    /// performs the caller's history write with the ENCODED copy-path value plugged in
    /// (<see cref="Helpers.ReferencePathList"/>; null when nothing is retained) and
    /// returns the committed row id or null. This method never throws into the
    /// caller's pipeline — EXCEPT a matching cancellation, which rethrows after
    /// reconciliation (the cancellation-boundary invariant above): retention
    /// bookkeeping must not fail a generation that already succeeded, nor add a second
    /// error to one that already failed (ENH-6c failed-row retention).
    /// Per-item fail-soft: a failed copy drops that item's path from the row value but
    /// keeps its ORIGINAL selection in the re-arm list.
    /// </summary>
    public async Task<ReferencePersistOutcome> PersistWithRowAsync(
        IReadOnlyList<(ReferenceImage Bytes, Models.ReferenceImageSelection Original)>? usedReferences,
        bool historyEnabled,
        Func<string?, Task<int?>> writeRow,
        CancellationToken ct = default)
    {
        // No references on the run, or history off (nothing may be retained): plain row
        // write, keep whatever in-memory selections the caller already had.
        if (usedReferences == null || usedReferences.Count == 0 || !historyEnabled)
        {
            var passthrough = usedReferences is { Count: > 0 }
                ? usedReferences.Select(i => i.Original).ToArray()
                : null;
            return new ReferencePersistOutcome(await SafeWriteRowAsync(writeRow, null, ct).ConfigureAwait(false), passthrough);
        }

        // Sequential landings, CLAIM-AS-YOU-LAND (ENH-6f plan round 2): each landed
        // path is claimed before the next item starts, so a cancel mid-loop finds
        // every earlier one protected and settles them deterministically. A per-item
        // failure is fail-soft (null slot — the item keeps its original selection).
        // ENH-6g: an item lands as (a) CHAIN REUSE — an AppReferences selection
        // already inside our root (the redo/Regenerate chain: validated through the
        // kernel gate at read time this run, pipeline-leased) records the same path
        // with zero IO; or (b) LandOrReuseCopyAsync — the keyed content-addressed
        // write that dedups identical bytes across sources.
        var copyPaths = new string?[usedReferences.Count];
        var landedClaims = new List<LiveReferenceLease>(usedReferences.Count);
        var landedPaths = new List<string>(usedReferences.Count);
        try
        {
            for (var i = 0; i < usedReferences.Count; i++)
            {
                // Before EVERY item, including the no-IO reuse branch — a fully
                // reused list must not reach writeRow pre-cancelled (Codex plan r2).
                ct.ThrowIfCancellationRequested();

                var (bytes, original) = usedReferences[i];
                if (original.Origin == Models.ReferenceImageOrigin.AppReferences
                    && Helpers.MediaPathPolicy.TryResolveWithin(original.Path, _referencesDir, out var reusablePath))
                {
                    copyPaths[i] = reusablePath;
                    landedClaims.Add(AddInFlightLease(new[] { TryCanonicalize(reusablePath) ?? reusablePath }));
                    landedPaths.Add(reusablePath);
                    continue;
                }

                var landed = await LandOrReuseCopyAsync(bytes, ct).ConfigureAwait(false);
                if (landed == null)
                    continue;
                copyPaths[i] = landed.Value.Path;
                landedClaims.Add(landed.Value.Claim);
                landedPaths.Add(landed.Value.Path);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paths landed before the cancel were never row-written — settle them
            // (release without scheduling + claim-respecting batch delete, untokened;
            // a REUSED path is retained by its rows/outer lease), THEN surface the
            // cancel (round-2 amendment: exact sequencing).
            await SettleLandedCopiesAsync(landedClaims, landedPaths).ConfigureAwait(false);
            throw;
        }

        if (landedPaths.Count == 0)
        {
            // Fail-soft: every copy failed but the run's outcome stands — the row is
            // still written, just without reference paths.
            return new ReferencePersistOutcome(
                await SafeWriteRowAsync(writeRow, null, ct).ConfigureAwait(false),
                usedReferences.Select(i => i.Original).ToArray());
        }

        var encoded = Helpers.ReferencePathList.Encode(landedPaths);

        int? id;
        try
        {
            id = await writeRow(encoded).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row state is AMBIGUOUS (the cancel may have landed after the commit):
            // reconcile ownership TO COMPLETION — no token, ONE query snapshot for all
            // copies — then surface the cancel. Mapping the OCE to a normal outcome
            // would let a cancelled pipeline continue, arm redo, or present success
            // (Codex final check R2). The claims are settled here (the caller never
            // sees an outcome to dispose); the batch delete retains committed copies
            // and treats a failed query as retain-all.
            await SettleLandedCopiesAsync(landedClaims, landedPaths).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("History write threw during reference persist: {ErrorType} — reconciling copy ownership", ex.GetType().Name);
            id = null;
        }

        if (id != null)
            return new ReferencePersistOutcome(id, BuildRearmSelections(usedReferences, copyPaths),
                ConsolidateFreshClaims(landedClaims, landedPaths));

        // Null does not prove "not committed" — reconcile by ONE query snapshot.
        // Ambiguity retains the copies: the startup sweep reaps genuinely orphaned
        // files later, whereas a wrongly deleted file breaks a committed row's
        // Regenerate forever.
        var referenced = await QueryReferencedCanonicalSetCountedAsync(landedPaths).ConfigureAwait(false);
        if (referenced == null)
        {
            ReleaseAllWithoutScheduling(landedClaims);
            Logger.Warning("Reference ownership reconciliation query failed — retaining copies for the startup sweep");
            return new ReferencePersistOutcome(null, usedReferences.Select(i => i.Original).ToArray());
        }

        // ENH-6g PER-ITEM reconciliation (Codex plan r2 blocker): under dedup a landed
        // path may be PRE-referenced by an OLDER row, so "any hit ⇒ our row committed"
        // (the pre-6g rule, valid for fresh GUIDs) is wrong. The encoded value commits
        // atomically, so a landed path MISSING from the set PROVES our value did not
        // commit — settle that item; a path PRESENT is row-protected regardless of
        // whose row owns it — retain it and re-arm its item as the AppReferences copy.
        var rearm = new Models.ReferenceImageSelection[usedReferences.Count];
        var settleClaims = new List<LiveReferenceLease>();
        var settlePaths = new List<string>();
        var retainedClaims = new List<LiveReferenceLease>();
        var retainedPaths = new List<string>();
        var claimIndex = 0;
        for (var i = 0; i < usedReferences.Count; i++)
        {
            if (copyPaths[i] is not { } landedPath)
            {
                rearm[i] = usedReferences[i].Original;
                continue;
            }
            var claim = landedClaims[claimIndex++];
            var canonical = TryCanonicalize(landedPath);
            if (canonical != null && referenced.Contains(canonical))
            {
                rearm[i] = new Models.ReferenceImageSelection(landedPath, Models.ReferenceImageOrigin.AppReferences);
                retainedClaims.Add(claim);
                retainedPaths.Add(landedPath);
            }
            else
            {
                rearm[i] = usedReferences[i].Original;
                settleClaims.Add(claim);
                settlePaths.Add(landedPath);
            }
        }
        if (settlePaths.Count > 0)
            await SettleLandedCopiesAsync(settleClaims, settlePaths).ConfigureAwait(false);
        return retainedPaths.Count == 0
            ? new ReferencePersistOutcome(null, rearm)
            : new ReferencePersistOutcome(null, rearm, ConsolidateFreshClaims(retainedClaims, retainedPaths));
    }

    /// <summary>
    /// Per-item re-arm mapping (ENH-6f): items whose copy landed upgrade to the row's
    /// AppReferences copy; items whose copy failed keep the caller's original
    /// selection. Selection order preserved.
    /// </summary>
    private static IReadOnlyList<Models.ReferenceImageSelection> BuildRearmSelections(
        IReadOnlyList<(ReferenceImage Bytes, Models.ReferenceImageSelection Original)> usedReferences,
        string?[] copyPaths)
    {
        var rearm = new Models.ReferenceImageSelection[usedReferences.Count];
        for (var i = 0; i < usedReferences.Count; i++)
        {
            rearm[i] = copyPaths[i] is { } copyPath
                ? new Models.ReferenceImageSelection(copyPath, Models.ReferenceImageOrigin.AppReferences)
                : usedReferences[i].Original;
        }
        return rearm;
    }

    /// <summary>
    /// Consolidate the per-copy claims taken during the copy loop into ONE composite
    /// fresh-copy claim for the caller (ENH-6f): the composite claims all paths FIRST
    /// (no path is ever unclaimed in between), then the per-copy claims release
    /// without scheduling. The composite's dispose releases atomically and schedules
    /// ONE batch cleanup.
    /// </summary>
    private IDisposable ConsolidateFreshClaims(List<LiveReferenceLease> landedClaims, List<string> landedPaths)
    {
        var composite = AddInFlightLease(CanonicalizeAll(landedPaths));
        ReleaseAllWithoutScheduling(landedClaims);
        return composite;
    }

    private static void ReleaseAllWithoutScheduling(List<LiveReferenceLease> claims)
    {
        foreach (var claim in claims)
            claim.Release(scheduleCleanup: false);
    }

    /// <summary>
    /// Settle fresh copies that NO committed row is known to own: release the
    /// persistence-owned claims without scheduling, then push the whole set through
    /// the claim-respecting batch delete (query-only reconciliation, plan round 3: a
    /// copy may have escaped via a briefly-committed row and been claimed by another
    /// context, and direct deletion would ignore that claim; a committed copy is
    /// retained by the query, a failed query retains everything for the sweep).
    /// Untokened — reconciliation of possibly committed work runs to completion even
    /// on a cancelled pipeline.
    /// </summary>
    private async Task SettleLandedCopiesAsync(List<LiveReferenceLease> claims, IReadOnlyList<string> copyPaths)
    {
        ReleaseAllWithoutScheduling(claims);
        if (copyPaths.Count > 0)
            await TryDeleteIfUnreferencedAsync(copyPaths).ConfigureAwait(false);
    }

    /// <summary>
    /// ENH-6c: the ONE production path for FAILED-attempt retention — both
    /// MainViewModel failure routes (main-pipeline fallthrough + redo catch) call
    /// this, so the composition tests that drive it exercise the real orchestration.
    /// The failed run's own validated bytes died with its exception, so the (already
    /// sanitized) selection is re-read through the SAME validation gate the
    /// generation uses. Decision rules (Codex ENH-6c R1/R3):
    /// <list type="bullet">
    /// <item>History OFF: no read at all (retention is impossible — a multi-MB read
    /// would be waste, and the failure may predate any reference IO); the in-memory
    /// selection is KEPT so the in-session re-arm still carries it.</item>
    /// <item>History ON + read succeeds: bytes + selection flow into
    /// <see cref="PersistWithRowAsync"/> — the row carries the copy path, the re-arm
    /// upgrades to the AppReferences copy on confirmed ownership.</item>
    /// <item>History ON + read fails: the file is unusable NOW — neither the row nor
    /// the re-arm may carry the dead path (the ENH-6 sanitation invariant).</item>
    /// </list>
    /// Never throws — except a matching cancellation, which rethrows (cancellation
    /// boundary): retention must not add a second error to a failed pipeline.
    /// AppImages selections validate against the real images root; AppReferences
    /// selections validate against THIS instance's references root (production: the
    /// same folder AppPaths pins; tests: the temp root).
    /// </summary>
    public async Task<ReferencePersistOutcome> PersistFailedAttemptWithRowAsync(
        IReadOnlyList<Models.ReferenceImageSelection>? sanitizedSelections,
        bool historyEnabled,
        Func<string?, Task<int?>> writeRow,
        CancellationToken ct = default)
    {
        if (sanitizedSelections == null || sanitizedSelections.Count == 0)
            return new ReferencePersistOutcome(await SafeWriteRowAsync(writeRow, null, ct).ConfigureAwait(false), null);
        if (!historyEnabled)
        {
            // No read at all (retention is impossible; the failure may predate any
            // reference IO) — the in-memory selections are KEPT for the in-session re-arm.
            return new ReferencePersistOutcome(
                await SafeWriteRowAsync(writeRow, null, ct).ConfigureAwait(false),
                sanitizedSelections.ToArray());
        }

        var retained = await PrepareFailedRetentionAsync(sanitizedSelections, ct).ConfigureAwait(false);
        if (retained.Count == 0)
        {
            // Every re-read failed: neither the row nor the re-arm may carry dead
            // paths (the ENH-6 sanitation invariant, applied per item).
            return new ReferencePersistOutcome(await SafeWriteRowAsync(writeRow, null, ct).ConfigureAwait(false), null);
        }
        return await PersistWithRowAsync(retained, historyEnabled: true, writeRow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The failed-attempt retention decision — see
    /// <see cref="PersistFailedAttemptWithRowAsync"/>. GREEDY per item (ENH-6f round-2
    /// amendment): each selection re-reads through the same validation gate with the
    /// running aggregate budget; an item whose read fails — including an over-budget
    /// item, possible only if a file GREW since generation — is dropped individually
    /// (from row and re-arm) while the remaining budget carries to the next item.
    /// Retention must not discard five good references over one grown file.
    /// </summary>
    // totalBudget / transformedBudget are TEST SEAMS defaulting to the real aggregate
    // bound (Codex diff r1) — only a sub-real budget can exercise the running
    // subtractions against small fixture files.
    internal async Task<IReadOnlyList<(ReferenceImage Bytes, Models.ReferenceImageSelection Original)>> PrepareFailedRetentionAsync(
        IReadOnlyList<Models.ReferenceImageSelection> sanitizedSelections, CancellationToken ct = default,
        long totalBudget = Helpers.ReferenceImagePolicy.MaxTotalBytes,
        long transformedBudget = Helpers.ReferenceImagePolicy.MaxTotalBytes)
    {
        var retained = new List<(ReferenceImage, Models.ReferenceImageSelection)>(sanitizedSelections.Count);
        var remainingBudget = totalBudget;
        // The retained copy stores the PROVIDER-READY (possibly re-encoded) bytes, so
        // the transformed aggregate gets its own running cap — same greedy per-item
        // rule (an expanding re-encode drops that item, budget carries on).
        // RECONCILED accounting (ENH-6h diff r1): a DROPPED item — read failure,
        // over-budget original, or transformed overflow alike — consumes NEITHER
        // pool; the pools track what is actually retained (subtracting for a dropped
        // item would let one oversized file starve the remaining good references,
        // the exact outcome greediness exists to prevent). RETAINED items subtract
        // OriginalBytes from the original pool — never the transformed size —
        // exactly as in ReadReferenceImagesAsync. remainingTransformed also THREADS
        // into the read (ENH-6i diff r1) so a normalization the pool has no room for
        // rejects BEFORE its output allocates — the budget throw lands in the generic
        // per-item catch below: dropped greedily, budget unconsumed.
        var remainingTransformed = transformedBudget;
        foreach (var selection in sanitizedSelections)
        {
            try
            {
                var read = await AIEnhancementService.ReadReferenceImageCoreAsync(
                    selection, Helpers.AppPaths.ImagesDir, _referencesDir, ct,
                    remainingBudget, remainingTransformed).ConfigureAwait(false);
                if (read is { } r)
                {
                    if (r.Image.Bytes.Length > remainingTransformed)
                    {
                        Logger.Information("Failed-run reference not retained: transformed size over aggregate budget");
                        continue;
                    }
                    retained.Add((r.Image, selection));
                    remainingBudget -= r.OriginalBytes;
                    remainingTransformed -= r.Image.Bytes.Length;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A cancelled read proves nothing about the file — the caller's pipeline
                // must exit through its cancel path, never a fabricated retained/dropped
                // state (Codex final check R2).
                throw;
            }
            catch (Exception ex)
            {
                Logger.Information("Failed-run reference not retained: {ErrorType}", ex.GetType().Name);
            }
        }
        return retained;
    }

    /// <summary>Single-path wrapper over the batch delete.</summary>
    public Task TryDeleteIfUnreferencedAsync(string? referencePath, CancellationToken ct = default)
        => TryDeleteIfUnreferencedAsync(
            string.IsNullOrWhiteSpace(referencePath) ? Array.Empty<string>() : new[] { referencePath }, ct);

    /// <summary>
    /// Permanently delete reference copies UNLESS a remaining row still references the
    /// same file (canonical, case-insensitive comparison against every row's DECODED
    /// path list — ENH-6f) OR a live claim holds it (ENH-6e — an armed redo context,
    /// an in-flight generation, or a just-persisted unarmed copy). ONE DB snapshot
    /// covers the whole batch (ENH-6f: per-path scans multiplied full-table loads);
    /// each path's live check and delete still run under <c>_liveGate</c>, atomically
    /// against claim acquisition. Containment against the references root is enforced
    /// per file; missing files are tolerated. Called by
    /// <c>TranscriptionHistoryService</c> AFTER the row deletion committed, and by the
    /// release-time cleanup. No-op while quiesced (erasure in progress); a failed
    /// query retains EVERY candidate for the startup sweep.
    /// </summary>
    public async Task TryDeleteIfUnreferencedAsync(IReadOnlyList<string?> referencePaths, CancellationToken ct = default)
    {
        List<string>? candidates = null;
        foreach (var path in referencePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                (candidates ??= new List<string>()).Add(path);
        }
        if (candidates == null)
            return;
        if (!TryEnterDbOp())
            return; // quiesced — erasure owns the References folder now

        try
        {
            var referenced = await TryQueryReferencedCanonicalSetAsync(candidates, ct).ConfigureAwait(false);
            if (referenced == null)
            {
                Logger.Warning("Remaining-reference query failed — retaining reference file(s) for the startup sweep");
                return;
            }

            foreach (var path in candidates)
            {
                var canonical = TryCanonicalize(path);
                if (canonical == null)
                    continue; // unverifiable — retain for the sweep
                if (referenced.Contains(canonical))
                    continue; // a remaining row (possibly a duplicate/corrupt pointer
                              // with different casing) still needs the file — leave it
                _onBeforeLiveGateForTests?.Invoke();
                lock (_liveGate)
                {
                    if (IsLiveLocked(canonical))
                    {
                        // An armed/in-flight context still uses the file — its
                        // release-time cleanup re-runs this check later.
                        Logger.Information("Reference file retained — a live redo/generation context still references it");
                        continue;
                    }
                    TryDeleteContainedFile(path);
                }
            }
        }
        finally
        {
            ExitDbOp();
        }
    }

    /// <summary>
    /// Startup backstop for crash windows: delete every file in the references root
    /// (including <c>*.tmp</c> partials) that no row references AND that is older than
    /// <paramref name="olderThan"/> — the grace window keeps the sweep from racing an
    /// in-flight generation whose row hasn't committed yet. Must be invoked AFTER the
    /// DB migration added the column; failures are contained here (a sweep problem
    /// must never affect startup). Unconditional — not gated by any cleanup setting.
    /// </summary>
    public async Task SweepOrphansAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (!TryEnterDbOp())
            return; // quiesced — erasure in progress

        try
        {
            if (!Directory.Exists(_referencesDir))
                return;

            HashSet<string> referenced;
            using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var paths = await db.TranscriptionRecords
                    .Where(r => r.ReferenceImagePath != null && r.ReferenceImagePath != "")
                    .Select(r => r.ReferenceImagePath!)
                    .ToListAsync(ct).ConfigureAwait(false);
                // ENH-6f: each row's value may encode several paths — decode before
                // canonicalizing, or every multi-path row's copies would look orphaned.
                referenced = new HashSet<string>(
                    paths.SelectMany(Helpers.ReferencePathList.Decode).Select(TryCanonicalize).Where(p => p != null)!,
                    StringComparer.OrdinalIgnoreCase);
            }

            var cutoffUtc = DateTime.UtcNow - olderThan;
            var swept = 0;
            foreach (var file in Directory.EnumerateFiles(_referencesDir))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var canonical = TryCanonicalize(file);
                    if (canonical == null || referenced.Contains(canonical))
                        continue;
                    if (File.GetLastWriteTimeUtc(file) > cutoffUtc)
                        continue; // inside the grace window — may belong to an in-flight run
                    lock (_liveGate)
                    {
                        // ENH-6e: an armed/in-flight claim outranks the age check —
                        // check + delete under the lock, like the row-delete path.
                        if (IsLiveLocked(canonical))
                            continue;
                        File.Delete(file);
                    }
                    swept++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked/undeletable file — next startup's sweep retries.
                }
            }

            if (swept > 0)
                Logger.Information("Reference orphan sweep removed {Count} unreferenced file(s)", swept);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown during sweep — nothing to do.
        }
        catch (Exception ex)
        {
            // The sweep must never take startup down with it.
            Logger.Warning("Reference orphan sweep failed: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            ExitDbOp();
        }
    }

    /// <summary>
    /// Atomic copy of validated reference bytes into the references root:
    /// GUID-named temp file + <c>File.Move</c> to the final <c>ref_&lt;guid&gt;.&lt;ext&gt;</c>
    /// name only after a complete write, so a killed process can never leave a
    /// truncated file under a final name. The extension derives from the ALREADY
    /// VALIDATED whitelist mime (never the user's raw filename). Fail-soft: a
    /// non-cancellation failure removes the temp file and returns null (a matching
    /// cancellation also cleans the temp but RETHROWS — cancellation boundary);
    /// logging is path-free (the user's original path never enters this class at all).
    /// </summary>
    /// <summary>
    /// ENH-6g: the land-or-reuse primitive — content-addressed copy landing with
    /// dedup. Filename = <c>ref_&lt;HMACSHA256(installKey, bytes)&gt;.&lt;ext&gt;</c>
    /// (KEYED so exported basenames reveal nothing testable; identity = bytes + the
    /// validated mime's extension — identical bytes under different mimes stay
    /// separate deliberately, they are different provider payloads). Protocol
    /// (Codex plan rounds 1–4): cancellation first (the fast reuse path keeps the
    /// pinned pre-cancel contract) → hash OFF the calling thread → CLAIM the
    /// canonical target BEFORE any filesystem decision (deletion checks claims under
    /// <c>_liveGate</c>, so the exists/reuse choice can't race a last-row delete) →
    /// exists ⇒ verify FULL content through the kernel-verified gate (never trust
    /// the name; a mismatching file may be owned by other rows — left untouched,
    /// fall back to a unique GUID copy) / else write a UNIQUE temp and move, with a
    /// verified-winner arm for the two-writer move race. UNIVERSAL CLAIM RULE: an
    /// abandoned speculative claim releases with <c>Release(scheduleCleanup: false)</c>
    /// (scheduling infers ownership of a possibly pre-existing shared target); only
    /// claims RETURNED to the caller may schedule cleanup on disposal.
    /// </summary>
    internal async Task<(string Path, LiveReferenceLease Claim)?> LandOrReuseCopyAsync(
        ReferenceImage reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string finalPath;
        try
        {
            // Off the calling thread: the first item's hash would otherwise run
            // synchronously on the (UI) caller before the first await (≤50 MB HMAC).
            var hex = await Task.Run(() => Convert.ToHexString(
                global::System.Security.Cryptography.HMACSHA256.HashData(DedupKey, reference.Bytes)), ct).ConfigureAwait(false);
            Directory.CreateDirectory(_referencesDir);
            finalPath = Path.Combine(_referencesDir, $"ref_{hex.ToLowerInvariant()}{ExtensionFor(reference.MimeType)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("Reference dedup hashing failed ({ErrorType}) — falling back to a unique copy", ex.GetType().Name);
            return await SaveUniqueCopyWithClaimAsync(reference, ct).ConfigureAwait(false);
        }

        var claim = AddInFlightLease(new[] { TryCanonicalize(finalPath) ?? finalPath });
        string? tempPath = null;
        try
        {
            if (_probeFinalPathExists(finalPath))
            {
                if (await TryVerifyExistingCopyAsync(finalPath, reference, ct).ConfigureAwait(false))
                    return (finalPath, claim);
                Logger.Warning("Existing reference copy failed content verification — using a unique copy");
                claim.Release(scheduleCleanup: false);
                return await SaveUniqueCopyWithClaimAsync(reference, ct).ConfigureAwait(false);
            }

            tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
            await _writeAllBytesAsync(tempPath, reference.Bytes, ct).ConfigureAwait(false);
            try
            {
                File.Move(tempPath, finalPath);
                tempPath = null;
                return (finalPath, claim);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Two-writer race: someone landed the target first; verify the winner.
                if (await TryVerifyExistingCopyAsync(finalPath, reference, ct).ConfigureAwait(false))
                    return (finalPath, claim);
                Logger.Warning("Move-race winner failed content verification — using a unique copy");
                claim.Release(scheduleCleanup: false);
                return await SaveUniqueCopyWithClaimAsync(reference, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            claim.Release(scheduleCleanup: false);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("Reference copy failed ({ErrorType}) — generation continues without retention", ex.GetType().Name);
            claim.Release(scheduleCleanup: false);
            return null;
        }
        finally
        {
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { /* sweep backstop */ }
            }
        }
    }

    /// <summary>The unique-name (GUID) fallback: today's atomic writer plus its claim.</summary>
    private async Task<(string Path, LiveReferenceLease Claim)?> SaveUniqueCopyWithClaimAsync(
        ReferenceImage reference, CancellationToken ct)
    {
        var path = await TrySaveCopyAsync(reference, ct).ConfigureAwait(false);
        if (path == null)
            return null;
        // Claim-after-save is safe here and only here: nothing can reference a
        // fresh GUID name before we publish it.
        return (path, AddInFlightLease(new[] { TryCanonicalize(path) ?? path }));
    }

    /// <summary>
    /// FULL content comparison of an existing same-named copy against the run's bytes
    /// (strictly stronger than the planned digest compare — we hold the bytes), read
    /// through the kernel-verified gate so a reparse point can't stand in for the
    /// file. False on any failure (the caller falls back to a unique copy); a
    /// matching cancellation rethrows.
    /// </summary>
    private async Task<bool> TryVerifyExistingCopyAsync(string path, ReferenceImage expected, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var stream = Helpers.ReferenceImagePolicy.TryOpenVerifiedAppImage(path, _referencesDir);
                if (stream == null || stream.Length != expected.Bytes.Length)
                    return false;
                var buffer = new byte[81920];
                long offset = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!buffer.AsSpan(0, read).SequenceEqual(expected.Bytes.AsSpan((int)offset, read)))
                        return false;
                    offset += read;
                }
                return offset == expected.Bytes.Length;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };

    internal async Task<string?> TrySaveCopyAsync(ReferenceImage reference, CancellationToken ct = default)
    {
        var ext = ExtensionFor(reference.MimeType);
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_referencesDir);
            var name = $"ref_{Guid.NewGuid():N}{ext}";
            var finalPath = Path.Combine(_referencesDir, name);
            tempPath = finalPath + ".tmp";

            // Cancellable up to the move; past the move the copy is committed and the
            // cancellation boundary ends (nothing below takes the token).
            await _writeAllBytesAsync(tempPath, reference.Bytes, ct).ConfigureAwait(false);
            File.Move(tempPath, finalPath);
            tempPath = null; // moved — nothing to clean
            return finalPath;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The cancelled pipeline must see its cancel, never a fake fail-soft null;
            // the finally below cleans the partial temp and nothing reached a final name.
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning("Reference copy failed ({ErrorType}) — generation continues without retention", ex.GetType().Name);
            return null;
        }
        finally
        {
            // Immediate cleanup of a failed write's partial — long-running sessions
            // must not accumulate .tmp files until the next startup sweep.
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { /* sweep backstop */ }
            }
        }
    }

    private async Task<int?> SafeWriteRowAsync(Func<string?, Task<int?>> writeRow, string? copyPath, CancellationToken ct)
    {
        try
        {
            return await writeRow(copyPath).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // No copy exists on this path — nothing to reconcile; surface the cancel.
            throw;
        }
        catch (Exception ex)
        {
            // Mirrors TryWriteAsync's fail-soft contract: history problems never fail
            // a pipeline whose real work already succeeded.
            Logger.Warning("History write threw: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Quiesce-aware reconciliation query (ENH-6e): admission and the SQLite open are
    /// gated exactly like the delete/sweep paths — while quiesced, reconciliation
    /// reports AMBIGUOUS (null = retain everything for the sweep) without touching the
    /// database, so a drained quiesce really means no ReferencePersistence SQLite work
    /// is possible.
    /// </summary>
    private async Task<HashSet<string>?> QueryReferencedCanonicalSetCountedAsync(IReadOnlyList<string> candidates)
    {
        if (!TryEnterDbOp())
            return null;
        try
        {
            return await TryQueryReferencedCanonicalSetAsync(candidates).ConfigureAwait(false);
        }
        finally
        {
            ExitDbOp();
        }
    }

    /// <summary>
    /// ONE-snapshot membership query (ENH-6f): loads every row's encoded value once,
    /// decodes (<see cref="Helpers.ReferencePathList"/>), and returns the subset of
    /// <paramref name="candidates"/> (canonical, case-insensitive) some remaining row
    /// still references. NULL = the query failed or no candidate canonicalizes —
    /// ambiguous, callers must RETAIN every candidate (the tri-state's null arm;
    /// Codex plan round 2). Caller owns the DB-op admission.
    /// </summary>
    private async Task<HashSet<string>?> TryQueryReferencedCanonicalSetAsync(
        IReadOnlyList<string> candidates, CancellationToken ct = default)
    {
        try
        {
            var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var canonical = TryCanonicalize(candidate);
                if (canonical != null)
                    candidateSet.Add(canonical);
            }
            if (candidateSet.Count == 0)
                return null;

            using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var encodedValues = await db.TranscriptionRecords
                .Where(r => r.ReferenceImagePath != null && r.ReferenceImagePath != "")
                .Select(r => r.ReferenceImagePath!)
                .ToListAsync(ct).ConfigureAwait(false);

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var encoded in encodedValues)
            {
                foreach (var path in Helpers.ReferencePathList.Decode(encoded))
                {
                    var canonical = TryCanonicalize(path);
                    if (canonical != null && candidateSet.Contains(canonical))
                        referenced.Add(canonical);
                }
            }
            return referenced;
        }
        catch
        {
            return null;
        }
    }

    private void TryDeleteContainedFile(string path)
    {
        // Containment is non-negotiable even for paths this class produced — a
        // corrupt/imported DB value routed here must never delete outside the root.
        if (!Helpers.MediaPathPolicy.TryResolveWithin(path, _referencesDir, out var fullPath))
        {
            Logger.Warning("Reference delete refused — path failed containment");
            return;
        }
        try
        {
            File.Delete(fullPath); // permanent; no-throw when missing
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning("Reference delete failed ({ErrorType}) — sweep backstop applies", ex.GetType().Name);
        }
    }

    private static string? TryCanonicalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}