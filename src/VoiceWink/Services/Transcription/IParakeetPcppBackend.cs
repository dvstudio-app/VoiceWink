using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>What preparation resolved to for the Parakeet row when the pcpp backend is
/// registered — the runtime maps these onto <see cref="PrepareOutcome"/>.</summary>
public enum PcppPrepareOutcome
{
    /// <summary>The pcpp side serves and its server is resident and healthy: prepared, no sherpa
    /// load needed (the GGUF-only state must NOT read as NotDownloaded — the plan round's
    /// challenge 1).</summary>
    ServePcpp,

    /// <summary>The legacy sherpa path serves this preparation — the runtime continues into its
    /// unchanged load path.</summary>
    ServeSherpa,

    /// <summary>Nothing usable exists on disk; the caller's download flow drives the catalog row
    /// (interim semantics: a fresh install lands sherpa first and migrates — the rollout PR
    /// switches the catalog row).</summary>
    NotDownloaded,

    /// <summary>Bytes exist but nothing can serve right now (GGUF-only install whose server
    /// acquire failed, or a storm-tripped backend with no legacy bundle). A download is not the
    /// repair, so this must not read as NotDownloaded.</summary>
    Unavailable,
}

/// <summary>Which engine one transcription call runs on.</summary>
public enum PcppServe
{
    /// <summary>Decode through the resident server at <see cref="PcppAcquire.BaseUri"/>.</summary>
    Pcpp,

    /// <summary>Decode through the in-process sherpa path, unchanged.</summary>
    Sherpa,

    /// <summary>Neither side can serve (GGUF-only install whose acquire failed — the plan
    /// round's challenge 2): the call fails as a NORMAL transcription failure (REL-12 retains
    /// the WAV, amber Retry), never as a misleading "no model is loaded" from a sherpa branch
    /// that has no model.</summary>
    FailCall,
}

/// <summary>One transcription call's backend resolution. <see cref="BaseUri"/> and
/// <see cref="Generation"/> are meaningful only for <see cref="PcppServe.Pcpp"/>.</summary>
public readonly record struct PcppAcquire(PcppServe Decision, Uri? BaseUri, int Generation);

/// <summary>A pcpp slice decode failed (transport / status / contract / oversize — the typed
/// classes <see cref="ParakeetServerDecodeResult.FailureClass"/> carries). Thrown so the FIRST
/// failed slice aborts the whole call through <c>ChunkedDecode</c>'s normal propagation — the
/// whole-call failure policy (never a per-chunk mixed-engine fallback, never a partial join).</summary>
public sealed class PcppDecodeException(string failureClass)
    : Exception($"parakeet-server decode failed ({failureClass})")
{
    public string FailureClass { get; } = failureClass;
}

/// <summary>
/// TRN-29 slice 4: the ONE seam <see cref="ParakeetTranscriptionService"/> and
/// <see cref="ParakeetLocalRuntime"/> take for the parakeet.cpp backend. Null (the default in
/// every ordinary build) is the sherpa-only era, byte-inert.
///
/// <para><b>Why a seam instead of <c>PcppFeature.IsEnabled</c> inside the service:</b> the flag
/// is a compile-time const read ONLY at the composition root (its own documented pattern — the
/// <c>ParakeetFeature</c> precedent), and a const consulted inside the service would make the
/// routing unreachable in ordinary CI, so none of the race/routing behavior would ever execute
/// under test (the plan round's challenge 6). With the seam, tests inject fakes directly and
/// every row runs in the normal build; only <c>App.xaml.cs</c> reads the flag, registering the
/// real <see cref="ParakeetBackendCoordinator"/> when it is on.</para>
/// </summary>
public interface IParakeetPcppBackend
{
    /// <summary>Preparation-time resolution: derive state, run the transition decision, start
    /// the background migration download when it says so, and — when pcpp serves — spawn/warm
    /// the resident server so the decode at recording stop finds it ready.</summary>
    Task<PcppPrepareOutcome> PrepareAsync(CancellationToken ct);

    /// <summary>Per-transcription resolution under the service's lock. A resident healthy server
    /// is reused cheaply; the lease's generation is what a later escalation may kill.</summary>
    Task<PcppAcquire> TryAcquireForTranscriptionAsync(CancellationToken ct);

