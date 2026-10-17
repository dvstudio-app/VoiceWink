using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Licensing;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Home page, as it was before 2026-10-14 (title, mic, last transcription) with three additions the
/// owner chose that day after two other redesigns were rejected: the time saved as a small pill
/// under the title (opens Metrics), the status line directly under the mic, and under that
/// "Press [hotkey] in any app to dictate" with the hotkey in bold — a tester skipped setup and never
/// learned the hotkey. The last transcription's card carries a copy button. Also, when
/// they apply: the free trial's countdown or "has ended" line (LNC-11) and, once, the Microsoft
/// Store rating line (LNC-14, <see cref="StoreRatingPrompt"/>).
/// </summary>
public sealed class HomePage : Page
{
    private static ILogger Logger => Log.ForContext<HomePage>();

    private readonly MainViewModel _viewModel;
    private readonly LicenseService _license;
    private readonly TextBlock _statusBlock;
    private readonly TextBlock _transcriptionBlock;
    private readonly Border _copyButton;
    private readonly PropertyChangedEventHandler _vmPropertyChanged;

    /// <summary>What the last-transcription card says before anything is dictated. It names no way
    /// to record: the hotkey line under the mic is that.</summary>
    internal const string NoTranscriptionText = "No transcriptions yet.";

    /// <summary>The hotkey line: "Press", the hotkey in bold, "in any app to dictate" (owner's
    /// wording); the detail is its tooltip.</summary>
    internal const string HotkeyLead = "Press";
    internal const string HotkeyTail = "in any app to dictate";
    internal const string HotkeyLineTooltip = "Text appears at your cursor. Hold to talk, or tap to start and stop. Click the hotkey to change it.";

    // LNC-11: the free-trial line under the title. Resolved by HomeTrialLine from three reads of
    // the licence state (no licensing write; the trial window's last-seen advances as on every
    // status read, exactly as the License page's tick does), on Loaded and then on a slow tick — every trial boundary
    // is computed at read time (licensing.md), so nothing raises an event when a day rolls over or
    // the window ends under an open Home page. 30 s keeps the minutes honest once the countdown is
    // below a day (the License page ticks at 5 s because it also re-projects command outcomes).
    private readonly StackPanel _trialPanel;

    // The model downloads running in the background (Express setup starts them after its last
    // screen), with Cancel, so the first page the user lands on says the app is not fully ready yet.
    private readonly DownloadsCard _downloadsCard;
    private readonly TextBlock _trialText;
    private readonly TextBlock _trialLink;
    private DispatcherTimer? _trialTimer;
    private static readonly TimeSpan TrialRefreshInterval = TimeSpan.FromSeconds(30);

    // LNC-14 (a): "Rate VoiceWink in the Microsoft Store", once, for a Store install after about a week
    // of use. The prompt is the WINDOW's (one per process), handed to every Home page it builds, so the
    // line survives a page switch within the session that saw it. The timer asks the
    // prompt, while the line is displayed and has not yet counted, whether the window has now been on
    // screen with it long enough to count as seen (StoreRatingPrompt explains why Loaded is too early).
    private readonly StoreRatingPrompt _storeRating;
    private readonly TextBlock _storeRatingLink;
    private DispatcherTimer? _storeRatingSeenTimer;
    // Every second while the line is on screen (the 3-second rule needs it); every 15 while the window is
    // hidden — the default tray session can last for days, and only the restore needs catching.
    private static readonly TimeSpan StoreRatingOnScreenPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StoreRatingHiddenPoll = TimeSpan.FromSeconds(15);

    // The time saved (owner, 2026-10-14: a constant reminder of the app's value): a pill under the
    // title in the app's badge style — clock, the figure, "saved with VoiceWink", a chevron — that
    // opens Metrics. The Metrics page's number (TimeSavedEstimate), shown from the very start
    // (minutes, then hours), re-read on every arrival and whenever a dictation updates the totals.
    private readonly Services.Data.LifetimeMetricsService _metrics;
    private readonly Button _timeSavedPill;
    private readonly Run _timeSavedValue;
    private bool _metricsAttached;
    private long _timeSavedRefreshVersion;

