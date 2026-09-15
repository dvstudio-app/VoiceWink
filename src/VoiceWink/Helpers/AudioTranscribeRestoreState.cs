namespace VoiceWink.Helpers;

/// <summary>
/// What <c>AudioTranscribePage</c> should render for a given static transcription snapshot
/// (F27). Null-valued members mean "leave the control as it is" — the in-progress branch
/// deliberately doesn't touch the result card, and a completed run without a status keeps
/// whatever status is showing.
/// </summary>
public readonly record struct AudioTranscribeRestorePlan(
    bool ProgressActive,
    bool CancelVisible,
    string? StatusText,
    bool? ResultCardVisible,
    bool? CopyVisible,
    string? ResultText);

/// <summary>
/// Pure presentation rule for restoring/refreshing the Audio Transcribe page from the
/// process-wide transcription snapshot (F27) — extracted from the page so the matrix
/// (fresh / in-progress / non-empty success / empty success / failure / cancel) is
/// unit-testable. The load-bearing rule (Codex PR-3 R1+R2): the result card is visible
/// IFF the completed run produced non-empty text — failure and cancel paths store an
/// empty string, and rendering their blank card read as a success.
/// </summary>
public static class AudioTranscribeRestoreState
{
    /// <summary>
    /// Returns null when nothing should change (fresh state — no run has happened
    /// in this process).
    /// </summary>
    public static AudioTranscribeRestorePlan? Decide(bool isTranscribing, string? lastResult, string? lastStatus)
    {
        if (isTranscribing)
        {
            return new AudioTranscribeRestorePlan(
                ProgressActive: true,
                CancelVisible: true,
                StatusText: "Transcription in progress...",
                ResultCardVisible: null,
                CopyVisible: null,
                ResultText: null);
        }

        if (lastResult == null && lastStatus == null)
            return null; // fresh — leave the page in its default state

        var hasResult = !string.IsNullOrEmpty(lastResult);
        return new AudioTranscribeRestorePlan(
            ProgressActive: false,
            CancelVisible: false,
            StatusText: lastStatus,
            ResultCardVisible: hasResult,
            CopyVisible: hasResult,
            ResultText: hasResult ? lastResult : null);
    }
}
