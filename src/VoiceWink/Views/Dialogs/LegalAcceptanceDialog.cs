using global::System.Diagnostics;
using global::System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Legal;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// Modal acceptance gate (LGL-1). Used by <c>App.EnsureLegalAcceptanceAsync</c> for
/// onboarded users with stale or absent acceptance. The dialog itself owns
/// persistence: Accept-button click calls <see cref="LegalAcceptanceService.Accept"/>
/// and sets <c>args.Cancel = true</c> on failure so the dialog stays open and the
/// user can retry. Only when persistence durably succeeds does the dialog close
/// with <see cref="ContentDialogResult.Primary"/>. Caller therefore inspects the
/// return value only — no double-ownership of the persistence call.
/// </summary>
public sealed class LegalAcceptanceDialog : ContentDialog
{
    private static ILogger Logger => Log.ForContext<LegalAcceptanceDialog>();

    private readonly LegalAcceptanceService _service;
    private readonly CheckBox _agreeCheckbox;
    private readonly TextBlock _errorText;

    public LegalAcceptanceDialog(LegalAcceptanceService service)
    {
        _service = service;
        var state = service.GetState();

        Title = state.IsFirstAcceptance ? "Welcome — review terms" : "Updated terms";
        PrimaryButtonText = "Accept and continue";
        CloseButtonText = "Decline and exit";
        DefaultButton = ContentDialogButton.None;
        IsPrimaryButtonEnabled = false;
        RequestedTheme = AppTheme.ElementTheme;

        _agreeCheckbox = new CheckBox
        {
            Content = "I have read and agree to the EULA and Privacy Policy.",
            // Center content + drop the 32px MinHeight so the box lines up with the single-line
            // label (WinUI's default Top alignment floats the text off-center). Mirrors OnboardingPage.
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _agreeCheckbox.Checked += (_, _) => IsPrimaryButtonEnabled = true;
        _agreeCheckbox.Unchecked += (_, _) => IsPrimaryButtonEnabled = false;

        _errorText = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Content = BuildBody(service, state);
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private UIElement BuildBody(LegalAcceptanceService service, LegalAcceptanceState state)
    {
        var panel = new StackPanel { Spacing = 8 };

        if (!state.IsFirstAcceptance)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "We've updated our terms — please review.",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        // Shared scroller (AppTheme.CreateDialogScroller) owns the overlay-scrollbar
        // right-padding fix so it stays consistent across every dialog.
        var termsPanel = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SectionHeader($"EULA · v{service.Eula!.Version} · {service.Eula!.LastUpdated.ToString("d", CultureInfo.CurrentCulture)}"),
                LegalMarkdownRenderer.Render(service.Eula!.Body),
                SectionHeader($"Privacy Policy · v{service.Privacy!.Version} · {service.Privacy!.LastUpdated.ToString("d", CultureInfo.CurrentCulture)}"),
                LegalMarkdownRenderer.Render(service.Privacy!.Body),
            },
        };

        panel.Children.Add(AppTheme.CreateDialogScroller(termsPanel, 360));
        panel.Children.Add(_agreeCheckbox);
        panel.Children.Add(_errorText);
        return panel;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        Margin = new Thickness(0, 4, 0, 8),
    };

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            _service.Accept();
            // Persistence durably succeeded — let the dialog close with Primary result.
        }
        catch (LegalAcceptanceWriteException ex)
        {
            Logger.Warning(ex, "Legal acceptance persistence failed; keeping dialog open");
            _errorText.Text = "We couldn't save your acceptance. Please try again, or restart the app.";
            _errorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Unexpected exception during Accept; keeping dialog open");
            _errorText.Text = "Something went wrong. Please try again.";
            _errorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}

/// <summary>
/// Bundle-corrupted fail-closed dialog (LGL-1). Shown when the bundled EULA or
/// Privacy Policy can't be parsed — the only path forward is a reinstall, so the
/// dialog has a single CTA and exits the process on close.
/// </summary>
public sealed class LegalBundleCorruptedDialog : ContentDialog
{
    private static ILogger Logger => Log.ForContext<LegalBundleCorruptedDialog>();

    public LegalBundleCorruptedDialog()
    {
        Title = "VoiceWink can't load its terms";
        PrimaryButtonText = "Exit and reinstall";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "We can't display VoiceWink's End User Licence Agreement and Privacy Policy from this build. " +
                           "Please reinstall the latest version of VoiceWink to repair the bundle.",
                    FontSize = 13,
                    Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "Your settings, history, and licence are preserved across reinstalls.",
                    FontSize = 12,
                    Foreground = AppTheme.Brush(AppTheme.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                },
            },
        };

        PrimaryButtonClick += OnExitAndReinstall;
    }

    private void OnExitAndReinstall(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://voicewink.app/install")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to open reinstall link");
        }
    }
}
