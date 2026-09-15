using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Live log viewer -- tails the current Serilog log file and displays new entries in real time.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class LogViewerPage : Page
{
    private static ILogger Logger => Log.ForContext<LogViewerPage>();

    private RichTextBlock _logBlock = null!;
    private ScrollViewer _scrollViewer = null!;
    private readonly DispatcherTimer _tailTimer;
    private string? _currentLogPath;
    private long _lastPosition;
    private bool _autoScroll = true;

    public LogViewerPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        Background = AppTheme.Brush(AppTheme.ContentBg);
        BuildUI();

        _tailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tailTimer.Tick += OnTailTick;

        Loaded += (_, _) =>
        {
            _logBlock.Blocks.Clear();
            _lastPosition = 0;
            FindCurrentLogFile();
            ReadFullLog();
            _tailTimer.Start();
        };

        Unloaded += (_, _) =>
        {
            _tailTimer.Stop();
        };
    }

    private void BuildUI()
    {
        var header = AppTheme.CreatePageHeader("Log Viewer", "Live view of VoiceWink log output");

        // Copy rule for every toggle on this page (owner, 2026-08-04): state what the switch
        // DOES, as dryly as possible. No rationale, no "to help diagnose…", no reassurance —
        // the log pane is the point of the page and these descriptions were eating it.
        var autoScrollToggle = AppTheme.CreateToggleSetting(
            "Auto-scroll",
            "Scrolls to the newest entry.",
            _autoScroll,
            v => _autoScroll = v);

        // Verbose debug logging toggle
        var settings = App.Services.GetRequiredService<Services.System.SettingsService>();
        var debugHeartbeat = App.Services.GetRequiredService<Services.System.DebugHeartbeat>();
        var verboseToggle = AppTheme.CreateToggleSetting(
            "Verbose debug logging",
            "Writes debug-level detail, the UI thread heartbeat and operation timings to the log.",
            settings.GetBoolDefaulted(AppDefaults.VerboseLoggingEnabled),
            v =>
            {
                settings.SetBool(AppDefaults.VerboseLoggingEnabled, v);
                // The level switch is what makes the toggle mean what it says: until it existed
                // the ~150 Logger.Debug lines app-wide (half in the capture/paste/pill code) were dropped by the
                // sink whatever this switch showed (launch defaults audit, 2026-09-13).
                Helpers.LogLevelControl.Apply(v);
                debugHeartbeat.Refresh(DispatcherQueue);
            });

        // Clear button — deletes log files and reinitializes Serilog
        var clearBtn = AppTheme.CreateSecondaryButton("Clear Logs", (_, _) =>
        {
            try
            {
                _tailTimer.Stop();
                App.ClearAndReinitializeLogs();
                _logBlock.Blocks.Clear();
                _lastPosition = 0;
                _currentLogPath = null;
                FindCurrentLogFile();
                if (_currentLogPath != null)
                    ReadFullLog();
                else
                    AppendLine("[Logs cleared]", AppTheme.SubtleText);
                _tailTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to clear log files");
                AppendLine($"[Failed to clear logs: {ex.Message}]", AppTheme.AccentRed);
                _tailTimer.Start();
            }
        });

        // Report a problem — same redacted-zip flow as the About page (owner request
        // 2026-07-18: the person staring at logs is exactly the person about to
        // report them).
        var reportBtn = AppTheme.CreateSecondaryButton("Report a problem", async (_, _) =>
        {
            try
            {
                var dialog = new Dialogs.ReportProblemDialog { XamlRoot = this.XamlRoot };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to show Report a problem dialog");
            }
        });

        // Open the Logs folder in Explorer (ShellFolder is fail-soft + creates the dir if absent).
        var logsFolderBtn = AppTheme.CreateSecondaryButton("Logs folder",
            (_, _) => ShellFolder.Open(AppPaths.LogsDir));

        // Three equal star columns so the buttons distribute evenly across the page
        // width (owner 2026-07-31); each button stretches to fill its column.
        var buttonRow = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
        };
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (btn, col) in new[] { (clearBtn, 0), (logsFolderBtn, 1), (reportBtn, 2) })
        {
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(btn, col);
            buttonRow.Children.Add(btn);
        }

        // Log output area
        _logBlock = new RichTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush(AppTheme.TextSecondary),
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _logBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = AppTheme.Brush(AppTheme.CardBg),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(AppTheme.CardCornerRadius),
            BorderBrush = AppTheme.Brush(AppTheme.CardBorderColor),
            BorderThickness = new Thickness(1),
        };

        // Only the two toggles that change how this VIEW behaves stay here (owner, 2026-08-04).
        // The three REL-17/REL-21 consent toggles moved to Settings -> Diagnostics; with two
        // compact cards left, the collapsible fold this page briefly had is unnecessary, so the
        // log pane keeps the rest of the page without a click.
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(autoScrollToggle);
        stack.Children.Add(verboseToggle);
        stack.Children.Add(buttonRow);

        // Use a Grid so the ScrollViewer can fill remaining space
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(stack, 0);
        Grid.SetRow(_scrollViewer, 1);
        layout.Children.Add(stack);
        layout.Children.Add(_scrollViewer);

        Content = new Border
        {
            Background = AppTheme.Brush(AppTheme.ContentBg),
            Padding = new Thickness(AppTheme.PagePadding),
            Child = layout
        };
    }

    private void FindCurrentLogFile()
    {
        var logDir = Helpers.AppPaths.LogsDir;

        if (!Directory.Exists(logDir))
        {
            _currentLogPath = null;
            return;
        }

        // Find the most recently modified log file
        _currentLogPath = Directory.GetFiles(logDir, "voicewink-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private void ReadFullLog()
    {
        if (_currentLogPath == null || !File.Exists(_currentLogPath))
        {
            AppendLine("[No log file found]", AppTheme.SubtleText);
            return;
        }

        try
        {
            using var fs = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var content = reader.ReadToEnd();
            _lastPosition = fs.Position;

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Show last 200 lines on initial load to keep UI responsive
            var startIndex = Math.Max(0, lines.Length - 200);
            if (startIndex > 0)
                AppendLine($"--- Showing last {lines.Length - startIndex} of {lines.Length} lines ---", AppTheme.SubtleText);

            for (int i = startIndex; i < lines.Length; i++)
                AppendLogLine(lines[i].TrimEnd('\r'));

            ScrollToBottom();
        }
        catch (Exception ex)
        {
            AppendLine($"[Error reading log: {ex.Message}]", AppTheme.AccentRed);
        }
    }

    private void OnTailTick(object? sender, object e)
    {
        try
        {
            if (_currentLogPath == null || !File.Exists(_currentLogPath))
            {
                // Log file might have rotated
                FindCurrentLogFile();
                if (_currentLogPath != null)
                {
                    _lastPosition = 0;
                    _logBlock.Blocks.Clear();
                    ReadFullLog();
                }
                return;
            }

            using var fs = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _lastPosition)
            {
                // File was truncated/rotated
                _lastPosition = 0;
                _logBlock.Blocks.Clear();
            }

            if (fs.Length == _lastPosition) return;

            fs.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var newContent = reader.ReadToEnd();
            _lastPosition = fs.Position;

            var lines = newContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
                AppendLogLine(line.TrimEnd('\r'));

            if (_autoScroll)
                ScrollToBottom();
        }
        catch { /* file locked or error — retry next tick */ }
    }

    private void AppendLogLine(string line)
    {
        var color = AppTheme.TextSecondary;

        if (line.Contains("[ERR]") || line.Contains("[FTL]"))
            color = AppTheme.AccentRed;
        else if (line.Contains("[WRN]"))
            color = AppTheme.WarningText;
        else if (line.Contains("[DBG]"))
            color = AppTheme.DimText;

        AppendLine(line, color);
    }

    private const int MaxParagraphs = 500;

    private void AppendLine(string text, Windows.UI.Color color)
    {
        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph
        {
            Margin = new Thickness(0)
        };
        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = text,
            Foreground = AppTheme.Brush(color)
        });
        _logBlock.Blocks.Add(paragraph);

        // Prune old paragraphs to prevent unbounded memory growth
        while (_logBlock.Blocks.Count > MaxParagraphs)
            _logBlock.Blocks.RemoveAt(0);
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Force layout so ScrollableHeight reflects the newly added content
            _scrollViewer.UpdateLayout();
            _scrollViewer.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
        });
    }
}
