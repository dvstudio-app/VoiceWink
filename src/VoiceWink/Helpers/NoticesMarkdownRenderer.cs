using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VoiceWink.Helpers;

/// <summary>
/// Renders the shipped <c>THIRD-PARTY-NOTICES.md</c> into styled WinUI controls for
/// <see cref="Views.Dialogs.ThirdPartyNoticesDialog"/> (UI-22). Parse and render in one pass over the
/// lines, matching <see cref="LegalMarkdownRenderer"/>'s convention that rendering is verified by
/// looking at it rather than by unit tests — WinUI controls cannot be constructed in this repo's
/// test host.
///
/// <para>Deliberately NOT <see cref="LegalDocumentParser"/>: that one requires a YAML header and
/// hashes the body for the LGL-1 acceptance gate, and its block set has no concept of a table, a
/// fenced block or a comment. The notices manifest has all three (7 tables, 4 fences carrying the
/// verbatim MIT and Apache-2.0 texts, 7 maintainer comments, counted 2026-09-22).</para>
///
/// <para><b>The rule every branch below is written to: never drop content.</b> This file is what a
/// user is pointed at to satisfy the MIT / Apache-2.0 / CC-BY attribution obligations, so a line
/// shape this renderer does not recognise falls through to a paragraph rather than being discarded.
/// The ONE deliberate omission is the <c>&lt;!-- Maintainers: … --&gt;</c> notes, which are internal —
/// and an UNTERMINATED comment would hide everything after it, so that throws instead
/// (Codex plan review, blocker 1: a future missing <c>--&gt;</c> before the licence blocks would
/// otherwise render a dialog that looks complete).</para>
/// </summary>
public static class NoticesMarkdownRenderer
{
    /// <summary>
    /// The notices file uses all three optional inline constructs; the legal bundle uses none of
    /// them, which is why they are opt-in (see <see cref="MarkdownInlineFeatures"/>). Bare project
    /// URLs are the manifest's main way of citing a source, so they are worth making live.
    /// </summary>
    private const MarkdownInlineFeatures Inline =
        MarkdownInlineFeatures.CodeSpans | MarkdownInlineFeatures.AutoLink | MarkdownInlineFeatures.Escapes;

    /// <summary>A column this narrow gets <c>Auto</c> width; anything wider shares the leftover space.</summary>
    private const int NarrowColumnChars = 16;

