namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-3/LAI-4: is the built-in local AI engine's UI shown? True in every build since LAI-4 shipped
/// the "On this PC" provider that uses a downloaded model; LAI-3 kept it Debug-only because a Release
/// build between the two would have offered a 1.3–6.7 GB download that did nothing. Kept as a switch
/// so the whole surface can be turned off in one place. <c>static readonly</c>, not <c>const</c>: a
/// const makes the gated branch unreachable code (CS0162), the <see cref="ImageGenerationFeature"/>
/// precedent. Architecture is <see cref="OnThisPcAvailability"/>'s question, not this one's.
/// </summary>
internal static class LocalEngineFeature
{
    public static readonly bool IsEnabled = true;
}
