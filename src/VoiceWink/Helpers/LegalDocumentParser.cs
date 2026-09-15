using global::System.Globalization;
using global::System.Security.Cryptography;
using global::System.Text;
using global::System.Text.RegularExpressions;

namespace VoiceWink.Helpers;

/// <summary>
/// Parses a legal markdown document with a YAML-style header (LGL-1). Header
/// requires three keys: <c>version</c>, <c>last-updated</c>, <c>title</c>. Body
/// uses a deliberately constrained markdown subset (see <see cref="MarkdownBlock"/>):
/// `# / ## / ###` headings, `- bullet` items, blank-line-separated paragraphs,
/// `> ...` admonitions, and standalone-line `[text](https://...)` hyperlinks.
/// Anything unrecognised falls through to plain paragraph text.
///
/// <para>The parser normalises CRLF to LF before hashing so the SHA-256
/// content hash is stable across platforms regardless of git's checkout
/// line-ending behaviour. <c>.gitattributes</c> additionally pins
/// <c>src/VoiceWink/Assets/Legal/*.md</c> to LF.</para>
/// </summary>
public static class LegalDocumentParser
{
    private static readonly Regex HyperlinkLine =
        new(@"^\[([^\]]+)\]\((https?://[^\s)]+)\)$", RegexOptions.Compiled);

    public static LegalDocumentMetadata Parse(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            throw new LegalDocumentParseException("Document is empty.");

        // Normalise to LF before any processing — CRLF would otherwise change the
        // hash on Windows checkouts that didn't honour the .gitattributes rule.
        var normalised = rawText.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = normalised.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
            throw new LegalDocumentParseException("Missing YAML header opener (---) on first line.");

        // Find header close
        int headerCloseIndex = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                headerCloseIndex = i;
                break;
            }
        }
        if (headerCloseIndex < 0)
            throw new LegalDocumentParseException("Missing YAML header closer (---).");

        // Parse header keys
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < headerCloseIndex; i++)
        {
            var line = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                throw new LegalDocumentParseException($"Malformed header line: '{line}'");
            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            header[key] = value;
        }

        if (!header.TryGetValue("version", out var versionStr) ||
            !int.TryParse(versionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            version <= 0)
            throw new LegalDocumentParseException("Missing or invalid 'version' header (positive integer required).");

        if (!header.TryGetValue("last-updated", out var lastUpdatedStr) ||
            !DateOnly.TryParseExact(lastUpdatedStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastUpdated))
            throw new LegalDocumentParseException("Missing or invalid 'last-updated' header (yyyy-MM-dd required).");

        if (!header.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
            throw new LegalDocumentParseException("Missing or empty 'title' header.");

        // Body = everything after the closing ---
        var bodyLines = lines.Skip(headerCloseIndex + 1).ToArray();
        var bodyText = string.Join('\n', bodyLines).Trim();

        // Codex diff-review challenge: a header-only document with valid front matter
        // and no body would otherwise hash a zero-length string and let users "accept"
        // empty terms. Reject as a parse failure so BundleHealthy stays false.
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new LegalDocumentParseException("Document body is empty.");

        var body = ParseBody(bodyLines);
        var contentHash = HashBody(bodyText);

        return new LegalDocumentMetadata(version, lastUpdated, title, body, contentHash);
    }

    private static IReadOnlyList<MarkdownBlock> ParseBody(string[] lines)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraphBuffer = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraphBuffer.Length == 0) return;
            blocks.Add(new ParagraphBlock(paragraphBuffer.ToString().TrimEnd()));
            paragraphBuffer.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("> "))
            {
                FlushParagraph();
                blocks.Add(new AdmonitionBlock(line[2..].Trim()));
                continue;
            }

            if (line.StartsWith("### "))
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(3, line[4..].Trim()));
                continue;
            }
            if (line.StartsWith("## "))
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(2, line[3..].Trim()));
                continue;
            }
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(1, line[2..].Trim()));
                continue;
            }

            if (line.StartsWith("- "))
            {
                FlushParagraph();
                blocks.Add(new BulletBlock(line[2..].Trim()));
                continue;
            }

            var hyperlinkMatch = HyperlinkLine.Match(line);
            if (hyperlinkMatch.Success)
            {
                FlushParagraph();
                blocks.Add(new HyperlinkBlock(hyperlinkMatch.Groups[1].Value, hyperlinkMatch.Groups[2].Value));
                continue;
            }

            if (paragraphBuffer.Length > 0) paragraphBuffer.Append(' ');
            paragraphBuffer.Append(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static string HashBody(string trimmedBody)
    {
        var bytes = Encoding.UTF8.GetBytes(trimmedBody);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