    private static readonly Regex SeparatorCell = new(@"^:?-{2,}:?$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRule = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the document. Throws <see cref="FormatException"/> when the markdown cannot be
    /// rendered without hiding content — the caller falls back to opening the file itself.
    /// </summary>
    public static StackPanel Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 8 };
        var paragraph = new StringBuilder();
        var bullet = new StringBuilder();
        var fence = new StringBuilder();
        var table = new List<string>();
        var inFence = false;
        var inComment = false;
        var titleSkipped = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            panel.Children.Add(Paragraph(paragraph.ToString()));
            paragraph.Clear();
        }

        void FlushBullet()
        {
            if (bullet.Length == 0) return;
            panel.Children.Add(Bullet(bullet.ToString()));
            bullet.Clear();
        }

        void FlushTable()
        {
            if (table.Count == 0) return;
            panel.Children.Add(Table(table));
            table.Clear();
        }

        void FlushFence()
        {
            if (fence.Length == 0) return;
            panel.Children.Add(Preformatted(fence.ToString()));
            fence.Clear();
        }

        void FlushAll()
        {
            FlushBullet();
            FlushParagraph();
            FlushTable();
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            // Inside a fence NOTHING is interpreted — not a comment marker, not a pipe, not a hash.
            // The Apache 2.0 text alone is ~200 lines of prose that would otherwise be re-flowed.
            if (inFence)
            {
                if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = false;
                    FlushFence();
                }
                else
                {
                    if (fence.Length > 0) fence.Append('\n');
                    fence.Append(rawLine.TrimEnd());
                }
                continue;
            }

            var line = StripComments(rawLine, ref inComment);

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushAll();
                inFence = true;
                continue;
            }

            var trimmed = line.Trim();

            // A line that was ONLY a comment arrives here empty, and a blank line is what separates
            // blocks in markdown — so a maintainer note between two paragraphs keeps them apart.
            if (trimmed.Length == 0)
            {
                FlushAll();
                continue;
            }

            // Table rows are buffered: the column count is not known until the run ends. A run whose
            // second line is not a `|---|` separator is NOT a table and is emitted as paragraphs.
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                FlushBullet();
                FlushParagraph();
                table.Add(trimmed);
                continue;
            }
            FlushTable();

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var hashes = trimmed.Length - trimmed.TrimStart('#').Length;
                if (hashes <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
                {
                    FlushAll();
                    // The document's own leading `# Third-Party Notices` is the ONE heading skipped:
                    // the dialog's title bar already names the document, and the two stacked read as
                    // two titles (owner, 2026-09-22). Scoped to a level-1 heading that is the FIRST
                    // thing in the file — a level-1 heading anywhere else still renders. The
                    // `titleSkipped` latch matters because skipping adds no child: without it the
                    // condition stays true and a SECOND consecutive `#` would vanish too, which is
                    // the silent content loss the rest of this file is written against (Kimi r2).
                    if (!(hashes == 1 && !titleSkipped && panel.Children.Count == 0))
                        panel.Children.Add(Heading(trimmed[(hashes + 1)..].Trim(), hashes));
                    else
                        titleSkipped = true;
                    continue;
                }
            }

            if (HorizontalRule.IsMatch(trimmed))
            {
                FlushAll();
                panel.Children.Add(Rule());
                continue;
            }

            if (trimmed.Length > 1 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ')
            {
                FlushParagraph();
                FlushBullet();
                bullet.Append(trimmed[2..].Trim());
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushAll();
                panel.Children.Add(Quote(trimmed[2..].Trim()));
                continue;
            }

            // An INDENTED line continues the bullet above it (the model-integrity bullets wrap over
            // three lines); anything else continues, or starts, a paragraph. Hard-wrapped source
            // lines join with a space — a newline is not a break in markdown.
            if (bullet.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                bullet.Append(' ').Append(trimmed);
                continue;
            }

            FlushBullet();
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }

        // An unterminated FENCE keeps its content (nothing is hidden), an unterminated COMMENT would
        // swallow the rest of the document — so only the second is a refusal.
        FlushFence();
        FlushAll();

        if (inComment)
            throw new FormatException("Unterminated HTML comment: everything after it would be hidden.");

        return panel;
    }

    /// <summary>
    /// Removes <c>&lt;!-- … --&gt;</c> spans, which may open and close mid-line and may run over
    /// several lines. <paramref name="inComment"/> carries that state across lines.
    /// </summary>
    private static string StripComments(string line, ref bool inComment)
    {
        if (!inComment && !line.Contains("<!--", StringComparison.Ordinal)) return line;

        var kept = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (inComment)
            {
                var end = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0) break;
                inComment = false;
                i = end + 3;
            }
            else
            {
                var start = line.IndexOf("<!--", i, StringComparison.Ordinal);
                if (start < 0)
                {
                    kept.Append(line, i, line.Length - i);
                    break;
                }
                kept.Append(line, i, start - i);
                inComment = true;
                i = start + 4;
            }
        }
        return kept.ToString();
    }

    private static TextBlock Heading(string text, int level)
    {
        var size = level switch { 1 => 20, 2 => 16, _ => 13 };
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 2),
        };
        MarkdownInline.Apply(tb, text, Inline);
        return tb;
    }

    private static TextBlock Paragraph(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        MarkdownInline.Apply(tb, text, Inline);
        return tb;
    }

    /// <summary>
    /// Two-column Grid, NOT a horizontal StackPanel — the same trap <see cref="LegalMarkdownRenderer"/>
    /// documents: a horizontal StackPanel measures its children with infinite width, so the content
    /// never receives a finite constraint and <c>TextWrapping.Wrap</c> silently never engages.
    /// </summary>
    private static Grid Bullet(string text)
    {
        var content = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        MarkdownInline.Apply(content, text, Inline);

        var marker = new TextBlock
        {
            Text = "• ",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid { Margin = new Thickness(16, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(marker);
        grid.Children.Add(content);
        return grid;
    }

    private static Border Quote(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        MarkdownInline.Apply(tb, text, Inline);

        // Amber as a BORDER, never as a Foreground — ThemeTextContrast/UI-15 reserves AccentAmber for
        // borders and tints, and sentences take the *Text colours.
        return new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.AccentAmber),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Child = tb,
        };
    }

    private static Border Rule() => new()
    {
        Height = 1,
        Background = AppTheme.Brush(AppTheme.CardBorderColor),
        Margin = new Thickness(0, 6, 0, 6),
    };

    /// <summary>
    /// A fenced block — the verbatim MIT and Apache-2.0 licence texts. ONE TextBlock for the whole
    /// block, not one per line, and selectable: a licence is text a reader may want to copy.
    /// </summary>
    private static Border Preformatted(string text) => new()
    {
        Background = AppTheme.Brush(AppTheme.CardBg),
        BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8, 10, 8),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        },
    };

    /// <summary>
    /// A buffered run of <c>|</c> lines. With a <c>|---|</c> separator on the second line it becomes a
    /// Grid; without one it was never a table, and every buffered line is emitted as a paragraph so
    /// the content survives.
    /// </summary>
    private static UIElement Table(IReadOnlyList<string> lines)
    {
        var rows = lines.Select(SplitCells).ToList();
        if (rows.Count < 2 || !rows[1].All(c => SeparatorCell.IsMatch(c)) || rows[1].Count == 0)
        {
            var fallback = new StackPanel { Spacing = 4 };
            foreach (var line in lines) fallback.Children.Add(Paragraph(line));
            return fallback;
        }

        var header = rows[0];
        var body = rows.Skip(2).ToList();

        // Widest row wins, not the header: a ragged row gets its own column rather than losing a cell.
        var columns = Math.Max(header.Count, body.Count == 0 ? 0 : body.Max(r => r.Count));

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        for (var c = 0; c < columns; c++)
        {
            var widest = CellsInColumn(header, body, c).Select(v => v.Length).DefaultIfEmpty(0).Max();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                // Auto only where the content is demonstrably short (a version, an SPDX id). A URL
                // column MUST be star-sized: Auto measures with infinite width, so it would push the
                // table past the dialog instead of wrapping.
                Width = widest <= NarrowColumnChars ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // divider
        foreach (var _ in body) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < header.Count; c++)
            AddCell(grid, header[c], row: 0, column: c, header: true);

        var divider = new Border
        {
            Height = 1,
            Background = AppTheme.Brush(AppTheme.CardBorderColor),
            Margin = new Thickness(0, 2, 0, 4),
        };
        Grid.SetRow(divider, 1);
        Grid.SetColumn(divider, 0);
        Grid.SetColumnSpan(divider, columns);
        grid.Children.Add(divider);

        for (var r = 0; r < body.Count; r++)
            for (var c = 0; c < body[r].Count; c++)
                AddCell(grid, body[r][c], row: r + 2, column: c, header: false);

        return grid;
    }

    private static IEnumerable<string> CellsInColumn(IReadOnlyList<string> header, IReadOnlyList<List<string>> body, int column)
    {
        if (column < header.Count) yield return header[column];
        foreach (var row in body)
            if (column < row.Count) yield return row[column];
    }

    private static void AddCell(Grid grid, string text, int row, int column, bool header)
    {
        var tb = new TextBlock
        {
            FontSize = 12,
            FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = AppTheme.Brush(header ? AppTheme.TextSecondary : AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 2),
            VerticalAlignment = VerticalAlignment.Top,
        };
        MarkdownInline.Apply(tb, text, Inline);
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    /// <summary>
    /// Splits a table row on its unescaped pipes. The outer pipes are optional, and <c>\|</c> is left
    /// escaped for <see cref="MarkdownInline"/> to resolve — undoing it here would make a cell
    /// containing a pipe indistinguishable from a column break on any later pass.
    /// </summary>
    private static List<string> SplitCells(string line)
    {
        var s = line.Trim();
        if (s.StartsWith("|", StringComparison.Ordinal)) s = s[1..];
        if (s.EndsWith("|", StringComparison.Ordinal) && !s.EndsWith("\\|", StringComparison.Ordinal)) s = s[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '|')
            {
                cell.Append("\\|");
                i++;
                continue;
            }
            if (s[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }
            cell.Append(s[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
