using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>Which text-enhancement card carries the green "Active" marker. Public only so a test
/// theory can take it as a parameter.</summary>
public enum ActiveTextCardKind
{
    None,
    BuiltIn,
    Provider,
}

/// <summary>
/// The ONE rule behind the "Active" marker on setup's AI step and the AI Enhancement page
/// (2026-10-09, owner): the marker names what dictations will actually use. The provider card is
/// marked only while its box shows the provider in use, and never for a cloud provider without an
/// API key — setup marked a keyless OpenAI "Active" while the user was downloading a built-in model.
/// </summary>
internal static class ActiveTextCard
{
    /// <param name="inUse">The provider dictations use (the active prompt's override first), or
    /// null when none is stored.</param>
    /// <param name="shown">The provider the provider card's box shows, or null when it shows none.</param>
    /// <param name="requiresKey">The in-use provider needs an API key (false for Local server).</param>
    /// <param name="hasKey">A key is stored for the in-use provider.</param>
    internal static ActiveTextCardKind Resolve(AIProvider? inUse, AIProvider? shown, bool requiresKey, bool hasKey)
    {
        if (inUse is not { } provider)
            return ActiveTextCardKind.None;
        if (provider == AIProvider.OnThisPc)
            return ActiveTextCardKind.BuiltIn;
        if (shown != provider || (requiresKey && !hasKey))
            return ActiveTextCardKind.None;
        return ActiveTextCardKind.Provider;
    }
}
