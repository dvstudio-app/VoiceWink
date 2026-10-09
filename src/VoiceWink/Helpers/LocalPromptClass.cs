using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-4: what a prompt asks a local model to do, as far as output length is concerned. Decides the
/// output cap (<see cref="LocalPromptClassifier.CapFor"/>) and which length checks
/// <see cref="LocalOutputGuard"/> applies.
/// </summary>
public enum LocalPromptClass
{
    /// <summary>The shipped cleanup prompt (and <see cref="PredefinedPrompts.Default"/>).</summary>
    Cleanup,
    /// <summary>The shipped E-mail or Chat prompt.</summary>
    EmailChat,
    /// <summary>One of the shipped translation prompts.</summary>
    Translate,
    /// <summary>An edited shipped prompt or the user's own prompt.</summary>
    Custom,
    /// <summary>The shipped Assistant prompt, unedited (LAI-12: every provider gets the answer-only
    /// system prompt for it, and a local reply to it is never length-checked).</summary>
    Assistant,
}

/// <summary>LAI-4: the local engine's view of one enhancement request (see <c>AIProviderConfig.LocalRequest</c>).</summary>
internal sealed record LocalRequestHints(LocalPromptClass PromptClass, string SourceText);

/// <summary>
/// LAI-4 (design plan §5.3, R3-B2): the prompt class is read from the prompt SNAPSHOT by TEXT — a
/// shipped prompt keeps its class only while its text still equals the shipped template, compared
/// with line endings normalised and outer whitespace trimmed (the WinUI prompt editor writes back a
/// bare CR). Title and <c>SeedKey</c> are never consulted: an edited shipped prompt is custom, a
/// renamed but unchanged copy keeps its class, and the fallback prompt has no key at all.
/// </summary>
internal static class LocalPromptClassifier
{
    private static readonly string CleanupText = Normalize(PromptTemplates.ImproveAccuracyText);
    private static readonly string AssistantText = Normalize(PromptTemplates.Assistant.PromptText);
    private static readonly string[] EmailChatTexts =
        [Normalize(PromptTemplates.Email.PromptText), Normalize(PromptTemplates.Chat.PromptText)];
    private static readonly string[] TranslateTexts =
    [
        Normalize(PromptTemplates.TranslateDutch.PromptText),
        Normalize(PromptTemplates.TranslateChinese.PromptText),
        Normalize(PromptTemplates.TranslateEnglish.PromptText),
    ];

    internal static LocalPromptClass Classify(CustomPrompt? prompt)
    {
        var text = Normalize((prompt ?? PredefinedPrompts.Default).PromptText);
        if (text == CleanupText)
            return LocalPromptClass.Cleanup;
        if (Array.IndexOf(EmailChatTexts, text) >= 0)
            return LocalPromptClass.EmailChat;
        if (Array.IndexOf(TranslateTexts, text) >= 0)
            return LocalPromptClass.Translate;
        if (text == AssistantText)
            return LocalPromptClass.Assistant;
        return LocalPromptClass.Custom;
    }

    /// <summary>The ceiling on custom prompts' output, in tokens.</summary>
    internal const int CustomCeiling = 4096;

    /// <summary>
    /// The output cap in the SERVER's tokens for a source transcript of <paramref name="inputTokens"/>
    /// tokens — design plan §5.3's starting table, with Translate widened from <c>2·in + 64</c>: a
    /// token-dense source (Chinese) translated into a token-heavy target (Dutch) can need more than
    /// twice its own tokens. The app refuses a reply cut off at the cap (ENH-27), so a cap that is
    /// too tight throws good output away; each floor covers a short dictation.
    /// </summary>
    internal static int CapFor(LocalPromptClass promptClass, int inputTokens)
    {
        var input = Math.Max(0, inputTokens);
        return promptClass switch
        {
            // max(1.5·in + 64, 256), ceiling of the half.
            LocalPromptClass.Cleanup => Math.Max((3 * input + 1) / 2 + 64, 256),
            // max(1.3·in + 32, 512).
            LocalPromptClass.EmailChat => Math.Max((13 * input + 9) / 10 + 32, 512),
            // max(3·in + 64, 256).
            LocalPromptClass.Translate => Math.Max(3 * input + 64, 256),
            // min(max(2·in + 64, 1024), 4096).
            _ => Math.Min(Math.Max(2 * input + 64, 1024), CustomCeiling),
        };
    }

    /// <summary>Line endings to <c>\n</c> (a bare CR included), then trimmed.</summary>
    internal static string Normalize(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
