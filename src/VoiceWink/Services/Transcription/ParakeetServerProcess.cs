using System.Security.Cryptography;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>The launcher seam — everything that actually touches Win32. CI tests substitute a
/// fake; the real implementation is <see cref="NativeParakeetServerLauncher"/>. Kill/dispose act
/// on the OPAQUE handle a launch returned, never on a PID looked up later — the INS-4 lesson
/// (a PID resolves whoever owns it AT KILL TIME).</summary>
public interface IParakeetServerLauncher
{
    /// <summary>Create the child ALREADY INSIDE a kill-on-close job (atomic — no
    /// assign-after-start window; slice-2 plan round, Blocker 1). Null = launch failed for any
    /// reason (job setup, attribute list, process creation); the caller treats every failure
    /// identically: no child exists, sherpa serves this preparation.
    /// <para><paramref name="mode"/> (TRN-51/53) selects the child's compute device via a
    /// CHILD-SPECIFIC environment block — never a process-global mutation. <c>Auto</c> also
    /// strips an inherited <c>PARAKEET_DEVICE</c>, so a stray variable in the app's own
    /// environment cannot silently flip devices.</para>
    /// <para>TRN-68: <paramref name="deviceToken"/> (a <see cref="GpuAdapterPreference.DeviceToken"/>
    /// name, <c>Vulkan1</c>) pins an <c>Auto</c> child to that adapter — read from a PREVIOUS
    /// child's own enumeration in the same process environment, never persisted. Ignored under
    /// <c>Cpu</c>.</para></summary>
    IParakeetServerChild? TryLaunch(string exePath, string modelPath, ParakeetLaunchMode mode, string? deviceToken = null);
}

/// <summary>One launched child generation. Disposing kills it (the job handle closes with it,
/// so disposal is also what makes app-death cleanup a kernel guarantee rather than our code).</summary>
public interface IParakeetServerChild : IDisposable
{
    uint Pid { get; }
    bool HasExited { get; }

    /// <summary>The loopback port the child is LISTENING on, read from the kernel's TCP table
    /// keyed by this child's PID — the server's own banner prints the requested port (literal
    /// 0), so the OS is the only trustworthy handoff, and PID attribution is what a same-user
    /// spoofer cannot forge (plan round, Blocker 2). Null until the socket opens.</summary>
    int? TryReadListeningPort();

    /// <summary>Terminate this generation's process. Idempotent; never targets a PID.</summary>
    void Kill();

    /// <summary>Block up to <paramref name="timeout"/> for CONFIRMED process exit.
    /// TerminateProcess is asynchronous, so a kill without confirmation lets a successor
    /// spawn while ~1 GB of predecessor still dies (Codex diff r2 Blocker).</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>TRN-50: the compute backend THIS child selected, as its own parsed
    /// <c>using device:</c> token (<c>Vulkan0</c>, <c>CPU</c>) — TRN-60's line, read off the
    /// child's pipe; null until the child printed it, or forever when it never does. The ONLY
    /// basis for claiming a decode ran on the GPU (<see cref="ParakeetGpuEvidence"/>): the launch
    /// mode is a REQUEST, and an Auto child on a loader-present/no-device machine serves CPU.</summary>
    string? ObservedBackend { get; }

    /// <summary>TRN-50: the name of the device the child selected — the parsed device row whose
    /// index matches the selected <c>Vulkan&lt;N&gt;</c> token (a multi-adapter box must name the
    /// adapter that was USED, not the first one enumerated); null when unresolved. Parsed field
    /// only, bounded by the parser; feeds the log and the Settings advisory.</summary>
    string? ObservedGpuName { get; }

    /// <summary>TRN-68: ggml's <c>Found N Vulkan devices</c> count as this child printed it, −1
    /// until it did. With <see cref="ObservedDevices"/> and <see cref="ObservedBackend"/> it is the
    /// whole input of <see cref="GpuAdapterPreference.Decide"/> — the decision to respawn pinned is
    /// made from THIS child's enumeration and no other.</summary>
    int ObservedDeviceCount { get; }

    /// <summary>TRN-68: the device rows this child printed so far (a snapshot, pipe order; parsed
    /// fields only).</summary>
    IReadOnlyList<GgmlVulkanDeviceLine> ObservedDevices { get; }
}

/// <summary>
/// TRN-29 slice 2: owns at most ONE resident `parakeet-server` child and its lifecycle —
/// spawn → readiness → serve → kill — per the revised design
/// (<c>docs/plans/2026-08-23-trn29-parakeetcpp-swap/14-slice2-server-process.md</c>).
///
/// <para><b>No app caller yet.</b> The pcpp backend (a later slice) acquires the server through
/// <see cref="TryAcquireAsync"/>; until then nothing constructs this outside tests, and the
/// feature flag keeps the transition core on sherpa regardless.</para>
///
/// <para><b>One lease, generation-bound.</b> All lifecycle transitions run under one
/// <see cref="SemaphoreSlim"/> (never disposed — the standing shutdown scar). Every child
/// carries a generation number; <see cref="KillGeneration"/> — the cancellation escalation —
/// refuses any generation but the one its caller captured, so a cancelled request that lost
/// the race can never kill a successor's child or a decode running behind it (plan round,
/// Blocker 3).</para>
///
/// <para><b>The exe PROVENANCE is verified per LAUNCH</b> (plan round, high-value): a binary that
/// proves neither the pinned SHA-256 nor our Authenticode signature means no child, this
/// preparation falls back, and the storm fuse is NOT charged — a wrong binary is a provenance
/// failure, not a crash.</para>
///
/// <para><b>TRN-34 made that TWO proofs, and the wording above used to say only "hash".</b> It was
/// accurate until the shipped copy started being signed, at which point a pinned-hash-only gate
/// refused every spawn in every signed release. <see cref="ParakeetServerProvenance"/> owns the
/// decision; the seam that injects the signature verdict exists so the SIGNED branch is reachable
/// from a test, which is the thing whose absence let the original defect ship.</para>
/// </summary>
public sealed class ParakeetServerProcess
{
    private static ILogger Logger => Log.ForContext<ParakeetServerProcess>();

