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
    /// says are in use, not every model. Null shows every model.</param>
    /// <param name="whenSetupOffersNothing">Setup's AI step: what to show under the note when no model is the pick and none is in use (LAI-19).</param>
    /// <param name="retiredSwitch">The AI Enhancement page: a retired model in use offers this PC's pick (LAI-22). Null offers nothing.</param>
    internal static UIElement Build(LocalModelStore store, LocalHardwareProfile profile,
        Func<string, bool> isSelected, Action<string> select,
        OnThisPcEngine? engine = null, Action<string?>? changed = null, Action? measured = null,
        SpeedRatingsNotice.Tracker? notice = null, Func<string, bool>? setupInUse = null,
        Func<UIElement>? whenSetupOffersNothing = null, RetiredModelSwitch? retiredSwitch = null)
    {
        // Captured BEFORE the measurements are read, so a timing stored in between is not missed.
        var nextSpeedMeasured = engine?.NextSpeedMeasured;
        // Running checks captured BEFORE their results are read, so a settle in between is not missed.
        var runningChecks = LocalModelCatalog.All.ToDictionary(e => e.Tier,
            e => engine is not null && store.IsInstalled(e.Id) ? engine.WhenFirstUseCheckSettledAsync(e.Id) : null);
        var (speedProfile, measuredMs, processorOnly) = ProfileAndMeasurements(engine, profile);
        // Every row on one scale for THIS PC (owner rule, 2026-10-03): measured where a check ran,
        // estimated from the PC type and calibrated by the measured rows everywhere else.
        var arm64 = ModelRatings.IsArm64Os;
        var speeds = LocalModelRecommendation.Speeds(speedProfile, arm64, tier => measuredMs[tier], processorOnly);
        var recommended = LocalModelRecommendation.Recommend(speedProfile, tier => measuredMs[tier], arm64, processorOnly);
        // The Models page's card spacing.
        var rows = new StackPanel { Spacing = 12 };
        // LAI-22: only the rows the list can show; a hidden retired row's star must not announce a change.
        var listed = LocalModelCatalog.All
            .Where(e => LocalModelRecommendation.IsListed(e, store.IsInstalled(e.Id)))
            .ToList();
        if (notice?.Show(
                SpeedRatingsNotice.Fingerprint(listed.Select(e =>
                    (e.Id, LocalModelRecommendation.MeasuredSpeedStars(speeds[e.Tier].Ms)))),
                // The build before this one: the measured star where a check had timed it, the
                // catalog's fixed star otherwise.
                SpeedRatingsNotice.Fingerprint(listed.Select(e =>
                    (e.Id, measuredMs[e.Tier] is int ms ? LocalModelRecommendation.MeasuredSpeedStars(ms) : e.Speed)))) == true)
        {
            rows.Children.Add(Caption(SpeedRatingsNotice.Text, AppTheme.TextSecondary));
        }
        if (recommended is null)
        {
            rows.Children.Add(Caption(LocalModelRecommendation.NoPickNote(profile, tier => measuredMs[tier]), AppTheme.WarningText));
        }

        if (measured is not null && nextSpeedMeasured is { } speedMeasured)
            RefreshWhenMeasured(speedMeasured, measured);

        var ordered = (setupInUse is not null
                ? LocalModelRecommendation.SetupOffer(recommended, setupInUse)
                : LocalModelRecommendation.DisplayOrder())
            .Where(e => LocalModelRecommendation.IsListed(e, store.IsInstalled(e.Id)))
            .ToList();
        var pick = recommended is { } pickTier ? LocalModelCatalog.All.First(e => e.Tier == pickTier) : null;
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
            // Setup shows stars only, and a model this PC cannot run well shows its note alone (owner, LAI-19).
            rows.Children.Add(BuildRow(store, engine, changed, measured, entry, entry.Tier == recommended,
                note, setupInUse is null && note is null ? LocalModelAdvice.CardLine(entry) : null, speed, isSelected, select,
                retiredSwitch, pick));
            if (measured is not null && runningChecks[entry.Tier] is { } running)
                RefreshWhenMeasured(running, measured);
        }
        if (setupInUse is not null && ordered.Count == 0 && whenSetupOffersNothing is not null)
            rows.Children.Add(whenSetupOffersNothing());
        return rows;
    }

    /// <summary>
    /// The built-in model recommended for this PC, measured where a check ran — or null when none
    /// is (under the memory floor, memory unreadable, or too slow). Express setup downloads only
    /// this (LNC-15), so it never puts a model that does not fit on a PC.
    /// </summary>
    internal static string? RecommendedPick(OnThisPcEngine? engine, LocalHardwareProfile profile)
    {
        var (speedProfile, measuredMs, processorOnly) = ProfileAndMeasurements(engine, profile);
        var tier = LocalModelRecommendation.Recommend(speedProfile, t => measuredMs[t], ModelRatings.IsArm64Os, processorOnly);
        return tier is { } t ? LocalModelCatalog.All.First(e => e.Tier == t).Id : null;
    }

    /// <summary>
    /// The profile the speed estimates and floors read, and the speed this PC measured for each
    /// model (null where nothing was measured). On the processor (GPU acceleration off, or no graphics
    /// card can serve it — LAI-18b) the card plays no part, so neither estimates nor floors count it.
    /// A deleted model's measurement still calibrates the others: deleting it must not move them.
    /// </summary>
    private static (LocalHardwareProfile SpeedProfile, IReadOnlyDictionary<LocalModelTier, int?> MeasuredMs, bool ProcessorOnly)
        ProfileAndMeasurements(OnThisPcEngine? engine, LocalHardwareProfile profile)
    {
        var processorOnly = LocalModelRecommendation.RunsOnProcessor(
            engine?.RunsOnProcessor == true, GpuToggleAvailability.Current, ModelRatings.IsArm64Os);
        return (processorOnly ? profile with { LargestDedicatedVramBytes = null } : profile,
            LocalModelCatalog.All.ToDictionary(e => e.Tier, e => engine?.MeasuredSpeedMs(e.Id)),
            processorOnly);
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
        LocalModelEntry entry, bool isRecommended, string? floorNote, string? adviceLine, LocalModelRecommendation.TierSpeed tierSpeed,
        Func<string, bool> isSelected, Action<string> select, RetiredModelSwitch? retiredSwitch, LocalModelEntry? pick)
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
            Text = $"  {FormatSize(entry.Model.SizeBytes).Replace(' ', '\u00A0')}",
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
        });
        if (isRecommended)
        {
            title.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                // Non-breaking spaces: on a narrow page the tag moves to the next line whole
                // instead of splitting after "this" (owner, 2026-10-09).
                Text = "  Best\u00A0for\u00A0this\u00A0PC",
                FontSize = 12,
                Foreground = AppTheme.Brush(AppTheme.AccentGreenText),
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
        if (adviceLine is not null)
        {
            text.Children.Add(new TextBlock { Text = adviceLine, FontSize = 12, Foreground = AppTheme.Brush(AppTheme.TextSecondary), TextWrapping = TextWrapping.Wrap });
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
            Foreground = AppTheme.Brush(AppTheme.AccentBlueText),
            Background = AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var download = AppTheme.CreateActionToggleButton("Download", "Cancel", compact: true);
        var selectBtn = AppTheme.CreateCompactButton("Select");
        selectBtn.MinWidth = AppTheme.ModelCardControlMinWidth;
        var active = AppTheme.CreateActiveStatusBadge();
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

        // LAI-22: a retired model in use offers this PC's pick; one button per label, shown by state.
        var offerLine = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var switchBtn = AppTheme.CreateCompactButton("Switch");
        var downloadAndSwitchBtn = AppTheme.CreateCompactButton("Download and switch");
        var offerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        offerButtons.Children.Add(switchBtn);
        offerButtons.Children.Add(downloadAndSwitchBtn);
        var offer = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        offer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(offerButtons, 1);
        offer.Children.Add(offerLine);
        offer.Children.Add(offerButtons);

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
            download.Element.Visibility = LocalModelRecommendation.OffersDownload(entry, installed) ? Visibility.Visible : Visibility.Collapsed;
            // LAI-22: a deleted retired model leaves the list at once; the page may not rebuild its rows.
            card.Visibility = LocalModelRecommendation.IsListed(entry, installed) ? Visibility.Visible : Visibility.Collapsed;
            selectBtn.Visibility = installed && !chosen ? Visibility.Visible : Visibility.Collapsed;
            active.Visibility = chosen ? Visibility.Visible : Visibility.Collapsed;
            // Delete beside Select, as on the Models page; an Active model is switched away from first.
            delete.Visibility = installed && !chosen ? Visibility.Visible : Visibility.Collapsed;
            // The Models page's status icon: green check in use, blue check downloaded, grey arrow not.
            iconGlyph.Glyph = installed ? "\uE73E" : "\uE896";
            ToolTipService.SetToolTip(icon, AppTheme.ModelStatusTooltip(chosen, installed));
            iconGlyph.Foreground = AppTheme.Brush(chosen ? AppTheme.AccentGreenText : installed ? AppTheme.AccentBlueText : AppTheme.SubtleText);
            icon.Background = chosen
                ? AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88))
                : installed
                    ? AppTheme.Brush(AppTheme.ActivePillBg)
                    : AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147));
            card.BorderBrush = chosen
                ? AppTheme.Brush(ColorHelper.FromArgb(80, 48, 209, 88))
                : AppTheme.Brush(AppTheme.CardBorderColor);
            var offered = retiredSwitch is null ? default : RetiredModelSwitch.Offer(entry, chosen, pick,
                pick is not null && store.IsInstalled(pick.Id),
                retiredSwitch.PendingTargetFor(entry.Id) is { } target ? LocalModelCatalog.Find(target) : null);
            offer.Visibility = offered.Kind == RetiredOfferKind.None ? Visibility.Collapsed : Visibility.Visible;
            offerLine.Text = offered.Line ?? "";
            switchBtn.Visibility = offered.Kind == RetiredOfferKind.Switch ? Visibility.Visible : Visibility.Collapsed;
            downloadAndSwitchBtn.Visibility = offered.Kind == RetiredOfferKind.DownloadAndSwitch ? Visibility.Visible : Visibility.Collapsed;
        }

        // LAI-22: a switch not made shows the offer again and why; the switch keeps the reason for a rebuilt row.
        async void WatchSwitch(Task<RetiredSwitchResult> run)
        {
            await run;   // never faults: the switch reports every outcome as its result
            Render();
            var lastStatus = retiredSwitch?.LastStatusFor(entry.Id, pick?.Id ?? "");
            ShowStatus(offer.Visibility == Visibility.Visible ? lastStatus : null);
        }

        void AcceptOffer()
        {
            if (retiredSwitch is null || pick is null)
                return;
            // LAI-22: a cloud provider shown in the box without a key is not stored; the click says "built-in".
            select(entry.Id);
            ShowStatus(null);
            WatchSwitch(retiredSwitch.Accept(entry.Id, pick.Id));
            Render();
            // Still running: the pick's own row attaches to the download this started.
            if (retiredSwitch.PendingRunFor(entry.Id) is not null)
                measured?.Invoke();
        }
        switchBtn.Click += (_, _) => AcceptOffer();
        downloadAndSwitchBtn.Click += (_, _) => AcceptOffer();

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

        download.Element.Click += (_, _) =>
        {
            if (download.IsCancelling)
            {
                // A tap inside the double-click interval is the second half of the Download click,
                // not a cancel (ActionToggleCore's remarks; the Downloads card does the same).
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

        selectBtn.Click += (_, _) =>
        {
            select(entry.Id);
            Render();
        };

        delete.Click += async (_, _) =>
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
            if (await DialogLease.MainWindow.ShowAsync(dialog, "Delete built-in model") != ContentDialogResult.Primary)
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
        if (retiredSwitch?.PendingRunFor(entry.Id) is { } pendingSwitch)
        {
            WatchSwitch(pendingSwitch);
        }
        else if (retiredSwitch?.LastStatusFor(entry.Id, pick?.Id ?? "") is { } lastStatus && offer.Visibility == Visibility.Visible)
        {
            ShowStatus(lastStatus);   // LAI-22: the rebuild that follows a first-use check must not wipe the reason
        }
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(left);
        grid.Children.Add(buttons);
        card.Child = new StackPanel { Children = { grid, offer, status } };
        return card;
    }

    /// <summary>Decimal gigabytes with one decimal ("2.7 GB"), what download sizes are quoted in.</summary>
    internal static string FormatSize(long bytes) => $"{bytes / 1_000_000_000.0:0.0} GB";
}
