namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// LAI-1: what a provider is CALLED in a provider dropdown, and the way back.
/// </summary>
/// <remarks>
/// Every provider combo holds display strings and parses the selection back, so the two
/// directions must be a bijection over the enum — a label two providers could produce, or one the
/// reverse does not recover, silently turns a selection into a different provider. Every provider
/// except <see cref="AIProvider.LocalServer"/> and <see cref="AIProvider.OnThisPc"/> displays as its member name, which is what the
/// combos showed before this type existed; settings, history labels and logs keep the member name.
/// Pinned by <c>AIProviderDisplayTests</c>.
/// </remarks>
public static class AIProviderDisplay
{
    public const string LocalServerLabel = "Local server";
    /// <summary>"Built-in models" since 2026-10-04 (owner: the name the Models page uses for the
    /// speech models that run on this PC).</summary>
    public const string OnThisPcLabel = "Built-in models";

    /// <summary>What <see cref="AIProvider.OnThisPc"/> was called before: "On this PC" until
    /// 2026-09-30, "VoiceWink Engine" until 2026-10-04. Settings, history labels and logs store the
    /// member name, so nothing persisted carries them; they still parse so a label read from
    /// anywhere older resolves instead of failing.</summary>
    internal static readonly string[] LegacyOnThisPcLabels = ["On this PC", "VoiceWink Engine"];

    public static string Label(AIProvider provider)
        => provider switch
        {
            AIProvider.LocalServer => LocalServerLabel,
            AIProvider.OnThisPc => OnThisPcLabel,
            _ => provider.ToString(),
        };

    public static bool TryParse(string? label, out AIProvider provider)
    {
        if (string.Equals(label, LocalServerLabel, StringComparison.Ordinal))
        {
            provider = AIProvider.LocalServer;
            return true;
        }
        if (string.Equals(label, OnThisPcLabel, StringComparison.Ordinal)
            || LegacyOnThisPcLabels.Contains(label, StringComparer.Ordinal))
        {
            provider = AIProvider.OnThisPc;
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
