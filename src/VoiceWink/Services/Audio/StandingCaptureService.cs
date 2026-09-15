using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Audio;

/// <summary>
/// A recording drain's exclusive lease on the standing capture (AUD-6). Created by
/// <see cref="StandingCaptureService.TryClaim"/>; the recorder attaches its WAV sink through it
/// and MUST <see cref="Detach"/> (or Dispose) when the recording ends — detach is what releases
/// latched teardown/rebuild requests. Idempotent, and tolerant of the service being disposed
/// underneath it (tray Quit during an active recording quits immediately; DI dispose order is
/// unspecified).
/// </summary>
public sealed class StandingCaptureClaim : IDisposable
{
    private readonly StandingCaptureService _service;
    private int _detached;

    /// <summary>The hotkey-press instant (Stopwatch timestamp) audio persistence starts at.</summary>
    public long CutTimestamp { get; }

    /// <summary>How the standing capture's device was resolved at ITS start (kind + selection
    /// metadata; <c>Device</c> is always null — the MMDevice never leaves the service). Carried
    /// on the claim so the warm start can route a <c>FallbackToDefault</c> through the SAME
    /// AUD-1 one-shot notice latch the cold path uses (Kimi diff review #1 — without this, a
    /// pinned-mic fallback records the default silently on every warm recording).</summary>
    public ResolvedRecordingDevice ResolutionInfo { get; }

    internal StandingCaptureClaim(StandingCaptureService service, long cutTimestamp, ResolvedRecordingDevice resolutionInfo)
    {
        _service = service;
        CutTimestamp = cutTimestamp;
        ResolutionInfo = resolutionInfo;
    }

    /// <summary>Atomically replay ring audio from <see cref="CutTimestamp"/> into
    /// <paramref name="sink"/> and go live — see <see cref="CaptureRingBuffer.AttachSink"/>.</summary>
    public SinkAttachResult AttachSink(Action<byte[]> sink) => _service.AttachClaimSink(this, sink);

    public void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) == 1) return;
        _service.ReleaseClaim(this);
    }

    public void Dispose() => Detach();
}

/// <summary>
/// AUD-6: the standing warm capture — a stock NAudio shared-mode WASAPI capture kept RUNNING
/// while policy allows, feeding a RAM-only <see cref="CaptureRingBuffer"/>, so a hotkey press
/// starts persisting audio from the press instant with zero cold-start cost (construct/writer/
/// WASAPI-init measured 40–770 ms on the cold path, with machine-stall tails to 2.1 s).
///
/// <para><b>Ownership + guards.</b> One lifecycle semaphore serializes start/stop/rebuild; every
/// capture's event handlers are bound to a monotonic session id and self-discard when stale (the
/// recorder's proven r2 pattern). Construction goes through <see cref="BoundedComCall"/> with the
/// cold path's device-ownership transfer (timeout/OCE ⇒ the helper's abandonment continuation
/// owns the device; in-bound fault ⇒ this service still owns it).</para>
///
/// <para><b>Lifecycle latching (plan review B5).</b> While a claim is active, teardown AND
/// rebuild requests latch and execute at claim detach — a settings toggle, suspend, or device
/// churn must never yank a live recording's audio source. A mid-drain capture DEATH stops data
/// (the VM's 3 s stall warning is the surface, parity with today) but disposes nothing until
/// detach.</para>
///
/// <para><b>Endpoint gate (plan §3c).</b> The freshly resolved device is classified via
/// <see cref="EndpointClassificationReader"/>; an affirmative Bluetooth hands-free endpoint is a
/// STABLE refusal (parks, no backoff) — an open capture stream would hold the HFP link and lock
/// the headset out of A2DP for the app's whole lifetime. Fail-open on every other kind, kind
/// logged on every lifecycle line (enums only, never device names — these lines ride into
/// support bundles).</para>
///
/// <para><b>Default-device changes.</b> The service owns its own enumerator + notification
/// client, registered from an MTA thread-pool thread (NOT AudioDeviceManager's pump-less STA and
/// not the UI thread); the callback body does no COM — it only posts to the debounced rebuild
/// policy, and only when the standing device follows the system default.</para>
/// </summary>
public sealed class StandingCaptureService : IDisposable
{
    private static ILogger Logger => Log.ForContext<StandingCaptureService>();

    internal static readonly TimeSpan ConstructBound = TimeSpan.FromSeconds(1);

    // Seams (tests inject all of them; production uses the public ctor below).
    private readonly Func<CancellationToken, Task<ResolvedRecordingDevice>> _resolveDevice;
    private readonly Func<MMDevice?, IWaveIn> _captureConstructor;
    private readonly Func<MMDevice?, CaptureEndpointKind> _classifyEndpoint;
    private readonly Func<bool> _startGate;
    private readonly Func<long> _timestamp;
    private readonly long _timestampFrequency;
    private readonly Func<TimeSpan, Task> _delay;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _gate = new();
    private readonly CaptureRingBuffer _ring;

    // AUD-8 parity (the AUD-6 + AUD-8 merge, 2026-08-05): the standing capture converts with the
    // SAME shared code as the cold path, so it must apply the same anti-alias policy — warm
    // recordings aliasing while cold ones are filtered would be a silent per-path quality fork.
    // Read once at construction, mirroring AudioRecorderService's seam (the const inline in an
    // `if` would make a branch provably unreachable — CS0162).
    private readonly bool _antiAliasEnabled;