    // Spacing, top to bottom, as the owner tuned it on a local build (2026-10-14): more air above the
    // mic and between the status and the hotkey line, less between the hotkey line and the last
    // transcription (the section header adds its own 24 above itself).
    private const double TitleGapBelow = 8;
    private const double LineGapBelow = 16;      // under the pill, the trial line and the Store line
    private const double MicGapAbove = 28;
    private const double MicGapBelow = 12;
    private const double StatusGapBelow = 22;
    private const double HotkeyGapBelow = 24;

    /// <param name="recordingHotkey">The recording hotkey's canonical binding ("RightControl",
    /// "Ctrl+Space"), read by the window when it builds the page — Home is rebuilt on every
    /// navigation, so a change made in Settings shows on return.</param>
    public HomePage(StoreRatingPrompt storeRating, Services.Data.LifetimeMetricsService metrics,
        Services.System.ClipboardService clipboard, string recordingHotkey)
    {
        _storeRating = storeRating;
        _metrics = metrics;
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
            Margin = new Thickness(0, 0, 0, TitleGapBelow)
        };

        // ── Time saved pill ──────────────────────────────────────────
        // A Button rather than a tapped Border, so it takes keyboard focus and Enter; its look is set
        // through the Button's own resource keys (the sidebar's pattern), the focus visual kept.
        // The badge style is the app's own (the "Active" marker: a tinted ground, small text).
        // The tint is the accent; the text and icons are its readable shade, which clears 4.5:1 on
        // the tint up to ThemeSwatches.TintAlphaMax — the pressed state's alpha.
        var accent = AppTheme.AccentBlue;
        var accentText = AppTheme.Brush(AppTheme.AccentBlueText);
        _timeSavedValue = new Run
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        };
        var timeSavedText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        timeSavedText.Inlines.Add(_timeSavedValue);
        timeSavedText.Inlines.Add(new Run
        {
            Text = " " + TimeSavedEstimate.HomeLabel,
            Foreground = accentText,
        });
        _timeSavedPill = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "",   // Clock - the glyph of the Metrics page's "Time Saved" card
                        FontSize = 13,
                        Foreground = accentText,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    timeSavedText,
                    new FontIcon
                    {
                        Glyph = "",   // ChevronRight
                        FontSize = 9,
                        Foreground = accentText,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 5, 12, 5),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0),
            Background = AppTheme.Brush(ColorHelper.FromArgb(36, accent.R, accent.G, accent.B)),
            Margin = new Thickness(0, 0, 0, LineGapBelow),
            Visibility = Visibility.Collapsed,
        };
        _timeSavedPill.Resources["ButtonBackgroundPointerOver"] = AppTheme.Brush(ColorHelper.FromArgb(44, accent.R, accent.G, accent.B));
        _timeSavedPill.Resources["ButtonBackgroundPressed"] = AppTheme.Brush(ColorHelper.FromArgb(ThemeSwatches.TintAlphaMax, accent.R, accent.G, accent.B));
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            _timeSavedPill.Resources[key] = AppTheme.TransparentBrush;
        ToolTipService.SetToolTip(_timeSavedPill, TimeSavedEstimate.HomeCardTooltip);
        _timeSavedPill.Click += (_, _) => App.MainWindowInstance?.NavigateToPage("metrics");

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
            Margin = new Thickness(0, 0, 0, LineGapBelow),
            Visibility = Visibility.Collapsed,
            Children = { _trialText, _trialLink }
        };

        // ── Background downloads ─────────────────────────────────────
        _downloadsCard = new DownloadsCard("Downloads");
        _downloadsCard.Element.Margin = new Thickness(0, 0, 0, LineGapBelow);

        // ── Store rating line (LNC-14 a) ─────────────────────────────
        // One plain link, no question in front of it and no reward; collapsed unless the prompt says
        // it is due. Opens the listing in the Store app, or its web page where the Store cannot start.
        _storeRatingLink = AppTheme.CreateActionLink(FeedbackLinks.StoreRatingText, OnStoreRatingClicked, "Microsoft Store");
        _storeRatingLink.HorizontalAlignment = HorizontalAlignment.Center;
        _storeRatingLink.TextAlignment = TextAlignment.Center;
        _storeRatingLink.Margin = new Thickness(0, 0, 0, LineGapBelow);
        _storeRatingLink.Visibility = Visibility.Collapsed;

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
            Glyph = "",
            FontSize = 26,
            // White in both themes (TextPrimary is black in Light, which read as black on blue).
            Foreground = AppTheme.Brush(Colors.White),
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
            Margin = new Thickness(0, MicGapAbove, 0, MicGapBelow)
        };
        buttonContainer.Children.Add(glowCircle);
        buttonContainer.Children.Add(recordButton);

        // ── Status, directly under the mic ──────────────────────────
        // Wraps: StatusText carries provider error prose (a rejected image prompt, a network
        // failure), which measures far wider than the column. Centered + NoWrap clipped such a
        // message on BOTH sides, so it started and ended mid-word.
        _statusBlock = new TextBlock
        {
            Text = _viewModel.StatusText,
            FontSize = 15,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, StatusGapBelow)
        };

        // ── Hotkey line, under the status ───────────────────────────
        // One sentence, the hotkey in bold (owner: no key drawing, just make it stand out). One
        // TextBlock, so it wraps as text in a narrow window and Narrator reads it as it reads.
        var hotkeyLine = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, HotkeyGapBelow),
        };
        hotkeyLine.Inlines.Add(new Run { Text = HotkeyLead + " " });
        // The hotkey is a link to where it is changed (owner, 2026-10-14): Settings, scrolled to
        // Keyboard shortcuts. An inline Hyperlink — keyboard- and Narrator-reachable, the app's
        // idiom for a link inside a sentence — kept in the bold look the owner chose.
        var hotkeyLink = new Hyperlink
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            UnderlineStyle = UnderlineStyle.None,
        };
        hotkeyLink.Inlines.Add(new Run { Text = HotkeyKeyDisplay.Describe(recordingHotkey) });
        hotkeyLink.Click += (_, _) => App.MainWindowInstance?.NavigateToHotkeySettings();
        hotkeyLine.Inlines.Add(hotkeyLink);
        hotkeyLine.Inlines.Add(new Run { Text = " " + HotkeyTail });
        ToolTipService.SetToolTip(hotkeyLine, HotkeyLineTooltip);
        AutomationProperties.SetHelpText(hotkeyLine, HotkeyLineTooltip);

        // ── Last Transcription section ───────────────────────────────
        var sectionHeader = AppTheme.CreateSectionHeader("Last Transcription");
        sectionHeader.HorizontalAlignment = HorizontalAlignment.Center;
        sectionHeader.TextAlignment = TextAlignment.Center;

        _transcriptionBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            IsTextSelectionEnabled = true,
            // Room on both sides for the copy button at the right edge, so centred text stays centred
            // and never runs under it.
            Margin = new Thickness(28, 0, 28, 0),
        };
        // The copy button sits at the card's right edge, vertically centred (owner, 2026-10-14: copy
        // from Home as easily as from History) — History's own button, through the one shared helper.
        _copyButton = AppTheme.CreateCopyButton(
            "Copy text",
            HistoryImagePresentation.CopyFeedbackLabel(isImageItem: false),
            () => _viewModel.LastTranscription ?? "",
            clipboard.SetClipboardAsync);
        _copyButton.HorizontalAlignment = HorizontalAlignment.Right;
        _copyButton.VerticalAlignment = VerticalAlignment.Center;
        _copyButton.Margin = new Thickness(0, 0, -12, 0);
        ShowLastTranscription();

        var transcriptionCard = AppTheme.CreateCard(new Grid
        {
            Children = { _transcriptionBlock, _copyButton }
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
                else if (e.PropertyName == nameof(MainViewModel.LastTranscription))
                    ShowLastTranscription();
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
            RefreshStoreRatingLine();
            // Once per arrival: the flag keeps a missed Unloaded from subscribing twice.
            if (!_metricsAttached)
            {
                _metrics.MetricsChanged += OnMetricsChanged;
                _metricsAttached = true;
            }
            _ = RefreshTimeSavedAsync();
            // Refresh with current values in case they changed while on another page
            _statusBlock.Text = _viewModel.StatusText;
            ShowLastTranscription();
        };

        // ── Hero layout ──────────────────────────────────────────────
        var heroPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 560,
            Margin = new Thickness(0, 60, 0, 0),
            Children =
            {
                title,
                _timeSavedPill,
                _trialPanel,
                _downloadsCard.Element,
                _storeRatingLink,
                buttonContainer,
                _statusBlock,
                hotkeyLine,
                sectionHeader,
                transcriptionCard
            }
        };

        AppTheme.SetPageScrollContent(this, heroPanel);
    }

    private void ShowLastTranscription()
    {
        var text = _viewModel.LastTranscription;
        var hasText = !string.IsNullOrEmpty(text);
        _transcriptionBlock.Text = hasText ? text : NoTranscriptionText;
        _transcriptionBlock.Foreground = AppTheme.Brush(hasText ? AppTheme.TextSecondary : AppTheme.DimText);
        _copyButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

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

    /// <summary>
    /// LNC-14 (a): asked on every arrival. A displayed line that has not yet counted as seen starts the
    /// poll that records it once the window has been on screen with it (<see cref="StoreRatingPrompt"/>).
    /// Fail-soft like the trial line: a settings read that throws hides the line, never the page.
    /// </summary>
    private void RefreshStoreRatingLine()
    {
        bool show;
        try
        {
            show = _storeRating.ShouldDisplay();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Home Store rating line: settings read failed — hiding the line");
            show = false;
        }
        _storeRatingLink.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            StopStoreRatingSeenTimer();
            return;
        }

        _storeRating.BeginVisit(); // the on-screen time counts from this visit, never an earlier one
        _storeRatingSeenTimer ??= new DispatcherTimer();
        _storeRatingSeenTimer.Interval = StoreRatingOnScreenPoll;
        _storeRatingSeenTimer.Tick -= OnStoreRatingSeenTick;
        _storeRatingSeenTimer.Tick += OnStoreRatingSeenTick;
        _storeRatingSeenTimer.Start();
    }

    private void OnStoreRatingSeenTick(object? sender, object e)
    {
        // A poll left behind by a page that is no longer shown (WinUI 3 does not always deliver Unloaded)
        // must not count time the user spent elsewhere.
        if (!IsLoaded || _storeRatingSeenTimer is null)
        {
            StopStoreRatingSeenTimer();
            return;
        }

        StoreRatingObservation observation;
        try
        {
            observation = _storeRating.ObserveDisplayed(
                unobstructed: IsStoreRatingLineInViewport() && !IsCoveredByDialog());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Home Store rating line: recording it as seen failed");
            observation = StoreRatingObservation.Counted; // stop polling; it stays, uncounted, until the next visit
        }

        switch (observation)
        {
            case StoreRatingObservation.Counted:
                StopStoreRatingSeenTimer();
                break;
            case StoreRatingObservation.OnScreen:
                _storeRatingSeenTimer.Interval = StoreRatingOnScreenPoll;
                break;
            default:
                _storeRatingSeenTimer.Interval = StoreRatingHiddenPoll;
                break;
        }
    }

    /// <summary>
    /// A dialog over the window — the legal re-prompt or What's new at startup — hides Home behind it, so
    /// the line does not count as seen while one is open.
    /// </summary>
    private bool IsCoveredByDialog()
    {
        if (XamlRoot is not { } root) return true;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is ContentDialog) return true;
        }
        return false;
    }

    /// <summary>
    /// The line is inside the page's visible area — a long last transcription makes Home scroll, and a
    /// line scrolled out of view is not being seen (Codex diff round 1). The page content is the shared
    /// scroll wrapper (<see cref="AppTheme.SetPageScrollContent"/>).
    /// </summary>
    private bool IsStoreRatingLineInViewport()
    {
        if (Content is not ScrollViewer scroller
            || _storeRatingLink.Visibility != Visibility.Visible
            || _storeRatingLink.ActualWidth <= 0
            || _storeRatingLink.ActualHeight <= 0)
            return false;

        var bounds = _storeRatingLink.TransformToVisual(scroller)
            .TransformBounds(new global::Windows.Foundation.Rect(
                0, 0, _storeRatingLink.ActualWidth, _storeRatingLink.ActualHeight));
        return bounds.X >= 0 && bounds.Y >= 0
            && bounds.X + bounds.Width <= scroller.ViewportWidth
            && bounds.Y + bounds.Height <= scroller.ViewportHeight;
    }

    private void OnStoreRatingClicked()
    {
        // The click is proof enough that it was seen; it goes only once the Store (or its web page)
        // actually opened, so a click that opened nothing can be tried again.
        _storeRating.RecordSeen();
        if (!ShellLink.TryOpenStoreListing()) return;
        _storeRating.Dismiss();
        StopStoreRatingSeenTimer();
        _storeRatingLink.Visibility = Visibility.Collapsed;
    }

    private void StopStoreRatingSeenTimer()
    {
        if (_storeRatingSeenTimer == null) return;
        _storeRatingSeenTimer.Stop();
        _storeRatingSeenTimer.Tick -= OnStoreRatingSeenTick;
    }

    /// <summary>
    /// MetricsChanged is raised on the recording pipeline's thread, so this hops to the UI thread
    /// first. A page that has left the tree drops its own subscription here: WinUI 3 does not always
    /// deliver Unloaded, and the service is a singleton that would otherwise keep every Home page it
    /// ever served (Codex diff r1, PR #1225).
    /// </summary>
    private void OnMetricsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded)
        {
            _metrics.MetricsChanged -= OnMetricsChanged;
            _metricsAttached = false;
            return;
        }
        _ = RefreshTimeSavedAsync();
    });

    /// <summary>
    /// Reads the lifetime totals and shows the pill. Fail-soft like the trial line: a read that
    /// throws hides the pill, never the page. Refreshes overlap (an arrival and a dictation), so each
    /// stamps a version and renders only while it is still the newest — an older read finishing last
    /// must not put back a smaller number (Codex diff r1, PR #1225).
    /// </summary>
    private async Task RefreshTimeSavedAsync()
    {
        var version = Interlocked.Increment(ref _timeSavedRefreshVersion);
        string? value;
        try
        {
            var stats = await _metrics.GetAggregateStatsAsync();
            value = TimeSavedEstimate.HomeValue(TimeSavedEstimate.Minutes(stats.TotalWords, stats.Total));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Home time-saved pill: metrics read failed — hiding the pill");
            value = null;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != Volatile.Read(ref _timeSavedRefreshVersion)) return;
            _timeSavedValue.Text = value ?? "";
            _timeSavedPill.Visibility = value is not null ? Visibility.Visible : Visibility.Collapsed;
            if (value is not null)
                AutomationProperties.SetName(_timeSavedPill, $"{value} {TimeSavedEstimate.HomeLabel}. Open Metrics.");
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        if (_metricsAttached)
        {
            _metrics.MetricsChanged -= OnMetricsChanged;
            _metricsAttached = false;
        }
        // Stopped AND unsubscribed: a DispatcherTimer holds a strong reference to its handler.
        if (_trialTimer != null)
        {
            _trialTimer.Stop();
            _trialTimer.Tick -= OnTrialTick;
        }
        StopStoreRatingSeenTimer();
    }
}
