using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using VoiceWink.Controls; // FlowPanel (badge row)
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AIEnhancement.Providers;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.Input;
using VoiceWink.Services.System; // SettingsService (still used; Codex's unused-using nit was incorrect)
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// AI Enhancement settings page.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class EnhancementPage : Page
{
    private static ILogger Logger => Log.ForContext<EnhancementPage>();

    private readonly EnhancementViewModel _viewModel;
    private readonly List<Action> _attachSubscriptions = [];
    private readonly List<Action> _detachSubscriptions = [];
    private StackPanel? _promptListPanel;
    private ComboBox? _textProviderCombo;
    private ComboBox? _imageProviderCombo;
    private bool _subscriptionsAttached;
    // Bumped synchronously at every BuildUI so queued dispatcher callbacks can fence out a
    // superseded control generation — combo.IsLoaded alone lags (Unloaded dispatch is async).
    private int _uiGeneration;
    private readonly AIEnhancementService _enhancement;

    public EnhancementPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _viewModel = App.Services.GetRequiredService<EnhancementViewModel>();
        // Resolved ONCE here so the rest of the page shares it. IMG-5's option gating needs the
        // service, and the launch-freeze baseline forbids ADDING service-locator call sites — so
        // this field replaces the existing per-method lookups rather than joining them.
        _enhancement = App.Services.GetRequiredService<AIEnhancementService>();
        // The enable switch is re-read BEFORE the toggle is built (2026-09-13): the singleton
        // ViewModel captured it once, and the onboarding wizard writes it straight to settings —
        // after "Relaunch setup wizard" this page showed the pre-wizard state while the runtime
        // already used the new one. AttachSubscriptions resyncs provider/model/keys but runs on
        // Loaded, after BuildUI has already seeded the toggle, so it cannot do this job.
        _viewModel.ReloadFromSettings();
        BuildUI();
        Loaded += (_, _) =>
        {
            AttachSubscriptions(); // also fetches models
            _viewModel.ReloadPrompts();
            RefreshPromptList();
        };
        Unloaded += (_, _) => DetachSubscriptions();
    }

    private void RegisterViewModelSubscription(PropertyChangedEventHandler handler)
    {
        _attachSubscriptions.Add(() => _viewModel.PropertyChanged += handler);
        _detachSubscriptions.Add(() => _viewModel.PropertyChanged -= handler);
    }

    private void RegisterCollectionSubscription(INotifyCollectionChanged collection, NotifyCollectionChangedEventHandler handler)
    {
        _attachSubscriptions.Add(() => collection.CollectionChanged += handler);
        _detachSubscriptions.Add(() => collection.CollectionChanged -= handler);
    }

    /// <summary>
    /// An API-key label paired with a link to the CURRENT provider's console (owner request
    /// 2026-07-31 — same affordance as the Models page). Two behaviours the owner asked for on
    /// review: the link's WORDING follows whether a key is already stored ("Get API key" when
    /// there is none — the user needs to obtain one; "Manage API key" when there is — the same
    /// console is now where they'd rotate or check it), and it re-targets on every provider
    /// change, HIDING for a provider with no known console so switching can never leave a link
    /// pointing at the previous provider's dashboard.
    ///
    /// <para>Label and link share ONE TextBlock (<see cref="AppTheme.CreateLabelWithLink"/>) so
    /// they sit on a common baseline — see that method's remarks for why a HyperlinkButton beside
    /// the label could not be made to align.</para>
    /// </summary>
    private TextBlock BuildApiKeyLabelRow(
        string labelText,
        Func<AIProvider> currentProvider,
        string providerPropertyName,
        Func<bool> hasStoredKey,
        string hasKeyPropertyName)
    {
        var (block, applyLink) = AppTheme.CreateLabelWithLink(
            labelText, 13, AppTheme.Brush(AppTheme.SubtleText), linkFontSize: 13);
        block.Margin = new Thickness(0, 12, 0, 6);

        void ApplyState() => applyLink(
            ProviderConsoleUrls.ForProvider(currentProvider()),
            hasStoredKey() ? "Manage API key" : "Get API key");
        ApplyState();

        PropertyChangedEventHandler stateChangedHandler = (_, e) =>
        {
            if (e.PropertyName == providerPropertyName || e.PropertyName == hasKeyPropertyName)
                ApplyState();
        };
        RegisterViewModelSubscription(stateChangedHandler);

        return block;
    }

    private void AttachHandlers()
    {
        if (_subscriptionsAttached) return;

        foreach (var attach in _attachSubscriptions)
            attach();

        _subscriptionsAttached = true;
    }

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached) return;

        AttachHandlers();

        // Sync from persisted settings. If the user previously selected a keyless provider
        // and navigated away, the ViewModel may still hold the keyless provider in memory
        // (settings were never written). Reset both the ViewModel and the combo visuals.
        var enhancement = _enhancement;
        _viewModel.SelectedProvider = enhancement.SelectedProvider;
        _viewModel.SelectedImageProvider = enhancement.SelectedImageProvider;
        _viewModel.SelectedModel = enhancement.SelectedModel;
        _viewModel.SelectedImageModel = enhancement.SelectedImageModel;
        // Force combo sync even if the value didn't change (no PropertyChanged fired)
        if (_textProviderCombo != null)
            _textProviderCombo.SelectedItem = _viewModel.SelectedProvider.ToString();
        if (_imageProviderCombo != null)
            _imageProviderCombo.SelectedItem = _viewModel.SelectedImageProvider.ToString();

        // Keys can change on OTHER pages (ModelsPage saves transcription keys for the same
        // OpenAI/Groq providers) while this singleton VM sits detached — resync the masked
        // boxes + HasExistingKey/HasExistingImageKey from the actual store before fetching,
        // or the key-status indicators stay wrong indefinitely (Codex PR-2 R3).
        _viewModel.RefreshApiKeys();

        // Re-fetch models in case API keys changed on another page (e.g. Settings)
        _ = _viewModel.FetchModelsCommand.ExecuteAsync(null);
        if (ImageGenerationFeature.IsEnabled)
            _ = _viewModel.FetchImageModelsCommand.ExecuteAsync(null);
    }

    private void DetachSubscriptions()
    {
        if (!_subscriptionsAttached) return;

        for (var i = _detachSubscriptions.Count - 1; i >= 0; i--)
            _detachSubscriptions[i]();
        _subscriptionsAttached = false;
    }

    private void BuildUI()
    {
        // Generation-scoped subscription ownership: a mid-session rebuild (enable toggle,
        // set-active-prompt) replaces every control, so the old generation's handlers must
        // detach and its registrations must be discarded WITH the old visual tree — otherwise
        // VM events keep driving detached combos (a live instance of the 0x80070490 crash
        // surface) while the replacement controls never attach.
        _uiGeneration++;
        DetachSubscriptions();
        _attachSubscriptions.Clear();
        _detachSubscriptions.Clear();

        // -- Page header --
        var header = AppTheme.CreatePageHeader(
            "AI Enhancement",
            "Use AI to clean up and enhance transcription output.");

        // -- Enable toggle --
        var enableToggle = AppTheme.CreateToggleSetting(
            "Enable AI Enhancement",
            "Turn on AI-powered enhancement features",
            _viewModel.IsEnabled,
            isOn =>
            {
                _viewModel.IsEnabled = isOn;
                BuildUI(); // rebuild to reflect prompt active states
            });

        // -- AI Provider Integration card --
        var providerSection = BuildProviderSection();

        // -- Enhancement Prompts section --
        var promptsSection = BuildPromptsSection();

        // -- Assemble page --
        var root = new StackPanel
        {
            Children =
            {
                header,
                enableToggle,
                providerSection,
                promptsSection
            }
        };

        AppTheme.SetPageScrollContent(this, root);

        // Mid-session rebuild: no Loaded event will fire again, so the new generation's
        // handlers go live here. The ctor-time call leaves attachment to Loaded as before.
        if (IsLoaded)
            AttachHandlers();
    }

    private UIElement BuildProviderSection()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Text Enhancement");

        // ── Helper: create a provider combo + refresh button row ────────
        (StackPanel row, ComboBox combo) CreateProviderRow(
            string label, AIProvider initial,
            Action<AIProvider> onChanged,
            Func<Task> fetchAction,
            Func<string?> getFetchError,
            string fetchErrorPropertyName,
            bool imageOnly = false)
        {
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var combo = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            AppTheme.AllowParentScroll(combo);
            var registry = App.Services.GetRequiredService<AIProviderRegistry>();
            foreach (var p in EnhancementViewModel.AvailableProviders)
            {
                if (!imageOnly || registry.SupportsImageGeneration(p))
                    combo.Items.Add(p.ToString());
            }
            combo.SelectedItem = initial.ToString();
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string str && Enum.TryParse<AIProvider>(str, out var p))
                    onChanged(p);
            };

            var refreshIcon = new FontIcon { Glyph = "\uE72C", FontSize = 14, Foreground = AppTheme.Brush(AppTheme.SubtleText) };
            var loadingRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Foreground = AppTheme.Brush(AppTheme.AccentBlue) };
            var refreshBtn = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = AppTheme.Brush(AppTheme.CardBg),
                BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = refreshIcon
            };
            var normalBg = AppTheme.Brush(AppTheme.CardBg);
            var hoverBg = AppTheme.Brush(AppTheme.HoverBg);
            refreshBtn.PointerEntered += (_, _) => refreshBtn.Background = hoverBg;
            refreshBtn.PointerExited += (_, _) => refreshBtn.Background = normalBg;
            // ENH-1: single error line per provider row, fed by the VM's fetch-error
            // observable (the fetch commands swallow their exceptions, so a throw-only
            // inline error would stay permanently dark — the observable carries WHY,
            // e.g. "User location is not supported for the API use.").
            var fetchErrorText = new TextBlock
            {
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentRed),
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };

            void ApplyFetchError()
            {
                var error = getFetchError();
                fetchErrorText.Text = error ?? "";
                fetchErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            }

            ApplyFetchError();
            PropertyChangedEventHandler fetchErrorChangedHandler = (_, e) =>
            {
                if (e.PropertyName == fetchErrorPropertyName)
                    DispatcherQueue.TryEnqueue(ApplyFetchError);
            };
            RegisterViewModelSubscription(fetchErrorChangedHandler);

            refreshBtn.Tapped += async (_, _) =>
            {
                refreshBtn.Child = loadingRing;
                loadingRing.IsActive = true;
                try
                {
                    await fetchAction();
                }
                catch (Exception ex)
                {
                    // The fetch commands handle their own failures via the observable;
                    // this only catches unexpected command-infrastructure faults.
                    Logger.Warning(ex, "FetchModels failed");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        fetchErrorText.Text = "Could not fetch models. Check your API key.";
                        fetchErrorText.Visibility = Visibility.Visible;
                    });
                }
                finally
                {
                    loadingRing.IsActive = false;
                    refreshBtn.Child = refreshIcon;
                }
            };
            ToolTipService.SetToolTip(refreshBtn, $"Fetch available models");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(combo);
            row.Children.Add(refreshBtn);

            // Return a wrapper that includes the label and inline error text
            var wrapper = new StackPanel { Children = { lbl, row, fetchErrorText } };
            return (wrapper, combo);
        }

        // ── Helper: create an editable model combo bound to a model list ──
        ComboBox CreateModelCombo(
            string placeholder,
            Func<string> getCurrent,
            Action<string> setCurrent,
            ObservableCollection<string> models,
            string propertyName)
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                PlaceholderText = placeholder,
                Width = 280,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AppTheme.AllowParentScroll(combo);

            var suppress = false;

            void ApplyCurrent()
            {
                var cur = getCurrent();
                if (combo.Items.Contains(cur))
                    combo.SelectedItem = cur;
                else
                    combo.Text = cur;
            }

            void ApplyCurrentSuppressed()
            {
                suppress = true;
                try { ApplyCurrent(); }
                finally { suppress = false; }
            }

            void RefreshItems()
            {
                // Full-body suppress, ApplyCurrent included: the reselect is just as
                // programmatic as the repopulate — unsuppressed it writes back into the
                // VM's selected model mid-refresh.
                suppress = true;
                try
                {
                    // Single ItemsSource swap, never Items.Clear()+Add loop: churning a
                    // live IsEditable combo's item collection corrupts native combo/popup
                    // state (the PR #163 0x80070490 class — this site was its LAST
                    // holdout, and crashed live on 2026-07-30 after a rapid provider
                    // cycle + first-key save; the coalescing below bounds how often a
                    // refresh runs, but only the atomic swap makes the refresh itself
                    // safe). Snapshot then assign once.
                    combo.ItemsSource = models.ToList();
                    ApplyCurrent();
                }
                finally { suppress = false; }
            }

            // Stale fence for queued callbacks: the generation catches a BuildUI rebuild
            // (bumped synchronously — IsLoaded lags, Unloaded dispatch is async), IsLoaded
            // catches nav-away. A skipped refresh is recovered by the combo's own Loaded
            // handler below, which re-runs the full suppressed refresh.
            var generation = _uiGeneration;
            bool IsStale() => generation != _uiGeneration || !combo.IsLoaded;

            // Coalesce CollectionChanged bursts (a provider switch fires many: Clear at
            // switch + the fetch's repopulate) into ONE queued refresh — per-event
            // unconditional enqueues churn the live editable combo's native item state
            // and corrupt it (fatal COMException 0x80070490 on the next dropdown open).
            var refreshPending = false;
            NotifyCollectionChangedEventHandler collectionChangedHandler = (_, _) =>
            {
                if (refreshPending) return;
                refreshPending = true;
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    refreshPending = false;
                    if (IsStale()) return;
                    RefreshItems();
                }))
                {
                    refreshPending = false; // enqueue refused (shutdown) — don't wedge the flag
                }
            };
            RegisterCollectionSubscription(models, collectionChangedHandler);
            RefreshItems();
            // Full refresh, not just a reselect: a queued refresh that was fenced out while
            // this combo wasn't loaded yet (rebuild racing an in-flight fetch) would otherwise
            // be lost for good — entering the tree always converges to the VM's current list.
            combo.Loaded += (_, _) => RefreshItems();

            combo.TextSubmitted += (_, args) => setCurrent(args.Text);
            combo.SelectionChanged += (_, _) =>
            {
                if (!suppress && combo.SelectedItem is string selected)
                    setCurrent(selected);
            };

            PropertyChangedEventHandler propertyChangedHandler = (_, e) =>
            {
                if (e.PropertyName == propertyName)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (IsStale()) return;
                        ApplyCurrentSuppressed();
                    });
                }
            };
            RegisterViewModelSubscription(propertyChangedHandler);

            return combo;
        }

        // ── Text Provider + Model ──────────────────────────────────────
        var (textProviderRow, textProviderCombo) = CreateProviderRow(
            "Provider", _viewModel.SelectedProvider,
            p => _viewModel.SelectedProvider = p,
            () => _viewModel.FetchModelsCommand.ExecuteAsync(null),
            () => _viewModel.ModelFetchError,
            nameof(EnhancementViewModel.ModelFetchError));
        _textProviderCombo = textProviderCombo;

        var modelLabel = new TextBlock
        {
            Text = "Model",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 8, 0, 6)
        };

        var modelCombo = CreateModelCombo(
            "Select or type a model name",
            () => _viewModel.SelectedModel,
            v => _viewModel.SelectedModel = v,
            _viewModel.AvailableModels,
            nameof(EnhancementViewModel.SelectedModel));

        // API Key for text provider — the label carries a "Get API key" link to the CURRENT
        // provider's console (owner request 2026-07-31, mirroring the Models page): the key is
        // asked for here, so this is where the user needs to be told where to get one.
        var apiKeyLabel = BuildApiKeyLabelRow(
            "API Key",
            () => _viewModel.SelectedProvider,
            nameof(EnhancementViewModel.SelectedProvider),
            () => _viewModel.HasExistingKey,
            nameof(EnhancementViewModel.HasExistingKey));

        var apiKeyBox = new PasswordBox
        {
            Password = _viewModel.ApiKey,
            PlaceholderText = "Enter your API key",
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        apiKeyBox.PasswordChanged += (_, _) => _viewModel.ApiKey = apiKeyBox.Password;
        PropertyChangedEventHandler apiKeyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(EnhancementViewModel.ApiKey) && apiKeyBox.Password != _viewModel.ApiKey)
                apiKeyBox.Password = _viewModel.ApiKey;
        };
        RegisterViewModelSubscription(apiKeyChangedHandler);

        var saveBtn = AppTheme.CreateAccentButton("Save Key", async (_, _) => await _viewModel.SaveApiKeyAsync());

        var apiKeyStatus = CreateApiKeyStatusIndicator(
            () => _viewModel.ApiKeyStatus,
            nameof(EnhancementViewModel.ApiKeyStatus),
            // ACTUAL stored-key state, not the password box's contents — a DPAPI-failed
            // save leaves text in the box with nothing persisted (Codex PR-2 R2).
            () => _viewModel.HasExistingKey,
            nameof(EnhancementViewModel.HasExistingKey));

        var saveBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { apiKeyBox, saveBtn, apiKeyStatus }
        };

        // ── Image Provider + Model ─────────────────────────────────────
        var (imageProviderRow, imageProviderCombo) = CreateProviderRow(
            "Provider", _viewModel.SelectedImageProvider,
            p => _viewModel.SelectedImageProvider = p,
            () => _viewModel.FetchImageModelsCommand.ExecuteAsync(null),
            () => _viewModel.ImageModelFetchError,
            nameof(EnhancementViewModel.ImageModelFetchError),
            imageOnly: true);
        _imageProviderCombo = imageProviderCombo;

        var imageModelLabel = new TextBlock
        {
            Text = "Model",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 8, 0, 6)
        };

        var imageModelCombo = CreateModelCombo(
            "Select or type an image model",
            () => _viewModel.SelectedImageModel,
            v => _viewModel.SelectedImageModel = v,
            _viewModel.AvailableImageModels,
            nameof(EnhancementViewModel.SelectedImageModel));

        var imageModelHint = new TextBlock
        {
            Text = "Used by image generation prompts (e.g. Generate Image).",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            Margin = new Thickness(0, 4, 0, 0)
        };

        // HIS-2 (owner request 2026-07-15): the "New image…" + "Open images folder"
        // buttons moved to the History page's toolbar — History is where generated
        // images live; this page keeps only generation SETTINGS.

        // API Key for image provider (only visible when different from text provider)
        var imageApiKeyLabel = BuildApiKeyLabelRow(
            "API Key",
            () => _viewModel.SelectedImageProvider,
            nameof(EnhancementViewModel.SelectedImageProvider),
            () => _viewModel.HasExistingImageKey,
            nameof(EnhancementViewModel.HasExistingImageKey));

        var imageApiKeyBox = new PasswordBox
        {
            Password = _viewModel.ImageApiKey,
            PlaceholderText = "Enter your API key",
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        imageApiKeyBox.PasswordChanged += (_, _) => _viewModel.ImageApiKey = imageApiKeyBox.Password;
        PropertyChangedEventHandler imageApiKeyChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(EnhancementViewModel.ImageApiKey) && imageApiKeyBox.Password != _viewModel.ImageApiKey)
                imageApiKeyBox.Password = _viewModel.ImageApiKey;
        };
        RegisterViewModelSubscription(imageApiKeyChangedHandler);

        var saveImageBtn = AppTheme.CreateAccentButton("Save Key", async (_, _) => await _viewModel.SaveImageApiKeyAsync());

        var imageApiKeyStatus = CreateApiKeyStatusIndicator(
            () => _viewModel.ImageApiKeyStatus,
            nameof(EnhancementViewModel.ImageApiKeyStatus),
            () => _viewModel.HasExistingImageKey,
            nameof(EnhancementViewModel.HasExistingImageKey));

        var saveImageBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { imageApiKeyBox, saveImageBtn, imageApiKeyStatus }
        };

        // Hide image API key section when same provider (one key suffices)
        var imageKeyPanel = new StackPanel
        {
            Children = { imageApiKeyLabel, saveImageBtnRow },
            Visibility = _viewModel.SelectedProvider == _viewModel.SelectedImageProvider
                ? Visibility.Collapsed : Visibility.Visible
        };
        PropertyChangedEventHandler imageKeyPanelChangedHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(EnhancementViewModel.SelectedProvider)
                or nameof(EnhancementViewModel.SelectedImageProvider))
            {
                DispatcherQueue.TryEnqueue(() =>
                    imageKeyPanel.Visibility = _viewModel.SelectedProvider == _viewModel.SelectedImageProvider
                        ? Visibility.Collapsed : Visibility.Visible);
            }
        };
        RegisterViewModelSubscription(imageKeyPanelChangedHandler);

        // Per-card "Show all models" escape hatches (owner 2026-07-31): the text card's box
        // governs the text list, the image card's box the image list — one checkbox used to
        // flood both dropdowns at once.
        var (showAllCheck, showAllWarning) = BuildShowAllModelsRow(
            () => _viewModel.ShowAllModels, v => _viewModel.ShowAllModels = v);
        var (showAllImageCheck, showAllImageWarning) = BuildShowAllModelsRow(
            () => _viewModel.ShowAllImageModels, v => _viewModel.ShowAllImageModels = v);

        // No fetch here: first-load fetching happens in the Loaded path (AttachSubscriptions),
        // and a mid-session BuildUI rebuild must NOT refetch — the new combos are populated
        // from the VM's current collections by CreateModelCombo's initial RefreshItems().

        // TWO cards, not one (owner 2026-07-31 — the combined card was cluttered): text
        // enhancement and image generation are configured independently, so each gets its own
        // card with its own provider, model, key and show-all toggle. The `separator` that used
        // to divide the two halves is gone — the card edges do that job now.
        var textCardContent = new StackPanel
        {
            Children =
            {
                textProviderRow,
                modelLabel,
                modelCombo,
                apiKeyLabel,
                saveBtnRow,
                showAllCheck,
                showAllWarning
            }
        };

        // NO extra Spacing here (owner 2026-07-31 — "too much blank space"): CreateSectionHeader
        // already carries a 24 px top / 12 px bottom margin and CreateCard a 12 px bottom, so a
        // StackPanel Spacing ADDS to all three and inflated every gap on the page.
        var sections = new StackPanel
        {
            Children = { sectionHeader, AppTheme.CreateCard(textCardContent) }
        };

        // Image generation is gated by ImageGenerationFeature (currently enabled in all builds) —
        // the whole image card disappears if it is ever switched off.
        if (ImageGenerationFeature.IsEnabled)
        {
            var imageCardContent = new StackPanel
            {
                Children =
                {
                    imageProviderRow,
                    imageModelLabel,
                    imageModelCombo,
                    imageModelHint,
                    imageKeyPanel,
                    showAllImageCheck,
                    showAllImageWarning
                }
            };

            sections.Children.Add(AppTheme.CreateSectionHeader("Image Generation"));
            sections.Children.Add(AppTheme.CreateCard(imageCardContent));
        }

        return sections;
    }

    /// <summary>The "Show all available models" checkbox + its experimental warning, built per
    /// card so the text and image lists have independent escape hatches (owner 2026-07-31).</summary>
    private static (CheckBox Check, TextBlock Warning) BuildShowAllModelsRow(
        Func<bool> getValue, Action<bool> setValue)
    {
        var check = new CheckBox
        {
            Content = "Show all available models",
            IsChecked = getValue(),
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            // Center content + drop the 32px MinHeight so the box lines up with the single-line
            // label (WinUI's default Top alignment floats the text off-center). Mirrors OnboardingPage.
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var warning = new TextBlock
        {
            Text = "⚠ Experimental — shows every model the provider returns, including ones this list can't use. Selecting an unsupported model may cause errors.",
            FontSize = 12,
            Foreground = AppTheme.Brush(ColorHelper.FromArgb(255, 255, 180, 50)),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = getValue() ? Visibility.Visible : Visibility.Collapsed
        };
        check.Checked += (_, _) => { setValue(true); warning.Visibility = Visibility.Visible; };
        check.Unchecked += (_, _) => { setValue(false); warning.Visibility = Visibility.Collapsed; };
        return (check, warning);
    }

    private UIElement BuildPromptsSection()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Enhancement Prompts");

        _promptListPanel = new StackPanel { Spacing = 8 };
        RefreshPromptList();

        // Button row: Add Prompt + Add from Template
        var addBtn = AppTheme.CreateAccentButton("+ Add Prompt", (s, e) =>
        {
            // Open the edit dialog immediately on a blank DETACHED draft, so the user is never
            // left with a silently-appended prompt they didn't notice. The draft is not in the
            // collection yet — the dialog commits it only on a valid Save (isNew), so an abandoned
            // or failed add leaves nothing behind.
            var prompt = _viewModel.CreateDraftPrompt();
            ShowEditPromptDialog(prompt, isNew: true);
        });
        addBtn.HorizontalAlignment = HorizontalAlignment.Left;

        var templateBtn = AppTheme.CreateSecondaryButton("Add from Template", (s, e) =>
        {
            ShowTemplateDialog();
        });
        templateBtn.HorizontalAlignment = HorizontalAlignment.Left;

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { addBtn, templateBtn }
        };

        return new StackPanel
        {
            Children = { sectionHeader, buttonRow, _promptListPanel }
        };
    }

    private void RefreshPromptList()
    {
        if (_promptListPanel == null) return;
        _promptListPanel.Children.Clear();

        if (_viewModel.Prompts.Count == 0)
        {
            var emptyIcon = new FontIcon
            {
                Glyph = "\uE945",
                FontSize = 36,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptyTitle = new TextBlock
            {
                Text = "No prompts yet",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var emptySubtitle = new TextBlock
            {
                Text = "Add a prompt or pick one from the templates\nto enhance your transcriptions.",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var emptyContent = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0, 32, 0, 32),
                Children = { emptyIcon, emptyTitle, emptySubtitle }
            };
            _promptListPanel.Children.Add(AppTheme.CreateCard(emptyContent));
            return;
        }

        // "Improve Transcription" (the seeded default, immutable key "improve-accuracy") stays
        // pinned at the top in its own section; every other prompt is listed alphabetically below
        // (UAT 2026-07-22). Match by SeedKey so a rename never unpins it, and sort for DISPLAY only —
        // the ViewModel's collection order (and each prompt's IsActive flag) is untouched.
        var pinnedKey = PromptTemplates.ImproveAccuracy.Key;
        var pinned = _viewModel.Prompts.Where(p => p.SeedKey == pinnedKey).ToList();
        var rest = _viewModel.Prompts.Where(p => p.SeedKey != pinnedKey)
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var prompt in pinned)
            _promptListPanel.Children.Add(BuildPromptCard(prompt));

        if (pinned.Count > 0 && rest.Count > 0)
            _promptListPanel.Children.Add(new Border
            {
                Height = 1,
                Background = AppTheme.Brush(AppTheme.CardBorderColor),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

        foreach (var prompt in rest)
            _promptListPanel.Children.Add(BuildPromptCard(prompt));
    }

    private UIElement BuildPromptCard(CustomPrompt prompt)
    {
        // Active indicator: green dot when active, dim dot when inactive
        var indicator = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = prompt.IsActive
                ? AppTheme.Brush(AppTheme.AccentGreen)
                : AppTheme.Brush(AppTheme.DimText),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 12, 0)
        };

        // Title
        var titleBlock = new TextBlock
        {
            Text = prompt.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        // Description (if available)
        UIElement? descriptionBlock = null;
        if (!string.IsNullOrEmpty(prompt.Description))
        {
            descriptionBlock = new TextBlock
            {
                Text = prompt.Description,
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 2, 0, 0)
            };
        }

        // Prompt text — show full text for active prompt, truncated for others
        var previewBlock = new TextBlock
        {
            Text = prompt.PromptText,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxLines = prompt.IsActive ? 0 : 3,
            TextTrimming = prompt.IsActive ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };

        // For inactive prompts, allow click to expand/collapse
        if (!prompt.IsActive)
        {
            var isExpanded = false;
            previewBlock.Tapped += (_, _) =>
            {
                isExpanded = !isExpanded;
                previewBlock.MaxLines = isExpanded ? 0 : 3;
                previewBlock.TextTrimming = isExpanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            };
        }

        // Type badge: "Image" or "Text"
        var typeBadgeColor = prompt.IsImageGeneration
            ? ColorHelper.FromArgb(255, 175, 82, 222) // purple for image
            : AppTheme.AccentBlue;
        var typeBadgeBg = prompt.IsImageGeneration
            ? ColorHelper.FromArgb(30, 175, 82, 222)
            : ColorHelper.FromArgb(30, 0, 122, 255);
        var typeBadge = new Border
        {
            Background = AppTheme.Brush(typeBadgeBg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = prompt.IsImageGeneration ? "Image" : "Text",
                FontSize = 11,
                Foreground = AppTheme.Brush(typeBadgeColor)
            }
        };

        // Model override badge (only if set)
        UIElement? modelOverrideBadge = null;
        if (!string.IsNullOrWhiteSpace(prompt.ModelOverride))
        {
            modelOverrideBadge = new Border
            {
                Background = AppTheme.Brush(ColorHelper.FromArgb(30, 0, 122, 255)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = $"Model: {prompt.ModelOverride}",
                    FontSize = 11,
                    Foreground = AppTheme.Brush(AppTheme.AccentBlue)
                }
            };
        }

        // Active badge — OMITTED entirely when inactive, not added collapsed. FlowPanel measures
        // every child and adds HorizontalSpacing after it, so a zero-size collapsed child would
        // leave a phantom gap before the first visible badge.
        UIElement? activeBadge = null;
        if (prompt.IsActive)
        {
            activeBadge = new Border
            {
                Background = AppTheme.Brush(ColorHelper.FromArgb(30, 48, 209, 88)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = "Active",
                    FontSize = 11,
                    Foreground = AppTheme.Brush(AppTheme.AccentGreen)
                }
            };
        }

        // Trigger words display
        UIElement? triggerWordsDisplay = null;
        if (prompt.TriggerWords.Count > 0)
        {
            var triggerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0)
            };
            triggerPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE720", // microphone
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                VerticalAlignment = VerticalAlignment.Center
            });
            foreach (var word in prompt.TriggerWords)
            {
                var conflicting = FindConflictingPrompt(word, prompt);
                var isConflict = conflicting != null;
                var pillBg = isConflict
                    ? ColorHelper.FromArgb(30, 255, 149, 0) // amber tint
                    : ColorHelper.FromArgb(30, 0, 122, 255);
                var pillFg = isConflict ? AppTheme.WarningText : AppTheme.AccentBlue;

                var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                if (isConflict)
                {
                    pillContent.Children.Add(new FontIcon
                    {
                        Glyph = "\uE7BA", // warning
                        FontSize = 10,
                        Foreground = AppTheme.Brush(pillFg),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                pillContent.Children.Add(new TextBlock
                {
                    Text = $"\"{word}\"",
                    FontSize = 11,
                    Foreground = AppTheme.Brush(pillFg)
                });

                var pill = new Border
                {
                    Background = AppTheme.Brush(pillBg),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Child = pillContent
                };

                if (isConflict)
                {
                    ToolTipService.SetToolTip(pill,
                        $"Also used by \"{conflicting!.Title}\" — only the first match in the list will trigger");
                }

                triggerPanel.Children.Add(pill);
            }
            triggerWordsDisplay = triggerPanel;
        }

        // Hotkey badge display
        UIElement? hotkeyDisplay = null;
        if (!string.IsNullOrEmpty(prompt.Hotkey))
        {
            var hotkeyPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0)
            };
            hotkeyPanel.Children.Add(new FontIcon
            {
                Glyph = "\uE765", // keyboard
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                VerticalAlignment = VerticalAlignment.Center
            });
            hotkeyPanel.Children.Add(new Border
            {
                Background = AppTheme.Brush(ColorHelper.FromArgb(30, 48, 209, 88)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = HotkeyKeyDisplay.Describe(prompt.Hotkey ?? string.Empty),
                    FontSize = 11,
                    Foreground = AppTheme.Brush(AppTheme.AccentGreen)
                }
            });
            hotkeyDisplay = hotkeyPanel;
        }

        // Every badge on ONE line (owner 2026-08-05): Active leads, then the blue informational
        // ones. Active used to sit on its own row below them. FlowPanel rather than a horizontal
        // StackPanel because "Model: <id>" can be long enough to overflow a narrow card — it wraps
        // instead of clipping.
        var badgeRow = new FlowPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6,
            Margin = new Thickness(0, 6, 0, 0)
        };
        if (activeBadge != null) badgeRow.Children.Add(activeBadge);
        badgeRow.Children.Add(typeBadge);
        if (modelOverrideBadge != null) badgeRow.Children.Add(modelOverrideBadge);

        var textPanel = new StackPanel();
        textPanel.Children.Add(titleBlock);
        if (descriptionBlock != null) textPanel.Children.Add(descriptionBlock);
        textPanel.Children.Add(previewBlock);
        textPanel.Children.Add(badgeRow);
        if (triggerWordsDisplay != null) textPanel.Children.Add(triggerWordsDisplay);
        if (hotkeyDisplay != null) textPanel.Children.Add(hotkeyDisplay);

        // Action buttons row
        var buttonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Spacing = 8
        };

        var setActiveBtn = AppTheme.CreateSecondaryButton(
            prompt.IsActive ? "Deactivate" : "Activate",
            async (s, e) =>
            {
                // If activating while enhancement is off, ask to enable it
                if (!prompt.IsActive && !_viewModel.IsEnabled)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "AI Enhancement Disabled",
                        Content = "AI enhancement is currently off. Enable it to use this prompt?",
                        PrimaryButtonText = "Enable",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = AppTheme.ElementTheme
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                        return;
                    _viewModel.IsEnabled = true;
                }
                _viewModel.SetActivePromptCommand.Execute(prompt);
                BuildUI(); // rebuild to reflect toggle + prompt state
            });
        buttonsRow.Children.Add(setActiveBtn);

        var editBtn = AppTheme.CreateSecondaryButton("Edit", (s, e) =>
        {
            ShowEditPromptDialog(prompt);
        });
        buttonsRow.Children.Add(editBtn);

        // Trigger words button
        var triggerBtn = AppTheme.CreateSecondaryButton("Trigger Words", (s, e) =>
        {
            ShowTriggerWordDialog(prompt);
        });
        buttonsRow.Children.Add(triggerBtn);

        // Hotkey button
        var hotkeyBtnLabel = string.IsNullOrEmpty(prompt.Hotkey)
            ? "Hotkey"
            : $"Hotkey ({HotkeyKeyDisplay.Describe(prompt.Hotkey)})";
        var hotkeyBtn = AppTheme.CreateSecondaryButton(hotkeyBtnLabel, (s, e) =>
        {
            ShowHotkeyDialog(prompt);
        });
        buttonsRow.Children.Add(hotkeyBtn);

        // Configure button (type + model override)
        var configBtn = AppTheme.CreateSecondaryButton("Configure", (s, e) =>
        {
            ShowConfigureDialog(prompt);
        });
        buttonsRow.Children.Add(configBtn);

        var deleteBtn = AppTheme.CreateDangerButton("Delete", async (s, e) =>
        {
            // Warn when deleting this enhancement structurally changes something: it's the active
            // prompt, and/or one or more App Modes link to it (Option C — the user must consciously
            // accept the change). Copy is truthful: a deleted enhancement isn't replaced by a fixed
            // "default prompt" — runtime just follows the user's normal enhancement settings.
            var linkedNames = App.Services.GetRequiredService<AppModeManager>()
                .GetAppModeNamesLinkedTo(prompt.Id);
            if (prompt.IsActive || linkedNames.Count > 0)
            {
                var msg = $"Delete \"{prompt.Title}\"?";
                if (prompt.IsActive)
                    msg += "\n\nIt's currently the active enhancement.";
                if (linkedNames.Count > 0)
                    msg += $"\n\nIt's used by: {string.Join(", ", linkedNames)}. Those app modes will "
                         + "no longer use this enhancement and will follow your normal enhancement settings.";

                var dialog = new ContentDialog
                {
                    Title = "Delete enhancement",
                    // Scroller so a long list of affected app-mode names can't clip the warning
                    // (the requirement is that the user actually SEES which modes change) — Codex r1.
                    Content = AppTheme.CreateDialogScroller(new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap }),
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            _viewModel.DeletePromptCommand.Execute(prompt);
            RefreshPromptList();
        });
        buttonsRow.Children.Add(deleteBtn);

        // Main content grid: indicator dot + text panel
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(indicator, 0);
        Grid.SetColumn(textPanel, 1);
        contentGrid.Children.Add(indicator);
        contentGrid.Children.Add(textPanel);

        var innerPanel = new StackPanel
        {
            Children = { contentGrid, buttonsRow }
        };

        // Active card gets a left blue accent border; inactive uses standard card
        if (prompt.IsActive)
        {
            return new Border
            {
                Background = AppTheme.Brush(AppTheme.CardBg),
                BorderBrush = AppTheme.Brush(AppTheme.AccentBlue),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
                Padding = new Thickness(AppTheme.CardPadding),
                Margin = new Thickness(0, 0, 0, 4),
                Child = innerPanel
            };
        }

        return AppTheme.CreateCard(innerPanel);
    }

    /// <summary>
    /// Find another prompt whose trigger word MATCHES this one at runtime. Uses the
    /// same token canonicalization as the matcher (PRM-4: TriggerEchoGate token
    /// spans — punctuation/whitespace/case-insensitive), so triggers that collide in
    /// detection ("hey assistant" vs "Hey, assistant") are flagged here too instead
    /// of only literal duplicates (Codex round 4). Returns null if no conflict.
    /// </summary>
    private CustomPrompt? FindConflictingPrompt(string triggerWord, CustomPrompt current)
    {
        static string Canonical(string w) =>
            string.Join(' ', Helpers.TriggerEchoGate.TokenizeSpans(w).Select(t => t.Token));

        var canonical = Canonical(triggerWord);
        return _viewModel.Prompts.FirstOrDefault(p =>
            p.Id != current.Id &&
            p.TriggerWords.Any(w => Canonical(w) == canonical));
    }

    private async void ShowEditPromptDialog(CustomPrompt prompt, bool isNew = false)
    {
        try
        {
        var dialog = new ContentDialog
        {
            Title = isNew ? "New AI Enhancement" : "Edit AI Enhancement",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme
        };

        var nameBox = new TextBox
        {
            Text = prompt.Title,
            PlaceholderText = "Prompt name",
            Margin = new Thickness(0, 0, 0, 16)
        };

        var promptBox = new TextBox
        {
            Text = prompt.PromptText,
            PlaceholderText = "Enter your prompt instructions",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 200
        };

        // Keep Save disabled until both fields have real content, so a blank New Prompt can't be
        // "saved" into a silent no-op (Codex diff r2). An existing prompt opens pre-filled, so Save
        // starts enabled and only disables if the user clears a field.
        void UpdateSaveEnabled() =>
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(promptBox.Text);
        nameBox.TextChanged += (_, _) => UpdateSaveEnabled();
        promptBox.TextChanged += (_, _) => UpdateSaveEnabled();
        UpdateSaveEnabled();

        var content = new StackPanel
        {
            Width = 450,
            Children =
            {
                new TextBlock
                {
                    Text = "Name",
                    FontSize = 13,
                    Foreground = AppTheme.Brush(AppTheme.SubtleText),
                    Margin = new Thickness(0, 0, 0, 6)
                },
                nameBox,
                new TextBlock
                {
                    Text = "Prompt",
                    FontSize = 13,
                    Foreground = AppTheme.Brush(AppTheme.SubtleText),
                    Margin = new Thickness(0, 0, 0, 6)
                },
                promptBox
            }
        };

        if (prompt.IsImageGeneration)
        {
            // The image request's prompt is the PROCESSED dictation (text pipeline +
            // trigger-word strip, or whatever the user edits in the generation
            // dialog) — never this PromptText, which is descriptive UI copy only
            // (PRM-2, prompt-stack audit F11). Say so — users editing it expecting
            // behavior change get nothing.
            content.Children.Add(new TextBlock
            {
                Text = "Note: for image prompts, your processed dictation (or the text you edit in the generation dialog) is what's sent to the image model — this description is not part of the request.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        // Height-flexible dialog scroller (repo rule): a bare StackPanel in a
        // ContentDialog clips on constrained displays — it never scrolls.
        dialog.Content = AppTheme.CreateDialogScroller(content);

        var result = await dialog.ShowAsync();

        var newTitle = nameBox.Text.Trim();
        var newText = promptBox.Text.Trim();
        if (result == ContentDialogResult.Primary
            && !string.IsNullOrEmpty(newTitle) && !string.IsNullOrEmpty(newText))
        {
            // A new prompt is added to the collection ONLY here, on a valid Save; an existing
            // prompt is updated in place.
            if (isNew)
                _viewModel.CommitNewPrompt(prompt, newTitle, newText);
            else
                _viewModel.UpdatePrompt(prompt, newTitle, newText);
            RefreshPromptList();
        }
        // Cancelled or confirmed with an empty name/body: nothing to clean up — a new prompt's
        // draft was never added to the collection (and neither is an existing prompt changed), so
        // even a dialog that threw before this point leaves the collection untouched.
        }
        catch (Exception ex) { Logger.Error(ex, "ShowEditPromptDialog failed"); }
    }

    private async void ShowTemplateDialog()
    {
        try
        {
        var dialog = new ContentDialog
        {
            Title = "Add from Template",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme
        };

        var templateList = new StackPanel { Spacing = 8 };

        var availableTemplates = _viewModel.GetAvailableTemplates();
        if (availableTemplates.Length == 0)
        {
            templateList.Children.Add(new TextBlock
            {
                Text = "All templates have already been added.",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 8, 0, 8)
            });
        }

        foreach (var template in availableTemplates)
        {
            var iconBlock = new FontIcon
            {
                FontSize = 20,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            try
            {
                var codePoint = Convert.ToInt32(template.Icon, 16);
                iconBlock.Glyph = char.ConvertFromUtf32(codePoint);
            }
            catch { iconBlock.Glyph = char.ConvertFromUtf32(0xE945); }

            var titleText = new TextBlock
            {
                Text = template.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.TextPrimary)
            };

            var descText = new TextBlock
            {
                Text = template.Description,
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.SubtleText)
            };

            var textPanel = new StackPanel
            {
                Children = { titleText, descText }
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { iconBlock, textPanel }
            };

            var card = new Border
            {
                Background = AppTheme.Brush(AppTheme.CardBg),
                BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var normalBg = AppTheme.Brush(AppTheme.CardBg);
            var hoverBg = AppTheme.Brush(AppTheme.HoverBg);
            card.Child = row;
            card.PointerEntered += (_, _) => card.Background = hoverBg;
            card.PointerExited += (_, _) => card.Background = normalBg;

            var capturedTemplate = template;
            card.Tapped += (_, _) =>
            {
                _viewModel.AddFromTemplate(capturedTemplate);
                RefreshPromptList();
                dialog.Hide();
            };

            templateList.Children.Add(card);
        }

        dialog.Content = new ScrollViewer
        {
            Content = templateList,
            MaxHeight = 400
        };

        await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Error(ex, "ShowTemplateDialog failed"); }
    }

    private async void ShowTriggerWordDialog(CustomPrompt prompt)
    {
        try
        {
        var dialog = new ContentDialog
        {
            Title = $"Trigger Words for \"{prompt.Title}\"",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme
        };

        var infoText = new TextBlock
        {
            Text = "Trigger words let you activate this prompt by voice. Say the trigger word at the start or end of your sentence, and VoiceWink will automatically use this prompt for that transcription.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var exampleText = new TextBlock
        {
            Text = "Example: Set \"Hey assistant\" as a trigger, then say \"Hey assistant, what's the capital of France?\"",
            FontSize = 12,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var triggerBox = new TextBox
        {
            Text = string.Join(", ", prompt.TriggerWords),
            PlaceholderText = "e.g. Hey assistant, Dear AI",
            AcceptsReturn = false
        };

        var hintText = new TextBlock
        {
            Text = "Separate multiple trigger words with commas.",
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var warningText = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        triggerBox.TextChanged += (_, _) =>
        {
            var words = triggerBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w.Length > 0);
            var conflicts = new List<string>();
            foreach (var w in words)
            {
                var other = FindConflictingPrompt(w, prompt);
                if (other != null)
                    conflicts.Add($"\"{w}\" is also used by \"{other.Title}\"");
            }
            if (conflicts.Count > 0)
            {
                warningText.Text = string.Join("; ", conflicts) + " — only the first match in the list will trigger.";
                warningText.Visibility = Visibility.Visible;
            }
            else
            {
                warningText.Visibility = Visibility.Collapsed;
            }
        };

        dialog.Content = new StackPanel
        {
            Width = 400,
            Children = { infoText, exampleText, triggerBox, hintText, warningText }
        };

        dialog.PrimaryButtonClick += (_, _) =>
        {
            var words = triggerBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w.Length > 0)
                .ToList();
            _viewModel.UpdateTriggerWords(prompt, words);
            RefreshPromptList();
        };

        await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Error(ex, "ShowTriggerWordDialog failed"); }
    }

    private async void ShowHotkeyDialog(CustomPrompt prompt)
    {
        try
        {
            var settingsSvc = App.Services.GetRequiredService<SettingsService>();

            // Read every role together, from the one place that knows what the roles ARE. The four
            // hand-read locals this replaces were a third private list of the hotkey roles, and the
            // review found each such list missing something different.
            HotkeyBindingSnapshot PromptConflictSnapshot() => HotkeyBindingSnapshot.FromSettings(
                settingsSvc.GetString,
                [.. _viewModel.Prompts
                    .Where(p => !string.IsNullOrWhiteSpace(p.Hotkey))
                    .Select(p => new PromptHotkey(p.Id, p.Title, p.Hotkey))]);

            var dialog = new ContentDialog
            {
                Title = $"Hotkey for \"{prompt.Title}\"",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };

            var infoText = new TextBlock
            {
                Text = "Assign a hotkey to activate this prompt. Press the hotkey to start recording — the transcription will be enhanced using this prompt.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var hotkeyCombo = new ComboBox
            {
                Width = 200
            };
            AppTheme.PopulateHotkeyCombo(hotkeyCombo, includeNone: true);
            // Items are display names; the stored prompt hotkey is canonical.
            hotkeyCombo.SelectedItem = string.IsNullOrEmpty(prompt.Hotkey)
                ? "None (disabled)"
                : HotkeyKeyDisplay.ToDisplayToken(prompt.Hotkey);

            var warningText = new TextBlock
            {
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            hotkeyCombo.SelectionChanged += (_, _) =>
            {
                var selected = hotkeyCombo.SelectedItem as string;
                if (string.IsNullOrEmpty(selected) || selected == "None (disabled)")
                {
                    warningText.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = true;
                    return;
                }

                // The item is a display name ("Right Alt"); the scan and storage speak canonical.
                var selectedCanonical = HotkeyKeyDisplay.ToCanonicalToken(selected);

                // ONE scan over every role AND every other prompt — this dialog used to keep its
                // own four-role list, the third such copy in the app, and each copy was missing
                // something different (Codex diff rounds 4 and 5).
                var collisions = HotkeyConflictScan.ForPrompt(PromptConflictSnapshot(), selectedCanonical, prompt.Id);
                if (collisions.Count == 0)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    warningText.Visibility = Visibility.Collapsed;
                    return;
                }

                // The RECORDING role is the one that blocks — it must always exist, so a prompt may
                // never take it. Everything else warns and proceeds.
                var blocking = collisions.FirstOrDefault(c => c.Role == HotkeyRole.Recording);
                if (blocking.ExistingBinding is not null)
                {
                    warningText.Text = $"⚠ {selected} is your recording hotkey and cannot be used here.";
                    warningText.Visibility = Visibility.Visible;
                    dialog.IsPrimaryButtonEnabled = false;
                    return;
                }

                dialog.IsPrimaryButtonEnabled = true;
                var first = collisions[0];
                warningText.Text = first.IsPrompt
                    ? $"{selected} is already assigned to \"{first.PromptTitle}\". Saving will move it to this prompt."
                    : $"⚠ {selected} is {first.Describe()}. This will override that function.";
                warningText.Visibility = Visibility.Visible;
            };

            dialog.Content = new StackPanel
            {
                Width = 400,
                Children = { infoText, hotkeyCombo, warningText }
            };

            dialog.PrimaryButtonClick += (_, _) =>
            {
                var selected = hotkeyCombo.SelectedItem as string;
                var newHotkey = selected is null || selected == "None (disabled)"
                    ? null
                    : HotkeyKeyDisplay.ToCanonicalToken(selected);

                // Clear conflicting hotkey from other prompts.
                //
                // HKY-3: these use the SAME predicates as the warnings above, and that is a
                // correctness requirement rather than tidiness (Grok diff review r2). String
                // equality here while the warnings parse means warn and act can disagree: a
                // stored "Control + F6" warns "will override" and then clears nothing, and
                // RegisterPromptHotkeys afterwards shadows the new prompt off the hook — the
                // user is told the conflict was resolved and left with a dead hotkey.
                if (!string.IsNullOrEmpty(newHotkey))
                {
                    // Prompt-vs-prompt: both single-key, so Conflicts is the right question.
                    foreach (var other in _viewModel.Prompts.Where(p =>
                                 p.Id != prompt.Id && HotkeyBinding.Conflicts(p.Hotkey, newHotkey)))
                        other.Hotkey = null;

                    // Clear whatever ROLE this prompt takes over, from the same scan the warning
                    // used — so what the user was told and what happens cannot disagree. Recording
                    // is unreachable here: the Save button is disabled when it collides.
                    var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
                    foreach (var collision in HotkeyConflictScan.ForPrompt(
                                 PromptConflictSnapshot(), newHotkey, prompt.Id))
                    {
                        if (collision.Role is { } role and not HotkeyRole.Recording)
                            settingsVm.SetHotkey(role, "");
                    }
                }

                _viewModel.UpdateHotkey(prompt, newHotkey);
                RefreshPromptList();
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Error(ex, "ShowHotkeyDialog failed"); }
    }

    private async void ShowConfigureDialog(CustomPrompt prompt)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = $"Configure \"{prompt.Title}\"",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };
            // Lifecycle fence (PR C): RefreshModelOptions awaits a per-provider model
            // fetch; if the dialog is dismissed during it, the continuation must not
            // touch the combo. Set in Closed, checked after the await.
            var dialogClosed = false;
            dialog.Closed += (_, _) => dialogClosed = true;

            // ── Name ───────────────────────────────────────────────────────
            var nameLabel = new TextBlock
            {
                Text = "Name",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var nameBox = new TextBox
            {
                Text = prompt.Title,
                PlaceholderText = "Enhancement name",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // ── Enhancement Type ────────────────────────────────────────
            var typeLabel = new TextBlock
            {
                Text = "Enhancement Type",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6)
            };

            var typeCombo = new ComboBox { Width = 220 };
            typeCombo.Items.Add("Text Enhancement");
            typeCombo.Items.Add("Image Generation");
            typeCombo.SelectedIndex = prompt.IsImageGeneration ? 1 : 0;

            var typeHint = new TextBlock
            {
                Text = "Text Enhancement sends transcription to a chat model.\nImage Generation creates an image from your description.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };

            // ── Provider Override ────────────────────────────────────────
            var providerLabel = new TextBlock
            {
                Text = "Provider Override",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6)
            };

            const string defaultProviderLabel = "(Default \u2014 uses main selection)";
            var providerCombo = new ComboBox { Width = 320 };
            providerCombo.Items.Add(defaultProviderLabel);
            foreach (var p in EnhancementViewModel.AvailableProviders)
                providerCombo.Items.Add(p.ToString());

            // Pre-select from saved override
            if (!string.IsNullOrEmpty(prompt.ProviderOverride)
                && Enum.TryParse<AIProvider>(prompt.ProviderOverride, out _))
            {
                providerCombo.SelectedItem = prompt.ProviderOverride;
            }
            else
            {
                providerCombo.SelectedIndex = 0;
            }

            var providerHint = new TextBlock
            {
                Text = "Leave on default to use the main provider. Set a specific provider to override.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };

            // ── Model Override ───────────────────────────────────────────
            var modelLabel = new TextBlock
            {
                Text = "Model Override",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6)
            };

            const string defaultModelLabel = "(Default \u2014 uses main selection)";
            var modelCombo = new ComboBox
            {
                IsEditable = true,
                PlaceholderText = defaultModelLabel,
                Width = 320
            };
            AppTheme.AllowParentScroll(modelCombo);

            var modelHint = new TextBlock
            {
                Text = "Select (Default) or clear the text to use the main model. Pick or type a model to override.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };

            // ── Image Aspect ────────────────────────────────────────────────
            var aspectLabel = new TextBlock
            {
                Text = "Aspect Ratio",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6),
                Visibility = prompt.IsImageGeneration ? Visibility.Visible : Visibility.Collapsed
            };

            var aspectCombo = new ComboBox { Width = 220 };
            AppTheme.PopulateImageAspectComboWithIndicators(aspectCombo, prompt.ImageAspect);
            aspectCombo.Visibility = aspectLabel.Visibility;

            // ── Image Size (resolution tier) ────────────────────────────────
            var sizeLabel = new TextBlock
            {
                Text = "Size",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 12, 0, 6),
                Visibility = aspectLabel.Visibility
            };

            var sizeCombo = new ComboBox { Width = 220 };
            AppTheme.PopulateImageSizeTierComboWithIndicators(sizeCombo, prompt.ImageSizeTier);
            sizeCombo.Visibility = aspectLabel.Visibility;

            var sizeHint = new TextBlock
            {
                Text = "Auto lets the model pick. Higher tiers cost more / take longer to generate.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = aspectLabel.Visibility
            };

            // ── Image Quality (detail) ──────────────────────────────────────
            // Hidden when the resolved provider is Gemini — Gemini-native models have no
            // separate detail knob (their imageSize covers it). The visibility is recomputed
            // when typeCombo or providerCombo changes.
            var qualityLabel = new TextBlock
            {
                Text = "Quality",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 12, 0, 4),
                Visibility = aspectLabel.Visibility
            };
            var qualityCombo = new ComboBox { Width = 220 };
            // IMG-12: the standing REQUEST, tracked apart from the combo's tag (which only ever
            // holds the value clamped to the CURRENT model). See QualitySelectionTracker.
            var qualityAsk = new QualitySelectionTracker(prompt.ImageQuality);
            using (qualityAsk.BeginPopulate())
            {
                AppTheme.PopulateImageQualityComboWithIndicators(qualityCombo, prompt.ImageQuality);
            }
            qualityCombo.SelectionChanged += (_, _) =>
            {
                if (qualityAsk.IsPopulating) return;
                qualityAsk.NoteUserPick(AppTheme.SelectedIndicatorTag(qualityCombo));
            };
            qualityCombo.Visibility = aspectLabel.Visibility;

            var qualityHint = new TextBlock
            {
                Text = "Higher quality = more tokens / longer rendering. Auto picks per the model's default.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = aspectLabel.Visibility
            };

            // Resolve the effective image provider from the combo (Default → main selection).
            AIProvider EffectiveProvider()
            {
                var sel = providerCombo.SelectedItem as string;
                if (sel == defaultProviderLabel) return _viewModel.SelectedImageProvider;
                return Enum.TryParse<AIProvider>(sel, out var p) ? p : _viewModel.SelectedImageProvider;
            }

            // Resolve the effective image model from the combo (Default → null, treated as
            // "unknown" — capability defaults will apply per the provider's typical model).
            string? EffectiveModel()
            {
                var raw = modelCombo.SelectedItem as string ?? modelCombo.Text;
                if (string.IsNullOrEmpty(raw) || raw == defaultModelLabel) return null;
                return raw;
            }

            // Re-filter the aspect / size / quality dropdowns based on the resolved provider+model.
            // Also toggles visibility of the Size + Quality sub-sections when the model doesn't
            // accept those parameters at all (e.g. gpt-image-1.x has no tier; Gemini has no quality).
            void RefreshImageOptionGating()
            {
                if (typeCombo.SelectedIndex != 1) return; // only meaningful on the image branch
                var provider = EffectiveProvider();
                // IMG-5: "(Default)" must gate against the model the RUNTIME would pick, not the
                // provider's hardcoded default. Resolved with a NULL prompt deliberately — that is
                // the "no override" view (persisted-for-provider, else provider default). Passing
                // the real prompt would honour prompt.ModelOverride, which is STALE mid-edit: the
                // user's in-progress pick lives in the combo and hasn't been saved yet.
                var model = ImageOptionGating.ResolveGatingModel(
                    EffectiveModel(),
                    previousModel: null,
                    _enhancement.ResolveEffectiveImageModel(null, provider));
                var gating = ImageOptionGating.Decide(
                    provider, model, _enhancement.ImageCapabilitiesFor(provider, model));
                var tiers = gating.Tiers;

                // Hide AND skip repopulation when the model publishes no aspect_ratio — populating
                // a row we then hide would fall the combo back to Auto and erase the saved aspect
                // on the next Save (PopulateIndicatorCombo atomically replaces the rows, preserving
                // a supported tag; a hidden row's tag is never among them). Mirrors the tier path
                // below.
                var aspectVis = gating.ShowAspect ? Visibility.Visible : Visibility.Collapsed;
                aspectLabel.Visibility = aspectVis;
                aspectCombo.Visibility = aspectVis;
                if (gating.ShowAspect)
                {
                    AppTheme.PopulateImageAspectComboWithIndicators(
                        aspectCombo,
                        AppTheme.SelectedIndicatorTag(aspectCombo) ?? prompt.ImageAspect,
                        gating.Aspects);
                }

                var tierVis = tiers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                sizeLabel.Visibility = tierVis;
                sizeCombo.Visibility = tierVis;
                sizeHint.Visibility = tierVis;
                if (tiers.Count > 0)
                {
                    AppTheme.PopulateImageSizeTierComboWithIndicators(
                        sizeCombo,
                        AppTheme.SelectedIndicatorTag(sizeCombo) ?? prompt.ImageSizeTier,
                        tiers);
                }

                // IMG-12: per-model quality tiers, so this row repopulates like the two above it
                // (and skips when hidden, so Save cannot erase a tag the model does not expose).
                // The selection is CLAMPED, matching what NormalizeForModel would put on the wire.
                var qVis = gating.ShowQuality ? Visibility.Visible : Visibility.Collapsed;
                qualityLabel.Visibility = qVis;
                qualityCombo.Visibility = qVis;
                qualityHint.Visibility = qVis;
                if (gating.ShowQuality)
                {
                    // Display = the ask, clamped to THIS model; the ask is untouched here.
                    using (qualityAsk.BeginPopulate())
                    {
                        AppTheme.PopulateImageQualityComboWithIndicators(
                            qualityCombo,
                            ImageOptions.ClampQuality(qualityAsk.Requested, gating.Qualities),
                            gating.Qualities);
                    }
                }
            }
            RefreshImageOptionGating();
            // UI-18: and again once the dialog is on screen. The populate above runs before
            // ShowAsync, and an ItemsSource swap while the combo is detached leaves the closed box
            // blank though the selection itself is correct — see PopulateIndicatorCombo. Safe here
            // because Opened precedes any user interaction, so each row re-derives the same tag it
            // derived pre-show; on a text prompt the gating's own early return makes this a no-op.
            dialog.Opened += (_, _) => RefreshImageOptionGating();

            // ── Ask for Image Options toggle ────────────────────────────────
            var askSizeToggle = new ToggleSwitch
            {
                IsOn = prompt.AskImageSize,
                OnContent = "Yes",
                OffContent = "No",
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = prompt.IsImageGeneration ? Visibility.Visible : Visibility.Collapsed
            };
            var askSizeLabel = new TextBlock
            {
                Text = "Ask for image options each time",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = askSizeToggle.Visibility
            };
            var askSizeHint = new TextBlock
            {
                Text = "When enabled, you'll pick aspect / size / quality each time before generating.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = askSizeToggle.Visibility
            };

            // Show/hide image-specific controls when type changes
            typeCombo.SelectionChanged += (_, _) =>
            {
                var vis = typeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                aspectLabel.Visibility = vis;
                aspectCombo.Visibility = vis;
                sizeLabel.Visibility = vis;
                sizeCombo.Visibility = vis;
                sizeHint.Visibility = vis;
                qualityLabel.Visibility = vis;
                qualityCombo.Visibility = vis;
                qualityHint.Visibility = vis;
                askSizeLabel.Visibility = vis;
                askSizeToggle.Visibility = vis;
                askSizeHint.Visibility = vis;
                if (typeCombo.SelectedIndex == 1) RefreshImageOptionGating();
            };
            // Provider / model changes affect which aspects / tiers are valid + whether Quality
            // applies — re-filter on every change.
            providerCombo.SelectionChanged += (_, _) => RefreshImageOptionGating();
            modelCombo.SelectionChanged    += (_, _) => RefreshImageOptionGating();
            // NOTE: typed models are handled by the TextSubmitted handler further down, which
            // already re-gates AND updates pendingModelOverride. Do not add a second registration
            // here — it fires the same idempotent refresh twice per commit.

            // ── Model list refresh logic ─────────────────────────────────
            // The dialog tracks its LIVE model choice (pendingModelOverride) and routes
            // every refresh through PromptModelOverridePolicy. Restoring from the
            // persisted prompt.ModelOverride here clobbered an in-dialog revert to
            // "(Default)" on the next provider/type change, and carried a model from
            // provider X into provider Y as free text (2026-07-08: claude-fable-5
            // persisted under provider=(default), then sent to OpenAI, then 404).
            int refreshGeneration = 0;
            string? pendingModelOverride = prompt.ModelOverride;
            bool suppressModelTracking = false;

            PromptModelOverridePolicy.OverrideContext CurrentContext() => new(
                providerCombo.SelectedIndex != 0
                    && Enum.TryParse<AIProvider>(providerCombo.SelectedItem as string, out var p)
                    ? p : null,
                typeCombo.SelectedIndex == 1);

            var lastContext = CurrentContext();

            string? NormalizeModel(string? raw)
                => string.IsNullOrWhiteSpace(raw) || raw == defaultModelLabel ? null : raw.Trim();

            // User-driven changes update the pending choice; programmatic mutations
            // during a refresh are excluded via suppressModelTracking (the ItemsSource
            // swap / SelectedIndex / Text assignments raise SelectionChanged too).
            modelCombo.SelectionChanged += (_, _) =>
            {
                if (suppressModelTracking) return;
                pendingModelOverride = NormalizeModel(modelCombo.SelectedItem as string);
            };
            // Editable combo: typed (unlisted) models arrive via TextSubmitted, which
            // also affects the image aspect/tier/quality gating.
            modelCombo.TextSubmitted += (_, args) =>
            {
                if (suppressModelTracking) return;
                pendingModelOverride = NormalizeModel(args.Text);
                RefreshImageOptionGating();
            };

            async void RefreshModelOptions()
            {
                int gen = ++refreshGeneration;
                var nextContext = CurrentContext();

                // Fold the combo's LIVE state (typed-but-uncommitted text included —
                // TextSubmitted only fires on commit) into the pending value before
                // deciding, so a provider/type switch mid-edit judges what the user
                // actually sees (Codex diff round 1). Skipped while the combo is
                // still unpopulated (the dialog's very first refresh): the empty
                // combo would wipe the saved override before it is ever shown.
                if (modelCombo.Items.Count > 0)
                    pendingModelOverride = NormalizeModel(modelCombo.SelectedItem as string ?? modelCombo.Text);

                List<string> models;
                if (nextContext.ProviderOverride is not { } provider)
                {
                    // Use the already-loaded main model list
                    models = (nextContext.IsImage ? _viewModel.AvailableImageModels : _viewModel.AvailableModels).ToList();
                }
                else
                {
                    // Fetch models for the overridden provider
                    modelCombo.PlaceholderText = "Loading models\u2026";
                    models = await _viewModel.FetchModelsForProviderAsync(provider, nextContext.IsImage);
                    if (gen != refreshGeneration || dialogClosed) return; // stale switch, or dialog dismissed mid-fetch
                    modelCombo.PlaceholderText = defaultModelLabel;
                }

                // Decide the selection under the new context BEFORE mutating the combo;
                // a stale override (different provider or text/image domain, absent from
                // the new list) resets to "(Default)".
                pendingModelOverride = PromptModelOverridePolicy.NextSelection(
                    lastContext, nextContext, pendingModelOverride, models);
                lastContext = nextContext;

                suppressModelTracking = true;
                try
                {
                    // Single ItemsSource swap, never Items.Clear()+Add loop: churning a
                    // live IsEditable combo's item collection corrupts native combo/popup
                    // state (the PR #163 0x80070490 class). Build the list (sentinel
                    // first) then assign once.
                    var items = new List<string>(models.Count + 1) { defaultModelLabel };
                    items.AddRange(models);
                    modelCombo.ItemsSource = items;

                    if (string.IsNullOrEmpty(pendingModelOverride))
                        modelCombo.SelectedIndex = 0; // "(Default — uses main selection)"
                    else if (items.Contains(pendingModelOverride))
                        modelCombo.SelectedItem = pendingModelOverride;
                    else
                        modelCombo.Text = pendingModelOverride;
                }
                finally
                {
                    suppressModelTracking = false;
                }

                // IMG-5: this fetch is what fills the capability cache, so re-gate against it.
                // SelectionChanged is NOT sufficient (the same reason App.xaml.cs re-gates
                // explicitly after its fetch): a programmatic `Text` assignment above raises no
                // SelectionChanged at all, and a reselection landing on the same string across the
                // ItemsSource swap raises none either. Without this the editor keeps offering the
                // static catch-all — krea with 3 tiers and 10 aspects — while a fresh snapshot
                // saying {1K} sits in the cache, until the user happens to touch a combo.
                RefreshImageOptionGating();
            }

            RefreshModelOptions();
            typeCombo.SelectionChanged += (_, _) => RefreshModelOptions();
            providerCombo.SelectionChanged += (_, _) => RefreshModelOptions();

            // ── Speech recognition language (PRM-5) ────────────────────────
            // UAT 14.7: translating reliably meant setting the GLOBAL transcription language to the
            // SOURCE language; the owner expected it on the enhancement doing the translating. NOT
            // hidden for trigger-word prompts (Codex plan review): the same prompt can also be
            // reached by hotkey or an App-Mode link, where the override DOES apply — the hint below
            // states the one case where it cannot.
            var languageLabel = new TextBlock
            {
                Text = "Speech recognition language",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6),
            };
            var languageCombo = new ComboBox { Width = 320 };
            AppTheme.AllowParentScroll(languageCombo);
            languageCombo.Items.Add(new ComboBoxItem { Content = "Use default", Tag = "" });
            languageCombo.Items.Add(new ComboBoxItem { Content = "Auto-detect", Tag = "auto" });
            foreach (var isoCode in ViewModels.ModelManagementViewModel.SupportedLanguages
                         .Where(c => c != "auto")
                         .OrderBy(c => ViewModels.ModelManagementViewModel.GetLanguageDisplayName(c),
                                  StringComparer.OrdinalIgnoreCase))
            {
                languageCombo.Items.Add(new ComboBoxItem
                {
                    Content = ViewModels.ModelManagementViewModel.GetLanguageDisplayName(isoCode),
                    Tag = isoCode,
                });
            }
            languageCombo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(prompt.LanguageOverride))
            {
                foreach (ComboBoxItem item in languageCombo.Items)
                {
                    if (item.Tag is string tag && tag == prompt.LanguageOverride)
                    {
                        languageCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            var languageHint = new TextBlock
            {
                // PRM-7 (owner UAT 2026-08-17, §28.1): §28.1 passed but the owner asked for this to
                // be simplified. Cut to one sentence plus one qualifier. The mid-recording caveat is
                // KEPT deliberately — §28's known-limit note says the limitation is stated, not
                // hidden — but it no longer re-explains what the label already says.
                // "before recording STARTS", not "before you start speaking" (Codex diff r1
                // blocker): the cutoff is the claim instant. `MainViewModel` snapshots
                // `_pendingPromptOverride` there (PRM-5, "AT THE CLAIM INSTANT"), so a user who
                // starts recording, pauses, and only then picks a prompt is already past it — the
                // looser wording promised them an override that does not apply. That inaccuracy
                // predates this change; shortening the hint is when it got noticed.
                Text = "Applies when this prompt is chosen before recording starts. Chosen "
                     + "mid-recording — by trigger word or prompt hotkey — recording keeps the "
                     + "language it started with.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };

#if DEBUG
            // ── Reasoning (ENH-8 phase A — DEBUG-only preview) ────────────
            // Text-enhancement only; the stored value is mapped to provider wire
            // parameters via ReasoningEffortPolicy and honored only in Debug builds.
            var reasoningLabel = new TextBlock
            {
                Text = "Reasoning (debug preview)",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                Margin = new Thickness(0, 16, 0, 6),
                Visibility = prompt.IsImageGeneration ? Visibility.Collapsed : Visibility.Visible
            };
            var reasoningCombo = new ComboBox { Width = 320, Visibility = reasoningLabel.Visibility };
            // ENH-23: for the models ReasoningEffortPolicy lists, "Default" is VoiceWink's dictation
            // default (the lowest reasoning effort the model accepts); a model with no row keeps the
            // provider's own choice, as every model did before ENH-23.
            reasoningCombo.Items.Add("Default (VoiceWink's setting for this model)");
            reasoningCombo.Items.Add("Minimal — fastest");
            reasoningCombo.Items.Add("Thorough — thinks longer");
            reasoningCombo.SelectedIndex = Helpers.ReasoningEffortPolicy.Parse(prompt.ReasoningOverride) switch
            {
                Helpers.ReasoningChoice.Minimal => 1,
                Helpers.ReasoningChoice.Thorough => 2,
                _ => 0
            };
            var reasoningHint = new TextBlock
            {
                Text = "On OpenAI and Gemini, Minimal equals Default (VoiceWink's row for the model, or nothing where it has none) and Thorough sends high; on Anthropic, Minimal is low and Thorough high — all three only for the families that take the parameter (GPT-5 except its chat variants, Gemini 2.5 and later, the Claude families that document effort; any other id sends nothing under every choice); on OpenRouter, Minimal sends low for the families it knows (never for a Pro model) and Thorough high. On Groq and Cerebras, Minimal is VoiceWink's default and Thorough hands the choice back to the provider. Mistral ignores it.",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = reasoningLabel.Visibility
            };
            typeCombo.SelectionChanged += (_, _) =>
            {
                var vis = typeCombo.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
                reasoningLabel.Visibility = vis;
                reasoningCombo.Visibility = vis;
                reasoningHint.Visibility = vis;
            };
#endif

            var contentPanel = new StackPanel
            {
                Width = 400,
                Children =
                {
                    nameLabel, nameBox,
                    typeLabel, typeCombo, typeHint,
                    aspectLabel, aspectCombo,
                    sizeLabel, sizeCombo, sizeHint,
                    qualityLabel, qualityCombo, qualityHint,
                    askSizeLabel, askSizeToggle, askSizeHint,
                    providerLabel, providerCombo, providerHint,
                    modelLabel, modelCombo, modelHint,
                    languageLabel, languageCombo, languageHint
                }
            };
#if DEBUG
            contentPanel.Children.Add(reasoningLabel);
            contentPanel.Children.Add(reasoningCombo);
            contentPanel.Children.Add(reasoningHint);
#endif
            // PRM-5: the language row makes the RELEASE dialog as tall as Debug already was, so both
            // now wrap in the height-flexible scroller — a bare StackPanel in a ContentDialog clips
            // instead of scrolling (repo rule; Codex plan review asked for this explicitly).
            dialog.Content = AppTheme.CreateDialogScroller(contentPanel);

            dialog.PrimaryButtonClick += (_, _) =>
            {
                var newName = nameBox.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                    prompt.Title = newName;
                var isImage = typeCombo.SelectedIndex == 1;
                var providerOverride = providerCombo.SelectedIndex == 0
                    ? null
                    : providerCombo.SelectedItem as string;
                var modelRaw = modelCombo.SelectedItem as string ?? modelCombo.Text;
                var modelOverride = modelRaw == defaultModelLabel ? null : modelRaw;
                // We persist the dropdown's current selection regardless of visibility — if the
                // user has set up a "2K" preference on gpt-image-2 and now flips the provider to
                // gemini-2.5-flash-image (no tier), hiding the dropdown shouldn't *erase* the
                // preference. The runtime ignores it on models that don't support it; the value
                // re-appears in the UI when the user returns to a tier-capable model.
                var imageAspect = AppTheme.SelectedIndicatorTag(aspectCombo);
                var imageSizeTier = AppTheme.SelectedIndicatorTag(sizeCombo);
                // IMG-12: quality persists its standing REQUEST, not the combo's tag. Same intent as
                // the comment above — "hiding the dropdown shouldn't erase the preference" — but the
                // quality row now needs it stated in code, because its rows are per-model: a
                // Maximum clamped to Enhanced on a narrow model and then hidden by a third model
                // would otherwise be SAVED as Enhanced and downgraded for good (Codex diff r1).
                var imageQuality = qualityAsk.Requested;
                var askImageSize = isImage && askSizeToggle.IsOn;
#if DEBUG
                // ENH-8: persisted via the same UpdatePromptConfig save (mutate-then-save,
                // the prompt.Title pattern). Selection persists regardless of type — the
                // runtime only applies it to text enhancement (image-options precedent:
                // hidden values are preserved, not erased).
                prompt.ReasoningOverride = reasoningCombo.SelectedIndex switch
                {
                    1 => Helpers.ReasoningEffortPolicy.MinimalValue,
                    2 => Helpers.ReasoningEffortPolicy.ThoroughValue,
                    _ => null
                };
#endif
                // PRM-5: same mutate-then-save pattern as ReasoningOverride/Title. "" (Use default)
                // normalizes to null so the property stays absent from the JSON.
                var languageTag = (languageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                prompt.LanguageOverride = string.IsNullOrWhiteSpace(languageTag) ? null : languageTag;
                _viewModel.UpdatePromptConfig(prompt, isImage, providerOverride, modelOverride,
                    imageAspect, imageSizeTier, askImageSize, imageQuality);
                RefreshPromptList();
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Error(ex, "ShowConfigureDialog failed"); }
    }

    private StackPanel CreateApiKeyStatusIndicator(
        Func<ApiKeyStatus> getStatus, string propertyName,
        Func<bool> getHasKey, string hasKeyPropertyName)
    {
        var icon = new FontIcon
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };

        var text = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, text }
        };

        void Apply()
        {
            var status = getStatus();
            switch (status)
            {
                case ApiKeyStatus.Valid:
                    icon.Glyph = "\uE73E"; // Checkmark
                    icon.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
                    text.Text = "Key valid";
                    text.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
                    panel.Visibility = Visibility.Visible;
                    break;
                case ApiKeyStatus.Invalid:
                    icon.Glyph = "\uE783"; // Error
                    icon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                    text.Text = "Invalid API key";
                    text.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                    panel.Visibility = Visibility.Visible;
                    break;
                default: // ApiKeyStatus.Unknown
                    // ENH-1: Unknown now also covers "key saved but the validation fetch
                    // failed for a non-auth reason" (geo-block, outage). Showing "No key
                    // set" beside a masked key would contradict the row \u2014 distinguish by
                    // key presence.
                    icon.Glyph = "\uE946"; // Info (Segoe MDL2 Assets)
                    icon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
                    text.Text = getHasKey() ? "Key saved \u2014 not verified" : "No key set";
                    text.Foreground = AppTheme.Brush(AppTheme.SubtleText);
                    panel.Visibility = Visibility.Visible;
                    break;
            }
        }

        Apply();
        PropertyChangedEventHandler statusChangedHandler = (_, e) =>
        {
            if (e.PropertyName == propertyName || e.PropertyName == hasKeyPropertyName)
                DispatcherQueue.TryEnqueue(Apply);
        };
        RegisterViewModelSubscription(statusChangedHandler);

        return panel;
    }

}
