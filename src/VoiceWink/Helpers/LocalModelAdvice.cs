using VoiceWink.Services.AIEnhancement.LocalEngine;

namespace VoiceWink.Helpers;

/// <summary>LAI-19: the lines that say what a built-in model is recommended for, from its measured rates. Pure.</summary>
internal static class LocalModelAdvice
{
    /// <summary>A prompt class is weak on a model above this share of bad or refused replies, in percent (owner, 2026-11-12).</summary>
    internal const int WeakAbovePercent = 15;

    /// <summary>The line under a prompt's card, or null when the model it runs on does well on it.</summary>
    /// <param name="model">The built-in model the prompt runs on; null when it runs elsewhere.</param>
    /// <param name="pick">The built-in model recommended for this PC, or null.</param>
    internal static string? PromptLine(LocalModelEntry? model, LocalPromptClass promptClass, LocalModelEntry? pick)
    {
        if (model is null || !IsWeak(model, promptClass))
            return null;
        if (pick is not null && !IsWeak(pick, promptClass))
            return $"On this PC, {pick.DisplayName} is recommended for this enhancement.";
        return promptClass == LocalPromptClass.Cleanup
            ? "On this PC, a cloud model is recommended for spoken corrections and Dictionary words."
            : "On this PC, a cloud model is recommended for this enhancement.";
    }

    /// <summary>The line on a model's card: what it is recommended for, and a cloud model for the rest; null when there is nothing to say.</summary>
    internal static string? CardLine(LocalModelEntry model)
    {
        var classes = new[] { LocalPromptClass.Cleanup, LocalPromptClass.EmailChat, LocalPromptClass.Assistant, LocalPromptClass.Translate };
        var weak = classes.Where(c => IsWeak(model, c)).ToList();
        if (weak.Count == 0)
            // The smallest model is never recommended (owner decision D1), so its card names no strengths.
            return model.Tier == LocalModelTier.Light ? null : "Recommended for all enhancements.";
        if (weak.Count == classes.Length || model.Tier == LocalModelTier.Light)
            // No larger model exists above the largest.
            return model.Tier == LocalModelTier.Best
                ? "A cloud model is recommended."
                : "A larger model or a cloud model is recommended.";
        return $"Recommended for {Join(classes.Except(weak).SelectMany(Names))}. For other enhancements, a cloud model is recommended.";
    }

    // A custom prompt is not measured, so it is never weak.
    private static bool IsWeak(LocalModelEntry model, LocalPromptClass promptClass)
    {
        LocalModelBad? rate = promptClass switch
        {
            LocalPromptClass.Cleanup => model.BadRates.Cleanup,
            LocalPromptClass.EmailChat => model.BadRates.EmailChat,
            LocalPromptClass.Translate => model.BadRates.Translate,
            LocalPromptClass.Assistant => model.BadRates.Assistant,
            _ => null,
        };
        // Counts, not a rounded percent: 55 of 360 is over 15%.
        return rate is { } r && r.Failed * 100 > r.Of * WeakAbovePercent;
    }

    private static string[] Names(LocalPromptClass promptClass) => promptClass switch
    {
        LocalPromptClass.Cleanup => ["Improve Transcription"],
        LocalPromptClass.EmailChat => ["E-mail", "Chat"],
        LocalPromptClass.Assistant => ["Assistant"],
        _ => ["translation"],
    };

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }
}
