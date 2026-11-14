using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>Why a provider and text model cannot clean a dictation, or <see cref="Ready"/>.</summary>
internal enum TextReadiness { Ready, NoKey, BadKey, NoModel, NotChat, NotInstalled, EngineUnavailable }

/// <summary>ONB-14: the one "can this provider and model clean text" rule, from stored state only; each check mirrors a refusal in the enhancement path.</summary>
internal static class TextModelReadiness
{
    /// <param name="key">Null = no stored key; otherwise <see cref="ApiKeyFormat.Validate"/>'s verdict.</param>
    internal static TextReadiness Of(bool requiresKey, ApiKeyFormatVerdict? key, string? model,
        bool builtIn, bool engineOffered, Func<string, bool> isInstalled)
        => builtIn && !engineOffered ? TextReadiness.EngineUnavailable
         : requiresKey && key is null ? TextReadiness.NoKey
         // A malformed stored key is refused on every provider, keyless ones included.
         : key is not null && key != ApiKeyFormatVerdict.Ok ? TextReadiness.BadKey
         : string.IsNullOrWhiteSpace(model) ? TextReadiness.NoModel
         // Only key-requiring providers get the chat-model check: a user server's ids are its own.
         : requiresKey && !ModelDisplayPolicy.IsChatModel(model) ? TextReadiness.NotChat
         : builtIn && !isInstalled(model) ? TextReadiness.NotInstalled
         : TextReadiness.Ready;
}
