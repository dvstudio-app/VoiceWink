using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.ViewModels;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// The transcription Retry picker (TRN-17): choose the model and recognition language for ONE
/// re-run of a failure-retained recording.
///
/// <para><b>Why the retry needed a dialog.</b> It re-ran with whatever the global Models-page
/// selection said, so the commonest failure — the cloud provider is unreachable — could only be
/// answered by leaving the pill, changing a global setting, and coming back hoping the pill was
/// still armed. The AI-enhancement redo already solved the same problem with a dialog; this is its
/// sibling.</para>
///
/// <para><b>A dialog CLASS, not another inline builder in <c>App.xaml.cs</c>.</b> AGENTS.md forbids
/// growing a hub, and <c>ShowEnhancementOptionsDialogAsync</c> is the counter-example: 1,146 lines,
/// five late-bound <c>Action?</c> fields, three lifecycle fences. Everything here arrives as data on
/// the request, so this type resolves no services and locates nothing.</para>
///
/// <para><b>Per-run only.</b> Nothing here writes settings. The choice rides the retry context and
/// dies with it — the Models page keeps saying whatever it said (owner decision).</para>
///
/// <para>Combo hygiene follows the three rules in root CLAUDE.md: plain DATA rows through
/// <c>DisplayMemberPath</c> (never a <c>UIElement</c> in an item's Content — <c>COMException
/// 0x800F1000</c>, four live fatal crashes), one <c>ItemsSource</c> swap per repopulate (never
/// <c>Items.Clear()</c>+<c>Add</c> — <c>COMException 0x80070490</c>), and
/// <see cref="AppTheme.AllowParentScroll"/> on both combos so an open popup owns the wheel.</para>
/// </summary>
public sealed class TranscriptionRetryOptionsDialog : ContentDialog
{
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _languageCombo;
    private readonly TextBlock _languageNote;
    private readonly RetryLanguageSelection _language;

    /// <summary>The user's choices, or <c>null</c> until they confirm.</summary>
    public MainViewModel.TranscriptionRetrySelection? Selection { get; private set; }

    public TranscriptionRetryOptionsDialog(MainViewModel.TranscriptionRetryPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Title = "Retry transcription";
        PrimaryButtonText = "Retry";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;

        _language = new RetryLanguageSelection(request.PreselectedLanguage);

        _modelCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Catalogue rows, rendered by the CATALOGUE's DisplayName — so a filename that
            // happens to be on disk structurally cannot become a label (UI-2).
            ItemsSource = request.Models,
            DisplayMemberPath = nameof(TranscriptionModelInfo.DisplayName),
            PlaceholderText = "Select a model...",
        };
        AppTheme.AllowParentScroll(_modelCombo);

        // Null preselect is a REAL answer, not a reason to fall back to index 0: the model the
        // attempt used is no longer runnable (deleted from disk, key removed), and silently
        // committing a different one is what the UX-1 rule forbids. The note below says which.
        if (request.PreselectedModelName != null)
        {
            _modelCombo.SelectedIndex = request.Models
                .ToList()
                .FindIndex(m => ModelDiskReconciliation.IsSameModel(m.Name, request.PreselectedModelName));
        }

