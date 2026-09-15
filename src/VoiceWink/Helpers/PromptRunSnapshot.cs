using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>Where the effective prompt for a recording came from (PRM-4).</summary>
public enum PromptSource { None, Hotkey, Trigger, AppMode, Active }

/// <summary>
/// PRM-4 Option B: ONE frozen, deep-cloned view of the prompt set for a single
/// dictation. Hint composition, trigger detection, App-Mode lookup, and active
/// resolution ALL read this snapshot, so a settings edit mid-transcription (which
/// rebuilds the live cache, and can even mutate cached prompt objects in place) can't
/// make them disagree. Resolution returns an explicit <see cref="PromptSource"/> so
/// callers gate on the SOURCE (e.g. "trigger-selected image") rather than brittle
/// reference identity.
/// </summary>
public sealed class PromptRunSnapshot
{
    /// <summary>Cloned prompt candidates — the frozen list detection/composition scan.</summary>
    public IReadOnlyList<CustomPrompt> Candidates { get; }
    /// <summary>The globally-active prompt, DERIVED from <see cref="Candidates"/>.</summary>
    public CustomPrompt? Active { get; }
    /// <summary>The hotkey/retry prompt override for this run, cloned (never a live ref).</summary>
    public CustomPrompt? HotkeyOverride { get; }

    private PromptRunSnapshot(IReadOnlyList<CustomPrompt> candidates, CustomPrompt? active, CustomPrompt? hotkeyOverride)
    {
        Candidates = candidates;
        Active = active;
        HotkeyOverride = hotkeyOverride;
    }

    /// <summary>
    /// Deep-clone the live prompt list and the hotkey override; derive the active
    /// prompt from the clones (mirrors <c>AIEnhancementService.GetActivePrompt</c> =
    /// first IsActive). Capture ONCE before transcription.
    /// </summary>
    public static PromptRunSnapshot Capture(IEnumerable<CustomPrompt> livePrompts, CustomPrompt? liveHotkeyOverride)
    {
        var candidates = livePrompts.Select(p => p.Clone()).ToList();
        var active = candidates.FirstOrDefault(p => p.IsActive);
        return new PromptRunSnapshot(candidates, active, liveHotkeyOverride?.Clone());
    }

    /// <summary>App-Mode linked-enhancement lookup against the frozen candidates.</summary>
    public CustomPrompt? FindById(string? id)
        => string.IsNullOrEmpty(id) ? null : Candidates.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Resolve the effective prompt with its source. Priority (unchanged):
    /// hotkey → trigger → app-mode → globally-active. <paramref name="triggerPrompt"/>
    /// must be a candidate from THIS snapshot (detection scanned it).
    /// </summary>
    public (CustomPrompt? Prompt, PromptSource Source) Resolve(
        bool triggerFired, CustomPrompt? triggerPrompt, CustomPrompt? appModePrompt)
    {
        if (HotkeyOverride != null) return (HotkeyOverride, PromptSource.Hotkey);
        if (triggerFired && triggerPrompt != null) return (triggerPrompt, PromptSource.Trigger);
        if (appModePrompt != null) return (appModePrompt, PromptSource.AppMode);
        if (Active != null) return (Active, PromptSource.Active);
        return (null, PromptSource.None);
    }
}
