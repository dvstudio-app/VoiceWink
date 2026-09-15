using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Enums;
using VoiceWink.Services.Licensing;
using VoiceWink.Services.System;
using VoiceWink.Services.Transcription;
using VoiceWink.Services.Transcription.Clients;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Onboarding wizard — 13-step flow: welcome, legal, theme, language, transcription
/// choice, transcription config, AI enhancement, hotkeys, clipboard behavior,
/// startup, license, crash reporting, done. All UI built in code to bypass
/// PRI/XAML resource loading issues. Styled with AppTheme dark palette.
///
/// <para>The Legal step at index 1 (LGL-1) is the GDPR Art. 13 acceptance gate.
/// It runs before any preference write so the user is informed of controller
/// identity and processing purposes before any data — even local app preferences —
/// is collected.</para>
/// </summary>
public sealed class OnboardingPage : Page
{
    private static ILogger Logger => Log.ForContext<OnboardingPage>();

    private readonly SettingsService _settings;

    private readonly Services.Legal.LegalAcceptanceService _legal;
    private readonly ModelDownloadManager _downloader;
    private readonly ApiKeyManager _apiKeys;
    private int _step;
    private readonly bool _parakeetAvailable;
    private StackPanel _contentPanel = null!;

    /// <summary>
    /// When the download button last became Cancel — the arming window's origin (UI-3).
    ///
    /// <para>MONOTONIC, not wall clock. A <c>DateTime.UtcNow</c> difference goes NEGATIVE across a
    /// clock rollback (an NTP correction, a VM resume), which would leave Cancel unarmed until the
    /// clock caught up (Codex diff review r3). Subsecond interaction timing has no business reading
    /// the wall clock.</para>
    /// </summary>
    private long _downloadCancelShownTicks = -1;

    /// <summary>
    /// The in-flight model download's cancellation-provenance state, or <c>null</c> when none is
    /// running (UI-3). Decision logic lives in <see cref="Helpers.DownloadAttemptState"/> — see its
    /// remarks for why attempt-scoping matters. The token this attempt's work actually observes is
    /// paired with it here, since the state type stays WinUI-free and testable.
    /// </summary>
    private (Helpers.DownloadAttemptState State, CancellationTokenSource Cts)? _downloadAttempt;

    // Session state — preserved across Back/Forward navigation
    private string? _selectedHotkey;
    private string? _selectedPasteLastHotkey;
    private string? _selectedRedoLastHotkey;
    private string _selectedLanguage = "auto";
    private bool _useCloudTranscription;
    private List<Func<Task<bool>>>? _enhancementKeyValidators;

    // Step panel cache — preserves user selections when navigating Back
    private readonly Dictionary<int, UIElement> _stepCache = new();

    // 14 since UPD-4b (2026-08-08) added the Updates step at index 12, immediately after Crash
    // Reporting — the two network-egress consent screens sit together, and inserting there (rather
    // than beside Startup Behavior) left the License step's cache exclusion and every existing
    // ShowStep target untouched.
    private const int TotalSteps = 14;

    // The "Done" step is always the last one by index. Derive from TotalSteps so
    // the caching logic automatically adapts when a step is added or removed.
    private const int DoneStepIndex = TotalSteps - 1;

    /// <summary>Raised when the user completes the onboarding wizard.</summary>
    public event Action? OnboardingCompleted;

    public OnboardingPage()
    {
        _settings = App.Services.GetRequiredService<SettingsService>();
        _legal = App.Services.GetRequiredService<Services.Legal.LegalAcceptanceService>();
        _skipLegal = new OnboardingStepFlow.FrozenDecision(
            () => OnboardingStepFlow.ShouldSkipLegal(_legal.BundleHealthy, _legal.RequiresPromptAtStartup()),
            ex => Logger.Warning("Could not evaluate the legal-step skip — showing the step: {ErrorType}",
                                 ex.GetType().Name));
        _downloader = App.Services.GetRequiredService<ModelDownloadManager>();
        _apiKeys = App.Services.GetRequiredService<ApiKeyManager>();
        // Read ONCE, here, with the page's other dependencies: a CPU/build-flag probe, not I/O.
        // Fail-soft — anything that stops us answering means "no", which lands on the Whisper
        // backstop rather than throwing during a flow that has no user to report to yet.
        try
        {
            // ParakeetTranscriptionService, NOT ParakeetLocalRuntime: the runtime is registered
            // ONLY as ILocalTranscriptionRuntime (App.xaml.cs), so resolving the concrete type
            // throws — and the fail-soft catch below would then have pinned the recommendation to
            // Whisper forever, with a green suite and nothing to see (Kimi, plan review). The
            // service IS concretely registered.
            //
            // A page-ctor App.Services read is this page's existing seam (SettingsService,
            // ModelDownloadManager and ApiKeyManager arrive the same way three lines up) rather
            // than a new locator scattered through the file; and the static shortcut is not
            // available, because ParakeetFeature.IsEnabled is composition-root-only by its own
            // documented rule.
            _parakeetAvailable = App.Services
                .GetRequiredService<Services.Transcription.ParakeetTranscriptionService>().IsAvailable;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not determine Parakeet availability; recommending the Whisper default");
            _parakeetAvailable = false;
        }
        // Initialize from settings so re-running the wizard reflects current config.
        _selectedLanguage = _settings.GetString(AppDefaults.SelectedLanguage, "auto");
        _useCloudTranscription = InitialUseCloud(_settings.GetString(AppDefaults.SelectedModelName, ""));
        _step = 0;
        BuildUI();
        Unloaded += (_, _) => AbandonCurrentDownload();
    }

    /// <summary>
    /// Mark the in-flight download (if any) as abandoned and cancel it. The ONE place all three
    /// non-user cancellers — Unload, Back, step reconstruction — route through, so none of them can
    /// forget to set <c>Abandoned</c> the way the page-level flag was forgotten.
    /// </summary>
    private void AbandonCurrentDownload()
    {
        if (_downloadAttempt is not { } attempt) return;
        var (state, cts) = attempt;
        state.Abandoned = true;
        cts.Cancel();
    }

    private void BuildUI()
    {
        _contentPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 500,
            Spacing = 16,
            Padding = new Thickness(0, 20, 0, 20)
        };

        ShowStep(0);

        var scrollViewer = new ScrollViewer
        {
            Content = _contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        Content = new Grid
        {
            Background = AppTheme.Brush(AppTheme.ContentBg),
            Padding = new Thickness(40, 20, 40, 20),
            Children = { scrollViewer }
        };
    }

