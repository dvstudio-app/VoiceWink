namespace VoiceWink.Models.Enums;

/// <summary>
/// Transcription model provider.
/// </summary>
public enum ModelProvider
{
    Local,
    Groq,
    Deepgram,
    OpenAI,
    ElevenLabs,
    [System.Obsolete("Not a transcription provider")] Mistral,
    [System.Obsolete("Not a transcription provider")] Gemini,
    [System.Obsolete("API deprecated")] Soniox
}