    private readonly IParakeetServerLauncher _launcher;
    private readonly Func<Uri, CancellationToken, Task<bool>> _probe;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, AuthenticodeSignature.Verdict> _verifySignature;
    private readonly SemaphoreSlim _gate = new(1, 1); // never disposed, deliberately.
    private readonly List<DateTime> _involuntaryExitsUtc = [];

    private IParakeetServerChild? _child;
    private IParakeetServerChild? _retiring;
    private int _generation;
    private Uri? _baseUri;
    private string? _servedModelPath;
    private string? _servedInstallIdentity;
    private bool _stormTripped;
    private readonly TimeSpan _probeBudget;

    /// <summary>TRN-51/53: the device mode children spawn with. Starts from the composition
    /// root's decision (toggle OFF or no system Vulkan loader ⇒ <see cref="ParakeetLaunchMode.Cpu"/>)
    /// and can move Auto→Cpu exactly once, as a session LATCH, when an Auto child died before
    /// health and its single un-charged CPU retry came up healthy (codex plan round, Blocker 2:
    /// a bad-but-present GPU driver must not walk the storm fuse into "default engine down" when
    /// the same binary serves fine on CPU). Only ever written under <c>_gate</c>.</summary>
    private ParakeetLaunchMode _launchMode;

    /// <summary>Advisory read for the coordinator's state derivation (feeds
    /// <c>ParakeetTransitionState.PcppRunnable</c>). Deliberately lock-free: a bool read is
    /// atomic, the fuse only ever goes false→true, and the answer is re-derived at every
    /// preparation — a one-cycle-stale read costs one extra refused acquire, nothing more.</summary>
    public bool IsStormTripped => _stormTripped;

    /// <summary>TRN-68 test seam: how many INVOLUNTARY exits the storm ledger holds right now —
    /// the direct proof that a pinned respawn's retire and a pinned child's death are never
    /// charged (the fuse-trip sequences prove the same thing four acquires later).</summary>
    internal int InvoluntaryExitCountForTest => _involuntaryExitsUtc.Count;

    /// <summary>TRN-50: the mode the LIVE child was spawned with — what <see cref="RequestCpu"/>'s
    /// consumer reads to decide whether that child must go. Written under <c>_gate</c> beside
    /// <c>_child</c>.</summary>
    private ParakeetLaunchMode _servedMode = ParakeetLaunchMode.Auto;

    /// <summary>TRN-50: a pending CPU request — 0 none, 1 requested. Monotonic (never cleared) and
    /// Interlocked, because the writers run OUTSIDE <c>_gate</c> by design: the self-test lands off
    /// the startup path and the CPU re-decode runs under the transcription service's lock, and
    /// either awaiting <c>_gate</c> could park behind an acquire's 30 s health hold (Codex plan
    /// round, Blocker 4). <see cref="TryAcquireAsync"/> consumes it under the gate.</summary>
    private int _cpuRequested;

    /// <summary>TRN-68: set when a child PINNED to the dedicated adapter died before health — the
    /// dedicated driver is the suspect, so the pin is not offered again this session (the next start
    /// re-evaluates from fresh rows) and that death takes the same uncharged CPU retry an Auto death
    /// takes. Only ever written under <c>_gate</c>.</summary>
    private bool _pinRefused;

    /// <summary>TRN-52: the device mode the NEXT child spawns with — <see cref="ParakeetLaunchMode.Auto"/>
    /// (GPU permitted) or the Cpu pin/latch. Advisory and lock-free like <see cref="IsStormTripped"/>:
    /// an enum read is atomic, the value moves Auto→Cpu at most once per session, and its one
    /// consumer (the Models page's speed-star set, via <c>LocalComputeSnapshot</c>) re-reads it on
    /// every render. It is the app's OWN resolution, not the child's report: a loader-present
    /// machine with no working device serves CPU under Auto and still reads Auto here — the
    /// documented residual, the same class as Whisper's loaded-Vulkan-with-zero-devices.
    /// Since TRN-50 a pending CPU request already reads <see cref="ParakeetLaunchMode.Cpu"/> here,
    /// so the stars follow the decision at once rather than at the next acquire.</summary>
    public ParakeetLaunchMode LaunchMode
        => Volatile.Read(ref _cpuRequested) != 0 ? ParakeetLaunchMode.Cpu : _launchMode;

    /// <summary>TRN-64 PR 2 (Codex diff r1 Blocker): is a LIVE child serving this process from the
    /// GPU — the child's own parsed device token through <see cref="ParakeetGpuEvidence.IsGpu"/>,
    /// never <see cref="LaunchMode"/>. Auto is a REQUEST: a loader-present machine with no usable
    /// adapter serves CPU under it and still reads Auto (the residual documented there), so an
    /// affirmative "running on your graphics card" built on the launch mode is a claim the app
    /// cannot support. No child, an exited child, or a CPU-served one all read false — the
    /// direction that WITHHOLDS the claim. Lock-free like its neighbours: one volatile read of the
    /// same field <see cref="ObservedFor"/> reads, and the answer is advisory (the row re-reads it
    /// on every render).</summary>
    internal bool HasLiveGpuEvidence => GetLiveGpuEvidence().IsGpu;

