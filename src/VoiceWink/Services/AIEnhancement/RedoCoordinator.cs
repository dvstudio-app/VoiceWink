using VoiceWink.ViewModels;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Owns the retained state of the two "act on the last attempt" affordances: the most recent
/// <see cref="MainViewModel.RedoContext"/> (redo = re-run enhancement from a transcript) and the
/// most recent <see cref="MainViewModel.TranscriptionRetryContext"/> (retry = re-run transcription
/// from failure-retained audio, REL-12).
///
/// Dismiss TIMING is no longer here (ERR-PERSIST controller refactor, 2026-07-21): the single-owner
/// <see cref="VoiceWink.Views.MiniRecorderPresentationController"/> owns every pill's lifetime,
/// pause/resume, and dismissal. This class is now purely the two context slots plus their
/// independent teardown — <see cref="Clear"/> is redo-only (History-page picker cancel / redo start
/// must never tear down an armed retry; retry teardown is the explicit
/// <c>MainViewModel.ClearRetryState</c>).
///
/// The coordinator does NOT own the <c>IsRedoAvailable</c>/<c>RedoTone</c>/<c>IsRetryAvailable</c>/
/// <c>ArmedAffordance</c> observables — those remain on <c>MainViewModel</c> so the WinUI binding
/// paths continue to work (<c>App.xaml.cs</c> translates them into controller arm/retire calls).
/// </summary>
public sealed class RedoCoordinator
{
    /// <summary>Snapshot of the most-recent enhancement, or null if redo is not armed.</summary>
    public MainViewModel.RedoContext? LastContext { get; set; }

    /// <summary>Snapshot of the most-recent failed transcription (retained WAV + the
    /// recording-scoped inputs needed to re-run it), or null if retry is not armed (REL-12).
    /// Deliberately NOT touched by <see cref="Clear"/> — redo teardown paths (History-page
    /// picker cancel, redo start) must never tear down an armed retry.</summary>
    public MainViewModel.TranscriptionRetryContext? LastRetryContext { get; set; }

    /// <summary>Redo teardown: null the REDO context. Deliberately leaves
    /// <see cref="LastRetryContext"/> alone (a History-page redo cancel while a failed dictation's
    /// retry is armed must not tear the retry down — a data-loss path in plan v1).</summary>
    public void Clear() => LastContext = null;

    /// <summary>Retry teardown counterpart of <see cref="Clear"/>: null the RETRY context. WAV-file
    /// deletion is the caller's job (<c>MainViewModel.ClearRetryState</c> + <see cref="Helpers.RetainedWavLedger"/>).</summary>
    public void ClearRetry() => LastRetryContext = null;
}
