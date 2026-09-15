namespace VoiceWink.Helpers;

/// <summary>
/// Parsed legal document — header metadata plus a layout-tree body and a content
/// hash for acceptance tracking. <see cref="ContentHash"/> is the durable acceptance
/// artifact; <see cref="Version"/> is a UI display label only.
/// </summary>
public sealed record LegalDocumentMetadata(
    int Version,
    DateOnly LastUpdated,
    string Title,
    IReadOnlyList<MarkdownBlock> Body,
    string ContentHash);

/// <summary>Thrown by <see cref="LegalDocumentParser"/> on any header / IO failure.</summary>
public sealed class LegalDocumentParseException : Exception
{
    public LegalDocumentParseException(string message) : base(message) { }
    public LegalDocumentParseException(string message, Exception inner) : base(message, inner) { }
}
