using VoiceWink.Models.Enums;

namespace VoiceWink.Helpers;

/// <summary>What the pill's ↻ and the redo-last hotkey open (REL-20).</summary>
public enum RedoLastAction
{
    /// <summary>Nothing armed; the caller logs it.</summary>
    None,
    /// <summary>The failure-retained recording's Retry picker (REL-12).</summary>
    Retry,
    /// <summary>The AI picker for the armed redo.</summary>
    Redo,
    /// <summary>The Retry picker for the last delivered recording.</summary>
    RetranscribeLast,
    /// <summary>The two-choice dialog: transcribe again, or run the AI again.</summary>
    Chooser,
}

/// <summary>
/// REL-20: pure routing for the redo-last entry. An armed failure retry keeps its recency rule
/// (REL-12); a held last recording opens the chooser only for the redo its own run armed, so a
/// later History redo is never paired with an unrelated recording. Pinned by <c>RedoLastRoutingTests</c>.
/// </summary>
public static class RedoLastRouting
{
    public static RedoLastAction Decide(
        ArmedAffordance armed, bool retryArmed, bool redoArmed, bool lastHeld, bool redoPairedWithHeld)
    {
        if (armed == ArmedAffordance.Retry && retryArmed) return RedoLastAction.Retry;
        if (lastHeld)
        {
            if (!redoArmed) return RedoLastAction.RetranscribeLast;
            if (redoPairedWithHeld) return RedoLastAction.Chooser;
            return armed == ArmedAffordance.Redo ? RedoLastAction.Redo : RedoLastAction.RetranscribeLast;
        }
        if (armed == ArmedAffordance.Redo && redoArmed) return RedoLastAction.Redo;
        if (retryArmed) return RedoLastAction.Retry;
        return redoArmed ? RedoLastAction.Redo : RedoLastAction.None;
    }
}

/// <summary>REL-20: the chooser's two answers.</summary>
public enum RedoChoice
{
    /// <summary>Transcribe the kept recording again (the Retry picker follows).</summary>
    Transcribe,
    /// <summary>Run the AI again on the text (the AI picker follows).</summary>
    Enhance,
}
