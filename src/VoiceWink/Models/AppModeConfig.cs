namespace VoiceWink.Models;

/// <summary>
/// Per-app recording configuration.
/// Auto-activates based on focused window process name.
/// </summary>
public class AppModeConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// UPD-4 provenance: the immutable <see cref="AppModeTemplate.Key"/> this config was seeded
    /// from (null for user-created configs). How <c>DefaultPromptsReseed</c> matches a persisted
    /// default App-Mode config across versions. The runtime <see cref="Id"/> stays a GUID.
    /// </summary>
    public string? SeedKey { get; set; }

    public string Name { get; set; } = string.Empty;
    public string[] ProcessPatterns { get; set; } = [];
    public string? ModelOverride { get; set; }
    public string? LanguageOverride { get; set; }

    /// <summary>
    /// Optional link to a CustomPrompt by Id. When this App Mode config triggers,
    /// the linked enhancement is used instead of the normal active prompt —
    /// even if global AI enhancement is disabled.
    /// </summary>
    public string? LinkedEnhancementId { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    // Legacy field — kept for deserialization compat, no longer used at runtime.
    public string? PromptOverride { get; set; }
}
