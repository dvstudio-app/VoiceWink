using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.LocalEngine;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Views.Pages;

/// <summary>
/// The VoiceWink Engine's model rows, shown INSIDE the Text Enhancement card in place of the Model
/// dropdown while that provider is selected (owner, 2026-09-30 — the separate "Local AI models" card
/// read as disconnected from the choice it serves). Rows follow the Models page: name and size, the
/// Accuracy / Speed stars, and Download ⇄ Cancel, Select / Active, Delete.
/// <para>The model <see cref="LocalModelRecommendation"/> picks for this PC comes first, tagged "Best
/// for this PC"; the others follow under "Other models". Every decision lives in the catalog, the
/// store, the recommendation and the engine host — a finished download starts its first-use check,
/// a delete stops the engine first. Only plain controls the page already renders (Border, Grid,
/// StackPanel, TextBlock, ProgressBar).</para>
/// </summary>
internal static class LocalModelsSection
{
    /// <param name="isSelected">Whether a model id is the provider's selected model.</param>
    /// <param name="select">Makes an installed model the selected one.</param>
    /// <param name="changed">A download finished or a model was deleted.</param>
    /// <param name="measured">A first-use check settled: its time now decides the stars and the pick.</param>
    internal static UIElement Build(LocalModelStore store, LocalHardwareProfile profile,
        Func<string, bool> isSelected, Action<string> select,
        OnThisPcEngine? engine = null, Action? changed = null, Action? measured = null)
    {
        // Running checks captured BEFORE their results are read, so a settle in between is not missed.
        var runningChecks = LocalModelCatalog.All.ToDictionary(e => e.Tier,
            e => engine is not null && store.IsInstalled(e.Id) ? engine.WhenFirstUseCheckSettledAsync(e.Id) : null);
        // The speed this PC measured for each installed model; null where nothing was measured.
        var measuredMs = LocalModelCatalog.All.ToDictionary(e => e.Tier,
            e => engine is not null && store.IsInstalled(e.Id) ? engine.MeasuredSpeedMs(e.Id) : null);
        var recommended = LocalModelRecommendation.Recommend(profile, tier => measuredMs[tier]);
        var rows = new StackPanel { Spacing = 4 };
        if (recommended is null)
        {
            rows.Children.Add(Caption(
                profile.TotalRamBytes is null ? "This PC's memory could not be read."
                : LocalModelCatalog.All.Any(e => LocalModelRecommendation.Fits(e.Tier, profile))
                    ? "Built-in AI is slow on this PC. A cloud provider is recommended."
                    : "Not enough memory for built-in AI. A cloud provider is recommended.",
                AppTheme.WarningText));
        }

        var ordered = LocalModelRecommendation.DisplayOrder(recommended);
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (recommended is not null && i == 1)
                rows.Children.Add(Caption("Other models", AppTheme.SubtleText));
            var ms = measuredMs[entry.Tier];
            // A measured model ran here, so its floor no longer applies; a slow one says so.
            var note = ms is not null
                ? (LocalModelRecommendation.IsTooSlow(ms) ? "Slow on this PC." : null)
                : LocalModelRecommendation.FloorNote(entry.Tier, profile);
            rows.Children.Add(BuildRow(store, engine, changed, measured, entry, entry.Tier == recommended,
                note, ms, isSelected, select));
            if (measured is not null && runningChecks[entry.Tier] is { } running)
                RefreshWhenMeasured(running, measured);
        }
        return rows;
    }

    /// <summary>Rebuilds the rows once a running first-use check has settled, so its time shows.</summary>
    private static async void RefreshWhenMeasured(Task running, Action measured)
    {
        try
        {
            await running;
        }
        catch
        {
            return;
        }
        measured();
    }

    private static TextBlock Caption(string text, Windows.UI.Color color) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = AppTheme.Brush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private static UIElement BuildRow(LocalModelStore store, OnThisPcEngine? engine, Action? changed, Action? measured,
        LocalModelEntry entry, bool isRecommended, string? floorNote, int? measuredMs,
        Func<string, bool> isSelected, Action<string> select)
    {
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Brush(AppTheme.TextPrimary) };
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
                Text = "  Best for this PC",
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
            });
        }

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        var speed = measuredMs is { } ms ? LocalModelRecommendation.MeasuredSpeedStars(ms) : entry.Speed;
        var ratings = new TextBlock
        {
            Text = $"Accuracy {ModelRatings.Stars(entry.Accuracy)} · Speed {ModelRatings.Stars(speed)}",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
        };
        if (measuredMs is not null)
            ToolTipService.SetToolTip(ratings, "Speed measured on this PC.");
        text.Children.Add(ratings);
        if (floorNote is not null)
        {
            text.Children.Add(new TextBlock { Text = floorNote, FontSize = 12, Foreground = AppTheme.Brush(AppTheme.WarningText), TextWrapping = TextWrapping.Wrap });
        }
        var status = new TextBlock { FontSize = 12, Foreground = AppTheme.Brush(AppTheme.FailureText), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        text.Children.Add(progress);
        text.Children.Add(status);

        var download = AppTheme.CreateActionToggleButton("Download", "Cancel");
        var selectBtn = AppTheme.CreateCompactButton("Select");
        selectBtn.MinWidth = 80;
        var active = new Border
        {
            Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            MinWidth = 80,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Active",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var delete = AppTheme.CreateCompactButton("Delete", isDanger: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(download.Element);
        buttons.Children.Add(selectBtn);
        buttons.Children.Add(active);
        buttons.Children.Add(delete);

        // The running download lives in the store, not here: the page rebuilds its rows, and a
        // rebuilt row must show the transfer that is still running and still be able to cancel it.
        LocalModelDownloadAttempt? watched = null;
        void Render()
        {
            var installed = store.IsInstalled(entry.Id);
            var chosen = installed && isSelected(entry.Id);
            download.Element.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            selectBtn.Visibility = installed && !chosen ? Visibility.Visible : Visibility.Collapsed;
            active.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
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
                    if (measured is not null && engine?.WhenFirstUseCheckSettledAsync(entry.Id) is { } running)
                        RefreshWhenMeasured(running, measured);
                    changed?.Invoke();
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

        selectBtn.Tapped += (_, _) =>
        {
            select(entry.Id);
            Render();
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
            changed?.Invoke();
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
        // The Models page's row separator.
        return new Border
        {
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 4),
            Child = grid,
        };
    }

    /// <summary>Decimal gigabytes with one decimal ("2.7 GB"), what download sizes are quoted in.</summary>
    internal static string FormatSize(long bytes) => $"{bytes / 1_000_000_000.0:0.0} GB";
}
