using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace VoiceWink.Helpers;

/// <summary>
/// Maps a parsed <see cref="MarkdownBlock"/> sequence to a <see cref="StackPanel"/>
/// of styled WinUI controls (LGL-1). Pure block-to-control translation — no parsing.
/// Tests don't construct WinUI controls; rendering is exercised via UAT.
///
/// <para>Inline handling (<c>**bold**</c>, <c>[label](url)</c>, the safe-URI gate) lives in
/// <see cref="MarkdownInline"/> since UI-22, where the notices renderer shares it. It moved
/// unchanged and is called with no optional features, so these documents render exactly as
/// before — including the backticks and the one Windows path in <c>privacy-v5.md</c>, which
/// stay literal.</para>
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
        MarkdownInline.Apply(tb, text);
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
        MarkdownInline.Apply(content, text);

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
        if (MarkdownInline.TryCreateSafeLinkUri(url, out var uri))
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
        MarkdownInline.Apply(tb, text);
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

    // The inline engine (the [label](url) regex, the safe-URI gate, the **bold** splitting) moved to
    // MarkdownInline in UI-22 so the third-party-notices renderer shares ONE copy of it. The legal
    // parser keeps the raw inline text — asterisks and brackets are part of the content hash, so
    // acceptance state is unaffected — and it is interpreted here at render time, as before.
}
