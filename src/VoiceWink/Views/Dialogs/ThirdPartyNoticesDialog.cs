using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;

namespace VoiceWink.Views.Dialogs;

/// <summary>
/// The shipped third-party licence manifest, in the app (UI-22). Built on
/// <see cref="WhatsNewDialog"/>'s shape — code-behind, presentation-only, the shared
/// <see cref="AppTheme.CreateDialogScroller(UIElement, double)"/> — because the owner asked for
/// "a dialogue similar to the What's New dialogue" instead of the Notepad launch that shipped in
/// PR #935. The caller owns reading the file and the failure path; this type only renders.
/// </summary>
public sealed class ThirdPartyNoticesDialog : ContentDialog
{
    /// <summary>The template's own default; the override below never goes under it.</summary>
    private const double DefaultMaxWidth = 548;

    /// <summary>Wide enough for a four-column package table, narrow enough to stay readable.</summary>
    private const double PreferredMaxWidth = 880;

    /// <summary>
    /// <paramref name="root"/> is taken here rather than assigned by the caller afterwards because
    /// the width override needs it DURING construction (Codex plan review): a ContentDialog cannot
    /// exceed its host window, so the preferred width is clamped to what the window actually offers.
    /// </summary>
    /// <exception cref="FormatException">The markdown cannot be rendered without hiding content.</exception>
    public ThirdPartyNoticesDialog(string markdown, XamlRoot? root)
    {
        Title = "Third-party licenses";
        PrimaryButtonText = "Close";
        DefaultButton = ContentDialogButton.Primary;
        RequestedTheme = AppTheme.ElementTheme;
        XamlRoot = root;

        // The package tables need more than the template's 548 epx. Same local-resource technique,
        // and the same never-shrink rule, as the ContentDialogMaxHeight override in App.xaml.cs.
        var windowWidth = root?.Size.Width ?? 0;
        if (windowWidth > 0)
            Resources["ContentDialogMaxWidth"] =
                Math.Max(DefaultMaxWidth, Math.Min(PreferredMaxWidth, windowWidth - 48.0));

        Content = AppTheme.CreateDialogScroller(NoticesMarkdownRenderer.Render(markdown), 460);
        AppTheme.CentreCommandButton(this); // the lone button sits in the right half otherwise
    }
}
