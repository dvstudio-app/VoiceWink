namespace VoiceWink.Helpers;

/// <summary>
/// The per-recording state behind <see cref="AudioStallPolicy"/> — which session this recording is
/// watching, and which warning is on screen — with the lifecycle transitions the ViewModel drives
/// (AUD-11).
/// </summary>
/// <remarks>
/// <para>Extracted from <c>MainViewModel</c> because the two bare fields it replaces were the
/// change's one genuinely untestable part: `MainViewModel` needs 20 constructor dependencies
/// (`MainViewModelGateTests` says so in its own header), so nothing could execute "warm start
/// primes the tick before the capture attaches" or "the shown warning is cleared per recording".
/// A pure policy test cannot cover either — it is handed the state it is supposed to be proving.
/// Both gaps were raised twice in review before this type existed.</para>
///
/// <para>Not thread-safe by design: every caller is the UI thread (the meter tick and the
/// recording lifecycle both run there), matching the fields it replaced.</para>
/// </remarks>
internal sealed class AudioStallMonitor
{
    private AudioStallWarning _shown;
    private long _expectedSessionId;

    /// <summary>The warning currently on screen. Exposed for assertions and diagnostics.</summary>
    internal AudioStallWarning Shown => _shown;

    /// <summary>The session being watched, or 0 when no capture is attached.</summary>
    internal long ExpectedSessionId => _expectedSessionId;

    /// <summary>
    /// A recording is starting. Clears the shown warning and the watched session together.
    /// </summary>
    /// <param name="captureSessionId">The live capture's session, or <b>0 when none is attached
    /// yet</b> — which the warm path must pass, because it starts the meter tick before it
    /// attaches, and then calls <see cref="AttachCapture"/>.</param>
    /// <remarks>
    /// Clearing the shown warning here is not optional. The typed warning is stickier than the
    /// bool it replaced — a shown <see cref="AudioStallWarning.RecorderDead"/> deliberately
    /// suppresses later verdicts so a flapping capture cannot strobe the pill — so a missed reset
    /// would silence the watchdog for the rest of the process, which is the AUD-11 failure shape
    /// as a steady state. Clearing the session guarantees a recording can never inherit its
    /// predecessor's id and report a dead recorder on its first tick.
    /// </remarks>
    internal void BeginRecording(long captureSessionId)
    {
        _shown = AudioStallWarning.None;
        _expectedSessionId = captureSessionId;
    }

    /// <summary>The capture is live; start watching it. The warm path's second half.</summary>
    internal void AttachCapture(long captureSessionId) => _expectedSessionId = captureSessionId;

    /// <summary>
    /// Stop watching — an intentional stop, or any teardown. Called BEFORE a deliberate stop so
    /// the window where the state still says Recording and the recorder has stopped stays silent.
    /// </summary>
    internal void Detach() => _expectedSessionId = 0;

    /// <summary>
    /// Evaluate this tick and record whatever warning it produces, so the caller only has to
    /// render it.
    /// </summary>
    internal AudioStallVerdict Evaluate(bool isRecordingState, long recorderSessionId, TimeSpan silence)
    {
        var verdict = AudioStallPolicy.Evaluate(
            isRecordingState, _expectedSessionId, recorderSessionId, silence, _shown);

        _shown = verdict switch
        {
            AudioStallVerdict.WarnRecorderDead => AudioStallWarning.RecorderDead,
            AudioStallVerdict.WarnStall => AudioStallWarning.Stall,
            AudioStallVerdict.Recovered => AudioStallWarning.None,
            _ => _shown,
        };

        return verdict;
    }
}