    /// <summary>UI-14 / TRN-68: the adapter the LIVE child is computing on — its own resolved row
    /// name — or null exactly when <see cref="HasLiveGpuEvidence"/> is false (or the name is not
    /// resolved yet). The Models page names THIS, not the warm-up child's persisted name: since
    /// TRN-68 each child picks its adapter independently, and after a refused pin the resident child
    /// can sit on the integrated adapter while the marker still carries the dedicated one the warm-up
    /// child measured (self-review Blocker).</summary>
    internal string? LiveGpuName => GetLiveGpuEvidence().Name;

    /// <summary>The two live facts from ONE captured child reference (Codex verification-round
    /// Blocker): read as two properties they were two volatile reads, and a child that exited or
    /// was replaced between them paired <c>IsGpu == true</c> with a null name — the page then fell
    /// back to the persisted warm-up adapter and named it live. Every consumer takes the pair from
    /// here, so evidence from two generations can never be combined. Lock-free like
    /// <see cref="ObservedFor"/>: one volatile read, advisory answer, re-read on every render.</summary>
    internal (bool IsGpu, string? Name) GetLiveGpuEvidence()
    {
        var child = Volatile.Read(ref _child);
        if (child is not { HasExited: false } || !ParakeetGpuEvidence.IsGpu(child.ObservedBackend))
        {
            return (false, null);
        }
        return (true, child.ObservedGpuName);
    }

    /// <summary>Test seam: install the child this process reports on, so the predicate above can
    /// be exercised without a real spawn (Codex diff r1 advisory — the wiring that SUPPLIES the
    /// tick's flag had no coverage). Never called outside tests.</summary>
    internal void SetChildForTest(IParakeetServerChild? child) => Volatile.Write(ref _child, child);

    /// <summary>TRN-67 test seam: hold <see cref="_gate"/> so a contention row is deterministic
    /// rather than a race. The real long holder is <see cref="TryAcquireAsync"/>'s spawn-and-health
    /// wait, which a test cannot park for a controlled interval without also driving a real
    /// child — and which leaves <c>_child</c> null while it holds, so it cannot exercise
    /// "a missed shutdown left the EXISTING child alive" at all. The
    /// <c>ParakeetBackendCoordinator.HoldStateForTests</c> precedent.</summary>
    internal IDisposable HoldGateForTests()
    {
        _gate.Wait();
        return new GateHold(_gate);
    }