        _languageCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(LanguageRow.Display),
        };
        AppTheme.AllowParentScroll(_languageCombo);

        _languageNote = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "The recording was saved. Choose what to transcribe it with — "
                         + "this applies to this retry only.",
                    Foreground = AppTheme.Brush(AppTheme.TextSecondary),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                UnavailableModelNote(request),
                Label("Model:"),
                _modelCombo,
                Label("Language:"),
                _languageCombo,
                _languageNote,
            },
        };

        // Height-flexible scroller (repo invariant) — a bare StackPanel in a ContentDialog clips
        // its button row instead of scrolling.
        Content = AppTheme.CreateDialogScroller(panel);

        _modelCombo.SelectionChanged += (_, _) =>
        {
            RefreshLanguageRows();
            UpdateConfirmEnabled();
        };
        _languageCombo.SelectionChanged += (_, _) =>
        {
            // Ignored while RefreshLanguageRows holds the populate scope: rebinding the combo for
            // a newly-picked model raises this too, and treating that as a user edit would lose a
            // pinned language the moment it passed through a model that could not offer it.
            _language.UserPicked(SelectedLanguageCode());
            RefreshLanguageNote();
        };

        RefreshLanguageRows();
        UpdateConfirmEnabled();

        PrimaryButtonClick += (_, _) =>
        {
            // Guarded by IsPrimaryButtonEnabled, but read defensively: a null model here would
            // reach the pipeline as a retry with no model at all.
            if (SelectedModel() is not { } model) return;

            // Commit the ASK, not the combo's clamped display value. Auto is always row 0 and
            // always selected, so reading the combo would ALWAYS win and the ask/display split
            // would survive only inside one dialog session (Kimi diff review): global zh → pick
            // Parakeet (clamps to Auto) → confirm → fail again → re-open → switch to Whisper →
            // confirm, and the zh pin is silently gone, which is the wrong-output failure
            // RetryLanguageSelection exists to prevent. Committing the ask makes the carry restore
            // it, and costs nothing on the run itself: EffectiveTranscriptionLanguage clamps at
            // the wire, so the Parakeet attempt is identical either way.
            Selection = new MainViewModel.TranscriptionRetrySelection(model.Name, _language.Requested);
        };
    }

    private TranscriptionModelInfo? SelectedModel() => _modelCombo.SelectedItem as TranscriptionModelInfo;

    private string? SelectedLanguageCode() => (_languageCombo.SelectedItem as LanguageRow)?.Code;

    /// <summary>
    /// Rebind the language rows for the currently selected model. Held inside the populate scope so
    /// the resulting SelectionChanged is not mistaken for a user edit.
    /// </summary>
    private void RefreshLanguageRows()
    {
        var (rows, index) = RetryLanguageSelection.RowsFor(SelectedModel(), _language.Requested);

        using (_language.BeginPopulate())
        {
            // ONE ItemsSource swap, built off-combo — never Items.Clear()+Add on a live combo.
            _languageCombo.ItemsSource = rows;
            _languageCombo.SelectedIndex = index;
        }

        RefreshLanguageNote();
    }

    /// <summary>
    /// The amber sentence under the language combo — "Parakeet detects the language itself", or
    /// "supports 25 European languages, not Chinese". A sibling TextBlock updated in place, never
    /// per-item combo annotation, which is the crash surface.
    ///
    /// <para>Composed against what the user ASKED for, not the clamped display: the whole point is
    /// to explain why the two differ. <c>recommendation: null</c> because this dialog offers no
    /// download — the action clause would name a model the user cannot get from here.</para>
    /// </summary>
    private void RefreshLanguageNote()
    {
        var note = ModelLanguageSupport.ComposeLanguageNote(SelectedModel(), _language.Requested);
        _languageNote.Text = note ?? "";
        _languageNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateConfirmEnabled()
    {
        // Only the model gates it: Auto is always row 0 of the language combo and always present,
        // so there is no unselected language state to guard.
        IsPrimaryButtonEnabled = SelectedModel() != null;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = AppTheme.Brush(AppTheme.TextPrimary),
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>
    /// Names the model the attempt would have used when it is no longer runnable, so an empty
    /// selection reads as an explanation rather than a bug. Rendered through
    /// <see cref="ModelDisplayName.Resolve"/>, which returns "(unrecognised model)" for a
    /// hand-edited id instead of echoing it.
    /// </summary>
    private static TextBlock UnavailableModelNote(MainViewModel.TranscriptionRetryPickerRequest request) => new()
    {
        Text = $"{request.RequestedModelDisplayName} can't run right now — pick another model.",
        Foreground = AppTheme.Brush(AppTheme.WarningText),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = request.PreselectedModelName == null ? Visibility.Visible : Visibility.Collapsed,
    };
}
