using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VoiceWink.Helpers;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Views.Pages;

/// <summary>
/// The model downloads in progress, one row each: name and size, a progress bar, "45% · 1.2 of
/// 2.7 GB", and Cancel — or, for a stopped download its owner still wants, Download (owner,
/// 2026-10-17: a multi-gigabyte download must be visible and cancellable, in setup too). Reads
/// <see cref="BackgroundDownloads"/>, so a download shows the same wherever it was started; shown on
/// Home and in custom setup's speech step, collapsed while there is nothing to show.
/// <para>Only plain controls the app already renders (Border, Grid, StackPanel, TextBlock,
/// ProgressBar). Listens while loaded; one refresh is queued at a time, since a download reports as
/// often as it reads.</para>
/// </summary>
internal sealed class DownloadsCard
{
    private readonly Func<string, bool>? _include;
    private readonly bool _showStopped;
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly Dictionary<string, RowView> _views = new(StringComparer.OrdinalIgnoreCase);
    private string _layout = "";
    private volatile bool _refreshQueued;
    private bool _attached;

    /// <param name="title">Shown above the rows; null shows none.</param>
    /// <param name="include">Which model keys to show; null shows every download.</param>
    /// <param name="showStopped">Whether stopped downloads are listed with a Download button.</param>
    internal DownloadsCard(string? title, Func<string, bool>? include = null, bool showStopped = true)
    {
        _include = include;
        _showStopped = showStopped;

        var content = new StackPanel { Spacing = 10 };
        if (title is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            });
        }
        content.Children.Add(_rows);

        Element = AppTheme.CreateCard(content);
        Element.Visibility = Visibility.Collapsed;
        Element.Loaded += (_, _) =>
        {
            if (!_attached)
            {
                BackgroundDownloads.Instance.Changed += OnChanged;
                _attached = true;
            }
            Refresh();
        };
        Element.Unloaded += (_, _) =>
        {
            if (_attached)
            {
                BackgroundDownloads.Instance.Changed -= OnChanged;
                _attached = false;
            }
        };
    }

    public Border Element { get; }

    // Raised on the downloading thread.
    private void OnChanged()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        if (!Element.DispatcherQueue.TryEnqueue(Refresh))
            _refreshQueued = false;
    }

    private void Refresh()
    {
        _refreshQueued = false;
        var rows = BackgroundDownloads.Instance.Snapshot()
            .Where(r => (_showStopped || !r.Stopped) && (_include is null || _include(r.Key)))
            .ToList();

        // Rows are rebuilt only when the set changes, so a button under the pointer stays put while
        // its download moves.
        var layout = string.Join("|", rows.Select(r => $"{r.Key}:{r.Stopped}"));
        if (layout != _layout)
        {
            _layout = layout;
            _rows.Children.Clear();
            _views.Clear();
            foreach (var row in rows)
            {
                var view = new RowView();
                _views[row.Key] = view;
                _rows.Children.Add(view.Root);
            }
        }
        foreach (var row in rows)
            _views[row.Key].Show(row);
        Element.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class RowView
    {
        private readonly TextBlock _title = new()
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly ProgressBar _bar = new()
        {
            Minimum = 0,
            Maximum = 1,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Foreground = AppTheme.Brush(AppTheme.AccentBlueText),
            Background = AppTheme.Brush(ColorHelper.FromArgb(40, 142, 142, 147)),
            Margin = new Thickness(0, 6, 0, 4),
        };
        private readonly TextBlock _detail = new()
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
        };
        private readonly AppTheme.ActionToggleButton _button = AppTheme.CreateActionToggleButton("Download", "Cancel", compact: true);
        private DownloadRow? _row;

        internal RowView()
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(_title);
            text.Children.Add(_bar);
            text.Children.Add(_detail);

            _button.Element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_button.Element, 1);

            Root = new Grid { ColumnSpacing = 16 };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.Children.Add(text);
            Root.Children.Add(_button.Element);

            _button.Element.Tapped += (_, _) =>
            {
                if (_row is not { } row)
                    return;
                if (_button.IsCancelling)
                {
                    // A tap inside the double-click interval of the download starting is the second
                    // half of the click that started it, not a cancel (ActionToggleCore's remarks).
                    if (ActionToggleCore.CancelTapIsArmed(
                            TimeSpan.FromMilliseconds(Environment.TickCount64 - row.StartedTicks),
                            ActionToggleCore.ArmingWindow(NativeInterop.GetDoubleClickTime())))
                        BackgroundDownloads.Instance.Cancel(row.Key);
                    return;
                }
                BackgroundDownloads.Instance.Restart(row.Key);
            };
        }

        internal Grid Root { get; }

        internal void Show(DownloadRow row)
        {
            _row = row;
            AutomationProperties.SetName(_bar, $"{row.DisplayName} download");
            _title.Text = row.TotalBytes is { } total
                ? $"{row.DisplayName} · {DownloadProgressText.Size(total)}"
                : row.DisplayName;

            if (row.Stopped)
            {
                _bar.Visibility = Visibility.Collapsed;
                _detail.Text = row.Note ?? "";
                _button.ShowPrimary();
                _button.Element.Visibility = Visibility.Visible;
                AutomationProperties.SetName(_button.Element, $"Download {row.DisplayName}");
                return;
            }

            _bar.Visibility = Visibility.Visible;
            _bar.Value = row.Fraction;
            _detail.Text = DownloadProgressText.Progress(row.Fraction, row.TotalBytes);
            _button.ShowCancel();
            _button.Element.Visibility = row.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_button.Element, $"Cancel the {row.DisplayName} download");
        }
    }
}