    private sealed class GateHold(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    /// <summary>TRN-50: the launcher, for the coordinator's throwaway CPU re-decode child — the
    /// same seam every spawn takes, so that child inherits the spawn gate and the kill-and-confirm
    /// protocol through <see cref="ParakeetEphemeralChild"/> rather than a second launch path.</summary>
    internal IParakeetServerLauncher Launcher => _launcher;

    /// <summary>
    /// TRN-50: ask for CPU from the next spawn on — a NON-BLOCKING, monotonic intent. Sets the
    /// flag, logs the reason once, returns at once; never touches <c>_gate</c>. The next
    /// <see cref="TryAcquireAsync"/> consumes it: launch mode Cpu, and a live child that was
    /// spawned under Auto is retired before reuse (an expected stop, never a fuse charge). A
    /// child mid-decode is therefore never killed by a verdict — the decode holds the service
    /// lock, not this gate, and only the NEXT acquire retires it (Codex plan round, Blocker 4).
    /// Callers: the GPU self-test's FAIL and the CPU re-decode that recovered text.
    /// </summary>
    public void RequestCpu(string reason)
    {
        if (Interlocked.Exchange(ref _cpuRequested, 1) == 0)
        {
            Logger.Warning("parakeet-server: CPU requested for the rest of this session - {Reason}; the next acquire retires a GPU-served child and spawns Cpu",
                LogValueSanitizer.SingleLine(reason));
        }
    }

    /// <summary>
    /// TRN-50: the live child's own compute observation for <paramref name="generation"/> —
    /// its parsed backend-selection token and the selected adapter's name — or null when the
    /// generation is not the live one, the child is gone, or it never printed a selection.
    /// Lock-free by design (the caller holds the transcription service's lock and must not wait
    /// on <c>_gate</c>): a torn read across an acquire can only REFUSE, never affirm a GPU that
    /// was not observed, which is the fail-closed direction for everything keyed on it.
    /// <para>That property is EARNED by ordering, not assumed (self-review, concurrency lens): the
    /// spawn path publishes the new generation number BEFORE the new child, and this reads the
    /// child BEFORE the generation, both through <see cref="Volatile"/>. So a reader that sees a
    /// successor child necessarily sees a generation past the one it asked for and refuses; the
    /// reverse order let it pair generation G with generation G+1's child and affirm THAT child's
    /// device line for a decode it never ran — a false FAIL verdict pinned for the app version.</para>
    /// </summary>
    public (string? Backend, string? GpuName)? ObservedFor(int generation)
    {
        var child = Volatile.Read(ref _child);
        if (child is null || Volatile.Read(ref _generation) != generation || child.HasExited)
        {
            return null;
        }
        return (child.ObservedBackend, child.ObservedGpuName);
    }

    /// <param name="probe">One health-probe request against the candidate base URI; true =
    /// the server answered semantically. Injected so CI never opens a socket.</param>
    /// <param name="utcNow">Clock seam for the storm ledger.</param>
    /// <param name="probeBudget">Real-time bound on each probe/poll await, so a HUNG probe
    /// cannot hold the gate and its candidate child past the health contract (Codex diff r2
    /// Blocker). Defaults to <see cref="ParakeetServerPolicy.HealthBudget"/>; overridable as a
    /// test seam only.</param>
    /// <param name="verifySignature">TRN-34 Authenticode seam. A test cannot produce a file signed
    /// by our certificate — that is exactly why the signed branch of this gate went unexercised and
    /// shipped broken — so the verdict is injectable and the production default is the real
    /// verifier.</param>
    public ParakeetServerProcess(
        IParakeetServerLauncher launcher,
        Func<Uri, CancellationToken, Task<bool>> probe,
        Func<DateTime>? utcNow = null,
        TimeSpan? probeBudget = null,
        Func<string, AuthenticodeSignature.Verdict>? verifySignature = null,
        ParakeetLaunchMode initialLaunchMode = ParakeetLaunchMode.Auto)
    {
        _verifySignature = verifySignature ?? AuthenticodeSignature.Verify;
        _launcher = launcher;
        _probe = probe;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probeBudget = probeBudget ?? ParakeetServerPolicy.HealthBudget;
        _launchMode = initialLaunchMode;
    }

    /// <summary>The lease a successful acquire returns: where to POST, and which generation a
    /// later cancellation escalation may kill.</summary>
    public readonly record struct ServerLease(Uri BaseUri, int Generation);

    /// <summary>
    /// Ensure a healthy resident server for <paramref name="modelPath"/> and return its lease,
    /// or null when the backend cannot run this preparation (storm tripped, provenance refused —
    /// neither the pinned hash nor our signature — launch/health failure): the transition core then
    /// serves sherpa (row 10), or reports Unavailable when no legacy bundle exists. Never throws
    /// for lifecycle reasons; cancellation propagates.
    ///
    /// <para><paramref name="installIdentity"/> binds the resident child to the INSTALL, not the
    /// path (slice-4 plan round, challenge 3): a delete + re-download lands new bytes at the SAME
    /// path, and a path-keyed cache would keep serving the prior install's resident model past the
    /// coordinator's fresh hash verdict. A live child whose identity differs is retired as an
    /// EXPECTED stop — never charged to the storm fuse.</para>
    /// </summary>
    public async Task<ServerLease?> TryAcquireAsync(
        string exePath, string expectedExeSha256, string modelPath, string installIdentity,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A predecessor whose exit is UNCONFIRMED blocks every successor: one child is the
            // invariant, and TerminateProcess is asynchronous (Codex diff r2 Blocker). Its
            // handles stay open (never disposed while unconfirmed) precisely so exit remains
            // observable and the job coupling stays alive.
            if (_retiring is { } retiring)
            {
                if (!retiring.HasExited)
                {
                    Logger.Warning("parakeet-server: predecessor still exiting - serving sherpa this preparation");
                    return null;
                }
                retiring.Dispose();
                _retiring = null;
            }

            // TRN-50: consume a pending CPU request BEFORE the reuse check — the request was made
            // outside the gate (self-test FAIL, or a CPU re-decode that recovered text) and this
            // is the one place it takes effect. The mode moves to Cpu, and a live child that was
            // spawned under Auto is retired as an EXPECTED stop (it is charged only if it had
            // already died on its own, exactly like a model switch), so the next spawn is Cpu.
            if (Volatile.Read(ref _cpuRequested) != 0 && _launchMode != ParakeetLaunchMode.Cpu)
            {
                _launchMode = ParakeetLaunchMode.Cpu;
                Logger.Information("parakeet-server: CPU request consumed - launch mode is Cpu for the rest of this session");
            }
            if (_launchMode == ParakeetLaunchMode.Cpu && _child is { } gpuServed && _servedMode == ParakeetLaunchMode.Auto)
            {
                if (gpuServed.HasExited)
                {
                    RecordInvoluntaryExit();
                }
                Retire(gpuServed);
                _child = null;
                _baseUri = null;
                Logger.Information("parakeet-server: retired the GPU-served child so the next spawn is Cpu");
                if (_retiring is not null)
                {
                    return null; // its exit is unconfirmed; no successor may spawn yet.
                }
            }

            // A live child serving the same model AND the same INSTALL: observe health cheaply
            // and reuse. Identity is ordinal — it is an opaque token the coordinator minted, not
            // a path.
            //
            // Reuse MINTS A NEW GENERATION (slice-4 self-review, concurrency F2): a cancelled
            // recording's escalation runs its 2 s grace OUTSIDE every lock, and a new
            // recording's preflight can re-lease this child inside that window. With a shared
            // generation number the stale escalation's kill would take the successor's child
            // (one busy OPTIONS probe away from a cold 30 s respawn); with a fresh number the
            // stale kill no-ops on the generation compare. The wedged-child case still
            // converges — the successor's decode fails, the whole-call policy retires ITS
            // generation, and the fuse sees any real crash.
            if (_child is { } existing && !existing.HasExited
                && string.Equals(_servedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_servedInstallIdentity, installIdentity, StringComparison.Ordinal)
                && _baseUri is not null)
            {
                _generation++;
                return new ServerLease(_baseUri, _generation);
            }

            // A dead, wrong-model, or wrong-INSTALL child is retired first (a model or install
            // switch is an EXPECTED stop; an already-exited child was involuntary and is charged
            // to the fuse here, at the first moment we observe it).
            if (_child is { } stale)
            {
                if (stale.HasExited)
                {
                    RecordInvoluntaryExit();
                }
                Retire(stale);
                _child = null;
                _baseUri = null;
                if (_retiring is not null)
                {
                    // The old child has not confirmed its exit yet; no successor may spawn.
                    return null;
                }
            }

            // The storm fuse survives model-path rebuilds by design: a crashing binary does not
            // get a fresh fuse by switching artifacts. Only app restart resets it.
            if (_stormTripped || !ParakeetServerPolicy.MayRespawn(_involuntaryExitsUtc, _utcNow()))
            {
                if (!_stormTripped)
                {
                    _stormTripped = true;
                    Logger.Error("parakeet-server: crash storm tripped ({Count} involuntary exits in {Window}) - pcpp backend unrunnable until app restart",
                        _involuntaryExitsUtc.Count, ParakeetServerPolicy.StormWindow);
                }
                return null;
            }

            // Provenance, per LAUNCH: a wrong binary never spawns and never charges the fuse.
            // TRN-34's rationale (two accepted proofs, hash first, signature only on mismatch)
            // lives on ParakeetSpawnGate since TRN-51, because the warm-up path spawns too and
            // the gate must be a property of the SPAWN, not of this class — see the gate's doc.
            var gate = ParakeetSpawnGate.Check(exePath, expectedExeSha256, _verifySignature);
            if (!gate.Spawnable)
            {
                // Name BOTH branches. The old line said only "executable hash mismatch", which read
                // as file corruption and hid a systematic release defect for a week. Sanitized:
                // the signature branch embeds the certificate SUBJECT, attacker-authored text on
                // the planted-exe path, and this line reaches the support bundle.
                Logger.Error("parakeet-server: refusing to spawn - {Reason}", LogValueSanitizer.SingleLine(gate.RefusalReason));
                return null;
            }

            // TRN-51 (codex plan round, Blocker 2): an Auto (GPU) child that exits BEFORE health
            // gets exactly ONE CPU retry, not charged to the storm fuse — a bad-but-present
            // Vulkan driver that kills the child at device init must not walk the fuse into
            // "default engine unrunnable" when the same binary serves fine on CPU. A healthy
            // retry LATCHES Cpu for the session; a Cpu-mode failure takes the existing storm
            // policy unchanged. The gate may be held for two health budgets in the retry case —
            // three since TRN-68 on a two-GPU PC whose pinned child then dies (Auto → pinned →
            // Cpu; each spawn under its own budget, the pinned respawn's kill sub-second) —
            // acceptable: acquire already tolerates one 30 s cold-load budget, every contender
            // is bounded (ShutdownGateWait, the delete flow's gate wait, the escalation probe),
            // and the retry path exists precisely so that machine class ends up SERVED.
            var mode = _launchMode;
            string? pin = null;        // TRN-68: the adapter token THIS attempt carries, if any
            var cpuRetried = false;    // the one uncharged CPU retry, spent or not
            while (true)
            {
            string? respawnPin = null; // TRN-68: set inside the health wait when the child's own rows say "pin"
            // A cancelled acquire must not pay a ~1 GB spawn it will retire one line later — the
            // relaunches (pinned, CPU retry) re-enter here after a Retire that took real time.
            ct.ThrowIfCancellationRequested();
            var child = _launcher.TryLaunch(exePath, modelPath, mode, pin);
            if (child is null)
            {
                Logger.Warning("parakeet-server: launch failed (job setup or process creation, launch mode {Mode}) - serving sherpa this preparation", mode);
                return null;
            }

            // From this point the candidate child EXISTS and must never escape untracked:
            // cancellation or a probe/delay exception before it is assigned to _child would
            // otherwise strand a resident server no later code path can reach (Codex diff r1
            // Blocker) - in-job, so app death still reaps it, but alive until then while
            // further acquires spawn more.
            var spawnedUtc = _utcNow();
            Uri? baseUri = null;
            using var health = CancellationTokenSource.CreateLinkedTokenSource(ct);
            health.CancelAfter(_probeBudget);
            try
            {
                while (ParakeetServerPolicy.WithinHealthBudget(spawnedUtc, _utcNow()))
                {
                    health.Token.ThrowIfCancellationRequested();
                    if (child.HasExited)
                    {
                        break;
                    }
                    // TRN-68: the child's OWN enumeration decides, before health — the rows print in
                    // its first few hundred ms and the model load takes seconds, so a pinned respawn
                    // is a sub-second kill of a child that has loaded nothing yet. Auto only, once
                    // per acquire, never after a pinned child died this session.
                    if (pin is null && mode == ParakeetLaunchMode.Auto && !_pinRefused)
                    {
                        var decision = GpuAdapterPreference.Decide(
                            child.ObservedDeviceCount, child.ObservedDevices,
                            ParakeetGpuEvidence.TryDeviceIndex(child.ObservedBackend, out var selected) ? selected : null);
                        if (decision.Kind == GpuAdapterDecisionKind.Pin)
                        {
                            respawnPin = GpuAdapterPreference.DeviceToken(decision.Index);
                            Logger.Information("parakeet-server: the child selected {GpuName} while {PinnedGpuName} is available - respawning pinned to {Pin} (TRN-68)",
                                LogValueSanitizer.SingleLine(decision.SelectedName ?? "?"),
                                LogValueSanitizer.SingleLine(decision.PinnedName ?? "?"),
                                respawnPin);
                            break;
                        }
                    }
                    if (baseUri is null && child.TryReadListeningPort() is { } port)
                    {
                        baseUri = new Uri($"http://127.0.0.1:{port}/");
                    }
                    // WaitAsync bounds the AWAIT itself: CancelAfter only cancels the TOKEN,
                    // and a probe that ignores it would otherwise block here forever holding
                    // the gate and the candidate child (Codex diff r3). The orphaned probe
                    // task's eventual fault is observed fire-and-forget so it cannot surface
                    // as an unobserved-task exception.
                    var probeTask = baseUri is null ? null : _probe(baseUri, health.Token);
                    if (probeTask is not null)
                    {
                        bool healthy;
                        try
                        {
                            healthy = await probeTask.WaitAsync(health.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _ = probeTask.ContinueWith(
                                static task => _ = task.Exception,
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            throw;
                        }
                        if (healthy)
                        {
                            if (mode == ParakeetLaunchMode.Cpu && cpuRetried)
                            {
                                // The un-charged retry came up healthy: the GPU path is what
                                // failed, so stop offering it for the rest of the session.
                                _launchMode = ParakeetLaunchMode.Cpu;
                                Logger.Warning("parakeet-server: CPU retry healthy after the Auto (GPU) child died before health - latching CPU launch mode for this session");
                            }
                            // Generation BEFORE child, both volatile — the order ObservedFor's
                            // lock-free refusal depends on (its doc comment says why).
                            Volatile.Write(ref _generation, _generation + 1);
                            Volatile.Write(ref _child, child);
                            _baseUri = baseUri;
                            _servedModelPath = modelPath;
                            _servedInstallIdentity = installIdentity;
                            _servedMode = mode; // TRN-50: what a later CPU request retires on.
                            // TRN-53 follow-up: name the launch mode this child was SPAWNED with — the
                            // local `mode`, which is the served one after a CPU retry. `Cpu` proves CPU
                            // was REQUESTED for the child (the toggle off, no system Vulkan loader, or
                            // the session latch), never which of those caused it; `Auto` is the
                            // GPU-if-enumerable request, never a GPU claim (the child does not report
                            // its device — the residual `LocalComputeSnapshot` records). Until this
                            // line the log held no Parakeet-side evidence of the OFF state at all.
                            Logger.Information("parakeet-server: generation {Gen} healthy on port {Port} (pid {Pid}, launch mode {Mode})",
                                _generation, baseUri!.Port, child.Pid, mode);
                            if (pin is not null)
                            {
                                // TRN-68: the pin is a REQUEST like the mode; the child's own `backend
                                // device` line (already logged) is the proof it took. A child that
                                // answered with another token (parakeet.cpp's name-not-found falls
                                // back to CPU) is served as it is — one pinned attempt, never a loop.
                                Logger.Information("parakeet-server: generation {Gen} pinned to {Pin} (TRN-68)", _generation, pin);
                                if (child.ObservedBackend is { } served && !string.Equals(served, pin, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Warning("parakeet-server: the child pinned to {Pin} selected {GpuDevice} instead - served as it is (TRN-68)",
                                        pin, LogValueSanitizer.SingleLine(served));
                                }
                            }
                            return new ServerLease(baseUri, _generation);
                        }
                    }
                    await Task.Delay(ParakeetServerPolicy.HealthPollInterval, health.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // USER cancellation: retire the candidate, propagate.
                Retire(child);
                throw;
            }
            catch (OperationCanceledException)
            {
                // The BUDGET fired while an await hung (a probe that accepts and never
                // answers): a health failure, not a user cancel and not a crash - retire and
                // fall back instead of propagating a cancellation nobody requested.
                Retire(child);
                RefusePinIfPinned(pin, "hung before health");
                Logger.Warning("parakeet-server: health wait exceeded {Budget} (hung probe, launch mode {Mode}) - serving sherpa this preparation",
                    _probeBudget, mode);
                return null;
            }
            catch (Exception ex)
            {
                // A throwing probe is a health failure, not a lifecycle crash: retire the
                // candidate and take the documented fallback. Never charge the fuse - the
                // child did not exit on its own.
                Retire(child);
                RefusePinIfPinned(pin, "its health probe threw");
                Logger.Warning(ex, "parakeet-server: health probe threw (launch mode {Mode}) - serving sherpa this preparation", mode);
                return null;
            }

            // TRN-68: the child's own rows asked for a pin — an EXPECTED stop, never charged.
            // Retire through the confirm-or-park path like every other retire; an unconfirmed
            // exit defers the pinned spawn to the next preparation under the one-child invariant.
            if (respawnPin is not null)
            {
                Retire(child);
                if (_retiring is not null)
                {
                    Logger.Warning("parakeet-server: predecessor exit unconfirmed - pinned respawn deferred to the next preparation (TRN-68)");
                    return null;
                }
                pin = respawnPin;
                continue;
            }

            // Health failure (budget expired, or the child died mid-wait): retire, charge the
            // fuse only when the child exited on its own, fall back.
            var exitedOnItsOwn = child.HasExited;
            Retire(child);
            // TRN-68: a PINNED attempt that failed in ANY way — died, or sat there without ever
            // reaching health — refuses the pin for the session BEFORE the retry decision below
            // (self-review Blocker: keyed on the death alone, a pinned child that HANGS at device
            // init would be retried on every acquire, ~35 s each, and the machine would lose the
            // integrated-adapter child it served with before TRN-68). User cancellation is the
            // one exit that says nothing about the driver and keeps the pin (the catch above).
            RefusePinIfPinned(pin, exitedOnItsOwn ? "exited before health" : "never reached health");
            if (exitedOnItsOwn && mode == ParakeetLaunchMode.Auto && !cpuRetried)
            {
                // The Auto (GPU) child died before health: NOT charged, one CPU retry. The
                // retiring child's exit was voluntary from the fuse's point of view — the whole
                // point of this branch is that a driver-shaped death must not consume a strike.
                // TRN-68: a pinned child's death is the same driver-shaped death and takes the
                // same retry — bounded at Auto → pinned → Cpu.
                Logger.Warning("parakeet-server: Auto (GPU) child exited before health - retrying once on CPU (not charged to the storm fuse)");
                if (_retiring is not null)
                {
                    // The dead child has not confirmed its exit; the retry cannot spawn under
                    // the one-child invariant. Serve sherpa this preparation; the NEXT acquire
                    // starts from _launchMode (still Auto — no latch without a healthy retry).
                    Logger.Warning("parakeet-server: predecessor exit unconfirmed - CPU retry deferred to the next preparation");
                    return null;
                }
                mode = ParakeetLaunchMode.Cpu;
                cpuRetried = true;
                pin = null;
                continue;
            }
            if (exitedOnItsOwn)
            {
                RecordInvoluntaryExit();
            }
            // EVERY exit of this loop names the mode — launch failed, hung probe, throwing probe,
            // and this one (self-review couplings lens + PR #737 codex r1 Blocker 1): on a
            // toggle-OFF machine whose child never reaches health the healthy line never fires,
            // and the two probe exits above return before this line, so each has to carry it
            // itself or the log holds no Parakeet-side evidence of the OFF state.
            Logger.Warning("parakeet-server: not healthy within {Budget} (childExited={Exited}, launch mode {Mode}) - serving sherpa this preparation",
                ParakeetServerPolicy.HealthBudget, exitedOnItsOwn, mode);
            return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The cancellation escalation: kill the child, but ONLY if the caller's captured
    /// generation is still the live one — a stale caller's kill must never reach a successor
    /// (plan round, Blocker 3). An expected kill is not charged to the storm fuse.
    /// </summary>
    public async Task KillGenerationAsync(int generation, TimeSpan? gateWait = null)
    {
        // gateWait (K2): the cancellation escalation passes a bound because it runs under the
        // service lock — see ProbeGenerationAsync. A timed-out entry SKIPS the kill: the gate's
        // holder is an acquire, and any acquire mints a fresh generation, so this stale kill
        // would be refused by the compare below anyway. Unbounded (null) remains the default
        // for callers that are not holding anything.
        if (gateWait is { } bound)
        {
            if (!await _gate.WaitAsync(bound).ConfigureAwait(false))
            {
                Logger.Information(
                    "parakeet-server: kill of generation {Gen} skipped - gate busy with an acquire (stale by construction)",
                    generation);
                return;
            }
        }
        else
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        try
        {
            if (_child is { } child && _generation == generation)
            {
                // Classify BEFORE killing: a child that already exited on its own is a CRASH
                // the fuse must see - unconditionally treating the escalation as an expected
                // stop let "crash, cancel, escalate" bypass the fourth-crash cutoff forever
                // (Codex diff r1 Blocker).
                if (child.HasExited)
                {
                    RecordInvoluntaryExit();
                }
                Retire(child);
                _child = null;
                _baseUri = null;
                Logger.Information("parakeet-server: generation {Gen} killed by cancellation escalation", generation);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Race row 2's escalation probe: is the child of <paramref name="generation"/> still
    /// resident AND answering? False for a retired, killed, exited, or superseded generation —
    /// the caller kills on false, and killing a generation that is already gone is a no-op in
    /// <see cref="KillGenerationAsync"/>, so false is always the safe answer (slice-4 plan
    /// round, challenge 5b: there was no generation-bound probe surface at all).
    ///
    /// <para>Runs on its own bounded, NON-user token: the caller is escalating a cancellation,
    /// so the user's token is cancelled by definition and must not skip the probe. Holding the
    /// gate for the probe duration is deliberate — the sole concurrent callers are acquires,
    /// which must not observe (or replace) a child mid-escalation.</para>
    /// </summary>
    public async Task<bool> ProbeGenerationAsync(int generation, TimeSpan timeout)
    {
        // BOUNDED gate entry (Kimi diff r1, K2): the escalation runs while the transcription
        // service's own lock is held, and an unbounded wait here can park behind a concurrent
        // acquire's 30 s health hold. A busy gate answers TRUE — "treat as alive, do not
        // kill" — because the holder is an acquire whose completion mints a fresh generation
        // either way, so the caller's stale kill would no-op; refusing to kill on a
        // cannot-look is the safe direction.
        if (!await _gate.WaitAsync(timeout).ConfigureAwait(false))
        {
            return true;
        }
        try
        {
            if (_child is not { } child || _generation != generation || child.HasExited || _baseUri is null)
            {
                return false;
            }

            using var bound = new CancellationTokenSource(timeout);
            var probeTask = _probe(_baseUri, bound.Token);
            try
            {
                // WaitAsync bounds the AWAIT itself, same as the health loop: a probe that
                // ignores its token must not hold the gate past the budget.
                return await probeTask.WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = probeTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return false; // no answer within the budget = not healthy.
            }
            catch (Exception)
            {
                return false; // a throwing probe is a no-answer, never an escalation-stopper.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mid-session retire for the delete flow (slice-4 self-review, state F4): kill the resident
    /// child through the CONFIRM-OR-PARK path — never <see cref="ShutdownAsync"/>, whose
    /// unconditional dispose is only sound because no successor can spawn after app exit. Here a
    /// successor CAN spawn, so an unconfirmed kill parks in <c>_retiring</c> and the next
    /// acquire refuses until it is seen dead, exactly as every other retire does.
    ///
    /// <para>The gate wait is BOUNDED: the caller is the UI thread, and an acquire's health
    /// wait can hold the gate for up to 30 s. False = could not retire inside the bound —
    /// the caller aborts its delete (tombstone stays, retry later) rather than freezing.</para>
    /// </summary>
    public async Task<bool> TryRetireResidentAsync(TimeSpan gateWait)
    {
        if (!await _gate.WaitAsync(gateWait).ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            if (_retiring is { } retiring)
            {
                if (!retiring.HasExited)
                {
                    return false; // a predecessor is still dying; the delete must not proceed.
                }
                retiring.Dispose();
                _retiring = null;
            }
            if (_child is { } child)
            {
                if (child.HasExited)
                {
                    RecordInvoluntaryExit(); // observed dead first here: a crash the fuse must see.
                }
                Retire(child);
                _child = null;
                _baseUri = null;
                if (_retiring is not null)
                {
                    return false; // kill unconfirmed: parked, and the file may still be open.
                }
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Shutdown: explicit kill (the job handle's closure is the app-death backstop, not
    /// the primary path). App-EXIT only — its unconditional dispose assumes no successor can
    /// spawn; mid-session callers use <see cref="TryRetireResidentAsync"/>.
    ///
    /// <para>TRN-67: the gate acquire is BOUNDED by
    /// <see cref="ParakeetServerPolicy.ShutdownGateWait"/>, because this runs on the UI thread at
    /// app exit and the gate's long holder is an in-flight spawn's 30 s health wait. **On a miss we
    /// skip the whole body**, which costs only the ordering and the confirmation — the child is in
    /// a <c>KILL_ON_JOB_CLOSE</c> job from creation, so process exit terminates it regardless. This
    /// doc said "Graceful shutdown" until TRN-67 and the word misled: <c>Kill()</c> is
    /// <c>TerminateProcess</c>, the same class of kill the job performs. Read the old wording as
    /// "explicit rather than a side effect of process death", never as "polite".</para></summary>
    public async Task ShutdownAsync()
    {
        if (!await _gate.WaitAsync(ParakeetServerPolicy.ShutdownGateWait).ConfigureAwait(false))
        {
            // A MISS returns WITHOUT releasing — releasing a semaphore this call never took would
            // raise the count and admit two writers (the TRN-39 shape, one layer up).
            Logger.Warning(
                "parakeet-server: shutdown gate busy after {TimeoutMs} ms - skipping the explicit kill; the kill-on-close job terminates the child at process exit",
                (int)ParakeetServerPolicy.ShutdownGateWait.TotalMilliseconds);
            return;
        }
        try
        {
            if (_child is { } child)
            {
                // Shutdown disposes UNCONDITIONALLY: the app is exiting, and closing the job
                // handle is itself the kernel-level kill (KILL_ON_JOB_CLOSE) - no successor
                // can spawn after shutdown, so the retiring protocol is unnecessary here.
                child.Kill();
                _ = child.WaitForExit(ParakeetServerPolicy.RetireWait);
                child.Dispose();
                _child = null;
                _baseUri = null;
            }
            if (_retiring is { } retiringChild)
            {
                retiringChild.Dispose();
                _retiring = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>TRN-68: the dedicated adapter's driver is the suspect once a pinned attempt failed,
    /// however it failed; the next acquire serves the integrated child as before TRN-68. Idempotent
    /// and a no-op for an unpinned attempt. Always called under the gate.</summary>
    private void RefusePinIfPinned(string? pin, string how)
    {
        if (pin is null || _pinRefused)
        {
            return;
        }
        _pinRefused = true;
        Logger.Warning("parakeet-server: the child pinned to {Pin} {How} - the pin is refused for this session (TRN-68)", pin, how);
    }

    /// <summary>Kill + CONFIRM exit; a child that has not confirmed within
    /// <see cref="ParakeetServerPolicy.RetireWait"/> parks in the retiring state - handles
    /// open for observation, no successor until it is seen dead (Codex diff r2 Blocker).
    /// Always called under the gate.</summary>
    private void Retire(IParakeetServerChild child)
    {
        child.Kill();
        if (child.WaitForExit(ParakeetServerPolicy.RetireWait))
        {
            child.Dispose();
            return;
        }
        Logger.Warning("parakeet-server: kill not confirmed within {Wait} - parking in retiring state",
            ParakeetServerPolicy.RetireWait);
        _retiring = child;
    }

    private void RecordInvoluntaryExit()
    {
        var now = _utcNow();
        _involuntaryExitsUtc.Add(now);
        // Bound the ledger: entries older than the window can never influence MayRespawn again.
        _involuntaryExitsUtc.RemoveAll(t => t <= now - ParakeetServerPolicy.StormWindow);
        Logger.Warning("parakeet-server: involuntary exit recorded ({Count} in window)", _involuntaryExitsUtc.Count);
    }

}