    // The LATEST user/lifecycle intent — should the standing capture be running? Stamped under
    // _gate at every intent entry (EnsureStarted/EnsureHealthy/toggle-ON ⇒ true; Stop/suspend/
    // toggle-OFF/dispose ⇒ false) BEFORE any lock wait, and revalidated at the start's final
    // publish (Codex diff r4): a stop that TIMES OUT on the lifecycle lock — because an
    // in-flight start is stalled in unbounded COM — has still recorded its intent, so the start
    // publishes nothing and disposes locally instead of leaving the microphone active after the
    // user disabled the feature. Later-ON-wins falls out of last-writer-wins under the gate.
    private bool _desiredRunning;
    private long _sessionSeq;
    // Session id whose capture died (RecordingStopped) — lets the start's final publish detect
    // a capture that died AT BIRTH (a synchronous RecordingStopped from inside StartRecording,
    // the same immediate-device-error the cold recorder guards) instead of resurrecting it as a
    // claimable session (Codex diff r3).
    private long _deadSession;
    private IWaveIn? _capture;
    private MMDevice? _device;
    private bool _running;
    private StandingCaptureClaim? _claim;
    private long _lastDataTimestamp;
    // AUD-21: 1 once this stream has delivered a single non-zero sample; 0 until then. MONOTONIC
    // within a stream and re-seeded to 0 at every start, so it answers exactly "has this stream ever
    // carried audio" — the only question that separates a stuck capture client from a gating
    // endpoint's ordinary between-dictation silence (see StandingCapturePolicy.ClassifyClaim).
    // Interlocked/Volatile rather than _gate-only, mirroring _lastDataTimestamp: the capture thread
    // writes it and the UI thread reads it at claim time.
    private int _sawNonZeroData;
    // AUD-21: when this stream started, so a stream too young to have proven anything is not
    // condemned by an empty record. Stamped beside _lastDataTimestamp's grace seed.
    private long _streamStartTimestamp;
    private int _consecutiveFailures;
    private bool _parked;
    private bool _rebuildScheduled;
    private string? _latchedTeardownReason;
    private string? _latchedRebuildReason;
    private bool _disposed;
    private RecordingDeviceResolutionKind _deviceKind;
    private CaptureEndpointKind _endpointKind;
    // Pre-first-resolution seed: nothing has been resolved yet, so the tag is honestly unknown
    // rather than a placeholder that could be mistaken for a real endpoint.
    private ResolvedRecordingDevice _lastResolutionInfo =
        new(null, RecordingDeviceResolutionKind.SystemDefault, null, null, Helpers.CaptureDeviceTag.Unknown, null);
    private readonly bool _enableDeviceNotifications;
    private MMDeviceEnumerator? _notifyEnumerator;
    private DefaultDeviceNotificationClient? _notifyClient;
    // First-N bound on the standing data-error log: pre-AUD-6 a decode fault could only log
    // during a recording; on an always-on stream it would log at driver rate for the app's
    // lifetime (Kimi diff review #6).
    private int _dataErrorCount;

    /// <summary>Production wiring: AUD-1 device resolution, stock WasapiCapture, the real
    /// classifier read, Stopwatch clock, real default-device notifications.</summary>
    public StandingCaptureService(RecordingDeviceSelectionService deviceSelection, Func<bool> startGate)
        : this(
            deviceSelection.ResolveForRecordingAsync,
            device => device != null ? new WasapiCapture(device) : new WasapiCapture(),
            EndpointClassificationReader.Classify,
            startGate,
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            Task.Delay,
            enableDeviceNotifications: true,
            antiAliasEnabled: CaptureFilterFeature.IsEnabled)
    {
    }

    internal StandingCaptureService(
        Func<CancellationToken, Task<ResolvedRecordingDevice>> resolveDevice,
        Func<MMDevice?, IWaveIn> captureConstructor,
        Func<MMDevice?, CaptureEndpointKind> classifyEndpoint,
        Func<bool> startGate,
        Func<long> timestamp,
        long timestampFrequency,
        Func<TimeSpan, Task> delay,
        // Default OFF for the internal (test) ctor: registration creates a REAL
        // MMDeviceEnumerator, which unit tests must never touch.
        bool enableDeviceNotifications = false,
        // AUD-8 lever, mirrored from AudioRecorderService: the production ctor passes the const;
        // tests default OFF (their fakes feed 16 kHz mono, where the downsampler is bit-exact
        // passthrough anyway) and opt in to pin the wiring.
        bool antiAliasEnabled = false)
    {
        _enableDeviceNotifications = enableDeviceNotifications;
        _antiAliasEnabled = antiAliasEnabled;
        _resolveDevice = resolveDevice;
        _captureConstructor = captureConstructor;
        _classifyEndpoint = classifyEndpoint;
        _startGate = startGate;
        _timestamp = timestamp;
        _timestampFrequency = timestampFrequency;
        _delay = delay;
        _ring = new CaptureRingBuffer(timestampFrequency);
    }

    /// <summary>What the constructor stored — the AUD-8 gate test asserts the const reached the
    /// field through the PUBLIC ctor, mirroring the recorder's identical seam.</summary>
    internal bool AntiAliasEnabledForTesting => _antiAliasEnabled;

    /// <summary>The AUD-8 lever's decision for a standing session — the INSTANCE method the start
    /// path calls, mirroring <c>AudioRecorderService.CreateDownsamplerFor</c> and pinned the same
    /// way (a test-reassembled composition would not catch a hard-coded call site).</summary>
    internal CaptureDownsampler? CreateDownsamplerFor(int sourceRate) =>
        _antiAliasEnabled ? new CaptureDownsampler(sourceRate, CaptureFormatConverter.TargetSampleRate) : null;

    public bool IsRunning { get { lock (_gate) return _running; } }

    /// <summary>
    /// Claim the standing capture for a recording, or null when the caller must go cold
    /// (not running, already claimed, the stream stopped delivering, or — AUD-21 — it has delivered
    /// nothing but exact zeros since it started). The stale AND silent cases both post a rebuild so
    /// a stream that failed without raising RecordingStopped heals instead of disabling the feature
    /// forever (final check R2). Refusals log the verdict; numbers/enums only.
    /// </summary>
    public StandingCaptureClaim? TryClaim(long cutTimestamp)
    {
        string verdictLabel;
        lock (_gate)
        {
            if (_disposed) return null;

            var now = _timestamp();
            var ageMs = TicksToMs(now - Interlocked.Read(ref _lastDataTimestamp));
            var streamAgeMs = TicksToMs(now - Interlocked.Read(ref _streamStartTimestamp));
            var verdict = StandingCapturePolicy.ClassifyClaim(
                _running, _claim != null, ageMs,
                everDeliveredNonZero: Volatile.Read(ref _sawNonZeroData) != 0,
                streamAgeMs: streamAgeMs);
            if (verdict == StandingClaimVerdict.Claimable)
            {
                var claim = new StandingCaptureClaim(this, cutTimestamp, _lastResolutionInfo);
                _claim = claim;
                return claim;
            }

            if (verdict == StandingClaimVerdict.StaleData)
            {
                _running = false; // a stalled stream is not live; later claims say NotRunning
                ScheduleRebuildLocked("stale-health", resetFailures: false);
            }
            else if (verdict == StandingClaimVerdict.SilentStream)
            {
                // AUD-21. Two deliberate differences from the StaleData branch above:
                //
                // `_running` is NOT cleared. This stream genuinely IS running and delivering on
                // time — it is the AUDIO that is dead — and the flag's only production reader is
                // App's warm-up branch, for which `true` is the correct answer. Clearing it would
                // relabel every later refusal NotRunning and misreport a lifecycle state the
                // rebuild is about to replace. Test-pinned, because "harmonizing" the two branches
                // is the obvious future edit and it would silently reverse this.
                //
                // The rebuild is what heals a genuinely stuck client (a fresh WASAPI client is
                // exactly what an app restart provided in the 2026-08-25 incident). Churn is
                // bounded WITHOUT a special case: the verdict only exists inside TryClaim, so it is
                // user-paced at one hotkey press each, and ScheduleRebuildLocked's _rebuildScheduled
                // absorption coalesces repeats.
                //
                // Park-ladder honesty (self-review L3 corrected the first version of this comment):
                // what actually keeps a permanently-muted-but-healthy machine out of the park ladder
                // is the PUBLISH-TIME reset — a successful start zeroes _consecutiveFailures in the
                // same block that sets _running, and SilentStream is only reachable while _running
                // is true, so the counter is provably 0 whenever this branch runs. A muted mic's
                // rebuilds START fine (mute is an endpoint property, not a start failure) and reset
                // the counter every time. `resetFailures: false` is therefore a no-op here, kept for
                // symmetry with StaleData; the ladder still engages on the one real path — a rebuild
                // whose START throws — which is the ladder doing its job.
                ScheduleRebuildLocked("silent-stream", resetFailures: false);
            }
            verdictLabel = verdict.ToString();
        }

        Logger.Information("Standing capture claim refused ({Verdict}) — recording starts cold", verdictLabel);
        return null;
    }