    /// <summary>Decode ONE slice through the resident server. Synchronous by design — it runs on
    /// the service's decode worker inside <c>Task.Run</c>, exactly where the sherpa decode runs.
    /// Returns the transcript ("" is a SUCCESSFUL empty decode); throws
    /// <see cref="PcppDecodeException"/> on any typed failure and <see cref="OperationCanceledException"/>
    /// on cancellation.</summary>
    string DecodeSlice(Uri baseUri, float[] slice, int sampleRate, CancellationToken ct);

    /// <summary>The whole-call failure policy's teeth (plan-round challenge 5c): a typed decode
    /// failure retires the leased generation, so a user Retry acquires a FRESH child instead of
    /// replaying the same possibly-wedged one. A genuinely broken server then charges the storm
    /// fuse through its involuntary exits until the transition serves sherpa.</summary>
    Task OnWholeCallFailedAsync(int generation);

    /// <summary>Race row 2: cancellation arrived while a pcpp request was IN FLIGHT. Waits the
    /// cancel-kill grace, probes the generation once, and kills it only if it does not answer —
    /// all on non-cancelled bounded tokens (the user's token is already cancelled and must not
    /// skip the escalation). Runs while the service still holds its lock, which is what makes a
    /// queued second transcription unable to observe a half-killed child.</summary>
    Task OnCancelledInFlightAsync(int generation);

    /// <summary>Protocol-success bookkeeping: the whole pcpp call completed without a typed
    /// failure — resets the whole-call failure tripwire and ARMS a pending proof for
    /// <see cref="NotifyUsableTranscription"/>. Called by the service strictly after its final
    /// cancellation check. Deliberately NOT the cleanup trigger (Codex diff r1+r2): a
    /// protocol-successful decode can still yield nothing usable — raw-empty, or raw text like
    /// a bracketed artifact that the TEXT PIPELINE then empties — and empty outcomes were the
    /// OLD engine's signature failure class, so they must never count as pcpp proving itself.</summary>
    void NotifyWholeCallSucceeded();

    /// <summary>The PROOF and the AUTO-CLEANUP trigger (owner, 2026-08-24: no button, no
    /// dialog): called by the ViewModel once the machine-owned text pipeline yielded NON-EMPTY
    /// text for a Parakeet transcription. The criterion is ENGINE HEALTH, deliberately NOT user
    /// receipt (owner design decision, 2026-08-24, closing four rounds of one class): whether
    /// the user then cancels an image picker or trips an echo gate is intent, not evidence
    /// about the decode, so downstream delivery gates play no part. Commits "pcpp has proven
    /// itself on this install" and fires ONE background delete of the legacy sherpa bundle
    /// (CAS-guarded; a partial failure retries on the next usable transcription; a run that
    /// finds nothing left latches quiet for the process lifetime). No-ops unless a pending
    /// proof from <see cref="NotifyWholeCallSucceeded"/> is armed AND unconsumed — the arm
    /// clears on every new acquire, so text served by the sherpa fall-through (or another
    /// engine after a stale pcpp success) can never prove pcpp (Codex diff r2).</summary>
    void NotifyUsableTranscription();

    /// <summary>The transition decision's row presentation for the single Parakeet Models-page
    /// row (Codex diff r1): with the GGUF migrated and sherpa cleaned up, the name-set the page
    /// derives from (<c>GetDownloadedModels</c>, catalog-driven) no longer contains the row, so
    /// without this the page shows "Download" over a healthy 940 MB install. Derives fresh.
    ///
    /// <para><b>TRN-38: TRI-STATE, and <c>null</c> means "could not determine".</b> Both readers
    /// below run on the UI THREAD, and the coordinator's state lock is held for up to ~20 s by a
    /// delete (TRN-37 moved the delete OFF the UI thread, which is exactly what made these two
    /// reachable freeze doors). They now take the lock with a bounded wait and answer
    /// <c>null</c> rather than parking the window.
    ///
    /// <para><b>The rule a caller must follow — and "leave the catalog-derived set untouched" is
    /// NOT it</b> (Grok round 3 advisory; this sentence USED to say that, and it is precisely the
    /// round-1 Blocker): while the answer is unknown, <b>preserve the row's last confirmed
    /// presentation and run no command against it</b>. Coercing null to a verdict, or letting the
    /// catalog answer stand in for one, renders Download over an artifact that is mid-delete —
    /// and the download path does not consult the coordinator's tombstone, so the user's delete
    /// gets reversed. See <c>ModelManagementViewModel.IsPcppRowStateIndeterminate</c>, which is
    /// where that policy lives; anything new that can flip a row must consult it.</para></summary>
    bool? RowInstalled();

