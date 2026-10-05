using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.AIEnhancement.LocalEngine;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Views.Pages;

/// <summary>
/// The built-in AI models, one card per model like the Models page's built-in transcription models
/// (owner, 2026-10-04: the single card holding every row read as one big block): a status icon, name
/// and size, the Accuracy / Speed stars, and Download ⇄ Cancel, Select / Active, Delete on the right.
/// Shown on the AI Enhancement page under "Built-in Models" and in setup's AI step.
/// <para>The rows follow <see cref="LocalModelRecommendation.DisplayOrder"/> (most accurate first); the
/// model the recommendation picks for this PC is tagged "Best for this PC" in place. Every decision lives in the catalog, the
/// store, the recommendation and the engine host — a finished download starts its first-use check,
/// a delete stops the engine first. Only plain controls the page already renders (Border, Grid,
/// StackPanel, TextBlock, ProgressBar).</para>
/// </summary>
internal static class LocalModelsSection
{
    /// <param name="isSelected">Whether a model id is the provider's selected model.</param>
    /// <param name="select">Makes an installed model the selected one.</param>
    /// <param name="changed">A download finished (its model id) or a model was deleted (null).</param>
    /// <param name="measured">A first-use check settled: its time now decides the stars and the pick.</param>
    /// <param name="notice">The page's "speed ratings were updated" tracker; null shows no line.</param>
    /// <param name="setupInUse">Setup's AI step: shows only the model for this PC plus the models this
    /// says are in use, not all four. Null shows every model.</param>
    internal static UIElement Build(LocalModelStore store, LocalHardwareProfile profile,
        Func<string, bool> isSelected, Action<string> select,
        OnThisPcEngine? engine = null, Action<string?>? changed = null, Action? measured = null,
        SpeedRatingsNotice.Tracker? notice = null, Func<string, bool>? setupInUse = null)
    {
        // Captured BEFORE the measurements are read, so a timing stored in between is not missed.
        var nextSpeedMeasured = engine?.NextSpeedMeasured;
        // Running checks captured BEFORE their results are read, so a settle in between is not missed.
        var runningChecks = LocalModelCatalog.All.ToDictionary(e => e.Tier,
            e => engine is not null && store.IsInstalled(e.Id) ? engine.WhenFirstUseCheckSettledAsync(e.Id) : null);
        // The speed this PC measured for each installed model; null where nothing was measured.
        // A deleted model's measurement still calibrates the others: deleting it must not move them.
        var measuredMs = LocalModelCatalog.All.ToDictionary(e => e.Tier,
            e => engine?.MeasuredSpeedMs(e.Id));
        // Every row on one scale for THIS PC (owner rule, 2026-10-03): measured where a check ran,
        // estimated from the PC type and calibrated by the measured rows everywhere else.
        var arm64 = ModelRatings.IsArm64Os;
        // GPU acceleration off: the graphics card plays no part, so neither estimates nor floors count it.
        var speedProfile = engine?.RunsOnProcessor == true ? profile with { LargestDedicatedVramBytes = null } : profile;
        var speeds = LocalModelRecommendation.Speeds(speedProfile, arm64, tier => measuredMs[tier]);
        var recommended = LocalModelRecommendation.Recommend(speedProfile, tier => measuredMs[tier], arm64);
        // The Models page's card spacing.
        var rows = new StackPanel { Spacing = 12 };
        if (notice?.Show(
                SpeedRatingsNotice.Fingerprint(LocalModelCatalog.All.Select(e =>
                    (e.Id, LocalModelRecommendation.MeasuredSpeedStars(speeds[e.Tier].Ms)))),
                // The build before this one: the measured star where a check had timed it, the
                // catalog's fixed star otherwise.
                SpeedRatingsNotice.Fingerprint(LocalModelCatalog.All.Select(e =>
                    (e.Id, measuredMs[e.Tier] is int ms ? LocalModelRecommendation.MeasuredSpeedStars(ms) : e.Speed)))) == true)
        {
            rows.Children.Add(Caption(SpeedRatingsNotice.Text, AppTheme.TextSecondary));
        }
        if (recommended is null)
        {
            rows.Children.Add(Caption(
                profile.TotalRamBytes is null ? "This PC's memory could not be read."
                : LocalModelCatalog.All.Any(e => LocalModelRecommendation.Fits(e.Tier, profile))
                    ? "Built-in AI is slow on this PC. A cloud provider is recommended."
                    : "Not enough memory for built-in AI. A cloud provider is recommended.",
                AppTheme.WarningText));
        }

        if (measured is not null && nextSpeedMeasured is { } speedMeasured)
            RefreshWhenMeasured(speedMeasured, measured);

        var ordered = setupInUse is not null
            ? LocalModelRecommendation.SetupOffer(recommended, setupInUse)
            : LocalModelRecommendation.DisplayOrder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var speed = speeds[entry.Tier];
            // A measured model ran here, so its floor no longer applies; a slow one says so. An
            // unmeasured one shows its floor first, then whether it is likely too slow here.
            var note = speed.Measured
                ? (LocalModelRecommendation.IsTooSlow(speed.Ms) ? "Slow on this PC." : null)
                : LocalModelRecommendation.FloorNote(entry.Tier, speedProfile)
                  ?? (LocalModelRecommendation.IsTooSlow(speed.Ms) ? "Likely slow on this PC." : null);
            rows.Children.Add(BuildRow(store, engine, changed, measured, entry, entry.Tier == recommended,
                note, speed, isSelected, select));
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
    };