    internal SinkAttachResult AttachClaimSink(StandingCaptureClaim claim, Action<byte[]> sink)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_claim, claim))
                throw new InvalidOperationException("Claim is no longer current.");
        }
        return _ring.AttachSink(claim.CutTimestamp, sink);
    }

    internal void ReleaseClaim(StandingCaptureClaim claim)
    {
        // Ring detach first: live delivery must stop before any deferred teardown disposes the
        // capture. Safe on a disposed service — the ring outlives the COM objects.
        _ring.DetachSink();

        string? teardown, rebuild;
        lock (_gate)
        {
            if (!ReferenceEquals(_claim, claim)) return;
            _claim = null;
            teardown = _latchedTeardownReason;
            rebuild = _latchedRebuildReason;
            _latchedTeardownReason = null;
            _latchedRebuildReason = null;
            if (_disposed) return;
        }

        // Deferred lifecycle work runs OFF the caller's thread (detach happens on the recording
        // stop path). Teardown wins over rebuild — an explicit stop request is a stronger intent.
        if (teardown != null)
            _ = RunLoggedAsync(() => StopAsync(teardown), "deferred teardown");
        else if (rebuild != null)
            ScheduleRebuild(rebuild, resetFailures: false);
    }

    /// <summary>Settings toggle. ON starts (subject to the gate), OFF tears down —
    /// latched until detach when a recording drain is active (B5).</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
            _ = RunLoggedAsync(() => EnsureStartedAsync("setting-on"), "setting-on start");
        else
            _ = RunLoggedAsync(() => StopAsync("setting-off"), "setting-off teardown");
    }

    /// <summary>The AUD-1 microphone selection changed — rebuild onto the new selection.</summary>
    public void NotifyDeviceSelectionChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _parked = false;
            _consecutiveFailures = 0;
        }
        ScheduleRebuild("selection-change", resetFailures: true);
    }

    /// <summary>
    /// AUD-21 (self-review L1): a completed recording measured ALL-ZERO — the strongest possible
    /// evidence the capture chain is delivering silence, far stronger than anything the stream's
    /// own recency can say. Re-arms the from-birth silence record and posts a rebuild, which closes
    /// the one flavor the birth rule alone cannot see: a stream that latched silent AFTER once
    /// carrying audio keeps its monotonic flag forever, so its warm claims stay granted and every
    /// recording is zeros until process restart. With this hook the SECOND dictation works in every
    /// latch flavor: the re-armed flag refuses the next claim (cold path, fresh client) and the
    /// rebuild replaces the stream, which then proves itself on live speech or keeps refusing.
    ///
    /// <para>Safe on weaker evidence too: a COLD recording of zeros (muted endpoint) re-arms a
    /// stream that is itself seeing zeros, so the refusal it causes is truthful; a user who
    /// recorded genuine silence in a hard-gated room costs one cold start and the young-window
    /// alternation already recorded as the design's residual. User-paced, absorbed by
    /// _rebuildScheduled, and a no-op when parked, stopped, or disposed.</para>
    /// </summary>
    public void NotifyRecordingWasDigitallySilent()
    {
        lock (_gate)
        {
            if (_disposed || _parked || !_running) return;
            Interlocked.Exchange(ref _sawNonZeroData, 0);
            ScheduleRebuildLocked("digitally-silent-recording", resetFailures: false);
        }
    }

    /// <summary>System suspend — release the device before sleep (latched while claimed).</summary>
    public void NotifySuspend()
        => _ = RunLoggedAsync(() => StopAsync("suspend"), "suspend teardown");

    /// <summary>
    /// Wake-path entry (startup warm-up, resume, session unlock — the ARM64 modern-standby wake
    /// signal where PowerModeChanged.Resume never fires). Healthy running stream ⇒ no-op (this is
    /// what makes a per-unlock call cheap on machines whose stream survived the lock); running
    /// but STALE ⇒ the stream died silently across the sleep — rebuild; parked ⇒ stays parked
    /// (the park's cause — a BT endpoint, no mic — is unchanged by a wake; device-change events
    /// are what unpark); stopped ⇒ start, subject to the gate.
    /// </summary>
    public async Task EnsureHealthyAsync(string reason)
    {
        // A wake is only a "should be running" signal WHEN THE GATE AGREES (Kimi diff r4
        // blocker): a lock/unlock is not user intent, and without this check it erased an
        // explicit setting-OFF latched during a recording — clearing the teardown latch and
        // re-stamping desired-running, leaving the microphone held for the rest of the session
        // with the feature off. Gate-refused ⇒ touch NOTHING (latch and intent survive).
        //
        // Evaluated INSIDE the _gate hold, atomically with the intent mutation (Codex diff r5):
        // a pre-lock read let a wake capture a stale `true`, pause, and apply it AFTER a
        // concurrent OFF had latched — erasing the OFF anyway. Under the hold, either the OFF
        // ran first (the setting is persisted before SetEnabled fires StopAsync, so this read
        // sees the gate closed and touches nothing) or the wake ran first and the later OFF
        // stamps over it — later-OFF always wins. This fence is the wake's ONLY intent stamp:
        // the stopped-stream branch below starts through StartWithoutRestampAsync, never
        // EnsureStartedAsync, whose entry stamp outside this fence was the r6 hole (an OFF
        // between the two stamps was erased by the second).
        //
        // Lock-hold honesty (Codex diff r6 corrected an earlier overclaim here): the settings
        // read is an in-memory cache, and the license status is cached — but its FIRST-USE
        // hardware-fingerprint initialization can touch registry/WMI. In practice it is warm
        // long before any wake (the hotkey recording gate evaluates it per press) and it is
        // lifetime-cached after; neither service calls back into this one (one-way dependency,
        // no lock inversion — grep-verified at review). If a future gate source adds real I/O,
        // this in-lock placement must be revisited.
        var stale = false;
        lock (_gate)
        {
            if (_disposed) return;
            bool gateOpen;
            try { gateOpen = _startGate(); }
            catch (Exception ex)
            {
                // Fail closed, but not silently: if the gate closure ever throws persistently,
                // wake-healing goes inert and this is the only trace of why (Kimi diff r6).
                // Warning, not Debug — the file sink's floor is Information, so a Debug line
                // never reaches the support bundle this exists for (Kimi diff r7).
                Logger.Warning(ex, "Standing capture wake gate check threw — wake ignored");
                return;
            }
            if (!gateOpen) return;
            // Latest intent wins (Codex diff r3): a GATED wake cancels a teardown latched
            // during the claim (suspend while recording), or the resume would be silently
            // discarded at detach and nothing would ever restart the feature. Cleared even
            // when parked (harmless — a park has no latches to honor).
            _latchedTeardownReason = null;
            _desiredRunning = true;
            if (_parked) return;
            if (_running)
            {
                var ageMs = TicksToMs(_timestamp() - Interlocked.Read(ref _lastDataTimestamp));
                if (ageMs <= StandingCapturePolicy.MaxClaimDataAgeMs) return;
                _running = false;
                stale = true;
            }
        }

        if (stale)
        {
            ScheduleRebuild($"wake-stale:{reason}", resetFailures: true);
            return;
        }

        // Test seam (Codex r7): awaited between the wake's fenced stamp and the start
        // execution — the exact window the r6 fix closes — so the regression test can
        // interleave an OFF deterministically instead of hoping for a scheduling loss.
        if (WakeFenceExitedForTest is { } fenceExited)
            await fenceExited().ConfigureAwait(false);

        // Deliberately NOT EnsureStartedAsync (Codex diff r6): its entry stamp sits outside
        // this method's fence, and re-stamping here erased an OFF that landed between the two.
        // The wake's intent was fenced above; from here the start must only EXECUTE it — the
        // gate re-check in StartCoreLockedAsync and the intent re-validation at the final
        // publish still catch a later OFF.
        await StartWithoutRestampAsync(reason).ConfigureAwait(false);
    }

    /// <summary>See the call site in <see cref="EnsureHealthyAsync"/>. Test-only; never set in
    /// production.</summary>
    internal Func<Task>? WakeFenceExitedForTest { get; set; }

    /// <summary>Test-only read of the latest intent. The r6 regression test's DISCRIMINATOR:
    /// end-state assertions cannot tell the fixed and vulnerable forms apart (a queued OFF
    /// repairs the vulnerable form's bad publish after the fact), but the intent bit can — on
    /// the vulnerable form the wake's post-fence re-stamp flips it back to true.</summary>
    internal bool DesiredRunningForTest { get { lock (_gate) return _desiredRunning; } }

    /// <summary>
    /// Start the standing capture if the gate allows. Idempotent; the USER-INTENT entry — its
    /// only caller besides tests is <see cref="SetEnabled"/>(true), so the unconditional intent
    /// stamp here is always genuine "should be running" (wake paths enter through
    /// <see cref="EnsureHealthyAsync"/>, whose stamp is gate-fenced, and continue through
    /// <see cref="StartWithoutRestampAsync"/> so they can never re-stamp past their fence —
    /// Codex diff r6). A gate refusal or a Bluetooth-hands-free endpoint PARKS (stable, no
    /// backoff); a start failure schedules a backoff rebuild until the park threshold.
    /// </summary>
    public async Task EnsureStartedAsync(string reason)
    {
        // Latest intent wins (Codex diff r3): a "should be running" entry cancels a LATCHED
        // teardown — OFF→ON (or suspend→resume) during a claim must not tear the service down
        // at detach on the strength of the SUPERSEDED request. A latched REBUILD survives (it
        // agrees with "running"). The desired-running stamp is what the start's final publish
        // revalidates (Codex diff r4).
        lock (_gate)
        {
            _latchedTeardownReason = null;
            _desiredRunning = true;
        }

        await StartWithoutRestampAsync(reason).ConfigureAwait(false);
    }

    /// <summary>The lifecycle-locked start EXECUTION, with no intent mutation — the shared tail
    /// of the user-intent entry (which stamps) and the wake entry (whose stamp is fenced inside
    /// <see cref="EnsureHealthyAsync"/> and must not be repeated after it).</summary>
    private async Task StartWithoutRestampAsync(string reason)
    {
        if (!await _lifecycleLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            Logger.Warning("Standing capture start ({Reason}) skipped — lifecycle lock busy", reason);
            return;
        }
        try
        {
            await StartCoreLockedAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Tear the standing capture down (latched until detach while claimed). The
    /// pre-lock check below is a FAST PATH only — the authoritative claim re-check happens
    /// inside <see cref="StopCoreLocked"/>, atomically with the session invalidation, because a
    /// hotkey claim can land in the window between this check and the lifecycle-lock
    /// acquisition (Codex diff r2 blocker: the B5 guarantee must not have a TOCTOU).</summary>
    public async Task StopAsync(string reason)
    {
        lock (_gate)
        {
            // Intent recorded FIRST, unconditionally (Codex diff r4): even if the lock wait
            // below times out behind a COM-stalled start, that start's final publish reads this
            // and abandons — the stop's effect survives its own timeout.
            _desiredRunning = false;

            if (StandingCapturePolicy.DecideLifecycleRequest(_claim != null)
                == StandingLifecycleDecision.LatchUntilDetach)
            {
                _latchedTeardownReason = reason;
                Logger.Information("Standing capture teardown ({Reason}) latched until recording detach", reason);
                return;
            }
        }

        if (!await _lifecycleLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            Logger.Warning("Standing capture stop ({Reason}) timed out on the lifecycle lock — intent recorded; an in-flight start will not publish", reason);
            return;
        }
        try
        {
            StopCoreLocked(reason, LatchKind.Teardown);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>What a lifecycle request does when <see cref="StopCoreLocked"/> finds a claim
    /// attached at its atomic re-check.</summary>
    private enum LatchKind
    {
        Teardown,
        Rebuild,
        /// <summary>Dispose: proceeds even under a claim — quitting must not hang; the claim's
        /// detach tolerates the disposed service (10c).</summary>
        None,
    }

    private async Task StartCoreLockedAsync(string reason)
    {
        if (_disposed) return;
        lock (_gate) { if (_running) return; }

        // The start side of B5 (Kimi diff r3 blocker): a capture that died MID-DRAIN leaves
        // _claim attached and the dead _capture/_device still referenced. Starting over them
        // would splice a NEW capture's audio into the old recording's still-attached sink and
        // overwrite the dead COM objects without disposal (a mic-indicator-holding leak).
        // StopCoreLocked's atomic re-check owns both cases: claimed ⇒ the start latches as a
        // REBUILD and runs at detach (teardown-still-wins preserved); unclaimed stale refs ⇒
        // disposed before re-construction. A genuinely fresh start passes through as a no-op.
        if (!StopCoreLocked($"pre-start:{reason}", LatchKind.Rebuild))
            return;

        // Intent re-check BEFORE any construction (Kimi r5's offered hardening, folded into the
        // r6 refactor): the publish re-validation remains the race-safe net, but refusing here
        // spares the transient construct-then-abandon mic blink when a stop/suspend intent is
        // already recorded (e.g. a device-churn rebuild racing a suspend). Logged for parity
        // with every other early return in this method (Kimi diff r7).
        lock (_gate)
        {
            if (!_desiredRunning)
            {
                Logger.Information("Standing capture start refused ({Reason}) — stop/suspend intent already recorded", reason);
                return;
            }
        }

        if (!_startGate())
        {
            Logger.Information("Standing capture not started ({Reason}) — gate refused (setting/onboarding/legal/license)", reason);
            return;
        }

        // Register device notifications on the first gate-passing ATTEMPT, not on the first
        // SUCCESS (Codex diff r1 blocker): a no-microphone park and a Bluetooth refusal both
        // happen before any success, and the notification is exactly the signal that can
        // un-park them (a mic plugged in, the default moved off the BT headset).
        EnsureNotificationClient();

        ResolvedRecordingDevice resolution;
        try
        {
            resolution = await _resolveDevice(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnStartFailure(reason, "device-resolve", ex);
            return;
        }

        var kind = _classifyEndpoint(resolution.Device);
        if (!StandingCapturePolicy.IsEndpointStandable(kind))
        {
            // STABLE refusal — parks, never backs off (plan §3c). Re-kicked by selection or
            // default-device change. The claim path goes cold, i.e. today's behavior.
            try { resolution.Device?.Dispose(); }
            catch (Exception ex) { Logger.Debug(ex, "Disposing refused endpoint's device failed"); }
            lock (_gate) { _parked = true; }
            Logger.Information("Standing capture refused (kind={Kind}) — recordings start cold on this endpoint", kind);
            return;
        }

        var device = resolution.Device;
        IWaveIn? capture = null;
        var deviceOwnedHere = true;
        try
        {
            try
            {
                capture = await BoundedComCall.RunBoundedAsync(
                    () => _captureConstructor(device),
                    ConstructBound,
                    "standing-NewWasapiCapture",
                    dependency: device,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ownership transferred to BoundedComCall's abandonment continuation.
                deviceOwnedHere = false;
                throw;
            }

            if (capture == null)
            {
                // Timeout: same ownership transfer as the cold path's construct.
                deviceOwnedHere = false;
                throw new AudioCaptureUnavailableException(
                    "Standing capture construction timed out — audio service busy.");
            }

            var format = capture.WaveFormat;
            if (!CaptureFormatConverter.IsSupportedFormat(format))
            {
                // Refused BEFORE subscription (Kimi diff r2 #2): an unsupported negotiated format
                // would make the converter's per-buffer "Unsupported audio format" warning fire
                // at driver rate for the app's lifetime while the ring receives nothing. A
                // useless stream must fail the start (backoff → park) so claims go cold instead.
                throw new AudioCaptureUnavailableException(
                    $"Standing capture format unsupported: {format.Encoding} {format.BitsPerSample}bit");
            }

            var session = Interlocked.Increment(ref _sessionSeq);
            // AUD-8 parity: one downsampler per standing session, bound into the closure exactly
            // like the recorder's per-attempt instance — a stale callback can never reach a newer
            // session's filter state, and the ring receives the SAME filtered bytes a cold
            // recording would write.
            var downsampler = CreateDownsamplerFor(format.SampleRate);
            capture.DataAvailable += (s, e) => OnDataAvailable(session, format, downsampler, e);
            capture.RecordingStopped += (s, e) => OnCaptureStopped(session, e);

            _ring.Clear();
            // Grace for the first buffers: a claim in the first second after start is allowed
            // even before DataAvailable fires (the drain then starts at the first live chunk,
            // at most one delivery period after the cut — same head as a cold start's best case).
            Interlocked.Exchange(ref _lastDataTimestamp, _timestamp());
            // AUD-21: the silence record belongs to THIS stream, so both fields re-seed here. This
            // runs BEFORE StartRecording(), so no callback of the new session can precede the reset;
            // and the last possible OLD-session write strictly precedes StopCoreLocked's session
            // bump, which strictly precedes reaching this line again — so the unlocked reset cannot
            // race a stale setter.
            Interlocked.Exchange(ref _sawNonZeroData, 0);
            Interlocked.Exchange(ref _streamStartTimestamp, _timestamp());

            capture.StartRecording();

            // Final publish is CONDITIONAL (Codex diff r3): a synchronous RecordingStopped from
            // inside StartRecording (immediate device error — the cold recorder guards the same
            // case) marks the session dead, and a Dispose that timed out on the lifecycle lock
            // marks the service disposed; publishing either would resurrect a dead or
            // post-disposal capture as claimable.
            var published = false;
            lock (_gate)
            {
                if (!_disposed && _deadSession != session && _desiredRunning)
                {
                    _capture = capture;
                    _device = device;
                    _running = true;
                    _parked = false;
                    _consecutiveFailures = 0;
                    _deviceKind = resolution.Kind;
                    _endpointKind = kind;
                    // Kind + selection metadata only — the MMDevice itself never rides a claim.
                    // AUD-16: the tag MUST be copied through. It is the only device identity the
                    // warm path has (the MMDevice is dropped here by design), and the warm path is
                    // the default one, so losing it here would silently degrade every instant
                    // recording to `dev=unknown` — which is exactly why `DeviceTag` has no default
                    // value on the record: omitting it is a compile error, not a quiet regression.
                    // AUD-18: the endpoint id copies through for the same reason as the tag —
                    // the warm effects line addresses its query with it, in-process only.
                    _lastResolutionInfo = new ResolvedRecordingDevice(
                        null, resolution.Kind, resolution.SelectedId, resolution.SelectedName,
                        resolution.DeviceTag, resolution.EndpointId);
                    published = true;
                }
            }

            if (!published)
            {
                try { capture.Dispose(); }
                catch (Exception dex) { Logger.Debug(dex, "Disposing unpublishable standing capture threw"); }
                if (deviceOwnedHere)
                {
                    try { device?.Dispose(); }
                    catch (Exception dex) { Logger.Debug(dex, "Disposing unpublishable standing device threw"); }
                }
                Logger.Information("Standing capture start abandoned at publish ({Reason}) — capture died at birth, intent turned off, or service disposed", reason);
                // Died-at-birth must feed the FAILURE ladder: the death callback's own rebuild
                // schedule carries no failure count, and a device erroring on every start would
                // otherwise rebuild at the bare 500 ms debounce forever instead of backing off
                // and parking. (The double schedule is absorbed by the scheduled flag.) An
                // intent-off abandon deliberately does NOT count as a failure — the user asked
                // for exactly this outcome, and a later ON starts fresh.
                bool diedAtBirth;
                lock (_gate) { diedAtBirth = !_disposed && _deadSession == session; }
                if (diedAtBirth)
                {
                    OnStartFailure(reason, "died-at-birth",
                        new AudioCaptureUnavailableException("Standing capture stopped immediately after start."));
                }
                return;
            }

            Logger.Information("Standing capture: started ({Reason}, device={DeviceKind}, kind={EndpointKind}, dev={DeviceTag})",
                reason, resolution.Kind, kind, resolution.DeviceTag);
        }
        catch (Exception ex)
        {
            try { capture?.Dispose(); }
            catch (Exception dex) { Logger.Debug(dex, "Disposing failed standing capture threw"); }
            if (deviceOwnedHere)
            {
                try { device?.Dispose(); }
                catch (Exception dex) { Logger.Debug(dex, "Disposing standing capture device threw"); }
            }
            OnStartFailure(reason, "construct-start", ex);
        }
    }

    private void OnStartFailure(string reason, string phase, Exception ex)
    {
        int failures;
        bool parked;
        lock (_gate)
        {
            failures = ++_consecutiveFailures;
            parked = StandingCapturePolicy.ShouldParkAfterFailures(failures);
            _parked = parked;
        }

        Logger.Warning("Standing capture start failed ({Reason}, phase={Phase}, failures={Failures}): {ExType}",
            reason, phase, failures, ex.GetType().Name);

        if (parked)
        {
            Logger.Information("Standing capture parked after {Failures} consecutive failures — will retry on device change", failures);
            return;
        }

        ScheduleRebuild($"retry-{phase}", resetFailures: false);
    }

    /// <summary>Returns false when the teardown LATCHED instead of running (a claim was attached
    /// at the atomic re-check) — callers like <see cref="RebuildAsync"/> must then skip their
    /// follow-up start.</summary>
    private bool StopCoreLocked(string reason, LatchKind latchKind)
    {
        IWaveIn? capture;
        MMDevice? device;
        lock (_gate)
        {
            // AUTHORITATIVE claim re-check, atomic with the session invalidation (Codex diff r2
            // blocker): the callers' pre-lock checks race a hotkey claim that lands before the
            // lifecycle lock is acquired — the B5 "never yank a live drain's source" guarantee
            // is only real if the decision and the invalidation share one _gate hold. TryClaim
            // takes the same lock and requires _running, so past this block no new claim can
            // attach to the session being torn down.
            if (_claim != null && latchKind != LatchKind.None)
            {
                if (latchKind == LatchKind.Teardown) _latchedTeardownReason = reason;
                else _latchedRebuildReason ??= reason;
                Logger.Information("Standing capture {Kind} ({Reason}) latched at the atomic re-check — claim attached",
                    latchKind, reason);
                return false;
            }

            // Invalidate in-flight handlers first: after this, no callback may touch state.
            Interlocked.Increment(ref _sessionSeq);
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
            _running = false;

            // Ring cleared INSIDE the same hold (Codex diff r2 blocker): OnDataAvailable's
            // append is gate-held with a session re-check, so a stale in-flight callback either
            // appended BEFORE this block (its chunk dies with the ring here) or re-checks AFTER
            // it (session mismatch, discarded) — old-device audio can never land in the
            // replacement session's ring.
            _ring.Clear();
        }

        if (capture != null)
        {
            try { capture.StopRecording(); }
            catch (Exception ex) { Logger.Debug(ex, "Standing capture StopRecording threw during teardown"); }
            try { capture.Dispose(); }
            catch (Exception ex) { Logger.Debug(ex, "Standing capture Dispose threw during teardown"); }
        }
        if (device != null)
        {
            try { device.Dispose(); }
            catch (Exception ex) { Logger.Debug(ex, "Standing capture device Dispose threw during teardown"); }
        }

        if (capture != null || device != null)
            Logger.Information("Standing capture: stopped ({Reason})", reason);
        return true;
    }

    private void OnDataAvailable(long session, WaveFormat format, CaptureDownsampler? downsampler, WaveInEventArgs e)
    {
        if (Interlocked.Read(ref _sessionSeq) != session) return;

        // Stamp at ARRIVAL, before conversion (Codex diff r1): the stamp approximates when the
        // chunk's audio ENDED, and every µs of work before it skews the cut-point model late.
        // The model stays arrival-based — a delivery burst after a scheduler stall still stamps
        // old audio "now", which is why the pre-press bound is documented as typical-case, not
        // absolute (see CaptureRingBuffer's doc + legal addendum A11).
        var now = _timestamp();

        try
        {
            var converted = CaptureFormatConverter.ConvertToTarget(e.Buffer, e.BytesRecorded, format, downsampler);
            if (converted.Length == 0) return;

            // AUD-21: the has-this-stream-ever-carried-audio decision lives INSIDE the gate below —
            // the whole test-and-set must be atomic with NotifyRecordingWasDigitallySilent's clear
            // (Codex diff r1 Blocker): computed out here, a zero-chunk callback that read flag=1 and
            // then blocked on the gate while the notify cleared it would write 1 back on entry,
            // undoing the re-arm and letting one more silent warm recording through before the
            // rebuild lands. The flag check short-circuits the scan, so a stream that has carried
            // audio pays a single int read per chunk; "before first sound" pays the vectorized
            // IndexOfAnyExcept — ~320 B per driver period, no allocation (ConvertToTarget returns a
            // fresh buffer this callback exclusively owns), negligible inside the hold. Scanned
            // POST-conversion deliberately: these are the bytes that reach the ring and the WAV.

            // Guard and mutation share ONE _gate hold (Codex diff r2 blocker): the top-of-method
            // check races teardown across the conversion above — a stale callback passing it
            // could otherwise append OLD-device audio into the ring AFTER StopCoreLocked cleared
            // it, i.e. into the REPLACEMENT session. StopCoreLocked bumps the session and clears
            // the ring under this same lock, so here it is either before that (chunk dies with
            // the clear) or after (mismatch, discarded). Lock order gate→ring; nothing nests
            // ring→gate (ReleaseClaim's detach-then-gate is sequential, not nested).
            lock (_gate)
            {
                if (Interlocked.Read(ref _sessionSeq) != session) return;
                _ring.Append(now, converted);
                Interlocked.Exchange(ref _lastDataTimestamp, now);
                // AUD-21: read-scan-set as one atom under the gate (see the comment above the lock),
                // after the same session re-check as the ring append — so a stale callback that
                // loses the re-check can never stamp a replacement session's stream, and a
                // digitally-silent-recording notice can never be un-done by an in-flight zero chunk.
                if (Volatile.Read(ref _sawNonZeroData) == 0
                    && converted.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    Volatile.Write(ref _sawNonZeroData, 1);
            }
        }
        catch (Exception ex)
        {
            // A throwing SINK surfaces here too (ring delivery is inline). The recorder's sink
            // owns its own ObjectDisposedException tolerance; anything else is logged and the
            // chunk dropped — the standing stream must survive a drain-side fault. First-N
            // bounded: on an always-on stream a per-buffer Error would otherwise log at driver
            // rate for the app's lifetime (Kimi diff r1 #6).
            var count = Interlocked.Increment(ref _dataErrorCount);
            if (count <= 3 || count % 1000 == 0)
                Logger.Error(ex, "Error processing standing capture audio data (occurrence {Count})", count);
        }
    }

    private void OnCaptureStopped(long session, StoppedEventArgs e)
    {
        if (Interlocked.Read(ref _sessionSeq) != session)
        {
            Logger.Debug("Stale standing RecordingStopped callback discarded");
            return;
        }

        lock (_gate)
        {
            // Re-checked under the SAME hold that mutates (Codex diff r2 blocker): a rebuild can
            // start a NEWER session between the check above and this block, and a stale death
            // notice flipping _running would kill the healthy replacement — the exact failure
            // shape the recorder's r2 guard exists to prevent.
            if (Interlocked.Read(ref _sessionSeq) != session)
            {
                Logger.Debug("Stale standing RecordingStopped callback discarded (lost the re-check race)");
                return;
            }
            // Marks a capture that died AT BIRTH (synchronous RecordingStopped from inside
            // StartRecording) so the start's conditional publish refuses it (Codex diff r3).
            _deadSession = session;
            _running = false;
            if (_disposed) return;
        }

        if (e.Exception != null)
            Logger.Warning("Standing capture: dead (RecordingStopped, {ExType})", e.Exception.GetType().Name);
        else
            Logger.Information("Standing capture: dead (RecordingStopped, no exception)");

        // The dead capture's COM objects are torn down by the rebuild (StopCore runs first
        // inside it). While a drain is attached the request latches (B5) — data has stopped, the
        // VM's stall warning covers the surface, and nothing is disposed under the drain.
        ScheduleRebuild("capture-death", resetFailures: false);
    }

    private void ScheduleRebuild(string reason, bool resetFailures)
    {
        lock (_gate)
        {
            ScheduleRebuildLocked(reason, resetFailures);
        }
    }

    private void ScheduleRebuildLocked(string reason, bool resetFailures)
    {
        if (_disposed) return;
        if (resetFailures) { _consecutiveFailures = 0; _parked = false; }
        if (_parked) return;

        if (_claim != null)
        {
            // B5: latch — runs at detach (teardown, if also latched, wins there).
            _latchedRebuildReason ??= reason;
            Logger.Information("Standing capture rebuild ({Reason}) latched until recording detach", reason);
            return;
        }

        if (_rebuildScheduled) return;
        _rebuildScheduled = true;

        var delay = StandingCapturePolicy.NextRebuildDelay(_consecutiveFailures);
        Logger.Information("Standing capture rebuild scheduled ({Reason}, delay={DelayMs}ms, failures={Failures})",
            reason, (int)delay.TotalMilliseconds, _consecutiveFailures);

        _ = RunLoggedAsync(async () =>
        {
            await _delay(delay).ConfigureAwait(false);
            lock (_gate) { _rebuildScheduled = false; }
            await RebuildAsync(reason).ConfigureAwait(false);
        }, "scheduled rebuild");
    }

    private async Task RebuildAsync(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_claim != null)
            {
                _latchedRebuildReason ??= reason;
                return;
            }
        }

        if (!await _lifecycleLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            Logger.Warning("Standing capture rebuild ({Reason}) skipped — lifecycle lock busy", reason);
            return;
        }
        try
        {
            // A claim that landed between this method's pre-lock check and here latches the
            // rebuild at the atomic re-check (returns false) — the follow-up start must not run
            // either, or it would double-start against the still-claimed stream.
            if (!StopCoreLocked($"rebuild:{reason}", LatchKind.Rebuild))
                return;
            await StartCoreLockedAsync($"rebuild:{reason}").ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Register for device notifications, once, at the first gate-passing start ATTEMPT —
    /// deliberately NOT the first success (Codex diff r1: parks happen before any success, and
    /// the notification is exactly what un-parks them); a failed registration clears the client
    /// so the next attempt retries. Runs on an MTA thread-pool thread — AudioDeviceManager's STA
    /// pumps no messages (its own header records the notification-deadlock history), and the UI
    /// thread is out for the same reason. The callback body does no COM: it only posts to the
    /// debounced rebuild policy.
    /// </summary>
    private void EnsureNotificationClient()
    {
        if (!_enableDeviceNotifications) return;
        lock (_gate)
        {
            if (_notifyClient != null || _disposed) return;
            _notifyClient = new DefaultDeviceNotificationClient(this);
        }

        _ = Task.Run(() =>
        {
            MMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                DefaultDeviceNotificationClient? client;
                lock (_gate)
                {
                    client = _notifyClient;
                    if (_disposed || client == null)
                    {
                        enumerator.Dispose();
                        return;
                    }
                    _notifyEnumerator = enumerator;
                }
                enumerator.RegisterEndpointNotificationCallback(client);
                Logger.Information("Standing capture: device notifications registered");
            }
            catch (Exception ex)
            {
                // Retryable (Codex diff r1): clear the client so the NEXT start attempt tries
                // again, instead of a one-shot failure silently disabling device-change healing
                // for the whole session — and dispose the local enumerator, which nothing else
                // owns once the fields are cleared (Codex diff r2).
                lock (_gate)
                {
                    _notifyClient = null;
                    _notifyEnumerator = null;
                }
                try { enumerator?.Dispose(); }
                catch (Exception dex) { Logger.Debug(dex, "Disposing failed notification enumerator threw"); }
                Logger.Warning("Device notification registration failed: {ExType} — will retry at the next start attempt; until then device changes heal via stale-claim rebuilds only", ex.GetType().Name);
            }
        });
    }

    internal void OnDefaultCaptureDeviceChanged()
    {
        // With the feature (or any other gate) off, a rebuild would be scheduled and refused on
        // every default-device change for the app's lifetime — pure log churn in support
        // bundles (Kimi diff r1 #5). The gate closure reads in-memory caches only.
        try { if (!_startGate()) return; }
        catch { return; }

        bool follows;
        lock (_gate)
        {
            if (_disposed) return;
            // A pinned selection ignores default churn; default-following devices rebuild.
            // A parked service (incl. the no-mic park and a BT refusal) re-evaluates — a new
            // default is exactly the event that can change the answer.
            follows = _deviceKind != RecordingDeviceResolutionKind.SelectedDevice || _parked || !_running;
            if (follows) { _parked = false; _consecutiveFailures = 0; }
        }
        if (follows)
            ScheduleRebuild("default-device-change", resetFailures: true);
    }

    /// <summary>
    /// A capture device appeared or changed state. Narrower re-kick than a default change: only a
    /// service that stands to GAIN from a new device reacts — parked, mid-failure-chain, or running
    /// on a <see cref="RecordingDeviceResolutionKind.FallbackToDefault"/> stream (the pinned mic may
    /// have returned; the cold path re-resolves per recording and would pick it up, so the standing
    /// stream must too). Everything else ignores topology noise.
    ///
    /// <para><b><c>_consecutiveFailures > 0</c> is load-bearing, not belt-and-braces.</b> Without it
    /// this handler sampled only <c>_parked</c>/<c>_running</c> — both still false while the attempt
    /// that is ABOUT to park is in flight, because <see cref="OnStartFailure"/> increments the count
    /// and sets <c>_parked</c> only after the bounded construction attempt completes or is abandoned.
    /// A device-added event landing in that window was therefore DROPPED, the in-flight attempt
    /// parked a moment later, and nothing un-parked it: plug a microphone into a no-mic machine while
    /// a retry is running and instant recording stays dead until another device event or a restart.
    /// Silent, and permanent.</para>
    ///
    /// <para><b>Why this clause rather than the broader <c>!_running</c>: churn, not safety.</b> An
    /// earlier version of this comment claimed <c>!_running</c> would "re-kick a service nobody asked
    /// to run" — that is FALSE, and the sibling <see cref="OnDefaultCaptureDeviceChanged"/> disproves
    /// it by using <c>!_running</c> verbatim (Kimi diff review). Intent is re-validated three times
    /// downstream: <c>_startGate()</c> above, the pre-construction <c>_desiredRunning</c> check, and
    /// the conditional publish. The narrow clause simply avoids waking a service that was never
    /// trying — and even that is partial, since <c>StopCoreLocked</c> does not clear the failure
    /// count, so a topology event during suspend still costs one intent-refused rebuild.</para>
    ///
    /// <para>Found as an intermittent full-suite failure — a deliberate 6× loop on the unfixed code
    /// did NOT reproduce it, so no rate is claimed — and then pinned deterministically by
    /// <c>TopologyChange_ArrivingDuringTheParkingAttempt_IsNotLost</c>, which raises the event from
    /// inside the failing attempt rather than racing it.</para>
    /// </summary>
    internal void OnCaptureDeviceTopologyChanged()
    {
        try { if (!_startGate()) return; }
        catch { return; }

        bool rekick;
        lock (_gate)
        {
            if (_disposed) return;
            rekick = _parked
                     || _consecutiveFailures > 0
                     || (_running && _deviceKind == RecordingDeviceResolutionKind.FallbackToDefault);
            if (rekick) { _parked = false; _consecutiveFailures = 0; }
        }
        if (rekick)
            ScheduleRebuild("device-topology-change", resetFailures: true);
    }

    private double TicksToMs(long ticks) => ticks * 1000.0 / _timestampFrequency;

    private async Task RunLoggedAsync(Func<Task> body, string label)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning("Standing capture {Label} failed: {ExType}", label, ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        DefaultDeviceNotificationClient? client;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _desiredRunning = false;
            enumerator = _notifyEnumerator;
            client = _notifyClient;
            _notifyEnumerator = null;
            _notifyClient = null;
        }

        // Unregister BEFORE the enumerator is disposed (final check item 8).
        if (enumerator != null)
        {
            try { if (client != null) enumerator.UnregisterEndpointNotificationCallback(client); }
            catch (Exception ex) { Logger.Debug(ex, "Unregistering device notifications failed"); }
            try { enumerator.Dispose(); }
            catch (Exception ex) { Logger.Debug(ex, "Disposing notification enumerator failed"); }
        }

        // Best-effort teardown even if a lifecycle op is wedged mid-COM — quitting must not hang.
        var acquired = false;
        try { acquired = _lifecycleLock.Wait(TimeSpan.FromSeconds(2)); }
        catch (ObjectDisposedException) { }
        try
        {
            StopCoreLocked("dispose", LatchKind.None);
        }
        finally
        {
            if (acquired) _lifecycleLock.Release();
        }
        // The semaphore is deliberately never disposed — same reasoning as the recorder's and
        // Whisper's: nothing proves this is the last toucher, and a parked WaitAsync meeting a
        // disposed handle turns shutdown into a crash.
    }

    /// <summary>No-op-everything-but-default-changed IMMNotificationClient. Callbacks arrive on
    /// MMDevAPI worker threads; the body must stay COM-free and fast.</summary>
    private sealed class DefaultDeviceNotificationClient : IMMNotificationClient
    {
        private readonly StandingCaptureService _owner;
        public DefaultDeviceNotificationClient(StandingCaptureService owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Capture) return;
            if (role != Role.Console && role != Role.Communications) return;
            try { _owner.OnDefaultCaptureDeviceChanged(); }
            catch (Exception ex) { Logger.Debug(ex, "Default-device-change handling failed"); }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            // Only an endpoint BECOMING usable can un-park or upgrade a fallback stream.
            if (newState != DeviceState.Active) return;
            try { _owner.OnCaptureDeviceTopologyChanged(); }
            catch (Exception ex) { Logger.Debug(ex, "Device-state-change handling failed"); }
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            try { _owner.OnCaptureDeviceTopologyChanged(); }
            catch (Exception ex) { Logger.Debug(ex, "Device-added handling failed"); }
        }

        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
