using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AIEnhancement.LocalEngine;
using VoiceWink.Services.System;
using VoiceWink.ViewModels;
using VoiceWink.Views.Pages;

namespace VoiceWink.Views.Dialogs;

/// <summary>LAI-30: offers the retired built-in model's switch at start, so a user who never opens AI Enhancement sees it; plain ContentDialog, all in code.</summary>
internal sealed class RetiredModelSwitchDialog
{
    private static ILogger Logger => Log.ForContext<RetiredModelSwitchDialog>();

    private readonly SettingsService _settings;
    private readonly AIEnhancementService _enhancement;
    private readonly EnhancementViewModel _viewModel;

    public RetiredModelSwitchDialog(SettingsService settings, AIEnhancementService enhancement, EnhancementViewModel viewModel)
    {
        _settings = settings;
        _enhancement = enhancement;
        _viewModel = viewModel;
    }

    /// <summary>Shows the offer once per app version per model; a minimized start or a held dialog slot defers it to the next start without recording it. Never throws.</summary>
    /// <param name="rootAsync">The main window's XamlRoot once it can show a dialog, or null.</param>
    internal async Task MaybeShowAsync(string appVersion, bool startMinimized, Func<bool> isQuitting, Func<Task<XamlRoot?>> rootAsync)
    {
        try
        {
            // As What's new: no modal over a hidden or tray start.
            if (startMinimized || isQuitting())
                return;
            if (_viewModel.RetiredSwitch is not { } retiredSwitch || _viewModel.LocalModels is not { } store)
                return;
            var selected = _enhancement.SelectedProvider == AIProvider.OnThisPc ? _enhancement.SelectedModel : null;
            // The hardware read only when a retired model is in use: it walks the registry.
            var shown = _settings.GetString(AppDefaults.RetiredModelOfferShown, "");
            if (!_enhancement.IsEnabled || selected is null || LocalModelCatalog.Find(selected) is not { Retired: true }
                || RetiredModelOfferPolicy.WasShown(shown, RetiredModelOfferPolicy.MarkerFor(appVersion, selected)))
                return;
            var pickId = LocalModelsSection.RecommendedPick(_viewModel.OnThisPc, LocalHardwareProfile.Read());
            var offer = RetiredModelOfferPolicy.Decide(_enhancement.IsEnabled, selected, store.IsInstalled,
                pickId is null ? null : LocalModelCatalog.Find(pickId), retiredSwitch.PendingTargetFor(selected),
                appVersion, shown);
            if (offer is null)
                return;

            var root = await rootAsync();
            if (root is null || isQuitting())
                return;
            using var slot = DialogLease.MainWindow.TryAcquire("Retired model switch offer");
            if (slot is null)
                return;

            var dialog = new ContentDialog
            {
                Title = RetiredModelOfferPolicy.Title(offer),
                Content = new TextBlock
                {
                    Text = RetiredModelOfferPolicy.Body(offer),
                    FontSize = 13,
                    Foreground = AppTheme.Brush(AppTheme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = RetiredModelOfferPolicy.PrimaryButton(offer),
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
                RequestedTheme = AppTheme.ElementTheme,
                XamlRoot = root,
            };
            // Recorded as it opens, as What's new is, however it is dismissed: "Not now" asks again at the next update.
            _settings.SetString(AppDefaults.RetiredModelOfferShown, RetiredModelOfferPolicy.AddMarker(shown, appVersion, offer.Marker));
            Logger.Information("Retired model switch offered at start: {RetiredModelId} to {LocalModelId} ({Kind})",
                offer.Retired.Id, offer.Pick.Id, offer.Kind);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                Logger.Information("Retired model switch declined at start");
                return;
            }
            // The card's own path (select, then accept): its row shows the running switch, and its button stays for a later try.
            _viewModel.UseBuiltInModel(offer.Retired.Id);
            _ = retiredSwitch.Accept(offer.Retired.Id, offer.Pick.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning("Retired model switch offer failed ({ErrorType}); the AI Enhancement page still offers it", ex.GetType().Name);
        }
    }
}
