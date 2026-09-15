using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace VoiceWink.Helpers;

/// <summary>
/// Maps a parsed <see cref="MarkdownBlock"/> sequence to a <see cref="StackPanel"/>
/// of styled WinUI controls (LGL-1). Pure block-to-control translation — no parsing.
/// Tests don't construct WinUI controls; rendering is exercised via UAT.
/// </summary>
public static class LegalMarkdownRenderer
{
    public static StackPanel Render(IReadOnlyList<MarkdownBlock> blocks)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in blocks)
        {
            panel.Children.Add(RenderBlock(block));
        }
        return panel;
    }

    private static UIElement RenderBlock(MarkdownBlock block) => block switch
    {
        HeadingBlock(1, var text) => Heading(text, 22),
        HeadingBlock(2, var text) => Heading(text, 16),
        HeadingBlock(3, var text) => Heading(text, 13),
        ParagraphBlock(var text) => Paragraph(text),
        BulletBlock(var text) => Bullet(text),
        HyperlinkBlock(var text, var url) => HyperlinkLine(text, url),
        AdmonitionBlock(var text) => Admonition(text),
        _ => Paragraph(block.ToString() ?? string.Empty)
    };

    private static TextBlock Heading(string text, int fontSize) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, fontSize >= 22 ? 12 : 8, 0, 4),
    };

    private static TextBlock Paragraph(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        ApplyInline(tb, text);
        return tb;
    }

    private static Grid Bullet(string text)
    {
        var content = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        ApplyInline(content, text);

        // Two-column Grid, NOT a horizontal StackPanel: a horizontal StackPanel measures
        // its children with infinite width, so the content TextBlock never receives a
        // finite constraint and TextWrapping.Wrap silently never engages — long bullets
        // rendered as a single clipped line (user screenshot 2026-07-04, re-acceptance
        // dialog). The star column hands the content the remaining width, which is what
        // makes wrapping work.
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

    private static TextBlock HyperlinkLine(string text, string url)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            TextWrapping = TextWrapping.Wrap,
        };
        if (TryCreateSafeLinkUri(url, out var uri))
        {
            var link = new Hyperlink { NavigateUri = uri };
            link.Inlines.Add(new Run { Text = text });
            tb.Inlines.Add(link);
        }
        else
        {
            tb.Text = text; // malformed/unsafe URL — show the label as plain text, never throw
        }
        return tb;
    }

    private static Border Admonition(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
        };
        ApplyInline(tb, text);
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

    // Matches an inline markdown link [label](url): label has no ']', url no whitespace or ')'.
    private static readonly System.Text.RegularExpressions.Regex InlineLinkRegex =
        new(@"\[([^\]]+)\]\(([^)\s]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // http(s) + mailto only — never javascript:/file:/data:/etc., which Uri.TryCreate(Absolute) would
    // otherwise accept. Shared by BOTH the inline and block link paths so a malformed or hostile URL
    // degrades to plain text instead of becoming a live link OR throwing UriFormatException at render
    // (the block path previously did new Uri(url) unguarded). (Codex + adversarial review 2026-06-14.)
    private static bool TryCreateSafeLinkUri(string url, out Uri uri)
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

    // Inline markdown for the legal docs: **bold** AND [label](url) links (incl. mailto:). The parser
    // keeps the raw inline text (asterisks/brackets are part of the content hash, so acceptance state
    // is unaffected); we interpret it at render time. A bracket pair that isn't a usable URL falls back
    // to plain text, so malformed input can never produce a dead link or break rendering. Previously
    // only **bold** was handled, so inline links (e.g. the §11 support email) rendered as raw "[x](y)".
    private static void ApplyInline(TextBlock tb, string text)
    {
        int pos = 0;
        foreach (System.Text.RegularExpressions.Match m in InlineLinkRegex.Matches(text))
        {
            if (m.Index > pos)
                AddFormattedRuns(tb, text.Substring(pos, m.Index - pos));

            var label = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            if (TryCreateSafeLinkUri(url, out var uri))
            {
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = label });
                tb.Inlines.Add(link);
            }
            else
            {
                AddFormattedRuns(tb, label); // not a usable URL — render the label as plain text
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            AddFormattedRuns(tb, text.Substring(pos));
    }

    // **bold** splitting: odd-index segments render SemiBold. Runs without an explicit weight inherit
    // the TextBlock's FontWeight (e.g. admonitions stay SemiBold), so this also just strips literal "**".
    private static void AddFormattedRuns(TextBlock tb, string text)
    {
        var segments = text.Split("**");
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0) continue;
            var run = new Run { Text = segments[i] };
            if ((i & 1) == 1) run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            tb.Inlines.Add(run);
        }
    }
}
