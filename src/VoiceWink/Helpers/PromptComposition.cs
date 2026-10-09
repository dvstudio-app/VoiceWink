using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-12: which system and user messages an enhancement request sends. Every prompt gets the
/// envelope (<see cref="AIPrompts.BuildSystemPrompt"/>) and the framed transcript
/// (<see cref="AIPrompts.BuildUserPrompt"/>) — except the unedited shipped Assistant prompt, which gets
/// <see cref="AIPrompts.BuildAssistantAnswerSystemPrompt"/> and the raw dictation, on every provider.
/// Measured: under the envelope small models echo the question back instead of answering — local
/// (Qwen3.5 2B 14/60 answered) and cloud alike (OpenAI gpt-4.1-nano 23/60, Groq allam-2-7b 21/60);
/// the answer prompt lifted both, and no model measured lost more than one answer on it (gemma-3-27b
/// 59 to 58; bench README, "Assistant prompt: answer or echo"). An edited Assistant prompt is a
/// custom prompt and keeps the envelope.
/// </summary>
internal static class PromptComposition
{
    internal static bool UsesAnswerPrompt(CustomPrompt prompt)
        => LocalPromptClassifier.Classify(prompt) == LocalPromptClass.Assistant;

    internal static (string System, string User) Compose(
        CustomPrompt prompt, string transcribedText, IReadOnlyList<string>? vocabularyTerms)
        => UsesAnswerPrompt(prompt)
            ? (AIPrompts.BuildAssistantAnswerSystemPrompt(vocabularyTerms), transcribedText)
            : (AIPrompts.BuildSystemPrompt(prompt.PromptText, vocabularyTerms), AIPrompts.BuildUserPrompt(transcribedText));
}
