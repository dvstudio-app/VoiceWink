using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Audio;

/// <summary>
/// WASAPI-based audio recorder (NAudio, 16kHz mono WAV).
/// Records to 16kHz mono 16-bit PCM WAV (Whisper's expected format).
/// Includes EMA metering (0.6/0.4 smoothing) for audio level visualization.
/// Two start paths since AUD-6: the COLD path (own WasapiCapture, unchanged) and the WARM path
/// (<see cref="StartFromStandingAsync"/> — drains the standing capture's ring instead of opening
/// a device; owns no capture and no MMDevice, so <see cref="Cleanup"/> can never touch the
/// standing stream by construction).
/// </summary>
public sealed class AudioRecorderService : IDisposable
{
    private static ILogger Logger => Log.ForContext<AudioRecorderService>();

    // Target format for Whisper: 16kHz mono 16-bit PCM (constants live with the shared
    // converter since AUD-6 — the standing capture converts with the same code).
    private const int TargetSampleRate = CaptureFormatConverter.TargetSampleRate;
    private const int TargetChannels = CaptureFormatConverter.TargetChannels;
    private const int TargetBitsPerSample = CaptureFormatConverter.TargetBitsPerSample;

    private readonly SemaphoreSlim _recordingLock = new(1, 1);
    // IWaveIn, not WasapiCapture: AUD-12 injects the constructor so the cold path is testable
    // without hardware. All uses here are IWaveIn members (WaveFormat, DataAvailable,
    // RecordingStopped, StartRecording, Dispose); production still builds a WasapiCapture.
    private IWaveIn? _capture;
    private MMDevice? _captureDevice;
    private WaveFileWriter? _writer;
    private WaveFormat? _captureFormat;

    // AUD-8 deliberately has NO _downsampler field. The instance is built in StartRecordingAsync
    // where the source rate first becomes known, and bound into that attempt's DataAvailable
    // closure — so its lifetime is the closure's, and no code path can reach another attempt's
    // filter state. A field existed briefly and became write-only once the closure binding landed
    // (assigned, nulled in Cleanup, read nowhere); nulling it also released nothing, since the
    // closure holds the reference regardless. Kimi diff review r2: dead state carrying a comment
    // that contradicted the real mechanism.

    // Read ONCE at construction (see CaptureFilterFeature for why the const is not read inline).
    private readonly bool _antiAliasEnabled;
    private string? _currentFilePath;

    // AUD-11: "is a capture live, and WHICH one" as ONE atomic value — the live attempt's session
    // id, or 0 when nothing is recording. It replaced a plain `bool _isRecording` for two reasons.
    //
    // (1) It is read from the dispatcher (the meter tick's dead-recorder check) while NAudio's
    //     capture thread writes it, and a plain bool carried no memory barrier.
    // (2) The dead-recorder check needs the identity, not just the flag, and deriving
    //     "recording + which session" from TWO fields could tear: a torn read that returned 0
    //     while the caller's session was live would have been reported as a dead recorder — a
    //     false alarm on the pill. One field cannot disagree with itself.
    //
    // Distinct from _sessionSeq below: that ALLOCATES ids (one per attempt, live or not), this
    // records which allocated id currently owns a running capture.
    private long _liveSessionId;

    private bool _stopRequested;
    private int _cleanupDone; // 0 = pending, 1 = done; guarded by Interlocked
    private TaskCompletionSource<bool>? _stopTcs;

    // AUD-6 warm mode: the standing-capture claim this recording drains through. Non-null exactly
    // while a warm recording is active; _capture/_captureDevice stay NULL in warm mode, which is
    // what makes Cleanup() structurally unable to dispose the standing capture. Detached (which
    // releases any latched standing teardown/rebuild) by Cleanup — the one funnel every teardown
    // passes through.
    private StandingCaptureClaim? _warmClaim;

    // Monotonic capture-attempt id (AUD-1 r2 session guard). Incremented under _recordingLock
    // at every start (and at Dispose); each capture's event handlers carry the id they were
    // subscribed with and self-discard when it no longer matches — a delayed callback from a
    // failed previous attempt can never mutate the current attempt's state.
    private long _sessionSeq;

    // Session id that has already reported "capture live" to the latency probe; 0 = none
    // (sessions start at 1). Deliberately SESSION-VALUED rather than a reset-per-start bool
    // (Codex diff review): a superseded attempt's callback can pass the staleness check, be
    // preempted, and resume after the next attempt re-armed a shared flag — setting it and
    // permanently suppressing the NEW attempt's terminal mark. Comparing against the session
    // makes a stale setter self-correcting: the live attempt's next buffer still differs.
    private long _captureLiveMarkedSession;

    // Probe lease for the CURRENT attempt; 0 = none. Released by Cleanup(), which is the one
    // funnel every teardown passes through — see the disarm there for why RecordingStopped alone
    // was not enough.
    private long _activeProbeToken;

    // AUD-14: the live post-stop grace, published from dwell entry until ApplyGraceTrim
    // unpublishes it AFTER the teardown has quiesced delivery (Codex dual round: the keep-point
    // is read only once no callback can still be between writer.Write and Policy.Feed — the warm
    // path's ring DetachSink joins an in-flight delivery under the ring lock, and the cold path's
    // completed stop-TCS means the capture thread is done raising DataAvailable; reading at dwell
    // end instead could trim a final chunk that reached the WAV but not yet the policy, and the
    // clamp-to-fed eats the pad exactly when speech runs to the end). One immutable holder rather
    // than two fields so the capture thread can never observe a policy without its cut signal.
    // Volatile: written by the stop path, read by the capture callback.
    private sealed record GraceSession(
        Helpers.PostStopGrace Policy, TaskCompletionSource<bool> Cut);
    private volatile GraceSession? _graceSession;

    // Serializes grace ACTIVATION with each callback's write+feed pair (Codex verification
    // round): without it, a chunk written between the snapshot read and the session publish is
    // in the file but never fed — on a silent tail the trim then cuts it, breaching the
    // per-recording "never shorter than today" floor by exactly the final word's chunk. Under
    // the gate a buffer is atomically either IN the snapshot or FED to the policy. Uncontended
    // on every callback except the one stop instant; the stop side's hold is a length read plus
    // a publish, bounded behind at most one in-flight write.
    private readonly object _graceGate = new();

    /// <summary>AUD-14 test seam: runs on the stop thread immediately BEFORE grace activation
    /// takes <see cref="_graceGate"/> — the boundary the atomicity contract is pinned at.
    /// Null in production.</summary>
    internal Action? GraceActivatingHookForTests { get; set; }

    private bool IsStaleSession(long session) => Interlocked.Read(ref _sessionSeq) != session;

    /// <summary>
    /// AUD-12: serialises a capture's teardown against the NEXT attempt's identity allocation.
    /// </summary>
    /// <remarks>
    /// <para><c>OnRecordingStopped</c> used to check staleness exactly once, at entry, and then
    /// clear state and dispose. Preempted in between — and step one of "in between" was a
    /// synchronous Serilog write, which AUD-11 showed can stall for seconds on a wedged endpoint —
    /// a successor could start, re-arm the <c>_cleanupDone</c> latch and install its own capture,
    /// and the resumed callback would then dispose the SUCCESSOR's capture, writer and stop-TCS.
    /// Reachable because the 2 s stop-callback timeout deliberately returns while the real
    /// callback is still pending.</para>
    /// <para>The gate makes the identity re-check and the field swaps atomic with respect to that
    /// allocation, so a stale callback either finished entirely before the successor existed or
    /// touches nothing. <b>It holds field assignments only</b> — disposal happens outside it, and
    /// putting the slow work back inside would block the next recording behind a wedged teardown,
    /// which is worse than the bug.</para>
    /// <para>Lock order is always <c>_recordingLock</c> → <c>_teardownGate</c> →
    /// {<c>_meterLock</c>, ring append lock}; the capture-thread callback takes only this gate and
    /// never waits on <c>_recordingLock</c>, and chunk handlers take neither. Terminal
    /// <see cref="Dispose"/> is the ONE teardown that skips the gate — see there for why.</para>
    /// </remarks>
    private readonly object _teardownGate = new();