    private static UIElement BuildRow(LocalModelStore store, OnThisPcEngine? engine, Action<string?>? changed, Action? measured,
        LocalModelEntry entry, bool isRecommended, string? floorNote, LocalModelRecommendation.TierSpeed tierSpeed,
        Func<string, bool> isSelected, Action<string> select)
    {
        // ── Left: the Models page's status icon, then name + size and the stars line ──
        var iconGlyph = new FontIcon
        {
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = iconGlyph,
        };

        // Wraps: setup's narrower page leaves the name less room beside a running download (Codex diff r1).
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Brush(AppTheme.TextPrimary), TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.DisplayName });
        title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = $"  {FormatSize(entry.Model.SizeBytes)}",
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
        });
        if (isRecommended)
        {
            title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  Best for this PC",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen),
            });
        }

        var speed = LocalModelRecommendation.MeasuredSpeedStars(tierSpeed.Ms);
        var ratings = new TextBlock
        {
            Text = $"Accuracy {ModelRatings.Stars(entry.Accuracy)} · Speed {ModelRatings.Stars(speed)}",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            Margin = new Thickness(0, 2, 0, 0),
        };
        ToolTipService.SetToolTip(ratings, ModelRatings.SpeedTooltip(
            tierSpeed.Measured ? ModelRatings.SpeedBasis.Measured : ModelRatings.SpeedBasis.Estimated));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(ratings);
        if (floorNote is not null)
        {
            text.Children.Add(new TextBlock { Text = floorNote, FontSize = 12, Foreground = AppTheme.Brush(AppTheme.WarningText), TextWrapping = TextWrapping.Wrap });
        }

        var left = new Grid();
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        left.Children.Add(icon);
        left.Children.Add(text);

        // ── Right: the Models page's actions ──
        var progress = new ProgressBar
        {
            Width = 100,
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            CornerRadius = new CornerRadius(3),
            Foreground = AppTheme.Brush(AppTheme.AccentBlue),
            Background = AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
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
        buttons.Children.Add(active);
        buttons.Children.Add(selectBtn);
        buttons.Children.Add(delete);
        buttons.Children.Add(progress);
        buttons.Children.Add(download.Element);

        var status = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.FailureText),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var card = new Border
        {
            Background = AppTheme.Brush(AppTheme.CardBg),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
            Padding = new Thickness(AppTheme.CardPadding, 16, AppTheme.CardPadding, 16),
        };

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
            // Delete beside Select, as on the Models page; an Active model is switched away from first.
            delete.Visibility = installed && !chosen ? Visibility.Visible : Visibility.Collapsed;
            // The Models page's status icon: green check in use, blue check downloaded, grey arrow not.
            iconGlyph.Glyph = installed ? "\uE73E" : "\uE896";
            iconGlyph.Foreground = AppTheme.Brush(chosen ? AppTheme.AccentGreen : installed ? AppTheme.AccentBlue : AppTheme.SubtleText);
            icon.Background = chosen
                ? AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88))
                : installed
                    ? AppTheme.Brush(AppTheme.ActivePillBg)
                    : AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147));
            card.BorderBrush = chosen
                ? AppTheme.Brush(ColorHelper.FromArgb(80, 48, 209, 88))
                : AppTheme.Brush(AppTheme.CardBorderColor);
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
                    changed?.Invoke(entry.Id);
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
            changed?.Invoke(null);
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
        grid.Children.Add(left);
        grid.Children.Add(buttons);
        card.Child = new StackPanel { Children = { grid, status } };
        return card;
    }

    /// <summary>Decimal gigabytes with one decimal ("2.7 GB"), what download sizes are quoted in.</summary>
    internal static string FormatSize(long bytes) => $"{bytes / 1_000_000_000.0:0.0} GB";
}
