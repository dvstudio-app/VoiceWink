namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// ENH-6h: the ONE path-free formatter for reference-set log summaries — per-item
/// mime + size (restores the per-item detail the ENH-6f count-only summary dropped,
/// which made the 2026-07-15 oversized-reference incident harder to diagnose).
/// Consumed by AIEnhancementService AND both image clients; never includes paths.
/// </summary>
public static class ReferenceLogFormat
{
    public static string Describe(IReadOnlyList<ReferenceImage>? references)
        => references == null || references.Count == 0
            ? "none"
            : $"{references.Count} [{string.Join(", ", references.Select(r => $"{r.MimeType} {r.Bytes.Length / 1024} KB"))}]";
}
