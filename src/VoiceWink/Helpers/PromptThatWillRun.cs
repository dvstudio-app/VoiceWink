using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>What a prompt asks of the text provider: nothing (an image prompt), or its overrides.</summary>
internal readonly record struct PromptRoute(bool IsImage, AIProvider? ProviderOverride, string? ModelOverride)
{
    /// <summary>A text prompt with a provider or model override of its own.</summary>
    internal bool HasTextOverride => !IsImage && (ProviderOverride is not null || !string.IsNullOrWhiteSpace(ModelOverride));
}

/// <summary>ONB-14: the prompt AI enhancement will run — the AI page's OFF deactivates every prompt and ON restores one, so setup judges that one.</summary>
internal static class PromptThatWillRun
{
    /// <summary>The active prompt; else, while the view model is off, the one its switch restores.</summary>
    internal static CustomPrompt? Resolve(CustomPrompt? active, IReadOnlyList<CustomPrompt> allPrompts,
        string lastActivePromptId, bool viewModelIsEnabled)
    {
        if (active is not null || viewModelIsEnabled)
            return active;
        var last = string.IsNullOrEmpty(lastActivePromptId)
            ? null
            : allPrompts.FirstOrDefault(p => p.Id == lastActivePromptId);
        return last ?? allPrompts.FirstOrDefault(p => p.SeedKey == PromptTemplates.ImproveAccuracy.Key);
    }

    /// <summary>The route read from the app's own state; the one reader setup, Express and the adopter share.</summary>
    /// <param name="viewModelIsEnabled">The AI page view model's cached switch, not the stored one: setup writes the stored switch directly.</param>
    internal static PromptRoute Route(AIEnhancementService enhancement, Services.System.SettingsService settings, bool viewModelIsEnabled)
        => Route(enhancement.GetActivePrompt(), enhancement.GetPrompts(),
            settings.GetString(AppDefaults.LastActivePromptId, ""), viewModelIsEnabled);

    /// <summary>The route of <see cref="Resolve"/>'s prompt; no prompt reads as a text prompt with no overrides.</summary>
    internal static PromptRoute Route(CustomPrompt? active, IReadOnlyList<CustomPrompt> allPrompts,
        string lastActivePromptId, bool viewModelIsEnabled)
    {
        var prompt = Resolve(active, allPrompts, lastActivePromptId, viewModelIsEnabled);
        // Parsed as the enhancement service parses it: an unparseable override is no override.
        AIProvider? provider = !string.IsNullOrWhiteSpace(prompt?.ProviderOverride)
                               && Enum.TryParse<AIProvider>(prompt.ProviderOverride, out var p) ? p : null;
        return new PromptRoute(prompt?.IsImageGeneration == true, provider, prompt?.ModelOverride);
    }
}
