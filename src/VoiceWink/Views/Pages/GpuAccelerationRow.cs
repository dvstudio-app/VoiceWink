using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;
using VoiceWink.Views.Dialogs;

namespace VoiceWink.Views.Pages;

/// <summary>
/// The "GPU acceleration" switch, shared by the Models page and the AI Enhancement page's built-in
/// models (owner, 2026-10-04: one setting, shown where each kind of built-in model is chosen). The
/// one stored preference drives both the speech engines and the bundled AI engine, read once at
/// start, so a flip offers the restart that applies it.
/// </summary>
internal static class GpuAccelerationRow
{
    private static ILogger Logger => Log.ForContext(typeof(GpuAccelerationRow));

    /// <summary>The switch row. The displayed state goes INTO the factory: setting <c>IsOn</c>
    /// afterwards would fire <c>Toggled</c> and write the preference.</summary>
    internal static Border Build(GpuTogglePresentation presentation, Action<bool> onToggled)
    {
        var row = AppTheme.CreateToggleSetting(
            "GPU acceleration",
            presentation.Description,
            presentation.IsOn,
            onToggled,
            out var toggle);
        toggle.IsEnabled = presentation.Enabled;
        AppTheme.StripCardBorder(row);
        return row;
    }

    /// <summary>
    /// TRN-59: the switch's own change event — the ONLY trigger of the restart offer, so a disabled
    /// row (which never raises it) never offers a restart, and "Reset all settings" (which writes
    /// through the preference, not the switch) never prompts. The write goes first
    /// (<c>GpuAccelerationPreference.Write</c>: persist, log, re-arm the self-test); the offer follows
    /// only when that write CHANGED the stored value.
    /// </summary>
    /// <param name="afterWrite">Runs after the write and before the offer (the Models page refreshes
    /// the lines under its switch).</param>
    internal static void Apply(bool isOn, Func<bool> read, Action<bool> write, AppRestartService? restart,
        Func<XamlRoot?> xamlRoot, Action? afterWrite = null)
    {
        var changed = read() != isOn;
        write(isOn);
        afterWrite?.Invoke();
        if (!changed || restart is null) return;
        _ = OfferRestartAsync(restart, isOn, xamlRoot());
    }

    /// <summary>
    /// Opens the "Restart now / Later" dialog. The setting is already written when this runs; the
    /// dialog only decides WHEN it applies. Fail-soft: a second ContentDialog already open throws,
    /// and that must not surface.
    /// </summary>
    private static async Task OfferRestartAsync(AppRestartService restart, bool isOn, XamlRoot? xamlRoot)
    {
        try
        {
            var dialog = new RestartToApplyDialog(restart, isOn) { XamlRoot = xamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not offer the restart after the GPU acceleration flip; the change applies at the next start");
        }
    }
}
