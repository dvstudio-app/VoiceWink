using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace VoiceWink.Helpers;

/// <summary>
/// Optional inline constructs. Every one is OFF by default so the legal documents
/// (<see cref="LegalMarkdownRenderer"/>) render exactly as they did before this engine was shared:
/// <c>privacy-v5.md</c> contains four backticks and one <c>Recordings\Debug</c> path today, both of
/// which render literally, and changing that is a change to a T2 asset's appearance.
/// </summary>
[Flags]
internal enum MarkdownInlineFeatures
{
    None = 0,

    /// <summary><c>`code`</c> renders monospace, backticks dropped.</summary>
    CodeSpans = 1 << 0,

    /// <summary>A bare <c>https://…</c> becomes a link (trailing sentence punctuation stays text).</summary>
    AutoLink = 1 << 1,

    /// <summary><c>\*</c> → <c>*</c> and the other markdown backslash escapes.</summary>
    Escapes = 1 << 2,
}

/// <summary>
/// The ONE inline-markdown engine behind every document the app renders: the legal bundle (LGL-1)
/// and the third-party notices (UI-22). Extracted from <see cref="LegalMarkdownRenderer"/>, whose
/// private copy it was, when the notices dialog needed the same <c>[label](url)</c> + <c>**bold**</c>
/// handling — a second copy would drift, and the safe-URI gate is the half that must not.
/// </summary>
internal static class MarkdownInline
{
    // ONE pass, alternation order = precedence: a URL inside a [label](url) is consumed by the link
    // branch before the bare-URL branch can see it. Label has no ']', link URL no whitespace or ')',
    // a code span no backtick; a bare URL stops at whitespace, a bracket, a pipe (table cell) or a
    // closing paren, so a parenthesised or tabulated URL doesn't swallow its delimiter.
    private static readonly Regex Tokens = new(
        @"(?<link>\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\))|(?<code>`(?<codeText>[^`]+)`)|(?<bare>https?://[^\s<>()\[\]|]+)",
        RegexOptions.Compiled);

    // Markdown's backslash escapes, restricted to the ones this app's documents actually use.
    private static readonly Regex Escape = new(@"\\([*_`|\\])", RegexOptions.Compiled);

    // A bare URL at the end of a sentence carries the sentence's punctuation; the link must not.
    private const string TrailingPunctuation = ".,;:!?";

    /// <summary>
    /// Renders <paramref name="text"/> into <paramref name="tb"/>'s inlines. A construct that is not
    /// enabled, or a URL the safe-URI gate refuses, falls through to plain text — malformed input can
    /// never produce a dead link, lose its content, or throw at render.
    /// </summary>
    internal static void Apply(TextBlock tb, string text, MarkdownInlineFeatures features = MarkdownInlineFeatures.None)
    {
        int pos = 0;
        foreach (Match m in Tokens.Matches(text))
        {
            // A match before the cursor belongs to a construct already consumed (the bare-URL branch
            // can match inside a skipped code span), and a disabled construct is left as plain text —
            // both by NOT advancing pos, so the following segment carries the characters verbatim.
            if (m.Index < pos) continue;
            if (m.Groups["code"].Success && !features.HasFlag(MarkdownInlineFeatures.CodeSpans)) continue;
            if (m.Groups["bare"].Success && !features.HasFlag(MarkdownInlineFeatures.AutoLink)) continue;

            if (m.Index > pos)
                AddFormattedRuns(tb, text[pos..m.Index], features);

            if (m.Groups["link"].Success)
            {
                var label = m.Groups["label"].Value;
                if (TryCreateSafeLinkUri(m.Groups["url"].Value, out var uri))
                    AddLink(tb, label, uri);
                else
                    // Not a usable URL — render the LABEL, with its own inline constructs still
                    // honoured: the notices file's "[`LICENSE`](LICENSE)" points at a repo-relative
                    // path the safe-URI gate refuses, and sending the label straight to the run
                    // splitter printed its backticks (owner, 2026-09-22). Recursion terminates by
                    // construction — a label cannot contain ']', so it cannot contain a nested link.
                    Apply(tb, label, features);
                pos = m.Index + m.Length;
            }
            else if (m.Groups["code"].Success)
            {
                tb.Inlines.Add(new Run
                {
                    Text = m.Groups["codeText"].Value,
                    FontFamily = new FontFamily("Consolas"),
                });
                pos = m.Index + m.Length;
            }
            else
            {
                var raw = m.Groups["bare"].Value;
                int keep = raw.Length;
                while (keep > 0 && TrailingPunctuation.Contains(raw[keep - 1])) keep--;
                var url = raw[..keep];
                if (keep > 0 && TryCreateSafeLinkUri(url, out var uri))
                {
                    AddLink(tb, url, uri);
                    pos = m.Index + keep; // the punctuation we trimmed stays part of the sentence
                }
                else
                {
                    // Enabled but gate-refused. The lead-in was already emitted above, so leaving
                    // pos behind would make the tail segment re-emit it — "go http://%zz end"
                    // rendering as "go go http://%zz end" (Kimi diff r2). Emit the match verbatim
                    // and advance: unreachable with today's manifest, but the file's own contract
                    // is that malformed input never loses OR duplicates content.
                    AddFormattedRuns(tb, raw, features);
                    pos = m.Index + m.Length;
                }
            }
        }

        if (pos < text.Length)
            AddFormattedRuns(tb, text[pos..], features);
    }

    /// <summary>
    /// http(s) + mailto only — never javascript:/file:/data:/etc., which <c>Uri.TryCreate(Absolute)</c>
    /// would otherwise accept. The ONE gate for every link path in the app, so a malformed or hostile
    /// URL degrades to plain text instead of becoming a live link OR throwing at render.
    /// (Codex + adversarial review 2026-06-14.)
    /// </summary>
    internal static bool TryCreateSafeLinkUri(string url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeMailto))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private static void AddLink(TextBlock tb, string label, Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri };
        link.Inlines.Add(new Run { Text = label });
        tb.Inlines.Add(link);
    }

    // **bold** splitting: odd-index segments render SemiBold. Runs without an explicit weight inherit
    // the TextBlock's FontWeight (e.g. admonitions stay SemiBold), so this also just strips literal "**".
    private static void AddFormattedRuns(TextBlock tb, string text, MarkdownInlineFeatures features)
    {
        var segments = text.Split("**");
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0) continue;
            var body = features.HasFlag(MarkdownInlineFeatures.Escapes)
                ? Escape.Replace(segments[i], "$1")
                : segments[i];
            var run = new Run { Text = body };
            if ((i & 1) == 1) run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            tb.Inlines.Add(run);
        }
    }
}
