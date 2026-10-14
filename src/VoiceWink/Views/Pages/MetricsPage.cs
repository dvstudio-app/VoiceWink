using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Data;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Metrics dashboard — stats, usage counts, transcription volume.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class MetricsPage : Page
{
    private static ILogger Logger => Log.ForContext<MetricsPage>();

    // The time-saved model lives in TimeSavedEstimate (2026-10-14): Home shows the same number.
    // The info line below STATES the typing speed from that constant, so the two never drift.

    private readonly LifetimeMetricsService _metrics;
    private TextBlock? _totalCount;
    private TextBlock? _todayCount;
    private TextBlock? _totalChars;
    private TextBlock? _timeSaved;
    private TextBlock? _avgWords;
    private TextBlock? _wordsCaptured;
    private bool _subscriptionsAttached;

    public MetricsPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        _metrics = App.Services.GetRequiredService<LifetimeMetricsService>();
        BuildUI();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void BuildUI()
    {
        // Page header
        var header = AppTheme.CreatePageHeader("Metrics", "Track your transcription usage and productivity");

        // ── Primary stat cards ──────────────────────────────────────
        var (totalCard, totalValue) = AppTheme.CreateStatCard("Total Transcriptions", "0", "\uE8D3");  // Document icon
        var (todayCard, todayValue) = AppTheme.CreateStatCard("Today", "0", "\uE787");                  // Calendar icon
        var (charsCard, charsValue) = AppTheme.CreateStatCard("Characters Captured", "0", "\uE8C1");    // Font icon

        _totalCount = totalValue;
        _todayCount = todayValue;
        _totalChars = charsValue;

        // ── Derived stat cards ──────────────────────────────────────
        var (timeSavedCard, timeSavedValue) = AppTheme.CreateStatCard("Time Saved", "0 min", "\uE823");      // Clock icon
        var (avgWordsCard, avgWordsValue) = AppTheme.CreateStatCard("Avg Words/Session", "0", "\uE9D9");     // Processing icon
        var (wordsCapturedCard, wordsCapturedValue) = AppTheme.CreateStatCard("Words Captured", "0", "\uE8F2"); // Chat icon

        _timeSaved = timeSavedValue;
        _avgWords = avgWordsValue;
        _wordsCaptured = wordsCapturedValue;

        // ── Primary stats row ───────────────────────────────────────
        var primaryStatsHeader = AppTheme.CreateSectionHeader("Overview");

        // Use Grid with equal star columns so cards shrink gracefully on narrow windows
        totalCard.MinWidth = 120;
        todayCard.MinWidth = 120;
        charsCard.MinWidth = 120;
        var primaryStatsPanel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        primaryStatsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primaryStatsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primaryStatsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totalCard.Margin = new Thickness(0, 0, 8, 0);
        todayCard.Margin = new Thickness(4, 0, 4, 0);
        charsCard.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(totalCard, 0);
        Grid.SetColumn(todayCard, 1);
        Grid.SetColumn(charsCard, 2);
        primaryStatsPanel.Children.Add(totalCard);
        primaryStatsPanel.Children.Add(todayCard);
        primaryStatsPanel.Children.Add(charsCard);

        // ── Productivity stats row ──────────────────────────────────
        var productivityHeader = AppTheme.CreateSectionHeader("Productivity");

        timeSavedCard.MinWidth = 120;
        avgWordsCard.MinWidth = 120;
        wordsCapturedCard.MinWidth = 120;
        var productivityPanel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        productivityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        productivityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        productivityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeSavedCard.Margin = new Thickness(0, 0, 8, 0);
        avgWordsCard.Margin = new Thickness(4, 0, 4, 0);
        wordsCapturedCard.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(timeSavedCard, 0);
        Grid.SetColumn(avgWordsCard, 1);
        Grid.SetColumn(wordsCapturedCard, 2);
        productivityPanel.Children.Add(timeSavedCard);
        productivityPanel.Children.Add(avgWordsCard);
        productivityPanel.Children.Add(wordsCapturedCard);

        // ── Info note (below header) ──────────────────────────────
        var infoIcon = new FontIcon
        {
            Glyph = "\uE946",  // Info icon
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            Margin = new Thickness(0, 1, 6, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        var infoText = new TextBlock
        {
            Text = $"Time saved compares typing at {TimeSavedEstimate.TypingWpm:F0} words a minute with dictation, " +
                   "end-to-end. Totals are lifetime stats and do not shrink when transcription " +
                   "history is cleared.",
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.DimText),
            TextWrapping = TextWrapping.Wrap
        };

        var infoRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(infoIcon, 0);
        Grid.SetColumn(infoText, 1);
        infoRow.Children.Add(infoIcon);
        infoRow.Children.Add(infoText);

        // ── Assemble page ───────────────────────────────────────────
        var pageContent = new StackPanel
        {
            Children =
            {
                header,
                infoRow,
                primaryStatsHeader,
                primaryStatsPanel,
                productivityHeader,
                productivityPanel
            }
        };

        AppTheme.SetPageScrollContent(this, pageContent);
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            var stats = await _metrics.GetAggregateStatsAsync();

            var avgWordsPerSession = stats.Total > 0 ? stats.TotalWords / stats.Total : 0;

            // Time saved: full end-to-end comparison (typing vs voice, including editing)
            var minutesSaved = TimeSavedEstimate.Minutes(stats.TotalWords, stats.Total);

            DispatcherQueue.TryEnqueue(() =>
            {
                _totalCount!.Text = stats.Total.ToString("N0");
                _todayCount!.Text = stats.Today.ToString("N0");
                _totalChars!.Text = stats.TotalCharacters.ToString("N0");
                _wordsCaptured!.Text = stats.TotalWords.ToString("N0");
                _avgWords!.Text = avgWordsPerSession.ToString("N0");

                _timeSaved!.Text = TimeSavedEstimate.CardText(minutesSaved);
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load metrics");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscriptionsAttached)
        {
            _metrics.MetricsChanged += OnMetricsChanged;
            _subscriptionsAttached = true;
        }

        _ = LoadStatsAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscriptionsAttached)
            return;

        _metrics.MetricsChanged -= OnMetricsChanged;
        _subscriptionsAttached = false;
    }

    private void OnMetricsChanged()
    {
        _ = LoadStatsAsync();
    }
}
