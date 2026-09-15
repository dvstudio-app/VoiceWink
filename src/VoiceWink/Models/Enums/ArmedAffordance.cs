namespace VoiceWink.Models.Enums;

/// <summary>
/// Which "act on the last attempt" affordance was armed MOST RECENTLY — the pill's
/// recency selector (REL-12). Redo (re-run enhancement from a transcript) and retry
/// (re-run transcription from retained audio) are exclusive within one pipeline
/// attempt but can BOTH be armed over time (a History-page redo arms redo while a
/// failed dictation's retry stays armed). A fixed precedence order is wrong in one
/// direction or the other, so rendering (<c>MiniRecorderLifecycleState</c>) and the
/// redo-last hotkey routing both follow whichever armed last, falling back to
/// whichever still exists.
/// </summary>
public enum ArmedAffordance
{
    None,
    Redo,
    Retry,
}
