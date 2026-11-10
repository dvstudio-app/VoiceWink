namespace VoiceWink.Helpers;

/// <summary>What happens to a recording WAV when its pipeline life ends (REL-17).</summary>
public enum RecordingEndDisposition
{
    /// <summary>Delete now (today's default).</summary>
    Delete,
    /// <summary>Leave in place — an armed retry owns it (REL-12; ledger-tracked).</summary>
    RetainForRetry,
    /// <summary>Move to <c>Recordings\Debug\</c> — the keep-recordings debug setting is ON
    /// (location is the retention marker; the unconditional 7-day sweep owns lifetime).</summary>
    RetainForDebug,
    /// <summary>Leave in place — the delivered recording becomes the last-recording slot (REL-20;
    /// ledger-tracked). Keep-recordings applies when the slot later discards it.</summary>
    RetainAsLast,
}

/// <summary>
/// Pure end-of-life decision for a recording WAV at the transcription pipeline's cleanup
/// sites (REL-17). Precedence: an armed retry ALWAYS keeps its WAV (REL-12's whole point) >
/// an explicit user cancel ALWAYS discards (a discarded take is not a diagnostic — recorded
/// scope decision, plan round 2) > a delivered recording is kept as the last recording (REL-20) >
/// the keep-recordings setting retains for debugging > delete. Executor stays in
/// <c>MainViewModel</c> (move/delete + ledger bookkeeping); pinned by <c>RecordingDispositionTests</c>.
/// </summary>
public static class RecordingDisposition
{
    public static RecordingEndDisposition Decide(bool keepForRetry, bool userCancelled, bool keepForDebug)
        => Decide(keepForRetry, userCancelled, delivered: false, keepForDebug);

    public static RecordingEndDisposition Decide(bool keepForRetry, bool userCancelled, bool delivered, bool keepForDebug)
        => keepForRetry ? RecordingEndDisposition.RetainForRetry
         : userCancelled ? RecordingEndDisposition.Delete
         : delivered ? RecordingEndDisposition.RetainAsLast
         : keepForDebug ? RecordingEndDisposition.RetainForDebug
         : RecordingEndDisposition.Delete;
}
