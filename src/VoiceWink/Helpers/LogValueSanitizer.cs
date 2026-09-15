namespace VoiceWink.Helpers;

/// <summary>
/// Flattens a value to a single line before it is logged, so a PII-class token can never span
/// output lines.
///
/// <para><b>Why this exists (SEC-3).</b> Sensitive log values are protected in two layers: the
/// property-name allowlist in <c>LogRedactionEnricher</c> covers the Sentry sub-logger, and a
/// rendered-value regex covers the REL-3 support-zip and the GDPR export, which scrub
/// already-rendered text. Those rendered-line scrubbers read the log <b>line by line</b>
/// (<c>ReadLine()</c> + <c>RedactString</c>), and the value patterns are anchored to end-of-line
/// because that is the only anchor a caller-supplied value cannot forge — it has no closing
/// delimiter to inject.</para>
///
/// <para>A value containing CR or LF defeats exactly that: it splits across output lines, the
/// anchored pattern matches only the first segment, and the remainder survives into the bundle
/// unredacted. Sanitizing at the EMIT site makes "a sensitive token never spans lines" true by
/// construction, so the regex is sound rather than best-effort.</para>
///
/// <para>Values reaching these sites are OS-supplied (process names, audio endpoint names) or
/// user-authored (App Mode labels); none is trusted to be single-line.</para>
///
/// <para><b>The identifier gate (2026-09-13).</b> <see cref="IsIdentifier"/> / <see cref="IdentifierOrShape"/>
/// is the ONE rule for "is this value an id or is it text" — behind the startup banner's
/// configuration facts (<c>DiagnosticSnapshot</c>) and the root-logger <c>ModelIdEnricher</c>
/// that gates every <c>{Model}</c> log property. It exists because the AI Enhancement page's
/// model box is an EDITABLE combo: the stored "model id" can be any text a user typed, and a
/// log line, a breadcrumb, a support bundle or a GDPR export that renders it verbatim carries
/// that text — the scrubs at those boundaries know key shapes and named tokens, not prose.</para>
/// </summary>
internal static class LogValueSanitizer
{
    /// <summary>Placeholder for a null or entirely-whitespace value, so an empty token is still visibly a token.</summary>
    internal const string EmptyMarker = "<empty>";

    /// <summary>
    /// U+2028 LINE SEPARATOR — not a control character, but treated as a break by some readers.
    /// Spelled as an escape deliberately: the C# lexer treats a LITERAL U+2028 as a newline, so
    /// writing the character itself inside a char literal is a compile error ("newline in
    /// constant"). The character this class exists to neutralize breaks the file that
    /// neutralizes it — verified while writing it, not theorized.
    /// </summary>
    private const char LineSeparator = '\u2028';

    /// <summary>U+2029 PARAGRAPH SEPARATOR — same reasoning as <see cref="LineSeparator"/>. Both
    /// are already matched by <c>char.IsWhiteSpace</c> (categories Zl/Zp); naming them is
    /// auditability at a security boundary, not a gap being closed.</summary>
    private const char ParagraphSeparator = '\u2029';

    /// <summary>
    /// Returns <paramref name="value"/> with every control character — including CR and LF —
    /// replaced by a single space, runs of whitespace collapsed, and the result trimmed.
    /// Null or whitespace-only input yields <see cref="EmptyMarker"/>.
    /// </summary>
    internal static string SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EmptyMarker;

        var sb = new global::System.Text.StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            // char.IsControl covers CR, LF, TAB and the C0/C1 ranges; char.IsWhiteSpace covers
            // ordinary and exotic spaces — and, measured, ALSO both separators below (they are
            // Unicode categories Zl/Zp, which IsWhiteSpace includes). The explicit checks are
            // therefore redundant defense-in-depth, kept because this is a security boundary and
            // naming the two characters makes the intent auditable; an earlier comment here
            // claimed IsWhiteSpace did NOT cover them, which was wrong (Kimi diff r1).
            var isSpace = char.IsControl(ch)
                || char.IsWhiteSpace(ch)
                || ch == LineSeparator
                || ch == ParagraphSeparator;

            if (isSpace)
            {
                // Leading whitespace is dropped (sb.Length == 0); interior runs collapse to one.
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        // A trailing space can only exist if the value ended in whitespace; drop it.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;

        return sb.Length == 0 ? EmptyMarker : sb.ToString();
    }

    /// <summary>
    /// Is this value an IDENTIFIER — a catalog model id, a language code, a hotkey token, an enum
    /// name, one of the app's own placeholders such as <c>(default)</c> — rather than TEXT? The
    /// rule is about SHAPE, not alphabet: printable ASCII, no whitespace, 1–64 characters. It
    /// admits every id the app stores (<c>gpt-5.2</c>, <c>openai/gpt-5.2:free</c>,
    /// <c>@cf/meta/llama-4-scout</c>, <c>ggml-small-q8_0</c>, <c>Ctrl+Space</c>) and refuses what a
    /// person types or dictates: a phrase has a space, a name is often non-ASCII, a note is long.
    /// A quoted or dash-led token passes ON PURPOSE — the hazard this gate exists for is prose, and
    /// the alphabet rule that preceded it (PR #917's <c>DiagnosticSnapshot.IsToken</c>) refused
    /// quotes and a leading dash, which stopped no text and would have masked the app's own
    /// <c>(default)</c> placeholder. Pure; case-tabled in <c>LogValueSanitizerTests</c>.
    /// </summary>
    internal static bool IsIdentifier(string? value)
        => value is not null && IdentifierShape.IsMatch(value);

    /// <summary>
    /// The value itself when <see cref="IsIdentifier"/> accepts it — the SAME reference, so a
    /// caller can cheap-compare with <see cref="object.ReferenceEquals"/> — otherwise its SHAPE,
    /// <c>(not an id, N chars)</c> with N the original length, so text never reaches a log line,
    /// a breadcrumb, a bundle or an export. Null reads as <c>(not an id, 0 chars)</c>.
    /// </summary>
    internal static string IdentifierOrShape(string? value)
        => IsIdentifier(value) ? value! : $"(not an id, {value?.Length ?? 0} chars)";

    // \z, not $: in .NET, $ also matches before a FINAL newline, so "gpt-5.2\n" would pass as an
    // identifier — a value with a line break, the exact thing SingleLine exists to keep out of a
    // log line (the alphabet regex this replaced had the same hole; self-review, pinned).
    private static readonly global::System.Text.RegularExpressions.Regex IdentifierShape =
        new(@"^[\x21-\x7E]{1,64}\z", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
