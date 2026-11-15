using System.Text.Json.Serialization;

namespace VoiceWink.Models;

/// <summary>
/// Per-app recording configuration.
/// Auto-activates based on focused window process name.
/// </summary>
public class AppModeConfig : IJsonOnDeserializing, IJsonOnDeserialized
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// UPD-4 provenance: the immutable <see cref="AppModeTemplate.Key"/> this config was seeded
    /// from (null for user-created configs). How <c>DefaultPromptsReseed</c> matches a persisted
    /// default App-Mode config across versions. The runtime <see cref="Id"/> stays a GUID.
    /// </summary>
    public string? SeedKey { get; set; }

    public string Name { get; set; } = string.Empty;
    private string[] _processPatterns = [];

    /// <summary>ENH-40b: a stored JSON null reads as an empty list, so that entry matches nothing and the others keep working.</summary>
    public string[] ProcessPatterns { get => _processPatterns; set => _processPatterns = value ?? []; }
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

    /// <summary>ENH-40c/ENH-43: true when this entry was read with no Id, or with an earlier entry's Id, and was given a new one, which the reader saves.</summary>
    internal bool IdMinted { get; private set; }

    /// <summary>ENH-40c: clears the constructor's Id before the stored fields arrive, so an omitted Id reads like a null one.</summary>
    void IJsonOnDeserializing.OnDeserializing() => Id = string.Empty;

    /// <summary>ENH-40c: a stored missing, null or blank Id gets a new one and a null Name reads as empty, so the entry stays editable and deletable on its own.</summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(Id))
            MintId();
        Name ??= string.Empty;
    }

    private string MintId()
    {
        Id = Guid.NewGuid().ToString();
        IdMinted = true;
        return Id;
    }

    /// <summary>ENH-38: the one rule for "this stored text is a usable App Mode list", shared by the manager and the re-seed.</summary>
    /// <param name="json">The stored <c>appModeConfigs</c> text.</param>
    /// <param name="configs">The list; null for a JSON <c>null</c>, which holds nothing to lose.</param>
    /// <param name="failure">The failure kind when false — an exception type name, never its message (it can quote the text).</param>
    public static bool TryReadList(string json, out List<AppModeConfig>? configs, out string failure)
    {
        try
        {
            configs = global::System.Text.Json.JsonSerializer.Deserialize<List<AppModeConfig>>(json);
        }
        catch (Exception ex)
        {
            configs = null;
            failure = ex.GetType().Name;
            return false;
        }

        if (configs != null && configs.Exists(c => c is null))
        {
            configs = null;
            failure = "null entry";
            return false;
        }

        if (configs != null)
            StoredIdRepair.MintRepeatedIds(configs, static c => c.Id, static c => c.MintId());

        failure = "";
        return true;
    }
}