    private void ShowStep(int step)
    {
        // ONB-2: the legal step disappears from NAVIGATION once acceptance is recorded and the
        // bundle is healthy. Resolved here rather than at each caller so every entry point — the
        // Welcome button, Back from Theme, a re-show — goes through one rule.
        var skipLegal = SkipLegalStep;
        var resolved = OnboardingStepFlow.Resolve(step, _step, skipLegal);
        if (resolved != step)
        {
            ShowStep(resolved);
            return;
        }

        _step = step;
        _contentPanel.Children.Clear();

        int displayStep = OnboardingStepFlow.DisplayNumber(step, skipLegal);
        _contentPanel.Children.Add(new TextBlock
        {
            Text = $"Step {displayStep} of {OnboardingStepFlow.EffectiveTotal(TotalSteps, skipLegal)}",
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Welcome (0), Legal (1), License (10), and Done (DoneStepIndex) are not
        // cached.
        //
        // Legal (step 1, LGL-1) is excluded so the checkbox + button-enabled
        // state are never desynced from the persisted hashes — a Back-then-Forward
        // re-evaluates RequiresAcceptance against fresh service state.
        //
        // License (step 10) is excluded because its presentation depends on live
        // LicenseService state — after the user activates/starts-trial and clicks
        // Back, the cached Unlicensed panel would hide the now-Activated skip
        // screen. Rebuilding fresh is cheap.
        bool shouldCache = step != 0 && step != DoneStepIndex && step != 1 && step != 10;

        // Return cached panel if already built (preserves user selections on Back)
        if (shouldCache && _stepCache.TryGetValue(step, out var cached))
        {
            _contentPanel.Children.Add(cached);
            return;
        }

        // Build fresh — the Build* methods add to _contentPanel and we cache the result
        int childCountBefore = _contentPanel.Children.Count;
        switch (step)
        {
            case 0: BuildWelcomeStep(); break;
            case 1: BuildLegalStep(); break;
            case 2: BuildThemeStep(); break;
            case 3: BuildLanguageStep(); break;
            case 4: BuildTranscriptionChoiceStep(); break;
            case 5: BuildTranscriptionConfigStep(); break;
            case 6: BuildAiEnhancementStep(); break;
            case 7: BuildHotkeyStep(); break;
            case 8: BuildClipboardStep(); break;
            case 9: BuildStartupStep(); break;
            case 10: BuildLicenseStep(); break;
            case 11: BuildCrashReportingStep(); break;
            case 12: BuildUpdatesStep(); break;
            case 13: BuildDoneStep(); break;
        }

        // Wrap newly added children in a container for caching
        if (shouldCache && _contentPanel.Children.Count > childCountBefore)
        {
            var wrapper = new StackPanel { Spacing = 8 };
            while (_contentPanel.Children.Count > childCountBefore)
            {
                var child = _contentPanel.Children[childCountBefore];
                _contentPanel.Children.RemoveAt(childCountBefore);
                wrapper.Children.Add(child);
            }
            _stepCache[step] = wrapper;
            _contentPanel.Children.Add(wrapper);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Step 0: Welcome
    // ────────────────────────────────────────────────────────────────

    private void BuildWelcomeStep()
    {
        AddStepHeader("Welcome to VoiceWink",
            "Voice-to-text for Windows. Record, transcribe, and paste at your cursor.",
            titleFontSize: 28, subtitleFontSize: 16);

        var startBtn = AppTheme.CreateAccentButton("Get Started");
        startBtn.Tapped += (_, _) => ShowStep(1);
        AddButtonRow(startBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 1: Legal (LGL-1) — GDPR Art. 13 acceptance gate
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the legal step is omitted from navigation, decided ONCE per wizard run (ONB-2).
    ///
    /// <para><b>Both conditions are load-bearing.</b> Acceptance recorded is the owner's request;
    /// bundle HEALTHY is the safety half — <see cref="BuildLegalStep"/>'s fail-closed branch
    /// (reinstall-or-exit) is the only surface that reports a broken EULA/Privacy bundle, so a build
    /// that cannot load its legal texts must still stop here rather than sail past.</para>
    ///
    /// <para><b>Frozen at first use, and that is a fix rather than an optimisation.</b> Re-evaluating
    /// per <c>ShowStep</c> made the COUNTER move under a new user: "Step 1 of 13", legal at
    /// "Step 2 of 13", then — the moment they accept — Theme as "Step 2 of 12". The total silently
    /// shrank mid-wizard (Codex diff review). A run's step count has to be a constant of that run.</para>
    ///
    /// <para><b>The trade, recorded because the two reviewers disagreed about it.</b> Kimi argued for
    /// per-call evaluation on the grounds that freezing resurrects the "Already accepted…" screen —
    /// the very screen this card exists to remove — for a fresh user who accepts and then navigates
    /// Back within the SAME run. That is true, and it is the accepted cost: a counter that changes
    /// under the user is a defect on every fresh install, while the hint screen is a narrow
    /// same-run-Back case that costs one extra click. The skip takes effect from the NEXT run, which
    /// is the case the card was actually about (existing installs re-running the wizard).</para>
    /// </summary>
    private bool SkipLegalStep => _skipLegal.Value;

    /// <summary>
    /// Decided at most once per page (= per wizard run), fail-closed on any error. Both the freeze
    /// and the predicate live in <see cref="OnboardingStepFlow"/> so they are testable — neither was,
    /// when they lived here as a nullable field plus a try/catch.
    /// </summary>
    private readonly OnboardingStepFlow.FrozenDecision _skipLegal;

    private void BuildLegalStep()
    {
        AddStepHeader("Terms and Privacy",
            "Please review and accept the End User Licence Agreement and Privacy Policy to continue.");

        var legal = _legal;
        if (!legal.BundleHealthy)
        {
            // Fail-closed: cannot satisfy LGL-1 done-when criteria without a working bundle.
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "We couldn't load the EULA and Privacy Policy from this build. " +
                       "Please reinstall VoiceWink — your settings, history, and licence are preserved.",
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            var exitBtn = AppTheme.CreateDangerButton("Exit");
            exitBtn.Tapped += (_, _) => Environment.Exit(0);
            AddButtonRow(CreateBackButton(), exitBtn);
            return;
        }

        // Skip-screen for users who already accepted the current hashes.
        //
        // ONB-2 narrowed what reaches here, and the remaining case is worth naming (Kimi diff
        // review). The existing-install re-run no longer arrives — navigation resolves that step
        // away before this builder runs. What DOES still arrive is a fresh user who accepted during
        // THIS run and then pressed Back: the skip decision is frozen per run, so the step is still
        // in their sequence. That is the accepted cost of a stable counter, not a leftover.
        if (!legal.RequiresPromptAtStartup())
        {
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "Already accepted on this device. You can skip to the next step.",
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            });
            var skipBtn = AppTheme.CreateAccentButton("Continue");
            skipBtn.Tapped += (_, _) => ShowStep(2);
            AddButtonRow(CreateBackButton(), skipBtn);
            return;
        }

        // The terms render inline and scroll within the page's single outer ScrollViewer. The previous
        // design nested an inner height-capped ScrollViewer here, but its fixed chrome reservation
        // under-counted the header card + agree-checkbox + button row on smaller / HiDPI windows,
        // pushing the footer below the fold (UAT clip) and producing a double scrollbar. One scroll
        // region = no double-scroll, and the checkbox + buttons can never be clipped off the bottom.
        var termsPanel = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                AppTheme.CreateSectionHeader($"EULA · v{legal.Eula!.Version}"),
                LegalMarkdownRenderer.Render(legal.Eula!.Body),
                AppTheme.CreateSectionHeader($"Privacy Policy · v{legal.Privacy!.Version}"),
                LegalMarkdownRenderer.Render(legal.Privacy!.Body),
            },
        };
        _contentPanel.Children.Add(termsPanel);

        var checkbox = new CheckBox
        {
            Content = "I have read and agree to the EULA and Privacy Policy.",
            // WinUI's default CheckBox.VerticalContentAlignment is Top with a 32px MinHeight, which
            // floats the single-line label up off the box's vertical center. Center the content and
            // drop the min-height slack so the box and text line up.
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _contentPanel.Children.Add(checkbox);

        var errorText = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _contentPanel.Children.Add(errorText);

        var continueBtn = AppTheme.CreateAccentButton("Accept and continue");
        AppTheme.SetButtonEnabled(continueBtn, false, "Accept and continue");
        checkbox.Checked += (_, _) => AppTheme.SetButtonEnabled(continueBtn, true, "Accept and continue");
        checkbox.Unchecked += (_, _) => AppTheme.SetButtonEnabled(continueBtn, false, "Accept and continue");

        continueBtn.Tapped += (_, _) =>
        {
            try
            {
                legal.Accept();
                ShowStep(2);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Persisting legal acceptance failed in onboarding");
                errorText.Text = "We couldn't save your acceptance. Please try again, or restart the app.";
                errorText.Visibility = Visibility.Visible;
                // Continue button stays enabled so the user can retry.
            }
        };

        var declineBtn = AppTheme.CreateSecondaryButton("Decline and exit");
        declineBtn.Tapped += (_, _) => Environment.Exit(0);

        AddButtonRow(CreateBackButton(), continueBtn, declineBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 2: Theme
    // ────────────────────────────────────────────────────────────────

    private void BuildThemeStep()
    {
        AddStepHeader("Choose Your Theme",
            "Pick a look that suits you. You can change this later in Settings.");

        var currentTheme = _settings.GetString(AppDefaults.ThemeMode, "System");
        var themes = new[] { "System", "Dark", "Light" };
        var labels = new[] { "System Default", "Dark", "Light" };
        var icons = new[] { "\uE770", "\uE708", "\uE706" }; // Monitor, Moon, Sun
        var descriptions = new[] { "Follows your Windows setting", "Easy on the eyes", "Classic bright look" };

        for (int i = 0; i < themes.Length; i++)
        {
            var theme = themes[i];
            var isSelected = theme == currentTheme;

            var iconBlock = new FontIcon
            {
                Glyph = icons[i],
                FontSize = 20,
                Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var textPanel = new StackPanel { Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = labels[i],
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary)
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = descriptions[i],
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.SubtleText)
            });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { iconBlock, textPanel }
            };

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 14, 20, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = row
            };
            UpdateOptionStyle(border, isSelected);

            border.Tapped += (_, _) =>
            {
                _settings.SetString(AppDefaults.ThemeMode, theme);
                if (Enum.TryParse<ThemeMode>(theme, out var mode))
                    AppTheme.SetTheme(mode);
                // Update page-level theme and background
                RequestedTheme = AppTheme.ElementTheme;
                if (Content is Grid outerGrid)
                    outerGrid.Background = AppTheme.Brush(AppTheme.ContentBg);
                // Clear ALL cached steps so every page picks up new theme colors
                _stepCache.Clear();
                ShowStep(_step);
            };

            _contentPanel.Children.Add(border);
        }

        var themeNextBtn = AppTheme.CreateAccentButton("Continue");
        themeNextBtn.Tapped += (_, _) => ShowStep(3);
        AddButtonRow(CreateBackButton(), themeNextBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 3: Language
    // ────────────────────────────────────────────────────────────────

    private void BuildLanguageStep()
    {
        AddStepHeader("Choose Your Language",
            "Select the language you'll speak most often. You can change this later in Settings.");

        // Build sorted language list: Auto-detect first, then alphabetical by display name
        var languages = ModelManagementViewModel.SupportedLanguages;
        var sortedLangs = languages
            .Where(l => l != "auto")
            .Select(l => (Code: l, Display: ModelManagementViewModel.GetLanguageDisplayName(l)))
            .OrderBy(l => l.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sortedLangs.Insert(0, (Code: "auto", Display: "Auto-detect"));

        var languageCombo = new ComboBox
        {
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AppTheme.AllowParentScroll(languageCombo);

        // Find current selection — CASE-INSENSITIVELY, and canonicalize the state to match.
        //
        // A persisted value comes back however an import or a hand edit left it, and this compared
        // with `==`: an imported "JA" found no row, so the combo showed "Auto-detect" while
        // `_selectedLanguage` stayed "JA" — the screen said one thing and Continue persisted
        // another, with the download step then recommending a model and discussing Japanese
        // (Codex diff review r3). Every other language comparison in the app is already
        // case-insensitive, so this was the odd one out rather than a deliberate strictness.
        //
        // An UNLISTED code is kept AND shown, via a synthetic row carrying the code itself. Two
        // earlier versions were wrong in opposite directions: leaving the value alone while showing
        // "Auto-detect" made the screen lie (r8), and coercing it to "auto" ERASED a valid
        // preference — the picker's 99 are not the limit of what whisper.cpp accepts, which is why
        // SupportedCodesFor refuses to derive a negative claim from that list, and "yue" is the
        // repo's own worked example (r9). Showing it hides nothing and loses nothing.
        var resolved = ResolvePersistedLanguage(sortedLangs, _selectedLanguage);
        _selectedLanguage = resolved.Code;

        // ONE object owns what the combo SHOWS and what a selection MEANS — `sortedLangs` is never
        // read again below. See LanguageComboRows for why that is structural rather than tidiness.
        var comboRows = new LanguageComboRows(sortedLangs, resolved);

        foreach (var display in comboRows.Displays)
            languageCombo.Items.Add(display);

        // Selected and resolved BY INDEX, never by display string — two rows can render the same
        // text, and `SelectedItem = <display>` would land on whichever comes first (Codex r12).
        languageCombo.SelectedIndex = comboRows.ResolvedIndex;

        languageCombo.SelectionChanged += (_, _) =>
        {
            if (comboRows.CodeAt(languageCombo.SelectedIndex) is { } code)
                _selectedLanguage = code;
        };

        _contentPanel.Children.Add(languageCombo);

        // Continue button — saves language setting and advances
        var langContinueBtn = AppTheme.CreateAccentButton("Continue");
        langContinueBtn.Tapped += (_, _) =>
        {
            _settings.SetString(AppDefaults.SelectedLanguage, _selectedLanguage);
            // Sync the ViewModel singleton so the Models page shows the correct language
            App.Services.GetRequiredService<ModelManagementViewModel>().SelectedLanguage = _selectedLanguage;
            // Invalidate transcription config step — model selection depends on language
            _stepCache.Remove(5);
            // ONB-1: step 4 is cached, and its local card now prints a LANGUAGE-DERIVED size
            // (LocalOptionDescription). Without this, English → Continue → Back → Japanese →
            // Continue would serve the cached panel still showing Parakeet's 670 MB while step 5
            // recommends Whisper Small at 264 MB. Rebuilding also re-derives the card highlight
            // from _useCloudTranscription; the two Tapped handlers keep that field current, so
            // there is no desync today — this comment is here so an edit to either handler cannot
            // break that silently.
            //
            // Both numbers are ShowStep's switch ordinals (4 = transcription choice, 5 = its
            // config step). Inserting a step before either without fixing these invalidates the
            // wrong panel — it fails loudly (the wrong screen renders) rather than silently, which
            // is why the pre-existing Remove(5) got away with a bare literal.
            _stepCache.Remove(4);
            ShowStep(4);
        };
        AddButtonRow(CreateBackButton(), langContinueBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 4: Transcription Choice (Cloud vs Local)
    // ────────────────────────────────────────────────────────────────

    private void BuildTranscriptionChoiceStep()
    {
        AddStepHeader("Choose Transcription Method",
            "Transcribe using a local model or a cloud API. You can change this later.");

        // ── Cloud option card ──
        var cloudBorder = CreateSelectableOption(
            "\uE753",
            // NEITHER card says "Recommended" (ONB-1): the ask was a default, not a ranking, and
            // the pre-selected highlight is what carries the default.
            "Cloud Transcription",
            "Fast, accurate, no download. Requires a free API key.",
            _useCloudTranscription);

        // ── Local option card ──
        var localBorder = CreateSelectableOption(
            "\uE7F8",
            "Local Transcription",
            // Size resolves from the same catalogue row step 5 prints, so the mode-choice card and
            // the download screen cannot disagree. This was a hardcoded "~260 MB" defended as
            // deliberately model-agnostic — defensible while the recommendation was Whisper Small
            // (264 MB), a 2.5x understatement once Parakeet (670 MB) became it.
            LocalOptionDescription(_parakeetAvailable, _selectedLanguage),
            !_useCloudTranscription);

        cloudBorder.Tapped += (_, _) =>
        {
            _useCloudTranscription = true;
            _stepCache.Remove(5); // Rebuild config step for cloud
            UpdateOptionStyle(cloudBorder, true);
            UpdateOptionChildColors(cloudBorder, true);
            UpdateOptionStyle(localBorder, false);
            UpdateOptionChildColors(localBorder, false);
        };

        localBorder.Tapped += (_, _) =>
        {
            _useCloudTranscription = false;
            _stepCache.Remove(5); // Rebuild config step for local
            UpdateOptionStyle(cloudBorder, false);
            UpdateOptionChildColors(cloudBorder, false);
            UpdateOptionStyle(localBorder, true);
            UpdateOptionChildColors(localBorder, true);
        };

        // LOCAL FIRST (ONB-1, owner 2026-08-05: "with Parakeet the game has changed"). The order
        // lives in ChoiceCardOrder as DATA so a test can pin it — reordering that array is the only
        // way to change what the user sees here, which is what makes the card's headline behaviour
        // an enforced fact rather than a line anyone can swap back (Codex diff review asked for a
        // seam; the alternative, constructing this Page in a test, is not possible).
        //
        // The construction order above is immaterial to what renders; what the handlers require is
        // only that both Borders exist before either is attached.
        foreach (var card in ChoiceCardOrder)
        {
            _contentPanel.Children.Add(card == TranscriptionChoiceCard.Local ? localBorder : cloudBorder);
        }

        var choiceNextBtn = AppTheme.CreateAccentButton("Continue");
        choiceNextBtn.Tapped += (_, _) => ShowStep(5);
        AddButtonRow(CreateBackButton(), choiceNextBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 5: Transcription Configuration (Cloud setup or Local download)
    // ────────────────────────────────────────────────────────────────

    private void BuildTranscriptionConfigStep()
    {
        // Cancel any orphaned download from a previous visit
        AbandonCurrentDownload();

        if (_useCloudTranscription)
        {
            AddStepHeader("Set Up Cloud Transcription",
                "Choose a provider and enter your API key.\nGroq offers a generous free tier.");

            var cloudPanel = new StackPanel { Spacing = 10 };
            var selectBtn = BuildCloudDetailPanel(cloudPanel);
            _contentPanel.Children.Add(cloudPanel);
            AddButtonRow(CreateBackButton(), selectBtn);
        }
        else
        {
            AddStepHeader("Download Local Model");

            var localPanel = new StackPanel { Spacing = 10 };
            BuildLocalDetailPanel(localPanel);

            // The language the user picked at step 3 may be one this model cannot recognise.
            //
            // The note lives HERE and not beside the language picker deliberately: step 3 runs
            // BEFORE the cloud-vs-local choice, so at that point there is no model to speak about —
            // warning there would mean warning on the strength of a model the user has not chosen,
            // and it would fire for someone about to pick a cloud provider that handles their
            // language perfectly well. This branch is the first place the model is determinate AND
            // the audience has committed to local.
            //
            // It reads the PERSISTED selection, falling back to the recommendation. Using the
            // recommendation unconditionally made the note dead code AND wrong: the wizard can be
            // relaunched over an existing configuration, and "Skip download for now" below
            // deliberately preserves the current selection — so a user who already has Parakeet
            // selected with Chinese pinned, the exact case this exists for, would have seen
            // nothing (both diff reviewers).
            //
            // WHICH model the note describes depends on which branch the panel above rendered,
            // because the branches end the user on different models:
            //
            //   recommendation already downloaded -> Continue, persisting the RECOMMENDATION
            //   recommendation absent, others present -> "Skip download", keeping the PERSISTED one
            //   nothing present -> download the RECOMMENDATION
            //
            // Two wrong answers were shipped in review before this one. Reading settings named
            // Whisper Small on a fresh install (GetString returns the DEFAULT, not empty) while the
            // panel downloaded Parakeet; then reading the recommendation unconditionally left the
            // skip path silent — Parakeet selected, Chinese pinned, Small absent, recommendation
            // therefore Small, no warning, and Skip preserves the Parakeet the user cannot use.
            // Both found by Codex. The note has to follow the branch, so it asks the same resolver
            // the skip path relies on.
            var noteModel = _downloader.GetDownloadedModels()
                .Contains(RecommendedLocalModel.Name, ModelDiskReconciliation.NameComparer)
                    ? RecommendedLocalModel
                    : ResolveOnboardingLocalModel(
                        _settings.GetString(AppDefaults.SelectedModelName, ""),
                        _parakeetAvailable, _selectedLanguage);

            // ONB-5: the note JUDGES noteModel (the persisted selection, per the resolution above)
            // but may NAME the recommendation, so the amber line finally connects to the panel it
            // sits under instead of reading as a disconnected fact about an engine this screen is
            // actively replacing. ComposeLanguageNote drops the clause itself when the
            // recommendation is the same model or cannot recognise the language either.
            var languageNote = ModelLanguageSupport.ComposeLanguageNote(
                noteModel, _selectedLanguage, RecommendedLocalModel);
            if (languageNote is not null)
            {
                localPanel.Children.Add(new TextBlock
                {
                    Text = languageNote,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AppTheme.Brush(AppTheme.WarningText),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                });
            }

            _contentPanel.Children.Add(localPanel);

            var downloaded = _downloader.GetDownloadedModels();
            // Same descriptor the panel above recommends and offers to download — this branch
            // decides whether that exact model is already present, so resolving it independently
            // is how the two answers drift (Codex diff review: changing only the download site
            // would send an already-downloaded q8 recommendation down the wrong branch).
            var modelName = RecommendedLocalModel.Name;
            if (downloaded.Contains(modelName, ModelDiskReconciliation.NameComparer))
            {
                var continueBtn = AppTheme.CreateAccentButton("Continue with Local Model");
                continueBtn.Tapped += (_, _) => ShowStep(6);
                AddButtonRow(CreateBackButton(), continueBtn);
            }
            else if (InstalledCatalogModels.Known(downloaded).Count > 0
                     && PersistedLocalSelectionIsServiceable(
                            _settings.GetString(AppDefaults.SelectedModelName, ""),
                            _parakeetAvailable))
            {
                // The second condition is ONB-1's regression fix (Codex diff review round 2): Skip
                // only advances the wizard, so offering it while the persisted selection is
                // unserviceable would let onboarding COMPLETE on a dead name. Without it, the user
                // falls through to the Back-only row and must either Download the recommendation or
                // go back and pick cloud — both of which repair the selection.
                // Gated on CATALOG-KNOWN files, not on "any .bin present": an unknown file cannot be
                // served by LocalModelPreparer, so offering to use it is offering a dead end.
                //
                // The label is "Skip download for now", not "use existing model", because the button
                // does NOT select one: it only advances the wizard, so SelectedModelName keeps the
                // default — which in this branch is the one model that is NOT downloaded —
                // and the first recording starts a download of it. The old label promised otherwise.
                // An attempt to fix it here picked the first catalog entry, and catalog order is
                // PRESENTATION order — best-first since 2026-08-04, smallest-first before that — so
                // that pick tracks whatever the page happens to render at the top rather than what
                // suits this machine: today a user holding Tiny plus a larger model would be handed
                // the larger one, and before the reorder the same code handed them Tiny and could
                // downgrade an existing Large selection on a re-run. (The original example was
                // ggml-tiny.en, which was also English-only; that row is gone since 2026-08-03, but
                // the ordering hazard is not.)
                // Choosing correctly needs a real policy (prefer the current selection, then
                // language compatibility) behind a stale-file-aware seam. That is its own change.
                var skipBtn = AppTheme.CreateSecondaryButton("Skip download for now");
                skipBtn.Tapped += (_, _) => ShowStep(6);
                AddButtonRow(CreateBackButton(), skipBtn);
            }
            else
            {
                AddButtonRow(CreateBackButton());
            }
        }
    }

    private Border BuildCloudDetailPanel(StackPanel panel)
    {
        var providers = new[] { ModelProvider.Groq, ModelProvider.OpenAI, ModelProvider.Deepgram, ModelProvider.ElevenLabs };
        var providerLabels = new[] { "Groq", "OpenAI", "Deepgram", "ElevenLabs" };

        var signupUrls = new Dictionary<ModelProvider, string>
        {
            [ModelProvider.Groq] = "https://console.groq.com/keys",
            [ModelProvider.OpenAI] = "https://platform.openai.com/api-keys",
            [ModelProvider.Deepgram] = "https://console.deepgram.com",
            [ModelProvider.ElevenLabs] = "https://elevenlabs.io/app/settings/api-keys",
        };

        // ── All controls inside a single card ──
        var cardPanel = new StackPanel { Spacing = 12 };

        // Provider row
        var providerCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AppTheme.AllowParentScroll(providerCombo);
        foreach (var label in providerLabels) providerCombo.Items.Add(label);
        providerCombo.SelectedIndex = 0;
        cardPanel.Children.Add(CreateLabeledRow("Provider", providerCombo));

        // Signup link
        var signupLink = new HyperlinkButton
        {
            Content = "Get a free Groq API key \u2192",
            NavigateUri = new Uri(signupUrls[ModelProvider.Groq]),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0)
        };
        cardPanel.Children.Add(signupLink);

        // API key row
        var apiKeyBox = new PasswordBox { PlaceholderText = "Paste your API key", HorizontalAlignment = HorizontalAlignment.Stretch };
        var (keyStatusPanel, SetKeyStatus) = CreateKeyStatusPanel();
        var saveKeyBtn = AppTheme.CreateCompactButton("Save", isAccent: true);

        var apiKeyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { apiKeyBox, saveKeyBtn }
        };
        // Make the password box stretch
        apiKeyBox.Width = 320;

        cardPanel.Children.Add(CreateLabeledRow("API Key", apiKeyRow));
        cardPanel.Children.Add(keyStatusPanel);

        // Model row
        var modelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AppTheme.AllowParentScroll(modelCombo);
        cardPanel.Children.Add(CreateLabeledRow("Model", modelCombo));

        panel.Children.Add(AppTheme.CreateCard(cardPanel));

        // ── Logic ──

        void UpdateSignupLink(ModelProvider provider)
        {
            if (signupUrls.TryGetValue(provider, out var url))
            {
                signupLink.Content = $"Get a {provider} API key \u2192";
                signupLink.NavigateUri = new Uri(url);
                signupLink.Visibility = Visibility.Visible;
            }
            else
            {
                signupLink.Visibility = Visibility.Collapsed;
            }
        }

        void PopulateModels(ModelProvider provider)
        {
            modelCombo.Items.Clear();
            var models = CloudModels.GetModelsForProvider(provider);
            foreach (var m in models) modelCombo.Items.Add(m.DisplayName);
            if (models.Length > 0) modelCombo.SelectedIndex = 0;

            var providerKey = provider.ToString().ToLowerInvariant();
            var existingKey = _apiKeys.GetApiKey(providerKey);
            if (!string.IsNullOrEmpty(existingKey))
            {
                apiKeyBox.Password = existingKey;
                SetKeyStatus("\uE73E", "Key saved", AppTheme.AccentGreen, true);
            }
            else
            {
                apiKeyBox.Password = "";
                SetKeyStatus("", "", AppTheme.TextSecondary, false);
            }
        }

        // Shared save-and-validate logic for both Save button and Continue button.
        // Returns true if the key is valid/saved (OK to proceed), false if invalid.
        async Task<bool> SaveAndValidateKeyAsync()
        {
            // ENH-17 (Codex diff review): the catch-all below used to report a green "Key saved"
            // and return true for ANY throw, including one before anything was persisted — a
            // fail-open gate on the first-run path, where the user has no second surface to notice.
            // Declared outside the try so the catch can actually see it.
            var committed = false;
            try
            {
                var provider = providers[providerCombo.SelectedIndex];
                var providerKey = provider.ToString().ToLowerInvariant();
                var key = apiKeyBox.Password;
                if (string.IsNullOrWhiteSpace(key))
                {
                    // ENH-17: a clear is a write, so it claims the slot like any other.
                    var clearGeneration = _apiKeys.BeginKeyWrite(providerKey);
                    if (!_apiKeys.TryCommitApiKey(providerKey, "", clearGeneration, out _))
                    {
                        Logger.Information("Onboarding transcription key clear for {Provider} superseded", providerKey);
                        return true;
                    }
                    SetKeyStatus("\uE946", "Key cleared", AppTheme.TextSecondary, true);
                    return true;
                }
                var previousKey = _apiKeys.GetApiKey(providerKey) ?? "";
                if (key == previousKey)
                    return true; // already saved, no validation needed

                // ENH-17: claimed AFTER the idempotence check — a path that writes nothing must not
                // burn a generation and supersede a real in-flight save. apikey_{provider} is ONE
                // slot shared with the Enhancement page, which is why this surface claims at all.
                var transcriptionGeneration = _apiKeys.BeginKeyWrite(providerKey);

                SetKeyStatus("\uE946", "Validating...", AppTheme.TextSecondary, true);

                bool isValid = false;
                bool couldNotValidate = false;
                try
                {
                    var http = App.Services.GetRequiredService<IHttpClientFactory>().CreateClient("transcription");
                    ITranscriptionService? client = providerKey switch
                    {
                        "groq" => new GroqClient(http, key),
                        "deepgram" => new DeepgramClient(http, key),
                        "elevenlabs" => new ElevenLabsClient(http, key),
                        "openai" => new OpenAITranscriptionClient(http, key),
                        _ => null
                    };
                    isValid = client != null ? await client.ValidateKeyAsync() : true;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                           or global::System.Net.HttpStatusCode.Forbidden)
                {
                    isValid = false;
                }
                catch (Exception) { couldNotValidate = true; }

                if (isValid)
                {
                    // A DPAPI save failure must not report success (F28/F44) —
                    // the key would silently evaporate at restart.
                    // ENH-15: InvalidFormat and EncryptionFailed are both refusals to persist,
                    // but only one of them is the device's fault \u2014 say which.
                    if (!_apiKeys.TryCommitApiKey(providerKey, key, transcriptionGeneration, out var saveResult))
                    {
                        Logger.Information("Onboarding transcription key save for {Provider} superseded", providerKey);
                        return false;
                    }
                    if (!saveResult.IsSuccess())
                    {
                        SetKeyStatus("\uE783", saveResult == Services.System.ApiKeySaveResult.InvalidFormat
                            ? Helpers.ApiKeyFormat.DescribeCandidate(key)
                            : "Could not save the key on this device", AppTheme.AccentRed, true);
                        return false;
                    }
                    committed = true;
                    SetKeyStatus("\uE73E", "Key valid", AppTheme.AccentGreen, true);
                    return true;
                }
                if (couldNotValidate)
                {
                    // ENH-15: InvalidFormat and EncryptionFailed are both refusals to persist,
                    // but only one of them is the device's fault \u2014 say which.
                    if (!_apiKeys.TryCommitApiKey(providerKey, key, transcriptionGeneration, out var saveResult))
                    {
                        Logger.Information("Onboarding transcription key save for {Provider} superseded", providerKey);
                        return false;
                    }
                    if (!saveResult.IsSuccess())
                    {
                        SetKeyStatus("\uE783", saveResult == Services.System.ApiKeySaveResult.InvalidFormat
                            ? Helpers.ApiKeyFormat.DescribeCandidate(key)
                            : "Could not save the key on this device", AppTheme.AccentRed, true);
                        return false;
                    }
                    committed = true;
                    SetKeyStatus("\uE7BA", "Could not validate — key saved", AppTheme.WarningText, true);
                    return true;
                }

                if (!_apiKeys.IsCurrentKeyWrite(providerKey, transcriptionGeneration))
                {
                    // ENH-17 (Codex round 3): a stale REJECTED validation must not repaint a row
                    // a newer save already owns. NOT cosmetic like the "Validating..." label:
                    // restoring the previous password can empty the box, and the next Continue
                    // press then takes the CLEAR branch and DELETES the newer save's good key.
                    Logger.Information("Onboarding transcription key rejection for {Provider} superseded", providerKey);
                    return false;
                }
                apiKeyBox.Password = previousKey;
                if (!string.IsNullOrEmpty(previousKey))
                    SetKeyStatus("\uE73E", "Invalid key rejected — previous key restored", AppTheme.AccentGreen, true);
                else
                    SetKeyStatus("\uE783", "Invalid API key", AppTheme.AccentRed, true);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Onboarding key validation failed");
                if (committed)
                {
                    SetKeyStatus("\uE7BA", "Key saved — models not loaded", AppTheme.WarningText, true);
                    return true;
                }
                SetKeyStatus("\uE783", "Could not save the key — try again", AppTheme.AccentRed, true);
                return false;
            }
        }

        saveKeyBtn.Tapped += async (_, _) => await SaveAndValidateKeyAsync();

        PopulateModels(providers[0]);

        var warnedNoKey = false;
        providerCombo.SelectionChanged += (_, _) =>
        {
            if (providerCombo.SelectedIndex >= 0 && providerCombo.SelectedIndex < providers.Length)
            {
                var p = providers[providerCombo.SelectedIndex];
                PopulateModels(p);
                UpdateSignupLink(p);
                warnedNoKey = false;
            }
        };

        // Select button — returned to caller for button row layout
        var selectBtn = AppTheme.CreateAccentButton("Continue with Cloud Model");
        selectBtn.Tapped += async (_, _) =>
        {
            // Save and validate any key in the box (same logic as Save button)
            if (!await SaveAndValidateKeyAsync())
                return;

            var provider = providers[providerCombo.SelectedIndex];
            var providerKey = provider.ToString().ToLowerInvariant();

            // Warn once if no API key — block the first click, allow the second
            if (!_apiKeys.HasApiKey(providerKey) && !warnedNoKey)
            {
                SetKeyStatus("\uE7BA", "No API key — press Continue again to proceed without one.", AppTheme.WarningText, true);
                warnedNoKey = true;
                return;
            }

            var models = CloudModels.GetModelsForProvider(provider);
            if (modelCombo.SelectedIndex >= 0 && modelCombo.SelectedIndex < models.Length)
            {
                var chosen = models[modelCombo.SelectedIndex];
                App.Services.GetRequiredService<ModelManagementViewModel>().SelectCloudModel(chosen.Name);
                Logger.Information("Onboarding: selected cloud model {Provider}/{Model}", provider, chosen.Name);
                ShowStep(6);
            }
        };
        return selectBtn;
    }

    /// <summary>TRN-6: the ONE place onboarding decides which local model it recommends, and the
    /// only source of the size it prints. Both were previously literals repeated across five sites
    /// — two model names and three "466 MB" strings — which is how the q8 catalogue change would
    /// have shipped a recommendation for a model that no longer exists next to a size that was
    /// never true of it (caught at review).
    ///
    /// <para><b>The English/multilingual branch is gone (2026-08-03).</b> It read
    /// <c>_isEnglish ? "ggml-small.en-q8_0" : "ggml-small-q8_0"</c>, and dropping the English-only
    /// rows turned the first arm into a <c>.Single()</c> that THROWS during first-run onboarding for
    /// every user who picks English — the users with no persisted selection for
    /// <c>LocalModelMigration</c> to rewrite. Nothing in the gate battery caught it (there is no
    /// OnboardingPageTests, and the size-from-catalogue design above makes the SIZE drift-proof
    /// while leaving the NAME a literal). Caught by plan review.</para>
    ///
    /// <para>The name now lives on <see cref="RecommendedLocalModelName"/> so a test can pin it
    /// against the catalogue. Pinning it here would be circular — the test would assert a literal it
    /// owns.</para></summary>
    /// <summary>The recommendation for THIS user: their machine, and the language they picked at
    /// step 3 — which runs before this one, so the value is settled by the time it is read.</summary>
    private TranscriptionModelInfo RecommendedLocalModel =>
        RecommendedLocalModelFor(_parakeetAvailable, _selectedLanguage);

    /// <summary>Which mode step 4 opens on (ONB-1): the user's CURRENT configuration, never a
    /// constant.
    ///
    /// <para>A fresh install has the local Whisper default persisted
    /// (<see cref="AppDefaults.SelectedModelName"/>), so this answers <c>false</c> — local first,
    /// which is what ONB-1 asked for. The reason it reads the setting rather than hardcoding
    /// <c>false</c> is that the wizard is re-runnable and the two branches are NOT symmetric: the
    /// local branch CAN write <see cref="AppDefaults.SelectedModelName"/> as soon as step 5 is
    /// REACHED — when, and only when, the recommended model is already on disk. That write sits in
    /// <c>BuildLocalDetailPanel</c>, which runs during panel CONSTRUCTION, so in that case merely
    /// navigating there rewrites the setting with no tap at all. (If the recommendation is not
    /// downloaded, reaching step 5 writes nothing.) The cloud branch persists only when
    /// the user taps the dedicated "Continue with Cloud Model" button (see <c>SelectCloudModel</c>),
    /// and with no API key on file only on the SECOND tap, after a warning. So a hardcoded local
    /// default would silently convert a configured cloud user just by walking them to step 5; the old
    /// hardcoded cloud default could not do the reverse. Do NOT restate this as "writes on
    /// Continue" (both diff reviewers caught that wording): the Continue tap is not a guard point.</para>
    ///
    /// <para><b>An UNKNOWN name classifies as local, and this call does not repair it</b> — it only
    /// decides which card opens. An imported settings backup can carry one; see
    /// <see cref="PersistedLocalSelectionIsServiceable"/> for why that route is supported rather than
    /// hypothetical. What guarantees such a user cannot FINISH the wizard on a dead name is the
    /// serviceability gate on "Skip download for now": with Skip suppressed, only Download and
    /// Back-then-cloud remain, and both repair the selection. Outside the wizard the name is still
    /// unreconciled — that is ONB-6, which is no longer an onboarding completion path.</para>
    ///
    /// <para>Internal so a test can pin all three cases — the page itself is not
    /// unit-constructible (its ctor resolves four services and builds a WinUI Page).</para></summary>
    internal static bool InitialUseCloud(string? persistedModel) =>
        CloudModels.IsCloudModel(persistedModel);

    /// <summary>Is the persisted selection a LOCAL model this machine can actually run — i.e. would
    /// leaving the download step without choosing anything still end with the user on a working local
    /// model?
    ///
    /// <para>True for a local catalogue row whose runtime is available, downloaded or not: a
    /// catalogue name that is not on disk yet is fine, because the first recording downloads it (that
    /// is exactly what the comment above the Skip button describes). False for everything else —
    /// an unknown name (nothing to download, nothing to route to, and the user gets a generic
    /// "Recording failed" that never mentions the model), a row whose runtime is unavailable on this
    /// machine, and <b>a cloud model</b>.</para>
    ///
    /// <para><b>Why cloud is false here, which is not obvious:</b> this gate sits in the LOCAL branch
    /// of the download step. A user who deliberately chose Local must not finish onboarding with a
    /// cloud model still selected — Skip persists nothing, so treating a cloud selection as
    /// "serviceable" let that user complete the wizard and keep sending audio to a provider after
    /// asking for offline transcription (Codex diff review round 4; my earlier version returned true
    /// for cloud and a test enshrined it).</para>
    ///
    /// <para><b>This gates whether "Skip download for now" is offered at all</b> (ONB-1 diff review,
    /// Codex round 2). The state is reachable through the SUPPORTED settings-import path, not just
    /// hand edits: <c>SelectedModelName</c> is exported and imported verbatim, arbitrary strings pass
    /// validation, and import runs only <c>LocalModelMigration</c>, which rewrites retired LOCAL
    /// names — so an older backup carrying a retired CLOUD name survives it. Before ONB-1 such a
    /// rerun opened on the cloud card, where an explicit tap repaired the selection; opening on local
    /// made Skip the natural exit and would have carried the dead name to the end of the wizard.
    /// Suppressing Skip closes that without inventing the replacement-selection policy the Skip
    /// comment defers — the user still has Download in front of them and Back to choose cloud.</para>
    ///
    /// <para>Deliberately NOT a check for "is it downloaded": that is what the branch above already
    /// asks, and conflating the two would stop offering Skip to the users it exists for.</para>
    ///
    /// <para><b>Catalogue membership is not enough — the runtime has to be able to RUN it</b> (Codex
    /// diff review round 3). A Parakeet selection on a machine or build where sherpa-onnx is
    /// unavailable is exactly as dead as an unknown name: <c>ParakeetLocalRuntime</c> answers
    /// <c>PrepareOutcome.Unavailable</c> and the recording fails. Whisper rows are always serviceable
    /// (whisper.cpp ships with the app), so availability is asked of Parakeet rows only, keyed on the
    /// row's own <see cref="LocalRuntimeKind"/> rather than its name — the same rule the local-runtime
    /// seam uses, so a future non-Whisper engine has to opt in here rather than inherit a pass.</para></summary>
    internal static bool PersistedLocalSelectionIsServiceable(string? persistedModel, bool parakeetAvailable)
    {
        // Only LOCAL catalogue rows live in PredefinedModels, so a cloud name finds nothing here and
        // answers false. That is the point, not an oversight: the gate is in the LOCAL branch, so a
        // persisted cloud model must never authorize skipping local setup.
        var row = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, persistedModel));
        if (row is null)
            return false;

        // Explicit and FAIL-CLOSED per runtime. A `!= Parakeet` test would hand every FUTURE engine a
        // pass on the strength of not being Parakeet, which is the opposite of the opt-in this comment
        // claims (Codex diff review round 4 caught exactly that).
        return row.Runtime switch
        {
            LocalRuntimeKind.Whisper => true,          // whisper.cpp ships with the app
            LocalRuntimeKind.Parakeet => parakeetAvailable,
            _ => false,                                 // a new engine opts in HERE, deliberately
        };
    }

    /// <summary>Step 4's local-card description. Shares <see cref="RecommendedLocalModelFor"/> and
    /// <see cref="FormatSize"/> with step 5's <c>BuildLocalDetailPanel</c>, so the mode-choice card
    /// and the download screen cannot print different sizes — which is exactly what happened while
    /// this string carried a hardcoded "~260 MB" and the next screen offered Parakeet at 670 MB.
    ///
    /// <para>Internal for the same reason as <see cref="InitialUseCloud"/>: the test asserts the
    /// rendered sentence against the resolvers, so the pin is "both screens agree" rather than a
    /// re-implementation of the arithmetic.</para></summary>
    internal static string LocalOptionDescription(bool parakeetAvailable, string? selectedLanguage) =>
        "Works offline. Downloads a model "
        + $"({FormatSize(RecommendedLocalModelFor(parakeetAvailable, selectedLanguage))}).";

    /// <summary>The two cards step 4 offers.</summary>
    internal enum TranscriptionChoiceCard
    {
        Local,
        Cloud
    }

    /// <summary>The order step 4 RENDERS its two cards in. Local first (ONB-1).
    ///
    /// <para>This is data rather than two literal <c>Children.Add</c> calls for one reason: the page
    /// cannot be constructed in a unit test, so without a seam the card's entire headline behaviour
    /// ("local first") was unpinned and a future edit could swap it back with the suite still green
    /// (Codex diff review). Reordering this array is now the only way to change what the user sees,
    /// and a test asserts what it says.</para></summary>
    internal static readonly TranscriptionChoiceCard[] ChoiceCardOrder =
        [TranscriptionChoiceCard.Local, TranscriptionChoiceCard.Cloud];

    /// <summary>
    /// Which local model the "Download Local Model" step is really about: the persisted selection
    /// when it names a local catalogue row, otherwise the recommendation.
    ///
    /// <para>Pure and <c>internal</c> so it is testable — Codex's second diff review noted the
    /// corrected resolution was unpinned, and it is the part that decides whether the language note
    /// fires at all.</para>
    ///
    /// <para>Exists because onboarding is not only a first-run flow: it can be relaunched over an
    /// existing configuration, and the "Skip download for now" path deliberately keeps whatever is
    /// already selected. Anything describing "the model you are about to use" therefore has to ask
    /// what IS selected, not what is recommended — otherwise a user already on Parakeet with
    /// Chinese pinned is told nothing.</para>
    ///
    /// <para>A CLOUD selection resolves to null and falls back, because this step only runs once the
    /// user has chosen local. Case-insensitive: the runtime seam resolves that way and a persisted
    /// value can carry any casing from an import or a hand edit.</para>
    /// </summary>
    internal static TranscriptionModelInfo ResolveOnboardingLocalModel(
        string? persistedSelection, bool parakeetAvailable, string? selectedLanguage)
    {
        var recommended = RecommendedLocalModelFor(parakeetAvailable, selectedLanguage);
        if (string.IsNullOrWhiteSpace(persistedSelection)) return recommended;

        return PredefinedModels.Models.FirstOrDefault(
            m => string.Equals(m.Name, persistedSelection, StringComparison.OrdinalIgnoreCase))
            ?? recommended;
    }

    /// <summary>
    /// The local model onboarding recommends — <b>Parakeet since 2026-08-04 (owner instruction),
    /// falling back to Whisper Small on a machine that cannot run it.</b>
    ///
    /// <para>No longer a const, and the reason is the fallback: Parakeet is unusable behind the
    /// <c>-p:ParakeetEnabled=false</c> build lever, below the SSE2 CPU floor, and in a NATIVE arm64
    /// process — which we do not ship, so ARM64 machines running the x64 build under emulation DO
    /// get Parakeet (see <c>DefaultLocalModel</c>).
    /// Recommending it there would download 670 MB and then fail to load, because
    /// <c>PrepareOutcome.Unavailable</c> only arrives after the bytes are on disk — which is
    /// precisely why <c>IsAvailable</c> exists as a question separate from <c>CanServe</c>, and why
    /// it is asked HERE rather than at prepare time.</para>
    ///
    /// <para>Availability is a PARAMETER, not a lookup: it is read once in the constructor (a
    /// CPU/build-flag probe, not I/O) and threaded through, which keeps these resolvers pure and
    /// lets a test drive both branches — including the unavailable one, which cannot be reproduced
    /// on the machine this is developed on.</para>
    ///
    /// <para>Internal so <c>ModelsPageTests</c> can pin BOTH branches against the catalogue: the
    /// resolver below uses <c>Single()</c>, so a name absent from the catalogue is a first-run
    /// crash rather than a fallback — which is exactly how the dropped <c>.en</c> rows nearly
    /// shipped a crash (recorded above).</para></summary>

    internal static string RecommendedLocalModelName(bool parakeetAvailable, string? selectedLanguage) =>
        DefaultLocalModel.Resolve(parakeetAvailable, selectedLanguage);

    /// <summary>
    /// Which picker row a PERSISTED language value selects, and what the combo should display.
    ///
    /// <para>Pure, and extracted for exactly one reason: reverting the case-insensitive comparison
    /// to <c>==</c> left every test green (Codex diff review r4). A WinUI page is not
    /// unit-constructible, so behaviour that lives only in <c>BuildLanguageStep</c> cannot be pinned
    /// at all — the same reason <c>ResolveOnboardingLocalModel</c> and
    /// <c>RecommendedLocalModelName</c> are already static helpers here.</para>
    ///
    /// <para>Case-INSENSITIVE because a persisted value comes back however an import or hand edit
    /// left it. The caller adopts the returned <c>Code</c> so the state matches what is displayed;
    /// an imported <c>"JA"</c> otherwise showed "Auto-detect" while the state kept <c>"JA"</c>, and
    /// Continue then persisted a language the screen never showed.</para>
    ///
    /// <para>An UNLISTED code is KEPT and SHOWN, as a synthetic row labelled with the code itself
    /// (<c>yue (imported)</c>). The value survives and the screen tells the truth about it.</para>
    ///
    /// <para><b>Two earlier versions were each wrong, in opposite directions, and both were caught.</b>
    /// The first returned <c>null</c> and left the value alone while displaying "Auto-detect" — the
    /// screen said one thing and Continue persisted another (r8). The second coerced it to
    /// <c>auto</c>, which agreed with the screen but ERASED a valid preference: the picker's 99 codes
    /// are not the limit of what whisper.cpp accepts, which is exactly why
    /// <c>ModelLanguageSupport.SupportedCodesFor</c> refuses to derive a negative claim from that
    /// list, and <c>yue</c> is the repo's own worked example (r9). Showing it resolves both: nothing
    /// is hidden and nothing is lost.</para>
    ///
    /// <para>The label is deliberately the raw code — we have no display name for something outside
    /// the catalogue, and inventing one would be a second fabrication.</para>
    /// </summary>
    internal static (string Code, string Display) ResolvePersistedLanguage(
        IReadOnlyList<(string Code, string Display)> languages, string? persisted)
    {
        foreach (var l in languages)
            if (string.Equals(l.Code, persisted, StringComparison.OrdinalIgnoreCase))
                return (l.Code, l.Display);

        // Nothing to preserve — a blank persisted value is genuinely "no choice", not a choice we
        // cannot render.
        if (string.IsNullOrWhiteSpace(persisted)) return ("auto", "Auto-detect");

        return (persisted, $"{persisted} (imported)");
    }

    /// <summary>
    /// The language picker's row set, and the ONLY way to turn a selected row INDEX back into a
    /// language code.
    ///
    /// <para><b>A type rather than two helpers, and that is the whole point.</b> The defect it closes
    /// was two lists: the synthetic <c>yue (imported)</c> row went straight into
    /// <c>ComboBox.Items</c> while <c>SelectionChanged</c> looked selections up in the CATALOGUE
    /// list, so reselecting the synthetic row found no match and <c>_selectedLanguage</c> silently
    /// kept the previous selection while the combo displayed the imported code (Codex diff review
    /// r10). Fixing that by passing the combined list to both sites left the same mistake one edit
    /// away — the catalogue list is still in scope at the call site, and a test that repeats the
    /// lookup itself cannot notice a handler that reverts to it (Codex r11, Kimi r11 independently).
    /// Owning both operations here means the population and the lookup CANNOT disagree, because
    /// neither caller gets to choose a list.</para>
    ///
    /// <para>Membership is decided on the CODE, not the display string (Kimi r11). The synthetic row
    /// exists precisely because the code is absent from the catalogue, so the code is what the
    /// question is about; two catalogue rows sharing a display name would otherwise suppress a
    /// genuinely unlisted code's row.</para>
    ///
    /// <para><b>Case-INSENSITIVE, and the reason is narrower than "to match
    /// <see cref="ResolvePersistedLanguage"/>" (Kimi r13 asked; the first answer was vaguer than the
    /// truth).</b> For a code the catalogue HAS, the resolver returns that row's own <c>Code</c>
    /// verbatim, so the comparison would match under any comparer. For a code it does NOT have,
    /// nothing matches under any comparer. The one branch where casing can actually differ is the
    /// BLANK one, which returns a hardcoded <c>"auto"</c> rather than a catalogue value — so a
    /// catalogue that ever spelled its auto-detect row <c>"Auto"</c> would, under an ordinal
    /// comparer, find no match and append a SECOND auto-detect row. Pinned by
    /// <c>ADifferentlyCasedAutoRow_IsNotDuplicated</c>.</para>
    /// </summary>
    internal sealed class LanguageComboRows
    {
        private readonly IReadOnlyList<(string Code, string Display)> _rows;

        internal LanguageComboRows(
            IReadOnlyList<(string Code, string Display)> languages, (string Code, string Display) resolved)
        {
            var existing = -1;
            for (var i = 0; i < languages.Count; i++)
                if (string.Equals(languages[i].Code, resolved.Code, StringComparison.OrdinalIgnoreCase))
                {
                    existing = i;
                    break;
                }

            if (existing >= 0)
            {
                _rows = languages;
                ResolvedIndex = existing;
            }
            else
            {
                _rows = [.. languages, resolved];
                ResolvedIndex = languages.Count;
            }
        }

        /// <summary>What the combo shows, in order. The population loop's only source.</summary>
        internal IEnumerable<string> Displays => _rows.Select(r => r.Display);

        /// <summary>Which row the persisted value resolved to — what the combo must start on.</summary>
        internal int ResolvedIndex { get; }

        /// <summary>
        /// The code behind the row at <paramref name="index"/>, or <c>null</c> when the index names no
        /// row — which a caller must treat as "leave the current selection alone", never as a code.
        ///
        /// <para><b>By INDEX, not by display string, and that distinction is a fixed defect rather
        /// than a preference.</b> The first version looked selections up by display text and returned
        /// the FIRST row carrying it — so with the very collision this class claims to support (a
        /// catalogue row rendering identically to the synthetic one), selecting the synthetic
        /// <c>yue (imported)</c> row returned the OTHER row's code. The row was present and
        /// unselectable, which is worse than the hidden-preference defect it was added to fix, and
        /// the collision test asserted only the row COUNT so it never noticed (Codex diff review
        /// r12).</para>
        ///
        /// <para>The combo is populated from <see cref="Displays"/> in order and holds plain strings,
        /// so its <c>SelectedIndex</c> IS this index. Deliberately not a data-object
        /// <c>ItemTemplate</c>, which would also carry identity: plain strings are what the repo's
        /// WinUI 3 rules push every ComboBox toward, and an index needs no template at all.</para>
        /// </summary>
        internal string? CodeAt(int index)
            => index >= 0 && index < _rows.Count ? _rows[index].Code : null;
    }

    internal static TranscriptionModelInfo RecommendedLocalModelFor(
        bool parakeetAvailable, string? selectedLanguage) =>
        PredefinedModels.Models.Single(
            m => m.Name == RecommendedLocalModelName(parakeetAvailable, selectedLanguage));

    /// <summary>
    /// Why this model is the recommendation — one clause, the same for both engines since ONB-4.
    ///
    /// <para><b>The Parakeet branch is gone by OWNER DECISION (2026-08-05, UAT §91.2), reversing the
    /// restriction this method existed to enforce.</b> It used to say "for its speed" for Parakeet and
    /// reserve "best balance of speed and accuracy" for Whisper Small, because <c>ModelRatings</c>
    /// measured Parakeet's accuracy on this hardware on 2026-08-30 (15.3% WER, best of any local
    /// model on the owner's own corpus), which agrees with what the owner already reported from
    /// use — the note below predates the measurement and is kept because it is what prompted it.
    /// Owner, from use: <i>"I've
    /// been using Parakeet now for a while and it is very accurate. We can absolutely say it's the
    /// best balance of speed and accuracy."</i> Their call to make.</para>
    ///
    /// <para><b>What the claim rests on, as of 2026-08-30: a measurement.</b> Parakeet scored
    /// 15.3% WER on the owner's own 56-clip corpus — the best of any LOCAL model, ahead of Whisper
    /// Medium's 17.0% and of four paid cloud rows — so the comparative claim against Whisper Small
    /// (19.9%) is measured rather than carried. It rested on an Open ASR Leaderboard judgement
    /// until that date, which is what the sentence above used to say. The measurement is of the
    /// GGUF bundle that ships by default; under the <c>PcppEnabled=false</c> kill switch the
    /// sherpa bundle ships instead and carries the star by the same-weights anchoring rule, not by
    /// its own measurement.</para>
    ///
    /// <para>"for your chosen language" is the second half of the same owner decision: the
    /// recommendation is already language-derived (<see cref="DefaultLocalModel.Resolve"/> falls back
    /// to Whisper where Parakeet lacks the language), so the sentence now says so instead of implying
    /// one model is best in general. The parameter stays for the shared signature and because a future
    /// per-engine clause would need it again.</para>
    /// </summary>
    /// <param name="selectedLanguage">
    /// The pinned language, so the clause can DROP its language half when nothing claims to support
    /// that value.
    ///
    /// <para><b>ONB-5 r3 (Codex):</b> the note was taught to refuse a download promise it could not
    /// back — <see cref="ModelLanguageSupport.CanRecognise"/> requires positive evidence — but this
    /// sentence, two lines above it on the same screen, still said "for your chosen language" for an
    /// imported <c>"made-up"</c>. The composer ended up stricter than the screen it sits on. Fixed
    /// here rather than deferred to a card, per the owner rule of 2026-08-06.</para>
    /// </param>
    /// <remarks>
    /// <paramref name="selectedLanguage"/> is deliberately NOT optional. A default made omission
    /// compile, so the language claim could silently return — and a source-text test is a weak guard
    /// against that compared with the compiler refusing it outright (Codex diff review r6).
    /// </remarks>
    internal static string RecommendationReason(
        TranscriptionModelInfo model, string? selectedLanguage)
        => ModelLanguageSupport.CanRecognise(model, selectedLanguage)
            ? "for the best balance of speed and accuracy for your chosen language"
            // No positive evidence for this language — the speed/accuracy half is still true and
            // sourced (TRN-3's stars), so only the language claim is dropped. Saying less is the
            // honest option; saying nothing would delete a defensible recommendation.
            : "for the best balance of speed and accuracy";

    /// <summary>Download size as onboarding prints it — MB, no decimals, matching the copy's tone.
    /// Derived from the catalogue's exact byte count so it cannot drift from what is downloaded.</summary>
    private static string FormatSize(TranscriptionModelInfo model) =>
        $"{model.FileSizeBytes / 1_000_000} MB";

    private void BuildLocalDetailPanel(StackPanel panel)
    {
        var recommended = RecommendedLocalModel;
        var modelName = recommended.Name;
        var displayName = recommended.DisplayName;

        var infoText = new TextBlock
        {
            // The reason clause and what authorises it live on RecommendationReason (ONB-4).
            Text = $"The {displayName} model is recommended {RecommendationReason(recommended, _selectedLanguage)} ({FormatSize(recommended)}).",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(infoText);

        var downloaded = _downloader.GetDownloadedModels();
        // Case-insensitive: List.Contains is ordinal, so an on-disk "GGML-SMALL.bin" would read as
        // "not downloaded" and onboarding would offer to fetch a model that is already there.
        var hasRecommended = downloaded.Contains(modelName, ModelDiskReconciliation.NameComparer);

        if (hasRecommended)
        {
            panel.Children.Add(new TextBlock
            {
                // DISPLAY name, never the catalogue id (UI-2, owner rule 2026-08-05 §91.4: "we should
                // never use technical file names for the models ... Except for in the logs where it
                // matters"). This line and the inventory sentence ONB-4 deleted below were the two
                // live leaks; `displayName` is the same row's friendly name, already resolved above.
                Text = $"Model already downloaded: {displayName}",
                FontSize = 14,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });

            _settings.SetString(AppDefaults.SelectedModelName, modelName);
            var vm = App.Services.GetRequiredService<ModelManagementViewModel>();
            vm.SelectedModelName = modelName;
            vm.LoadModels(); // Refresh downloaded state

            // Continue button added via AddButtonRow by caller
        }
        else
        {
            // ONB-4b DELETED the inventory sentence that stood here — "You have Parakeet but not
            // ggml-small-q8_0." Owner, UAT 2026-08-05: "the user doesn't care". It was also the second
            // of UI-2's two raw-id leaks, so removing it closed both at once. Do not reinstate it: the
            // user is being offered a download, and which OTHER models they happen to hold is not
            // information that helps them decide.
            var progressBar = new ProgressBar
            {
                Width = 300,
                Height = 6,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                Background = AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 8, 0, 0)
            };
            var progressText = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            panel.Children.Add(progressBar);
            panel.Children.Add(progressText);

            // UI-3: ONE button. Its label and palette follow the download's state — a greyed-out
            // "Download X" sitting above a separate "Cancel Download" told the user the action they
            // wanted was disabled, when it had simply moved.
            var primaryLabel = $"Download {displayName} ({FormatSize(recommended)})";
            var actionBtn = AppTheme.CreateActionToggleButton(primaryLabel, "Cancel Download");
            actionBtn.Element.HorizontalAlignment = HorizontalAlignment.Center;
            actionBtn.Element.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(actionBtn.Element);

            // Error text block — hidden until an error occurs
            var errorText = new TextBlock
            {
                Foreground = AppTheme.Brush(AppTheme.AccentRed),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0)
            };
            panel.Children.Add(errorText);

            // Skip button added via AddButtonRow by caller if models already downloaded

            actionBtn.Element.Tapped += async (_, _) =>
            {
                // ONE handler, branching on the button's own state, so the two actions cannot get
                // out of step with the two labels.
                if (actionBtn.IsCancelling)
                {
                    // Ignore a tap inside the arming window — it is the second half of a
                    // double-click on Download, not a cancel. The window is the USER'S configured
                    // double-click interval; see ActionToggleCore's remarks.
                    if (!ActionToggleCore.CancelTapIsArmed(
                            TimeSpan.FromMilliseconds(Environment.TickCount64 - _downloadCancelShownTicks),
                            ActionToggleCore.ArmingWindow(NativeInterop.GetDoubleClickTime())))
                        return;

                    // Mark THIS attempt user-cancelled — not abandoned. The completion path treats
                    // the two differently, and Abandoned always wins if both end up set (see
                    // DownloadAttemptState's remarks): a user who taps Cancel and then also
                    // navigates away gets the navigation's answer.
                    if (_downloadAttempt is { } cancelling)
                    {
                        var (cancellingState, cancellingCts) = cancelling;
                        cancellingState.CancelledByUser = true;
                        cancellingCts.Cancel();
                    }
                    return;
                }

                // Reset error state from previous attempt
                errorText.Visibility = Visibility.Collapsed;
                progressBar.Value = 0;
                progressText.Text = "0%";

                actionBtn.ShowCancel();
                _downloadCancelShownTicks = Environment.TickCount64;

                // A fresh attempt, and `state`/`attemptCts` — the LOCALS — are what this closure
                // checks at completion, never a re-read of `_downloadAttempt`. That is what makes
                // the provenance attempt-scoped rather than page-global.
                var state = new Helpers.DownloadAttemptState();
                var attemptCts = new CancellationTokenSource();
                _downloadAttempt = (state, attemptCts);
                var ct = attemptCts.Token;

                try
                {
                    var model = PredefinedModels.Models
                        .First(m => ModelDiskReconciliation.IsSameModel(m.Name, modelName));
                    var progress = new Progress<double>(p =>
                    {
                        progressBar.Value = p * 100;
                        progressText.Text = $"{p * 100:F0}%";
                    });
                    await _downloader.DownloadModelAsync(model, progress, ct);

                    // A completed-but-cancelled download is only a SUCCESS when `state` says so —
                    // ShouldFinalize, not a raw check of one flag. `ModelDownloadManager` has no
                    // cancellation check between its final commit and its return, so this line is
                    // genuinely reachable however the token got cancelled (Codex diff review r5, r6).
                    //
                    // A page-level "cancelled by user" bool was wrong here: Back, Unload, and step
                    // reconstruction all cancel the same token WITHOUT resetting a stale flag from an
                    // earlier Cancel tap, so a user who tapped Cancel and then navigated away still
                    // read as "keep it" — writing settings and forcing step 6 for a screen already
                    // left. `state` is scoped to THIS attempt and `Abandoned` always overrides
                    // `CancelledByUser` in `ShouldFinalize` for exactly that reason.
                    //
                    // Four versions of this branch, each wrong in a different way: a bare return (no
                    // explanation), a message that still returned (installed but unselected), success
                    // unconditionally (the Back-after-Cancel race), and now this.
                    if (!state.ShouldFinalize(ct.IsCancellationRequested))
                    {
                        // The page is going away or the user is navigating. The bytes are on disk
                        // and the Models page will find them; touching UI or settings from here
                        // would be acting on behalf of a screen that no longer exists.
                        Logger.Information("Onboarding download finished after the screen was abandoned — leaving state alone");
                        return;
                    }

                    if (ct.IsCancellationRequested)
                        Logger.Information("Onboarding download completed as the user's cancel arrived — keeping it");
                    _settings.SetString(AppDefaults.SelectedModelName, modelName);
                    var vm = App.Services.GetRequiredService<ModelManagementViewModel>();
                    vm.SelectedModelName = modelName;
                    vm.LoadModels(); // Refresh downloaded state after download

                    // The download CHANGED what this step should say, so its cached panel is now a
                    // lie: pressing Back restored "Download Whisper Small (264 MB)" plus the old
                    // progress and Skip controls for a model that is already downloaded and
                    // selected (Codex diff review). Every other branch that changes this step's
                    // meaning already invalidates it — the cloud/local switches at :529 and :539 —
                    // and completing a download is the same kind of change.
                    _stepCache.Remove(5);

                    ShowStep(6);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Logger.Information("Onboarding model download cancelled");
                        // UI-3 side effect worth naming: the button now morphs to Cancel UNDER the
                        // cursor, so the second click of a habitual double-click lands on it and
                        // aborts a download the user meant to start. The old layout absorbed that
                        // click on a disabled button. Silence would read as "Download doesn't work",
                        // so say what happened (Kimi diff review).
                        errorText.Text = "Download cancelled.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        Logger.Warning("Onboarding model download stalled (network timeout)");
                        errorText.Text = "Download stalled. Check your internet connection and try again.";
                        errorText.Visibility = Visibility.Visible;
                    }
                    progressBar.Value = 0;
                    progressText.Text = "0%";
                }
                catch (Services.Transcription.ModelSourceIOException ex) when (ex.InnerException is OperationCanceledException)
                {
                    // Same stall restoration as ModelManagementViewModel's arm (Kimi,
                    // verification round): TRN-33's RemoteAsync re-types a stall OCE as a typed
                    // source failure, which would otherwise fall to the generic handler.
                    Logger.Warning(ex, "Onboarding model download stalled (network timeout)");
                    errorText.Text = "Download stalled. Check your internet connection and try again.";
                    errorText.Visibility = Visibility.Visible;
                    progressBar.Value = 0;
                    progressText.Text = "0%";
                }
                catch (HttpRequestException ex)
                {
                    Logger.Error(ex, "Onboarding model download failed (HTTP)");
                    errorText.Text = "Download failed. Check your internet connection and try again.";
                    errorText.Visibility = Visibility.Visible;
                    progressBar.Value = 0;
                    progressText.Text = "0%";
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Onboarding model download failed");
                    errorText.Text = "Download failed. Please try again.";
                    errorText.Visibility = Visibility.Visible;
                    progressBar.Value = 0;
                    progressText.Text = "0%";
                }
                finally
                {
                    // Retire THIS attempt. The `ReferenceEquals` guard is defensive — a new attempt
                    // can only start after this one's button returns to primary, which happens right
                    // here, so `_downloadAttempt` cannot legitimately be anything else yet — but a
                    // guard costs nothing and means a future reordering fails safe instead of
                    // clobbering a newer attempt's reference. Compared by the STATE object's
                    // identity, since the field holds a value tuple.
                    if (_downloadAttempt is { } current && ReferenceEquals(current.State, state))
                        _downloadAttempt = null;
                    attemptCts.Dispose();

                    // Back to the primary action — the SAME finally that restored both buttons
                    // before, so the restore point is unchanged.
                    actionBtn.ShowPrimary();
                }
            };
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Step 6: AI Enhancement
    // ────────────────────────────────────────────────────────────────

    private void BuildAiEnhancementStep()
    {
        var enhancement = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();

        AddStepHeader("AI Enhancement (Optional)",
            "Improve transcriptions with AI or generate images by voice.\nRequires an API key from a supported provider.");

        // ── Provider config panel (shown when toggle is on) ──
        var configPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        BuildEnhancementProviderPanel(configPanel, enhancement);

        // ── Toggle ──
        var currentEnabled = _settings.GetBool(AppDefaults.AiEnhancementEnabled, false);
        if (currentEnabled) configPanel.Visibility = Visibility.Visible;

        var toggleSetting = AppTheme.CreateToggleSetting(
            "Enable AI Enhancement",
            "Turn on AI-powered text enhancement and image generation.",
            currentEnabled,
            isOn =>
            {
                _settings.SetBool(AppDefaults.AiEnhancementEnabled, isOn);
                configPanel.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
            });
        _contentPanel.Children.Add(toggleSetting);
        _contentPanel.Children.Add(configPanel);

        // ── Buttons ──
        var continueBtn = AppTheme.CreateAccentButton("Continue");
        continueBtn.Tapped += async (_, _) =>
        {
            // Validate any unsaved keys before proceeding. Run ALL validators in one pass (don't
            // short-circuit on the first failure), so a key that fails validation on one provider
            // still lets the other provider's key be saved in the same click. (An EMPTY box with no
            // saved key passes straight through — see the validator: there is no "press again"
            // guard on this step any more; the cloud-transcription step keeps its own. This comment
            // described that guard until 2026-09-13, and the Done card's copy was written against it.)
            if (_enhancementKeyValidators != null)
            {
                bool allOk = true;
                foreach (var validate in _enhancementKeyValidators)
                {
                    if (!await validate())
                        allOk = false;
                }
                if (!allOk)
                    return; // a validator blocked (e.g. just-armed no-key warning) — don't proceed
            }
            ShowStep(7);
        };
        AddButtonRow(CreateBackButton(), continueBtn);
    }

    private void BuildEnhancementProviderPanel(StackPanel panel, Services.AIEnhancement.AIEnhancementService enhancement)
    {
        var allProviders = new[] {
            Services.AIEnhancement.AIProvider.OpenAI,
            Services.AIEnhancement.AIProvider.Anthropic,
            Services.AIEnhancement.AIProvider.Gemini,
            Services.AIEnhancement.AIProvider.Groq,
            Services.AIEnhancement.AIProvider.Mistral,
            Services.AIEnhancement.AIProvider.OpenRouter,
            Services.AIEnhancement.AIProvider.Cerebras
        };
        var imageProviders = new[] {
            Services.AIEnhancement.AIProvider.OpenAI,
            Services.AIEnhancement.AIProvider.Gemini,
            Services.AIEnhancement.AIProvider.OpenRouter
        };

        var signupUrls = new Dictionary<Services.AIEnhancement.AIProvider, string>
        {
            [Services.AIEnhancement.AIProvider.OpenAI] = "https://platform.openai.com/api-keys",
            [Services.AIEnhancement.AIProvider.Anthropic] = "https://console.anthropic.com/settings/keys",
            [Services.AIEnhancement.AIProvider.Gemini] = "https://aistudio.google.com/apikey",
            [Services.AIEnhancement.AIProvider.Groq] = "https://console.groq.com/keys",
            [Services.AIEnhancement.AIProvider.Mistral] = "https://console.mistral.ai/api-keys",
            [Services.AIEnhancement.AIProvider.OpenRouter] = "https://openrouter.ai/settings/keys",
            [Services.AIEnhancement.AIProvider.Cerebras] = "https://cloud.cerebras.ai",
        };

        // ── Text Enhancement Provider ──
        var textCard = new StackPanel { Spacing = 10 };
        textCard.Children.Add(new TextBlock
        {
            Text = "Text Enhancement",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        });
        textCard.Children.Add(new TextBlock
        {
            Text = "Improve grammar, translate, or summarize your transcriptions.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap
        });

        // Only pre-select if the user previously chose a provider (raw setting is non-empty)
        var rawTextProvider = _settings.GetString(AppDefaults.AiProvider, "");
        Services.AIEnhancement.AIProvider? textProviderInit =
            !string.IsNullOrEmpty(rawTextProvider) && Enum.TryParse<Services.AIEnhancement.AIProvider>(rawTextProvider, out var tp) ? tp : null;

        var validateTextKey = BuildProviderKeyRow(textCard, allProviders, signupUrls, textProviderInit,
            provider => enhancement.SelectedProvider = provider,
            isImageModels: false);
        panel.Children.Add(AppTheme.CreateCard(textCard));

        // ── Image Generation Provider ──
        var imageCard = new StackPanel { Spacing = 10 };
        imageCard.Children.Add(new TextBlock
        {
            Text = "Image Generation",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        });
        imageCard.Children.Add(new TextBlock
        {
            Text = "Describe an image by voice. Images are placed on your clipboard.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap
        });

        var rawImageProvider = _settings.GetString(AppDefaults.AiImageProvider, "");
        Services.AIEnhancement.AIProvider? imageProviderInit =
            !string.IsNullOrEmpty(rawImageProvider) && Enum.TryParse<Services.AIEnhancement.AIProvider>(rawImageProvider, out var ip) ? ip : null;

        var validateImageKey = BuildProviderKeyRow(imageCard, imageProviders, signupUrls, imageProviderInit,
            provider => enhancement.SelectedImageProvider = provider,
            isImageModels: true);
        panel.Children.Add(AppTheme.CreateCard(imageCard));

        // Store validators so the Continue button can trigger them
        _enhancementKeyValidators = [validateTextKey, validateImageKey];
    }

    /// <summary>
    /// Returns a validation function: saves any unsaved key, returns true if OK to proceed.
    /// </summary>
    private Func<Task<bool>> BuildProviderKeyRow(
        StackPanel card,
        Services.AIEnhancement.AIProvider[] providers,
        Dictionary<Services.AIEnhancement.AIProvider, string> signupUrls,
        Services.AIEnhancement.AIProvider? currentProvider,
        Action<Services.AIEnhancement.AIProvider> onProviderChanged,
        bool isImageModels = false)
    {
        var providerCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Select a provider"
        };
        AppTheme.AllowParentScroll(providerCombo);
        foreach (var p in providers) providerCombo.Items.Add(p.ToString());
        if (currentProvider.HasValue)
            providerCombo.SelectedItem = currentProvider.Value.ToString();
        card.Children.Add(CreateLabeledRow("Provider", providerCombo));

        // Key details panel — hidden until a provider is selected
        var keyDetailsPanel = new StackPanel
        {
            Spacing = 8,
            Visibility = currentProvider.HasValue ? Visibility.Visible : Visibility.Collapsed
        };

        var signupLink = new HyperlinkButton
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0)
        };
        keyDetailsPanel.Children.Add(signupLink);

        var apiKeyBox = new PasswordBox
        {
            PlaceholderText = "Paste your API key",
            Width = 320
        };
        var (eKeyStatusPanel, SetEnhKeyStatus) = CreateKeyStatusPanel();
        var saveKeyBtn = AppTheme.CreateCompactButton("Save", isAccent: true);

        var apiKeyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { apiKeyBox, saveKeyBtn }
        };
        keyDetailsPanel.Children.Add(CreateLabeledRow("API Key", apiKeyRow));
        keyDetailsPanel.Children.Add(eKeyStatusPanel);

        // ── Model selection ──
        var modelCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Save a valid API key first"
        };
        AppTheme.AllowParentScroll(modelCombo);
        var modelRow = CreateLabeledRow("Model", modelCombo);
        modelRow.Visibility = Visibility.Collapsed;
        keyDetailsPanel.Children.Add(modelRow);

        var enhancement = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();

        async Task LoadModelsAsync(Services.AIEnhancement.AIProvider provider, List<string>? prefetchedModels = null)
        {
            modelCombo.Items.Clear();
            modelCombo.PlaceholderText = "Loading models...";
            modelRow.Visibility = Visibility.Visible;
            try
            {
                // Display-ready from the service (modality catalog + provider curation + shared
                // preview/date policy). The prefetched list came from the same call.
                var models = prefetchedModels
                    ?? await enhancement.FetchAvailableModelsAsync(
                        isImageModels
                            ? global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Image
                            : global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Text,
                        showAll: false, providerOverride: provider);
                modelCombo.Items.Clear();
                foreach (var m in models) modelCombo.Items.Add(m);

                var currentModel = isImageModels ? enhancement.SelectedImageModel : enhancement.SelectedModel;
                if (!string.IsNullOrEmpty(currentModel) && models.Contains(currentModel))
                    modelCombo.SelectedItem = currentModel;

                modelCombo.PlaceholderText = models.Count > 0 ? "Select a model" : "No models available";
            }
            catch (Helpers.InvalidApiKeyFormatException ex)
            {
                // ENH-15: a user returning to onboarding with a legacy malformed key otherwise
                // gets only "Failed to load models" — true but unactionable, and the one screen
                // where a stuck first-run user has nowhere else to look. Must precede the bare
                // catch below.
                Logger.Warning("Stored API key for {Provider} has an invalid format: {Verdict}",
                    ex.Provider, ex.Verdict);
                SetEnhKeyStatus("\uE783", ex.UserMessage, AppTheme.AccentRed, true);
                modelCombo.PlaceholderText = "Enter a valid API key";
            }
            catch
            {
                modelCombo.PlaceholderText = "Failed to load models";
            }
        }

        modelCombo.SelectionChanged += (_, _) =>
        {
            if (modelCombo.SelectedItem is string selectedModel)
            {
                if (isImageModels)
                    enhancement.SelectedImageModel = selectedModel;
                else
                    enhancement.SelectedModel = selectedModel;
            }
        };

        card.Children.Add(keyDetailsPanel);

        void UpdateSignup(Services.AIEnhancement.AIProvider provider)
        {
            if (signupUrls.TryGetValue(provider, out var url))
            {
                signupLink.Content = $"Get a {provider} API key \u2192";
                signupLink.NavigateUri = new Uri(url);
                signupLink.Visibility = Visibility.Visible;
            }
            else
            {
                signupLink.Visibility = Visibility.Collapsed;
            }
        }

        void UpdateKeyStatus(Services.AIEnhancement.AIProvider provider)
        {
            var providerKey = provider.ToString().ToLowerInvariant();
            var existingKey = _apiKeys.GetApiKey(providerKey);
            if (!string.IsNullOrEmpty(existingKey))
            {
                apiKeyBox.Password = existingKey;
                SetEnhKeyStatus("\uE73E", "Key saved", AppTheme.AccentGreen, true);
            }
            else
            {
                apiKeyBox.Password = "";
                SetEnhKeyStatus("", "", AppTheme.TextSecondary, false);
            }
        }

        // Shared save-and-validate for both Save button and Continue validator.
        // Returns true if OK to proceed, false if invalid key.
        async Task<bool> SaveAndValidateEnhKeyAsync()
        {
            // ENH-17: declared OUTSIDE the try so the catch-all can tell whether anything was
            // actually written. Inside the try it would be out of scope exactly where it matters.
            var committed = false;
            try
            {
                if (providerCombo.SelectedItem is not string selectedName) return true;
                var idx = Array.FindIndex(providers, p => p.ToString() == selectedName);
                if (idx < 0) return true;
                var provider = providers[idx];
                var providerKey = provider.ToString().ToLowerInvariant();
                var key = apiKeyBox.Password;

                if (string.IsNullOrWhiteSpace(key))
                {
                    // A clear IS a write, so it claims — this screen has two independent re-entrant
                    // triggers (the Save button and the Continue validator).
                    var clearGeneration = _apiKeys.BeginKeyWrite(providerKey);
                    if (!_apiKeys.TryCommitApiKey(providerKey, "", clearGeneration, out _))
                    {
                        Logger.Information("Onboarding key clear for {Provider} superseded by a newer write", provider);
                        return true; // superseded by a newer write, which owns the slot
                    }
                    SetEnhKeyStatus("\uE946", "Key cleared", AppTheme.TextSecondary, true);
                    return true;
                }

                var previousKey = _apiKeys.GetApiKey(providerKey) ?? "";
                if (key == previousKey)
                    return true; // already saved — idempotence, not a rollback

                // ENH-17: claim AFTER the idempotence check (Kimi review). A path that writes
                // nothing must not burn a generation — pressing Continue with an unchanged key
                // would otherwise supersede a genuinely in-flight cross-surface save, and then
                // commit nothing, so the real save is refused and neither write lands.
                var generation = _apiKeys.BeginKeyWrite(providerKey);

                // Format-check before any network call, matching the funnel's rule and its copy.
                var candidateVerdict = Helpers.ApiKeyFormat.Validate(Helpers.ApiKeyFormat.Normalize(key));
                if (candidateVerdict != Helpers.ApiKeyFormatVerdict.Ok)
                {
                    SetEnhKeyStatus("\uE783", Helpers.ApiKeyFormat.Describe(candidateVerdict), AppTheme.AccentRed, true);
                    return false;
                }

                SetEnhKeyStatus("\uE946", "Validating...", AppTheme.TextSecondary, true);

                // ENH-17: nothing is written until validation passes, so there is no temp-save and
                // no rollback. `committed` (declared above the try) keeps the catch-all honest.
                bool isValid = false;
                bool couldNotValidate = false;
                List<string>? fetchedModels = null;
                try
                {
                    var svc = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();
                    // The modality query so the image card validates against (and reuses) the
                    // image catalog, not the chat one — otherwise the image combo would reuse a
                    // chat list and show "No models available".
                    var fetched = await svc.ValidateCandidateKeyAsync(
                        provider, key,
                        isImageModels
                            ? global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Image
                            : global::VoiceWink.Services.AIEnhancement.Providers.ModelCatalogQuery.Text,
                        showAll: false, ct: default);
                    fetchedModels = fetched.Models;
                    // A NON-auth failure (geo-block, outage, timeout) must not read as a bad key —
                    // it previously did, because Error was ignored and RawCount is 0 on failure.
                    // Aligns onboarding with EnhancementViewModel's posture (Codex plan r2).
                    if (fetched.Error != null) { couldNotValidate = true; }
                    else
                    // RawCount, not Models.Count: "the provider returned nothing" is what makes a
                    // key invalid. Judging it on the filtered list lets a filtering rule roll back
                    // a perfectly good key — e.g. a Mistral account whose whole catalog is
                    // curated away would have its key rejected here.
                    isValid = fetched.RawCount > 0;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                           or global::System.Net.HttpStatusCode.Forbidden)
                {
                    isValid = false;
                }
                catch (Exception) { couldNotValidate = true; }

                if (isValid || couldNotValidate)
                {
                    // couldNotValidate keeps its shipped posture — a network failure saves the key
                    // rather than punishing a flaky connection — but it is now a DELIBERATE write
                    // through the same commit check, so a clear during the window still wins.
                    if (!_apiKeys.TryCommitApiKey(providerKey, key, generation, out var commitResult))
                    {
                        // FALSE, not true (Codex ENH-17 verification). This is a wizard GATE: a
                        // superseded save knows nothing about whether the newer key is persisted or
                        // valid, so returning true advanced the user past the step on an unknown
                        // state. The newer save sets its own status when it lands; the user can
                        // press Continue again then.
                        Logger.Information("Onboarding key save for {Provider} superseded by a newer write", provider);
                        return false;
                    }
                    if (!commitResult.IsSuccess())
                    {
                        SetEnhKeyStatus("\uE783", "Could not save the key on this device", AppTheme.AccentRed, true);
                        return false;
                    }
                    committed = true;

                    if (isValid)
                    {
                        SetEnhKeyStatus("\uE73E", "Key valid", AppTheme.AccentGreen, true);
                        await LoadModelsAsync(provider, fetchedModels); // reuse already-fetched models
                    }
                    else
                    {
                        SetEnhKeyStatus("\uE7BA", "Could not validate — key saved", AppTheme.WarningText, true);
                    }
                    return true;
                }

                // Invalid. Nothing was ever written, so there is nothing to restore — the stored
                // key is already whatever it was before this attempt.
                if (!_apiKeys.IsCurrentKeyWrite(providerKey, generation))
                {
                    // ENH-17 (Codex round 3): a stale REJECTED validation must not repaint a row
                    // a newer save already owns. NOT cosmetic like the "Validating..." label:
                    // restoring the previous password can empty the box, and the next Continue
                    // press then takes the CLEAR branch and DELETES the newer save's good key.
                    Logger.Information("Onboarding key rejection for {Provider} superseded", providerKey);
                    return false;
                }
                apiKeyBox.Password = previousKey;
                SetEnhKeyStatus("\uE783",
                    string.IsNullOrEmpty(previousKey) ? "Invalid API key" : "Invalid key rejected — previous key kept",
                    AppTheme.AccentRed, true);
                return false;
            }
            catch (Exception ex)
            {
                // ENH-17: this catch-all used to paint a green "Key saved" and return true. Under
                // write-then-validate that was mostly true, because the temp-save had usually
                // landed. With no temp-save it would be a FAIL-OPEN GATE — any stray throw would
                // wave a first-run user past this step with NO key stored (Kimi, ENH-17 plan
                // review). It now reports what actually happened.
                Logger.Warning(ex, "Onboarding enhancement key validation failed");
                if (committed)
                {
                    SetEnhKeyStatus("\uE7BA", "Key saved — models not loaded", AppTheme.WarningText, true);
                    return true;
                }
                SetEnhKeyStatus("\uE783", "Could not save the key — try again", AppTheme.AccentRed, true);
                return false;
            }
        }

        saveKeyBtn.Tapped += async (_, _) => await SaveAndValidateEnhKeyAsync();

        providerCombo.SelectionChanged += async (_, _) =>
        {
            if (providerCombo.SelectedItem is string name &&
                Enum.TryParse<Services.AIEnhancement.AIProvider>(name, out var p))
            {
                onProviderChanged(p);
                UpdateSignup(p);
                UpdateKeyStatus(p);
                keyDetailsPanel.Visibility = Visibility.Visible;
                // Load models if key already exists
                var providerKey = p.ToString().ToLowerInvariant();
                if (!string.IsNullOrEmpty(_apiKeys.GetApiKey(providerKey)))
                    await LoadModelsAsync(p);
                else
                    modelRow.Visibility = Visibility.Collapsed;
            }
        };

        if (currentProvider.HasValue)
        {
            UpdateSignup(currentProvider.Value);
            UpdateKeyStatus(currentProvider.Value);
            // Load models if key already exists on initial display
            var providerKey = currentProvider.Value.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(_apiKeys.GetApiKey(providerKey)))
                _ = LoadModelsAsync(currentProvider.Value);
        }

