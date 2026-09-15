namespace VoiceWink.Models.Entities;

/// <summary>
/// Custom vocabulary word — a keyterm for the cloud providers that take hints and a spelling
/// for AI enhancement to prefer (never a Whisper prompt since TRN-11).
/// </summary>
public class VocabularyWord
{
    public int Id { get; set; }
    public string Word { get; set; } = string.Empty;
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
