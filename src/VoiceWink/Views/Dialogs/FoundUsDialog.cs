using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Controls;
using VoiceWink.Helpers;

namespace VoiceWink.Views.Dialogs;

/// <summary>LNC-14 "How did you find VoiceWink?": one button per answer; the page sends <see cref="ChosenTag"/>.</summary>
public sealed class FoundUsDialog : ContentDialog
{
    /// <summary>The picked answer's tag, or null when the user chose "Not now".</summary>
    public string? ChosenTag { get; private set; }

    public FoundUsDialog()
    {
        Title = FeedbackLinks.FoundUsText;
        CloseButtonText = FeedbackLinks.NotNowText;
        RequestedTheme = AppTheme.ElementTheme;

        var choices = new FlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var (label, tag) in FeedbackLinks.FoundUsChoices)
        {
            choices.Children.Add(AppTheme.CreateSecondaryButton(label, (_, _) =>
            {
                ChosenTag = tag;
                Hide();
            }));
        }

        Content = AppTheme.CreateDialogScroller(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                choices,
                new TextBlock
                {
                    Text = FeedbackLinks.FoundUsPrivacyLine,
                    FontSize = 12,
                    Foreground = AppTheme.Brush(AppTheme.SubtleText),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
    }
}
