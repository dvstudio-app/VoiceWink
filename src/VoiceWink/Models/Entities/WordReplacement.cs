namespace VoiceWink.Models.Entities;

/// <summary>
/// Word replacement rule (original text -> replacement).
/// OriginalText can contain comma-separated variants (e.g. "thx,ty").
/// </summary>
public class WordReplacement
{
    public int Id { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string ReplacementText { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
}
