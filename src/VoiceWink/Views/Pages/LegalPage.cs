using global::System.Diagnostics;
using global::System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Legal;

namespace VoiceWink.Views.Pages;

/// <summary>Discriminator used by <see cref="LegalPage"/> to preselect a pane.</summary>
public enum LegalDocument { Eula, Privacy }

/// <summary>
/// Settings → Legal viewer (LGL-1). Hosts both the EULA and the Privacy Policy in
/// a tabbed-by-toggle layout (no <c>Pivot</c> per the WinUI / NavigationView ban
/// in CLAUDE.md). Renders bundled markdown via <see cref="LegalMarkdownRenderer"/>.
/// Bundle-unhealthy state degrades gracefully here (the launch gate is fail-closed
/// on the same condition — see <c>App.EnsureLegalAcceptanceAsync</c>).
/// </summary>
public sealed class LegalPage : Page
{
    private static ILogger Logger => Log.ForContext<LegalPage>();

    private readonly LegalAcceptanceService _service;
    private readonly LegalDocument _initial;
    private ContentControl? _paneHost;
    private Border? _eulaToggle;
    private Border? _privacyToggle;
    private LegalDocument _current;

    public LegalPage() : this(LegalDocument.Eula) { }

    public LegalPage(LegalDocument initial)
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _service = App.Services.GetRequiredService<LegalAcceptanceService>();
        _initial = initial;
        _current = initial;
        BuildUI();
    }

    private void BuildUI()
    {
        var backButton = BuildBackButton();
        var header = AppTheme.CreatePageHeader("Legal", "End User Licence Agreement and Privacy Policy.");

        if (!_service.BundleHealthy)
        {
            AppTheme.SetPageScrollContent(this, new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    backButton,
                    header,
                    AppTheme.CreateCard(new TextBlock
                    {
                        Text = "We couldn't load the EULA and Privacy Policy from this build. " +
                               "Please reinstall VoiceWink — your settings, history, and licence are preserved.",
                        Foreground = AppTheme.Brush(AppTheme.WarningText),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                    }),
                },
            });
            return;
        }

        _eulaToggle = BuildToggle("EULA", LegalDocument.Eula);
        _privacyToggle = BuildToggle("Privacy Policy", LegalDocument.Privacy);

        var toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { _eulaToggle, _privacyToggle },
        };

        _paneHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        UpdateActivePane();

        AppTheme.SetPageScrollContent(this, new StackPanel
        {
            Spacing = 4,
            Children = { backButton, header, toggleRow, _paneHost },
        });
    }

    /// <summary>"‹ Back to About" affordance — this page is reachable only from the About page,
    /// so it gives the user an explicit way back (no sidebar entry of its own).</summary>
    private static UIElement BuildBackButton()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "‹",
                    FontSize = 16,
                    Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Back to About",
                    FontSize = 13,
                    Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var border = new Border
        {
            Child = row,
            Padding = new Thickness(8, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Background = AppTheme.TransparentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
        };
        border.Tapped += (_, _) => App.MainWindowInstance?.NavigateToPage("about");
        border.PointerEntered += (_, _) => border.Background = AppTheme.Brush(AppTheme.RowHoverBg);
        border.PointerExited += (_, _) => border.Background = AppTheme.TransparentBrush;
        return border;
    }

    private Border BuildToggle(string label, LegalDocument doc)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Child = tb,
        };
        border.Tapped += (_, _) =>
        {
            _current = doc;
            UpdateActivePane();
        };
        return border;
    }

    private void UpdateActivePane()
    {
        if (_paneHost == null || _eulaToggle == null || _privacyToggle == null) return;

        StyleToggle(_eulaToggle, _current == LegalDocument.Eula);
        StyleToggle(_privacyToggle, _current == LegalDocument.Privacy);

        var doc = _current == LegalDocument.Eula ? _service.Eula! : _service.Privacy!;
        _paneHost.Content = BuildPane(doc, _current);
    }

    private static void StyleToggle(Border border, bool active)
    {
        border.Background = AppTheme.Brush(active ? AppTheme.ActivePillBg : AppTheme.RowBg);
        if (border.Child is TextBlock tb)
            tb.Foreground = AppTheme.Brush(active ? AppTheme.AccentBlue : AppTheme.TextSecondary);
    }

    private static UIElement BuildPane(LegalDocumentMetadata doc, LegalDocument kind)
    {
        var versionLine = new TextBlock
        {
            Text = $"Version {doc.Version} · last updated {doc.LastUpdated.ToString("d", CultureInfo.CurrentCulture)}",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var rendered = LegalMarkdownRenderer.Render(doc.Body);

        // "View in browser" link at the bottom of the pane — points at the
        // versioned URL on voicewink.app/legal so users who want native
        // browser rendering have an escape hatch.
        var slug = kind == LegalDocument.Eula ? "eula" : "privacy";
        var browserLink = new TextBlock
        {
            Text = $"View in browser: voicewink.app/legal/{slug}-v{doc.Version}",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            Margin = new Thickness(0, 12, 0, 0),
        };
        browserLink.Tapped += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo($"https://voicewink.app/legal/{slug}-v{doc.Version}")
                { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to open browser link for {Slug}", slug);
            }
        };
        browserLink.PointerEntered += (_, _) => browserLink.Opacity = 0.8;
        browserLink.PointerExited += (_, _) => browserLink.Opacity = 1.0;
        ToolTipService.SetToolTip(browserLink, $"https://voicewink.app/legal/{slug}-v{doc.Version}");

        return AppTheme.CreateCard(new StackPanel
        {
            Spacing = 4,
            Children = { versionLine, rendered, browserLink },
        });
    }
}
