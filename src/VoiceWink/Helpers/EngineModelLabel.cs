using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AIEnhancement.LocalEngine;

namespace VoiceWink.Helpers;

/// <summary>
/// What a VoiceWink Engine model is CALLED ("Qwen3.5 4B") versus what is stored and sent
/// ("qwen3.5-4b-q4km"). Settings, prompt overrides and history keep the id; every model combo that
/// lists the engine's models shows the name and maps the pick back through <see cref="Id"/>.
/// </summary>
/// <remarks>Provider-scoped both ways: only <see cref="AIProvider.OnThisPc"/> is mapped, so a cloud
/// or Local server model whose id happens to equal an engine name is never rewritten. Anything that
/// is not a catalog name or id passes through unchanged (typed text, an id from an older catalog).
/// Pinned by <c>EngineModelLabelTests</c>.</remarks>
internal static class EngineModelLabel
{
    internal static string Label(AIProvider? provider, string id)
        => provider == AIProvider.OnThisPc && LocalModelCatalog.Find(id) is { } entry
            ? entry.DisplayName
            : id;

    internal static string Id(AIProvider? provider, string text)
        => provider == AIProvider.OnThisPc
           && LocalModelCatalog.All.FirstOrDefault(e => string.Equals(e.DisplayName, text, StringComparison.Ordinal)) is { } entry
            ? entry.Id
            : text;

    internal static List<string> Labels(AIProvider? provider, IEnumerable<string> ids)
        => ids.Select(id => Label(provider, id)).ToList();

    /// <summary>A history row's stored "Provider/model" as the user reads it: the provider's
    /// dropdown label and the engine model's name ("VoiceWink Engine/Qwen3.5 4B"). A label that does
    /// not parse is shown as stored.</summary>
    internal static string? ForHistory(string? stored)
    {
        var (provider, model) = ProviderModelLabel.Parse(stored);
        return provider is { } p && model is not null
            ? $"{AIProviderDisplay.Label(p)}/{Label(p, model)}"
            : stored;
    }
}
