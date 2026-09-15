using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// ENH-6d: parse a history row's persisted "Provider/model" EnhancementModelName
/// label back into its parts. The FIRST slash splits — models may themselves contain
/// slashes (OpenRouter's "google/gemini-2.5-flash-image"), so everything after it is
/// the model verbatim. The model survives an unknown provider token (a renamed enum
/// member must not discard the still-useful model string); both parts are null for
/// absent/unparsable labels. Shared by HistoryPage's Regenerate and Iterate buttons
/// so both dialogs take over the row's exact generation settings.
/// </summary>
public static class ProviderModelLabel
{
    public static (AIProvider? Provider, string? Model) Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return (null, null);
        var slashIdx = label.IndexOf('/');
        if (slashIdx <= 0)
            return (null, null);
        AIProvider? provider = Enum.TryParse<AIProvider>(label[..slashIdx], out var parsed) ? parsed : null;
        return (provider, label[(slashIdx + 1)..]);
    }
}
