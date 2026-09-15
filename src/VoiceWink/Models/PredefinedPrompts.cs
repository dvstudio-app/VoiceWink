namespace VoiceWink.Models;

/// <summary>
/// Default AI enhancement prompts for new users.
/// All prompts are deletable and can be re-added from templates.
/// </summary>
public static class PredefinedPrompts
{
    /// <summary>
    /// Fallback prompt used when enhancement is enabled but no prompt is active.
    /// Rules text comes from the ONE canonical constant — never a duplicated literal
    /// (PRM-1 audit F13).
    /// </summary>
    public static readonly CustomPrompt Default = new()
    {
        Id = "default",
        Title = "Improve Transcription",
        PromptText = PromptTemplates.ImproveAccuracyText,
        Icon = "E8D4",
        Description = "Improve clarity and accuracy of the transcription",
        IsActive = false
    };

    /// <summary>
    /// Initial prompt set for new users (first launch, no saved prompts).
    /// Built from the default template list (excludes translations).
    /// </summary>
    public static CustomPrompt[] All => PromptTemplates.All
        .Select((t, i) => t.ToCustomPrompt(isActive: i == 0))  // first prompt (Improve Transcription) active by default
        .ToArray();
}
