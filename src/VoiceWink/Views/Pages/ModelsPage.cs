using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Enums;
using VoiceWink.Services.System;
using VoiceWink.Services.Transcription;
using VoiceWink.Services.Transcription.Clients;
using VoiceWink.ViewModels;
using VoiceWink.Views.Dialogs;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Model management page.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class ModelsPage : Page
{
    private static ILogger Logger => Log.ForContext<ModelsPage>();

    private readonly ModelManagementViewModel _viewModel;
    private readonly ApiKeyManager _apiKeyManager;
    private readonly PropertyChangedEventHandler _vmPropertyChanged;
    private readonly List<(ModelItemViewModel model, PropertyChangedEventHandler handler)> _modelHandlers = new();
    private StackPanel? _modelList;
    private StackPanel? _cloudProviderList;
    private ComboBox? _languageCombo;
    private TextBlock? _gpuAdvisory;
    private TextBlock? _gpuStatus;
    private readonly Action _onGpuSelfTestChanged;
    private TextBlock? _activeModelName;
    private TextBlock? _activeModelSize;
    private TextBlock? _languageNote;

    public ModelsPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _viewModel = App.Services.GetRequiredService<ModelManagementViewModel>();
        _apiKeyManager = App.Services.GetRequiredService<ApiKeyManager>();
        BuildUI();
        // UI-12: the GPU row's status line follows the self-test as it runs — raised on the
        // warm-up's thread, so hop to the dispatcher and honour the unload fence like every
        // other queued rebuild here.
        _onGpuSelfTestChanged = () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isUnloaded) return;
            RefreshGpuAdvisory();
        });
        _vmPropertyChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(ModelManagementViewModel.SelectedModelName)
                or nameof(ModelManagementViewModel.SelectedLanguage))
                RefreshModelList();
        };
        // No constructor rebuild: the fence below starts CLOSED and Loaded performs the
        // initial rebuild (Codex R2 — a page that never loads, e.g. MainWindow's handled
        // add/remove COM failure, must never run its queued rebuild and pin handlers onto
        // the singleton VM's items with no unload ever coming).
        Loaded += (_, _) =>
        {
            _isUnloaded = false;
            // Detach-before-attach: Loaded can refire without a paired Unloaded — a
            // second subscription would double-fire every VM change (Codex R1).
            _viewModel.PropertyChanged -= _vmPropertyChanged;
            _viewModel.PropertyChanged += _vmPropertyChanged;
            GpuWarmup.Instance.SelfTestChanged -= _onGpuSelfTestChanged;
            GpuWarmup.Instance.SelfTestChanged += _onGpuSelfTestChanged;
            // Re-read disk BEFORE building the cards. The VM is a singleton whose LoadModels() ran
            // once at construction, so without this a model deleted from disk stayed "downloaded"
            // for the rest of the process (UAT 15.16, 2026-07-25). In-place + fail-soft.
            _viewModel.RefreshDownloadedState();
            RefreshModelList();
        };
        this.Unloaded += OnUnloaded;
    }

    // Unload fence (F13, Codex R1): RefreshModelList QUEUES its rebuild, so a callback
    // enqueued just before navigation could otherwise run AFTER OnUnloaded's drain and
    // re-subscribe card handlers onto the singleton VM's items with no later unload to
    // detach them — recreating the leak the drain fixes. Starts CLOSED (Codex R2): only
    // a page that actually LOADS may rebuild, so a construction that never enters the
    // tree can't subscribe anything that no unload will ever detach.
    private bool _isUnloaded = true;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        GpuWarmup.Instance.SelfTestChanged -= _onGpuSelfTestChanged;
        // Per-model card handlers subscribe onto the SINGLETON ModelManagementViewModel's
        // ModelItemViewModels, and MainWindow creates a fresh page per navigation — without
        // this drain every past visit's closures (capturing this page's controls) stayed
        // subscribed forever: a leak per visit, hottest during downloads (F13).
        DetachModelHandlers();
    }

    // The ONE unsubscribe loop for per-model card handlers — shared by OnUnloaded and
    // RefreshModelList's rebuild (which drains before re-subscribing fresh card closures).
    private void DetachModelHandlers()
    {
        foreach (var (model, handler) in _modelHandlers)
        {
            model.PropertyChanged -= handler;
        }
        _modelHandlers.Clear();
    }

    private void BuildUI()
    {
        // ── Page header ───────────────────────────────────────────────
        var header = AppTheme.CreatePageHeader("Models", "Select a local or cloud model for transcription.");

        // ── Default model info card ───────────────────────────────────
        var activeModelSection = AppTheme.CreateSectionHeader("Active Model");
        activeModelSection.Margin = new Thickness(0, 0, 0, 12); // reduce top gap after page header
        var defaultModelCard = CreateDefaultModelCard();

        // ── Local Models section ──────────────────────────────────────
        var localSection = AppTheme.CreateSectionHeader("Local Models");
        // UI-11: the GPU acceleration row leads the section, above the rows whose speed stars it
        // governs — those are rendered per engine from LocalComputeSnapshot, so this is the one
        // page where the toggle's effect is visible.
        var gpuCard = BuildGpuAccelerationCard();
        _modelList = new StackPanel { Spacing = 12 };

        // ── Language selector ─────────────────────────────────────────
        var languageSection = AppTheme.CreateSectionHeader("Language");
        var languageCombo = BuildLanguageCombo();

        // ── Cloud Providers section ─────────────────────────────────
        var cloudSection = AppTheme.CreateSectionHeader("Cloud Providers");
        _cloudProviderList = new StackPanel { Spacing = 16 };

        var root = new StackPanel
        {
            Children =
            {
                header,
                activeModelSection,
                defaultModelCard,
                languageSection,
                languageCombo,
                localSection,
                gpuCard,
                _modelList,
                cloudSection,
                _cloudProviderList
            }
        };

        AppTheme.SetPageScrollContent(this, root);
    }

    /// <summary>
    /// UI-11: the GPU acceleration row, moved here from Settings → General (which is otherwise
    /// window/startup behaviour). It governs both local engines, and the speed stars on this page
    /// are the one place its effect shows.
    ///
    /// <para>Everything below is carried over unchanged from the Settings card, including the
    /// reasons:</para>
    ///
    /// <para>TRN-53/UI-12: the copy used to state the restart requirement; TRN-59's modal now
    /// says it at the moment of the flip and offers the restart, so the standing sentence was
    /// removed (owner, 2026-09-04). The fact it stated is unchanged — the native load order is
    /// process-wide and frozen at the first decode, so a flip cannot apply mid-session by
    /// construction. That is also why the stars on this page do not move until the next launch:
    /// they follow the RESOLVED backend (TRN-52), never the stored preference.</para>
    ///
    /// <para>TRN-61: the row's PRESENTATION comes from the pre-boot Vulkan probe verdict — disabled
    /// and shown OFF only when the toggle is inert for BOTH engines (no Vulkan loader on the
    /// system); a Whisper-only decline keeps the row live and adds the amber advisory below. The
    /// displayed state goes INTO the factory: the toggle factory wires Toggled → onChanged at
    /// construction, so setting IsOn AFTERWARDS would fire the handler and WRITE the persisted
    /// value — the trap the card names. A probe verdict is about THIS machine at THIS boot; the
    /// stored preference stays the user's.</para>
    ///
    /// <para>TRN-50: a failed GPU self-test adds its own amber sentence (which adapter, which
    /// engine is on the CPU, and that off/on re-tests). Read from the LIVE marker through the
    /// warm-up instance — never a boot snapshot — so a re-arm shows on the next page build.</para>
    ///
    /// <para>UI-12: that sentence is scoped to the SELECTED model's engine, so a Parakeet failure
    /// is not shown to a user running Whisper. The selection changes without leaving the page, so
    /// the advisory is a FIELD refreshed by <see cref="RefreshGpuAdvisory"/> rather than a child
    /// added at build time — it is created once, always, and shown or collapsed. The toggle row
    /// itself is deliberately NOT re-rendered: its state comes from the pre-boot probe and the
    /// persisted value, and assigning <c>IsOn</c> after construction would fire <c>Toggled</c> and
    /// write the setting (the trap named above).</para>
    /// </summary>
    private Border BuildGpuAccelerationCard()
    {
        var presentation = CurrentGpuPresentation();

        var row = AppTheme.CreateToggleSetting(
            "GPU acceleration",
            presentation.Description,
            presentation.IsOn,
            OnGpuAccelerationToggled,
            out var toggle);
        toggle.IsEnabled = presentation.Enabled;
        AppTheme.StripCardBorder(row);

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(row);
        // TRN-61: amber, in the hotkey-warning style — an actual issue on this machine, never a
        // standing explanation of how the control works. Collapsed when there is nothing to say.
        _gpuAdvisory = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        content.Children.Add(_gpuAdvisory);
        // UI-12: the neutral status line ("Checking…" / "will be checked when VoiceWink restarts")
        // — secondary text, never amber: it is progress, not a problem.
        _gpuStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        content.Children.Add(_gpuStatus);
        ApplyGpuLines(presentation);

        return AppTheme.CreateCard(content);
    }

    /// <summary>The row's presentation right now — the pre-boot probe verdict, the persisted
    /// preference, the LIVE self-test marker, and (UI-12) the engine behind the selected model.
    /// <c>ModelRatings.RuntimeOf</c> is the same catalog-derived answer the speed stars use, so
    /// the advisory and the stars can never disagree about which engine a row belongs to; a cloud
    /// or unrecognised selection yields null, which keeps the union.</summary>
    private GpuTogglePresentation CurrentGpuPresentation()
    {
        var runtime = ModelRatings.RuntimeOf(_viewModel.SelectedModelName);
        // ONE snapshot per render (Codex diff r1 Blocker): the provider reads live child state on
        // every call, so two reads could pair a true "on the GPU" flag with a name taken after the
        // child changed — and the tick would then fall back to a persisted name and claim it live.
        var compute = LocalComputeSnapshot.Current;
        return GpuToggleAvailability.Decide(
            GpuToggleAvailability.Current,
            _viewModel.GpuAccelerationEnabled,
            // TRN-64 PR 2: Whisper verdicts are per MODEL, so the summary carries the selected
            // model's own — the positive tick must never speak for a model nobody judged.
            GpuWarmup.Instance.ReadSelfTestSummary(runtime == LocalRuntimeKind.Whisper ? _viewModel.SelectedModelName : null) ?? default,
            runtime,
            // UI-12: "will be checked when VoiceWink restarts" is promised only for a model that
            // is on disk — a fresh install with nothing downloaded never preloads, and a missing
            // GGUF never warms (Codex diff r2). The row's flag is the same one the Download /
            // Select buttons render from.
            selectedModelInstalled: _viewModel.Models.FirstOrDefault(m => m.IsSelected)?.IsDownloaded ?? false,
            // TRN-64 PR 2 (self-review): "is running on your graphics card" is a claim about THIS
            // process, so it needs the engine resolved on the GPU here — the same snapshot the
            // speed stars render from, so the tick and the stars can never disagree.
            selectedEngineOnGpu: runtime is { } engine && compute.For(engine) == LocalCompute.Gpu,
            // UI-14: the adapter that engine is on in THIS process, from the SAME snapshot, so the
            // tick names what the stars rate — never a persisted name the live child may not match.
            liveGpuName: runtime is { } liveEngine ? compute.GpuNameFor(liveEngine) : null);
    }

    /// <summary>UI-12: re-evaluate the two lines under the switch for the current selection and
    /// the live marker. Called from <see cref="RefreshModelList"/> (every SelectedModelName
    /// change), from the toggle's own change handler (so a flip OFF drops a stale warning at
    /// once instead of at the next navigation), and from <see cref="GpuWarmup.SelfTestChanged"/>
    /// (so "Checking…" becomes the verdict without leaving the page).</summary>
    private void RefreshGpuAdvisory() => ApplyGpuLines(CurrentGpuPresentation());

    private void ApplyGpuLines(GpuTogglePresentation presentation)
    {
        if (_gpuAdvisory is not null)
        {
            _gpuAdvisory.Text = presentation.Advisory ?? string.Empty;
            _gpuAdvisory.Visibility = presentation.Advisory is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_gpuStatus is not null)
        {
            _gpuStatus.Text = presentation.Status ?? string.Empty;
            _gpuStatus.Visibility = presentation.Status is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// TRN-59: the switch's own change event — the ONLY trigger of the restart offer, so a
    /// disabled row (which never raises it) never offers a restart, and the reset-all path
    /// (which writes through the preference, not the switch) never prompts. The WRITE goes first
    /// and is unchanged: <c>GpuAccelerationPreference.Write</c> — persist, log, re-arm the
    /// self-test — through the ViewModel. The offer follows only when that write CHANGED the
    /// stored value; a redundant write is a no-op there and leaves nothing to apply.
    /// </summary>
    private void OnGpuAccelerationToggled(bool isOn)
    {
        var changed = _viewModel.GpuAccelerationEnabled != isOn;
        _viewModel.GpuAccelerationEnabled = isOn;
        // UI-12: the lines under the switch follow the flip at once — OFF hides a self-test
        // warning (the user chose the CPU), ON after a re-arm shows what the cleared marker owes.
        RefreshGpuAdvisory();
        if (!changed) return;

        var restart = _viewModel.AppRestart;
        if (restart is null) return;
        _ = OfferRestartAsync(restart, isOn);
    }

    /// <summary>
    /// Opens the "Restart now / Later" dialog. The setting is already written when this runs;
    /// the dialog only decides WHEN it applies. Fail-soft: a second ContentDialog already open
    /// throws, and that must not surface. Since UI-12 the row carries no standing restart sentence,
    /// so this path is silent about it — an accepted cost recorded on that card.
    /// </summary>
    private async Task OfferRestartAsync(AppRestartService restart, bool isOn)
    {
        try
        {
            var dialog = new RestartToApplyDialog(restart, isOn) { XamlRoot = this.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not offer the restart after the GPU acceleration flip; the change applies at the next start");
        }
    }

    /// <summary>
    /// Builds the language selection card with human-readable display names,
    /// sorted alphabetically with "Auto-detect" pinned first.
    /// Each ComboBoxItem stores the ISO code as its Tag for lossless round-tripping.
    /// </summary>
    private Border BuildLanguageCombo()
    {
        _languageCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var combo = _languageCombo;
        AppTheme.AllowParentScroll(combo);

        // "Auto-detect" always first
        var autoItem = new ComboBoxItem
        {
            Content = "Auto-detect",
            Tag = "auto"
        };
        combo.Items.Add(autoItem);

        // All other languages sorted alphabetically by display name
        var sorted = ModelManagementViewModel.SupportedLanguages
            .Where(c => c != "auto")
            .OrderBy(c => ModelManagementViewModel.GetLanguageDisplayName(c),
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var isoCode in sorted)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = ModelManagementViewModel.GetLanguageDisplayName(isoCode),
                Tag = isoCode
            });
        }

        // Select the current value by matching Tag
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is string tag && tag == _viewModel.SelectedLanguage)
            {
                combo.SelectedItem = item;
                break;
            }
        }

        combo.SelectionChanged += (s, e) =>
        {
            if (combo.SelectedItem is ComboBoxItem selected && selected.Tag is string isoCode)
                _viewModel.SelectedLanguage = isoCode;
        };

        var titleBlock = new TextBlock
        {
            Text = "Transcription Language",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(titleBlock);
        grid.Children.Add(combo);

        // The selected model may not be able to honour the selected language — Parakeet
        // auto-detects and covers 25 European languages, so a pinned Chinese silently becomes
        // auto-detect against an engine with no Chinese. Before change D that fact reached the user
        // only as a log line.
        //
        // A sibling TextBlock, deliberately NOT per-item annotation of the combo: rebuilding a live
        // ComboBox's items is the operation behind this repo's fatal COMException 0x80070490
        // (ENH-7) and the 0x800F1000 crash (2026-08-03), and a UIElement in a ComboBoxItem's
        // Content is forbidden outright. Updated in place by RefreshModelList, same as the active
        // model's subtitle.
        _languageNote = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(_languageNote);
        UpdateLanguageNote();

        return new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }

    /// <summary>
    /// Show or hide the "this model cannot serve that language" note.
    ///
    /// <para>Every word comes from <see cref="ModelLanguageSupport.ComposeLanguageNote"/> — a pure
    /// function, so the wording is unit-testable without a page, and so the coverage phrase stays a
    /// single sourced string rather than a second hand-maintained copy of one already rendered a few
    /// rows above (the drift TRN-3 and TRN-8 spent their review rounds eliminating).</para>
    /// </summary>
    private void UpdateLanguageNote()
    {
        if (_languageNote is null) return;

        // IsSameModel, not bare string equality: a persisted Parakeet selection may carry the
        // pre-flip spelling, and losing this lookup silently suppresses the language-constraint
        // note for exactly the users who most need it (self-review, config lens).
        var model = PredefinedModels.Models.FirstOrDefault(
            m => ModelDiskReconciliation.IsSameModel(m.Name, _viewModel.SelectedModelName));
        var note = ModelLanguageSupport.ComposeLanguageNote(model, _viewModel.SelectedLanguage);

        _languageNote.Text = note ?? "";
        _languageNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Creates a highlighted card showing the currently active model at the top of the page.
    /// </summary>
    private Border CreateDefaultModelCard()
    {
        var activeModel = _viewModel.Models.FirstOrDefault(m => m.IsSelected);
        var modelName = activeModel?.DisplayName ?? "No model selected";
        var modelSize = activeModel?.FileSizeDisplay ?? "";

        var iconBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = AppTheme.Brush(AppTheme.ActivePillBg),
            Child = new FontIcon
            {
                Glyph = "\uE8D6", // Microphone icon
                FontSize = 18,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        _activeModelName = new TextBlock
        {
            Text = modelName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        _activeModelSize = new TextBlock
        {
            Text = modelSize,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            // TRN-7: the subtitle gained the star pair, so it is now long enough to reach the
            // Active badge — size, then TWO five-glyph star pairs, then a language bucket.
            // Deliberately no example row here: this comment carried one, and it went stale twice
            // in a single day as the speed stars moved. Star VALUES cannot change the width (a
            // pair is always five glyphs either way), so the worst case is only ever the longest
            // size and language strings, and naming a row added nothing but something to rot.
            // Wrapping keeps it readable instead of clipping the language bucket off the end.
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var textPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Children = { _activeModelName, _activeModelSize }
        };

        // Active badge
        var activeBadge = new Border
        {
            Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)), // AccentGreen at 20%
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Active",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen)
            }
        };

        // TRN-7: a two-column Grid, NOT the horizontal StackPanel this used to be. A horizontal
        // StackPanel measures its children with INFINITE available width, so the subtitle's
        // TextWrapping had no bound to wrap against — the text ran to its natural width and
        // clipped under the Active badge, i.e. the wrap was decorative and the fix ineffective
        // (Codex diff review). Auto for the icon, star for the text, so the text column gets a
        // real bounded width; textPanel is a VERTICAL StackPanel, which passes that bound down
        // to the TextBlock. Its 14 px left margin still supplies the gap after the icon.
        var leftPanel = new Grid();
        leftPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(textPanel, 1);
        leftPanel.Children.Add(iconBorder);
        leftPanel.Children.Add(textPanel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(activeBadge, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(activeBadge);

        return AppTheme.CreateCard(grid);
    }

    private void RefreshModelList()
    {
        if (_modelList == null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            // A rebuild queued before navigation must not run after OnUnloaded's drain —
            // it would re-subscribe onto the singleton VM with no future unload (F13).
            if (_isUnloaded) return;
            // Model or language changed — either can make the note appear, change or go away.
            UpdateLanguageNote();
            // UI-12: and the model change can make the GPU self-test warning appear or go away,
            // because it is scoped to the selected model's engine.
            RefreshGpuAdvisory();

            // Unsubscribe stale PropertyChanged handlers from previous model cards
            DetachModelHandlers();

            _modelList.Children.Clear();

            // TRN-52: ONE compute snapshot per refresh, handed to every row AND the Active Model
            // card below. Reading LocalComputeSnapshot.Current per call would let a Parakeet
            // Auto→Cpu latch (or a Whisper fall-back from its pinned order) land between two reads
            // and leave the selected row and its card showing different speed stars until the next
            // navigation (Codex diff r1 Blocker). The snapshot is still re-read on every refresh.
            var compute = LocalComputeSnapshot.Current;

            foreach (var model in _viewModel.Models)
            {
                _modelList.Children.Add(CreateModelCard(model, compute));
            }

            // Update the active model status card — check local models first, then cloud
            var activeModel = _viewModel.Models.FirstOrDefault(m => m.IsSelected);
            if (activeModel != null)
            {
                if (_activeModelName != null)
                    _activeModelName.Text = activeModel.DisplayName;
                if (_activeModelSize != null)
                    _activeModelSize.Text = ComposeActiveModelSubtitle(
                        activeModel.FileSizeDisplay,
                        GetRatingStarsLabel(activeModel.Name, compute),
                        GetLanguageSupportLabel(activeModel.Name));
            }
            else
            {
                // No local model is selected — check if a cloud model is active.
                // Canonicalize (PRM-3): an oddly-cased persisted name still matches
                // its catalog entry.
                var selectedName = CloudModels.Canonicalize(_viewModel.SelectedModelName);
                var cloudModel = CloudModels.Models.FirstOrDefault(m => m.Name == selectedName);
                if (cloudModel != null)
                {
                    if (_activeModelName != null)
                        _activeModelName.Text = cloudModel.DisplayName;
                    if (_activeModelSize != null)
                        _activeModelSize.Text = ComposeActiveModelSubtitle(
                            cloudModel.Provider.ToString(),
                            GetRatingStarsLabel(cloudModel.Name, compute),
                            GetLanguageSupportLabel(cloudModel.Name));
                }
                else
                {
                    if (_activeModelName != null)
                        _activeModelName.Text = "No model selected";
                    if (_activeModelSize != null)
                        _activeModelSize.Text = "";
                }
            }

            // Sync language combo with ViewModel (may have changed on model select)
            if (_languageCombo != null)
            {
                foreach (ComboBoxItem item in _languageCombo.Items)
                {
                    if (item.Tag is string tag && tag == _viewModel.SelectedLanguage)
                    {
                        _languageCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            // Refresh cloud provider cards
            RefreshCloudProviders();
        });
    }

    private UIElement CreateModelCard(ModelItemViewModel model, LocalComputeSnapshot compute)
    {
        // ── Left side: icon + model info ──────────────────────────────
        var iconBg = model.IsSelected
            ? AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88))   // green 20%
            : model.IsDownloaded
                ? AppTheme.Brush(AppTheme.ActivePillBg)                // blue 20%
                : AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)); // subtle

        var iconGlyph = model.IsDownloaded ? "\uE73E" : "\uE896"; // Checkmark or Download
        var iconColor = model.IsSelected
            ? AppTheme.AccentGreen
            : model.IsDownloaded
                ? AppTheme.AccentBlue
                : AppTheme.SubtleText;

        var iconBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = iconBg,
            Child = new FontIcon
            {
                Glyph = iconGlyph,
                FontSize = 16,
                Foreground = AppTheme.Brush(iconColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var nameBlock = new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        var sizeBlock = new TextBlock
        {
            Text = model.FileSizeDisplay,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { nameBlock, sizeBlock }
        };

        // Description based on model type
        var description = GetModelDescription(model.Name, compute);
        var descBlock = new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var textPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { nameRow, descBlock }
        };

        var leftPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconBorder, textPanel }
        };

        // ── Right side: actions ───────────────────────────────────────
        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (model.IsSelected)
        {
            // Green "Active" badge
            var activeBadge = new Border
            {
                Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)), // AccentGreen 20%
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Active",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            rightPanel.Children.Add(activeBadge);
        }

        if (model.IsDownloaded)
        {
            if (!model.IsSelected)
            {
                var selectBtn = AppTheme.CreateCompactButton("Select");
                selectBtn.MinWidth = 80;
                selectBtn.Tapped += async (_, _) =>
                {
                    // The English-only confirmation dialog that used to sit here is gone with the
                    // rows it guarded (2026-08-03): no catalogue model is English-only, so it could
                    // only ever protect an unreachable state. `EnglishOnlyModelNaming.IsEnglishOnly`
                    // survives for the two places where getting it wrong is silent — recognition
                    // language and tokenizer vocabulary — but there is nothing left to warn about
                    // at selection time.
                    AppTheme.SetButtonEnabled(selectBtn, false, "Loading...");
                    await _viewModel.SelectModelCommand.ExecuteAsync(model);
                    RefreshModelList();
                };
                rightPanel.Children.Add(selectBtn);

                var deleteBtn = AppTheme.CreateCompactButton("Delete", isDanger: true);
                deleteBtn.Tapped += async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Delete Model",
                        Content = $"Are you sure you want to delete \"{model.DisplayName}\"? You will need to re-download it to use it again.",
                        PrimaryButtonText = "Delete",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = AppTheme.ElementTheme
                    };

                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        // TRN-37 (Kimi plan round B1 + self-review lens 2, Blocker): the FULL
                        // sibling pattern — disable BEFORE awaiting, then await the async
                        // command. The disable is the re-entrancy guard, and selectBtn is the
                        // dangerous one: with the UI live during the pool-side delete, a Select
                        // on this row would persist a selection to the model being deleted
                        // (settings naming a missing model + a silent re-download at the next
                        // recording start). AsyncRelayCommand does NOT gate ExecuteAsync — its
                        // no-concurrent-executions default only feeds CanExecute, which a
                        // Tapped+= wiring never consults (measured against toolkit 8.4.0).
                        // `.Execute` instead of the await would separately let RefreshModelList
                        // rebuild the card before IsDownloaded flips (the UAT 15.16 dead-button
                        // class) and fault an unobserved task. (The toolkit strips the Async
                        // suffix, so the generated property is still DeleteModelCommand.)
                        AppTheme.SetButtonEnabled(selectBtn, false);
                        AppTheme.SetButtonEnabled(deleteBtn, false, "Deleting...");
                        await _viewModel.DeleteModelCommand.ExecuteAsync(model);
                        RefreshModelList();
                    }
                };
                rightPanel.Children.Add(deleteBtn);
            }
        }
        else if (model.IsDownloading)
        {
            rightPanel.Children.Add(CreateDownloadProgressPanel(model));
        }
        else
        {
            var downloadBtn = AppTheme.CreateCompactButton("Download", isAccent: true);
            downloadBtn.Tapped += async (_, _) =>
            {
                AppTheme.SetButtonEnabled(downloadBtn, false);
                rightPanel.Children.Remove(downloadBtn);
                rightPanel.Children.Add(CreateDownloadProgressPanel(model));

                await _viewModel.DownloadModelCommand.ExecuteAsync(model);
                RefreshModelList();
            };
            rightPanel.Children.Add(downloadBtn);
        }

        // TRN-29: the legacy sherpa bundle is cleaned up AUTOMATICALLY by the coordinator after
        // the new engine's first successful transcription (owner, 2026-08-24) — the "Free 670 MB"
        // offer + confirm dialog that briefly lived here never shipped in a release and were
        // removed the same day the flip merged.

        // ── Assemble card grid ────────────────────────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);

        // Error message (hidden when empty)
        var errorText = new TextBlock
        {
            Text = model.ErrorMessage,
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.AccentRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(model.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible,
            Margin = new Thickness(0, 8, 0, 0)
        };
        PropertyChangedEventHandler errorHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(ModelItemViewModel.ErrorMessage))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    errorText.Text = model.ErrorMessage;
                    errorText.Visibility = string.IsNullOrEmpty(model.ErrorMessage)
                        ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
        model.PropertyChanged += errorHandler;
        _modelHandlers.Add((model, errorHandler));

        var cardStack = new StackPanel();
        cardStack.Children.Add(grid);
        cardStack.Children.Add(errorText);

        // Highlighted border for active model
        var cardBorder = new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = model.IsSelected
                ? AppTheme.Brush(ColorHelper.FromArgb(80, 48, 209, 88))  // subtle green border for active
                : AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
            Padding = new Thickness(AppTheme.CardPadding, 16, AppTheme.CardPadding, 16),
            Child = cardStack
        };

        return cardBorder;
    }

    private StackPanel CreateDownloadProgressPanel(ModelItemViewModel model)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10
        };

        var progressBar = new ProgressBar
        {
            Width = 100,
            Height = 6,
            Value = model.DownloadProgress * 100,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            Background = AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };

        var progressText = new TextBlock
        {
            Text = $"{(model.DownloadProgress * 100):F0}%",
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 32
        };

        var cancelBtn = AppTheme.CreateCompactButton("Cancel", isDanger: true);
        cancelBtn.Tapped += (_, _) => model.DownloadCts?.Cancel();

        PropertyChangedEventHandler progressHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(ModelItemViewModel.DownloadProgress))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    progressBar.Value = model.DownloadProgress * 100;
                    progressText.Text = $"{(model.DownloadProgress * 100):F0}%";
                });
            }
        };
        model.PropertyChanged += progressHandler;
        _modelHandlers.Add((model, progressHandler));

        panel.Children.Add(progressBar);
        panel.Children.Add(progressText);
        panel.Children.Add(cancelBtn);

        return panel;
    }

    // ── Cloud provider UI ─────────────────────────────────────────

    /// <summary>
    /// Rebuilds the cloud providers section with provider groups and model cards.
    /// </summary>
    private void RefreshCloudProviders()
    {
        if (_cloudProviderList == null) return;

        _cloudProviderList.Children.Clear();

        // Group cloud models by provider, preserving enum order
        var providers = CloudModels.Models
            .GroupBy(m => m.Provider)
            .OrderBy(g => g.Key);

        foreach (var group in providers)
        {
            _cloudProviderList.Children.Add(CreateCloudProviderGroup(group.Key, group.ToArray()));
        }
    }

    /// <summary>
    /// Creates a provider group card containing an API key row and model cards.
    /// </summary>
    private UIElement CreateCloudProviderGroup(ModelProvider provider, TranscriptionModelInfo[] models)
    {
        var providerKey = provider.ToString().ToLowerInvariant();
        var hasKey = _apiKeyManager.HasApiKey(providerKey);

        var groupPanel = new StackPanel { Spacing = 8 };

        // ── Provider header with API key management ─────────────
        var providerName = new TextBlock
        {
            Text = provider.ToString(),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { providerName }
        };

        var headerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Link to the provider's own console, on the RIGHT with the other key affordances (owner
        // 2026-07-31) — added FIRST so it stays at the group's left edge in BOTH states: tapping
        // "Edit" removes the saved panel and APPENDS the key entry, so a link added last would
        // jump sides mid-interaction. Rendered only when a URL is known, so a provider without one
        // can never show a dead link; wording follows whether a key is stored. An inline hyperlink
        // rather than a HyperlinkButton keeps it on the baseline of the 12 px text beside it —
        // see AppTheme.CreateInlineLink.
        var consoleUrl = ProviderConsoleUrls.ForProvider(provider);
        if (consoleUrl != null)
        {
            var (consoleLink, applyConsoleLink) = AppTheme.CreateInlineLink(12);
            applyConsoleLink(consoleUrl, hasKey ? "Manage API key" : "Get API key");
            headerRight.Children.Add(consoleLink);
        }

        if (hasKey)
        {
            // Show "Key saved" indicator with Edit link
            var savedIcon = new FontIcon
            {
                Glyph = "\uE73E", // Checkmark
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                VerticalAlignment = VerticalAlignment.Center
            };

            var savedText = new TextBlock
            {
                Text = "Key saved",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var editLink = new TextBlock
            {
                Text = "Edit",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            // When "Edit" is tapped, replace the saved indicator with a PasswordBox
            var savedPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { savedIcon, savedText, editLink }
            };

            editLink.Tapped += (_, _) =>
            {
                headerRight.Children.Remove(savedPanel);
                AddApiKeyEntry(headerRight, providerKey, isEditing: true);
            };

            headerRight.Children.Add(savedPanel);
        }
        else
        {
            // Show inline API key entry
            AddApiKeyEntry(headerRight, providerKey);
        }

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerRight, 1);
        headerGrid.Children.Add(headerLeft);
        headerGrid.Children.Add(headerRight);

        groupPanel.Children.Add(headerGrid);

        // ── Model cards within this provider ────────────────────
        foreach (var model in models)
        {
            groupPanel.Children.Add(CreateCloudModelCard(model, providerKey));
        }

        // ── PRM-3: "Send dictionary and trigger words" toggles (owner rework
        // 2026-07-18: Deepgram gets a toggle too, the small privacy risk is disclosed
        // explicitly, and BOTH toggles default ON; renamed from "Enable Dictionary"
        // 2026-07-23 — the row gates the whole keyterm payload incl. trigger words,
        // not just Dictionary data) ──────────
        const string dictionaryToggleLead = "Improves recognition of dictionary and trigger words. ";
        if (provider == ModelProvider.Deepgram)
        {
            groupPanel.Children.Add(CreateDictionaryToggleRow(
                AppDefaults.DeepgramKeytermsEnabled,
                dictionaryToggleLead + "Words are sent in the request URL and may be logged."));
        }
        else if (provider == ModelProvider.ElevenLabs)
        {
            groupPanel.Children.Add(CreateDictionaryToggleRow(
                AppDefaults.ElevenLabsKeytermsEnabled,
                dictionaryToggleLead + "Up to 100 terms; adds 20% to the transcription cost."));
        }
        else if (provider == ModelProvider.OpenAI)
        {
            // Scoped to TRANSCRIPTION deliberately. An earlier draft said the words "stay on this
            // device" while the toggle is off, which is not something this row can promise — AI
            // enhancement can carry vocabulary to a provider by a completely separate path. A
            // privacy claim that is true of one subsystem and false of the app is worse than none.
            groupPanel.Children.Add(CreateDictionaryToggleRow(
                AppDefaults.OpenAIKeywordsEnabled,
                // "sent with GPT Transcribe only" is implementation truth. An earlier draft said the
                // other OpenAI models "don't accept them" — a claim about OpenAI's API we cannot
                // actually support: keywords are merely UNDOCUMENTED for those models, which is why
                // we don't send them, not a demonstrated rejection.
                dictionaryToggleLead + "Sent with GPT Transcribe only."));
        }

        return new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
            Padding = new Thickness(AppTheme.CardPadding, 16, AppTheme.CardPadding, 16),
            Child = groupPanel
        };
    }

    /// <summary>
    /// PRM-3: the shared "Send dictionary and trigger words" row (owner rework
    /// 2026-07-18 — one label per provider, provider-specific disclosure beside
    /// it, toggle right-aligned; exported + imported like any preference).
    /// The per-request read in TranscriptionServiceRegistry means a flip takes effect immediately.
    ///
    /// <para>The switch reads <see cref="SettingsService.GetBoolDefaulted"/>, NOT a literal fallback
    /// — the SAME call the request path makes. It was a hardcoded <c>true</c>, harmless while every
    /// provider defaulted ON and a lie the moment one did not. The subtler half is that even a
    /// correct literal is not enough: <c>Get</c> only consults the defaults table when a key is
    /// ABSENT, so a malformed stored value returns whatever literal each caller passed, and a UI
    /// literal that merely happens to match the request literal today is one edit from disagreeing.</para>
    /// </summary>
    private UIElement CreateDictionaryToggleRow(string settingKey, string disclosureText)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();

        // The master Dictionary toggle (Dictionary page) is a CONTENT gate; this row
        // stays a TRANSPORT gate and remains operable while the master is off —
        // prompt trigger words still ride the keyterm payload (URL exposure /
        // surcharge), so the user must keep the opt-out. The note makes the
        // interaction visible; build-time read is fine (pages are constructed fresh
        // per navigation).
        if (!settings.GetBool(AppDefaults.DictionaryEnabled, true))
        {
            disclosureText += " Dictionary is off — only trigger words will be sent.";
        }

        var label = new TextBlock
        {
            Text = "Send dictionary and trigger words",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };
        var disclosure = new TextBlock
        {
            Text = disclosureText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        var textPanel = new StackPanel { Spacing = 2, Children = { label, disclosure } };

        var toggle = new ToggleSwitch
        {
            IsOn = settings.GetBoolDefaulted(settingKey),
            OnContent = null,
            OffContent = null,
            // The default template reserves ~154px for On/Off content; with none, the
            // knob floats mid-card instead of at the card edge (owner screenshot
            // 2026-07-18). MinWidth 0 + right alignment pin it to the edge.
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) =>
            settings.SetBool(settingKey, toggle.IsOn);

        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(textPanel);
        row.Children.Add(toggle);
        return row;
    }

    /// <summary>
    /// Adds an inline PasswordBox + Save button for API key entry to the given panel.
    /// </summary>
    private void AddApiKeyEntry(StackPanel container, string providerKey, bool isEditing = false)
    {
        var existingKey = _apiKeyManager.GetApiKey(providerKey);
        var passwordBox = new PasswordBox
        {
            PlaceholderText = "Enter API key",
            Password = existingKey ?? "",
            Width = 250,
            VerticalAlignment = VerticalAlignment.Center
        };
        AppTheme.AllowParentScroll(passwordBox);

        var statusIcon = new FontIcon
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            Children = { statusIcon, statusLabel }
        };

        var saveBtn = AppTheme.CreateCompactButton("Save", isAccent: true);
        saveBtn.Tapped += async (_, _) =>
        {
            try
            {
                var key = passwordBox.Password;

                // ENH-17: claim the slot's write generation synchronously, before any await.
                // apikey_{provider} is ONE flat slot shared with the Enhancement page, so without a
                // claim this surface's save is invisible to an in-flight enhancement save's commit
                // check — last CLICK could lose to last COMPLETER. SetApiKey also advances the
                // generation, so an unclaimed write can never be overtaken; claiming additionally
                // orders two saves made from THIS page.
                var generation = _apiKeyManager.BeginKeyWrite(providerKey);

                // Empty key = clear the saved key
                if (string.IsNullOrWhiteSpace(key))
                {
                    if (!_apiKeyManager.TryCommitApiKey(providerKey, "", generation, out _))
                    {
                        // Nothing was written, so do not go on to log "cleared" or refresh against
                        // pre-newer-write state — the newer claim owns the slot (Kimi ENH-17 review).
                        Logger.Information("API key clear for {Provider} superseded by a newer write", providerKey);
                        return;
                    }
                    Logger.Information("API key cleared for provider: {Provider}", providerKey);
                    RefreshCloudProviders();
                    return;
                }

                // Capture existing key so we can restore on validation failure
                var previousKey = _apiKeyManager.GetApiKey(providerKey) ?? "";

                statusIcon.Glyph = "\uE946";
                statusIcon.Foreground = AppTheme.Brush(AppTheme.TextSecondary);
                statusLabel.Text = "Validating...";
                statusLabel.Foreground = AppTheme.Brush(AppTheme.TextSecondary);
                statusPanel.Visibility = Visibility.Visible;

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

                    if (client != null)
                        isValid = await client.ValidateKeyAsync();
                    else
                        isValid = true; // Unknown provider — accept
                }
                catch (HttpRequestException ex) when (ex.StatusCode is global::System.Net.HttpStatusCode.Unauthorized
                                                           or global::System.Net.HttpStatusCode.Forbidden)
                {
                    isValid = false;
                }
                catch (Exception)
                {
                    couldNotValidate = true;
                }

                if (isValid)
                {
                    // A failed LOCAL save (DPAPI refusal — F28/F44) must not report success:
                    // the key would silently evaporate at restart.
                    if (!_apiKeyManager.TryCommitApiKey(providerKey, key, generation, out var saveResult))
                    {
                        Logger.Information("API key save for {Provider} superseded by a newer write", providerKey);
                        return;
                    }
                    if (!saveResult.IsSuccess())
                    {
                        statusIcon.Glyph = "\uE783"; // Error
                        statusIcon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                        statusLabel.Text = saveResult == Services.System.ApiKeySaveResult.InvalidFormat ? Helpers.ApiKeyFormat.DescribeCandidate(key) : "Could not save the key on this device — try again";
                        statusLabel.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                        return;
                    }
                    Logger.Information("API key saved for provider: {Provider}", providerKey);
                    statusIcon.Glyph = "\uE73E"; // Checkmark
                    statusIcon.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
                    statusLabel.Text = "Key valid";
                    statusLabel.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
                    await Task.Delay(1500);
                    RefreshCloudProviders();
                }
                else if (couldNotValidate)
                {
                    // Network error — save the key but warn
                    if (!_apiKeyManager.TryCommitApiKey(providerKey, key, generation, out var saveResult))
                    {
                        Logger.Information("API key save for {Provider} superseded by a newer write", providerKey);
                        return;
                    }
                    if (!saveResult.IsSuccess())
                    {
                        statusIcon.Glyph = "\uE783"; // Error
                        statusIcon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                        statusLabel.Text = saveResult == Services.System.ApiKeySaveResult.InvalidFormat ? Helpers.ApiKeyFormat.DescribeCandidate(key) : "Could not save the key on this device — try again";
                        statusLabel.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                        return;
                    }
                    statusIcon.Glyph = "\uE7BA"; // Warning
                    statusIcon.Foreground = AppTheme.Brush(AppTheme.WarningText);
                    statusLabel.Text = "Could not validate — key saved";
                    statusLabel.Foreground = AppTheme.Brush(AppTheme.WarningText);
                }
                else
                {
                    // Invalid — restore previous key, don't save
                    if (!_apiKeyManager.IsCurrentKeyWrite(providerKey, generation))
                    {
                        // ENH-17 (Codex round 3): a stale REJECTED validation must not repaint a
                        // row a newer save already owns — restoring the previous password can empty
                        // the box, and the next save would then clear the newer key.
                        Logger.Information("API key rejection for {Provider} superseded — row left alone", providerKey);
                        return;
                    }
                    passwordBox.Password = previousKey;
                    statusIcon.Glyph = "\uE783"; // Error
                    statusIcon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                    statusLabel.Text = "Invalid API key";
                    statusLabel.Foreground = AppTheme.Brush(AppTheme.AccentRed);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error saving/validating API key");
            }
        };

        container.Children.Add(passwordBox);
        container.Children.Add(saveBtn);
        container.Children.Add(statusPanel);

        if (isEditing)
        {
            var cancelBtn = AppTheme.CreateCompactButton("Cancel");
            cancelBtn.Tapped += (_, _) => RefreshCloudProviders();
            container.Children.Add(cancelBtn);
        }
    }

    /// <summary>
    /// Creates a single cloud model card row with name and select/active button.
    /// </summary>
    private UIElement CreateCloudModelCard(TranscriptionModelInfo model, string providerKey)
    {
        // Canonicalized (PRM-3): an oddly-cased persisted selection still lights up
        // its catalog card as active.
        var isActive = CloudModels.Canonicalize(_viewModel.SelectedModelName) == model.Name;
        var hasKey = _apiKeyManager.HasApiKey(providerKey);

        // ── Left side: cloud icon + model name ──────────────────
        var iconColor = isActive
            ? AppTheme.AccentGreen
            : hasKey
                ? AppTheme.AccentBlue
                : AppTheme.SubtleText;

        var iconBg = isActive
            ? AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88))   // green 20%
            : hasKey
                ? AppTheme.Brush(AppTheme.ActivePillBg)                // blue 20%
                : AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)); // subtle

        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = iconBg,
            Child = new FontIcon
            {
                Glyph = "\uE753", // Cloud icon
                FontSize = 14,
                Foreground = AppTheme.Brush(iconColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var nameBlock = new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Guidance line, mirroring the local rows' description style (owner 2026-07-31).
        var cloudDescBlock = new TextBlock
        {
            Text = GetCloudModelDescription(model.Name),
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var cloudTextPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameBlock, cloudDescBlock }
        };

        var leftPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconBorder, cloudTextPanel },
            Spacing = 10
        };

        // ── Right side: select button or active badge ───────────
        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isActive)
        {
            // Green "Active" badge
            var activeBadge = new Border
            {
                Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Active",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            rightPanel.Children.Add(activeBadge);
        }
        else
        {
            if (!hasKey)
            {
                // Warning icon — no API key
                var warningIcon = new FontIcon
                {
                    Glyph = "\uE7BA", // Warning icon
                    FontSize = 14,
                    Foreground = AppTheme.Brush(AppTheme.WarningText),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                rightPanel.Children.Add(warningIcon);
            }

            var selectBtn = AppTheme.CreateCompactButton("Select");
            selectBtn.MinWidth = 80;
            var modelName = model.Name; // capture for closure

            if (!hasKey)
            {
                AppTheme.SetButtonEnabled(selectBtn, false);
            }
            else
            {
                selectBtn.Tapped += (_, _) =>
                {
                    _viewModel.SelectCloudModel(modelName);
                    RefreshModelList();
                };
            }

            rightPanel.Children.Add(selectBtn);
        }

        // ── Assemble row grid ───────────────────────────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);

        // Subtle separator line between model rows
        return new Border
        {
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 4),
            Child = grid
        };
    }

    // Model guidance (owner formula 2026-07-31 v2, unchanged in FORM): a star weighting on
    // exactly three labels — Accuracy ★1–5, Speed ★1–5, and a language-count bucket.
    //
    // The rows and their written bases now live in Helpers/ModelRatings.cs (backlog TRN-3).
    // They used to be two switch expressions right here with the reasoning in a comment, and
    // nothing checked the reasoning: accuracy was read off FOUR evidence sources at once, which
    // produced a visible ordering inversion (gpt-4o-mini-transcribe below nova-3 while carrying
    // the better measured value). The measured third-party index values that fixed that were
    // later REMOVED before go-public (backlog LNC-6 data-licence decision, 2026-08-23); the
    // corrected ordering stands as judgement, recorded per row in ModelRatings.cs.
    //
    // The anchoring rules survive intact and are now enforced by tests rather than by care:
    // the same weights carry the same DISPLAYED accuracy stars on every host, and SPEED does
    // not, being openly the model plus its host. Note "displayed" — this is an owner-pinned
    // PRESENTATION rule, not a measured fact: published indices score identical Whisper weights
    // very differently depending on who serves them, so accuracy is emphatically not a property
    // of the model alone; Helpers/ModelRatings.cs is why the hosted rows we have no basis for
    // are marked unmeasured rather than rated off someone else's run.
    //
    // Display format is byte-identical to the hand-written version. This card moved where the
    // numbers come from, not how they look.
    //
    // TRN-52 (2026-09-02): the SPEED star each local row shows follows the live
    // LocalComputeSnapshot — the GPU set when that row's ENGINE resolved a GPU backend in this
    // process, the CPU set otherwise. Both the rows and the Active Model card go through the
    // two-argument ModelRatings forms, which read the same snapshot, so they cannot disagree.
    // The snapshot is re-read on every render; a page already open when the Whisper backend
    // first loads keeps its stars until re-navigated (accepted). Accuracy never varies.

    /// <summary>Active Model subtitle: the left part (file size for a local model, the provider
    /// name for a cloud one), the star pair, and the language bucket. ANY part may be absent — a
    /// cloud row has no size, and an unknown model contributes neither stars nor bucket — so
    /// separators are derived from what actually survives rather than positioned in advance
    /// (never a stray "· " on the card, and never a doubled one when only the middle is missing,
    /// which is the case a fixed two-slot format got wrong once the third part arrived).
    ///
    /// <para>TRN-7 (owner UAT 2026-08-03) added the stars beside the languages the 2026-07-31
    /// request put here. Both come from <see cref="ModelRatings"/>, the same table the rows below
    /// render from, so the card cannot disagree with the row it describes.</para></summary>
    internal static string ComposeActiveModelSubtitle(string? left, string? ratings, string? languages) =>
        string.Join(" · ", new[] { left, ratings, languages }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>The language bucket alone, for the Active Model card (owner request 2026-07-31 —
    /// the card must state language support like the rows below it). Reads the same table the
    /// rows render from, so the card can never disagree with the row. Unknown/custom models
    /// contribute nothing rather than a guess.</summary>
    private static string? GetLanguageSupportLabel(string modelName) => ModelRatings.LanguagesFor(modelName);

    /// <summary>The accuracy/speed star pair alone, for the Active Model card (TRN-7). Same table,
    /// same reasoning as <see cref="GetLanguageSupportLabel"/>: an unknown model yields null and
    /// simply drops out of the subtitle, rather than rendering an invented rating.</summary>
    internal static string? GetRatingStarsLabel(string modelName, LocalComputeSnapshot compute) =>
        ModelRatings.StarsFor(modelName, compute);

    internal static string GetModelDescription(string modelName) =>
        GetModelDescription(modelName, LocalComputeSnapshot.Current);

    /// <summary>The row description against an explicit snapshot — the form RefreshModelList
    /// uses, so every row and the Active Model card of one refresh share one reading
    /// (TRN-52, Codex diff r1 Blocker).</summary>
    internal static string GetModelDescription(string modelName, LocalComputeSnapshot compute) =>
        ModelRatings.Describe(modelName, "Whisper model for local transcription.", compute);

    internal static string GetCloudModelDescription(string modelName) =>
        ModelRatings.Describe(modelName, "Cloud transcription model.");
}

// Extension to get DI services from page
internal static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider provider) where T : notnull
        => (T)provider.GetService(typeof(T))!;
}
