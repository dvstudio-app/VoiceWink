using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Enums;
using VoiceWink.Services.Licensing;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Home page showing recording status — and, since LNC-11, the free trial's countdown (the last
/// three days emphasized) or its "has ended" line, each with a link to the License page.
/// </summary>
public sealed class HomePage : Page
{
    private static ILogger Logger => Log.ForContext<HomePage>();

    private readonly MainViewModel _viewModel;
    private readonly LicenseService _license;
    private readonly TextBlock _statusBlock;
    private readonly TextBlock _transcriptionBlock;
    private TextBlock _hintText = null!;
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

    // LNC-11: the free-trial line under the title. Resolved by HomeTrialLine from three reads of
    // the licence state (no licensing write; the trial window's last-seen advances as on every
    // status read, exactly as the License page's tick does), on Loaded and then on a slow tick — every trial boundary
    // is computed at read time (licensing.md), so nothing raises an event when a day rolls over or
    // the window ends under an open Home page. 30 s keeps the minutes honest once the countdown is
    // below a day (the License page ticks at 5 s because it also re-projects command outcomes).
    private readonly StackPanel _trialPanel;
    private readonly TextBlock _trialText;
    private readonly TextBlock _trialLink;
    private DispatcherTimer? _trialTimer;
    private static readonly TimeSpan TrialRefreshInterval = TimeSpan.FromSeconds(30);

    public HomePage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _license = App.Services.GetRequiredService<LicenseService>();
        this.Unloaded += OnUnloaded;

        // ── Title ────────────────────────────────────────────────────
        var title = new TextBlock
        {
            Text = "VoiceWink",
            FontSize = 36,
            FontWeight = FontWeights.Bold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ── Free-trial line (LNC-11) ─────────────────────────────────
        // Countdown / ended line + a link to the License page, whose Buy-primary panel carries
        // the transaction. Collapsed unless the trial is running or has ended with no key.
        _trialText = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _trialLink = AppTheme.CreateActionLink(HomeTrialLine.CountdownLinkText,
            () => App.MainWindowInstance?.NavigateToPage("license"),
            "Open the License page");
        _trialLink.HorizontalAlignment = HorizontalAlignment.Center;
        _trialLink.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetName(_trialLink, "Open the License page");
        _trialPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = Visibility.Collapsed,
            Children = { _trialText, _trialLink }
        };

