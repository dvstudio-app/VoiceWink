namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// LAI-1: what a provider is CALLED in a provider dropdown, and the way back.
/// </summary>
/// <remarks>
/// Every provider combo holds display strings and parses the selection back, so the two
/// directions must be a bijection over the enum — a label two providers could produce, or one the
/// reverse does not recover, silently turns a selection into a different provider. Every provider
/// except <see cref="AIProvider.LocalServer"/> displays as its member name, which is what the
/// combos showed before this type existed; settings, history labels and logs keep the member name.
/// Pinned by <c>AIProviderDisplayTests</c>.
/// </remarks>
public static class AIProviderDisplay
{
    public const string LocalServerLabel = "Local server";

    public static string Label(AIProvider provider)
        => provider == AIProvider.LocalServer ? LocalServerLabel : provider.ToString();

    public static bool TryParse(string? label, out AIProvider provider)
    {
        if (string.Equals(label, LocalServerLabel, StringComparison.Ordinal))
        {
            provider = AIProvider.LocalServer;
            return true;
        }
        // Member names too, so a label written before this type existed still parses. Ordinal and
        // name-only: Enum.TryParse would also accept "7" or " openai ".
        foreach (var candidate in Enum.GetValues<AIProvider>())
        {
            if (string.Equals(label, candidate.ToString(), StringComparison.Ordinal))
            {
                provider = candidate;
                return true;
            }
        }
        provider = default;
        return false;
    }
}
