using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// The Enhancement page's offer to move an Ollama model from the processor to the PC's
/// graphics: "Switch to graphics" runs the fix — a spinner while it works, then the dialog stays
/// open with the result; "Not now" closes it. Plain words throughout (owner, 2026-09-28): the
/// environment variable is named only where the user has to set it by hand. Plain
/// <c>ContentDialog</c> (the <see cref="RestartToApplyDialog"/> shape); all UI in code-behind.
/// </summary>
public sealed class OllamaGraphicsDialog : ContentDialog
{
    private static ILogger Logger => Log.ForContext<OllamaGraphicsDialog>();

    private readonly Func<CancellationToken, Task<OllamaGraphicsFixOutcome>> _fix;
    private readonly TextBlock _body;
    private readonly TextBlock _status;
    private readonly ProgressRing _ring;
    private readonly Grid _statusRow;
    private readonly FontIcon _icon;
    private readonly Button _doneButton;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    public OllamaGraphicsDialog(string model, Func<CancellationToken, Task<OllamaGraphicsFixOutcome>> fix)
    {
        _fix = fix;

        Title = "Speed up AI enhancement";
        PrimaryButtonText = "Switch to graphics";
        CloseButtonText = "Not now";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;

        _body = new TextBlock
        {
            Text = BodyText(model),
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        };
        _ring = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _status = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        // The result's mark sits where the spinner was: a green check, or an amber warning.
        _icon = new FontIcon
        {
            FontSize = 16,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        // A Grid, not a horizontal StackPanel: the star column is what lets the result line wrap.
        _statusRow = new Grid
        {
            ColumnSpacing = 8,
            Visibility = Visibility.Collapsed,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        _statusRow.Children.Add(_ring);
        _statusRow.Children.Add(_icon);
        _statusRow.Children.Add(_status);
        Grid.SetColumn(_status, 1);

        // After the result the footer's buttons are removed and this one, centred, closes the
        // dialog (owner, 2026-09-28: a lone footer button sits in the right-hand half).
        _doneButton = new Button
        {
            MinWidth = 140,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _doneButton.Click += (_, _) => Hide();
        // Accent colours through the Button's own resource keys (the page's Save button colours);
        // the template keeps its hover and pressed behaviour.
        var accent = AppTheme.Brush(AppTheme.AccentBlue);
        var accentHover = AppTheme.Brush(AppTheme.AccentBlueHover);
        var onAccent = AppTheme.Brush(AppTheme.TextPrimary);
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPressed" })
            _doneButton.Resources[key] = accent;
        _doneButton.Resources["ButtonBackgroundPointerOver"] = accentHover;
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
            _doneButton.Resources[key] = onAccent;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            _doneButton.Resources[key] = accent;

        var content = new StackPanel { Spacing = 12, MaxWidth = 440 };
        content.Children.Add(_body);
        content.Children.Add(_statusRow);
        content.Children.Add(_doneButton);
        Content = content;

        PrimaryButtonClick += OnPrimaryClick;
        // Closing while the fix runs cancels its wait and re-check; a started restart still finishes.
        Closing += (_, _) => _cts.Cancel();
        // With the footer's buttons emptied, Esc no longer closes the dialog by itself.
        KeyDown += (_, e) =>
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape && _doneButton.Visibility == Visibility.Visible)
                Hide();
        };
    }

    internal static string BodyText(string model)
        => $"Ollama runs {model} on the processor. Your PC's graphics can run it faster. " +
           "Switching restarts Ollama.";

    internal const string WorkingText = "Switching… this can take up to a minute.";

    /// <summary>The result line — what happened and, where needed, the one thing to do next.</summary>
    internal static string OutcomeText(OllamaGraphicsFixOutcome outcome) => outcome switch
    {
        OllamaGraphicsFixOutcome.NowOnGraphics => "Ollama now uses your graphics.",
        OllamaGraphicsFixOutcome.StillOnProcessor => "Ollama still uses the processor. Your graphics may not support this model.",
        OllamaGraphicsFixOutcome.Unknown => "Ollama restarted. Your next recording checks where the model runs.",
        OllamaGraphicsFixOutcome.RestartManually => "One more step: quit Ollama from its icon in the taskbar, then open it again.",
        OllamaGraphicsFixOutcome.DidNotRestart => "Ollama didn't start again. Open Ollama from the Start menu.",
        _ => $"VoiceWink couldn't switch Ollama. To do it yourself, add {OllamaIntegratedGraphics.VariableName} = 1 " +
             "under \"Edit environment variables for your account\" and restart Ollama."
    };

    /// <summary>Only a reading that puts the model in video memory counts as switched.</summary>
    internal static bool IsConfirmedOnGraphics(OllamaGraphicsFixOutcome outcome)
        => outcome == OllamaGraphicsFixOutcome.NowOnGraphics;

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Stay open to show the result; a second click while running does nothing.
        args.Cancel = true;
        if (_running)
            return;
        _running = true;
        IsPrimaryButtonEnabled = false;
        _ring.IsActive = true;
        _ring.Visibility = Visibility.Visible;
        _status.Text = WorkingText;
        _statusRow.Visibility = Visibility.Visible;

        OllamaGraphicsFixOutcome outcome;
        try
        {
            outcome = await _fix(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the dialog closed
        }
        catch (Exception ex)
        {
            // Inside WinUI's callback: a throw here would end the process.
            Logger.Warning("Integrated-graphics fix failed: {ErrorType}", ex.GetType().Name);
            outcome = OllamaGraphicsFixOutcome.DidNotRestart;
        }

        _ring.IsActive = false;
        _ring.Visibility = Visibility.Collapsed;
        // The dialog may already be closing (Not now pressed as the fix returned): leave it alone.
        if (_cts.IsCancellationRequested)
            return;

        // Only a confirmed graphics reading earns the check mark (Codex diff r1).
        var confirmed = IsConfirmedOnGraphics(outcome);
        _icon.Glyph = confirmed ? "\uE73E" : "\uE7BA"; // CheckMark / Warning
        _icon.Foreground = AppTheme.Brush(confirmed ? AppTheme.AccentGreen : AppTheme.WarningText);
        _icon.Visibility = Visibility.Visible;
        _status.Text = OutcomeText(outcome);
        _status.Foreground = AppTheme.Brush(confirmed ? AppTheme.TextPrimary : AppTheme.WarningText);

        // Empty texts hide the footer's buttons; the centred button in the content replaces them.
        PrimaryButtonText = "";
        CloseButtonText = "";
        DefaultButton = ContentDialogButton.None;
        _doneButton.Content = confirmed ? "Done" : "Close";
        _doneButton.Visibility = Visibility.Visible;
        _doneButton.Focus(FocusState.Programmatic);
    }
}
