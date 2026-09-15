using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.Support;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// "What's new" changelog dialog (REL-4). Pure presentation — it renders the supplied
/// <see cref="ChangelogEntry"/> list and does NOT persist anything. The caller decides whether to
/// advance the last-seen marker (the auto-after-update path does; the Settings reopen does not).
/// All UI is built in code-behind, per the WinUI 3 constraints.
/// </summary>
public sealed class WhatsNewDialog : ContentDialog
{
    public WhatsNewDialog(IReadOnlyList<ChangelogEntry> entries, string? intro = null)
    {
        Title = "What's new in VoiceWink";
        PrimaryButtonText = "Got it";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;
        Content = BuildBody(entries, intro);
    }

    private static UIElement BuildBody(IReadOnlyList<ChangelogEntry> entries, string? intro)
    {
        var panel = new StackPanel { Spacing = 10 };

        if (!string.IsNullOrWhiteSpace(intro))
        {
            panel.Children.Add(new TextBlock
            {
                Text = intro,
                Foreground = AppTheme.Brush(AppTheme.TextSecondary),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No changelog entries are available.",
                Foreground = AppTheme.Brush(AppTheme.TextSecondary),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var entry in entries)
            {
                panel.Children.Add(VersionHeader(entry));
                foreach (var section in entry.Sections)
                {
                    if (!string.IsNullOrWhiteSpace(section.Title))
                        panel.Children.Add(SectionLabel(section.Title));
                    foreach (var item in section.Items)
                        panel.Children.Add(Bullet(item));
                }
            }
        }

        // Shared scroller: fixed height + right padding so the overlay scrollbar never
        // hides the text's right edge (the fix that already shipped for the legal dialog).
        return AppTheme.CreateDialogScroller(panel, 380);
    }

    private static TextBlock VersionHeader(ChangelogEntry e) => new()
    {
        Text = e.Date is null ? $"v{e.Version}" : $"v{e.Version} — {e.Date}",
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        Margin = new Thickness(0, 8, 0, 2),
    };

    private static TextBlock SectionLabel(string title) => new()
    {
        Text = title,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock Bullet(string text) => new()
    {
        Text = "•  " + StripMarkdown(text),
        FontSize = 13,
        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 0, 0, 0),
    };

    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    /// <summary>Render the small subset of inline markdown used in the changelog as plain text.</summary>
    private static string StripMarkdown(string s) =>
        MdLink.Replace(s, "$1").Replace("**", string.Empty).Replace("`", string.Empty);
}