    /// <summary>Do ANY Parakeet bytes exist (either bundle, or staging)? The DELETE gate — a
    /// row can be presentation-not-installed (VerifyFailed) while a 940 MB of bytes still need
    /// deleting, and the gate upstream of the delete routing read "nothing to act on" from the
    /// catalog resolver alone (Codex diff r1: the hidden GGUF was undeletable in the GGUF-only
    /// state). Tri-state since TRN-38 — see <see cref="RowInstalled"/>.</summary>
    bool? HasAnyArtifact();

    /// <summary>Row 11: delete BOTH artifacts (GGUF + legacy sherpa bundle) under the durable
    /// tombstone. Synchronous and bounded (the slow half is the server retire, normally
    /// milliseconds, worst-case parked at the retire wait); returns true only when both are
    /// gone and the tombstone cleared. On partial failure the tombstone STAYS — clearing it
    /// would let the migration download re-fetch bytes the user just asked to remove.</summary>
    bool TryDeleteBoth();

    /// <summary>REL-39: stop everything of ours that holds a file under <c>Models</c> so a GDPR
    /// erasure's delete pass can remove the folder — the same quiesce <see cref="TryDeleteBoth"/> runs
    /// before it deletes a bundle (cancel + bounded joins of the migration download and the
    /// verification hash, the TRN-49 warm-up child, the resident server), preceded by an
    /// UNCONDITIONAL warm-up cancel with the <c>DataErasure</c> reason (both legs) and a bounded wait
    /// for an in-flight Whisper self-test to end. No tombstone, no delete, no ledger change — the caller
    /// owns the bytes. Idempotent, every wait bounded. False means a child MAY still hold the file: the
    /// erasure continues regardless and names <c>Models</c> if the delete then fails (past its commit
    /// the user's personal data is already going; aborting would keep it to protect a model payload).</summary>
    Task<bool> TryQuiesceForErasureAsync();

    /// <summary>The installed legacy sherpa bundle's directory, or null when none is installed.
    /// The G6 flip made the catalog row the GGUF bundle, so the runtime's ServeSherpa
    /// fall-through can no longer resolve the sherpa directory from the row name — this is where
    /// it asks instead. Null AFTER <see cref="PrepareAsync"/> said ServeSherpa means an
    /// interleaved delete won the race; the caller reports NotDownloaded (self-repairing — the
    /// download flow fetches the active catalog row, which is the migration target).</summary>
    string? SherpaBundleDir();

    /// <summary>True when a user Retry structurally cannot succeed until the app restarts: the
    /// backend is latched unrunnable for this process (storm fuse, or the consecutive
    /// whole-call-failure tripwire) AND no legacy sherpa bundle remains to fall back to. The
    /// service consults this to make the failure message name the app restart instead of
    /// promising "Retry will restart the engine" — a promise a latched GGUF-only install loops
    /// on forever (the plan round's futile-copy fix).</summary>
    bool RetryRequiresAppRestart();

    /// <summary>
    /// TRN-50: the safety net for a GPU driver that decodes real speech to NOTHING (the owner's
    /// ARM64 laptop, Adreno X1-85: 3/3 dictations empty on the GPU, 3/3 fine on the CPU). Called
    /// by the service, under its lock, when a pcpp whole-call returned EMPTY on non-silent audio.
    /// Re-decodes the SAME plan at the SAME gain once, through a THROWAWAY child spawned in
    /// <see cref="ParakeetLaunchMode.Cpu"/>, and returns the joined text — or <c>null</c> when it
    /// was not attempted (the lease's child is not POSITIVELY GPU-confirmed by its own device
    /// line; already attempted this session; no model, gate refused, child never healthy) or did
    /// not complete. A non-empty result also requests CPU for the rest of the session and
    /// persists the FAIL verdict; an empty one changes nothing (the audio, not the GPU). Bounded
    /// to ONE attempt per session, never on a typed failure (that path retires the generation),
    /// never on silence (the caller's floor), never a second engine.
    /// </summary>
    Task<string?> TryDecodeOnCpuFallbackAsync(
        int generation, IReadOnlyList<DecodeChunk> plan, float[] samples, int sampleRate, double gainDb,
        CancellationToken ct);

    /// <summary>App exit: kill the resident server explicitly (the job object is the app-DEATH
    /// backstop, not the graceful path) and stop background work.</summary>
    Task ShutdownAsync();
}
