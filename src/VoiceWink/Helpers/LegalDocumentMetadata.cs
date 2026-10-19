namespace VoiceWink.Helpers;

/// <summary>
/// Parsed legal document — header metadata plus a layout-tree body and a content
/// hash for acceptance tracking. <see cref="ContentHash"/> is the durable acceptance
/// artifact; <see cref="Version"/> is a UI display label only.
/// <see cref="ChangeSummary"/> is the header's <c>changes</c> line, set only while its
/// <c>changes-for</c> hash prefix matches this body — a summary left over from an earlier
/// edit is never shown against new text. <see cref="ChangesSince"/> lists the hash prefixes
/// of the earlier bodies the summary is complete for (<c>changes-since</c>).
/// </summary>
public sealed record LegalDocumentMetadata(
    int Version,
    DateOnly LastUpdated,
    string Title,
    IReadOnlyList<MarkdownBlock> Body,
    string ContentHash,
    string? ChangeSummary = null,
    IReadOnlyList<string>? ChangesSince = null)
{
    /// <summary>
    /// The summary for a user who accepted <paramref name="acceptedHash"/>, or null when it would not
    /// cover everything they are now agreeing to (they skipped an edit, or never accepted this document).
    /// </summary>
    public string? SummaryFor(string? acceptedHash) =>
        acceptedHash is not null && ChangesSince is not null &&
        ChangesSince.Any(p => acceptedHash.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? ChangeSummary
            : null;
}

/// <summary>Thrown by <see cref="LegalDocumentParser"/> on any header / IO failure.</summary>
public sealed class LegalDocumentParseException : Exception
{
    public LegalDocumentParseException(string message) : base(message) { }
    public LegalDocumentParseException(string message, Exception inner) : base(message, inner) { }
}
