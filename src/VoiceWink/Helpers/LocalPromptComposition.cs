using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-12 (2026-10-08): which system and user messages an enhancement request sends. Every provider
/// and prompt gets the envelope (<see cref="AIPrompts.BuildSystemPrompt"/>) and the framed transcript
/// (<see cref="AIPrompts.BuildUserPrompt"/>) — except the unedited shipped Assistant prompt on a LOCAL
/// provider (Local server, Built-in models), which gets <see cref="AIPrompts.BuildLocalAnswerSystemPrompt"/>
/// and the raw dictation. Measured: the envelope stops small local models answering (bench README,
/// "Assistant prompt: answer or echo"); cloud models answered under it in every run, so they keep it.
/// An edited Assistant prompt is a custom prompt and keeps the envelope, like any other.
/// </summary>
internal static class LocalPromptComposition
{
    internal static bool UsesAnswerPrompt(AIProvider provider, CustomPrompt prompt)
        => LocalOutputGuard.Applies(provider)
           && LocalPromptClassifier.Classify(prompt) == LocalPromptClass.Assistant;

    internal static (string System, string User) Compose(
        AIProvider provider, CustomPrompt prompt, string transcribedText, IReadOnlyList<string>? vocabularyTerms)
        => UsesAnswerPrompt(provider, prompt)
            ? (AIPrompts.BuildLocalAnswerSystemPrompt(vocabularyTerms), transcribedText)
            : (AIPrompts.BuildSystemPrompt(prompt.PromptText, vocabularyTerms), AIPrompts.BuildUserPrompt(transcribedText));
}
