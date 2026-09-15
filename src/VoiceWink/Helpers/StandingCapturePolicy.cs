namespace VoiceWink.Helpers;

/// <summary>Why a standing-capture claim was refused (or wasn't).</summary>
public enum StandingClaimVerdict
{
    Claimable,
    NotRunning,
    AlreadyClaimed,
    /// <summary>The stream exists but stopped delivering buffers — claiming it would record
    /// silence. The service posts a rebuild on this verdict (Kimi final check R2): a stream that
    /// stalled WITHOUT raising RecordingStopped would otherwise disable the feature forever.</summary>
    StaleData,
    /// <summary>AUD-21: the stream is delivering buffers ON TIME, and every sample in every one of
    /// them has been exactly zero since it started — claiming it would record silence just as surely
    /// as <see cref="StaleData"/> would, which is why it earns a verdict of its own rather than
    /// passing as healthy. Posts a rebuild for the same reason StaleData does.</summary>
    SilentStream,
}

/// <summary>What to do with a teardown/rebuild request that arrives while a recording drain is
/// attached (Kimi plan review B5): never yank a live recording's audio source.</summary>
public enum StandingLifecycleDecision
{
    ExecuteNow,
    LatchUntilDetach,
}

/// <summary>
/// AUD-6: pure decisions for the standing warm capture — when it may run, when a claim is
/// grantable, how rebuilds pace themselves, and how lifecycle requests behave while a claim is
/// active. Pure so the whole matrix is unit-testable without audio hardware.
/// </summary>
public static class StandingCapturePolicy
{
    /// <summary>A claim is only trustworthy while data flowed within this bound — a "running"
    /// stream that delivered nothing for longer is treated as stalled.</summary>
    public const double MaxClaimDataAgeMs = 1000;

    /// <summary>
    /// AUD-21: how long a stream must have RUN before its never-delivered-a-non-zero-sample record
    /// is allowed to condemn it. Below this it is merely young — nothing has had time to arrive —
    /// and the safe direction is to let it prove itself rather than pre-judge it.
    ///
    /// <para>Comfortably above <see cref="MaxClaimDataAgeMs"/>, so a stream this rule condemns has
    /// already delivered many driver periods' worth of buffers and the silence is a property of the
    /// AUDIO, never of the delivery.</para>
    /// </summary>
    public const double MinSilenceEvidenceMs = 2000;

    /// <summary>Consecutive start failures after which the service PARKS instead of retrying on
    /// the capped backoff forever (a machine with no microphone would otherwise probe COM every
    /// 30 s for the whole session). A default-device-change notification re-kicks a parked
    /// service — plugging in a first microphone fires one.</summary>
    public const int ParkAfterConsecutiveFailures = 10;

    /// <summary>
    /// May the standing capture run at all? Consulted at (re)start time — endpoint kind is a
    /// separate, post-resolution question (<see cref="IsEndpointStandable"/>).
    /// Onboarding/legal gates keep the microphone closed on a first-run machine where nothing has
    /// been accepted yet; the license gate keeps the mic indicator honest for installs that can't
    /// record anyway. A license expiring mid-session deliberately does NOT tear down a running
    /// capture (recorded residual — re-evaluated at next launch).
    /// </summary>
    public static bool ShouldStart(
        bool settingEnabled, bool onboardingComplete, bool legalAccepted, bool licenseAllowsRecording)
        => settingEnabled && onboardingComplete && legalAccepted && licenseAllowsRecording;

    /// <summary>
    /// FAIL-OPEN polarity, decided at plan review (item 7, upheld at the final check): stand on
    /// <see cref="CaptureEndpointKind.Microphone"/>, <see cref="CaptureEndpointKind.WiredHeadset"/>
    /// AND <see cref="CaptureEndpointKind.Unknown"/>; refuse only an affirmative
    /// <see cref="CaptureEndpointKind.BluetoothHandsFree"/> — an open capture stream on a BT
    /// hands-free endpoint holds the HFP link active, locking the headset out of A2DP (music
    /// quality) and draining its battery for as long as the app runs. Fail-closed on Unknown was
    /// rejected because it turns classifier failure or exotic-but-legitimate devices into a
    /// silently-dead feature; the residual (HFP lock on a MISCLASSIFIED BT device) requires losing
    /// the form-factor signal — marker-loss alone classifies as WiredHeadset, which this gate also
    /// stands on, and that is the accepted trade. Refusal is a STABLE state: it parks, it never
    /// enters the failure backoff.
    /// </summary>
    public static bool IsEndpointStandable(CaptureEndpointKind kind)
        => kind != CaptureEndpointKind.BluetoothHandsFree;

