using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Controls;
using VoiceWink.Helpers;

namespace VoiceWink.Views.Dialogs;

/// <summary>LNC-14 "Tell a friend": the editable share message, copied or handed to the shell; the app sends nothing itself.</summary>
public sealed class TellAFriendDialog : ContentDialog
{
    private readonly TextBox _message;
    private readonly TextBlock _status;
    private readonly Func<string, Task<bool>> _copy;

    /// <param name="message">The pre-filled message (<see cref="FeedbackLinks.ShareMessage"/>).</param>
    /// <param name="copy">Puts text on the clipboard; false when it could not.</param>
    public TellAFriendDialog(string message, Func<string, Task<bool>> copy)
    {
        _copy = copy;

        Title = FeedbackLinks.TellAFriendText;
        CloseButtonText = FeedbackLinks.CloseText;
        RequestedTheme = AppTheme.ElementTheme;

        _message = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            // Bounded so a long paste scrolls inside the box instead of pushing the buttons out of view.
            MaxHeight = 200,
            Text = message,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_message, FeedbackLinks.TellAFriendText);

        _status = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        var actions = new FlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        actions.Children.Add(AppTheme.CreateAccentButton(FeedbackLinks.CopyMessageText, async (_, _) =>
        {
            if (FeedbackLinks.ShareClipboardText(_message.Text) is not { } text) return;
            Show(await CopySafelyAsync(text) ? FeedbackLinks.ShareCopied : null, FeedbackLinks.ShareCopyFailure);
        }));
        actions.Children.Add(AppTheme.CreateSecondaryButton(FeedbackLinks.CopyLinkText, async (_, _) =>
            Show(await CopySafelyAsync(VoiceWinkUrls.Share) ? FeedbackLinks.ShareLinkCopied : null, FeedbackLinks.ShareLinkCopyFailure)));
        actions.Children.Add(AppTheme.CreateSecondaryButton("Email", (_, _) =>
            Open(FeedbackLinks.BuildShareMailto(_message.Text), FeedbackLinks.ShareMailFailure)));
        actions.Children.Add(AppTheme.CreateSecondaryButton("WhatsApp", (_, _) =>
            Open(FeedbackLinks.BuildWhatsAppShareUrl(_message.Text), FeedbackLinks.ShareBrowserFailure)));
        actions.Children.Add(AppTheme.CreateSecondaryButton("LinkedIn", (_, _) =>
            Open(FeedbackLinks.BuildLinkedInShareUrl(), FeedbackLinks.ShareBrowserFailure)));
        actions.Children.Add(AppTheme.CreateSecondaryButton("X", (_, _) =>
            Open(FeedbackLinks.BuildXShareUrl(_message.Text), FeedbackLinks.ShareBrowserFailure)));

        Content = AppTheme.CreateDialogScroller(new StackPanel { Spacing = 12, Children = { _message, actions, _status } });
    }

    // The click handlers run inside WinUI's callback, where an exception would end the app.
    private async Task<bool> CopySafelyAsync(string text)
    {
        try { return await _copy(text); }
        catch { return false; }
    }

    private void Open(string? url, string failure)
    {
        if (url is null) { Show(null, FeedbackLinks.ShareTooLong); return; }
        Show(ShellLink.TryOpen(url) ? "" : null, failure);
    }

    /// <summary>Shows <paramref name="success"/> (empty hides the line) or, when it is null, <paramref name="failure"/>.</summary>
    private void Show(string? success, string failure)
    {
        var text = success ?? failure;
        _status.Text = text;
        _status.Foreground = AppTheme.Brush(success is null ? AppTheme.WarningText : AppTheme.TextSecondary);
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