        // Return a validator: saves any entered key; an empty key proceeds immediately (no confirm).
        return async () =>
        {
            if (providerCombo.SelectedItem is not string selectedName)
                return true;

            if (!Enum.TryParse<Services.AIEnhancement.AIProvider>(selectedName, out var provider))
                return true;

            var pKey = provider.ToString().ToLowerInvariant();

            // Empty key + none saved: proceed immediately (no confirm). Adding a key later is a Settings task.
            if (string.IsNullOrWhiteSpace(apiKeyBox.Password) && !_apiKeys.HasApiKey(pKey))
                return true;

            return await SaveAndValidateEnhKeyAsync();
        };
    }

    // ────────────────────────────────────────────────────────────────
    // Step 7: Hotkeys
    // ────────────────────────────────────────────────────────────────

    private void BuildHotkeyStep()
    {
        // Only read from settings on first visit; preserve user's in-session choice on Back/Forward
        // HKY-4: an ABSENT setting means a first run that never chose — the one case that may seed
        // a layout-aware default (RightControl where RightAlt is AltGr). A PRESENT value passes
        // through verbatim, so an existing install — wizard relaunches included — is untouched:
        // completion always persists the selection, so anyone who finished the wizard has one.
        _selectedHotkey ??= HotkeyDefaultSeed.Resolve(
            _settings.Contains(AppDefaults.HotkeyModifier)
                ? _settings.GetString(AppDefaults.HotkeyModifier, "RightAlt")
                : null,
            KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping());
        _selectedPasteLastHotkey ??= _settings.GetString(AppDefaults.PasteLastHotkeyModifier, "");
        _selectedRedoLastHotkey ??= _settings.GetString(AppDefaults.RedoLastHotkeyModifier, "");

        AddStepHeader("Choose Your Hotkeys",
            "These keys control recording, pasting, and redo. You can change them later in Settings.");

        // ── Warning text for conflict-prone keys (declared before selectors so lambdas can capture) ──
        var warningText = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var warningWrapper = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Child = warningText,
            Visibility = Visibility.Collapsed
        };

        // Parses the binding rather than substring-matching — the same fix SettingsPage's warning
        // took in diff round 3, applied here in round 6. The whole rule lives in HotkeyWarnings:
        // the bare-modifier switch since HKY-4, the shadow-vs-bare-key branch around it since
        // HKY-8 (this page and SettingsPage carried two hand-maintained copies, and the copy edit
        // that made them diverge is what forced the first extraction; the second is what stops a
        // wizard-bound Ctrl+C going unremarked now that the wizard can create one). The AltGr probe
        // result is captured once per step build — the NARROW essential-characters question, not
        // the hook's broad one (US-International must not warn).
        var anyAltGrLayout = KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping();
        string? GetKeyWarning(string? key)
            => HotkeyBinding.TryParse(key, out var binding, out _)
                ? HotkeyWarnings.BindingWarning(binding, anyAltGrLayout)
                : null;

        // Set when the recording row REFUSES a choice (see its handler), cleared the moment it
        // accepts one. It rides the same amber block as the advisories because the alternative —
        // the row silently snapping back to its previous value — is a defect the owner has already
        // reported once, against the Settings modifier picker: "the control appeared broken"
        // (HotkeyBindingEditor's remarks). The recording row has no "None" to fall back to, so its
        // revert is otherwise indistinguishable from the picker ignoring the click.
        string? refusalNote = null;

        // The row labels, named once because each is used twice — as the editor's title and inside
        // that row's HKY-10 note.
        const string recordingTitle = "Recording hotkey";
        const string pasteLastTitle = "Paste last";
        const string redoLastTitle = "Redo last";

        // HKY-10: one per row, set by that row's IncompleteChanged handler while its pickers hold a
        // modifier with no key. Captured locals for the same reason refusalNote is one — UpdateWarning
        // is declared BEFORE the editors, so it cannot reach them.
        string? recordingIncomplete = null;
        string? pasteLastIncomplete = null;
        string? redoLastIncomplete = null;

        void UpdateWarning()
        {
            // Action notes first — they explain something the user just did — then the standing
            // hazards. The HKY-10 notes join the refusal in that first group rather than REPLACING
            // the hazards: while a row is incomplete its stored binding is untouched, so whatever
            // hazard that binding carries is still live and still has to be shown.
            var lines = new List<string?>();
            if (refusalNote != null) lines.Add(refusalNote);
            foreach (var note in new[] { recordingIncomplete, pasteLastIncomplete, redoLastIncomplete })
            {
                if (note != null) lines.Add(note);
            }

            lines.AddRange(new[] { _selectedHotkey, _selectedPasteLastHotkey, _selectedRedoLastHotkey }
                .Select(GetKeyWarning)
                .Where(w => w != null)
                .Distinct());

            if (lines.Count > 0)
            {
                warningText.Text = string.Join("\n", lines);
                warningWrapper.Visibility = Visibility.Visible;
            }
            else
            {
                warningWrapper.Visibility = Visibility.Collapsed;
            }
        }

        // ── Hotkey editors with conflict detection ──
        //
        // HKY-8 (2026-08-31): the same HotkeyBindingEditor the Settings rows use — modifier picker
        // plus key picker — replacing this page's own single-key ComboBox. The owner's call:
        // "Obviously, the onboarding wizard should have the same options as settings." That reverses
        // HKY-3's deliberate "onboarding stays a SINGLE-KEY surface" scope line, so the comment that
        // stated it is gone rather than softened. Reuse, not a second picker: the rules that make an
        // invalid pair unconstructible (typing keys need Ctrl, modifier keys vanish once a modifier
        // is selected, a partial edit never publishes) are already case-tabled against that control,
        // and a parallel implementation would have to re-derive every one of them.
        //
        // The pickers STACK here because _contentPanel caps at 500 px: side by side they would leave
        // ~150 px for the title and description. That is the only difference from Settings.
        //
        // The secondary rows are CONSTRUCTED first so the recording handler can capture them as
        // non-null locals; they are ADDED to the panel in display order below. The old code needed
        // forward-declared nullable fields only because its closures captured raw ComboBoxes.

        // The wizard edits only three roles, but it must SEE every binding that exists — including
        // the generate-image hotkey it never shows and the prompt hotkeys it cannot edit. The
        // hand-written list this replaces knew about neither, so onboarding could hand paste-last a
        // key that silently killed one of them (Codex diff rounds 4 and 5). The snapshot is built
        // from the live settings, so no screen carries its own idea of which hotkeys exist.
        HotkeyBindingSnapshot ConflictSnapshot() =>
            HotkeyBindingSnapshot.FromSettings(_settings.GetString, HotkeyPrompts.Read())
                .With(HotkeyRole.Recording, _selectedHotkey)
                .With(HotkeyRole.PasteLast, _selectedPasteLastHotkey)
                .With(HotkeyRole.RedoLast, _selectedRedoLastHotkey);

        bool IsConflict(string candidate, HotkeyRole editing)
            => HotkeyConflictScan.ForRole(ConflictSnapshot(), candidate, editing).Count > 0;

        // Paste Last — revert to None on conflict
        var pasteLastEditor = new HotkeyBindingEditor(
            pasteLastTitle, includeNone: true, _selectedPasteLastHotkey!,
            description: "Paste the last transcription again", stackPickers: true);
        pasteLastEditor.IncompleteChanged += () =>
        {
            pasteLastIncomplete = pasteLastEditor.IsIncomplete
                ? HotkeyWarnings.IncompleteBindingNote(pasteLastTitle, _selectedPasteLastHotkey)
                : null;
            UpdateWarning();
        };
        pasteLastEditor.ValueChanged += selected =>
        {
            // THREE things move together on every revert, and dropping any one of them is the
            // defect class HKY-3's review found twice in this control: the field this step
            // PERSISTS at completion, what the row SHOWS, and the advisory that DESCRIBES it.
            // SetValue is deliberately silent — re-entering our own handler would loop — so unlike
            // the raw ComboBox this replaces, nothing recomputes the other two for us.
            if (IsConflict(selected, HotkeyRole.PasteLast))
            {
                _selectedPasteLastHotkey = "";
                pasteLastEditor.SetValue("");
                UpdateWarning();
                return;
            }
            _selectedPasteLastHotkey = selected;
            UpdateWarning();
        };

        // Redo Last — revert to None on conflict
        var redoLastEditor = new HotkeyBindingEditor(
            redoLastTitle, includeNone: true, _selectedRedoLastHotkey!,
            description: "Re-enhance with a different AI model", stackPickers: true);
        redoLastEditor.IncompleteChanged += () =>
        {
            redoLastIncomplete = redoLastEditor.IsIncomplete
                ? HotkeyWarnings.IncompleteBindingNote(redoLastTitle, _selectedRedoLastHotkey)
                : null;
            UpdateWarning();
        };
        redoLastEditor.ValueChanged += selected =>
        {
            if (IsConflict(selected, HotkeyRole.RedoLast))
            {
                _selectedRedoLastHotkey = "";
                redoLastEditor.SetValue("");
                UpdateWarning();
                return;
            }
            _selectedRedoLastHotkey = selected;
            UpdateWarning();
        };

        // Recording hotkey wins — clears conflicting secondary hotkeys
        var recordingEditor = new HotkeyBindingEditor(
            recordingTitle, includeNone: false, _selectedHotkey!,
            description: "Quick tap = hands-free • Hold = push-to-talk", stackPickers: true);
        recordingEditor.IncompleteChanged += () =>
        {
            // This is the row Codex hit: Ctrl + C, modifier to "No modifier", key box blanks, and
            // the amber shadow-shortcut advisory stayed up describing a chord the box no longer
            // showed. It is still the chord this wizard will SAVE — nothing was written — so that
            // advisory is right, and this line is what makes the screen agree with it. (The
            // advisory's own words are deliberately not quoted here: TheAdvisory_ComesFromHotkeyWarnings
            // forbids this step carrying a local copy of them, and a comment is indistinguishable
            // from one to a source contract.)
            recordingIncomplete = recordingEditor.IsIncomplete
                ? HotkeyWarnings.IncompleteBindingNote(recordingTitle, _selectedHotkey)
                : null;
            UpdateWarning();
        };
        recordingEditor.ValueChanged += selected =>
        {
            // Recording wins over the two rows this step SHOWS — but it cannot clear what it does
            // not show, and until HKY-8 it never had to. A single-key recording binding always WINS
            // the hook's press order, so the old wizard's worst case was killing someone else's
            // hotkey. A COMBO can LOSE: a bare modifier bound elsewhere starts its own gesture on
            // the modifier key-down and the active-gesture guard then eats the trigger
            // (`HotkeyBinding.BareModifierBlocksCombo`). So a user who binds Generate image to bare
            // Left Ctrl in Settings, relaunches the wizard and picks Ctrl + Space would have walked
            // out of a completed wizard with RECORDING ITSELF inert — no warning, no revert (Kimi
            // diff review r1; the sibling rows already asked the scan, this row predated it).
            //
            // Refuse rather than clear: the colliding roles here are ones the wizard never shows,
            // and Settings deliberately CONFIRMS before clearing a role rather than doing it
            // silently. Secondary-role collisions are filtered out so "recording wins" below still
            // works for the two rows this step does own.
            if (!string.IsNullOrEmpty(selected))
            {
                var blocked = HotkeyConflictScan
                    .ForRole(ConflictSnapshot(), selected, HotkeyRole.Recording)
                    .Where(c => c.Role is null or HotkeyRole.GenerateImage)
                    .ToList();
                if (blocked.Count > 0)
                {
                    // _selectedHotkey still holds the previous value — the assignment below is what
                    // commits a choice — so this restores the field, the display and Value together.
                    refusalNote =
                        $"{HotkeyKeyDisplay.Describe(selected)} can't be used: it clashes with "
                        + $"{string.Join(" and ", blocked.Select(c => c.Describe()).Distinct())}. "
                        + $"Keeping {HotkeyKeyDisplay.Describe(_selectedHotkey)}.";
                    recordingEditor.SetValue(_selectedHotkey);
                    UpdateWarning();
                    return;
                }
            }

            refusalNote = null;
            _selectedHotkey = selected;
            // Recording wins — clear any conflicting secondary hotkey
            if (!string.IsNullOrEmpty(selected))
            {
                if (HotkeyBinding.Conflicts(selected, _selectedPasteLastHotkey))
                {
                    _selectedPasteLastHotkey = "";
                    pasteLastEditor.SetValue("");
                }
                if (HotkeyBinding.Conflicts(selected, _selectedRedoLastHotkey))
                {
                    _selectedRedoLastHotkey = "";
                    redoLastEditor.SetValue("");
                }
            }
            UpdateWarning();
        };

        // Constructed secondary-first so the recording handler captures them as non-null locals;
        // ADDED to the panel in display order.
        _contentPanel.Children.Add(recordingEditor.Root);
        _contentPanel.Children.Add(pasteLastEditor.Root);
        _contentPanel.Children.Add(redoLastEditor.Root);

        UpdateWarning();
        _contentPanel.Children.Add(warningWrapper);

        // Instant recording (AUD-6) is ON by default and the wizard never mentioned it (launch
        // defaults audit, 2026-09-13) — so the first thing a new user noticed after setup was the
        // Windows microphone-in-use indicator lit for good, with nothing having said why. This is
        // the step about the recording key, so the sentence sits under it; the wording follows
        // the Settings → Audio toggle's, which is where the switch lives. Read from the setting,
        // not static: a relaunched wizard on an install that switched it off must not assert it
        // is on (self-review, copy lens) — with it off there is nothing to explain, so no line.
        if (_settings.GetBoolDefaulted(AppDefaults.InstantRecordingEnabled))
        {
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "Instant recording is on: VoiceWink keeps the microphone active so recording " +
                       "starts the moment you press the hotkey, and Windows shows its microphone-in-use " +
                       "indicator while VoiceWink runs. You can turn this off in Settings → Audio.",
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            });
        }

        var hotkeyNextBtn = AppTheme.CreateAccentButton("Continue");
        hotkeyNextBtn.Tapped += (_, _) => ShowStep(8);
        AddButtonRow(CreateBackButton(), hotkeyNextBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 8: Clipboard & Paste Behavior
    // ────────────────────────────────────────────────────────────────

    private void BuildClipboardStep()
    {
        AddStepHeader("Clipboard & Paste Behavior",
            "Control how VoiceWink interacts with your clipboard. You can change these later in Settings.");

        // ── Restore Clipboard After Paste ──
        var restoreClipboard = _settings.GetBool(AppDefaults.RestoreClipboardAfterPaste, false);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Restore Clipboard After Paste",
            "After pasting text, the original clipboard contents are restored. Does not apply to generated images (they stay on the clipboard for you to paste manually).",
            restoreClipboard,
            isOn => _settings.SetBool(AppDefaults.RestoreClipboardAfterPaste, isOn)));

        // ── Send Enter After Paste ──
        var sendEnter = _settings.GetBoolDefaulted(AppDefaults.SendEnterAfterPaste);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Send Enter after paste",
            // Says the consequence, not just the keystroke (launch defaults audit, 2026-09-13): in a
            // chat app, Enter SENDS — a user who reads "press Enter" as "new line" finds out by
            // sending a half-finished message.
            "Automatically press Enter after pasting text (push-to-talk only) — in chat apps like Teams or Slack this sends the message immediately. Does not apply to images.",
            sendEnter,
            isOn => _settings.SetBool(AppDefaults.SendEnterAfterPaste, isOn)));

        var clipNextBtn = AppTheme.CreateAccentButton("Continue");
        clipNextBtn.Tapped += (_, _) => ShowStep(9);
        AddButtonRow(CreateBackButton(), clipNextBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 9: Startup Behavior
    // ────────────────────────────────────────────────────────────────

    private void BuildStartupStep()
    {
        // Subtitle covers minimizing too since ONB-3 added that toggle — the screen's job is how
        // VoiceWink behaves around its window, not only how it starts.
        AddStepHeader("Startup Behavior",
            "Choose how VoiceWink starts and what happens when you minimize its window.");

        // ── Launch at login ──
        var launchAtLogin = _settings.GetBool(AppDefaults.LaunchAtLogin, true);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Launch at login",
            "Start VoiceWink automatically when you sign in to Windows.",
            launchAtLogin,
            isOn => _settings.SetBool(AppDefaults.LaunchAtLogin, isOn)));

        // ── Start minimized ──
        var startMinimized = _settings.GetBool(AppDefaults.StartMinimized, true);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Start minimized",
            "Start minimized to the system tray instead of showing the main window. VoiceWink is always available via the hotkey and tray icon.",
            startMinimized,
            isOn => _settings.SetBool(AppDefaults.StartMinimized, isOn)));

        // ── Minimize to tray (ONB-3) ──
        // Shipped in PR #227 but lived only on the Settings page, so the one screen whose job is
        // "how does VoiceWink behave around its window" was missing a third of the answer.
        // GetBoolDefaulted, not GetBool with a literal: it is the seam built for preferences that
        // have an AppDefaults entry, so two call sites cannot disagree on a corrupt settings file.
        // String is character-identical to the Settings toggle's — one option, one description.
        var minimizeToTray = _settings.GetBoolDefaulted(AppDefaults.MinimizeToTray);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Minimize to tray",
            "When you minimize the window, hide it to the system tray instead of the taskbar",
            minimizeToTray,
            isOn => _settings.SetBool(AppDefaults.MinimizeToTray, isOn)));

        // Label is "Continue" (not "Finish Setup") because LIC-2 inserted License +
        // Crash-Reporting steps after this one — this is no longer the last step.
        var continueBtn = AppTheme.CreateAccentButton("Continue");
        continueBtn.Tapped += (_, _) => ShowStep(10);
        AddButtonRow(CreateBackButton(), continueBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Step 10: License (LIC-2; redesigned 2026-09-03 as two fixed-slot cards)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The License step is TWO cards in two FIXED slots (2026-09-03 redesign; research basis in
    /// <c>docs/plans/2026-09-03-onb-license-step-v2/50-decision.md</c>). Slot 1 is the free trial —
    /// the PRIMARY action, because the fastest path to a first dictation is what converts. Since
    /// LIC-21 (owner decision 2026-09-06) it is the ONLY try path: the Lemon Squeezy trial key, which
    /// stayed reachable from slot 1 in every variant until then, is retired — a browser + email round
    /// trip in front of any value that the owner found confusing beside the key-free window three
    /// times over. Slot 2 is key entry for a purchased key, with "Buy a license" as a secondary
    /// button that becomes the step's accent once the trial has ended. A state change re-renders
    /// slot 1 IN PLACE (<see cref="OnboardingLicenseCard.Resolve"/>): coming Back from the next step
    /// after starting the trial must not insert, remove or reorder cards, and the header subtitle
    /// stays the same — a layout that moves cannot be learned (spatial memory; owner UAT 2026-09-03
    /// on the three-card layout that did exactly that).
    ///
    /// <para>The trial's length is never typed here: <see cref="LicenseViewModel.TryoutLengthDescription"/>
    /// derives it from <c>LicenseService.FirstRunGraceDuration</c> ("7 days" since LIC-21 PR A) and
    /// is null above the 60-day ceiling, in which case the sentence simply carries no length —
    /// never a false number.</para>
    ///
    /// <para><b>The key-free start is offered ONLY to a machine with no key at all</b>
    /// (<see cref="LicenseStatus.Unlicensed"/>) — the resolver's <c>StoredKey</c> arm carries the
    /// rationale: a stored-key status reached via Settings → "Relaunch setup wizard" would burn a
    /// trial the user never received. Every Continue on this step targets step 11 (Crash Reporting
    /// consent). A trial that has ENDED still continues — the wizard is not a licence gate; what the
    /// user may record is decided by <c>IsRecordingBlocked</c>, never this screen (Codex plan round,
    /// 2026-09-03).</para>
    /// </summary>
    private void BuildLicenseStep()
    {
        // Reuse LicenseViewModel so onboarding gets the same activation / trial / buy
        // commands (including empty-key guards, error messages, busy gate, status
        // preservation on failure) as the License page, without duplicating logic.
        // VM is transient in DI — each entry to this step creates a fresh one with
        // Status seeded from GetCachedStatus.
        var vm = App.Services.GetRequiredService<LicenseViewModel>();

        // Early-return skip screen for already-activated users.
        if (vm.Status == LicenseStatus.Activated)
        {
            AddStepHeader("Start using VoiceWink");
            _contentPanel.Children.Add(new TextBlock
            {
                Text = "This machine is already activated. You can skip to the next step.",
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            });
            var skipBtn = AppTheme.CreateAccentButton("Continue");
            skipBtn.Tapped += (_, _) => ShowStep(11);
            AddButtonRow(CreateBackButton(), skipBtn);
            return;
        }

        // Which slot-1 variant this entry renders — a pure, test-pinned decision (see the class doc
        // for why a stored-key status must never see the key-free start).
        var variant = OnboardingLicenseCard.Resolve(vm.Status, vm.TryoutEnded);

        // The header is STATIC for every variant: Start ↔ TryoutActive is the Back case and nothing
        // above the cards may change, and the state a machine is in belongs in slot 1, not in a
        // subtitle that would repeat the card beneath it (self-review lens B).
        AddStepHeader("Start using VoiceWink", "Every way in unlocks all features.");

        var trialLength = LicenseViewModel.TryoutLengthDescription;

        // ── Slot 1: the free-trial / status card, rendered in place per variant ──
        Border slot1;
        switch (variant)
        {
            case OnboardingLicenseCardVariant.TryoutActive:
            {
                var (card, content) = CreateLicenseOptionCard(
                    vm.TrialTimeRemainingDisplay,
                    "All features are unlocked. Activate a purchased key before the trial ends to keep recording.");
                var continueBtn = AppTheme.CreateAccentButton("Continue");
                continueBtn.HorizontalAlignment = HorizontalAlignment.Left;
                continueBtn.Tapped += (_, _) => ShowStep(11);
                content.Children.Add(continueBtn);
                slot1 = card;
                break;
            }
            case OnboardingLicenseCardVariant.TryoutEnded:
            {
                // The trial is spent, so Buy becomes the step's accent — in slot 2, beside the key
                // box, where the action lives; this card only says what happened. Continue below
                // still finishes the wizard — the recording gate is IsRecordingBlocked, never this
                // screen.
                var (card, _) = CreateLicenseOptionCard(
                    "Your free trial has ended on this device.",
                    "Enter a purchased license key below, or buy one.",
                    titleColor: AppTheme.WarningText); // 14 px text on the card: the accent reads 2.2:1 in the light theme (LIC-20)
                slot1 = card;
                break;
            }
            case OnboardingLicenseCardVariant.StoredKey:
            default:
            {
                // `default` shares the stored-key body so this switch fails the same way the
                // resolver does — toward the one variant that can never start a trial — rather
                // than toward the start card (self-review lens A, 2026-09-03).
                var (card, _) = CreateLicenseOptionCard(
                    "This machine already has a license key.",
                    "The License page shows its status. Enter a different key below, or buy a license.");
                slot1 = card;
                break;
            }
            case OnboardingLicenseCardVariant.Start:
            {
                // The body ends at "nothing to enter." (owner decision 2026-09-07, LIC-25): the
                // "When it ends, enter a license key or buy one." sentence that used to follow said
                // what the key card beneath already offers. The derived length ("for 7 days") is what
                // bounds the offer; the no-length branch is unreachable with the shipped 7-day window
                // and stays as the defence for a window above TryoutWindowCopy's ceiling.
                var (card, content) = CreateLicenseOptionCard(
                    "Free trial — no key needed",
                    trialLength is null
                        ? "Use everything free — no key, no email, nothing to enter."
                        : $"Use everything free for {trialLength} — no key, no email, nothing to enter.");
                var trialStatus = CreateLicenseStatusLine();
                var trialBtn = AppTheme.CreateAccentButton("Start free trial");
                trialBtn.HorizontalAlignment = HorizontalAlignment.Left;
                trialBtn.Tapped += (_, _) =>
                {
                    // Re-resolve at click time. A key activated in slot 2 moments ago can leave this
                    // card on screen while the machine now holds a STORED key — an expired key
                    // activates to Invalid, and an armed manifest's first activation lands on
                    // GraceExpired — and StartFirstRunGrace would then burn the trial on a machine
                    // that must never receive it. Re-render for the state the machine is actually in
                    // instead; step 10 is never cached, so ShowStep rebuilds it (self-review lens A).
                    //
                    // Resolve from the SERVICE's cached status, not the transient VM's: a FAILED
                    // activation from Unlicensed adopts the service's Invalid signal into the VM (LIC-1,
                    // nothing persisted), and reading that here as a stored key re-rendered the step
                    // and made the trial start take two clicks (owner UAT 2026-09-06, Phase A). Only
                    // a real stored key changes what the service answers.
                    vm.ReconcileStatusFromCache();
                    if (OnboardingLicenseCard.Resolve(vm.Status, vm.TryoutEnded) != OnboardingLicenseCardVariant.Start)
                    {
                        ShowStep(10);
                        return;
                    }
                    vm.StartTrialCommand.Execute(null);
                    if (vm.Status == LicenseStatus.FirstRunGrace)
                    {
                        ShowStep(11);
                        return;
                    }
                    // Surface the VM's specific message when set (a trial that ended between entry
                    // and the click leaves the status Unlicensed and explains itself) rather than
                    // overwriting it with generic copy; the generic line is only for a failure
                    // StartTrial did not classify. OutcomeMessage puts a possibly-consumed seat ahead
                    // of a clean refusal — this line is the wizard's only surface for either (LIC-19).
                    // A click turned away by the busy gate (an activation from slot 2 still in
                    // flight) leaves no outcome and writes the busy notice on the activity line, so
                    // that is the second fallback: the user must see WHY the trial did not start.
                    ShowLicenseStatus(trialStatus,
                        vm.OutcomeMessage ?? vm.ActivityMessage ?? "Couldn't start the free trial — please try again, or enter a license key.",
                        AppTheme.WarningText);
                };
                content.Children.Add(trialBtn);
                content.Children.Add(trialStatus);
                slot1 = card;
                break;
            }
        }

        // ── Slot 2: I have a key ──
        var (keyCard, keyContent) = CreateLicenseOptionCard(
            "I have a key",
            "Paste your license key.");
        // Key entry — writes through to vm.LicenseKeyInput, which the Activate command reads. The
        // placeholder is the stored key, masked, when one is stored — the License page's rule
        // (LIC-28), so the two key boxes agree; the Text stays whatever the user types.
        var keyBox = new TextBox
        {
            PlaceholderText = LicenseKeyBoxPlaceholder.Resolve(vm.MaskedKey),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new FontFamily("Consolas"),
        };
        keyBox.TextChanged += (_, _) => vm.LicenseKeyInput = keyBox.Text;
        var keyStatus = CreateLicenseStatusLine();
        // Exactly one control leads the step. Buy is the accent the moment the free path is gone
        // and value has been felt — the trial ended on this device, or the stored key expired —
        // which are the same two facts LicensePanelPlan promotes Buy on for the License page
        // (its used-up-trial key box and its Invalid panel's expired-key branch), so the wizard and
        // the page never disagree about the loudest control for one state. LIC-21; until then the
        // trial KEY took the ended-trial slot. Everywhere else Activate leads and Buy is quiet: the
        // research basis puts the purchase prompt after felt value.
        var buyLeads = variant == OnboardingLicenseCardVariant.TryoutEnded
            || (vm.Status == LicenseStatus.Invalid && vm.StoredKeyExpired);
        var activateBtn = buyLeads
            ? AppTheme.CreateSecondaryButton("Activate")
            : AppTheme.CreateAccentButton("Activate");
        activateBtn.Tapped += async (_, _) =>
        {
            AppTheme.SetButtonEnabled(activateBtn, false, "Activating...");
            try
            {
                await vm.ActivateCommand.ExecuteAsync(null);
                if (vm.Status == LicenseStatus.Activated)
                {
                    ShowStep(11);
                    return;
                }
                // OutcomeMessage: a possibly-consumed seat ahead of a clean refusal (LIC-19).
                ShowLicenseStatus(keyStatus, vm.OutcomeMessage ?? "Activation failed.", AppTheme.WarningText);
            }
            finally
            {
                AppTheme.SetButtonEnabled(activateBtn, true, "Activate");
            }
        };
        // Buy is a real button (the same control the License page uses); see `buyLeads` above for
        // when it is the accent.
        var buyBtn = buyLeads
            ? AppTheme.CreateAccentButton("Buy a license", (_, _) => vm.BuyCommand.Execute(null))
            : AppTheme.CreateSecondaryButton("Buy a license", (_, _) => vm.BuyCommand.Execute(null));
        ToolTipService.SetToolTip(buyBtn, VoiceWinkUrls.Buy);
        // The License page's key-form shape (LIC-19): a full-width key box, then ONE row holding both
        // actions, then the status line, then the recovery link. Activate used to sit beside the box
        // with Buy alone under the link, so the two actions read as unrelated and the status line had
        // only the width left beside the button — which wrapped the key-not-found refusal onto two
        // lines and pushed the step past the viewport (owner UAT 2026-09-06, Phase A). A FlowPanel so
        // a narrow window wraps the row instead of clipping it.
        var actionRow = new FlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        actionRow.Children.Add(activateBtn);
        actionRow.Children.Add(buyBtn);
        keyContent.Children.Add(keyBox);
        keyContent.Children.Add(actionRow);
        keyContent.Children.Add(keyStatus);
        // Tooltips carry the URLs because OpenUrl is fail-soft — a blocked browser launch would
        // otherwise leave the user with no idea where the link meant to go (the License page's
        // lost-key link does the same).
        keyContent.Children.Add(AppTheme.CreateActionLink("Lost your key? Recover it from your Lemon Squeezy orders.", () => vm.ForgotKeyCommand.Execute(null), VoiceWinkUrls.LostKey));

        // Two slots, fixed order, whatever the variant.
        _contentPanel.Children.Add(slot1);
        _contentPanel.Children.Add(keyCard);

        // Back only while slot 1 carries the forward action (Start, TryoutActive). A stored-key
        // machine and an ended trial get Continue as well: the wizard's remaining steps are harmless
        // to finish, and what the user may or may not record is decided by IsRecordingBlocked, never
        // by this screen (Codex plan round, 2026-09-03: a wizard that will not advance is a licence
        // gate in disguise).
        if (variant is OnboardingLicenseCardVariant.StoredKey or OnboardingLicenseCardVariant.TryoutEnded)
        {
            // Whenever Buy leads (ended trial, or an expired stored key — `buyLeads` is the same
            // predicate as slot 2's Buy/Activate weighting) Continue is secondary, otherwise the
            // loudest control on the page is the skip (self-review lens B; the expired-key arm was
            // missed at first and Grok's diff round caught the second accent).
            var continueBtn = buyLeads
                ? AppTheme.CreateSecondaryButton("Continue")
                : AppTheme.CreateAccentButton("Continue");
            continueBtn.Tapped += (_, _) => ShowStep(11);
            AddButtonRow(CreateBackButton(), continueBtn);
        }
        else
        {
            AddButtonRow(CreateBackButton());
        }
    }

    /// <summary>
    /// One option card of the License step — a title, a one-sentence body, and whatever the caller
    /// appends to the returned content panel (a button, the key row, a status line).
    /// </summary>
    private static (Border Card, StackPanel Content) CreateLicenseOptionCard(string title, string body, Windows.UI.Color? titleColor = null)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(titleColor ?? AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
        });
        return (AppTheme.CreateCard(content, padding: 16), content);
    }

    /// <summary>A per-card status line, collapsed until <see cref="ShowLicenseStatus"/> fills it.</summary>
    private static TextBlock CreateLicenseStatusLine() => new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private static void ShowLicenseStatus(TextBlock line, string text, Windows.UI.Color color)
    {
        line.Text = text;
        line.Foreground = AppTheme.Brush(color);
        line.Visibility = Visibility.Visible;
    }

    // ────────────────────────────────────────────────────────────────
    // Step 11: Crash Reporting consent (REL-1a)
    // ────────────────────────────────────────────────────────────────

    private void BuildCrashReportingStep()
    {
        AddStepHeader("Help improve VoiceWink",
            "Send anonymous crash reports so bugs get fixed before they affect more users, " +
            "and keep a full log of your prompts on this PC so you can attach it to a problem report. Your choice.");

        _contentPanel.Children.Add(new TextBlock
        {
            // REL-30: this list is CLOSED and it is the CONSENT surface — the Article 6(1)(a)
            // consent the privacy policy relies on is given here, so a category added to
            // privacy-v5 §2 must be added here in the same change, or the user consents to a
            // list that is false by omission (self-review, privacy lens, Blocker).
            //
            // MATCH THE POLICY'S WORDING, not merely its categories — the plural is
            // load-bearing and this line got it wrong once (Grok + Kimi diff r1, same Blocker
            // independently). DisplayDriverSignature enumerates EVERY display adapter (cap 16),
            // so a SINGULAR adapter/driver phrase describes less than what is sent on any
            // iGPU+dGPU laptop, and consent obtained is then narrower than the processing it
            // legitimises. Diff this string against privacy-v5 §2 verbatim.
            //
            // CrashReportConsentCopyTests pins all of this, incl. refusing the singular form by
            // name across this whole file — which is why the rejected wording is described here
            // rather than quoted.
            Text = "What's sent: stack traces, app version, OS version, your graphics adapter models and display driver versions, redacted log breadcrumbs (last 50 entries). No transcriptions, no API keys, no license keys, no machine hostname.",
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _contentPanel.Children.Add(new TextBlock
        {
            // The second half of what "Yes" records (owner decision 2026-09-13): the RAW prompt
            // trace, which never leaves the PC by itself. Named here because this step is the
            // consent surface for it now — privacy-v5 §2 calls it "the optional prompt-trace
            // logging (off by default)", and the wizard's Yes is what switches it on. The redacted
            // summary sentence is a statement of fact, not a consent: that file is always kept.
            // PromptTraceAlwaysKeptTests pins the file name and the retention.
            Text = HelpImproveKeptLocallyText,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        _contentPanel.Children.Add(new TextBlock
        {
            Text = "You can change either any time in Settings → Diagnostics.",
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var yesBtn = AppTheme.CreateAccentButton("Yes, help improve");
        yesBtn.Tapped += (_, _) =>
        {
            ApplyHelpImproveConsent(requested: true);
            ShowStep(12);
        };

        var noBtn = AppTheme.CreateSecondaryButton("No thanks");
        noBtn.Tapped += (_, _) =>
        {
            ApplyHelpImproveConsent(requested: false);
            ShowStep(12);
        };

        AddButtonRow(CreateBackButton(), yesBtn, noBtn);
    }

    /// <summary>
    /// What "Yes, help improve" keeps ON THIS PC (owner decision 2026-09-13): the raw prompt
    /// trace. One const so the wizard's consent surface and its copy test read the same words.
    /// </summary>
    internal const string HelpImproveKeptLocallyText =
        "Kept on this PC, never sent by itself: a full log of your prompts, the text you dictate and " +
        "the AI results (prompts-*.log), deleted after 7 days. It leaves your PC only if you attach it " +
        "to a problem report. A redacted summary with the text replaced by its length is always kept.";

    /// <summary>
    /// The "Help improve VoiceWink" answer, applied to BOTH consents it covers (owner decision
    /// 2026-09-13): crash reporting the way the Settings toggle applies it
    /// (<see cref="CrashReportingConsent"/> — a DURABLE write that fails OFF, the SDK started or
    /// stopped to match, the Sentry log sink attached or detached in THIS session), and the RAW
    /// prompt-trace opt-in through the same durable fail-OFF rule (<see cref="DiagnosticsConsent"/>).
    /// The two writes are INDEPENDENT — each in its own try, so a failure in one never skips the
    /// other (Codex plan round) — and the image-prompt sub-option is deliberately untouched: it
    /// stays the separate Settings-only opt-in REL-21 made it. Never advance-blocking: the step
    /// moves on whatever happened, because Settings → Diagnostics remains available for a
    /// re-confirm on a healthier day, and a choice that could not be saved is OFF — the
    /// privacy-safe direction.
    /// </summary>
    private void ApplyHelpImproveConsent(bool requested)
    {
        try
        {
            var result = CrashReportingConsent.Apply(_settings, requested, App.ReattachLogSinks);
            if (!result.Persisted)
                Logger.Warning("Could not persist crash reporting consent ({Requested}) during onboarding; forced to {Effective}",
                    requested, result.Effective);
        }
        catch (Exception ex)
        {
            // Apply is fail-safe internally; this catches a settings service that throws on the
            // in-memory write itself, or a type-init failure bubbling out of the SDK.
            Logger.Warning(ex, "Applying crash reporting consent ({Requested}) failed during onboarding", requested);
        }

        try
        {
            var result = DiagnosticsConsent.Apply(_settings, AppDefaults.PromptTraceLoggingOptIn, requested);
            if (!result.Persisted)
                Logger.Warning("Could not persist the prompt-log consent ({Requested}) during onboarding; forced to {Effective}",
                    requested, result.Effective);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Applying the prompt-log consent ({Requested}) failed during onboarding", requested);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Step 12: Updates (UPD-4b)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The update choices, offered at setup so every user decides rather than inheriting a
    /// default silently (owner decision 2026-08-08). Sits directly after Crash Reporting: both
    /// screens are about what VoiceWink does over the network on its own.
    ///
    /// <para>Both seeds use <c>GetBoolDefaulted</c>, never a literal — the ONB-3 rule, so the
    /// wizard and the Settings → Updates page cannot disagree about what the shipped default is,
    /// and a corrupt settings file resolves identically on both.</para>
    ///
    /// <para>Both writes go through <see cref="DiagnosticsConsent.Apply"/>, so a preference that
    /// cannot reach disk ends up OFF and SAYS so, instead of leaving a switch showing a state the
    /// app is not in. That matters more here than on the Settings page: this is the one moment the
    /// user is explicitly asked, and a silently-lost answer is the answer they will assume stuck.</para>
    /// </summary>
    private void BuildUpdatesStep()
    {
        AddStepHeader("Updates",
            "Choose how VoiceWink keeps itself up to date. You can change both of these later in Updates.");

        var warning = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };

        // Declared before the callbacks so each can correct the OTHER control; assigned by the
        // out-parameters below, which run before any callback can (a callback needs a user tap).
        ToggleSwitch? checkSwitch = null;
        ToggleSwitch? installSwitch = null;

        // ── Check for updates automatically ──
        var checkOn = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Check for updates automatically",
            "Automatically check for new VoiceWink versions in the background.",
            checkOn,
            isOn =>
            {
                // No scheduler notify HERE — deliberately. That needed a new App.Services locator
                // call (AGENTS.md forbids adding one), and wizard choices apply at Done anyway:
                // App's OnboardingCompleted handler calls UpdateRuntimeCoordinator
                // .ReconcileCheckSetting, which covers the relaunch-wizard path this notify
                // existed for. A first run needs nothing (the scheduler starts from the persisted
                // setting at the gated start).
                var effective = ApplyOnboardingToggle(AppDefaults.AutomaticUpdateCheckEnabled, isOn, warning);
                if (checkSwitch is { } sw && sw.IsOn != effective) sw.IsOn = effective;
                // Installing is only ever triggered by a background check, so the install choice
                // is meaningless without it. Grey the CONTROL, never the stored value — turning
                // checks back on restores the user's own install preference.
                if (installSwitch is { } inst) inst.IsEnabled = effective;
            },
            out checkSwitch));

        // ── Install updates automatically (default ON — an opt-out) ──
        var installOn = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled);
        _contentPanel.Children.Add(AppTheme.CreateToggleSetting(
            "Install updates automatically",
            // Character-identical to the Settings → Updates description: one option, one wording.
            UpdatesPage.InstallToggleDescription,
            installOn,
            isOn =>
            {
                var effective = ApplyOnboardingToggle(AppDefaults.AutomaticUpdateInstallEnabled, isOn, warning);
                if (installSwitch is { } sw && sw.IsOn != effective) sw.IsOn = effective;
            },
            out installSwitch));

        installSwitch.IsEnabled = checkOn;

        _contentPanel.Children.Add(warning);

        var continueBtn = AppTheme.CreateAccentButton("Continue");
        continueBtn.Tapped += (_, _) => ShowStep(13);
        AddButtonRow(CreateBackButton(), continueBtn);
    }

    /// <summary>
    /// One durable onboarding toggle write (UPD-4b). Returns the value actually in effect and
    /// surfaces a warning when it could not be persisted.
    ///
    /// <para>A plain <c>SetBool</c> would not do: <c>SettingsService</c> debounces writes and its
    /// <c>Save()</c> swallows failures, so the surrounding try/catch every other onboarding step
    /// uses detects a throw from the CALL, not a failure of the WRITE.
    /// <see cref="DiagnosticsConsent.Apply"/> flushes and reports, failing OFF in both
    /// directions.</para>
    /// </summary>
    private bool ApplyOnboardingToggle(string key, bool requested, TextBlock warning)
    {
        DiagnosticsConsent.ConsentApplyResult result;
        try
        {
            result = DiagnosticsConsent.Apply(_settings, key, requested);
        }
        catch (Exception ex)
        {
            // Apply is already fail-safe internally; this catches a settings service that throws
            // on the in-memory write itself. Never advance-blocking: the user's other choices and
            // the rest of the wizard matter more than this one preference.
            Logger.Warning(ex, "Persisting update preference {Key} failed during onboarding", key);
            warning.Text = "Couldn't save this setting. You can set it later in Updates.";
            warning.Visibility = Visibility.Visible;
            return false;
        }

        if (result.Persisted)
        {
            warning.Visibility = Visibility.Collapsed;
            return result.Effective;
        }

        Logger.Warning("Could not persist update preference {Key} during onboarding; forced to {Effective}",
            key, result.Effective);
        warning.Text = "Couldn't save this setting, so it's turned off for now. You can set it later in Updates.";
        warning.Visibility = Visibility.Visible;
        return result.Effective;
    }

    // ────────────────────────────────────────────────────────────────
    // Step 13: Done
    // ────────────────────────────────────────────────────────────────

    private void BuildDoneStep()
    {
        AddStepHeader("You're all set!",
            "VoiceWink is ready. Here's what was configured:",
            titleFontSize: 28);

        // ── Configuration summary card ──
        var summaryPanel = new StackPanel { Spacing = 4 };

        // Transcription model
        var modelName = _settings.GetString(AppDefaults.SelectedModelName, AppDefaults.DefaultWhisperModel);
        var modelDisplay = ModelDisplayName.Resolve(modelName);
        summaryPanel.Children.Add(CreateSummaryRow("\uE8D6", "Transcription", modelDisplay));

        // Language
        var langDisplay = ModelManagementViewModel.GetLanguageDisplayName(_selectedLanguage);
        summaryPanel.Children.Add(CreateSummaryRow("\uE774", "Language", langDisplay));

        // Enhancement. "ON" alone read as "ready" (launch defaults audit, 2026-09-13), but the AI
        // step lets the user continue with the switch on and no key (its validator passes an empty
        // box straight through — "adding a key later is a Settings task"), or with a key and no
        // model — so the summary names those states instead of promising a feature that throws
        // "No AI model selected" on the first dictation. "Usable" here means a key AND a model are
        // stored for the selected text provider, read the way the AI step reads the provider
        // (raw setting, parsed).
        var enhancementOn = _settings.GetBool(AppDefaults.AiEnhancementEnabled, false);
        summaryPanel.Children.Add(CreateSummaryRow("\uE945", "Enhancement", EnhancementSummary(enhancementOn)));

        // Recording Hotkey \u2014 the fields hold canonical strings; the summary speaks display names,
        // like every other surface (the seeded first-run default reads "Right Ctrl" here, not
        // "RightControl" \u2014 all three self-review lenses caught the mismatch).
        var hotkeyDisplay = HotkeyKeyDisplay.Describe(_selectedHotkey ?? "RightAlt");
        summaryPanel.Children.Add(CreateSummaryRow("\uE765", "Recording hotkey", hotkeyDisplay));

        // Paste Last Hotkey
        var pasteLastDisplay = string.IsNullOrEmpty(_selectedPasteLastHotkey)
            ? "Not set"
            : HotkeyKeyDisplay.Describe(_selectedPasteLastHotkey);
        summaryPanel.Children.Add(CreateSummaryRow("\uE77F", "Paste last hotkey", pasteLastDisplay));

        // Redo Last Hotkey
        var redoLastDisplay = string.IsNullOrEmpty(_selectedRedoLastHotkey)
            ? "Not set"
            : HotkeyKeyDisplay.Describe(_selectedRedoLastHotkey);
        summaryPanel.Children.Add(CreateSummaryRow("\uE72C", "Redo last hotkey", redoLastDisplay));

        // Clipboard restore
        var clipRestore = _settings.GetBool(AppDefaults.RestoreClipboardAfterPaste, false);
        summaryPanel.Children.Add(CreateSummaryRow("\uE8C8", "Clipboard restore", clipRestore ? "ON" : "OFF"));

        // Send Enter
        var sendEnter = _settings.GetBoolDefaulted(AppDefaults.SendEnterAfterPaste);
        summaryPanel.Children.Add(CreateSummaryRow("\uE751", "Send Enter after paste", sendEnter ? "ON" : "OFF"));

        // Startup
        var launchLogin = _settings.GetBool(AppDefaults.LaunchAtLogin, true);
        summaryPanel.Children.Add(CreateSummaryRow("\uE7E8", "Launch at login", launchLogin ? "ON" : "OFF"));
        var startMin = _settings.GetBool(AppDefaults.StartMinimized, true);
        summaryPanel.Children.Add(CreateSummaryRow("\uE921", "Start minimized", startMin ? "ON" : "OFF"));
        // The three answers the wizard asked for and then left out of its own summary (launch
        // defaults audit, 2026-09-13): the ONB-3 tray toggle, the crash-report consent (REL-1a)
        // and the two UPD-4b update choices. GetBoolDefaulted where a Defaults entry exists — the
        // same seam their steps read, so the card cannot disagree with the switch it summarises.
        var minimizeToTray = _settings.GetBoolDefaulted(AppDefaults.MinimizeToTray);
        summaryPanel.Children.Add(CreateSummaryRow("\uE921", "Minimize to tray", minimizeToTray ? "ON" : "OFF"));
        // One row for the "Help improve VoiceWink" answer, reading the TWO flags it set (a user can
        // diverge them in Settings before re-running the wizard, so the row reads the flags, never
        // the answer). Prompt logs = the RAW trace opt-in; the redacted summary is always kept.
        var crashReports = _settings.GetBoolDefaulted(AppDefaults.CrashReportingOptIn);
        var promptLogs = _settings.GetBoolDefaulted(AppDefaults.PromptTraceLoggingOptIn);
        summaryPanel.Children.Add(CreateSummaryRow("\uE7BA", "Help improve VoiceWink", HelpImproveSummary(crashReports, promptLogs)));
        var updateCheck = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);
        var updateInstall = _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateInstallEnabled);
        summaryPanel.Children.Add(CreateSummaryRow("\uE895", "Updates", UpdatesSummary(updateCheck, updateInstall)));

        _contentPanel.Children.Add(AppTheme.CreateCard(summaryPanel));

        var finishBtn = AppTheme.CreateAccentButton("Start Using VoiceWink");
        finishBtn.Tapped += (_, _) =>
        {
            // Persist hotkey selections now (deferred from hotkey step to avoid
            // saving intermediate choices if user navigates back or abandons)
            _settings.SetString(AppDefaults.HotkeyModifier, _selectedHotkey ?? "RightAlt");
            _settings.SetString(AppDefaults.PasteLastHotkeyModifier, _selectedPasteLastHotkey ?? "");
            _settings.SetString(AppDefaults.RedoLastHotkeyModifier, _selectedRedoLastHotkey ?? "");

            // Recording wins — against EVERYTHING, not just the two roles this wizard shows
            // (Fable diff r6). The recording selector clears colliding pasteLast/redoLast in-step,
            // but the wizard has no generate-image row and cannot edit prompts, so a recording
            // choice colliding with either persisted BOTH: HandleKeyDown matches generate-image
            // BEFORE recording, so `F8` recording beside `F8` generate-image left the RECORDING
            // hotkey dead — and a prompt on the recording key was shadowed with only a log line.
            // Applied here rather than in the selector because the wizard defers all persistence
            // to this button precisely so abandoning it leaves no trace.
            var recordingBinding = _selectedHotkey ?? "RightAlt";
            var collisions = HotkeyConflictScan.ForRole(
                HotkeyBindingSnapshot.FromSettings(_settings.GetString, HotkeyPrompts.Read()),
                recordingBinding, HotkeyRole.Recording);
            foreach (var collision in collisions)
            {
                if (collision.Role is { } role)
                    _settings.SetString(HotkeyConflictScan.SettingsKeyFor(role), "");
            }

            // Reload BEFORE the prompt re-registration so prompts are filtered against the roles
            // that were just persisted, not the pre-wizard ones.
            App.Services.GetRequiredService<Services.Input.HotkeyService>().ReloadHotkeys();

            var shadowedPromptIds = collisions
                .Where(c => c.IsPrompt)
                .Select(c => c.PromptId)
                .ToHashSet(StringComparer.Ordinal);
            if (shadowedPromptIds.Count > 0)
            {
                try
                {
                    var enhancement = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();
                    var prompts = enhancement.GetPrompts();
                    foreach (var p in prompts.Where(p => shadowedPromptIds.Contains(p.Id)))
                        p.Hotkey = null;
                    enhancement.SavePrompts(prompts);
                    App.Services.GetRequiredService<Services.Input.HotkeyService>().RegisterPromptHotkeys(
                        prompts.Where(p => !string.IsNullOrEmpty(p.Hotkey))
                               .Select(p => (p.Id, p.Hotkey!)));
                }
                catch (Exception ex)
                {
                    // Fail-soft, same posture as HotkeyPrompts.Read: an unclearable prompt hotkey
                    // is shadowed (dead but harmless) — RegisterPromptHotkeys already refuses to
                    // register a prompt a single-key role claims, so nothing double-fires.
                    Log.ForContext<OnboardingPage>().Warning(ex,
                        "Could not clear prompt hotkeys shadowed by the new recording hotkey");
                }
            }

            // Apply LaunchAtLogin to the Windows registry via the dedicated service.
            // Going through SettingsViewModel.LaunchAtLogin would no-op when the singleton VM
            // already holds the same value (CommunityToolkit OnXxxChanged only fires on change),
            // skipping the registry write on fresh installs.
            App.Services.GetRequiredService<Services.System.AutostartRegistrationService>()
                .Apply(_settings.GetBool(AppDefaults.LaunchAtLogin, true));

            _settings.SetBool(AppDefaults.HasCompletedOnboarding, true);
            OnboardingCompleted?.Invoke();
        };
        AddButtonRow(CreateBackButton(), finishBtn);
    }

    // ────────────────────────────────────────────────────────────────
    // Shared helpers
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a step header card to _contentPanel: a card containing a title TextBlock
    /// and an optional subtitle TextBlock.
    /// </summary>
    private void AddStepHeader(string title, string? subtitle = null, int titleFontSize = 24, int subtitleFontSize = 14)
    {
        var cardContent = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        cardContent.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = titleFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        if (subtitle != null)
        {
            cardContent.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = subtitleFontSize,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
            });
        }

        _contentPanel.Children.Add(AppTheme.CreateCard(cardContent));
    }

    /// <summary>
    /// Creates a key validation status panel (FontIcon + TextBlock) and a setter action.
    /// Used for API key validation feedback in both cloud transcription and AI enhancement steps.
    /// </summary>
    private static (StackPanel panel, Action<string, string, Windows.UI.Color, bool> setStatus) CreateKeyStatusPanel()
    {
        var icon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { icon, label }
        };

        void SetStatus(string glyph, string text, Windows.UI.Color color, bool visible = true)
        {
            icon.Glyph = glyph;
            icon.Foreground = AppTheme.Brush(color);
            label.Text = text;
            label.Foreground = AppTheme.Brush(color);
            panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        return (panel, SetStatus);
    }

    /// <summary>
    /// Creates a selectable option card with icon, title, and subtitle.
    /// Used for cloud/local transcription and theme options.
    /// </summary>
    private static Border CreateSelectableOption(string glyph, string title, string subtitle, bool isSelected)
    {
        var iconBlock = new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary)
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { iconBlock, textPanel }
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 14, 20, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = row
        };
        UpdateOptionStyle(border, isSelected);

        return border;
    }

    /// <summary>
    /// Updates an option border to selected (accent) or deselected (card) style.
    /// </summary>
    private static void UpdateOptionStyle(Border border, bool isSelected)
    {
        if (isSelected)
        {
            border.Background = AppTheme.Brush(AppTheme.ActivePillBg);
            border.BorderBrush = AppTheme.Brush(AppTheme.AccentBlue);
            border.BorderThickness = new Thickness(2);
        }
        else
        {
            border.Background = AppTheme.Brush(AppTheme.CardBg);
            border.BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor);
            border.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>
    /// Updates child text/icon colors inside a selectable option border.
    /// </summary>
    private static void UpdateOptionChildColors(Border border, bool isSelected)
    {
        if (border.Child is StackPanel row && row.Children.Count >= 2)
        {
            if (row.Children[0] is FontIcon icon)
                icon.Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary);
            if (row.Children[1] is StackPanel textPanel && textPanel.Children.Count > 0)
            {
                if (textPanel.Children[0] is TextBlock titleBlock)
                    titleBlock.Foreground = AppTheme.Brush(isSelected ? AppTheme.AccentBlue : AppTheme.TextSecondary);
            }
        }
    }

    /// <summary>
    /// The Done card's Enhancement value: OFF, ON, or ON with the provider setup named as
    /// incomplete. The text is pure (<see cref="EnhancementSummaryText"/>); this reads the two
    /// facts it needs — a stored key and a stored text model for the selected provider — the way
    /// the AI step reads the provider (raw setting, parsed, OpenAI when unparseable, mirroring
    /// <c>AIEnhancementService.ReadProviderSetting</c>) and the model the way the service does
    /// (<c>TextModelFor</c>: the per-provider key, no implicit default).
    /// </summary>
    private string EnhancementSummary(bool enabled)
    {
        if (!enabled)
            return EnhancementSummaryText(enabled: false, hasKey: false, hasModel: false);
        var raw = _settings.GetString(AppDefaults.AiProvider, "");
        var provider = Enum.TryParse<Services.AIEnhancement.AIProvider>(raw, out var p)
            ? p
            : Services.AIEnhancement.AIProvider.OpenAI;
        var hasKey = !string.IsNullOrEmpty(_apiKeys.GetApiKey(provider.ToString().ToLowerInvariant()));
        var hasModel = !string.IsNullOrEmpty(_settings.GetString(AppDefaults.AiModelKey(provider), ""));
        return EnhancementSummaryText(enabled: true, hasKey, hasModel);
    }

    /// <summary>Pure half of <see cref="EnhancementSummary"/>; pinned by <c>OnboardingSummaryTests</c>.
    /// The key is named before the model: without a key the model list cannot even be fetched.</summary>
    internal static string EnhancementSummaryText(bool enabled, bool hasKey, bool hasModel)
        => !enabled ? "OFF"
         : !hasKey ? "ON — provider setup incomplete (no API key)"
         : !hasModel ? "ON — provider setup incomplete (no model selected)"
         : "ON";

    /// <summary>
    /// The Done card's Updates value, one row for the two UPD-4b choices. Install is only ever
    /// triggered by a background check, so "install ON, check OFF" is reported as checks off —
    /// the same reading the Updates step enforces by greying the install switch. With install
    /// off, a found update lights the sidebar badge and waits for "Apply &amp; restart".
    /// </summary>
    internal static string UpdatesSummary(bool checkEnabled, bool installEnabled)
        => !checkEnabled ? "Automatic checks OFF"
         : installEnabled ? "Check and install automatically"
         : "Check automatically, install when you choose";

    /// <summary>The Done card's "Help improve VoiceWink" value — both flags, always named, so a
    /// mixed state (set in Settings) reads as what it is rather than as one answer.</summary>
    internal static string HelpImproveSummary(bool crashReports, bool promptLogs)
        => $"Crash reports {(crashReports ? "ON" : "OFF")}, prompt logs {(promptLogs ? "ON" : "OFF")}";

    /// <summary>
    /// Creates a summary row for the Done step: icon + label on the left, value on the right.
    /// </summary>
    private static Border CreateSummaryRow(string glyph, string label, string value)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center
        };

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            // ONB-8: the value column used to be Auto beside a star label column, so the label got only
            // what the value left — on the longest row (Help improve VoiceWink) that was nothing and
            // the two touched; past the card's 450 px cap the label was clipped outright. Now the label
            // column is Auto (it keeps its natural width at every card width the window can reach) and
            // the value takes the rest, right-aligned, wrapping when the card is too narrow, with a
            // fixed gap so the two never meet.
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(16, 0, 0, 0)
        };

        var leftPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, labelBlock }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(valueBlock);

        return new Border
        {
            Background = AppTheme.TransparentBrush,
            Padding = new Thickness(4, 4, 4, 4),
            Child = grid
        };
    }

    /// <summary>Creates a label + control row for form-style layout.</summary>
    private static StackPanel CreateLabeledRow(string label, UIElement control)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 0, 0, 4)
        };
        return new StackPanel { Children = { labelBlock, control } };
    }

    /// <summary>Creates a mode explanation row with icon, title, and description using Grid for proper text wrapping.</summary>
    private static Grid CreateModeRow(string glyph, string title, string description)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 18,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textPanel, 1);
        icon.Margin = new Thickness(0, 2, 12, 0);
        grid.Children.Add(icon);
        grid.Children.Add(textPanel);

        return grid;
    }


    /// <summary>Creates a Back button (does not add to panel).</summary>
    private Border CreateBackButton()
    {
        var btn = AppTheme.CreateSecondaryButton("Back");
        btn.Tapped += (_, _) =>
        {
            AbandonCurrentDownload();
            ShowStep(_step - 1);
        };
        return btn;
    }

    private void AddButtonRow(params FrameworkElement[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };
        foreach (var btn in buttons)
        {
            btn.Margin = new Thickness(0);
            row.Children.Add(btn);
        }
        _contentPanel.Children.Add(row);
    }
}