    /// <summary>
    /// The claim refusal matrix. Endpoint kind is deliberately absent — a running standing capture
    /// already passed <see cref="IsEndpointStandable"/> at start.
    ///
    /// <para><b>AUD-21 — why the silence test is "has it EVER delivered a non-zero sample", never
    /// "how long since the last one".</b> An APO/driver noise gate emits byte-identical exact-zero
    /// runs (AUD-10's own analysis; AUD-18 measured the owner's Intel array gating 34.56% of samples
    /// to zero), and this stream runs CONTINUOUSLY — so on a gating endpoint it sits at exact zeros
    /// for minutes between dictations, and the audio immediately before a hotkey press is by
    /// definition the quiet room. A recent-silence rule would therefore refuse every claim for a
    /// known-real population, permanently disabling instant recording for them. "Never, since this
    /// stream started" cannot express that failure: a gating endpoint passes the moment ANY sound
    /// crosses it, and a stuck capture client never does.</para>
    ///
    /// <para>Both new parameters are REQUIRED rather than defaulted, mirroring
    /// <c>ResolvedRecordingDevice.DeviceTag</c>'s "no default value on purpose": a default would let
    /// a future call site silently opt out of the check instead of failing to compile.</para>
    /// </summary>
    /// <param name="everDeliveredNonZero">Has this stream delivered at least one non-zero sample
    /// since it started? Monotonic — once true it stays true for the life of the stream.</param>
    /// <param name="streamAgeMs">How long the stream has been running. Guards against condemning a
    /// stream too young to have proven anything (<see cref="MinSilenceEvidenceMs"/>).</param>
    public static StandingClaimVerdict ClassifyClaim(
        bool running,
        bool alreadyClaimed,
        double lastDataAgeMs,
        bool everDeliveredNonZero,
        double streamAgeMs)
    {
        if (!running) return StandingClaimVerdict.NotRunning;
        if (alreadyClaimed) return StandingClaimVerdict.AlreadyClaimed;
        // Staleness keeps precedence over silence: a stream that stopped delivering is the stronger
        // fault and its handling is the established one. A stalled stream is also trivially
        // "never delivered non-zero" once its last live chunk ages out, so ordering the other way
        // would relabel every stall as a silence.
        if (lastDataAgeMs > MaxClaimDataAgeMs) return StandingClaimVerdict.StaleData;
        if (!everDeliveredNonZero && streamAgeMs >= MinSilenceEvidenceMs)
            return StandingClaimVerdict.SilentStream;
        return StandingClaimVerdict.Claimable;
    }

    /// <summary>Debounce + failure backoff for rebuilds: a healthy trigger coalesces at 500 ms
    /// (Windows fires several role callbacks per default-device change); consecutive failures
    /// back off 1 s → 2 s → 5 s → 30 s cap so a flapping device can't spin the loop.</summary>
    public static TimeSpan NextRebuildDelay(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => TimeSpan.FromMilliseconds(500),
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(30),
    };

    /// <summary>B5: no teardown OR rebuild executes while a claim is active — the request latches
    /// and runs at claim detach. A mid-drain capture death still stops delivering data (the VM's
    /// existing 3 s stall warning is the surface); what it must never do is dispose the stream
    /// out from under the drain.</summary>
    public static StandingLifecycleDecision DecideLifecycleRequest(bool claimActive)
        => claimActive ? StandingLifecycleDecision.LatchUntilDetach : StandingLifecycleDecision.ExecuteNow;

    /// <summary>See <see cref="ParkAfterConsecutiveFailures"/>.</summary>
    public static bool ShouldParkAfterFailures(int consecutiveFailures)
        => consecutiveFailures >= ParkAfterConsecutiveFailures;
}
