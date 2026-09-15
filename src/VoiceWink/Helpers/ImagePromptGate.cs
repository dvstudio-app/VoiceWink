namespace VoiceWink.Helpers;

/// <summary>
/// Decides whether the recording pipeline runs a TEXT enhancement, given a resolved prompt whose
/// intent may be image generation. An image-generation intent must NEVER be run as a text
/// enhancement: it would pick the image model / a nonsensical "generate an image" instruction, or —
/// after an image-generation failure nulls the prompt — silently clean up and overwrite the
/// transcript. Mirrors the repo's pure-decision-helper pattern (<see cref="ClassifyStopTap"/>,
/// <see cref="PromptModelOverridePolicy"/>). Pinned by ImagePromptGateTests.
/// </summary>
internal static class ImagePromptGate
{
    /// <summary>
    /// The pipeline's text-enhancement gate: the existing "forced prompt or (enhancement enabled and
    /// a prompt is present)" rule, EXCEPT an image-generation intent never text-enhances.
    /// </summary>
    /// <param name="promptWasImageGeneration">Resolved prompt's IsImageGeneration, captured BEFORE the
    /// image branch may null the prompt.</param>
    /// <param name="enhancementEnabled"><c>_enhancement.IsEnabled</c>.</param>
    /// <param name="hasForcedPrompt"><c>hotkeyOverride != null || detection.ShouldOverridePrompt || appModePrompt != null</c>.</param>
    /// <param name="promptPresentAtEnhance"><c>prompt != null</c> at the enhancement decision point
    /// (i.e. AFTER the image branch, which nulls prompt on an image-generation failure).</param>
    public static bool ShouldEnhanceText(
        bool promptWasImageGeneration,
        bool enhancementEnabled,
        bool hasForcedPrompt,
        bool promptPresentAtEnhance)
    {
        // An image intent "would have run" the image branch whenever a forced source or global
        // enhancement applies — the same enabler set the image branch gates on.
        var imageIntent = promptWasImageGeneration && (enhancementEnabled || hasForcedPrompt);

        // The existing inline shouldEnhance, verbatim…
        var baseShouldEnhance = hasForcedPrompt || (enhancementEnabled && promptPresentAtEnhance);
        // …then never text-enhance an image intent (covers the image-failure fall-through, where the
        // prompt is nulled but a forced source keeps baseShouldEnhance true).
        return baseShouldEnhance && !imageIntent;
    }
}
