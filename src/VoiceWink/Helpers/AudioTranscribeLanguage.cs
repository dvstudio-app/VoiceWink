using VoiceWink.Models;
using VoiceWink.ViewModels;

namespace VoiceWink.Helpers;

/// <summary>TRN-73: the Audio Transcribe page's recognition language — one resolution for the request and the "Language:" line.</summary>
internal static class AudioTranscribeLanguage
{
    /// <summary>Requested feeds the filler stage; Effective is what the file is decoded in (Parakeet: always auto).</summary>
    internal readonly record struct Choice(string Requested, string Effective);

    /// <summary>TRN-100: the active prompt's language override while AI enhancement is on, else the global setting (PRM-5's order; a file has no App Mode). AI off can leave a prompt active, so the switch gates it.</summary>
    internal static Choice Resolve(string? modelName, bool enhancementEnabled, CustomPrompt? activePrompt, string? globalSetting)
    {
        var (requested, _) = TranscriptionLanguageResolver.Resolve(
            retryLanguage: null,
            effectivePrePrompt: TranscriptionLanguageResolver.ForRecognition(
                activePrompt, activePrompt == null ? PromptSource.None : PromptSource.Active, enhancementEnabled).Prompt,
            appModeLanguage: null,
            globalLanguage: globalSetting);
        return new(requested, EffectiveTranscriptionLanguage.ForModelName(modelName, requested).Language);
    }

    /// <summary>The page's model line; an unlisted language code is never echoed (imported settings can carry any string).</summary>
    internal static string DisplayLine(string modelName, string? requestedLanguage)
    {
        var effective = EffectiveTranscriptionLanguage.ForModelName(
            CloudModels.Canonicalize(modelName), requestedLanguage ?? EffectiveTranscriptionLanguage.Auto).Language;
        var language = ModelManagementViewModel.LanguageDisplayNames.TryGetValue(effective, out var name)
            ? name
            : "(unrecognised language)";
        return $"Model: {ModelDisplayName.Resolve(modelName)} · Language: {language}";
    }

    /// <summary>A running job's line wins over current settings, which may have changed since it resolved.</summary>
    internal static string ShownLine(string? activeJobLine, string modelName, bool enhancementEnabled, CustomPrompt? activePrompt, string? globalSetting) =>
        activeJobLine ?? DisplayLine(modelName, Resolve(modelName, enhancementEnabled, activePrompt, globalSetting).Requested);
}
