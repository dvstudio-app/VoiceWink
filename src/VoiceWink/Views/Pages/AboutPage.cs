using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.Support;
using VoiceWink.Views.Dialogs;

namespace VoiceWink.Views.Pages;

/// <summary>
/// About sidebar page — groups the **About** (LGL-4), **Legal** (LGL-1), and **Support** (REL-3)
/// cards, in that order. These previously lived as cards inside the Settings page; they were
/// promoted to their own sidebar entry. All UI is built in code-behind per the WinUI 3 constraints.
/// </summary>
public sealed class AboutPage : Page
{
    private static ILogger Logger => Log.ForContext<AboutPage>();

    public AboutPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        BuildUI();
    }

    private void BuildUI()
    {
        var header = AppTheme.CreatePageHeader("About", "Version, legal, and support for VoiceWink.");

        var pageContent = new StackPanel
        {
            Children =
            {
                header,
                BuildAboutCard(),
                BuildLegalCard(),
                BuildSupportCard(),
            }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    /// <summary>About section (LGL-4) — publisher, version, copyright, and links.</summary>
    private Border BuildAboutCard()
    {
        var header = AppTheme.CreateSectionHeader("About");
        header.Margin = new Thickness(0, 0, 0, 12);

        var text = new TextBlock
        {
            Text = AboutInfo.BuildText(AboutInfo.CurrentVersion),
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        // The visible text stays the bare domain; the target carries the `?src=app` source tag like
        // every other voicewink.app link the app publishes (UI-19b — this was the one untagged
        // literal left after PR #992, so About-page clicks read as "Direct" on the site's beacon).
        var siteLink = new HyperlinkButton
        {
            Content = "voicewink.app",
            NavigateUri = new Uri(VoiceWinkUrls.Marketing),
        };
        var repoLink = new HyperlinkButton
        {
            Content = "Source code (GPL v3)",
            NavigateUri = new Uri("https://github.com/dvstudio-app/VoiceWink"),
        };

        // What's new (REL-4) — opens the full changelog history (does not advance the last-seen marker).
        var whatsNew = new HyperlinkButton { Content = "What's new" };
        whatsNew.Click += async (_, _) =>
        {
            try
            {
                var parser = App.Services.GetRequiredService<ChangelogParser>();
                // ParseForDisplay, not raw Parse: drops the rolled changelog's empty [Unreleased]
                // header (raw would render a blank "vUnreleased") and maps a content-bearing one
                // to the running build, same as the post-update dialog.
                var dialog = new WhatsNewDialog(parser.ParseForDisplay(treatUnreleasedAs: AboutInfo.CurrentVersion))
                {
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme,
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to show What's new dialog");
            }
        };

        // Third-party licenses (LGL-6 item 2) — the shipped notices manifest, opened in Notepad.
        var thirdParty = new HyperlinkButton { Content = "Third-party licenses" };
        thirdParty.Click += (_, _) => ShellFolder.OpenTextFile(AboutInfo.ThirdPartyNoticesPath);

        // The four links share one row that wraps on a narrow window instead of clipping.
        var links = new FlowPanel
        {
            HorizontalSpacing = 16,
            VerticalSpacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { siteLink, repoLink, whatsNew, thirdParty },
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { header, text, links }
        };
        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Legal section (LGL-1) — clickable rows that route to <see cref="Pages.LegalPage"/>
    /// with the matching pane preselected. When a stale-acceptance state surfaces here
    /// (rare for a launched user past the gate, but possible during dogfooding while
    /// versions are being bumped), an inline notice surfaces "Review now" to re-show
    /// the modal.
    /// </summary>
    private Border BuildLegalCard()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Legal");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var legal = App.Services.GetRequiredService<Services.Legal.LegalAcceptanceService>();
        var content = new StackPanel
        {
            Spacing = 4,
            Children = { sectionHeader }
        };

        if (!legal.BundleHealthy)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Couldn't load EULA / Privacy Policy from this build. Please reinstall VoiceWink.",
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
            return AppTheme.CreateCard(content);
        }

        var dateFmt = global::System.Globalization.CultureInfo.CurrentCulture;
        content.Children.Add(BuildLegalRow(
            $"View End User Licence Agreement (v{legal.Eula!.Version}, updated {legal.Eula!.LastUpdated.ToString("d", dateFmt)})",
            Pages.LegalDocument.Eula));
        content.Children.Add(BuildLegalRow(
            $"View Privacy Policy (v{legal.Privacy!.Version}, updated {legal.Privacy!.LastUpdated.ToString("d", dateFmt)})",
            Pages.LegalDocument.Privacy));

        if (legal.RequiresPromptAtStartup())
        {
            content.Children.Add(new TextBlock
            {
                Text = "An update to our terms is available — please re-accept.",
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return AppTheme.CreateCard(content);
    }

    private Border BuildLegalRow(string label, Pages.LegalDocument doc)
    {
        var arrow = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var labelTb = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(labelTb, 0);
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(labelTb);
        grid.Children.Add(arrow);

        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = AppTheme.TransparentBrush,
            Child = grid,
        };
        border.Tapped += (_, _) => App.MainWindowInstance?.NavigateToLegalPage(doc);
        // Clear hover affordance: a rounded row-highlight so it reads as a clickable row.
        border.PointerEntered += (_, _) => border.Background = AppTheme.Brush(AppTheme.RowHoverBg);
        border.PointerExited += (_, _) => border.Background = AppTheme.TransparentBrush;
        return border;
    }

    /// <summary>
    /// Support section (REL-3) — "Report a problem" opens the report dialog (consent + optional
    /// bundle); "Suggest an improvement" opens the email app directly, since a suggestion carries
    /// no logs and needs no dialog.
    /// </summary>
    private Border BuildSupportCard()
    {
        var header = AppTheme.CreateSectionHeader("Support");
        header.Margin = new Thickness(0, 0, 0, 12);

        // No blurb (Codex + Gemini diff r1 Blocker, PR #937): the one it carried claimed that BOTH
        // buttons open the mail app, which is false for "Report a problem" — that opens a dialog
        // first and never reaches the mail app when the dialog is cancelled or the bundle fails
        // closed. The header and the two button labels say everything true there is to say;
        // AboutInfoTests pins the sentence's absence.
        var reportButton = AppTheme.CreateSecondaryButton("Report a problem", async (_, _) =>
        {
            try
            {
                var dialog = new ReportProblemDialog { XamlRoot = this.XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to show Report a problem dialog");
            }
        });

        // Shown only when the launch itself throws — the shell refusing the mailto (no app
        // associated with it, a policy block, a broken handler command) — the one case where the
        // click would otherwise do nothing visible. Selectable because on this path the address
        // IS the remedy.
        var suggestStatus = new TextBlock
        {
            Text = $"Couldn't open your email app. Write to {VoiceWinkUrls.SupportEmail}.",
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
        };
        var suggestButton = AppTheme.CreateSecondaryButton("Suggest an improvement", (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AboutInfo.BuildSuggestionMailto(AboutInfo.CurrentVersion))
                {
                    UseShellExecute = true,
                });
                suggestStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to open the suggestion email");
                suggestStatus.Visibility = Visibility.Visible;
            }
        });

        // Same wrapping row as the About card's links: two buttons side by side, the second
        // dropping under the first on a narrow window instead of clipping.
        var buttons = new FlowPanel
        {
            HorizontalSpacing = 8,
            VerticalSpacing = 8,
            Children = { reportButton, suggestButton },
        };

        var content = new StackPanel { Spacing = 4, Children = { header, buttons, suggestStatus } };
        return AppTheme.CreateCard(content);
    }
}