    /// <summary>
    /// AUD-13: terminal fence. Set before <see cref="Dispose"/> touches anything, so a start that
    /// has not yet published its resources aborts instead of installing them over torn-down state.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> because the disposing thread (Quit, on the UI thread) and the starting
    /// thread are different, and the whole point is that the start observes the write promptly.
    /// Checked once after the recording lock is taken and again after every await that can span a
    /// disposal — today that is the bounded capture construction, which is the wide window: it can
    /// legitimately take a second.
    /// </remarks>
    private volatile bool _disposed;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioRecorderService));
    }

    /// <summary>
    /// Dispose anything this attempt built that the normal funnel could not reach, because a
    /// terminal <see cref="Dispose"/> burned the <c>_cleanupDone</c> latch first (AUD-13).
    /// </summary>
    /// <remarks>
    /// Without this, a start losing that race leaks its capture and writer: <c>Cleanup()</c>
    /// returns <see cref="DetachedSession.None"/> and walks away. Kimi's diff review caught the
    /// comment that claimed the funnel already covered it. Safe to run from a start-failure catch
    /// specifically because that caller holds <c>_recordingLock</c>, so there is no successor
    /// whose resources could be taken by mistake.
    /// </remarks>
    private void DisposeStrandedStartResources()
    {
        DetachedSession stranded;
        lock (_teardownGate)
        {
            stranded = new DetachedSession(_capture, _captureDevice, _writer, _warmClaim, null, 0);
            _capture = null;
            _captureDevice = null;
            _writer = null;
            _warmClaim = null;
            _captureFormat = null;
        }

        DisposeDetached(stranded);
    }

    /// <summary>
    /// Test seam (AUD-12): runs on the stopped-callback thread after the entry staleness check and
    /// <b>before</b> the gate is taken, so a test can let a successor allocate in between and prove
    /// the in-gate re-check catches it. Synchronous by contract — an async hook around a
    /// <c>lock</c> would be a new hazard.
    /// </summary>
    internal Action? StoppedCallbackPreGateHookForTests { get; set; }

    /// <summary>
    /// Test seam (AUD-12): runs <b>inside</b> the gate, after the re-check and before detach, so a
    /// test can hold the barrier while a successor tries to allocate. That position is what makes
    /// the mutation mapping work — moved earlier or later, the regression row stops discriminating.
    /// Synchronous, same reason as above.
    /// </summary>
    internal Action? StoppedCallbackInGateHookForTests { get; set; }

    /// <summary>
    /// Test seam (AUD-12): runs after the gate is released and after disposal, <b>immediately
    /// before the stop TCS is completed</b> — the one window where a successor can install its own
    /// TCS while this callback is still running.
    /// </summary>
    /// <remarks>
    /// It exists because the first version of the TCS test did not discriminate: with the callback
    /// paused before the gate, the in-gate re-check already turns it away, so the TCS line is never
    /// reached and completing the FIELD instead of the snapshot passed the test. Verified by
    /// mutation — that is the pause point where the difference is observable.
    /// </remarks>
    internal Action? StoppedCallbackPostDetachHookForTests { get; set; }

    /// <summary>Production constructor — the one the DI container resolves.</summary>
    public AudioRecorderService() : this(Helpers.CaptureFilterFeature.IsEnabled)
    {
    }

    /// <summary>
    /// Test seam for the AUD-8 lever. Mirrors <c>VoiceActivityDetectionService</c>'s two-constructor
    /// shape so the disabled path is exercisable in an ordinary default build; the const itself is
    /// read only by the public constructor above.
    /// </summary>
    internal AudioRecorderService(bool antiAliasEnabled)
        : this(antiAliasEnabled, DefaultCaptureConstructor)
    {
    }

    /// <summary>
    /// AUD-12 test seam: inject how a capture object is built. Mirrors
    /// <c>StandingCaptureService</c>'s <c>captureConstructor</c>, which established the pattern.
    /// </summary>
    /// <remarks>
    /// Added because the cold path was structurally untestable: <c>RecordingStopped</c> is only
    /// ever subscribed there (warm mode has no <c>_capture</c> at all), and <c>AGENTS.md:77</c>
    /// forbids tests that open real capture devices — so the AUD-12 race, which lives entirely in
    /// that callback, had no way to be exercised. It is worth more than the one test: the cold
    /// start path is where AUD-11 lived, and until now its only coverage was device-gated rows
    /// that silently no-op on CI.
    /// </remarks>
    internal AudioRecorderService(bool antiAliasEnabled, Func<MMDevice?, IWaveIn> captureConstructor)
    {
        _antiAliasEnabled = antiAliasEnabled;
        _captureConstructor = captureConstructor;
    }

    private static IWaveIn DefaultCaptureConstructor(MMDevice? device)
        => device != null ? new WasapiCapture(device) : new WasapiCapture();

    private readonly Func<MMDevice?, IWaveIn> _captureConstructor;

    /// <summary>
    /// What the constructor actually stored. Exists so a test can assert the const reached the field
    /// through the PUBLIC constructor — both diff reviewers found that nothing else could: the field
    /// is read only inside <c>StartRecordingAsync</c>, which no device-less CI runner reaches, so
    /// changing the public constructor to <c>: this(true)</c> would have left every test in both
    /// configurations green.
    /// </summary>
    internal bool AntiAliasEnabledForTesting => _antiAliasEnabled;

    /// <summary>
    /// The lever's DECISION, split out from <c>StartRecordingAsync</c> so it is reachable without a
    /// capture device — and an INSTANCE method reading <see cref="_antiAliasEnabled"/> directly, so a
    /// test and the recording path execute the same code.
    ///
    /// <para>It was static, taking <c>enabled</c> as a parameter, for three review rounds. That let
    /// the gate tests compose <c>AntiAliasEnabledForTesting</c> with the factory by hand while
    /// production composed them independently at the call site — so hard-coding <c>true</c>, or the
    /// wrong rate, in <c>StartRecordingAsync</c> would have left BOTH CI legs green (Codex r10). The
    /// whole point of these gate tests is that the lever cannot silently stop working; a seam the
    /// test reassembles itself does not deliver that.</para>
    /// </summary>
    internal CaptureDownsampler? CreateDownsamplerFor(int sourceRate) =>
        _antiAliasEnabled ? new CaptureDownsampler(sourceRate, TargetSampleRate) : null;

    // Audio metering
    private readonly object _meterLock = new();
    private float _averagePower = -160f;
    private float _peakPower = -160f;

    // EMA smoothing factors for audio metering
    private const float AverageSmoothingFactor = 0.6f;
    private const float PeakSmoothingFactor = 0.4f;

    // First chunk after a start whose peak rises above this threshold seeds the
    // meters at its raw value instead of EMA-blending from -160 dB. Silent
    // WASAPI leading buffers stay below the threshold and don't consume the snap.
    // AUD-31 note: this latch is what the pill waveform's silence guard covers. Until a chunk's
    // peak crosses this threshold the meters stay pinned at -160, so the first level every
    // recording delivers is -160 — which IS WaveformAmplitude.SilenceDbfs (its guard is <=), so it
    // renders exactly flat and the bars rest until real audio arrives rather than jumping from a
    // sentinel. AudioRecorderMeterTests pins that relationship against this method directly.
    // (AUD-29's noise-floor tracker, which needed this same fact to avoid seeding a background
    // from a sentinel, is gone; the stateless mapping has no state to poison.) The threshold is
    // on PEAK while the mapping reads RMS, so the two are not comparable constants and neither
    // is tuned against the other.
    private const float SnapThresholdDb = -80f;

    private bool _meterSnapPending = true;

    /// <summary>Called on the audio thread with raw PCM data for streaming.</summary>
    public Action<byte[]>? OnAudioChunk { get; set; }

    /// <summary>Timestamp of the last DataAvailable event. Used to detect audio stalls.</summary>
    public DateTime LastDataReceivedAt { get; private set; }

    public bool IsRecording => Interlocked.Read(ref _liveSessionId) != 0;

    /// <summary>
    /// The session id of the capture that is live right now, or 0 when nothing is recording.
    /// Matches the value returned by the <c>StartRecordingAsync</c> / <c>StartFromStandingAsync</c>
    /// call that began it (AUD-11).
    /// </summary>
    /// <remarks>
    /// One atomic read, deliberately — a caller comparing this against the id it started with is
    /// asking "is MY capture still the live one", and the answer must never be assembled from two
    /// fields that can disagree mid-update. It is stronger than <see cref="IsRecording"/> for that
    /// question: a successor capture makes this differ while <c>IsRecording</c> stays true.
    /// </remarks>
    public long CurrentSessionId => Interlocked.Read(ref _liveSessionId);
    public string? CurrentFilePath => _currentFilePath;

    public float AveragePower
    {
        get { lock (_meterLock) return _averagePower; }
    }

    public float PeakPower
    {
        get { lock (_meterLock) return _peakPower; }
    }

    /// <summary>
    /// Pre-warm the WASAPI capture path so the first user-initiated recording
    /// skips the cold-start cost (audio engine init, format negotiation, COM
    /// session setup — ~300-500ms on first press otherwise). Briefly opens,
    /// starts, and stops a throwaway capture against the supplied device (or
    /// system default if null), discarding all data. Triggers Windows' mic-in-use
    /// privacy indicator for ~150ms at app startup — accepted trade-off for
    /// instant first recording.
    ///
    /// Best-effort: failures (mic disabled, device unavailable, etc.) are logged
    /// at Debug and swallowed.
    /// </summary>
    public static async Task WarmUpAsync(MMDevice? device = null, CancellationToken ct = default)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        WasapiCapture? warmCapture = null;
        var stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            warmCapture = device != null
                ? new WasapiCapture(device)
                : new WasapiCapture();

            warmCapture.RecordingStopped += (_, _) => stopTcs.TrySetResult(true);

            warmCapture.StartRecording();
            // Give WASAPI engine a moment to fully spin up before tearing down.
            // 100ms covers the cold-start cost without making mic indicator linger.
            await Task.Delay(100, ct).ConfigureAwait(false);
            warmCapture.StopRecording();

            // Bounded wait for clean stop — don't hang startup if WASAPI is wedged.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await stopTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* timeout — proceed to dispose */ }

            sw.Stop();
            Logger.Information("AudioRecorderService warm-up completed in {Elapsed}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "AudioRecorderService warm-up failed (non-critical)");
        }
        finally
        {
            try { warmCapture?.Dispose(); }
            catch (Exception ex) { Logger.Debug(ex, "AudioRecorderService warm-up dispose failed"); }
        }
    }

    /// <summary>
    /// Start recording to a WAV file at the given path, using the specified device (or system default).
    ///
    /// <para><b>Device ownership: this method owns <paramref name="device"/> unconditionally from
    /// entry.</b> Callers must never dispose it themselves. On the success path ownership passes to
    /// <see cref="Cleanup"/> via <c>_captureDevice</c>; on every failure path — including a
    /// cancellation thrown by the lock wait, which happens BEFORE <c>_captureDevice</c> is
    /// assigned and therefore used to leak the device — this method disposes it.</para>
    /// </summary>
    /// <param name="deviceKind">How <paramref name="device"/> was chosen. Diagnostic only: it is
    /// logged as an enum so the capture-start timing line can distinguish an explicit selection
    /// from an implicit default without ever logging the device's name.</param>
    /// <param name="probeToken">Attempt token from <see cref="Helpers.RecordingStartLatencyProbe"/>,
    /// or 0 for callers that are not measuring (tray/UI starts, tests).</param>
    /// <param name="deviceTag">AUD-16 endpoint tag, computed by the CALLER during resolution.
    /// <b>This method must never derive it from <paramref name="device"/> itself</b> — reading the
    /// endpoint here would put a COM call back inside the lock-held tail, which is the precise shape
    /// AUD-11 removed. Null renders as the unknown sentinel; in practice only TESTS pass null, since
    /// every production caller resolves a device first and therefore has a tag.</param>
    /// <returns>The session id this attempt allocated — the handle a caller passes to
    /// <see cref="StopRecordingIfCurrentAsync"/> to stop only its OWN capture (AUD-11).</returns>
    public async Task<long> StartRecordingAsync(
        string outputFilePath,
        MMDevice? device = null,
        CancellationToken ct = default,
        RecordingDeviceResolutionKind deviceKind = RecordingDeviceResolutionKind.SystemDefault,
        long probeToken = 0,
        string? deviceTag = null,
        // AUD-18: the raw endpoint id, used ONLY to address the capture-effects query — never
        // logged. Optional to match deviceTag's shape (the compile-forced layer is the record);
        // a caller omitting it degrades that path's effects line to "unavailable (no endpoint
        // id)", honestly.
        string? endpointId = null)
    {
        var tag = deviceTag ?? Helpers.CaptureDeviceTag.Unknown;

        try
        {
            await _recordingLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Never accepted: _captureDevice is still unassigned, so Cleanup() will never see
            // this device and nothing else can release it.
            DisposeUnacceptedDevice(device);
            throw;
        }

        // Ownership is only handed to Cleanup() once _captureDevice accepts the device below.
        // ANY throw before that point — notably StopRecordingCoreAsync cancelling or faulting
        // while superseding an older recording — must release it here, or it leaks (Codex diff
        // review; the lock-wait catch above covered only the earliest of these paths).
        var deviceAccepted = false;
        try
        {
            ThrowIfDisposed(); // AUD-13
            await RunStartLockHeldHookAsync().ConfigureAwait(false);

            if (IsRecording)
            {
                Logger.Warning("Already recording, stopping previous session");
                await StopRecordingCoreAsync(ct).ConfigureAwait(false);
            }

            _currentFilePath = outputFilePath;

            // Re-arm the cleanup latch BEFORE this attempt allocates any resource (AUD-1 diff
            // review r1, blocking): a FAILED previous start left _cleanupDone = 1, and the old
            // re-arm point sat after capture+writer creation — a second attempt (the AUD-1
            // fallback retry, or a manual retry press) failing in that window hit the latch and
            // leaked a live WASAPI capture.
            //
            // The early re-arm is only safe TOGETHER with the session guard below (diff review
            // r2, blocking): NAudio raises RecordingStopped from its capture thread and
            // Dispose() does NOT join an in-flight handler (captureThread is nulled before the
            // event is raised), so a PREVIOUS attempt's delayed callback can resume while THIS
            // attempt is live. Every callback is therefore bound to its attempt's session id at
            // subscription and self-discards when stale — a resumed old handler can no longer
            // clear _liveSessionId, run Cleanup() against the new capture, reset the meters, or
            // complete the new attempt's _stopTcs.
            // AUD-12: the identity allocation takes the teardown barrier. Three interlocked
            // writes, nothing slow — but they are what makes a predecessor's in-flight callback
            // stale, so they must not interleave with that callback's own re-check. Resource
            // construction stays OUTSIDE: once the id has advanced, no older callback can touch
            // anything, so nothing built afterwards needs the gate.
            long session;
            lock (_teardownGate)
            {
                session = Interlocked.Increment(ref _sessionSeq);
                Interlocked.Exchange(ref _cleanupDone, 0);
                Interlocked.Exchange(ref _activeProbeToken, probeToken);
            }

            // Capture-start phase split. The 2026-08-02 latency investigation could attribute the
            // 1.3-3.3 s stalls only to "somewhere between the capture object existing and
            // StartRecording() returning" — which spans BOTH the WAV file creation (an enterprise
            // AV scan on %LOCALAPPDATA% is a plausible multi-second stall) and
            // AudioClient.Initialize. The two are measured separately here because the fix for
            // each is completely different, and the format log between them is deliberately
            // OUTSIDE both windows so a slow synchronous log write is not misattributed.
            // AUD-11: the three phases above did NOT span the whole lock hold. On 2026-08-06 a
            // start logged startCall=234ms while the real span from hotkey to "Recording started"
            // was 4.8 s — the missing time sat AFTER startCall was stamped and BEFORE the finally
            // below, a window nothing measured. `tailMs` closes it, so a slow lock-held tail
            // self-attributes instead of hiding behind a healthy-looking startCall.
            var phaseSw = global::System.Diagnostics.Stopwatch.StartNew();
            long constructMs = -1, writerMs = -1, startCallMs = -1, tailMs = -1;
            var startOutcome = "failed";

            try
            {
                // Create WASAPI capture — shared mode captures at the device's native format.
                // Store device reference for disposal — WasapiCapture does NOT own the MMDevice.
                //
                // The constructor is a synchronous COM call to IMMDevice::Activate. Under
                // heavy system contention it can block for many seconds (same root cause as
                // the audio warm-up's GetSystemDefaultDevice hangs). Wrap it in BoundedComCall
                // so a wedged audio service produces a fast user-actionable error rather than
                // freezing the UI / hotkey state machine.
                //
                // The device is passed as `dependency` so BoundedComCall owns its disposal on
                // the abandonment path — if the bounded constructor times out OR the caller
                // cancels, the worker may still be inside `new WasapiCapture(device)` using
                // the device; we transfer ownership to the helper's cleanup continuation by
                // nulling _captureDevice in BOTH the timeout (null result) and cancellation
                // (OCE) paths below, so the outer catch's Cleanup() can't double-dispose.
                _captureDevice = device;
                // From here Cleanup() owns the device (and, on the two branches below that null
                // _captureDevice, BoundedComCall's abandonment continuation does) — so the outer
                // safety net must not also release it.
                deviceAccepted = true;
                try
                {
                    _capture = await Helpers.BoundedComCall.RunBoundedAsync(
                        () => _captureConstructor(device),
                        TimeSpan.FromSeconds(1),
                        "recording-NewWasapiCapture",
                        dependency: device,
                        ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // BoundedComCall has taken ownership of `device` and will dispose it
                    // when the abandoned worker eventually completes. Null our reference
                    // so the outer catch's Cleanup() doesn't double-dispose.
                    _captureDevice = null;
                    throw;
                }

                if (_capture == null)
                {
                    // Same ownership transfer reasoning for the timeout path.
                    _captureDevice = null;
                    throw new AudioCaptureUnavailableException(
                        "Microphone is unavailable — please try again in a moment.");
                }

                // AUD-13: the widest await in this method just returned — bounded at 1 s, and
                // Dispose does not wait for it. If disposal won, everything below would install a
                // capture, a writer and a live session over state that has already been torn down.
                // The catch below cleans up what this attempt built: through the normal funnel if
                // the latch is still ours, and via DisposeStrandedStartResources when a terminal
                // Dispose burned it first. (An earlier comment here claimed the funnel alone
                // covered that — it does not, and Kimi's review caught the overclaim.)
                //
                // Residual, named rather than papered over: this is a check, not a commit, so a
                // Dispose landing between here and the publication below still wins. Bounded to
                // shutdown, and Dispose's own 2 s serialisation attempt covers the common case.
                // Closing it entirely means publishing capture/writer/session in one gated commit
                // — a restructure of this method, not a fence.
                if (_disposed)
                    throw new ObjectDisposedException(nameof(AudioRecorderService));

                constructMs = phaseSw.ElapsedMilliseconds;

                _captureFormat = _capture.WaveFormat;
                Logger.Information("Capture format: {Format}", _captureFormat);

                // AUD-8: one downsampler per attempt, so the filter's delay line and fractional
                // read phase start clean and live exactly as long as this recording. Null when the
                // lever is off — ConvertToTarget then takes the pre-AUD-8 path verbatim.
                var downsampler = CreateDownsamplerFor(_captureFormat.SampleRate);

                // Deliberately no rate ceiling — refusing a rate would break a working-but-slow
                // configuration, which is worse than slow filtering (Codex r6's cap-or-measure
                // choice) — but an expensive configuration must not be SILENT, so a field report of
                // gaps or dropouts on unusual hardware self-attributes.
                //
                // Thresholded on ESTIMATED COST, not on the rate. A rate threshold is the wrong
                // shape: 88.2 kHz costs 9.5% of a buffer period while the higher 96 kHz costs 5.2%,
                // because a fractional ratio pays two convolutions per output. Two earlier versions
                // used raw rate (>192 kHz, then >96 kHz) and both stayed silent on 88.2 kHz while
                // firing on cheaper configurations (Codex r8, then r10).
                if (downsampler != null && downsampler.EstimatedBufferCostPercent > 8.0)
                    Logger.Warning(
                        "Capture rate {SampleRate} Hz: AUD-8 filter cost is an estimated {CostPercent:F0}% of each buffer period ({Taps} taps, {ConvPerOutput} convolution(s)/output) — see tools/resampler-aliasing section 8",
                        _captureFormat.SampleRate,
                        downsampler.EstimatedBufferCostPercent,
                        downsampler.RelativeWorkPerOutput / (downsampler.IsIntegerRatio ? 1 : 2),
                        downsampler.IsIntegerRatio ? 1 : 2);

                // Create output WAV file in target format (16kHz mono 16-bit)
                var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
                phaseSw.Restart();
                _writer = new WaveFileWriter(outputFilePath, targetFormat);
                writerMs = phaseSw.ElapsedMilliseconds;

                // Session-bound subscriptions (r2 guard): the closures carry THIS attempt's id;
                // the handlers self-discard when the id is stale. Deliberately not unsubscribed
                // in Cleanup — an anonymous closure can't be, and doesn't need to be: disposal
                // stops the event source and the guard neutralizes any straggler.
                // AUD-8: the downsampler rides the CLOSURE, not a field read, for the same reason the
                // session id does. The pre-existing window at the top of this file (callback passes
                // IsStaleSession, is preempted, resumes after a new attempt started) would otherwise
                // let a straggler snapshot the NEW attempt's downsampler and push an old buffer
                // through it — corrupting 400 samples of delay line and permanently shifting the new
                // recording's phase, where before AUD-8 the same race cost one foreign buffer.
                // Binding it here makes that structurally impossible: each callback can only ever
                // reach its own attempt's instance (Codex diff review, blocking). The residual
                // one-foreign-buffer window on _writer is pre-existing and unchanged.
                _capture.DataAvailable += (s, e) => OnDataAvailable(session, probeToken, downsampler, s, e);
                _capture.RecordingStopped += (s, e) => OnRecordingStopped(session, s, e);

                _stopRequested = false;
                LastDataReceivedAt = DateTime.UtcNow; // Initialize before first DataAvailable

                // Diagnostic-only audio-session enumeration. Synchronous COM call that has
                // been observed to take many seconds under contention. Defer to a fire-and-
                // forget Task.Run so it can't delay the actual _capture.StartRecording()
                // below. The result feeds only a log line; nothing in the recording flow
                // depends on it.
                var deviceForLog = _captureDevice;
                _ = Task.Run(() => LogOtherCaptureSessions(deviceForLog));
                // Initialize state BEFORE StartRecording so a synchronous
                // RecordingStopped callback (immediate device error) finds a
                // non-null _stopTcs to complete and the snap flag armed.
                ResetMeters();
                // AUD-12, load-bearing ordering: this assignment happens AFTER the gated identity
                // allocation above. That is what lets DetachSessionResources capture _stopTcs
                // under the gate and know it is still THIS session's — a predecessor callback
                // that passes the in-gate re-check therefore cannot be holding a successor's TCS.
                _stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Codex 2026-05-19 review-loop round-4 finding: late cancellation
                // can land here after BoundedComCall returned a Capture but before
                // we actually start it. Without this check we'd hand the caller a
                // running capture against their explicit cancel intent. The catch
                // below uses Cleanup() to release the half-built state. The outer
                // VM (MainViewModel.StartRecordingAsync) also defends against the
                // post-return window where cancellation arrives between
                // StartRecording and our return.
                ct.ThrowIfCancellationRequested();

                Interlocked.Exchange(ref _liveSessionId, session);
                phaseSw.Restart();
                _capture.StartRecording();
                startCallMs = phaseSw.ElapsedMilliseconds;
                phaseSw.Restart(); // AUD-11: everything from here to the finally is `tail`.

                // If RecordingStopped fired synchronously (immediate device
                // error reported without throwing), OnRecordingStopped already
                // cleared _liveSessionId. Treat that as a start failure
                // rather than returning success to a caller who'd then drive
                // RecordingState.Recording on a dead capture.
                if (!IsRecording)
                {
                    throw new InvalidOperationException(
                        "WASAPI capture stopped immediately after start; the audio device may be unavailable.");
                }

                startOutcome = "ok";

                // Stamped HERE, not by the caller: the capture thread can deliver its first
                // buffer (and so mark capture-live) before the caller's continuation resumes,
                // which would leave this milestone unset on exactly the fast starts (Kimi final
                // check). It is deliberately "start returned", not "live" — StartRecording()
                // spawns a thread and returns; audioClient.Start() runs on that thread after.
                Helpers.RecordingStartLatencyProbe.Instance.MarkStartReturned(probeToken);

                // AUD-11: value-only, and deliberately NO `device.FriendlyName`. That property
                // opens the endpoint's property store — a synchronous COM call — and this line
                // runs while `_recordingLock` is held, so on a Bluetooth endpoint being grabbed
                // by another device it stalled the whole recorder: 4.6 s on one attempt and 2.3 s
                // on the next, which is what serialised two attempts into the AUD-11 race. It is
                // the strong suspect for that span rather than a proven cause (the Serilog write
                // sits in the same window) — `tail` below now measures the whole tail, so a
                // remaining slow sink is attributable instead of inferred.
                //
                // The name is not relogged elsewhere: `deviceKind` is the more actionable signal,
                // and `AudioDeviceManager`'s enumeration line already reports device COUNTS only
                // because device names are user-identifying and ride into support bundles.
                Logger.Information("Recording started: {Path} (device kind: {DeviceKind}, dev={DeviceTag})",
                    outputFilePath, deviceKind, tag);
                // AUD-18: one effects line per recording, fire-and-forget off the start path —
                // the reader owns the safety argument (no lock, no MMDevice, thread pool).
                CaptureEffectsReader.Instance.LogAfterStart(endpointId, tag);
                tailMs = phaseSw.ElapsedMilliseconds;
            }
            catch (ObjectDisposedException ex)
            {
                // AUD-13: an expected shutdown race, not a fault. Information, not Error — the
                // generic handler below would put "Failed to start recording" into support
                // bundles and Sentry on every Quit that lands mid-start, and drive the VM's
                // failure presentation into an error pill during shutdown (Kimi diff r3).
                Logger.Information("Recording start abandoned — the recorder was disposed ({ExType})", ex.GetType().Name);
                Cleanup();
                DisposeStrandedStartResources();
                _stopTcs = null;
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start recording");
                // Resets state set ahead of StartRecording so callers don't see a bogus
                // IsRecording=true. The live-session clear lives in the gated detach; the TCS
                // null stays HERE and only here — a failed start's TCS will never be completed by
                // anyone, so leaving it in the field would strand a joining stop. The teardown
                // path must NOT null it (see DetachSessionResources).
                Cleanup();
                DisposeStrandedStartResources();
                _stopTcs = null;
                throw;
            }
            finally
            {
                // Emitted on BOTH outcomes: a start that FAILED slowly is exactly as diagnostic
                // as one that succeeded slowly (Codex plan review). Numbers and an enum only —
                // never the device's FriendlyName or the WAV path.
                Logger.Information(
                    "Capture start: construct={ConstructMs}ms writer={WriterMs}ms startCall={StartCallMs}ms tail={TailMs}ms device={DeviceKind} dev={DeviceTag} outcome={Outcome}",
                    constructMs, writerMs, startCallMs, tailMs, deviceKind, tag, startOutcome);
            }

            return session;
        }
        catch
        {
            // Bare rethrow: the original exception and its stack reach the caller untouched,
            // which matters most for the OperationCanceledException the cancel paths observe.
            if (!deviceAccepted) DisposeUnacceptedDevice(device);
            throw;
        }
        finally
        {
            _recordingLock.Release();
        }
    }

    /// <summary>Release a device this attempt never accepted into <c>_captureDevice</c>. Silent
    /// and best-effort: a throwing Dispose must never mask the exception that got us here.</summary>
    private static void DisposeUnacceptedDevice(MMDevice? device)
    {
        try { device?.Dispose(); }
        catch (Exception ex) { Logger.Debug(ex, "Disposing unaccepted capture device failed"); }
    }

    /// <summary>
    /// AUD-6 warm start: record by draining the standing capture through <paramref name="claim"/>
    /// instead of opening a device. Audio from the claim's cut instant (the hotkey press) comes
    /// out of the RAM ring, so the one slow step here — creating the WAV writer — is off the
    /// user-perceived path entirely.
    ///
    /// <para><b>Claim ownership: this method owns <paramref name="claim"/> unconditionally from
    /// entry</b> (mirroring the cold path's device-ownership rule). On success it passes to
    /// <see cref="Cleanup"/> via <c>_warmClaim</c>; on every failure path it is detached here.
    /// Detach is idempotent, so a defensive caller-side dispose is harmless.</para>
    ///
    /// <para>Init parity with the cold start is deliberate and load-bearing (plan review B2):
    /// <c>LastDataReceivedAt</c> is stamped and the meters reset BEFORE the sink goes live —
    /// without the stamp, the meter-tick stall watchdog reads the PREVIOUS recording's timestamp
    /// and fires "No audio — check microphone" on the first tick of every warm recording.</para>
    /// </summary>
    /// <returns>The session id this attempt allocated — the handle a caller passes to
    /// <see cref="StopRecordingIfCurrentAsync"/> to stop only its OWN capture (AUD-11).</returns>
    public async Task<long> StartFromStandingAsync(
        string outputFilePath,
        StandingCaptureClaim claim,
        CancellationToken ct = default,
        long probeToken = 0)
    {
        // AUD-16: the warm path is the DEFAULT path (instant recording ships on), so it is the one
        // that most needed a per-recording device identity — and the one an earlier draft of this
        // change left out entirely. The tag rides the claim's resolution record; no COM here, and
        // no signature change, because the standing service already resolved this device.
        var warmTag = claim.ResolutionInfo.DeviceTag;

        try
        {
            await _recordingLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            SafeDetach(claim);
            throw;
        }

        // Ownership is only handed to Cleanup() once _warmClaim accepts the claim below. A throw
        // BEFORE that point — notably StopRecordingCoreAsync cancelling or faulting while
        // superseding an older recording — must detach it here, or the standing capture refuses
        // every later recording as AlreadyClaimed (Codex diff r2; the exact shape of the cold
        // path's deviceAccepted net).
        var claimAccepted = false;
        try
        {
            ThrowIfDisposed(); // AUD-13
            await RunStartLockHeldHookAsync().ConfigureAwait(false);

            if (IsRecording)
            {
                Logger.Warning("Already recording, stopping previous session");
                await StopRecordingCoreAsync(ct).ConfigureAwait(false);
            }

            // AUD-13: the warm path's WIDE await is the supersede-stop above, not a capture
            // construction — and it is genuinely wide, because `_capture.StopRecording()` is
            // unbounded (only the callback wait carries the 2 s bound). Quit does not cancel an
            // in-flight start's token, so `ct.ThrowIfCancellationRequested()` below does not cover
            // this. Without this line the fence was asymmetric: cold re-checked after its wide
            // await and warm did not, so a Quit landing here published a writer, a claim and a
            // live session onto a disposed recorder (Kimi diff r3 — my "checked in both start
            // paths" was true of the ENTRY check only).
            ThrowIfDisposed();

            _currentFilePath = outputFilePath;

            // Same latch/session/probe arming order as the cold start — Cleanup is the shared
            // funnel and expects them armed before any resource exists.
            // AUD-12: the identity allocation takes the teardown barrier. Three interlocked
            // writes, nothing slow — but they are what makes a predecessor's in-flight callback
            // stale, so they must not interleave with that callback's own re-check. Resource
            // construction stays OUTSIDE: once the id has advanced, no older callback can touch
            // anything, so nothing built afterwards needs the gate.
            long session;
            lock (_teardownGate)
            {
                session = Interlocked.Increment(ref _sessionSeq);
                Interlocked.Exchange(ref _cleanupDone, 0);
                Interlocked.Exchange(ref _activeProbeToken, probeToken);
            }

            var phaseSw = global::System.Diagnostics.Stopwatch.StartNew();
            long writerMs = -1;
            var outcome = "failed";
            try
            {
                // The one disk touch. Can stall (271 ms observed under AV) — the ring covers it.
                var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
                _writer = new WaveFileWriter(outputFilePath, targetFormat);
                writerMs = phaseSw.ElapsedMilliseconds;

                // B2 init parity — all state armed BEFORE the sink can deliver.
                _stopRequested = false;
                LastDataReceivedAt = DateTime.UtcNow;
                ResetMeters();
                // AUD-12, load-bearing ordering: this assignment happens AFTER the gated identity
                // allocation above. That is what lets DetachSessionResources capture _stopTcs
                // under the gate and know it is still THIS session's — a predecessor callback
                // that passes the in-gate re-check therefore cannot be holding a successor's TCS.
                _stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                ct.ThrowIfCancellationRequested();

                Interlocked.Exchange(ref _liveSessionId, session);
                _warmClaim = claim;
                claimAccepted = true;

                // Atomic replay-then-live under the ring's append lock (plan review B3): every
                // chunk since the cut lands in the replay XOR the live stream, exactly once.
                // Replayed chunks run the same downstream as live ones — writer, streaming hook,
                // meters — so the waveform is honest even while a writer stall was being covered.
                var attach = claim.AttachSink(chunk => OnWarmChunk(session, chunk));
                if (attach.TrimmedByCapacity)
                {
                    Logger.Warning(
                        "Warm drain trimmed at ring capacity — audio between the hotkey and the ring start was evicted (drained {DrainedBytes} bytes)",
                        attach.DrainedBytes);
                }

                outcome = "ok";
                Logger.Information("Recording started (warm): {Path} (dev={DeviceTag})", outputFilePath, warmTag);
                // AUD-18: the warm path is the DEFAULT path, so leaving it out would blind the
                // effects line for most recordings — the id rides the claim's resolution record,
                // captured at standing start; no COM here (the AUD-16 rule).
                CaptureEffectsReader.Instance.LogAfterStart(claim.ResolutionInfo.EndpointId, warmTag);
                Logger.Information("Warm attach: writer={WriterMs}ms drained={DrainedBytes}B chunks={DrainedChunks}",
                    writerMs, attach.DrainedBytes, attach.DrainedChunks);
                Helpers.RecordingStartLatencyProbe.Instance.MarkWarmWriterAttached(probeToken);
            }
            catch (ObjectDisposedException ex)
            {
                // Expected shutdown race, not a fault — see the cold path's twin.
                Logger.Information("Warm recording start abandoned — the recorder was disposed ({ExType})", ex.GetType().Name);
                _warmClaim = claim;
                claimAccepted = true;
                Cleanup();
                DisposeStrandedStartResources();
                _stopTcs = null;
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start warm recording");
                // Hand the claim to the funnel so latched standing teardown/rebuild still fires.
                _warmClaim = claim;
                claimAccepted = true;
                Cleanup();
                DisposeStrandedStartResources();
                _stopTcs = null;   // same reasoning as the cold catch
                throw;
            }
            finally
            {
                Logger.Information(
                    "Capture start: construct={ConstructMs}ms writer={WriterMs}ms startCall={StartCallMs}ms tail={TailMs}ms device={DeviceKind} dev={DeviceTag} outcome={Outcome}",
                    0, writerMs, 0, 0, "Warm", warmTag, outcome);
            }

            return session;
        }
        catch
        {
            // Bare rethrow, mirroring the cold path's device net: covers every throw before the
            // inner try accepted the claim (the superseding stop above, cancellation between).
            if (!claimAccepted) SafeDetach(claim);
            throw;
        }
        finally
        {
            _recordingLock.Release();
        }
    }

    /// <summary>Best-effort claim release for paths where the funnel can't run.</summary>
    private static void SafeDetach(StandingCaptureClaim claim)
    {
        try { claim.Detach(); }
        catch (Exception ex) { Logger.Debug(ex, "Detaching standing claim failed"); }
    }

    /// <summary>
    /// Warm-mode chunk sink: identical downstream semantics to the cold path's
    /// <see cref="OnDataAvailable"/> (writer, streaming hook, meters, stall timestamp), minus the
    /// conversion — the standing capture already converted. Runs on the capture thread (replay:
    /// on the attaching thread), session-guarded like every capture callback in this class.
    /// </summary>
    private void OnWarmChunk(long session, byte[] chunk)
    {
        if (IsStaleSession(session)) return;

        var writer = _writer;
        if (writer == null) return;

        try
        {
            // AUD-14: write and feed are ONE atom under _graceGate (Codex verification round) —
            // a chunk is either in the activation snapshot or fed to the policy, never neither.
            // The warm sink feeds exactly as the cold one does: BOTH self-review lenses found
            // the first revision hooked only OnDataAvailable, leaving the grace dead on the
            // shipping default (instant recording ON).
            lock (_graceGate)
            {
                writer.Write(chunk, 0, chunk.Length);
                if (_graceSession is { } warmGrace && warmGrace.Policy.Feed(chunk, chunk.Length))
                    warmGrace.Cut.TrySetResult(true);
            }
        }
        catch (ObjectDisposedException)
        {
            // Writer disposed between the null check and the write (detach-then-dispose still
            // races one in-flight dispatch on the capture thread) — same tolerance as cold.
            return;
        }

        // Stamped AFTER the successful write — deliberately stricter than the cold path's
        // stamp-at-top (Kimi diff r3): a persistently failing warm writer would otherwise keep
        // the 3 s stall watchdog silent while every chunk is lost.
        LastDataReceivedAt = DateTime.UtcNow;

        OnAudioChunk?.Invoke(chunk);
        UpdateMeters(chunk);
    }

    /// <summary>
    /// Test seam (AUD-11): awaited INSIDE <c>_recordingLock</c> at the very top of both start
    /// paths, while any PREDECESSOR capture is still live. Null in production.
    /// </summary>
    /// <remarks>
    /// It exists because the AUD-11 regression is invisible to any test that cannot hold the lock:
    /// the fix is that <see cref="StopRecordingIfCurrentAsync"/> compares identity AFTER acquiring
    /// the lock, and a test which lets a successor start finish first passes just as happily
    /// against the broken check-then-wait ordering. Pausing here lets a test queue a stale stop
    /// while the predecessor is still the live session — the exact interleaving that lost 17 s of
    /// dictation — and prove the successor survives it.
    /// </remarks>
    internal Func<Task>? StartLockHeldHookForTests { get; set; }

    /// <summary>AUD-14 quiescence pin (see the dwell block). Synchronous; null in production.</summary>
    internal Action? GraceDwellEndedHookForTests { get; set; }

    private Task RunStartLockHeldHookAsync() => StartLockHeldHookForTests?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// Stop the current recording session and wait for the final WAV buffers to flush.
    /// </summary>
    /// <remarks>
    /// Stops whatever is live when the lock is acquired. That is correct for a caller that OWNS
    /// the current recording, and wrong for one whose attempt may have been superseded — those
    /// callers must use <see cref="StopRecordingIfCurrentAsync"/> (AUD-11).
    /// </remarks>
    /// <param name="ct">Cancels the WAITS inside the stop. A token firing during the grace
    /// dwell ends the dwell early and the CAPTURE still stops; on the cold path the call itself
    /// may then complete by cancellation from the pre-existing 2 s stop wait (the grace is
    /// unpublished and the tail kept on that exit — Kimi A3).</param>
    /// <param name="grace">AUD-14 opt-in: keep capturing for up to
    /// <see cref="Helpers.PostStopGrace.MaxGraceMs"/> after this call (early-exiting on
    /// <see cref="Helpers.PostStopGrace.SilenceRunMs"/> of silence), then trim the WAV back to
    /// the last speech — never below the bytes already written when the stop arrived. Default
    /// OFF so every existing caller — cancel, supersede teardowns, Dispose — keeps today's
    /// byte-identical stop; the ONE graced caller is the normal transcribing stop.</param>
    public async Task StopRecordingAsync(CancellationToken ct = default, bool grace = false)
    {
        await _recordingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopRecordingCoreAsync(ct, grace).ConfigureAwait(false);
        }
        finally
        {
            _recordingLock.Release();
        }
    }

    /// <summary>
    /// Stop the current recording ONLY if it is still session <paramref name="expectedSessionId"/>
    /// — the value returned by the start call that began it (AUD-11).
    /// </summary>
    /// <returns>
    /// True if that session was found live and is now stopped — including when a stop was already
    /// in flight, which this call then awaits rather than duplicates. False if the recorder had
    /// already moved on. Deliberately not phrased as "this call did the stopping": callers use it
    /// to log which branch they took, not to claim authorship of the teardown.
    /// </returns>
    /// <remarks>
    /// <para>The identity test runs INSIDE <c>_recordingLock</c>, in the same acquisition that
    /// performs the stop. That is the whole point: a caller which tests <see cref="IsRecording"/>
    /// or <see cref="CurrentSessionId"/> first and then calls <see cref="StopRecordingAsync"/> has
    /// a time-of-check/time-of-use gap as wide as the lock wait. On 2026-08-06 a superseded
    /// attempt's queued stop crossed a 3.1 s lock hold and stopped its SUCCESSOR's capture 5 ms
    /// after it started; the pill showed Recording for another 17 s over a dead recorder and the
    /// dictation was lost. Do not "simplify" this into a public check followed by a plain stop.</para>
    /// <para><b>Invariant this rests on:</b> session ids are only ever ALLOCATED under
    /// <c>_recordingLock</c> (both start paths), so the comparison here and any predecessor's
    /// supersede-stop are mutually serialised with no window. <c>Dispose()</c> touches the
    /// session state outside the lock, but only terminally — it allocates nothing and clears
    /// <c>_liveSessionId</c>, so it can only make this method return false, never alias a live
    /// identity.</para>
    /// </remarks>
    public async Task<bool> StopRecordingIfCurrentAsync(long expectedSessionId, CancellationToken ct = default)
    {
        await _recordingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // One atomic read of the live session — never a bool plus a sequence, which could
            // tear into "nothing is recording" while this caller's capture was in fact live.
            if (expectedSessionId == 0 || Interlocked.Read(ref _liveSessionId) != expectedSessionId)
                return false;

            await StopRecordingCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _recordingLock.Release();
        }
    }

    /// <summary>
    /// Internal stop logic — caller must already hold _recordingLock. With <paramref name="grace"/>
    /// the stop DWELLS first (AUD-14): the capture stays live and the WAV keeps growing while the
    /// grace policy watches for the silence run, bounded by a timer — never chunk arrival, because
    /// a mid-grace capture death delivers no chunks and would otherwise hang the stop path. The
    /// trim runs AFTER the normal teardown closed the writer, against the recording's own chunk
    /// layout, and is fail-soft. A joiner (stop already in flight) and a not-recording call never
    /// grace. A token firing during the dwell ends the dwell early and the stop proceeds — an
    /// escaping OCE here would strand a live capture under a caller that believes it stopped.
    /// </summary>
    private async Task StopRecordingCoreAsync(CancellationToken ct, bool grace = false)
    {
        if (!IsRecording) return;

        (string Path, long SnapshotBytes, GraceSession Session)? trim = null;
        if (grace && !_stopRequested && _writer is { } graceWriter && _currentFilePath is { } gracePath)
        {
            GraceActivatingHookForTests?.Invoke();
            // The per-recording no-regression floor (plan B1): the writer's bytes at grace entry
            // IS today's cut point. Snapshot and publish are ONE atom under _graceGate (Codex
            // verification round), so a concurrent chunk is either counted in the snapshot or
            // fed to the published policy — the former in-flight blur is structurally gone.
            long snapshotBytes;
            GraceSession session;
            lock (_graceGate)
            {
                snapshotBytes = graceWriter.Length;
                session = new GraceSession(
                    new Helpers.PostStopGrace(),
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _graceSession = session;
            }
            try
            {
                await session.Cut.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(Helpers.PostStopGrace.MaxGraceMs), ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The dwell cap — the normal exit for a tail with trailing speech (or a dead
                // capture delivering nothing).
            }
            catch (OperationCanceledException)
            {
                // A cancelled STOP must still stop. Pre-AUD-14, no await in this core could
                // throw with the capture untouched — an escaping OCE here would return to a
                // caller that believes it stopped while the capture is fully live (the AUD-11
                // failure shape). The dwell ends early; the normal teardown below proceeds.
            }
            // Deliberately NOT unpublished here (Codex dual round): the session stays live
            // through the teardown so a callback still in flight keeps feeding the policy, and
            // ApplyGraceTrim reads the keep-point only after delivery has provably stopped.
            trim = (gracePath, snapshotBytes, session);
            // Test seam (the StartLockHeldHookForTests precedent): the one point where delivery
            // is still live but the dwell has ended — the quiescence pin feeds here, which is
            // exactly where a dwell-end keep-point read (the Codex Blocker) would already have
            // gone stale. Null in production.
            GraceDwellEndedHookForTests?.Invoke();
        }

        Logger.Information("Stopping recording");
        if (_stopRequested)
        {
            // Snapshot, never two reads of the field (Kimi diff review — a defect AUD-12 itself
            // introduced). Before the teardown split, `_stopTcs` was only ever nulled by a thread
            // holding `_recordingLock`, so reading it twice here was safe. DetachSessionResources
            // now nulls it under `_teardownGate` from the capture-thread callback, which holds no
            // `_recordingLock` — so the two reads could tear (NRE), or the second could land null
            // after detach but before the snapshot's TrySetResult and return WITHOUT waiting for
            // the writer flush. That last one is exactly the unpatched-WAV-header failure this
            // change exists to prevent. Same pattern as `stopTask` below.
            var inFlightStop = _stopTcs;
            if (inFlightStop != null)
                await inFlightStop.Task.WaitAsync(ct).ConfigureAwait(false);
            // This branch is why the teardown must NOT null `_stopTcs`: a stop joining an
            // in-progress teardown has to find the real handle and await the flush, exactly as it
            // did before AUD-12. An earlier revision cleared the field in detach and I described
            // the resulting early return as "narrower than master" — it was the opposite, a
            // regression, and Codex caught the claim (diff r3). Keep the snapshot for correctness
            // of WHICH TCS gets completed; keep the FIELD alive so joiners can still see it.
            return;
        }

        // AUD-6 warm stop: nothing to stop — the standing capture keeps running; the drain
        // ends synchronously. Cleanup detaches the claim (which stops sink delivery BEFORE the
        // writer is disposed and releases any latched standing teardown/rebuild) and flushes the
        // WAV. Mirrors OnRecordingStopped's body; the 2 s stop-callback wait never applies here
        // because there is no capture thread to wait for.
        if (_warmClaim != null)
        {
            // AUD-12: the _liveSessionId / _stopRequested clears moved INTO the gated detach, so
            // every session-field swap happens under the barrier. The TCS comes back with the
            // snapshot because the field is null by then.
            var warmDetached = Cleanup();
            ResetMeters();
            warmDetached.StopTcs?.TrySetResult(true);
            // Quiescent: Cleanup detached the claim, and the ring's DetachSink joins an in-flight
            // delivery under the ring lock — no callback can still be feeding the policy.
            ApplyGraceTrim(trim, deliveryQuiesced: true);
            return;
        }

        _stopRequested = true;
        var stopTask = _stopTcs?.Task ?? Task.CompletedTask;

        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error stopping WASAPI capture");
            Cleanup().StopTcs?.TrySetResult(true);
            ApplyGraceTrim(trim, deliveryQuiesced: false);
            return;
        }

        var stopQuiesced = true;
        try
        {
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pre-existing escape: a cancelled 2 s stop wait rethrows today, and the capture
            // still stops (StopRecording was already issued; the callback completes teardown).
            // With the grace session now published through teardown (Codex round), unpublish on
            // the way out — not quiesced, so the tail is kept at full length (Kimi A3).
            ApplyGraceTrim(trim, deliveryQuiesced: false);
            throw;
        }
        catch (TimeoutException ex)
        {
            Logger.Warning(ex, "Timed out waiting for WASAPI capture to stop cleanly");
            // THE path that makes AUD-12 reachable: this returns while the real callback is still
            // pending. The barrier is what stops that callback tearing down whatever starts next.
            Cleanup().StopTcs?.TrySetResult(true);
            stopQuiesced = false;
        }

        // The normal cold path is quiescent: the completed stop-TCS means RecordingStopped ran,
        // and NAudio raises no DataAvailable after it on that capture thread.
        ApplyGraceTrim(trim, deliveryQuiesced: stopQuiesced);
    }

    /// <summary>AUD-14: the post-teardown trim. Unpublishes the grace session, then — ONLY when
    /// the caller can vouch that delivery has quiesced (warm: the ring's DetachSink joined any
    /// in-flight delivery inside Cleanup; cold: the completed stop-TCS means the capture thread
    /// finished raising events) — reads the keep-point and trims. Reading before quiescence could
    /// trim a final chunk that reached the WAV but not yet the policy (Codex dual round): the
    /// clamp-to-fed eats the pad exactly when speech runs to the end of the counted audio. The
    /// stop-error and 2 s stop-timeout paths are NOT quiesced — they skip the trim and keep the
    /// full grace tail, the safe direction (real audio; the pipeline handles trailing silence),
    /// consistent with the trim's own fail-soft. On both TRIMMING paths the writer is provably
    /// disposed before this runs (Kimi verification round traced both); the helper's
    /// FileShare.None open stays as defense-in-depth, not a load-bearing race guard.</summary>
    private void ApplyGraceTrim((string Path, long SnapshotBytes, GraceSession Session)? trim, bool deliveryQuiesced)
    {
        if (trim is not { } t) return;
        _graceSession = null;
        if (!deliveryQuiesced)
        {
            Logger.Information("Post-stop grace: teardown did not quiesce — tail kept at full length");
            return;
        }
        Helpers.PostStopGrace.TrimFileFailSoft(t.Path, t.SnapshotBytes, t.Session.Policy.KeepTailMs);
    }

    /// <summary>
    /// Process incoming audio data: resample to 16kHz mono, write to file, compute meters.
    /// Session-guarded: a straggler event from a superseded capture (late buffer after a failed
    /// attempt's teardown) must not write into the CURRENT attempt's WAV or meters.
    /// </summary>
    private void OnDataAvailable(long session, long probeToken, CaptureDownsampler? downsampler, object? sender, WaveInEventArgs e)
    {
        if (IsStaleSession(session)) return;

        // First real buffer of this attempt = the earliest provable "the microphone is live"
        // moment, and the one the user actually experiences. Runs on NAudio's capture thread, so
        // the probe stamps + disarms under its lock and hands the log off to the thread pool
        // rather than doing synchronous file I/O here (Codex final check). The session guard
        // above already rejects a superseded capture's stragglers, and the probe's token check
        // rejects a superseded ATTEMPT's mark independently.
        if (probeToken != 0 && Interlocked.Exchange(ref _captureLiveMarkedSession, session) != session)
            Helpers.RecordingStartLatencyProbe.Instance.MarkCaptureLive(probeToken);

        LastDataReceivedAt = DateTime.UtcNow;

        // Snapshot both fields — Cleanup() can null them concurrently on another thread.
        // AUD-8's downsampler is deliberately NOT snapshotted from the field: it arrives as a
        // parameter bound in this attempt's subscription closure, so a straggler can never reach a
        // newer attempt's filter state. See the subscription site for why that matters.
        var writer = _writer;
        var captureFormat = _captureFormat;
        if (writer == null || captureFormat == null) return;

        try
        {
            // Convert captured audio to 16kHz mono 16-bit PCM
            var converted = ConvertToTarget(e.Buffer, e.BytesRecorded, captureFormat, downsampler);
            if (converted.Length == 0) return;

            try
            {
                // AUD-14: write and feed are ONE atom under _graceGate — see the warm sink.
                lock (_graceGate)
                {
                    writer.Write(converted, 0, converted.Length);
                    if (_graceSession is { } grace && grace.Policy.Feed(converted, converted.Length))
                        grace.Cut.TrySetResult(true);
                }
            }
            catch (ObjectDisposedException)
            {
                // Writer was disposed between our null check and the write call (race with Cleanup).
                return;
            }

            // Forward raw PCM chunk for streaming
            OnAudioChunk?.Invoke(converted);

            // Update audio meters
            UpdateMeters(converted);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error processing audio data");
        }
    }

    /// <summary>
    /// Convert captured audio (any format) to 16kHz mono 16-bit PCM. The BODY lives in
    /// <see cref="CaptureFormatConverter"/> (AUD-6 + AUD-8 merge, 2026-08-05): AUD-6 extracted the
    /// conversion so the standing warm capture shares it; AUD-8 gave it the anti-alias
    /// <paramref name="downsampler"/> (per-attempt state, bound in the subscription closure). One
    /// implementation serves both capture paths - a filter fix can never fork between them.
    ///
    /// <para>This instance seam stays for the reasons the AUD-8 review pinned: the lever's
    /// byte-identity and gate tests exercise the exact method the recording path calls
    /// (field -> <see cref="CreateDownsamplerFor"/> -> here), so hard-coding the lever or the wrong
    /// rate at the call site fails a test instead of shipping silently (Codex r10).</para>
    /// </summary>
    internal byte[] ConvertToTarget(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, CaptureDownsampler? downsampler)
        => CaptureFormatConverter.ConvertToTarget(buffer, bytesRecorded, sourceFormat, downsampler);


    /// <summary>
    /// Reset both meters to the noise floor and re-arm snap-on-first-non-silent.
    /// Called from StartRecordingAsync (before StartRecording, so the snap flag
    /// is in place when DataAvailable first fires) and from OnRecordingStopped
    /// (before TrySetResult, so callers see deterministic state on a fast restart).
    /// </summary>
    internal void ResetMeters()
    {
        lock (_meterLock)
        {
            _averagePower = -160f;
            _peakPower = -160f;
            _meterSnapPending = true;
        }
    }

    /// <summary>
    /// Update audio meters. The first chunk after a start whose peak rises above
    /// SnapThresholdDb seeds the meters at its raw value (no EMA cold-start);
    /// subsequent chunks blend with the existing EMA factors.
    /// </summary>
    internal void UpdateMeters(byte[] pcm16Data)
    {
        var sampleCount = pcm16Data.Length / 2;
        if (sampleCount == 0) return;

        float sumSquares = 0;
        float peak = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcm16Data, i * 2) / 32768f;
            sumSquares += sample * sample;
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }

        var rms = MathF.Sqrt(sumSquares / sampleCount);
        var rmsDb = rms > 0 ? 20f * MathF.Log10(rms) : -160f;
        var peakDb = peak > 0 ? 20f * MathF.Log10(peak) : -160f;

        rmsDb = Math.Max(rmsDb, -160f);
        peakDb = Math.Max(peakDb, -160f);

        lock (_meterLock)
        {
            if (_meterSnapPending)
            {
                if (peakDb > SnapThresholdDb)
                {
                    _averagePower = rmsDb;
                    _peakPower = peakDb;
                    _meterSnapPending = false;
                }
                // Sub-threshold while pending: leave meters at floor, keep snap armed.
            }
            else
            {
                _averagePower = AverageSmoothingFactor * rmsDb + (1f - AverageSmoothingFactor) * _averagePower;
                _peakPower = PeakSmoothingFactor * peakDb + (1f - PeakSmoothingFactor) * _peakPower;
            }
        }
    }

    /// <summary>
    /// Diagnostic: log other processes holding an audio session on the capture device.
    /// Helps diagnose mid-call mic conflicts (e.g., Teams losing audio when VoiceWink opens
    /// the same headset for transcription). Best-effort — never throws.
    ///
    /// <para><b>Privacy contract (SEC-3).</b> The process names collected here are the names of
    /// OTHER applications running on the user's machine — the same PII class as the clipboard
    /// probe's <c>ownerProc</c> and the watchdog's <c>foregroundProc</c>, and this line is
    /// <c>Information</c>, which is at Sentry's breadcrumb threshold. They ride the
    /// <c>{CaptureSessionProcesses}</c> property, which is on <c>LogRedactionEnricher</c>'s
    /// name allowlist (covers the Sentry sub-logger) and is scrubbed from rendered lines by
    /// <c>CaptureSessionProcessPattern</c> (covers the REL-3 support-zip and GDPR export). The
    /// property must stay LAST in the template — the rendered pattern is anchored to
    /// end-of-line, which is the only anchor a process name cannot forge.</para>
    ///
    /// <para>The capture device's <c>FriendlyName</c> is deliberately NOT logged. Users rename
    /// endpoints ("Dieter's AirPods") and it is arbitrary driver text that can contain quotes,
    /// so no delimited pattern could safely bound it. The adjacent <c>Capture start:</c> line
    /// already carries <c>device=&lt;kind&gt;</c>, which is the part with diagnostic value —
    /// the same call AUD-11 made when it removed <c>FriendlyName</c> from that line, and AUD-4
    /// made for the device-enumeration line.</para>
    /// </summary>
    /// <summary>
    /// The message template for the line below, as a const so a test can pin it. The method
    /// itself is private and needs a live COM <c>MMDevice</c>, so a test that retyped the
    /// template would only pin its own copy and keep passing after the real site drifted —
    /// and the drift that matters here is silent: appending a token after
    /// <c>{CaptureSessionProcesses}</c> moves it inside the end-of-line scrub, or leaves the
    /// process names mid-line where the pattern no longer reaches them.
    /// </summary>
    internal const string OtherCaptureSessionsTemplate =
        "Other capture sessions at recording start: count={Count} capProcs={CaptureSessionProcesses}";

    private static void LogOtherCaptureSessions(MMDevice? device)
    {
        if (device == null) return;

        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            if (sessions == null || sessions.Count == 0) return;

            var currentPid = global::System.Environment.ProcessId;
            var others = new List<string>();

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == currentPid) continue;

                    string processName;
                    try { processName = global::System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
                    catch { processName = "<unknown>"; }

                    others.Add($"{processName} (pid={pid}, state={session.State})");
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Failed to read audio session at index {Index}", i);
                }
            }

            if (others.Count > 0)
            {
                Logger.Information(OtherCaptureSessionsTemplate,
                    others.Count,
                    Helpers.LogValueSanitizer.SingleLine(string.Join(", ", others)));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not enumerate audio sessions (non-critical)");
        }
    }

    private void OnRecordingStopped(long session, object? sender, StoppedEventArgs e)
    {
        // Stale = a superseded attempt's delayed callback (NAudio raises this on its capture
        // thread; Dispose does not join an in-flight handler). It must not clear _liveSessionId,
        // Cleanup() the NEW attempt's resources, reset the meters, or complete the new _stopTcs
        // — its own resources were already torn down by the failure/stop path that superseded it.
        if (IsStaleSession(session))
        {
            Logger.Debug("Stale RecordingStopped callback discarded (superseded capture attempt)");
            return;
        }

        // No probe disarm here: DisposeDetached owns it as a per-session teardown invariant, so
        // the paths that never raise this callback at all are covered too.
        //
        // These two lines stay OUTSIDE the gate deliberately (AUD-12, both plan reviewers): a
        // synchronous Serilog write is the very stall that makes this race reachable, and holding
        // the barrier across it would block the next recording's start for the wedge duration.
        if (e.Exception != null)
        {
            Logger.Error(e.Exception, "Recording stopped due to error");
        }
        else
        {
            Logger.Information("Recording stopped normally");
        }

        StoppedCallbackPreGateHookForTests?.Invoke();

        DetachedSession detached;
        bool superseded;
        lock (_teardownGate)
        {
            // THE decisive check (AUD-12). Inside the gate the session cannot change under us,
            // because the only thing that changes it — the next attempt's identity allocation —
            // takes this same gate. The entry check above is a cheap early-out, not the guarantee:
            // everything between it and here can be preempted, and on wedged hardware is.
            superseded = IsStaleSession(session);
            if (!superseded)
            {
                StoppedCallbackInGateHookForTests?.Invoke();
                detached = DetachSessionResources();
                // Reset meters before completing the stop TCS so a fast-restart Start sees
                // deterministic state and isn't racing the prior session's residue.
                ResetMeters();
            }
            else
            {
                detached = DetachedSession.None;
            }
        }

        if (superseded)
        {
            // Logged after release — the barrier FIRING is the event this whole fix chases, so it
            // must be visible, but not at the cost of a Serilog write inside the gate (Kimi).
            Logger.Debug("Stale RecordingStopped callback stopped at the teardown barrier (a newer capture had already started)");
            return;
        }

        DisposeDetached(detached);
        // The CAPTURED TCS, never the field: by the time we get here a successor may have
        // installed its own, and completing that one would let its stop return without waiting
        // for its real callback — handing transcription a WAV the writer had not finished.
        StoppedCallbackPostDetachHookForTests?.Invoke();
        detached.StopTcs?.TrySetResult(true);
    }

    /// <summary>
    /// Everything one capture session owns, lifted out of the fields in a single gated step so the
    /// slow disposal can happen outside <c>_teardownGate</c> (AUD-12).
    /// </summary>
    private readonly record struct DetachedSession(
        IWaveIn? Capture,
        MMDevice? Device,
        WaveFileWriter? Writer,
        StandingCaptureClaim? Claim,
        TaskCompletionSource<bool>? StopTcs,
        long ProbeToken)
    {
        /// <summary>Nothing was detached — another teardown had already claimed this session.</summary>
        internal static DetachedSession None => default;
    }

    /// <summary>
    /// AUD-12 phase 1: take this session's resources out of the fields. <b>Caller MUST hold
    /// <c>_teardownGate</c>.</b> Field reads and writes only — no COM, no IO, no logging — so the
    /// gate is bounded by construction.
    /// </summary>
    /// <remarks>
    /// The split exists because the first version of this barrier held the gate across the whole
    /// of <c>Cleanup()</c>: COM disposal, the WAV writer flush and a synchronous Serilog write.
    /// None of those is bounded, and on the wedged-Bluetooth hardware that produced AUD-11 that
    /// would have made the next recording's start — and the 2 s stop timeout's own escape — wait
    /// out the wedge. Both plan reviewers rejected it independently. Detaching here and disposing
    /// outside keeps the gate to a fixed number of field assignments.
    /// </remarks>
    private DetachedSession DetachSessionResources()
    {
        // Guard against double-teardown: OnRecordingStopped fires on the NAudio capture
        // thread while StopRecordingCoreAsync may also tear down on error paths.
        // Interlocked ensures exactly one caller proceeds past this point.
        if (Interlocked.Exchange(ref _cleanupDone, 1) == 1) return DetachedSession.None;

        var detached = new DetachedSession(
            _capture,
            _captureDevice,
            _writer,
            _warmClaim,
            // AUD-12 (both plan reviewers, blocking): the stop TCS travels WITH the snapshot.
            // Completing `_stopTcs` from the field after the gate let a stale callback complete a
            // SUCCESSOR's stop — whose own stop would then return without waiting for its real
            // callback, defeating "wait for the final WAV buffers to flush" and handing the
            // transcription pipeline a WAV whose header the still-open writer had not patched.
            // Safe because a successor assigns its TCS only AFTER the gated allocation, so
            // in-gate-non-stale implies this is still our session's.
            _stopTcs,
            Interlocked.Exchange(ref _activeProbeToken, 0));

        _capture = null;
        _captureDevice = null;
        _writer = null;
        _warmClaim = null;
        _captureFormat = null;

        // `_stopTcs` is deliberately NOT nulled here — it rides in the snapshot above so the right
        // TCS gets completed, but the FIELD must survive until it is completed (Codex diff r3).
        // An earlier version cleared it, and that was a REGRESSION against master, not a
        // narrowing: master's Cleanup never touched `_stopTcs`, so a stop joining an in-progress
        // teardown still found the real handle and awaited the flush. Clearing it here made that
        // joining stop read null and return BEFORE the writer was disposed — handing transcription
        // a WAV with an unpatched header, the exact failure this card exists to stop. The start
        // paths overwrite the field with their own; the two start-failure catches null it
        // explicitly, because there nobody will ever complete it.

        Interlocked.Exchange(ref _liveSessionId, 0);
        _stopRequested = false;

        return detached;
    }

    /// <summary>
    /// AUD-12 phase 2: dispose what <see cref="DetachSessionResources"/> lifted out. Runs
    /// <b>outside</b> <c>_teardownGate</c>. The body is today's <c>Cleanup()</c> verbatim, in the
    /// same order, against the snapshot instead of the fields — that ordering is load-bearing and
    /// was reversed in a draft of this change (both plan reviewers).
    /// </summary>
    private static void DisposeDetached(DetachedSession detached)
    {
        // Release this attempt's probe lease FIRST — before anything that can block. Teardown is
        // the ONE funnel every attempt reaches —
        // RecordingStopped, BOTH StopRecordingCoreAsync failure paths (StopRecording() throwing,
        // and the 2 s stop-callback timeout, which exists precisely because the callback may
        // never arrive), the start-failure catch, and Dispose. Doing this only in
        // RecordingStopped left a successful-but-bufferless start armed forever, since the
        // ViewModel deliberately stops disarming once the start succeeded (Codex diff review r2).
        //
        // Unconditional by design: Disarm is token-scoped, so this is a no-op when capture-live
        // already fired (MarkCaptureLive is terminal) and when a newer attempt has armed its own
        // token. It also means a straggling post-teardown buffer can no longer mark a bogus
        // capture-live, because the token it carries is no longer armed.
        if (detached.ProbeToken != 0)
            Helpers.RecordingStartLatencyProbe.Instance.Disarm(detached.ProbeToken);

        // AUD-6 warm mode: detach the standing-capture claim FIRST — that stops sink delivery
        // (the ring's detach waits out an in-flight chunk write) before the writer below is
        // disposed, and releases any teardown/rebuild the standing service latched during the
        // drain. Idempotent, and tolerant of the standing service being already disposed
        // (tray Quit during an active recording; DI dispose order is unspecified).
        if (detached.Claim != null)
        {
            try { detached.Claim.Detach(); }
            catch (Exception ex) { Logger.Warning(ex, "Error detaching standing-capture claim"); }
        }

        if (detached.Capture != null)
        {
            // No unsubscribe — the handlers are session-bound closures; disposal stops the
            // event source and the session guard neutralizes any in-flight straggler.
            // Before the writer, as it always has been: disposal stops the event source, which
            // is what bounds the pre-existing one-foreign-buffer window (OnDataAvailable can
            // already be holding a local writer reference — phase 1 does NOT make these
            // unreachable, and an earlier draft of this change wrongly claimed it did).
            detached.Capture.Dispose();
        }

        if (detached.Writer != null)
        {
            try { detached.Writer.Dispose(); }
            catch (Exception ex) { Logger.Warning(ex, "Error closing WAV writer"); }
        }

        // Dispose the MMDevice — WasapiCapture does NOT take ownership of it
        if (detached.Device != null)
        {
            try { detached.Device.Dispose(); }
            catch (Exception ex) { Logger.Warning(ex, "Error disposing capture device"); }
        }
    }

    /// <summary>
    /// The two teardown phases as one call, for the callers that already hold
    /// <c>_recordingLock</c> (start-failure catches and the stop paths). They take
    /// <c>_teardownGate</c> for phase 1 — so every <b>teardown-side</b> field swap happens under
    /// the gate rather than resting on a latch-plus-benign-race argument — and dispose outside it.
    /// <para>Be precise about what the gate does and does not cover (Kimi diff r2): it serialises
    /// {teardown-side swaps} against {the start paths' identity allocation}. The start paths'
    /// PUBLISH-side swaps — <c>_capture</c>, <c>_writer</c>, <c>_stopTcs</c>, <c>_liveSessionId</c>
    /// — happen outside it, under <c>_recordingLock</c>, and correctly so. Do not read the gate as
    /// protection for those; that mistake is the shape of AUD-13.</para>
    /// </summary>
    /// <remarks>
    /// Note what this does NOT buy these callers: they hold <c>_recordingLock</c> across phase 2,
    /// so a slow disposal still delays the next start via that lock, exactly as before AUD-12.
    /// That is deliberate and cannot be moved — the stop path must finish the WAV before
    /// <c>MainViewModel</c> hands it to transcription. AUD-12 bounds contention on the GATE, not
    /// on <c>_recordingLock</c> (Codex final check).
    /// </remarks>
    /// <returns>
    /// What was detached — so the caller can complete <b>the captured</b> stop TCS rather than the
    /// field, which phase 1 has nulled. Returning it is not a convenience: these callers used to
    /// do <c>Cleanup(); _stopTcs?.TrySetResult(true);</c>, and after the split that field read is
    /// null, so a second concurrent stop awaiting the TCS would never be signalled.
    /// </returns>
    private DetachedSession Cleanup()
    {
        DetachedSession detached;
        lock (_teardownGate)
        {
            detached = DetachSessionResources();
        }

        DisposeDetached(detached);
        return detached;
    }

    public void Dispose()
    {
        // AUD-13, and it must be the FIRST thing: a start that has not yet published its
        // resources sees this and aborts, instead of installing a capture, writer and live
        // session over state torn down below. Tray Quit deliberately does not wait for a
        // recording that is still Starting, so that overlap is a real shutdown scenario, not a
        // theoretical one.
        _disposed = true;

        // Then try — BOUNDED — to serialise with a start that is already past the fence. Success
        // means no start is in flight and the rest of this method has the object to itself;
        // failure means we proceed exactly as before, with the fence and the post-await re-check
        // as the remaining protection. Bounded rather than unconditional because shutdown must
        // not hang behind a wedged COM call, which is the same reasoning that keeps the teardown
        // gate off this path.
        var serialised = false;
        try { serialised = _recordingLock.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { Logger.Debug(ex, "Dispose could not take the recording lock; proceeding"); }

        try
        {
            DisposeCore();
        }
        finally
        {
            if (serialised) _recordingLock.Release();
        }
    }

    private void DisposeCore()
    {
        // Invalidate any in-flight session callbacks first — after disposal no callback,
        // however delayed, may touch the torn-down state.
        Interlocked.Increment(ref _sessionSeq);

        // AUD-11: and clear the live session explicitly, because that increment is exactly what
        // stops the resulting RecordingStopped callback from doing it — the callback discards
        // itself as stale. Without this a disposed recorder that was mid-recording keeps
        // reporting IsRecording == true and a nonzero CurrentSessionId forever.
        Interlocked.Exchange(ref _liveSessionId, 0);

        // AUD-14 hygiene: a Dispose landing mid-dwell abandons the grace — the dwelling stop's
        // own ApplyGraceTrim still unpublishes, but Dispose must not leave a stale session for
        // straggler callbacks to feed after the recorder is gone.
        _graceSession = null;

        // Best-effort sync teardown: ask WASAPI to stop, then force cleanup.
        // We don't await the final flush here — disposing the WAV writer flushes synchronously.
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error while stopping capture during dispose");
        }

        try
        {
            // AUD-12: THE ONE teardown that skips `_teardownGate`, deliberately. The invariant is
            // "every TEARDOWN-SIDE session-field swap happens under the gate — except terminal
            // Dispose"; publish-side swaps are `_recordingLock`'s job, never the gate's (Kimi diff
            // r2 — the looser phrasing invited exactly the AUD-13 mistake). Taking it here would
            // let a wedged capture-thread callback stall
            // application shutdown, which is a worse failure than the one the gate closes.
            // Called as the two phases rather than Cleanup() so the skip is visible instead of
            // hidden behind a helper.
            //
            // What that skip is and is NOT safe against (Codex diff review corrected an earlier,
            // stronger claim here): the `_cleanupDone` latch does guarantee exactly one detacher,
            // so Dispose racing a CALLBACK is fine. It does NOT stop an in-flight START from
            // publishing _capture/_writer/_liveSessionId AFTER this ran, because Dispose never
            // takes `_recordingLock` — it disposes it below without ever acquiring it. That race
            // is PRE-EXISTING (identical on master, where Dispose was equally unsynchronised) and
            // needs a terminal fence with post-await revalidation, which is AUD-13. Do not read
            // this comment as a claim that the skip makes disposal safe against a live start.
            DisposeDetached(DetachSessionResources());
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error while disposing AudioRecorderService");
        }

        // AUD-13: `_recordingLock` is deliberately NOT disposed. A disposed fence and post-await
        // re-checks do not on their own make `Release()` safe (Codex diff review) — a start that
        // is already inside its `finally` will release a semaphore this method just disposed, and
        // that throws on the shutdown path. SemaphoreSlim only needs disposal if its
        // `AvailableWaitHandle` was materialised, which this class never touches, so leaving a
        // process-lifetime primitive undisposed costs nothing and removes the failure mode
        // entirely. Do not "tidy" this back into a Dispose call.
    }
}
