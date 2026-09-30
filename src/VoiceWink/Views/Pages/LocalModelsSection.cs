using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.LocalEngine;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Views.Pages;

/// <summary>
/// LAI-3: the AI Enhancement page's "Local AI models" card — one row per
/// <see cref="LocalModelCatalog"/> model with its size, its note, a "Recommended" tag on the tier
/// <see cref="LocalModelRecommendation"/> picks for this PC, the floor a row sits below, and
/// Download ⇄ Cancel / Delete. The page only hosts it (shown while "On this PC" is the text
/// provider, behind <see cref="OnThisPcAvailability.IsOffered"/>); every decision lives in the
/// catalog, the store, the recommendation and — LAI-4 — the engine host, which runs a finished
/// download's first-use check and stops the engine before a delete. Only plain controls the page already renders (Border, Grid, StackPanel,
/// TextBlock, ProgressBar) — no templated WinUI control this CLI build has not proven.
/// </summary>
internal static class LocalModelsSection
{
    internal static UIElement Build(LocalModelStore store, LocalHardwareProfile profile,
        OnThisPcEngine? engine = null, Action? installedChanged = null)
    {
        var recommended = LocalModelRecommendation.Recommend(profile);
        var rows = new StackPanel { Spacing = 12 };
        rows.Children.Add(AppTheme.CreateSectionHeader("Local AI models"));
        rows.Children.Add(new TextBlock
        {
            Text = recommended is not null ? "Runs AI enhancement on this PC."
                : profile.TotalRamBytes is null ? "Runs AI enhancement on this PC. This PC's memory could not be read."
                : "Runs AI enhancement on this PC. This PC has too little memory for a local model.",
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var entry in LocalModelCatalog.All)
        {
            rows.Children.Add(BuildRow(store, engine, installedChanged, entry, entry.Tier == recommended,
                LocalModelRecommendation.FloorNote(entry.Tier, profile)));
        }
        return AppTheme.CreateCard(rows);
    }

    private static UIElement BuildRow(LocalModelStore store, OnThisPcEngine? engine, Action? installedChanged,
        LocalModelEntry entry, bool isRecommended, string? floorNote)
    {
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Brush(AppTheme.TextPrimary) };
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{entry.DisplayName}  " });
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = FormatSize(entry.Model.SizeBytes),
            FontWeight = FontWeights.Normal,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        });
        if (isRecommended)
        {
            title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  Recommended",
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
            });
        }

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = entry.Note, Foreground = AppTheme.Brush(AppTheme.TextSecondary), TextWrapping = TextWrapping.Wrap });
        if (floorNote is not null)
        {
            text.Children.Add(new TextBlock { Text = floorNote, Foreground = AppTheme.Brush(AppTheme.WarningText), TextWrapping = TextWrapping.Wrap });
        }
        var status = new TextBlock { Foreground = AppTheme.Brush(AppTheme.FailureText), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        text.Children.Add(progress);
        text.Children.Add(status);

        var download = AppTheme.CreateActionToggleButton("Download", "Cancel");
        var delete = AppTheme.CreateCompactButton("Delete", isDanger: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(download.Element);
        buttons.Children.Add(delete);

        // The running download lives in the store, not here: the page rebuilds its rows, and a
        // rebuilt row must show the transfer that is still running and still be able to cancel it.
        LocalModelDownloadAttempt? watched = null;
        void Render()
        {
            var installed = store.IsInstalled(entry.Id);
            download.Element.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            delete.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        }

        void ShowStatus(string? message)
        {
            status.Text = message ?? "";
            status.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        void OnProgress(double value) => progress.Value = value;

        async void Watch(LocalModelDownloadAttempt attempt)
        {
            watched = attempt;
            download.ShowCancel();
            progress.Value = attempt.Progress;
            progress.Visibility = Visibility.Visible;
            attempt.ProgressChanged += OnProgress;
            try
            {
                var outcome = await attempt.Completion;
                if (outcome == LocalModelDownloadOutcome.RefusedDuringMaintenance)
                {
                    ShowStatus("An update or data erasure is running. Try again after it finishes.");
                }
                else if (outcome == LocalModelDownloadOutcome.Installed)
                {
                    // LAI-4: the first-use check runs now, not on the first dictation.
                    engine?.StartFirstUseCheck(entry.Id);
                    installedChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                // The user's Cancel: the partial is gone; the row returns to Download.
            }
            catch (ModelInstallBlockedException blocked)
            {
                ShowStatus(blocked.Message);   // app-authored: says why, and whether a retry can help
            }
            catch (Exception)
            {
                ShowStatus("Download failed. Try again.");   // the store logged the cause
            }
            finally
            {
                attempt.ProgressChanged -= OnProgress;
                watched = null;
                progress.Visibility = Visibility.Collapsed;
                download.ShowPrimary();
                Render();
            }
        }

        download.Element.Tapped += (_, _) =>
        {
            if (download.IsCancelling)
            {
                // A tap inside the double-click interval is the second half of the Download click,
                // not a cancel (ActionToggleCore's remarks; the Onboarding download does the same).
                if (watched is { } running && ActionToggleCore.CancelTapIsArmed(
                        TimeSpan.FromMilliseconds(Environment.TickCount64 - running.StartedTicks),
                        ActionToggleCore.ArmingWindow(NativeInterop.GetDoubleClickTime())))
                {
                    running.Cancel();
                }
                return;
            }
            ShowStatus(null);
            Watch(store.StartDownload(entry.Id));
        };

        delete.Tapped += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = $"Delete {entry.DisplayName}?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = delete.XamlRoot,
                RequestedTheme = AppTheme.ElementTheme,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            // LAI-4: the engine is stopped first — Windows refuses to delete a model it has mapped.
            var gone = engine is null
                ? await Task.Run(() => store.Delete(entry.Id))
                : await Task.Run(() => engine.DeleteModelAsync(entry.Id, () => store.Delete(entry.Id)));
            ShowStatus(gone ? null : "Couldn't delete — the file is in use.");
            Render();
            installedChanged?.Invoke();
        };

        Render();
        if (store.ActiveDownload(entry.Id) is { } alreadyRunning)
        {
            Watch(alreadyRunning);
        }
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>Decimal gigabytes with one decimal ("2.7 GB"), what download sizes are quoted in.</summary>
    internal static string FormatSize(long bytes) => $"{bytes / 1_000_000_000.0:0.0} GB";
}
