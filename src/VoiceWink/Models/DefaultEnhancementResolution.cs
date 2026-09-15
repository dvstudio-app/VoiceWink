namespace VoiceWink.Models;

/// <summary>
/// Outcome of resolving (and possibly adding) the default enhancement an App-Mode template links to,
/// by its stable SeedKey. Drives both the link decision and the user-facing notice.
/// </summary>
public enum DefaultEnhancementOutcome
{
    /// <summary>The template declares no default enhancement (`DefaultEnhancementKey` empty).</summary>
    NoKey,
    /// <summary>Exactly one existing prompt already carries the SeedKey — reuse it, add nothing.</summary>
    LinkedExisting,
    /// <summary>The default enhancement was absent and has been added from its shipped template.</summary>
    Added,
    /// <summary>Absent and could not be added: a prompt with the template's title already exists
    /// (unseeded), so adding would collide. No prompt added, no link.</summary>
    TitleConflict,
    /// <summary>More than one prompt carries the SeedKey — linking would be ambiguous, so none set.</summary>
    AmbiguousSeedKey,
    /// <summary>The SeedKey is not one of the reseed-managed <c>PromptTemplates.All</c> keys — a
    /// developer inconsistency the structural test forbids; treated as "no linkable default".</summary>
    UnknownKey,
}

/// <summary>Result of <c>EnhancementViewModel.ResolveOrAddDefaultEnhancement</c>: the prompt Id to
/// link (null when none), the outcome, and the shipped template title for any notice (never user
/// text).</summary>
public readonly record struct DefaultEnhancementResolution(
    string? PromptId,
    DefaultEnhancementOutcome Outcome,
    string? EnhancementTitle);
