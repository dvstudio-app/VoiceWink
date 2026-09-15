using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AppMode;
using VoiceWink.Services.Transcription;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// App Mode page — per-app recording configuration.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class AppModePage : Page
{
    private static ILogger Logger => Log.ForContext<AppModePage>();

    private readonly AppModeManager _manager;
    private readonly ModelDownloadManager _modelDownloader;
    private readonly AIEnhancementService _enhancement;
    private StackPanel? _configList;

    public AppModePage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        _manager = App.Services.GetRequiredService<AppModeManager>();
        _modelDownloader = App.Services.GetRequiredService<ModelDownloadManager>();
        _enhancement = App.Services.GetRequiredService<AIEnhancementService>();

        BuildUI();
    }

    private void BuildUI()
    {
        var header = AppTheme.CreatePageHeader(
            "App Mode",
            "Create per-app configurations that auto-activate based on the focused window.");

        var addButton = AppTheme.CreateAccentButton("+ Add Configuration", (_, _) =>
        {
            // Open the editor on a fresh (not-yet-persisted) config — mirrors the Enhancement
            // page's "+ Add Prompt". The config is added to the manager ONLY on a valid Save
            // (ShowEditConfigDialog isNew), so a cancelled add leaves no stub "New Config" behind.
            ShowEditConfigDialog(
                new AppModeConfig { Name = "", ProcessPatterns = [], IsEnabled = false },
                isNew: true);
        });
        addButton.HorizontalAlignment = HorizontalAlignment.Left;

        var templateBtn = AppTheme.CreateSecondaryButton("Add from Template", (_, _) =>
        {
            ShowTemplateDialog();
        });
        templateBtn.HorizontalAlignment = HorizontalAlignment.Left;

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, AppTheme.SectionSpacing),
            Children = { addButton, templateBtn }
        };

        _configList = new StackPanel { Spacing = 12 };
        RefreshList();

        var outerStack = new StackPanel
        {
            Children = { header, buttonRow, _configList }
        };

        AppTheme.SetPageScrollContent(this, outerStack);
    }

    private void RefreshList()
    {
        if (_configList == null) return;
        _configList.Children.Clear();

        var configs = _manager.GetConfigs();

        if (configs.Count == 0)
        {
            _configList.Children.Add(CreateEmptyState());
            return;
        }

        // Display-only alphabetical sort by name (UAT 2026-07-22). GetConfigs() returns the
        // live cached list used for activation matching — order it for display without mutating it.
        foreach (var config in configs.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            _configList.Children.Add(CreateConfigCard(config));
    }

    private Border CreateEmptyState()
    {
        var icon = new FontIcon
        {
            Glyph = "\uE7AC",
            FontSize = 36,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var title = new TextBlock
        {
            Text = "No configurations yet",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var desc = new TextBlock
        {
            Text = "Add a configuration to customize transcription and enhancement behavior for specific applications.",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 32, 0, 32),
            Children = { icon, title, desc }
        };

        return AppTheme.CreateCard(content);
    }

    // ────────────────────────────────────────────────────────────────
    // Config card — mirrors Enhancement page prompt card pattern
    // ────────────────────────────────────────────────────────────────

    private Border CreateConfigCard(AppModeConfig config)
    {
        // ── Name ──
        var nameBlock = new TextBlock
        {
            Text = config.Name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };

        // ── Process pattern pills ──
        var pillsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var hasPatterns = config.ProcessPatterns.Length > 0
            && config.ProcessPatterns.Any(p => !string.IsNullOrWhiteSpace(p));

        if (hasPatterns)
        {
            foreach (var pattern in config.ProcessPatterns)
            {
                if (!string.IsNullOrWhiteSpace(pattern))
                    pillsPanel.Children.Add(CreateProcessPill(pattern));
            }
        }
        else
        {
            pillsPanel.Children.Add(CreateWarningPill("No process pattern \u2014 config will never activate"));
        }

        // ── Override info ──
        var overridePanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 2 };

        if (!string.IsNullOrEmpty(config.ModelOverride))
        {
            var modelDisplay = ModelDisplayName.Resolve(config.ModelOverride);
            overridePanel.Children.Add(CreateOverrideText($"Model: {modelDisplay}"));
        }
        if (!string.IsNullOrEmpty(config.LanguageOverride))
        {
            var langDisplay = ModelManagementViewModel.GetLanguageDisplayName(config.LanguageOverride);
            overridePanel.Children.Add(CreateOverrideText($"Language: {langDisplay}"));
        }
        if (!string.IsNullOrEmpty(config.LinkedEnhancementId))
        {
            var promptTitle = _enhancement.GetPrompts()
                .FirstOrDefault(p => p.Id == config.LinkedEnhancementId)?.Title;
            if (promptTitle != null)
                overridePanel.Children.Add(CreateOverrideText($"Enhancement: {promptTitle}"));
            else
                overridePanel.Children.Add(CreateOverrideText("Enhancement: (deleted)",
                    AppTheme.WarningText));
        }

        if (overridePanel.Children.Count == 0)
            overridePanel.Children.Add(CreateOverrideText("No overrides configured"));

        // ── Active badge ──
        var activeBadge = new TextBlock
        {
            Text = "Active",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.AccentGreen),
            Visibility = config.IsEnabled ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0)
        };

        // ── Action buttons (same pattern as Enhancement page) ──
        var buttonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Spacing = 8
        };

        var activateBtn = AppTheme.CreateSecondaryButton(
            config.IsEnabled ? "Deactivate" : "Activate",
            (_, _) =>
            {
                config.IsEnabled = !config.IsEnabled;
                _manager.UpdateConfig(config);
                RefreshList();
            });
        buttonsRow.Children.Add(activateBtn);

        var configId = config.Id;
        var editBtn = AppTheme.CreateSecondaryButton("Edit", (_, _) =>
        {
            ShowEditConfigDialog(config);
        });
        buttonsRow.Children.Add(editBtn);

        var deleteBtn = AppTheme.CreateDangerButton("Delete", (_, _) =>
        {
            _manager.DeleteConfig(configId);
            RefreshList();
        });
        buttonsRow.Children.Add(deleteBtn);

        // ── Assemble card ──
        var innerPanel = new StackPanel
        {
            Children = { nameBlock, pillsPanel, overridePanel, activeBadge, buttonsRow }
        };

        // Active card gets a left green accent border (like Enhancement page uses blue)
        if (config.IsEnabled)
        {
            return new Border
            {
                Background = AppTheme.Brush(AppTheme.CardBg),
                BorderBrush = AppTheme.Brush(AppTheme.AccentGreen),
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
                Padding = new Thickness(AppTheme.CardPadding),
                Margin = new Thickness(0, 0, 0, 4),
                Child = innerPanel
            };
        }

        return AppTheme.CreateCard(innerPanel);
    }

    // ────────────────────────────────────────────────────────────────
    // Edit dialog
    // ────────────────────────────────────────────────────────────────

    private async void ShowEditConfigDialog(AppModeConfig config, bool isNew = false)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = isNew ? "New Configuration" : "Edit Configuration",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };

            var nameBox = new TextBox
            {
                Text = config.Name,
                PlaceholderText = "Configuration name",
                Margin = new Thickness(0, 0, 0, 12)
            };

            // Keep Save disabled until the name has real content, so a blank New Configuration
            // can't be saved into a nameless config (mirrors the Enhancement page). An existing
            // config opens pre-filled, so Save starts enabled and only disables if cleared.
            void UpdateSaveEnabled() =>
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
            nameBox.TextChanged += (_, _) => UpdateSaveEnabled();
            UpdateSaveEnabled();

            // Filter out the placeholder "processname" sentinel from old configs
            var existingPatterns = config.ProcessPatterns
                .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Equals("processname", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var processBox = new TextBox
            {
                Text = string.Join(", ", existingPatterns),
                PlaceholderText = "e.g. chrome, firefox, slack",
                Margin = new Thickness(0, 0, 0, 12)
            };

            var processHint = new TextBlock
            {
                Text = "Comma-separated process names (without .exe). Matched against the focused window.",
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };

            // ── Model override: toggle + two mutually exclusive dropdowns ──
            var useDefaultModel = string.IsNullOrEmpty(config.ModelOverride);
            var isUsingDefaultModel = useDefaultModel;

            var modelOverridePanel = new StackPanel
            {
                Spacing = 4,
                Visibility = useDefaultModel ? Visibility.Collapsed : Visibility.Visible
            };

            var localModelCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "None",
                Margin = new Thickness(0, 0, 0, 4)
            };
            AppTheme.AllowParentScroll(localModelCombo);

            var downloaded = _modelDownloader.GetDownloadedModels();
            var localModels = new List<(string Name, string Display)>();

            // Catalog-claimed only, and identified by CATALOG name — the Tag is persisted as the
            // App Mode override, so tagging the on-disk spelling would write a case variant into
            // settings. Onboarding asks the same helper the same question.
            foreach (var model in InstalledCatalogModels.Known(downloaded))
            {
                localModels.Add((model.Name, model.DisplayName));
                localModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = model.DisplayName,
                    Tag = model.Name
                });
            }

            if (localModels.Count != downloaded.Count)
            {
                Logger.Information(
                    "App Mode: {Skipped} downloaded model file(s) are not in the catalog and were not listed",
                    downloaded.Count - localModels.Count);
            }

            var cloudModelCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "None",
                Margin = new Thickness(0, 0, 0, 4)
            };
            AppTheme.AllowParentScroll(cloudModelCombo);

            foreach (var cloud in CloudModels.Models)
                cloudModelCombo.Items.Add(new ComboBoxItem { Content = cloud.DisplayName, Tag = cloud.Name });

            modelOverridePanel.Children.Add(CreateDialogLabel("Local Model"));
            modelOverridePanel.Children.Add(localModelCombo);
            modelOverridePanel.Children.Add(CreateDialogLabel("Cloud Model"));
            modelOverridePanel.Children.Add(cloudModelCombo);

            // Pre-select current value in the correct dropdown. Canonicalized
            // (PRM-3): an oddly-cased persisted override must still match its
            // dropdown item — a failed exact preselection could clear the override on
            // Save (Codex diff review).
            bool suppressModelSync = true;
            if (!string.IsNullOrEmpty(config.ModelOverride))
            {
                var canonicalOverride = CloudModels.Canonicalize(config.ModelOverride);
                var isCloud = CloudModels.IsCloudModel(canonicalOverride);
                if (isCloud)
                {
                    foreach (ComboBoxItem item in cloudModelCombo.Items)
                    {
                        if (item.Tag is string tag && tag == canonicalOverride)
                        { cloudModelCombo.SelectedItem = item; break; }
                    }
                }
                else
                {
                    for (int i = 0; i < localModels.Count; i++)
                    {
                        // ModelOverride is a persisted settings value, so its casing is whatever was
                        // written or imported. Ordinal equality silently fails to preselect, and
                        // the dialog then looks like no override is configured.
                        if (ModelDiskReconciliation.IsSameModel(localModels[i].Name, config.ModelOverride))
                        { localModelCombo.SelectedIndex = i; break; }
                    }
                }
            }

            // Mutual exclusion: selecting one clears the other
            localModelCombo.SelectionChanged += (_, _) =>
            {
                if (suppressModelSync) return;
                if (localModelCombo.SelectedIndex >= 0)
                {
                    suppressModelSync = true;
                    cloudModelCombo.SelectedIndex = -1;
                    suppressModelSync = false;
                }
            };
            cloudModelCombo.SelectionChanged += (_, _) =>
            {
                if (suppressModelSync) return;
                if (cloudModelCombo.SelectedIndex >= 0)
                {
                    suppressModelSync = true;
                    localModelCombo.SelectedIndex = -1;
                    suppressModelSync = false;
                }
            };
            suppressModelSync = false;

            var defaultModelToggle = new ToggleSwitch
            {
                IsOn = useDefaultModel,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, -12, 0)
            };
            AppTheme.AllowParentScroll(defaultModelToggle);
            defaultModelToggle.Toggled += (_, _) =>
            {
                isUsingDefaultModel = defaultModelToggle.IsOn;
                if (defaultModelToggle.IsOn)
                {
                    modelOverridePanel.Visibility = Visibility.Collapsed;
                    suppressModelSync = true;
                    localModelCombo.SelectedIndex = -1;
                    cloudModelCombo.SelectedIndex = -1;
                    suppressModelSync = false;
                }
                else
                {
                    modelOverridePanel.Visibility = Visibility.Visible;
                }
            };
            var defaultModelLabel = new TextBlock
            {
                Text = "Use default transcription model",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                VerticalAlignment = VerticalAlignment.Center
            };
            var useDefaultToggle = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            useDefaultToggle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            useDefaultToggle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(defaultModelLabel, 0);
            Grid.SetColumn(defaultModelToggle, 1);
            useDefaultToggle.Children.Add(defaultModelLabel);
            useDefaultToggle.Children.Add(defaultModelToggle);

            // ── Language override ──
            var languageCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 12)
            };
            AppTheme.AllowParentScroll(languageCombo);

            languageCombo.Items.Add(new ComboBoxItem { Content = "Use default", Tag = "" });
            languageCombo.Items.Add(new ComboBoxItem { Content = "Auto-detect", Tag = "auto" });

            var sortedLanguages = ModelManagementViewModel.SupportedLanguages
                .Where(c => c != "auto")
                .OrderBy(c => ModelManagementViewModel.GetLanguageDisplayName(c),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var isoCode in sortedLanguages)
            {
                languageCombo.Items.Add(new ComboBoxItem
                {
                    Content = ModelManagementViewModel.GetLanguageDisplayName(isoCode),
                    Tag = isoCode
                });
            }

            languageCombo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(config.LanguageOverride))
            {
                foreach (ComboBoxItem item in languageCombo.Items)
                {
                    if (item.Tag is string tag && tag == config.LanguageOverride)
                    {
                        languageCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            // ── Enhancement override ──
            var enhancementCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4)
            };
            AppTheme.AllowParentScroll(enhancementCombo);

            enhancementCombo.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
            var prompts = _enhancement.GetPrompts();
            foreach (var p in prompts)
                enhancementCombo.Items.Add(new ComboBoxItem { Content = p.Title, Tag = p.Id });

            enhancementCombo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(config.LinkedEnhancementId))
            {
                foreach (ComboBoxItem item in enhancementCombo.Items)
                {
                    if (item.Tag is string tag && tag == config.LinkedEnhancementId)
                    {
                        enhancementCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            var enhancementHint = new TextBlock
            {
                Text = "When this config activates, the selected enhancement runs automatically (even if enhancement is globally disabled).",
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };

            dialog.Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Width = 400,
                    Children =
                    {
                        CreateDialogLabel("Name"),
                        nameBox,
                        CreateDialogLabel("Process Patterns"),
                        processBox,
                        processHint,
                        useDefaultToggle,
                        modelOverridePanel,
                        CreateDialogLabel("Language"),
                        languageCombo,
                        CreateDialogLabel("Enhancement"),
                        enhancementCombo,
                        enhancementHint
                    }
                },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 620
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                var newName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(newName)) { args.Cancel = true; return; }

                var patterns = processBox.Text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                config.Name = newName;
                config.ProcessPatterns = patterns;

                // Model override — null when "use default" is checked
                string? selectedModel = null;
                if (!isUsingDefaultModel)
                {
                    if (cloudModelCombo.SelectedItem is ComboBoxItem cloudItem
                        && cloudItem.Tag is string cloudTag && !string.IsNullOrEmpty(cloudTag))
                        selectedModel = cloudTag;
                    else if (localModelCombo.SelectedItem is ComboBoxItem localItem
                        && localItem.Tag is string localTag && !string.IsNullOrEmpty(localTag))
                        selectedModel = localTag;
                }
                config.ModelOverride = selectedModel;

                // Language override
                config.LanguageOverride = languageCombo.SelectedItem is ComboBoxItem langItem
                    && langItem.Tag is string langTag && !string.IsNullOrEmpty(langTag)
                    ? langTag : null;

                // Enhancement override
                config.LinkedEnhancementId = enhancementCombo.SelectedItem is ComboBoxItem enhItem
                    && enhItem.Tag is string enhTag && !string.IsNullOrEmpty(enhTag)
                    ? enhTag : null;

                // Clear legacy field
                config.PromptOverride = null;

                // A new config is persisted ONLY here, on a valid Save; an existing config is
                // updated in place.
                if (isNew)
                    _manager.AddConfig(config);
                else
                    _manager.UpdateConfig(config);
                RefreshList();
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShowEditConfigDialog failed");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Template dialog
    // ────────────────────────────────────────────────────────────────

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
            AppModeTemplate? selected = null;

            var availableTemplates = _manager.GetAvailableTemplates();
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
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 12, 0)
                };
                try { iconBlock.Glyph = char.ConvertFromUtf32(Convert.ToInt32(template.Icon, 16)); }
                catch { iconBlock.Glyph = "\uE7AC"; }

                var titleText = new TextBlock
                {
                    Text = template.Name,
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppTheme.Brush(AppTheme.TextPrimary)
                };

                var descText = new TextBlock
                {
                    Text = template.Description,
                    FontSize = 12,
                    Foreground = AppTheme.Brush(AppTheme.SubtleText)
                };

                var pillsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                foreach (var pattern in template.ProcessPatterns)
                {
                    pillsPanel.Children.Add(new Border
                    {
                        Background = AppTheme.Brush(AppTheme.ActivePillBg),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(6, 2, 6, 2),
                        Child = new TextBlock
                        {
                            Text = pattern,
                            FontSize = 11,
                            Foreground = AppTheme.Brush(AppTheme.AccentBlue)
                        }
                    });
                }

                var textPanel = new StackPanel { Children = { titleText, descText, pillsPanel } };

                if (!string.IsNullOrEmpty(template.DefaultEnhancementTitle))
                {
                    textPanel.Children.Add(new TextBlock
                    {
                        Text = $"Enhancement: {template.DefaultEnhancementTitle}",
                        FontSize = 11,
                        Foreground = AppTheme.Brush(AppTheme.SubtleText),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

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
                    Padding = new Thickness(16, 12, 16, 12),
                    Child = row
                };

                var normalBg = AppTheme.Brush(AppTheme.CardBg);
                var hoverBg = AppTheme.Brush(AppTheme.HoverBg);
                card.PointerEntered += (_, _) => card.Background = hoverBg;
                card.PointerExited += (_, _) => card.Background = normalBg;

                var capturedTemplate = template;
                card.Tapped += (_, _) =>
                {
                    // Capture the choice and close. The app-mode add + default-enhancement
                    // resolve/add + any notice run AFTER this dialog is fully closed (below), so a
                    // second ContentDialog is never opened while this one is still open.
                    selected = capturedTemplate;
                    dialog.Hide();
                };

                templateList.Children.Add(card);
            }

            dialog.Content = new ScrollViewer
            {
                Content = templateList,
                MaxHeight = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            await dialog.ShowAsync();

            if (selected == null) return; // cancelled — nothing chosen

            // Add the app mode AND ensure/link its default enhancement in one testable step
            // (orphan-safe: it preflights the app-mode add before persisting any enhancement).
            var coordinator = App.Services.GetRequiredService<AppModeSetupCoordinator>();
            var result = coordinator.AddAppModeFromTemplate(selected);
            RefreshList();

            var notice = AppModeAddNotice.Message(result);
            if (notice != null)
            {
                await new ContentDialog
                {
                    Title = "App mode added",
                    Content = new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShowTemplateDialog failed");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static TextBlock CreateDialogLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = AppTheme.Brush(AppTheme.SubtleText),
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Border CreateProcessPill(string pattern)
    {
        return new Border
        {
            Background = AppTheme.Brush(AppTheme.ActivePillBg),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new TextBlock
            {
                Text = pattern,
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentBlue),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            }
        };
    }

    private static Border CreateWarningPill(string text)
    {
        return new Border
        {
            Background = AppTheme.Brush(
                ColorHelper.FromArgb(40, AppTheme.AccentAmber.R, AppTheme.AccentAmber.G, AppTheme.AccentAmber.B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.WarningText),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            }
        };
    }

    private static TextBlock CreateOverrideText(string text, Windows.UI.Color? color = null) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = AppTheme.Brush(color ?? AppTheme.SubtleText),
        TextWrapping = TextWrapping.Wrap
    };

}
