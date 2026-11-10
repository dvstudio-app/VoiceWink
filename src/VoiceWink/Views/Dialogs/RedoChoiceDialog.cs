using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// REL-20: after a delivered dictation, the pill's ↻ and the redo-last hotkey ask which re-run the
/// user wants. A plain ContentDialog (no Flyout, no new templated control — root CLAUDE.md WinUI
/// constraints); each button opens the picker that already exists for that re-run.
/// </summary>
public sealed class RedoChoiceDialog : ContentDialog
{
    public RedoChoiceDialog()
    {
        Title = "Run again";
        PrimaryButtonText = "Transcribe again";
        SecondaryButtonText = "Run AI again";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;
    }

    /// <summary>Maps the dialog's result to a choice; null = cancelled.</summary>
    public static RedoChoice? ChoiceFor(ContentDialogResult result) => result switch
    {
        ContentDialogResult.Primary => RedoChoice.Transcribe,
        ContentDialogResult.Secondary => RedoChoice.Enhance,
        _ => null,
    };
}
