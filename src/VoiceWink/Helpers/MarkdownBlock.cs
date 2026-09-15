namespace VoiceWink.Helpers;

/// <summary>
/// Discriminated layout-tree block for the legal-doc subset of markdown (LGL-1).
/// The parser emits these; <see cref="LegalMarkdownRenderer"/> maps them to WinUI controls.
/// Splitting parse from render keeps the parser pure and unit-testable without a XamlRoot.
/// </summary>
public abstract record MarkdownBlock;

public sealed record HeadingBlock(int Level, string Text) : MarkdownBlock;
public sealed record ParagraphBlock(string Text) : MarkdownBlock;
public sealed record BulletBlock(string Text) : MarkdownBlock;
public sealed record HyperlinkBlock(string Text, string Url) : MarkdownBlock;
public sealed record AdmonitionBlock(string Text) : MarkdownBlock;
