// doc-exempt: one-line line-ending normaliser; its reason is on the type and pinned in MultiLineTextBoxOrderTests
namespace VoiceWink.Helpers;

/// <summary>
/// A multi-line WinUI <c>TextBox</c> hands its text back with bare <c>\r</c> line breaks,
/// whatever it was given (measured 2026-09-28: "a\n\nb" reads back as "a\r\rb"). The app's
/// transcripts use <c>\n</c>, so text read from a box is normalised before it travels on —
/// an unedited redo then passes the original transcript through unchanged.
/// </summary>
internal static class TextBoxLineEndings
{
    internal static string ToLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
