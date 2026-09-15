using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using SharpHook.Native;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.Data;
using VoiceWink.Services.Privacy;
using VoiceWink.Services.System;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Settings page with grouped dark cards.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class SettingsPage : Page
{
    private static ILogger Logger => Log.ForContext<SettingsPage>();

    private readonly SettingsViewModel _viewModel;

    public SettingsPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
        BuildUI();
    }

    private void BuildUI()
    {
        // ── Page header ───────────────────────────────────────────────
        var header = AppTheme.CreatePageHeader("Settings", "Configure VoiceWink to your preferences.");

        // Resolved ONCE here and passed to the cards added in the 2026-08-04 grouping pass.
        // AGENTS.md forbids adding new App.Services lookups; this page already carries thirteen
        // of them (each Build*Card resolves its own), and those are left alone — the rule is about
        // not adding more. BuildUI is the page's composition point, so resolving here and passing
        // down is the "existing seam" the rule points at, and it is one lookup rather than the two
        // the new cards would otherwise have taken (Codex diff review).
        var settings = App.Services.GetRequiredService<Services.System.SettingsService>();

        // ── Keyboard Shortcuts card ───────────────────────────────────
        var shortcutsCard = BuildKeyboardShortcutsCard();

        // ── Audio card ────────────────────────────────────────────────
        var audioCard = BuildAudioCard();

        // ── Recorder card (PILL-4) ────────────────────────────────────
        var recorderCard = BuildRecorderCard();

        // ── Text Processing card (now includes the filler-word list) ──
        var textProcessingCard = BuildTextProcessingCard();

        // ── Clipboard card ────────────────────────────────────────────
        var clipboardCard = BuildClipboardCard();

        // ── History cleanup / Your data / Settings backup ─────────────
        // Three cards where "Data Management" used to be one holding three topics.
        var historyCleanupCard = BuildHistoryCleanupCard();
        var yourDataCard = BuildYourDataCard();
        var settingsBackupCard = BuildSettingsBackupCard();

        // ── Diagnostics card ────────────────────────────────────────────
        var diagnosticsCard = BuildDiagnosticsCard(settings);

        // (About / Legal / Support moved to the dedicated About sidebar page — AboutPage.cs)

        // ── General card ─────────────────────────────────────────────────
        // Theme + the three window/startup behaviours. These four were LOOSE cards leading the
        // page while everything after them was grouped, so a reader got no orientation until the
        // sixth row (owner asked for a grouping pass, 2026-08-04). "Send Crash Reports" used to
        // sit here too and moved to Diagnostics, where it belongs — it is a privacy consent, and
        // it was only here because this is where it was added.
        var generalCard = BuildGeneralCard(settings);

        // ── Relaunch Wizard button ──────────────────────────────────────
        var wizardButton = AppTheme.CreateSecondaryButton("Relaunch setup wizard", (_, _) =>
        {
            if (App.MainWindow is Views.MainWindow mainWindow)
            {
                var settings = App.Services.GetRequiredService<Services.System.SettingsService>();
                settings.SetBool(AppDefaults.HasCompletedOnboarding, false);
                mainWindow.ShowOnboarding();
            }
        });

        // ── Reset All button ────────────────────────────────────────────
        var resetAllButton = AppTheme.CreateSecondaryButton("Reset all settings to defaults", async (_, _) =>
        {
            // Says what the reset KEEPS (2026-09-13): the button's label promises "all settings",
            // but ResetAllSettings restores a deliberate subset and preserves everything that is
            // content or identity rather than a preference. A dialog that only asks "are you sure"
            // lets the user expect a factory state and find their prompts still there — or, worse,
            // fear their license and keys are about to go. The AI Enhancement switch and its
            // provider/model choices are NAMED among the kept items (Codex diff round 1, PR #914):
            // "every preference" read as "enhancement back to its shipped OFF", and a user acting
            // on that would dictate straight into an AI provider they believed was reset off.
            // SettingsViewModelResetTests pins the sentence to the behaviour.
            var dialog = new ContentDialog
            {
                Title = "Reset all settings",
                Content = "This resets these preferences to their defaults: hotkeys, audio, text " +
                          "processing, clipboard, appearance, startup, mini recorder, history " +
                          "recording and its cleanup, the Dictionary switches, speaker labels, GPU, " +
                          "updates and diagnostics.\n\n" +
                          "It keeps your license, API keys, " +
                          "the AI Enhancement switch and its provider and model choices, " +
                          "prompts, App Modes, dictionary words and replacements, history entries, " +
                          "the selected speech model and language, and your crash-report choice.",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _viewModel.ResetAllSettingsCommand.Execute(null);
                // Rebuild UI to reflect new values
                BuildUI();
            }
        });

        // Relaunch Wizard + Reset All sit in a single horizontal row.
        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { wizardButton, resetAllButton },
        };

        // ── Version label ──────────────────────────────────────────────
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionLabel = new TextBlock
        {
            Text = $"VoiceWink v{version?.ToString(3) ?? "0.0.0"}",
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 8),
            Opacity = 0.6
        };

        // ── Assemble page ─────────────────────────────────────────────
        var pageContent = new StackPanel
        {
            Children =
            {
                // Order (owner grouping pass, 2026-08-04): what the app looks like and when it
                // runs → how you invoke it → capture → the recording UI → what happens to the
                // text → what is kept → troubleshooting → destructive/global actions last.
                // Roughly descending frequency, and the two cards that can delete user data sit
                // next to each other instead of one being buried mid-page.
                header,
                generalCard,
                shortcutsCard,
                audioCard,
                recorderCard,
                textProcessingCard,
                clipboardCard,
                historyCleanupCard,
                yourDataCard,
                settingsBackupCard,
                diagnosticsCard,
                actionButtons,
                versionLabel
            }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    /// <summary>Keyboard shortcuts section — the four role hotkey rows (HKY-3: each is a
    /// modifier picker plus a key picker).</summary>
    private Border BuildKeyboardShortcutsCard()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Keyboard shortcuts");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12); // No top margin for first card

        // Conflict warning (shared across all four hotkeys)
        var warningText = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        UpdateHotkeyWarning(warningText, _viewModel.HotkeyModifier);

        // HKY-3: each row is a HotkeyBindingEditor (modifier picker + key picker presented as one
        // value). The four rows previously recovered their control by walking the child Grid and
        // taking the first ComboBox — which a second dropdown would have silently broken.
        //
        // The rows are built by iterating HotkeyRole rather than being listed one by one, so a new
        // role gets a row, a conflict check and a reset target without anyone remembering to add
        // it in four places. That is the same reason HotkeyConflictScan exists: every hand-written
        // enumeration of the hotkey roles in this app has turned out to be missing something.
        var editors = new Dictionary<HotkeyRole, HotkeyBindingEditor>();
        HotkeyBindingEditor? EditorForRole(HotkeyRole role)
            => editors.TryGetValue(role, out var editor) ? editor : null;

        foreach (var role in HotkeyBindingSnapshot.AllRoles)
        {
            editors[role] = BuildHotkeyRow(
                RowTitleFor(role),
                // The recording hotkey must always exist, so its row offers no "None".
                includeNone: role != HotkeyRole.Recording,
                role,
                warningText,
                EditorForRole);
        }

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(sectionHeader);
        content.Children.Add(editors[HotkeyRole.Recording].Root);
        content.Children.Add(warningText);   // sits under the recording row, which is what it describes
        foreach (var role in HotkeyBindingSnapshot.AllRoles)
        {
            if (role == HotkeyRole.Recording) continue;
            content.Children.Add(editors[role].Root);
        }

        return AppTheme.CreateCard(content);
    }

    /// <summary>The Settings row label for a role. Total, so a new role cannot render untitled.</summary>
    private static string RowTitleFor(HotkeyRole role) => role switch
    {
        HotkeyRole.Recording => "Recording hotkey",
        HotkeyRole.PasteLast => "Paste last hotkey",
        HotkeyRole.RedoLast => "Redo last hotkey",
        HotkeyRole.GenerateImage => "Generate image hotkey",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Untitled hotkey role"),
    };

    /// <summary>
    /// One hotkey row. All four behave identically apart from which settings property they read
    /// and write, so the conflict flow lives here once rather than in four near-copies.
    /// </summary>
    private HotkeyBindingEditor BuildHotkeyRow(
        string title,
        bool includeNone,
        HotkeyRole role,
        TextBlock warningText,
        Func<HotkeyRole, HotkeyBindingEditor?> editorForRole)
    {
        var editor = new HotkeyBindingEditor(title, includeNone, _viewModel.HotkeyFor(role));
        StripCardBorder(editor.Root);

        // HKY-10: a modifier change that strands the key publishes nothing, so before this the
        // advisory below the card was never recomputed and went on describing the stored binding
        // while the pickers showed a blank key. Recording ONLY, because this page renders a warning
        // line for that role alone — every other row's UpdateHotkeyWarning call is already gated the
        // same way, so an unconditional handler here would write the recording row's warning block
        // from an edit to a different row.
        if (role == HotkeyRole.Recording)
        {
            editor.IncompleteChanged += () =>
            {
                var stored = _viewModel.HotkeyFor(role);
                UpdateHotkeyWarning(
                    warningText,
                    stored,
                    editor.IsIncomplete ? HotkeyWarnings.IncompleteBindingNote(title, stored) : null);
            };
        }

        editor.ValueChanged += async value =>
        {
            // Read BEFORE writing: this is the value to restore if the user cancels.
            var previousValue = _viewModel.HotkeyFor(role);

            // Every rejection path goes through here. The warning is updated OPTIMISTICALLY below
            // so it tracks the picker as the user moves through it, which means a rejection has to
            // put it back too — otherwise the card keeps describing a binding that was refused.
            void Revert()
            {
                editor.SetValue(previousValue);
                if (role == HotkeyRole.Recording) UpdateHotkeyWarning(warningText, previousValue);
            }

            if (role == HotkeyRole.Recording) UpdateHotkeyWarning(warningText, value);

            if (string.IsNullOrEmpty(value))
            {
                _viewModel.SetHotkey(role, value);
                return;
            }

            // The recording hotkey cannot be taken over by another role — it is the one hotkey
            // that must always exist, so this is a hard block rather than an override prompt.
            if (role != HotkeyRole.Recording
                && HotkeyBinding.Conflicts(value, _viewModel.HotkeyFor(HotkeyRole.Recording)))
            {
                await ShowBlockedHotkeyDialogAsync(value, "recording");
                Revert();
                return;
            }

            // A binding that shadows a near-universal application shortcut (Ctrl+C, Ctrl+V, …) is
            // CONFIRMED, never refused: "universal" is a claim about other people's apps, and a
            // user who wants Ctrl+P for dictation is entitled to it. But the hook will suppress
            // that chord everywhere, so it must not happen by accident.
            if (HotkeyBinding.TryParse(value, out var candidate, out _)
                && HotkeyBinding.ShadowsCommonAppShortcut(candidate))
            {
                var accepted = await ShowHotkeyConflictDialogAsync(
                    value,
                    $"{HotkeyKeyDisplay.Describe(value)} is a common application shortcut. " +
                    "While VoiceWink is running it will no longer reach the app you are typing in.");
                if (!accepted)
                {
                    Revert();
                    return;
                }
            }

            var conflict = GetConflictDescription(value, role);
            if (conflict != null)
            {
                var confirmed = await ShowHotkeyConflictDialogAsync(value, conflict);
                if (!confirmed)
                {
                    Revert();
                    return;
                }

                // Capture which ROWS to reset BEFORE clearing — ClearConflictingHotkey resets the
                // view-model properties, so asking afterwards would always answer "no". Both this
                // and the clear read the SAME scan, so a row can never be cleared in settings while
                // still showing its old value on screen.
                var clearedRoles = HotkeyConflictScan
                    .ForRole(_viewModel.HotkeySnapshot(), value, role)
                    .Where(c => c.Role.HasValue)
                    .Select(c => c.Role!.Value)
                    .ToList();

                ClearConflictingHotkey(value, role);

                foreach (var other in clearedRoles)
                    editorForRole(other)?.SetValue(string.Empty);
            }

            _viewModel.SetHotkey(role, value);
        };

        return editor;
    }

    /// <summary>
    /// Shows/hides an amber warning for conflict-prone hotkeys.
    /// Mirrors the logic in OnboardingPage.BuildHotkeyStep.
    /// </summary>
    /// <remarks>
    /// HKY-3 made this parse the binding instead of matching substrings, and that was a fix rather
    /// than a tidy-up. Canonical strings changed what the old tests saw: the Ctrl branch looked for
    /// <c>"Control"</c>, which a canonical binding never contains, so <c>Ctrl+C</c> — the single
    /// most warning-worthy binding available — produced NO warning at all; and
    /// <c>key.Contains("Shift")</c> matched <c>Ctrl+Shift+D</c> and told the user it "may interfere
    /// with capitals", which is true of a bare Shift hotkey and false of a combo.
    ///
    /// <para>The rule itself lives in <see cref="HotkeyWarnings.BindingWarning"/> — the bare-modifier
    /// switch since HKY-4, the two-branch rule around it since HKY-8, when onboarding became a combo
    /// editor and needed the shadow branch this page had kept to itself. What stays here is the
    /// UI-side wrapper: parse, render, collapse when there is nothing to say.</para>
    /// </remarks>
    /// <param name="leadingNote">
    /// HKY-10: the "this row is unfinished" line, or null. Rendered ABOVE the hazard rather than
    /// instead of it — an incomplete row's stored binding is untouched, so its hazard is still live.
    /// The early return on an unparseable key therefore had to go: a row mid-edit still has a note
    /// to show even when <paramref name="key"/> parses to nothing.
    /// </param>
    private static void UpdateHotkeyWarning(TextBlock warningText, string? key, string? leadingNote = null)
    {
        var hazard = HotkeyBinding.TryParse(key, out var binding, out _)
            ? HotkeyWarnings.BindingWarning(
                binding, KeyboardLayoutProfile.AnyInstalledLayoutNeedsAltGrForTyping())
            : null;

        var lines = new[] { leadingNote, hazard }.Where(l => l != null).ToArray();

        warningText.Text = string.Join("\n", lines);
        warningText.Visibility = lines.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Audio section — Sound Feedback + Mute System Audio toggles.</summary>
    private Border BuildAudioCard()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Audio");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var microphoneRow = BuildMicrophoneRow();

        // AUD-6: the standing warm capture toggle. The copy is deliberately honest about the
        // mic-in-use indicator — the feature's real cost — and names the one workload it can
        // get in the way of (exclusive-mode audio apps).
        var instantRecording = AppTheme.CreateToggleSetting(
            "Instant recording",
            "Keeps the microphone active so recording starts the moment you press the hotkey. " +
            "Windows shows the microphone-in-use indicator while VoiceWink runs. " +
            "Turn off if another app needs exclusive microphone access.",
            _viewModel.InstantRecordingEnabled,
            v => _viewModel.InstantRecordingEnabled = v);
        StripCardBorder(instantRecording);

        var soundFeedback = AppTheme.CreateToggleSetting(
            "Sound feedback",
            "Play beeps when starting/stopping recording",
            _viewModel.IsSoundFeedbackEnabled,
            v => _viewModel.IsSoundFeedbackEnabled = v);
        StripCardBorder(soundFeedback);

        var muteSystem = AppTheme.CreateToggleSetting(
            "Mute system audio",
            "Mute other apps while recording",
            _viewModel.IsSystemMuteEnabled,
            v => _viewModel.IsSystemMuteEnabled = v);
        StripCardBorder(muteSystem);

        // Audio resumption delay slider
        var resumptionLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Text = $"Unmute delay after recording: {_viewModel.AudioResumptionDelay:F1}s",
            Margin = new Thickness(4, 8, 0, 0)
        };
        var resumptionSlider = new Slider
        {
            Minimum = 0,
            Maximum = 3,
            StepFrequency = 0.1,
            Value = _viewModel.AudioResumptionDelay,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 0, 4, 0)
        };
        resumptionSlider.ValueChanged += (_, e) =>
        {
            _viewModel.AudioResumptionDelay = Math.Round(e.NewValue, 1);
            resumptionLabel.Text = $"Unmute delay after recording: {_viewModel.AudioResumptionDelay:F1}s";
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { sectionHeader, microphoneRow, instantRecording, soundFeedback, muteSystem, resumptionLabel, resumptionSlider }
        };

        return AppTheme.CreateCard(content);
    }

    // AUD-1: suppression flag for programmatic Microphone-combo reselects; one-way unload
    // latch fencing async refreshes that land after navigation away; generation counter so a
    // refresh that completes AFTER a user selection cannot visually reselect the old item
    // (diff review r2 — persistence was never at risk, but the combo showed the stale choice).
    private bool _micComboSuppress;
    private bool _pageUnloaded;
    private int _micSelectionGen;
    // AUD-7: a snapshot whose swap was DEFERRED because the popup was open when the enumeration
    // landed. Assigning ItemsSource under a live popup destroys the item containers it is
    // rendering, and WinUI closes the dropdown — which is exactly what refresh-on-open did (the
    // list "flashed" and shut). The ENH-7 rule (ONE swap, never Clear+Add) was never the problem
    // and is unchanged; only the TIMING moved. The generation fence is re-checked at APPLY time,
    // not just at capture time, because a user selection made while the popup was open both
    // closes the dropdown and supersedes this snapshot.
    private (int Gen, IReadOnlyList<MicComboItem> Items, int SelectedIndex, string? ResolvedLabel)? _micPendingSnapshot;

    /// <summary>AUD-1: the Microphone dropdown — "System default" first (the default), then the
    /// active capture devices, plus a synthetic "(unavailable)" entry for a pinned-but-absent
    /// device. Items load ASYNC (enumeration can block behind a wedged audio service — never on
    /// the UI thread) and refresh on every dropdown open, single ItemsSource swap per refresh
    /// (the ENH-7 live-combo rule: never Clear+Add).</summary>
    private FrameworkElement BuildMicrophoneRow()
    {
        var titleBlock = new TextBlock
        {
            Text = "Microphone",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary)
        };
        var descBlock = new TextBlock
        {
            Text = "Which microphone VoiceWink records from. If the selected one is unavailable, the system default is used for that recording.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        // AUD-17: starts collapsed and stays collapsed until an enumeration actually resolves
        // something. The immediate populate below has no device snapshot, so an always-visible
        // block would flash empty (or worse, a placeholder) on every Settings open.
        //
        // Deliberately a LOCAL passed by parameter, never a page field: `BuildUI()` runs again on
        // Reset All, and a field would let a refresh started against the OLD row write its result
        // into the REPLACEMENT row (Codex diff review). Identity by parameter is how `combo`
        // already survives the same rebuild — one idiom, and the hazard becomes unrepresentable
        // rather than fenced.
        var resolvedBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var combo = new ComboBox
        {
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AppTheme.AllowParentScroll(combo);

        combo.SelectionChanged += (_, _) =>
        {
            if (_micComboSuppress) return;
            if (combo.SelectedItem is MicComboItem item)
            {
                _micSelectionGen++; // supersedes any in-flight refresh's stale snapshot
                _viewModel.SetMicrophoneSelection(item);
                // AUD-17: RETIRE the label first, then recompute. The label is a function of
                // (devices, pin) and the pin just changed, so whatever is on screen now describes
                // the PREVIOUS selection — leaving it up while the async refresh runs shows
                // "Currently: Headset" beside a combo reading "Rode NT-USB", and if the audio
                // service is wedged that contradiction never resolves (Codex diff review). Hiding
                // is never wrong: the worst case is a brief absence, against a persistent lie.
                ApplyResolvedMicrophoneLabel(resolvedBlock, null);
                _ = RefreshMicrophoneComboAsync(combo, resolvedBlock);
            }
        };
        // IMMEDIATE populate from settings alone — "System default" (+ the pinned entry) must
        // show even if the async enumeration below wedges behind a stuck audio service.
        var (immediateItems, immediateSelected, immediateLabel) = _viewModel.BuildMicrophoneListImmediate();
        ApplyResolvedMicrophoneLabel(resolvedBlock, immediateLabel);
        _micComboSuppress = true;
        try
        {
            combo.ItemsSource = immediateItems;
            combo.SelectedIndex = immediateSelected;
        }
        finally
        {
            _micComboSuppress = false;
        }

        combo.DropDownOpened += async (_, _) => await RefreshMicrophoneComboAsync(combo, resolvedBlock);
        // AUD-7: apply whatever the open-triggered enumeration produced, now that the popup is
        // gone. This is also what keeps refresh-on-open MEANINGFUL after the deferral: without
        // it a snapshot taken while open would be dropped, and a device plugged in mid-session
        // would never appear at all.
        combo.DropDownClosed += (_, _) => ApplyPendingMicrophoneSnapshot(combo, resolvedBlock);
        Unloaded += (_, _) => _pageUnloaded = true;
        _ = RefreshMicrophoneComboAsync(combo, resolvedBlock);

        // STACKED, not the label-left/control-right shape the sibling toggle rows use, and the
        // difference is the control: a ToggleSwitch is a fixed width, so it never competes with
        // the description for the row. This combo's width is its longest DEVICE NAME, which is
        // unbounded — Windows friendly names run long, AUD-1 appends a disambiguating suffix to
        // duplicates, and a pinned-but-absent device carries "(unavailable)" on top. In the
        // previous Grid (* description column, Auto combo column) the combo took what it wanted
        // and the description got the remainder: "Microphone Array (Intel® Smart Sound Technology
        // for Digital Microphones)" left it a ~96 px ribbon eleven lines tall (owner, 2026-08-16).
        // Capping the combo instead would truncate the device name — the one string the user is
        // reading to make the choice, and the one that distinguishes two similar mics.
        //
        // This is also the placement AUD-17 already describes ("the line under the Settings
        // Microphone dropdown", "the control above it"): the resolved-device label follows the
        // combo it resolves, which the two-column layout could not express.
        //
        // Inset 0 to match the sibling toggle rows, which StripCardBorder normalizes to
        // Padding(0,8,0,8) + Margin(0). This row carried Margin(4,4,4,4), so its title sat 4 px
        // right of "Sound feedback" with a tighter vertical rhythm (owner UAT 2026-08-03).
        // The card's unmute-delay label/slider keep their own 4 px inset deliberately: that is
        // the shared label+slider pattern, used identically in a second card.
        return new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 8),
            Children = { titleBlock, descBlock, combo, resolvedBlock }
        };
    }

    private async Task RefreshMicrophoneComboAsync(ComboBox combo, TextBlock resolvedBlock)
    {
        var gen = _micSelectionGen; // snapshot BEFORE the enumeration reads settings
        try
        {
            var (items, selectedIndex, resolvedLabel) = await _viewModel.BuildMicrophoneListAsync();
            if (_pageUnloaded) return;            // resumed after navigation away — stale UI
            if (gen != _micSelectionGen) return;  // user picked meanwhile — this snapshot is stale

            // AUD-7: never swap under a live popup — it closes the dropdown. Park it instead;
            // DropDownClosed applies it. If the user already closed the dropdown (or this is the
            // build-time refresh), IsDropDownOpen is false and the swap lands immediately, which
            // is the pre-AUD-7 path unchanged.
            if (combo.IsDropDownOpen)
            {
                _micPendingSnapshot = (gen, items, selectedIndex, resolvedLabel);
                return;
            }

            ApplyMicrophoneSnapshot(combo, resolvedBlock, items, selectedIndex, resolvedLabel);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Microphone list refresh failed");
        }
    }

    /// <summary>AUD-7: the deferred half of <see cref="RefreshMicrophoneComboAsync"/>. Re-checks
    /// BOTH fences at apply time — the page may have unloaded while the popup was open, and a
    /// selection made from the open dropdown bumps the generation, which must win over the
    /// snapshot that was captured before it.</summary>
    private void ApplyPendingMicrophoneSnapshot(ComboBox combo, TextBlock resolvedBlock)
    {
        if (_micPendingSnapshot is not { } pending) return;
        _micPendingSnapshot = null;
        if (_pageUnloaded || pending.Gen != _micSelectionGen) return;
        ApplyMicrophoneSnapshot(combo, resolvedBlock, pending.Items, pending.SelectedIndex, pending.ResolvedLabel);
    }

    /// <summary>AUD-17: render the resolved-device line on the block the CALLER owns, or hide it.
    /// The block arrives by parameter rather than from a field so a refresh started against a row
    /// that `BuildUI()` has since replaced writes into its own detached block instead of the live
    /// one. Hidden rather than blanked so the row keeps no reserved empty gap — the label is
    /// absent in the common case (a present device is pinned, so the combo already names it) and a
    /// permanently empty line reads as a rendering fault.</summary>
    private static void ApplyResolvedMicrophoneLabel(TextBlock block, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            block.Text = string.Empty;
            block.Visibility = Visibility.Collapsed;
            return;
        }

        block.Text = label;
        block.Visibility = Visibility.Visible;
    }

    /// <summary>The ONE ItemsSource swap for the Microphone combo (ENH-7: a single snapshot
    /// assignment, never Clear+Add). Callers guarantee the popup is closed.
    ///
    /// <para><b>Identity beats index.</b> The incoming <paramref name="selectedIndex"/> reflects the
    /// persisted pin as it stood when the enumeration ran, which a DEFERRED apply can outlive — the
    /// user may have picked from the very dropdown whose close triggered it. The generation fence
    /// catches that only if <c>SelectionChanged</c> has already fired, and the
    /// SelectionChanged-before-DropDownClosed order is a WinUI contract we depend on rather than
    /// control (Kimi diff review). So if the item the combo is CURRENTLY showing still exists in the
    /// new snapshot, it is re-selected by endpoint id and the index is ignored. That makes the
    /// result independent of event order instead of merely correct under today's order.</para>
    ///
    /// <para>Matching is on <see cref="MicComboItem.Id"/> — the WASAPI endpoint id, typed identity,
    /// with null meaning "System default" — never on the display string, which carries
    /// disambiguating suffixes and the "(unavailable)" annotation. That also makes the pinned
    /// device COMING BACK do the right thing: the synthetic unavailable entry and the real device
    /// share an id, so the real one is selected.</para></summary>
    private void ApplyMicrophoneSnapshot(
        ComboBox combo, TextBlock resolvedBlock,
        IReadOnlyList<MicComboItem> items, int selectedIndex, string? resolvedLabel)
    {
        var current = combo.SelectedItem as MicComboItem;
        _micComboSuppress = true;
        try
        {
            combo.ItemsSource = items; // ONE snapshot swap (ENH-7)
            var preserved = current is null ? -1 : IndexOfDevice(items, current.Id);
            combo.SelectedIndex = preserved >= 0 ? preserved : selectedIndex;
        }
        finally
        {
            _micComboSuppress = false;
        }

        // AUD-17: from the SAME snapshot as the items above, so the label can never describe a
        // different enumeration than the dropdown beside it. Outside the suppress scope because
        // it touches no combo state — and if the swap threw, this is skipped and the caller's
        // catch logs it, which is the right order: a stale label matters less than a failed swap.
        ApplyResolvedMicrophoneLabel(resolvedBlock, resolvedLabel);
    }

    /// <summary>Position of a device in the list by endpoint id, or -1. Null matches null, which is
    /// how the "System default" entry finds itself across a refresh.</summary>
    private static int IndexOfDevice(IReadOnlyList<MicComboItem> items, string? id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Recorder section — MiniRecorder pill drag hint + position reset (PILL-4).</summary>
    private Border BuildRecorderCard()
    {
        var settings = App.Services.GetRequiredService<Services.System.SettingsService>();

        // Title row: section header left, Reset Position right-aligned on the SAME line, both
        // vertically centered so the button's box lines up with the header's text (owner
        // 2026-07-31). The row owns the 12px bottom margin the header used to carry — leaving it
        // on the header would offset the button within the row.
        var sectionHeader = AppTheme.CreateSectionHeader("Mini recorder");
        sectionHeader.Margin = new Thickness(0);
        sectionHeader.VerticalAlignment = VerticalAlignment.Center;

        var resetButton = AppTheme.CreateSecondaryButton("Reset position", (_, _) =>
        {
            settings.Remove(AppDefaults.MiniRecorderPosFractionX);
            settings.Remove(AppDefaults.MiniRecorderPosFractionY);
        });
        resetButton.HorizontalAlignment = HorizontalAlignment.Right;
        resetButton.VerticalAlignment = VerticalAlignment.Center;
        resetButton.Margin = new Thickness(12, 0, 0, 0); // never let a long header crowd the button

        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sectionHeader, 0);
        Grid.SetColumn(resetButton, 1);
        headerRow.Children.Add(sectionHeader);
        headerRow.Children.Add(resetButton);

        var label = new TextBlock
        {
            Text = "Drag the pill to reposition it (remembered per screen). Reset returns it to the top center.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap
        };

        // Capture exclusion (2026-07-30). Deliberately not an absolute promise: Windows limits
        // WDA_EXCLUDEFROMCAPTURE to supported public capture mechanisms and DWM composition.
        var hideFromCapture = AppTheme.CreateToggleSetting(
            "Hide pill from screenshots",
            "Most screenshot, screen-recording and screen-share tools won't capture the pill. Applies the next time it appears.",
            settings.GetBool(AppDefaults.HidePillFromCapture, true),
            v => settings.SetBool(AppDefaults.HidePillFromCapture, v));
        // A setting INSIDE a card is never itself a card — same rule every other in-card toggle
        // follows (Audio, Text Processing, Clipboard). Missing here, it rendered as a nested box.
        StripCardBorder(hideFromCapture);

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { headerRow, label, hideFromCapture }
        };

        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Text processing — formatting, trailing space, and filler-word removal WITH its word list.
    ///
    /// <para>The list was its own "Filler Words" card until the 2026-08-04 grouping pass. One topic
    /// in two cards meant the <i>Remove filler words</i> toggle sat in one card and the list it
    /// governs in the next. Merging them is not the mistake being fixed in the old Data Management
    /// card: that was three separate TOPICS behind one header, this is one topic reunited. Total page
    /// height is unchanged — the list was already the very next card — minus one border and header.
    /// Ordering inside the card puts the toggle LAST so the list it controls sits directly beneath
    /// it.</para>
    /// </summary>
    private Border BuildTextProcessingCard()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Text processing");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var textFormatting = AppTheme.CreateToggleSetting(
            "Text formatting",
            "Add paragraph breaks to long transcriptions",
            _viewModel.IsTextFormattingEnabled,
            v => _viewModel.IsTextFormattingEnabled = v);
        StripCardBorder(textFormatting);

        var trailingSpace = AppTheme.CreateToggleSetting(
            "Trailing space",
            "Append a space after pasted text",
            _viewModel.AppendTrailingSpace,
            v => _viewModel.AppendTrailingSpace = v);
        StripCardBorder(trailingSpace);

        var removeFillers = AppTheme.CreateToggleSetting(
            "Remove filler words",
            "Remove uh, um, like, etc.",
            _viewModel.RemoveFillerWords,
            v => _viewModel.RemoveFillerWords = v);
        StripCardBorder(removeFillers);

        var fillerLabel = new TextBlock
        {
            Text = "Comma-separated list of words to remove from transcriptions",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var fillerBox = new TextBox
        {
            Text = _viewModel.FillerWords,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            MinHeight = 80,
            PlaceholderText = "uh, um, like, you know..."
        };
        fillerBox.TextChanged += (s, e) => _viewModel.FillerWords = fillerBox.Text;

        // Reset repopulates the box from the ViewModel — the command mutates the property, and the
        // TextBox is not bound, so without this assignment the box would keep the old text.
        var resetFillersButton = AppTheme.CreateSecondaryButton("Reset to defaults", (_, _) =>
        {
            _viewModel.ResetFillerWordsCommand.Execute(null);
            fillerBox.Text = _viewModel.FillerWords;
        });
        resetFillersButton.HorizontalAlignment = HorizontalAlignment.Left;
        resetFillersButton.Margin = new Thickness(0, 8, 0, 0);

        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                sectionHeader, textFormatting, trailingSpace, removeFillers,
                fillerLabel, fillerBox, resetFillersButton,
            }
        };

        return AppTheme.CreateCard(content);
    }

    /// <summary>Clipboard section — restore clipboard toggle.</summary>
    private Border BuildClipboardCard()
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Clipboard");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var restoreClipboard = AppTheme.CreateToggleSetting(
            "Restore clipboard",
            "Restore previous clipboard content after text paste (images stay on clipboard for re-use)",
            _viewModel.RestoreClipboardAfterPaste,
            v => _viewModel.RestoreClipboardAfterPaste = v);
        StripCardBorder(restoreClipboard);

        var sendEnter = AppTheme.CreateToggleSetting(
            "Send Enter after paste",
            "Press Enter after pasting in push-to-talk mode — in chat apps this sends the message immediately.",
            _viewModel.SendEnterAfterPaste,
            v => _viewModel.SendEnterAfterPaste = v);
        StripCardBorder(sendEnter);

        // Clipboard restore delay slider
        var restoreDelayLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Text = $"Clipboard restore delay: {_viewModel.ClipboardRestoreDelay:F1}s",
            Margin = new Thickness(4, 8, 0, 0)
        };
        var restoreDelaySlider = new Slider
        {
            Minimum = 0.5,
            Maximum = 5,
            StepFrequency = 0.5,
            Value = _viewModel.ClipboardRestoreDelay,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 0, 4, 0)
        };
        restoreDelaySlider.ValueChanged += (_, e) =>
        {
            _viewModel.ClipboardRestoreDelay = Math.Round(e.NewValue, 1);
            restoreDelayLabel.Text = $"Clipboard restore delay: {_viewModel.ClipboardRestoreDelay:F1}s";
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { sectionHeader, restoreClipboard, restoreDelayLabel, restoreDelaySlider, sendEnter }
        };

        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// History cleanup — the auto-delete toggle and its retention period.
    ///
    /// <para>Split out of the old "Data Management" card in the 2026-08-04 grouping pass. That card
    /// held THREE topics behind two internal sub-headers ("Your data", "Settings backup"), which was
    /// the page asking to be three cards. Named "History cleanup" rather than "History" deliberately:
    /// "Settings → History" would collide with the History sidebar page (Kimi plan review).</para>
    /// </summary>
    private Border BuildHistoryCleanupCard()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();

        var sectionHeader = AppTheme.CreateSectionHeader("History cleanup");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var cleanupToggle = AppTheme.CreateToggleSetting(
            "Auto-delete old transcriptions",
            "Automatically remove transcription history older than the retention period",
            settings.GetBoolDefaulted(AppDefaults.IsTranscriptionCleanupEnabled),
            v => settings.SetBool(AppDefaults.IsTranscriptionCleanupEnabled, v));
        StripCardBorder(cleanupToggle);

        var retentionOptions = new[] { "1 hour", "6 hours", "24 hours", "7 days", "30 days" };
        var retentionValues = new[] { 60, 360, 1440, 10080, 43200 };
        var currentMinutes = settings.GetInt(AppDefaults.TranscriptionRetentionMinutes, 10080);
        var currentIndex = Array.IndexOf(retentionValues, currentMinutes);
        var currentLabel = currentIndex >= 0 ? retentionOptions[currentIndex] : "7 days";

        var retentionCombo = AppTheme.CreateComboSetting(
            "Retention period",
            retentionOptions,
            currentLabel,
            v =>
            {
                var idx = Array.IndexOf(retentionOptions, v);
                if (idx >= 0)
                    settings.SetInt(AppDefaults.TranscriptionRetentionMinutes, retentionValues[idx]);
            });
        StripCardBorder(retentionCombo);

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { sectionHeader, cleanupToggle, retentionCombo },
        };
        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Your data (GDPR Art. 15 / 17 / 20) — export, delete history, delete everything, plus the
    /// images-folder shortcut.
    ///
    /// <para>The <c>includeMediaCheck</c> + <c>includeMediaLabel.Tapped</c> + <c>exportButton</c>
    /// trio is COUPLED and moves as a unit: the export lambda reads the checkbox's live state at
    /// click time, and the label's Tapped handler is what makes the text tap-to-toggle. "Images
    /// folder" stays in this card's button row (it opens user data, and its position there is a
    /// recorded owner ordering from 2026-07-31 — export leads, then the folder shortcut, then the
    /// two destructive actions). Its label matches the History page's button for the same action
    /// (owner 2026-08-05 — one action, one name).</para>
    /// </summary>
    private Border BuildYourDataCard()
    {
        var openImagesButton = AppTheme.CreateSecondaryButton("Images folder",
            (_, _) => ShellFolder.Open(AppPaths.ImagesDir));

        var privacyHeader = AppTheme.CreateSectionHeader("Your data");
        privacyHeader.Margin = new Thickness(0, 0, 0, 4);

        var privacyBlurb = new TextBlock
        {
            Text = "Export a copy of your data, or permanently delete it from this device.",
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        // Checkbox + label as sibling elements, both vertically centered, so the box lines up with
        // the text. The CheckBox's own box-vs-content template layout doesn't center reliably, so
        // the label lives in a separate TextBlock (tap-to-toggle preserved).
        var includeMediaCheck = new CheckBox
        {
            IsChecked = false,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var includeMediaLabel = new TextBlock
        {
            Text = "Include recordings, images, problem reports and logs",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        includeMediaLabel.Tapped += (_, _) => includeMediaCheck.IsChecked = !(includeMediaCheck.IsChecked ?? false);
        var includeMediaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 4),
            Children = { includeMediaCheck, includeMediaLabel },
        };

        var exportButton = AppTheme.CreateSecondaryButton("Export data",
            async (_, _) => await ExportDataAsync(includeMediaCheck.IsChecked == true));

        var deleteHistoryButton = AppTheme.CreateSecondaryButton("Delete history",
            async (_, _) => await ShowDeleteHistoryDialogAsync());

        var deleteAllButton = AppTheme.CreateSecondaryButton("Delete all data",
            async (_, _) => await ShowDeleteAllDialogAsync());

        // The images-folder shortcut + the three data buttons flow in a row, wrapping to the next
        // line when the card is too narrow to fit them all (FlowPanel — no built-in WrapPanel).
        var dataButtons = new FlowPanel
        {
            HorizontalSpacing = 8,
            VerticalSpacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
            // Export leads (owner 2026-07-31 — the primary data action), then the images
            // folder shortcut, then the two destructive actions.
            Children = { exportButton, openImagesButton, deleteHistoryButton, deleteAllButton },
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { privacyHeader, privacyBlurb, includeMediaRow, dataButtons },
        };
        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Settings backup — preferences only, deliberately a SEPARATE card from "Your data" above.
    /// The distinction is the point: that one exports your dictation content under GDPR, this one
    /// exports preferences and excludes secrets and device-specific keys. They were adjacent
    /// sub-headers inside one card before the 2026-08-04 grouping pass, which understated it.
    /// </summary>
    private Border BuildSettingsBackupCard()
    {
        var settingsBackupHeader = AppTheme.CreateSectionHeader("Settings backup");
        settingsBackupHeader.Margin = new Thickness(0, 0, 0, 4);
        var settingsBackupBlurb = new TextBlock
        {
            Text = "Save your preferences to a file, or restore them from one. Secrets and " +
                   "device-specific settings aren't included.",
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var exportSettingsButton = AppTheme.CreateSecondaryButton("Export settings",
            async (_, _) => await ExportSettingsAsync());
        var importSettingsButton = AppTheme.CreateSecondaryButton("Import settings",
            async (_, _) => await ImportSettingsAsync());
        var settingsBackupButtons = new FlowPanel
        {
            HorizontalSpacing = 8,
            VerticalSpacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { exportSettingsButton, importSettingsButton },
        };

        var content = new StackPanel
        {
            Spacing = 4,
            Children = { settingsBackupHeader, settingsBackupBlurb, settingsBackupButtons },
        };

        return AppTheme.CreateCard(content);
    }

    /// <summary>Run the GDPR export to a user-chosen ZIP via FileSavePicker (hwnd-initialized for unpackaged WinUI 3).</summary>
    private async Task ExportDataAsync(bool includeMedia)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = $"voicewink-export-{DateTime.Now:yyyy-MM-dd}",
            };
            picker.FileTypeChoices.Add("Zip archive", new List<string> { ".zip" });

            if (App.MainWindow is null)
            {
                Logger.Warning("Export aborted: no main window for picker hwnd");
                return;
            }
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return; // user cancelled

            var svc = App.Services.GetRequiredService<GdprExportService>();
            var result = await svc.ExportAsync(file.Path, includeMedia);
            if (result.SkippedFileCount > 0)
            {
                await ShowInfoAsync("Export complete (with omissions)",
                    $"Your data was exported to:\n{file.Path}\n\n" +
                    $"{result.SkippedFileCount} file(s) could not be included (in use by another " +
                    "app, unreadable, or damaged). If a file was merely in use, closing other " +
                    "apps and exporting again may capture it.");
            }
            else
            {
                await ShowInfoAsync("Export complete", $"Your data was exported to:\n{file.Path}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "GDPR export failed");
            await ShowInfoAsync("Export failed", "Sorry, the export couldn't be completed.\n\n" + ex.Message);
        }
    }

    /// <summary>Export re-importable preferences (ImportExportService JSON — sensitive keys stripped) via FileSavePicker.</summary>
    private async Task ExportSettingsAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = $"voicewink-settings-{DateTime.Now:yyyy-MM-dd}",
            };
            picker.FileTypeChoices.Add("Settings file", new List<string> { ".json" });

            if (App.MainWindow is null)
            {
                Logger.Warning("Export settings aborted: no main window for picker hwnd");
                return;
            }
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return; // user cancelled

            await App.Services.GetRequiredService<ImportExportService>().ExportAsync(file.Path);
            await ShowInfoAsync("Settings exported", $"Your preferences were exported to:\n{file.Path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Settings export failed");
            await ShowInfoAsync("Export failed", "Sorry, the settings export couldn't be completed.\n\n" + ex.Message);
        }
    }

    /// <summary>Import preferences from an ImportExportService JSON via FileOpenPicker (sensitive keys rejected, device-local keys skipped).</summary>
    private async Task ImportSettingsAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add(".json");

            if (App.MainWindow is null)
            {
                Logger.Warning("Import settings aborted: no main window for picker hwnd");
                return;
            }
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return; // user cancelled

            var result = await App.Services.GetRequiredService<ImportExportService>().ImportAsync(file.Path);

            var msg = $"{result.AppliedCount} setting(s) applied.";
            msg += "\n\nRestart VoiceWink for all changes to take effect.";
            await ShowInfoAsync("Settings imported", msg);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Settings import failed");
            await ShowInfoAsync("Import failed", "Sorry, the settings couldn't be imported.\n\n" + ex.Message);
        }
    }

    /// <summary>Confirm + delete transcription history (text + image rows and their media files).</summary>
    private async Task ShowDeleteHistoryDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Delete transcription history",
            // Copy review 2026-07-25 (owner scratchpad + two Codex rounds). The old text was wrong
            // twice: generated images go to the RECYCLE BIN (RecycleImageFile, permanent delete only
            // as a fallback), and it never mentioned that VoiceWink's saved reference-image COPIES go
            // too — which is what the owner asked about. It must also not imply the user's ORIGINAL
            // files are touched (TryDeleteIfUnreferencedAsync only ever removes app-owned copies),
            // and "recordings" is scoped to those belonging to deleted entries — an armed Retry's
            // WAV and orphaned recordings are not part of this.
            // Third pass: media cleanup is BEST-EFFORT (containment guards can skip a path, and a
            // failed remaining-reference query deliberately retains every copy for the startup
            // sweep), so the copy must say "tries to clean up" rather than promise deletion — and it
            // must disclose that the Recycle Bin has a permanent-delete fallback.
            Content = "This deletes every text and image entry from VoiceWink's history. VoiceWink also " +
                      "tries to clean up the recordings and saved reference-image copies belonging to " +
                      "those entries. Generated images are normally sent to the Recycle Bin; if Windows " +
                      "can't do that, they may be deleted permanently.\n\n" +
                      "Your original image files, settings, license and preferences are kept.",
            PrimaryButtonText = "Delete history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await App.Services.GetRequiredService<TranscriptionHistoryService>().DeleteAllAsync();
            await ShowInfoAsync("History deleted", "Your transcription history has been deleted.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Delete history failed");
            await ShowInfoAsync("Delete failed", "Sorry, history couldn't be deleted.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Destructive "delete everything" confirm. A checkbox gates the primary button (a deliberate
    /// friction step preventing an accidental click). On success the app exits via
    /// <see cref="DataErasureService"/>; a partial failure keeps the dialog open with guidance.
    /// </summary>
    private async Task ShowDeleteAllDialogAsync()
    {
        var status = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // Hold-to-confirm target. A Border (not a Button) is used so pointer press/release events
        // aren't swallowed by Button's own click handling. Holding it for 3s enables the dialog's
        // destructive primary button; releasing / leaving / closing before then resets it.
        var holdLabel = new TextBlock
        {
            Text = "Press and hold to confirm (3s)",
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var holdBorder = new Border
        {
            Child = holdLabel,
            Background = AppTheme.Brush(AppTheme.ContentBg),
            BorderBrush = AppTheme.Brush(AppTheme.AccentAmber),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var dialog = new ContentDialog
        {
            Title = "Delete all my data",
            PrimaryButtonText = "Delete everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = false,
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This permanently deletes ALL of your VoiceWink data on this device: settings, " +
                               "API keys, license activation, transcription history, recordings, generated images, " +
                               "downloaded models, and logs. VoiceWink will close.",
                        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    holdBorder,
                    status,
                },
            },
        };

        // Dialog-owned hold timer: 100ms ticks, 30 ticks = 3s. Enables the primary button on
        // completion (per the plan); reset on release / pointer-exit / dialog close.
        DispatcherTimer? holdTimer = null;
        var ticks = 0;
        var confirmed = false;

        void StopHold()
        {
            holdTimer?.Stop();
            holdTimer = null;
            ticks = 0;
            if (!confirmed) holdLabel.Text = "Press and hold to confirm (3s)";
        }

        holdBorder.PointerPressed += (_, _) =>
        {
            if (confirmed) return;
            ticks = 0;
            holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            holdTimer.Tick += (_, _) =>
            {
                ticks++;
                var remaining = Math.Max(0, (int)Math.Ceiling((30 - ticks) / 10.0));
                holdLabel.Text = $"Keep holding... {remaining}s";
                if (ticks >= 30)
                {
                    confirmed = true;
                    StopHold();
                    holdLabel.Text = "Confirmed - click \"Delete everything\"";
                    dialog.IsPrimaryButtonEnabled = true;
                }
            };
            holdTimer.Start();
        };
        holdBorder.PointerReleased += (_, _) => { if (!confirmed) StopHold(); };
        holdBorder.PointerExited += (_, _) => { if (!confirmed) StopHold(); };
        dialog.Closed += (_, _) => StopHold();

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // Keep the dialog open while we delete; only a partial failure surfaces back here
            // (on success the erasure service exits the process).
            var deferral = args.GetDeferral();
            args.Cancel = true;
            try
            {
                var result = await App.Services.GetRequiredService<DataErasureService>().DeleteAllAsync();
                status.Text = result.Message;
                status.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Delete-all failed");
                status.Text = "Sorry, deletion couldn't be completed: " + ex.Message;
                status.Visibility = Visibility.Visible;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    /// <summary>Minimal informational dialog (single close button).</summary>
    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
            RequestedTheme = AppTheme.ElementTheme,
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// General section — theme plus the three window/startup behaviours. Created 2026-08-04 in the
    /// grouping pass: these were four ungrouped cards at the head of the page, which is why the
    /// page opened with no orientation. Crash reporting deliberately does NOT live here — it is a
    /// privacy consent and moved to Diagnostics, and GPU acceleration moved to the Models page in
    /// UI-11 — it is an engine setting, and the speed stars it changes are rendered there.
    /// </summary>
    /// <param name="settingsSvc">Resolved once by <c>BuildUI</c> and passed in — AGENTS.md forbids
    /// adding new <c>App.Services</c> locator lookups, and this method is new (Codex diff review).
    /// The thirteen pre-existing lookups in this file are left alone; the rule is about not adding
    /// more.</param>
    private Border BuildGeneralCard(SettingsService settingsSvc)
    {
        var sectionHeader = AppTheme.CreateSectionHeader("General");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var themeCombo = AppTheme.CreateComboSetting("Theme",
            ["Dark", "Light", "System"],
            _viewModel.ThemeMode,
            v => _viewModel.ThemeMode = v);
        StripCardBorder(themeCombo);

        var launchAtLogin = AppTheme.CreateToggleSetting(
            "Launch at login",
            "Start VoiceWink automatically when you sign in to Windows",
            _viewModel.LaunchAtLogin,
            v => _viewModel.LaunchAtLogin = v);
        StripCardBorder(launchAtLogin);

        // UI-11: the GPU acceleration row moved to the Models page, directly above the local model
        // rows whose speed stars it governs. Its whole operation lives in GpuAccelerationPreference,
        // which "Reset all settings" also calls — this VM keeps no cached copy of the key.

        var startMinimized = AppTheme.CreateToggleSetting(
            "Start minimized",
            "Start VoiceWink minimized to the system tray instead of showing the main window",
            settingsSvc.GetBool(AppDefaults.StartMinimized, true),
            v => settingsSvc.SetBool(AppDefaults.StartMinimized, v));
        StripCardBorder(startMinimized);

        var minimizeToTray = AppTheme.CreateToggleSetting(
            "Minimize to tray",
            "When you minimize the window, hide it to the system tray instead of the taskbar",
            settingsSvc.GetBool(AppDefaults.MinimizeToTray, true),
            v => settingsSvc.SetBool(AppDefaults.MinimizeToTray, v));
        StripCardBorder(minimizeToTray);

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(sectionHeader);
        content.Children.Add(themeCombo);
        content.Children.Add(launchAtLogin);
        content.Children.Add(startMinimized);
        content.Children.Add(minimizeToTray);
        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Diagnostics section — the two REL-17 consent toggles (prompt/output trace with its REL-21
    /// image sub-option, and debug audio retention).
    ///
    /// <para><b>Moved here from the Log Viewer page on 2026-08-04 (owner).</b> REL-17 originally put
    /// them there on the reasoning that "the person staring at logs is exactly the person about to
    /// report them" — overridden because four toggle cards plus wrapping descriptions squeezed that
    /// page's actual log pane down to a sliver. Settings is where toggles live; the Log Viewer keeps
    /// only the two that change how the log VIEW behaves.</para>
    ///
    /// <para>The consent apply rule stays <c>DiagnosticsConsent.Apply</c> (a failed persist always
    /// ends OFF in memory — a failed opt-out must never resume collection, a failed opt-in must
    /// never stay active). This page owns the VISIBLE half: revert the actual switch and show an
    /// error line, so a control never displays a state the app is not in. The latch stops the
    /// programmatic revert from re-entering the Toggled callback.</para>
    /// </summary>
    /// <param name="settings">Resolved once by <c>BuildUI</c> and passed in — see
    /// <see cref="BuildGeneralCard"/>.</param>
    private Border BuildDiagnosticsCard(SettingsService settings)
    {
        var sectionHeader = AppTheme.CreateSectionHeader("Diagnostics");
        sectionHeader.Margin = new Thickness(0, 0, 0, 12);

        var consentError = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var consentReverting = false;
        void ApplyDiagnosticsConsent(string key, bool requested, ToggleSwitch toggle)
        {
            if (consentReverting) return;
            ShowConsentResult(Helpers.DiagnosticsConsent.Apply(settings, key, requested), key, toggle);
        }
        // The VISIBLE half of every consent apply on this card (the three DiagnosticsConsent rows
        // and the crash-reporting row, whose apply also drives the SDK and the log sink): revert
        // the switch and show the error line when the write did not land.
        void ShowConsentResult(Helpers.DiagnosticsConsent.ConsentApplyResult result, string key, ToggleSwitch toggle)
        {
            if (result.Persisted)
            {
                consentError.Visibility = Visibility.Collapsed;
                return;
            }
            Serilog.Log.Warning("Failed to persist diagnostics toggle {Key}; forced off", key);
            if (toggle.IsOn != result.Effective)
            {
                consentReverting = true;
                try { toggle.IsOn = result.Effective; }
                finally { consentReverting = false; }
            }
            // Honest copy: the on-disk value could not be updated, so a previously-saved ON may
            // come back at next launch — say so instead of promising "stays off".
            consentError.Text = "This setting couldn't be saved. It's off for now, but it may " +
                "turn back on when VoiceWink restarts — free up disk space and toggle it again.";
            consentError.Visibility = Visibility.Visible;
        }

        // REL-21's image option is a PEER ROW, not a child (owner, 2026-08-04). It shipped three
        // ways before this: a separate card inset 24 px, an indented row with a left hairline, and
        // a flush row with a divider — the indent was rejected each time and the divider with it.
        // So: an ordinary sibling row, renamed to read as one ("Log image generation prompts"
        // parallels "Log prompts and outputs"; "Include…" only made sense while it was a child).
        //
        // CONSTRUCTION ORDER IS LOAD-BEARING (Kimi plan review): the image row must exist before
        // the parent, because the parent's lambda captures its switch. Built parent-first this
        // compiles and then throws on the first toggle.
        ToggleSwitch includeImagePromptsSwitch = null!;
        var includeImagePromptsToggle = AppTheme.CreateToggleSetting(
            "Log image generation prompts",
            // RAW-only since 2026-09-13: the redacted summary line for an image generation is
            // always kept (op, provider, model, size options, the description's length), so this
            // row must say what it alone controls — the prompt's TEXT in prompts-*.log — or a user
            // who leaves it off is told less than the file holds (Codex plan round Blocker).
            "Writes image prompts, in full, to prompts-*.log. A redacted summary (text replaced by its length) is always kept for 7 days.",
            settings.GetBool(AppDefaults.PromptTraceIncludeImagePrompts, false),
            v => ApplyDiagnosticsConsent(AppDefaults.PromptTraceIncludeImagePrompts, v, includeImagePromptsSwitch),
            out includeImagePromptsSwitch);
        StripCardBorder(includeImagePromptsToggle);
        // Still greys out while the parent is off — that is EXISTING behaviour and this change is
        // layout-only. A peer that disables with its neighbour is unusual, but the alternative
        // (always enabled) would let the user set a value that has no effect, and removing the gate
        // is a functional change, not a UI one.
        includeImagePromptsSwitch.IsEnabled = settings.GetBool(AppDefaults.PromptTraceLoggingOptIn, false);

        ToggleSwitch promptTraceSwitch = null!;
        var promptTraceToggle = AppTheme.CreateToggleSetting(
            "Log prompts and outputs",
            // REL-34: this line is the WHOLE consent surface — ApplyDiagnosticsConsent shows no
            // dialog, so it is the only place a user is told what turning this on records. It said
            // "prompts and responses", which reads as an LLM exchange and never says that the words
            // the recogniser produced are written to a file. They are: PromptTraceOp carries
            // TranscriptionOutput, FileTranscriptionOutput and EnhancementOutput, so that text lands
            // here whether or not any AI enhancement ran. Name it.
            //
            // "from dictation and files", not "your speech": FileTranscriptionOutput is written by
            // the Transcribe File page, where the audio is a file the user CHOSE and the words in it
            // may be someone else's — an interview, a meeting recording. A list like this reads as
            // exhaustive, so naming only the user's own speech would be a narrower consent statement
            // than the vague wording it replaces (self-review pass).
            //
            // THE LIST IS COMPLETE, and that is the fix for its exhaustive reading (Codex diff r1
            // Blocker). Two more user-authored things ride the raw file and the first wording named
            // neither:
            //   - the APP MODE NAME, written verbatim under PromptTraceOp.LanguageResolution
            //     (MainViewModel's preflight; the name is free text the user typed into the App
            //     Modes dialog's Name box, so it can be anything).
            //   - the DICTIONARY AND TRIGGER WORDS, which ride the traced query string of the
            //     Deepgram / ElevenLabs / OpenAI transcription entries. privacy-v5.md §7 names both
            //     and the report dialog's own advisory names the Dictionary half, so omitting them
            //     here made this surface NARROWER than the shipped policy — the fail-open direction
            //     for a consent line.
            // Image prompts are deliberately still absent: they need the SIBLING toggle as well
            // (PromptTraceLog.ShouldWriteRaw gates image ops in the raw file; the sidecar is always
            // kept), and that row states its own scope. So this line must not make an unqualified "everything you send" claim either —
            // it enumerates exactly what THIS toggle alone authorises.
            //
            // The LABEL is untouched on purpose — ReportOptionAvailability quotes it back to the user
            // ("\"Log prompts and outputs\" controls whether they are recorded"), pinned by
            // ReportOptionAvailabilityTests.
            "Writes to prompts-*.log, in full: your prompts, transcribed text from dictation and " +
            "files, AI responses, the App Mode name, and the Dictionary and trigger words sent with them. " +
            "A redacted summary (text replaced by its length) is always kept for 7 days.",
            settings.GetBool(AppDefaults.PromptTraceLoggingOptIn, false),
            v =>
            {
                ApplyDiagnosticsConsent(AppDefaults.PromptTraceLoggingOptIn, v, promptTraceSwitch);
                // Read the SWITCH, not v: a failed persist re-enters this lambda through the
                // consent revert (which assigns IsOn under the reverting latch, re-firing
                // Toggled), and at that point v is the value the user asked for, not the value
                // the app is actually in. The image row's persisted value is deliberately left
                // alone — a stored preference must not be rewritten because a different toggle
                // moved, and PromptTraceLog.ShouldWriteRaw short-circuits raw tracing on the parent anyway.
                includeImagePromptsSwitch.IsEnabled = promptTraceSwitch.IsOn;
            },
            out promptTraceSwitch);
        StripCardBorder(promptTraceToggle);

        ToggleSwitch keepRecordingsSwitch = null!;
        var keepRecordingsToggle = AppTheme.CreateToggleSetting(
            "Keep audio recordings",
            // REL-23: quiet recordings get a volume boost before transcription; with this on, the
            // pre-boost original is kept too — the consent copy must count both files.
            "Keeps each recording in Recordings\\Debug for 7 days (plus the pre-boost original when a volume boost was applied).",
            settings.GetBool(AppDefaults.KeepRecordingsForDebug, false),
            v => ApplyDiagnosticsConsent(AppDefaults.KeepRecordingsForDebug, v, keepRecordingsSwitch),
            out keepRecordingsSwitch);
        StripCardBorder(keepRecordingsToggle);

        // Crash reporting joins them (owner, 2026-08-04): it is a privacy/diagnostics consent
        // toggle, and it sat in the window-behaviour cluster at the top of the page only because
        // that is where it was added. Off by default. Since the launch defaults audit
        // (2026-09-13) it takes the SAME durable fail-OFF apply as the three rows above and
        // attaches or detaches the Sentry log sink in this session (CrashReportingConsent) —
        // the copy used to promise the change only after a restart, because the logger was
        // rebuilt only at startup. CrashReportingConsentTests keeps that sentence out.
        ToggleSwitch crashReportingSwitch = null!;
        var crashReporting = AppTheme.CreateToggleSetting(
            "Send crash reports",
            // REL-30: kept in step with privacy-v5 §2's closed list and the onboarding consent
            // step — this toggle is where consent is withdrawn or granted after onboarding.
            // Match the policy's WORDING, not merely its categories: the adapter/driver plural
            // is load-bearing (every display adapter is sent, not one), and it was singular here
            // until the REL-30 diff round caught it. See the fuller note in OnboardingPage.
            "Sends anonymous crash reports: stack traces, app version, OS version, your graphics adapter models and display driver versions, redacted log breadcrumbs. No transcriptions, no API keys.",
            settings.GetBool(AppDefaults.CrashReportingOptIn, false),
            v =>
            {
                if (consentReverting) return;
                ShowConsentResult(
                    Helpers.CrashReportingConsent.Apply(settings, v, App.ReattachLogSinks),
                    AppDefaults.CrashReportingOptIn, crashReportingSwitch);
            },
            out crashReportingSwitch);
        StripCardBorder(crashReporting);

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(sectionHeader);
        content.Children.Add(promptTraceToggle);
        content.Children.Add(includeImagePromptsToggle);
        content.Children.Add(keepRecordingsToggle);
        content.Children.Add(crashReporting);
        content.Children.Add(consentError);
        return AppTheme.CreateCard(content);
    }

    /// <summary>
    /// Strips the card-style border/background from a setting row so it can
    /// live inside an outer card without double-boxing.
    /// </summary>
    /// <remarks>A local alias so this file's ~20 call sites stay short. The implementation moved
    /// to <see cref="AppTheme.StripCardBorder"/> in UI-11, when the Models page needed it too.</remarks>
    private static void StripCardBorder(Border border) => AppTheme.StripCardBorder(border);

    /// <summary>
    /// Show an info dialog explaining that a hotkey can't be used because it's
    /// already the recording hotkey (hard block — can't override).
    /// </summary>
    private async Task ShowBlockedHotkeyDialogAsync(string hotkey, string role)
    {
        try
        {
            if (this.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = "Hotkey unavailable",
                Content = $"{HotkeyKeyDisplay.Describe(hotkey)} is your {role} hotkey and cannot be reassigned. Choose a different key.",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to show blocked hotkey dialog");
        }
    }

    /// <summary>
    /// Describe what the hotkey conflict is (for the confirmation dialog). Null if no conflict.
    /// </summary>
    /// <remarks>
    /// Asks <see cref="HotkeyConflictScan"/> rather than enumerating roles here. This page used to
    /// carry its own list, as did onboarding and the prompt dialog, and each was incomplete in a
    /// different way — five review rounds found four of them. A screen that holds no list cannot
    /// hold an incomplete one.
    /// </remarks>
    private string? GetConflictDescription(string hotkey, HotkeyRole editing)
    {
        var collisions = HotkeyConflictScan.ForRole(_viewModel.HotkeySnapshot(), hotkey, editing);
        if (collisions.Count == 0) return null;

        var first = collisions[0];
        var displayed = HotkeyKeyDisplay.Describe(hotkey);
        return first.IsPrompt
            ? $"{displayed} is assigned to {first.Describe()}. It will be cleared."
            : $"{displayed} is currently {first.Describe()}. It will be reassigned.";
    }

    /// <summary>
    /// Show a confirmation dialog for hotkey conflicts.
    /// Returns true if the user confirms the override.
    /// </summary>
    private async Task<bool> ShowHotkeyConflictDialogAsync(string hotkey, string conflictDescription)
    {
        try
        {
            if (this.XamlRoot == null) return false;

            var dialog = new ContentDialog
            {
                Title = "Hotkey conflict",
                Content = conflictDescription,
                PrimaryButtonText = "Override",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to show hotkey conflict dialog");
            return false;
        }
    }

    /// <summary>
    /// Clear whatever the confirmed hotkey collides with — every role AND every prompt, from the
    /// one scan, so nothing can be missed by an out-of-date list.
    /// </summary>
    private void ClearConflictingHotkey(string hotkey, HotkeyRole editing)
    {
        var collisions = HotkeyConflictScan.ForRole(_viewModel.HotkeySnapshot(), hotkey, editing);
        if (collisions.Count == 0) return;

        foreach (var collision in collisions)
        {
            if (collision.Role is { } role) _viewModel.SetHotkey(role, "");
        }

        var clearedPromptIds = collisions.Where(c => c.IsPrompt).Select(c => c.PromptId).ToHashSet(StringComparer.Ordinal);
        if (clearedPromptIds.Count > 0)
        {
            var enhancement = App.Services.GetRequiredService<Services.AIEnhancement.AIEnhancementService>();
            var prompts = enhancement.GetPrompts();
            foreach (var prompt in prompts.Where(p => clearedPromptIds.Contains(p.Id)))
                prompt.Hotkey = null;
            enhancement.SavePrompts(prompts);

            // Re-register prompt hotkeys so the cleared keys leave the hook.
            var hotkeyService = App.Services.GetRequiredService<Services.Input.HotkeyService>();
            hotkeyService.RegisterPromptHotkeys(
                prompts.Where(p => !string.IsNullOrEmpty(p.Hotkey))
                       .Select(p => (p.Id, p.Hotkey!)));
        }

        App.Services.GetRequiredService<Services.Input.HotkeyService>().ReloadHotkeys();
    }
}
