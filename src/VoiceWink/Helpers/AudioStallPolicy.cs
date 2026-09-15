namespace VoiceWink.Helpers;

/// <summary>Which warning the meter tick is currently showing.</summary>
internal enum AudioStallWarning
{
    /// <summary>Nothing shown; the recording looks healthy.</summary>
    None,

    /// <summary>Our capture is live but no audio has arrived for a while.</summary>
    Stall,

    /// <summary>Our capture is gone while the UI still says Recording.</summary>
    RecorderDead,
}

/// <summary>What the tick should do about it.</summary>
internal enum AudioStallVerdict
{
    None,
    WarnStall,
    WarnRecorderDead,
    Recovered,
}

/// <summary>
/// The meter tick's decision: is this recording healthy, silent, or being captured by nobody
/// (AUD-11).
/// </summary>
/// <remarks>
/// <para>Before AUD-11 the tick's guard was <c>RecordingState == Recording &amp;&amp;
/// _recorder.IsRecording</c>, which made the whole safety net structurally blind to the one
/// failure it most needed to report: a recorder that is not recording at all. On 2026-08-06 a
/// superseded attempt's queued stop killed its successor's capture 5 ms after it started, and
/// because the recorder was then stopped, the watchdog said nothing for the 17 s the user spent
/// dictating into it.</para>
///
/// <para><b>Why an expected session id rather than a bool.</b> "Is MY capture still live" cannot
/// be answered by <c>IsRecording</c>: a SUCCESSOR capture leaves it true while our audio is gone.
/// Comparing session ids answers the real question, and it collapses the death check and the
/// not-attached-yet check into one comparison.</para>
///
/// <para><b>The pre-attach window is why rule 2 exists</b> and it is not hypothetical: the AUD-6
/// warm path flips to Recording and starts the meter timer BEFORE it attaches the capture, and
/// <c>StartMeterTimer</c> primes the first tick synchronously. In that window nothing is recording
/// and <c>LastDataReceivedAt</c> still holds the PREVIOUS recording's stamp (or default), so a
/// policy that keyed on "not recording + long silence" would fire on the prime tick of every warm
/// start — and instant recording is on by default. An earlier draft of this fix did exactly that;
/// both plan reviewers caught it.</para>
/// </remarks>
internal static class AudioStallPolicy
{
    /// <summary>No audio for this long, with our capture live, is a stall.</summary>
    internal static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(3);

    /// <summary>Audio this recent clears a shown stall warning.</summary>
    internal static readonly TimeSpan RecoveredWithin = TimeSpan.FromSeconds(1);

    /// <param name="isRecordingState">The VM is in <c>RecordingState.Recording</c>.</param>
    /// <param name="expectedSessionId">The session the VM believes it is recording, or 0 when it
    /// has not attached one yet (warm pre-attach) or has deliberately let go of it (any
    /// intentional stop).</param>
    /// <param name="recorderSessionId">The recorder's live session, or 0 when nothing is
    /// recording. One atomic read — see <c>AudioRecorderService.CurrentSessionId</c>.</param>
    /// <param name="silence">Time since the last delivered audio buffer.</param>
    /// <param name="shown">The warning currently on screen.</param>
    internal static AudioStallVerdict Evaluate(
        bool isRecordingState,
        long expectedSessionId,
        long recorderSessionId,
        TimeSpan silence,
        AudioStallWarning shown)
    {
        if (!isRecordingState) return AudioStallVerdict.None;

        // Nothing of ours is attached — the warm pre-attach window, or after an intentional stop
        // whose state change has not landed yet. Neither is a fault.
        if (expectedSessionId == 0) return AudioStallVerdict.None;

        if (recorderSessionId != expectedSessionId)
        {
            // Our capture is gone: stopped, died, or replaced by a successor. Unambiguous, so it
            // needs no silence threshold — the 3 s wait below exists only to tell a slow buffer
            // apart from a dead one, and there is nothing slow about a capture that is not there.
            // Dead outranks a shown stall; a stall never overwrites a shown death.
            return shown == AudioStallWarning.RecorderDead
                ? AudioStallVerdict.None
                : AudioStallVerdict.WarnRecorderDead;
        }

        if (silence > StallAfter && shown == AudioStallWarning.None)
            return AudioStallVerdict.WarnStall;

        // Only a STALL recovers. A dead recorder cannot come back — its session is gone for good —
        // so the status must never flip back to "Recording..." over one.
        if (silence <= RecoveredWithin && shown == AudioStallWarning.Stall)
            return AudioStallVerdict.Recovered;

        return AudioStallVerdict.None;
    }
}
