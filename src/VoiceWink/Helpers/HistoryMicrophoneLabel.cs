namespace VoiceWink.Helpers;

/// <summary>HIS-11: the History detail line's microphone part.</summary>
internal static class HistoryMicrophoneLabel
{
    internal const string NamePrefix = "Microphone: ";
    internal const string TagPrefix = "Microphone ID: ";

    /// <summary>The name when known, else the tag, else null (nothing is shown).</summary>
    internal static string? Describe(string? name, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(name)) return NamePrefix + name.Trim();
        if (!string.IsNullOrWhiteSpace(tag)) return TagPrefix + tag.Trim();
        return null;
    }
}