        // ── Status ───────────────────────────────────────────────────
        // Wraps: StatusText carries provider error prose (a rejected image
        // prompt, a network failure), which measures far wider than the hero
        // panel. Centered + NoWrap clipped such a message on BOTH sides, so it
        // started and ended mid-word.
        _statusBlock = new TextBlock
        {
            Text = _viewModel.StatusText,
            FontSize = 15,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 40)
        };

        // ── Record button glow (larger semi-transparent circle behind) ──
        var glowCircle = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(44),
            Background = AppTheme.Brush(ColorHelper.FromArgb(40, 0, 122, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // ── Record button (blue circle with microphone icon) ─────────
        var normalBg = AppTheme.Brush(AppTheme.AccentBlue);
        var hoverBg = AppTheme.Brush(AppTheme.AccentBlueHover);

        var micIcon = new FontIcon
        {
            Glyph = "\uE720",
            FontSize = 26,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var recordButton = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = normalBg,
            Child = micIcon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        AutomationProperties.SetName(recordButton, "Record");
        ToolTipService.SetToolTip(recordButton, "Click to start/stop recording");
        recordButton.PointerEntered += (_, _) => recordButton.Background = hoverBg;
        recordButton.PointerExited += (_, _) => recordButton.Background = normalBg;
        recordButton.Tapped += async (_, _) => await _viewModel.ToggleRecordAsync();

        // Stack glow + button via a Grid so they overlap concentrically
        var buttonContainer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        buttonContainer.Children.Add(glowCircle);
        buttonContainer.Children.Add(recordButton);

        // ── Hint text below button ───────────────────────────────────
        _hintText = new TextBlock
        {
            Text = GetHintText(_viewModel.RecordingState),
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 48)
        };

        // ── Last Transcription section ───────────────────────────────
        var sectionHeader = AppTheme.CreateSectionHeader("Last Transcription");
        sectionHeader.HorizontalAlignment = HorizontalAlignment.Center;
        sectionHeader.TextAlignment = TextAlignment.Center;

        _transcriptionBlock = new TextBlock
        {
            Text = string.IsNullOrEmpty(_viewModel.LastTranscription)
                ? "No transcriptions yet. Press the microphone button or use your hotkey to start recording."
                : _viewModel.LastTranscription,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            IsTextSelectionEnabled = true,
            Foreground = AppTheme.Brush(
                string.IsNullOrEmpty(_viewModel.LastTranscription)
                    ? AppTheme.DimText
                    : AppTheme.TextSecondary),
            Padding = new Thickness(0)
        };

        var transcriptionCard = AppTheme.CreateCard(new StackPanel
        {
            Children = { _transcriptionBlock }
        });
        transcriptionCard.MaxWidth = 560;
        transcriptionCard.HorizontalAlignment = HorizontalAlignment.Center;

        // ── PropertyChanged handler ──────────────────────────────────
        _vmPropertyChanged = (_, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusText))
                    _statusBlock.Text = _viewModel.StatusText;
                else if (e.PropertyName == nameof(MainViewModel.RecordingState))
                    _hintText.Text = GetHintText(_viewModel.RecordingState);
                else if (e.PropertyName == nameof(MainViewModel.LastTranscription))
                {
                    var text = _viewModel.LastTranscription;
                    var hasText = !string.IsNullOrEmpty(text);
                    _transcriptionBlock.Text = hasText
                        ? text
                        : "No transcriptions yet. Press the microphone button or use your hotkey to start recording.";
                    _transcriptionBlock.Foreground = AppTheme.Brush(
                        hasText ? AppTheme.TextSecondary : AppTheme.DimText);
                }
            });
        };
        Loaded += (_, _) =>
        {
            _viewModel.PropertyChanged += _vmPropertyChanged;
            // LNC-11: re-read the trial line on every arrival (a key activated on the License page
            // hides it; a day rolled over on another page moves it) and keep it current while here.
            RefreshTrialLine();
            _trialTimer ??= new DispatcherTimer { Interval = TrialRefreshInterval };
            // Unsubscribe before subscribing: WinUI 3's Unloaded is not always delivered, so a second
            // Loaded would otherwise tick twice (the License page's own lesson, 2026-09-04).
            _trialTimer.Tick -= OnTrialTick;
            _trialTimer.Tick += OnTrialTick;
            _trialTimer.Start();
            // Refresh with current values in case they changed while on another page
            _statusBlock.Text = _viewModel.StatusText;
            var text = _viewModel.LastTranscription;
            var hasText = !string.IsNullOrEmpty(text);
            _transcriptionBlock.Text = hasText
                ? text
                : "No transcriptions yet. Press the microphone button or use your hotkey to start recording.";
            _transcriptionBlock.Foreground = AppTheme.Brush(
                hasText ? AppTheme.TextSecondary : AppTheme.DimText);
        };

        // ── Hero layout (centered vertically) ───────────────────────
        var heroPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 560,
            Margin = new Thickness(0, 60, 0, 0),
            Children =
            {
                title,
                _trialPanel,
                _statusBlock,
                buttonContainer,
                _hintText,
                sectionHeader,
                transcriptionCard
            }
        };

        AppTheme.SetPageScrollContent(this, heroPanel);
    }

    private static string GetHintText(RecordingState state) => state switch
    {
        RecordingState.Idle => "Tap to speak",
        RecordingState.Starting => "Starting...",
        RecordingState.Recording => "Tap to stop",
        RecordingState.Transcribing => "Transcribing...",
        RecordingState.Enhancing => "Enhancing...",
        _ => ""
    };

    private void OnTrialTick(object? sender, object e) => RefreshTrialLine();

    /// <summary>
    /// LNC-11: three reads (the status read observes the trial window's last-seen, as every status
    /// read does), one pure decision, one render. Fail-soft — a settings read that throws
    /// hides the line rather than the page (the recording gate makes the same read and fails
    /// CLOSED there, because that one is a gate; this is a label).
    /// </summary>
    private void RefreshTrialLine()
    {
        HomeTrialLine line;
        try
        {
            line = HomeTrialLine.Resolve(
                _license.GetCachedStatus(),
                _license.GetTrialTimeRemaining(),
                _license.HasFirstRunGraceEnded());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Home trial line: license read failed — hiding the line");
            line = HomeTrialLine.Hidden;
        }
        RenderTrialLine(line);
    }

    private void RenderTrialLine(HomeTrialLine line)
    {
        if (!line.IsVisible)
        {
            _trialPanel.Visibility = Visibility.Collapsed;
            return;
        }
        // The emphasized countdown and the ended line are amber SENTENCES, so they take the
        // theme's WarningText swatch (WCAG-checked on the page ground), never the accent tint.
        var emphasized = line.Kind is HomeTrialLineKind.CountdownEmphasized or HomeTrialLineKind.Ended;
        _trialText.Text = line.Text;
        _trialText.Foreground = AppTheme.Brush(emphasized ? AppTheme.WarningText : AppTheme.SubtleText);
        _trialText.FontWeight = emphasized ? FontWeights.SemiBold : FontWeights.Normal;
        _trialLink.Text = line.LinkText;
        _trialPanel.Visibility = Visibility.Visible;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        // Stopped AND unsubscribed: a DispatcherTimer holds a strong reference to its handler.
        if (_trialTimer != null)
        {
            _trialTimer.Stop();
            _trialTimer.Tick -= OnTrialTick;
        }
    }
}
