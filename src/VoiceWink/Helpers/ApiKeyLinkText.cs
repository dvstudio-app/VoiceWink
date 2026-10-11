namespace VoiceWink.Helpers;

/// <summary>
/// The "Get a … API key →" link under setup's provider boxes, with the article the provider's name
/// takes ("an OpenAI", "a Groq"). A leading vowel letter takes "an" — right for every provider name
/// the app ships today; a name like "Uni…" (vowel letter, consonant sound) would need its own case.
/// </summary>
internal static class ApiKeyLinkText
{
    internal static string For(string providerName)
    {
        var article = providerName.Length > 0 && "AEIOUaeiou".Contains(providerName[0]) ? "an" : "a";
        return $"Get {article} {providerName} API key →";
    }
}
