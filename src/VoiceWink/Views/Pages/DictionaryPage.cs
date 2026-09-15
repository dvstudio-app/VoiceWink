using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.System;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Dictionary page — vocabulary words + word replacements management.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class DictionaryPage : Page
{
    private static ILogger Logger => Log.ForContext<DictionaryPage>();

    private readonly DictionaryViewModel _viewModel;
    private readonly NotifyCollectionChangedEventHandler _vocabCollectionChanged;
    private readonly NotifyCollectionChangedEventHandler _replacementsCollectionChanged;
    private StackPanel? _vocabList;
    private StackPanel? _replacementList;
    private StatusMessage? _statusMessage;
    // DCT-2: edit-in-place state for the Word Replacements card. The two input boxes double as
    // the editor: Edit on a row loads it here, the Add button reads Save and a Cancel appears
    // until the edit is saved or cancelled. Page state, not view-model state — a page instance is
    // created per navigation while the view model is a singleton, so an abandoned edit dies with
    // the page and nothing of it can leak into the next page's Add (the boxes are never mirrored
    // into the view model — see BuildReplacementsCard).
    private Models.Entities.WordReplacement? _editingReplacement;
    private TextBox? _replacementOriginalBox;
    private TextBox? _replacementTargetBox;
    private TextBlock? _replacementSubmitLabel;
    private Border? _replacementCancelButton;
    // One submit at a time (Codex + Gemini diff r1): Enter then a click on Add before the
    // database await returned inserted the boxes' text twice. Set for the whole async span.
    private bool _replacementSubmitPending;
    // Coalesce a burst of CollectionChanged events (bulk add/delete raises N) into ONE
    // queued full-panel rebuild per list instead of N. Each rebuild reads current VM
    // state, so collapsing duplicates loses nothing. Perf-only — DictionaryPage rebuilds
    // plain Border/TextBlock rows, no editable combo (not the PR #163 crash class).
    private bool _vocabRefreshPending;
    private bool _replacementsRefreshPending;
    private bool _isUnloaded;

    public DictionaryPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _viewModel = App.Services.GetRequiredService<DictionaryViewModel>();
        BuildUI();

        _vocabCollectionChanged = (_, _) => QueueVocabRefresh();
        _replacementsCollectionChanged = (_, _) => QueueReplacementsRefresh();

        Loaded += (_, _) =>
        {
            _isUnloaded = false; // fence re-opens on re-attach (F27 idiom)
            _viewModel.VocabularyWords.CollectionChanged += _vocabCollectionChanged;
            _viewModel.WordReplacements.CollectionChanged += _replacementsCollectionChanged;
            _ = _viewModel.LoadAsync();
        };
        this.Unloaded += OnUnloaded;
    }

    private void QueueVocabRefresh()
    {
        if (_vocabRefreshPending) return;
        _vocabRefreshPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _vocabRefreshPending = false;
                if (_isUnloaded) return; // page detached before this ran
                RefreshVocabulary();
            }))
        {
            _vocabRefreshPending = false; // enqueue refused (shutdown) — don't wedge the flag
        }
    }

    private void QueueReplacementsRefresh()
    {
        if (_replacementsRefreshPending) return;
        _replacementsRefreshPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _replacementsRefreshPending = false;
                if (_isUnloaded) return;
                RefreshReplacements();
            }))
        {
            _replacementsRefreshPending = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _viewModel.VocabularyWords.CollectionChanged -= _vocabCollectionChanged;
        _viewModel.WordReplacements.CollectionChanged -= _replacementsCollectionChanged;
    }

    private void BuildUI()
    {
        // ── Page header ───────────────────────────────────────────────
        var header = AppTheme.CreatePageHeader(
            "Dictionary",
            "Enhance VoiceWink's transcription accuracy by teaching it your vocabulary.");

        // ── Master "Enable Dictionary" toggle (mirrors the AI Enhancement page's
        // enable toggle). Gates runtime USE only — vocabulary hints, the enhancement
        // vocabulary block, and word replacements; both cards below stay editable so
        // the lists can be curated while off. Prompt trigger words are prompt-stack
        // data, not Dictionary data, and keep riding live-dictation hints.
        var settings = App.Services.GetRequiredService<SettingsService>();
        var enableToggle = AppTheme.CreateToggleSetting(
            "Enable Dictionary",
            // SET-2 (owner, 2026-08-03): the trailing "Prompt trigger words stay active." was
            // dropped at the owner's request. The BEHAVIOUR is unchanged — trigger words are
            // prompt-stack data and still ride live-dictation hints (see the note above) — and
            // the Models page still states it where it actually bites, on the Deepgram/ElevenLabs
            // rows ("Dictionary is off — only trigger words will be sent.").
            "Use your vocabulary and word replacements to improve transcriptions.",
            settings.GetBool(AppDefaults.DictionaryEnabled, true),
            isOn => settings.SetBool(AppDefaults.DictionaryEnabled, isOn));

        // ── Custom Vocabulary card ────────────────────────────────────
        var vocabCard = BuildVocabularyCard();

        // ── Word Replacements card ────────────────────────────────────
        var replacementsCard = BuildReplacementsCard();

        // Status message bar (hidden by default)
        var statusBlock = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.AccentRed),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _statusMessage = new StatusMessage(statusBlock);

        // ── Page layout ───────────────────────────────────────────────
        var pageContent = new StackPanel
        {
            Children = { header, enableToggle, vocabCard, replacementsCard, statusBlock }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Custom Vocabulary card
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private Border BuildVocabularyCard()
    {
        // Section header
        var sectionHeader = new TextBlock
        {
            Text = "Custom Vocabulary",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            Margin = new Thickness(0, 0, 0, 4)
        };

        // Info text
        var infoText = new TextBlock
        {
            // DCT-1: says where the words actually go — the previous copy promised "improve recognition
            // accuracy" for every engine and said trigger words reached local Whisper and Groq, both
            // false since TRN-11 (those transports send no hints; see HintTransportKind). Trimmed on
            // owner UAT 2026-09-13 (too long, and the defaults sentence was patronising): the Models-page toggle and the trigger-word
            // detail live where those settings are, and the shipped defaults are NOT announced here
            // (owner: the user can see the rows and knows what delete does) — DefaultDictionary's
            // own doc comment carries the seeding rule.
            Text = "Recognition hints for Deepgram, ElevenLabs and GPT Transcribe, and preferred spellings for AI enhancement. Local Whisper, Groq and Parakeet take no hints — use Word Replacements for those.",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        // Input row: TextBox + compact Add button
        var newWordBox = new TextBox
        {
            PlaceholderText = "Add a new word...",
            Height = 34,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        newWordBox.TextChanged += (_, _) => _viewModel.NewVocabularyWord = newWordBox.Text;

        // Enter key to add
        newWordBox.KeyDown += async (_, e) =>
        {
            // Guarded like HandleEnterKey below: an unhandled exception escaping an
            // async void UI handler (DB write failure) crashes the whole app (F38).
            try
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var word = newWordBox.Text?.Trim();
                    if (string.IsNullOrEmpty(word)) return;
                    var countBefore = _viewModel.VocabularyWords.Count;
                    await _viewModel.AddVocabularyWordCommand.ExecuteAsync(null);
                    if (_viewModel.VocabularyWords.Count > countBefore)
                        newWordBox.Text = "";
                }
            }
            catch (Exception ex)
            {
                Log.ForContext<DictionaryPage>().Error(ex, "Add vocabulary word failed");
                _statusMessage?.Show("Failed to add the word. Please try again.");
            }
        };

        var addWordBtn = CreateCompactAccentButton("Add");
        addWordBtn.Tapped += async (_, _) =>
        {
            try
            {
                var word = newWordBox.Text?.Trim();
                if (string.IsNullOrEmpty(word)) return;
                var countBefore = _viewModel.VocabularyWords.Count;
                await _viewModel.AddVocabularyWordCommand.ExecuteAsync(null);
                // Only clear the input if the word was actually added
                if (_viewModel.VocabularyWords.Count > countBefore)
                    newWordBox.Text = "";
            }
            catch (Exception ex)
            {
                Log.ForContext<DictionaryPage>().Error(ex, "Add vocabulary word failed");
                _statusMessage?.Show("Failed to add the word. Please try again.");
            }
        };

        // A Grid, not a horizontal StackPanel: a StackPanel gives a TextBox infinite width, so it
        // grows with its text and pushes the button off the card (owner UAT 2026-09-13, on the
        // replacement row below; this row gets the same shape). The box takes the card's width and
        // scrolls its own text; the button keeps its place.
        var inputRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 16) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        newWordBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(newWordBox, 0);
        Grid.SetColumn(addWordBtn, 1);
        inputRow.Children.Add(newWordBox);
        inputRow.Children.Add(addWordBtn);

        // Vocabulary word list (pills / tags)
        _vocabList = new StackPanel { Spacing = 8 };

        var cardContent = new StackPanel
        {
            Children = { sectionHeader, infoText, inputRow, _vocabList }
        };

        return AppTheme.CreateCard(cardContent);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Word Replacements card
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private Border BuildReplacementsCard()
    {
        // Section header
        var sectionHeader = new TextBlock
        {
            Text = "Word Replacements",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            Margin = new Thickness(0, 0, 0, 4)
        };

        // Info text
        var infoText = new TextBlock
        {
            Text = "Replaces words in every transcript.",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        // Input row: original + replacement + compact Add button
        var origBox = new TextBox
        {
            PlaceholderText = "Original word(s)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        // No TextChanged mirror into the view model (DCT-2 self-review, lens A): the view model
        // is a singleton and this page is not, so mirrored text outlived the page that typed it
        // and a later Add on empty boxes inserted it. The boxes are read at submit time instead.

        var replBox = new TextBox
        {
            PlaceholderText = "Replace with",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        _replacementOriginalBox = origBox;
        _replacementTargetBox = replBox;

        // Enter on either box and the button share ONE submit path (DCT-2): Add when no row is
        // being edited, Save for the row under edit. Both handlers are async void, so the guard
        // stays around the await — an unhandled exception there crashes the app (F38).
        async void HandleEnterKey(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true; // consumed here — no default-button bubbling, no sound cue (Gemini diff r1)
            await SubmitReplacementAsync();
        }
        origBox.KeyDown += HandleEnterKey;
        replBox.KeyDown += HandleEnterKey;

        var addReplBtn = CreateCompactAccentButton("Add");
        _replacementSubmitLabel = (TextBlock)addReplBtn.Child;
        addReplBtn.Tapped += async (_, _) => await SubmitReplacementAsync();

        var cancelEditBtn = AppTheme.CreateCompactButton("Cancel");
        cancelEditBtn.Height = 34;
        cancelEditBtn.VerticalAlignment = VerticalAlignment.Top;
        cancelEditBtn.Visibility = Visibility.Collapsed;
        cancelEditBtn.Tapped += (_, _) => EndEditReplacement(clearBoxes: true);
        _replacementCancelButton = cancelEditBtn;

        // A Grid, not a horizontal StackPanel (owner UAT 2026-09-13): the StackPanel gave each box
        // infinite width, so editing the shipped six-variant VoiceWink rule grew the original box
        // past the card and put Save off the screen. The two boxes now share the card's width
        // (3:2) and scroll their own text; the buttons sit in an Auto column that never moves.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { addReplBtn, cancelEditBtn }
        };
        var inputRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 16) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(origBox, 0);
        Grid.SetColumn(replBox, 1);
        Grid.SetColumn(buttons, 2);
        inputRow.Children.Add(origBox);
        inputRow.Children.Add(replBox);
        inputRow.Children.Add(buttons);

        // Replacement items list
        _replacementList = new StackPanel { Spacing = 6 };

        var cardContent = new StackPanel
        {
            Children = { sectionHeader, infoText, inputRow, _replacementList }
        };

        return AppTheme.CreateCard(cardContent);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Word Replacements: add / edit-in-place (DCT-2)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>Add a new row, or save the row under edit — whichever the input row is in. The
    /// boxes clear only when the write actually happened, so a refused submit (a blank field, a
    /// row deleted meanwhile) never throws the user's typing away.</summary>
    private async Task SubmitReplacementAsync()
    {
        if (_replacementOriginalBox == null || _replacementTargetBox == null) return;
        if (_replacementSubmitPending || _isUnloaded) return;
        _replacementSubmitPending = true;
        try
        {
            if (_editingReplacement is { } editing)
            {
                var saved = await _viewModel.UpdateReplacementAsync(
                    editing, _replacementOriginalBox.Text, _replacementTargetBox.Text);
                if (_isUnloaded) return; // navigated away mid-save: the write stands, the page is gone
                // A pencil clicked on another row while this save was in flight has already
                // moved the edit there; this continuation must not end THAT edit (lens A).
                var stillThisEdit = ReferenceEquals(_editingReplacement, editing);
                if (saved)
                {
                    if (stillThisEdit) EndEditReplacement(clearBoxes: true);
                }
                else if (string.IsNullOrWhiteSpace(_replacementOriginalBox.Text) || string.IsNullOrWhiteSpace(_replacementTargetBox.Text))
                {
                    // The view model refuses for exactly two reasons; a blank field is the first.
                    _statusMessage?.Show("Both fields are needed to save the replacement.");
                }
                else
                {
                    // The second: the row is gone. Nothing in the app deletes a row under this
                    // page's edit except its own delete button (which ends the edit itself), so
                    // this is parity with DeleteReplacementAsync's null branch, not a live path.
                    // Reload so the list agrees, keep the typing so the user can add it as a new row.
                    await _viewModel.LoadAsync();
                    if (_isUnloaded) return;
                    if (ReferenceEquals(_editingReplacement, editing)) EndEditReplacement(clearBoxes: false);
                    _statusMessage?.Show("That replacement no longer exists. Use Add to create it again.");
                }
                return;
            }

            // Add from the boxes' CURRENT text — never from view-model state (lens A).
            var added = await _viewModel.AddReplacementAsync(_replacementOriginalBox.Text, _replacementTargetBox.Text);
            if (_isUnloaded) return;
            // Clear only if no pencil was clicked while the add was in flight — the boxes would
            // then hold the row just loaded for editing, not the text just added (Gemini diff r1).
            if (added && _editingReplacement == null)
            {
                _replacementOriginalBox.Text = "";
                _replacementTargetBox.Text = "";
            }
        }
        catch (Exception ex)
        {
            var editingNow = _editingReplacement != null;
            Log.ForContext<DictionaryPage>().Error(ex, editingNow ? "Save word replacement failed" : "Add word replacement failed");
            if (_isUnloaded) return;
            _statusMessage?.Show(editingNow
                ? "Failed to save the replacement. Please try again."
                : "Failed to add the replacement. Please try again.");
        }
        finally
        {
            _replacementSubmitPending = false;
        }
    }

    /// <summary>Load a row into the input boxes and switch the row to Save / Cancel. Starting
    /// an edit while another is open simply moves the edit — nothing was written yet.</summary>
    private void BeginEditReplacement(Models.Entities.WordReplacement replacement)
    {
        if (_replacementOriginalBox == null || _replacementTargetBox == null) return;
        _editingReplacement = replacement;
        _replacementOriginalBox.Text = replacement.OriginalText;
        _replacementTargetBox.Text = replacement.ReplacementText;
        if (_replacementSubmitLabel != null) _replacementSubmitLabel.Text = "Save";
        if (_replacementCancelButton != null) _replacementCancelButton.Visibility = Visibility.Visible;
        RefreshReplacements(); // the row under edit is outlined
        _replacementOriginalBox.Focus(FocusState.Programmatic);
        _replacementOriginalBox.SelectionStart = _replacementOriginalBox.Text.Length;
    }

    /// <summary>Back to Add mode. <paramref name="clearBoxes"/> is false when the typing should
    /// survive (a row that vanished under the edit).</summary>
    private void EndEditReplacement(bool clearBoxes)
    {
        _editingReplacement = null;
        if (_replacementSubmitLabel != null) _replacementSubmitLabel.Text = "Add";
        if (_replacementCancelButton != null) _replacementCancelButton.Visibility = Visibility.Collapsed;
        if (clearBoxes && _replacementOriginalBox != null && _replacementTargetBox != null)
        {
            _replacementOriginalBox.Text = "";
            _replacementTargetBox.Text = "";
        }
        RefreshReplacements();
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Refresh: vocabulary pills
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private void RefreshVocabulary()
    {
        if (_vocabList == null) return;
        _vocabList.Children.Clear();

        if (_viewModel.VocabularyWords.Count == 0)
        {
            _vocabList.Children.Add(new TextBlock
            {
                Text = "No vocabulary words added yet. Add words above to improve transcription accuracy.",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 4)
            });
            return;
        }

        var wrapPanel = new FlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };

        foreach (var word in _viewModel.VocabularyWords)
        {
            var pill = CreateVocabularyPill(word.Word, async () =>
            {
                try
                {
                    await _viewModel.DeleteVocabularyWordCommand.ExecuteAsync(word);
                }
                catch (Exception ex)
                {
                    // Same async-void crash class as the add handlers (F38, Codex R1).
                    Log.ForContext<DictionaryPage>().Error(ex, "Delete vocabulary word failed");
                    _statusMessage?.Show("Failed to delete the word. Please try again.");
                }
            });
            wrapPanel.Children.Add(pill);
        }

        _vocabList.Children.Add(wrapPanel);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Refresh: replacement rows
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private void RefreshReplacements()
    {
        if (_replacementList == null) return;
        _replacementList.Children.Clear();

        if (_viewModel.WordReplacements.Count == 0)
        {
            _replacementList.Children.Add(new TextBlock
            {
                Text = "No word replacements configured yet. Add replacements above to auto-correct transcriptions.",
                FontSize = 13,
                Foreground = AppTheme.Brush(AppTheme.DimText),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 4)
            });
            return;
        }

        foreach (var r in _viewModel.WordReplacements)
        {
            _replacementList.Children.Add(CreateReplacementRow(r));
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  UI factory: vocabulary pill tag
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private static Border CreateVocabularyPill(string word, Action onDelete)
    {
        // AccentBlue at 15% opacity for background
        var pillBg = ColorHelper.FromArgb(38, 0, 122, 255); // ~15% of AccentBlue

        var wordText = new TextBlock
        {
            Text = word,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            VerticalAlignment = VerticalAlignment.Center
        };

        // X delete icon
        var deleteIcon = new FontIcon
        {
            Glyph = "\uE711", // Cancel / X
            FontSize = 10,
            Foreground = AppTheme.Brush(AppTheme.AccentBlue)
        };

        var deleteBorder = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Child = new Viewbox
            {
                Width = 10,
                Height = 10,
                Child = deleteIcon
            },
            Background = AppTheme.TransparentBrush
        };

        var deleteHoverBg = AppTheme.Brush(ColorHelper.FromArgb(50, 0, 122, 255));
        deleteBorder.PointerEntered += (_, _) => deleteBorder.Background = deleteHoverBg;
        deleteBorder.PointerExited += (_, _) => deleteBorder.Background = AppTheme.TransparentBrush;
        deleteBorder.Tapped += (_, _) => onDelete();

        var pillContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { wordText, deleteBorder }
        };

        return new Border
        {
            Background = AppTheme.Brush(pillBg),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 8, 6),
            Child = pillContent
        };
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  UI factory: replacement row
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private Border CreateReplacementRow(Models.Entities.WordReplacement replacement)
    {
        var rowBg = AppTheme.RowBg;
        var rowHoverBg = AppTheme.RowHoverBg;

        // Original text
        var originalText = new TextBlock
        {
            Text = replacement.OriginalText,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = AppTheme.Brush(replacement.IsEnabled ? AppTheme.TextPrimary : AppTheme.DimText),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // The tooltips carry the full text for a row that still trims — one longer than the row
        // itself. ToolTipService is already rendered by AppTheme in CLI-launched builds. (DCT-1
        // capped this label at 200 px and the shipped six-variant VoiceWink rule trimmed with
        // most of the row empty; the cap now comes from the row's width — ReplacementRowPanel.)
        ToolTipService.SetToolTip(originalText, replacement.OriginalText);

        // Arrow icon
        var arrow = new FontIcon
        {
            Glyph = "\uE72A", // Forward arrow
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };

        // Replacement text
        var replacementText = new TextBlock
        {
            Text = replacement.ReplacementText,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = AppTheme.Brush(replacement.IsEnabled ? AppTheme.AccentBlue : AppTheme.DimText),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        ToolTipService.SetToolTip(replacementText, replacement.ReplacementText);

        // Left side: original -> replacement, laid out by ReplacementRowPanel — NOT a horizontal
        // StackPanel, whose unconstrained desired width would floor the row's star column and push
        // the controls off the card. The panel caps the two labels from the width it is given
        // (ReplacementRowWidths.Split: both keep their natural width while they fit; otherwise the
        // replacement is guaranteed 40% of the row, or what the original leaves free, and the
        // original gets the rest, trimming last) and never asks for more than that width.
        var textGroup = new ReplacementRowPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { originalText, arrow, replacementText }
        };

        // Toggle button
        var toggleSwitch = new ToggleSwitch
        {
            IsOn = replacement.IsEnabled,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AppTheme.AllowParentScroll(toggleSwitch);
        var suppressToggle = false;
        toggleSwitch.Toggled += async (_, _) =>
        {
            if (suppressToggle) return;
            try
            {
                await _viewModel.ToggleReplacementCommand.ExecuteAsync(replacement);
            }
            catch (Exception ex)
            {
                // SEC-3: user-authored dictionary text — see DictionaryViewModel for the contract.
                Logger.Warning(ex, "Toggle save failed: dictTerm={Original}",
                    Helpers.LogValueSanitizer.SingleLine(replacement.OriginalText));
                // Revert the toggle switch to match the reverted in-memory state
                suppressToggle = true;
                toggleSwitch.IsOn = replacement.IsEnabled;
                suppressToggle = false;
                _statusMessage?.Show("Failed to save toggle state. Please try again.");
            }

            // The labels follow the row's FINAL state on both paths: a successful toggle never
            // rebuilds the list (nothing in the collection changes), so before this line only the
            // failure branch re-brushed them and a rule toggled off kept its enabled colours until
            // the page was rebuilt (self-review on PR 926, 2026-09-13).
            originalText.Foreground = AppTheme.Brush(replacement.IsEnabled ? AppTheme.TextPrimary : AppTheme.DimText);
            replacementText.Foreground = AppTheme.Brush(replacement.IsEnabled ? AppTheme.AccentBlue : AppTheme.DimText);
        };

        // Delete button (subtle X)
        var deleteIcon = new FontIcon
        {
            Glyph = "\uE74D", // Delete
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };

        var deleteBtn = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Child = new Viewbox
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = deleteIcon
            },
            Background = AppTheme.TransparentBrush
        };

        var deleteHoverBg = AppTheme.Brush(ColorHelper.FromArgb(30, 255, 69, 58));
        deleteBtn.PointerEntered += (_, _) =>
        {
            deleteBtn.Background = deleteHoverBg;
            deleteIcon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
        };
        deleteBtn.PointerExited += (_, _) =>
        {
            deleteBtn.Background = AppTheme.TransparentBrush;
            deleteIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        };
        deleteBtn.Tapped += async (_, _) =>
        {
            try
            {
                await _viewModel.DeleteReplacementCommand.ExecuteAsync(replacement);
                // DCT-2: deleting the row under edit cancels the edit — there is nothing left to
                // save into — and keeps the typing in case the delete was the wrong click.
                if (_editingReplacement?.Id == replacement.Id)
                    EndEditReplacement(clearBoxes: false);
            }
            catch (Exception ex)
            {
                Log.ForContext<DictionaryPage>().Error(ex, "Delete word replacement failed");
                _statusMessage?.Show("Failed to delete the replacement. Please try again.");
            }
        };

        // Edit button (DCT-2, pencil) — same chrome as the delete, accent hover instead of red
        var editIcon = new FontIcon
        {
            Glyph = "\uE70F", // Edit
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        var editBtn = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Child = new Viewbox
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = editIcon
            },
            Background = AppTheme.TransparentBrush
        };
        ToolTipService.SetToolTip(editBtn, "Edit");
        var editHoverBg = AppTheme.Brush(ColorHelper.FromArgb(30, 0, 122, 255));
        editBtn.PointerEntered += (_, _) =>
        {
            editBtn.Background = editHoverBg;
            editIcon.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
        };
        editBtn.PointerExited += (_, _) =>
        {
            editBtn.Background = AppTheme.TransparentBrush;
            editIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        };
        editBtn.Tapped += (_, _) => BeginEditReplacement(replacement);

        // Right side: toggle + edit + delete
        var controlsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
            Children = { toggleSwitch, editBtn, deleteBtn }
        };

        // Row layout with Grid for proper left/right alignment. The 12 px column gap keeps the
        // replacement text off the toggle when the row is full (owner UAT 2026-09-13).
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textGroup, 0);
        Grid.SetColumn(controlsPanel, 1);
        grid.Children.Add(textGroup);
        grid.Children.Add(controlsPanel);

        var isUnderEdit = _editingReplacement?.Id == replacement.Id;
        var rowBorder = new Border
        {
            Background = AppTheme.Brush(rowBg),
            CornerRadius = new CornerRadius(8),
            // DCT-2: the row whose text sits in the input boxes is outlined, so "which row am I
            // editing" is answered on the list itself, not only by the Save label. The outline's
            // 1 px comes out of the padding so the row's content does not shift (Gemini diff r1).
            Padding = isUnderEdit ? new Thickness(13, 7, 9, 7) : new Thickness(14, 8, 10, 8),
            BorderBrush = isUnderEdit ? AppTheme.Brush(AppTheme.AccentBlue) : null,
            BorderThickness = new Thickness(isUnderEdit ? 1 : 0),
            Child = grid
        };

        rowBorder.PointerEntered += (_, _) => rowBorder.Background = AppTheme.Brush(rowHoverBg);
        rowBorder.PointerExited += (_, _) => rowBorder.Background = AppTheme.Brush(rowBg);

        return rowBorder;
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  Compact accent button (smaller than AppTheme.CreateAccentButton)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    private static Border CreateCompactAccentButton(string text)
    {
        var btn = AppTheme.CreateCompactButton(text, isAccent: true);
        btn.Height = 34;
        btn.VerticalAlignment = VerticalAlignment.Top;
        return btn;
    }

    /// <summary>Simple auto-dismissing status message bar.</summary>
    private sealed class StatusMessage
    {
        private readonly TextBlock _block;
        private CancellationTokenSource? _cts;

        public StatusMessage(TextBlock block) => _block = block;

        public void Show(string text, int durationMs = 4000)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _block.Text = text;
            _block.Visibility = Visibility.Visible;
            _ = DismissAsync(_cts.Token, durationMs);
        }

        private async Task DismissAsync(CancellationToken ct, int ms)
        {
            try
            {
                await Task.Delay(ms, ct);
                _block.Visibility = Visibility.Collapsed;
            }
            catch (TaskCanceledException) { }
        }
    }
}
