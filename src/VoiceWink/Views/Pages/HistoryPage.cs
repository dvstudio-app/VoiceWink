using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Models.Entities;
using VoiceWink.Models.Enums;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.Data;
using VoiceWink.Services.System;
using VoiceWink.Services.TextProcessing;
using VoiceWink.ViewModels;
using WinRT.Interop;

namespace VoiceWink.Views.Pages;

/// <summary>
/// Transcription history page.
/// Dark polished theme with search, card-based list, and detail panel.
/// All UI built in code to bypass PRI/XAML resource loading issues.
/// </summary>
public sealed class HistoryPage : Page
{
    private static ILogger Logger => Log.ForContext<HistoryPage>();

    private readonly HistoryViewModel _viewModel;
    private readonly TranscriptionHistoryService _historyService;
    private readonly ClipboardService _clipboard;
    private readonly AIEnhancementService _enhancement;
    private readonly MainViewModel _mainViewModel;
    private readonly PropertyChangedEventHandler _vmPropertyChanged;
    private readonly NotifyCollectionChangedEventHandler _transcriptionsCollectionChanged;
    private StackPanel? _listPanel;
    private TextBlock? _detailText;
    private TextBlock? _detailMeta;
    private TextBlock? _countText;
    private TextBlock? _originalLabel;
    private TextBlock? _originalText;
    private Border? _enhancedBadge;
    private Microsoft.UI.Xaml.Controls.Image? _detailImage;
    private Border? _copyImageBtn;
    private TextBlock? _detailImageNote; // HIS-1: missing/undisplayable-image note in the preview slot
    // HIS-1: fences async BitmapImage ImageOpened/ImageFailed callbacks — bumped on
    // every detail refresh so a stale selection's decode can't paint the current one.
    private int _detailImageLoadGeneration;
    private TextBlock? _detailPlaceholder;
    private TextBox? _searchBox;
    private CancellationTokenSource? _searchDebounce;
    private int? _selectedId;
    private readonly Dictionary<int, Border> _rowBorders = new();
    // HIS-7: the list's ScrollViewer, kept so an external refresh can restore the viewport.
    // Held as a field rather than looked up through the visual tree so the restore cannot
    // silently no-op if the card wrapper's shape ever changes.
    private ScrollViewer? _listScroll;

    // HIS-7: the id of the row currently at the TOP of the rendered list. Held as a FIELD, and
    // that is load-bearing rather than incidental: OnHistoryChanged awaits _viewModel.RefreshAsync()
    // BEFORE calling RefreshList, and RefreshAsync mutates the same ObservableCollection in place.
    // Reading the "previous" top row out of _viewModel.Transcriptions inside RefreshList therefore
    // reads the NEW top row -- searching a list for its own element 0, which returns 0 every time
    // and makes the whole shift compensation dead code. The field is written at the END of a render,
    // so it always describes what was actually on screen when the next refresh begins.
    private int? _firstRenderedId;
    private int _renderedCount;
    private FrameworkElement? _loadMoreFooter;
    // Reference-COUNTED (F26): overlapping bulk rebuilds (OnLoaded / OnHistoryChanged /
    // RenderMoreAsync / DebounceSearchAsync) each open a suppression window across an await;
    // a plain bool let the first-completing flow clear it while another was still mid-rebuild,
    // leaking its adds through as spurious RefreshList calls. The per-item CollectionChanged
    // refresh is suppressed while ANY window is open — only the last to close re-enables it.
    // All four sites are UI-thread-only, so a plain int is race-free.
    private int _suppressCollectionRefreshDepth;
    private bool SuppressCollectionRefresh => _suppressCollectionRefreshDepth > 0;
    // Bumped by RefreshList() (search / filter / external refresh rebuild the list from
    // scratch). RenderMoreAsync captures it and bails after an await if it changed, so a
    // mid-load rebuild can't be clobbered by a stale render loop resuming against new state.
    private int _renderGeneration;
    private bool IsSearchActive => _viewModel.IsSearchActive;
    private bool _isLoadingMore;
    private enum FilterMode { All, Text, Images }
    private FilterMode _filterMode = FilterMode.All;

    public HistoryPage()
    {
        RequestedTheme = AppTheme.ElementTheme;
        _viewModel = App.Services.GetRequiredService<HistoryViewModel>();
        _historyService = App.Services.GetRequiredService<TranscriptionHistoryService>();
        _clipboard = App.Services.GetRequiredService<ClipboardService>();
        _enhancement = App.Services.GetRequiredService<AIEnhancementService>();
        _mainViewModel = App.Services.GetRequiredService<MainViewModel>();
        BuildUI();

        _vmPropertyChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryViewModel.SelectedTranscription))
                DispatcherQueue.TryEnqueue(RefreshDetail);
            else if (e.PropertyName == nameof(HistoryViewModel.TotalCount))
                DispatcherQueue.TryEnqueue(UpdateCountText);
        };

        _transcriptionsCollectionChanged = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems?.Count == 1)
            {
                var removed = (TranscriptionRecord)args.OldItems[0]!;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_rowBorders.Remove(removed.Id, out var border))
                    {
                        _listPanel?.Children.Remove(border);
                        _renderedCount = Math.Max(0, _renderedCount - 1);
                    }
                    RefreshDetail();
                });
            }
            else if (!SuppressCollectionRefresh)
            {
                // Lambda, not a method group: RefreshList now takes an optional parameter, which
                // no longer converts to DispatcherQueueHandler. Deliberately NOT preserving the
                // viewport here -- this is the fallback for an UNSUPPRESSED bulk collection
                // change, i.e. one whose shape this page did not drive and cannot reason about.
                // The new-transcription path the owner reported runs through OnHistoryChanged,
                // which does preserve.
                DispatcherQueue.TryEnqueue(() => RefreshList());
            }
        };

        // Subscribe in Loaded / unsubscribe in Unloaded so page caching works correctly
        Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += _vmPropertyChanged;
        _viewModel.Transcriptions.CollectionChanged += _transcriptionsCollectionChanged;
        _historyService.HistoryChanged += OnHistoryChanged;
        _suppressCollectionRefreshDepth++;
        try
        {
            // Sync search state from the (possibly cached) TextBox before loading
            _viewModel.SearchQuery = _searchBox?.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(_viewModel.SearchQuery))
                await _viewModel.SearchAsync();
            else
                await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load history data");
        }
        finally
        {
            if (_suppressCollectionRefreshDepth > 0) _suppressCollectionRefreshDepth--;
        }
        RefreshList();
        UpdateCountText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _searchDebounce?.Cancel();
        _viewModel.PropertyChanged -= _vmPropertyChanged;
        _viewModel.Transcriptions.CollectionChanged -= _transcriptionsCollectionChanged;
        _historyService.HistoryChanged -= OnHistoryChanged;
    }

    /// <summary>
    /// Defense-in-depth (F25): a persisted <c>ImageFilePath</c> is a free-form DB string that
    /// could be corrupt or imported from another machine. Before the preview/open/copy actions
    /// act on it, verify it through the KERNEL-RESOLVED containment gate (the same one the
    /// ENH-6 upload uses — lexical containment alone can be escaped via a junction planted
    /// inside Images\) and act on the returned verified physical path, so a bad row can never
    /// make History read, render, or shell-launch a file outside the app's own data.
    /// </summary>
    private static bool TryResolveHistoryImagePath(string? persisted, out string safePath)
        => ReferenceImagePolicy.TryGetVerifiedAppImagePath(persisted, AppPaths.ImagesDir, out safePath);

    /// <summary>
    /// ENH-6f: decode a row's encoded reference value into AppReferences selections
    /// for the Regenerate context (null when the row has none — RedoContext's
    /// null-≡-empty convention).
    /// </summary>
    private static IReadOnlyList<ReferenceImageSelection>? DecodeRowReferences(string? encoded)
    {
        var paths = ReferencePathList.Decode(encoded);
        if (paths.Count == 0)
            return null;
        return paths.Select(p => new ReferenceImageSelection(p, ReferenceImageOrigin.AppReferences)).ToArray();
    }

    private void OnHistoryChanged()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                _suppressCollectionRefreshDepth++;
                try
                {
                    await _viewModel.RefreshAsync();
                }
                finally
                {
                    if (_suppressCollectionRefreshDepth > 0) _suppressCollectionRefreshDepth--;
                }

                RefreshList(preserveViewport: true); // HIS-7
                UpdateCountText();
                RefreshDetail();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to refresh history after external change");
            }
        });
    }

    private void BuildUI()
    {
        // ── Page header ──────────────────────────────────────────────
        var header = AppTheme.CreatePageHeader("History");

        // ── Enable history toggle ──────────────────────────────────────
        var settings = App.Services.GetRequiredService<SettingsService>();
        var enableToggle = AppTheme.CreateToggleSetting(
            "Enable History",
            "When disabled, transcriptions are not saved (logging still works)",
            settings.GetBool(AppDefaults.IsHistoryEnabled, true),
            isOn => settings.SetBool(AppDefaults.IsHistoryEnabled, isOn));

        _countText = new TextBlock
        {
            Text = "0 transcriptions",
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center
        };

        // ── Search box (card-styled) ─────────────────────────────────
        _searchBox = new TextBox
        {
            PlaceholderText = "Search transcriptions...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 14,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(AppTheme.CardCornerRadius)
        };
        _searchBox.TextChanged += (s, e) =>
        {
            _searchDebounce?.Cancel();
            _searchDebounce?.Dispose();
            _searchDebounce = new CancellationTokenSource();
            var token = _searchDebounce.Token;
            _ = DebounceSearchAsync(_searchBox.Text, token);
        };

        var searchWrapper = new Border
        {
            Child = _searchBox
        };

        // ── Toolbar ──────────────────────────────────────────────────
        var exportBtn = AppTheme.CreateSecondaryButton("Export CSV", async (s, e) =>
        {
            try
            {
                var mainWindow = App.MainWindow;
                if (mainWindow == null) return;
                var hwnd = WindowNative.GetWindowHandle(mainWindow);

                var defaultName = $"voicewink-history-{DateTime.Now:yyyyMMdd}.csv";
                // Default to Downloads — not protected by Controlled Folder Access.
                var initialDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var fileBuffer = global::System.Runtime.InteropServices.Marshal.AllocHGlobal(520);
                try
                {
                    for (int i = 0; i < 520; i++)
                        global::System.Runtime.InteropServices.Marshal.WriteByte(fileBuffer, i, 0);
                    var nameBytes = global::System.Text.Encoding.Unicode.GetBytes(defaultName);
                    global::System.Runtime.InteropServices.Marshal.Copy(nameBytes, 0, fileBuffer, nameBytes.Length);

                    var ofn = new NativeInterop.OPENFILENAME
                    {
                        lStructSize = global::System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.OPENFILENAME>(),
                        hwndOwner = hwnd,
                        lpstrFilter = "CSV files (*.csv)\0*.csv\0All files (*.*)\0*.*\0",
                        lpstrFile = fileBuffer,
                        nMaxFile = 260,
                        lpstrDefExt = "csv",
                        lpstrTitle = "Export History",
                        lpstrInitialDir = initialDir,
                        Flags = NativeInterop.OFN_OVERWRITEPROMPT | NativeInterop.OFN_PATHMUSTEXIST
                    };

                    if (!NativeInterop.GetSaveFileName(ref ofn))
                        return;

                    var savePath = global::System.Runtime.InteropServices.Marshal.PtrToStringUni(fileBuffer)!;
                    var csvContent = _filterMode switch
                    {
                        FilterMode.Text => await _viewModel.ExportCsvFilteredAsync(imagesOnly: false),
                        FilterMode.Images => await _viewModel.ExportCsvFilteredAsync(imagesOnly: true),
                        _ => await _viewModel.ExportCsvContentAsync()
                    };

                    await Task.Run(() => global::System.IO.File.WriteAllText(
                        savePath, csvContent, new global::System.Text.UTF8Encoding(true)));

                    var successDialog = new ContentDialog
                    {
                        Title = "Export Successful",
                        Content = $"History exported to:\n{savePath}",
                        CloseButtonText = "OK",
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = AppTheme.ElementTheme
                    };
                    await successDialog.ShowAsync();
                }
                finally
                {
                    global::System.Runtime.InteropServices.Marshal.FreeHGlobal(fileBuffer);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error(ex, "CSV export blocked (likely Controlled Folder Access)");
                var errorDialog = new ContentDialog
                {
                    Title = "Export Blocked",
                    Content = "Windows Defender Controlled Folder Access is blocking this save.\n\n"
                        + "To fix: Open Windows Security \u2192 Virus & threat protection \u2192 "
                        + "Ransomware protection \u2192 Allow an app through Controlled folder access "
                        + "\u2192 add:\n\n"
                        + @"C:\Program Files\dotnet\dotnet.exe" + "\n\n"
                        + "Or save to your Downloads folder instead (not protected by CFA).",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                };
                await errorDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CSV export failed");
                var errorDialog = new ContentDialog
                {
                    Title = "Export Failed",
                    Content = $"Could not export history:\n{ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                };
                await errorDialog.ShowAsync();
            }
        });

        var deleteAllBtn = AppTheme.CreateDangerButton("Delete All", async (s, e) =>
        {
            try
            {
                var filterLabel = _filterMode switch
                {
                    FilterMode.Text => "text transcriptions",
                    FilterMode.Images => "image generations",
                    _ => "transcription history"
                };

                var dialog = new ContentDialog
                {
                    Title = _filterMode == FilterMode.All ? "Delete All History" : $"Delete All {(_filterMode == FilterMode.Images ? "Images" : "Text")}",
                    Content = $"Are you sure you want to delete all {filterLabel}? This action cannot be undone.",
                    PrimaryButtonText = "Delete All",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    if (_filterMode == FilterMode.All)
                        await _viewModel.DeleteAllCommand.ExecuteAsync(null);
                    else
                        await _viewModel.DeleteByTypeAsync(imagesOnly: _filterMode == FilterMode.Images);
                    RefreshList();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to delete history");
            }
        });

        // ── Search + action buttons on one row ──────────────────────
        // HIS-2: with four buttons beside the star-width search box, a narrow window
        // (there is no minimum width) could clip the action row once the search
        // column hit zero — below the threshold the buttons reflow onto their own
        // second row, order preserved, Delete All still rightmost (Codex review).
        var toolBar = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        toolBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(searchWrapper, 0);
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // HIS-2 (owner request 2026-07-15): "New image…" + "Open images folder" moved
        // here from the AI Enhancement page — History is where generated images live.
        // Creation action leads the row; the folder shortcut pairs with it; the danger
        // button stays rightmost.
        var newImageBtn = AppTheme.CreateSecondaryButton("New image",
            (_, _) => _mainViewModel.RequestNewImageGeneration());
        var openImagesBtn = AppTheme.CreateSecondaryButton("Images folder",
            (_, _) => ShellFolder.Open(AppPaths.ImagesDir));
        buttonRow.Children.Add(newImageBtn);
        buttonRow.Children.Add(openImagesBtn);
        buttonRow.Children.Add(exportBtn);
        buttonRow.Children.Add(deleteAllBtn);
        Grid.SetColumn(buttonRow, 1);
        toolBar.Children.Add(searchWrapper);
        toolBar.Children.Add(buttonRow);

        // Reflow threshold = the buttons' natural width + a usable search width.
        // ActualWidth of the auto column's content is its natural width once laid out.
        const double minUsableSearchWidth = 220;
        var toolBarNarrow = false;
        toolBar.SizeChanged += (_, e) =>
        {
            var buttonsWidth = buttonRow.ActualWidth > 0 ? buttonRow.ActualWidth : buttonRow.DesiredSize.Width;
            var narrow = e.NewSize.Width < buttonsWidth + minUsableSearchWidth;
            if (narrow == toolBarNarrow) return;
            toolBarNarrow = narrow;
            if (narrow)
            {
                Grid.SetRow(buttonRow, 1);
                Grid.SetColumn(buttonRow, 0);
                Grid.SetColumnSpan(buttonRow, 2);
                buttonRow.Margin = new Thickness(0, 10, 0, 0);
                buttonRow.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                Grid.SetRow(buttonRow, 0);
                Grid.SetColumn(buttonRow, 1);
                Grid.SetColumnSpan(buttonRow, 1);
                buttonRow.Margin = new Thickness(12, 0, 0, 0);
                buttonRow.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        };

        // ── Filter pills (All / Text / Images) ─────────────────────
        Border? _filterAllBtn = null, _filterTextBtn = null, _filterImagesBtn = null;

        void UpdateFilterVisuals()
        {
            var activeBg = AppTheme.Brush(AppTheme.ActivePillBg);
            var activeFg = AppTheme.Brush(AppTheme.AccentBlue);
            var inactiveBg = AppTheme.TransparentBrush;
            var inactiveFg = AppTheme.Brush(AppTheme.SubtleText);

            void Style(Border btn, bool active)
            {
                btn.Background = active ? activeBg : inactiveBg;
                if (btn.Child is TextBlock tb)
                    tb.Foreground = active ? activeFg : inactiveFg;
            }

            Style(_filterAllBtn!, _filterMode == FilterMode.All);
            Style(_filterTextBtn!, _filterMode == FilterMode.Text);
            Style(_filterImagesBtn!, _filterMode == FilterMode.Images);
        }

        Border CreateFilterPill(string label, FilterMode mode)
        {
            var tb = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.SubtleText),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 5, 14, 5),
                Child = tb
            };
            pill.Tapped += async (_, _) =>
            {
                try
                {
                    _filterMode = mode;
                    UpdateFilterVisuals();
                    bool? typeFilter = mode switch
                    {
                        FilterMode.Text => false,
                        FilterMode.Images => true,
                        _ => null
                    };
                    await _viewModel.SetTypeFilterAsync(typeFilter);
                    RefreshList();
                    UpdateCountText();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to apply filter");
                }
            };
            return pill;
        }

        _filterAllBtn = CreateFilterPill("All", FilterMode.All);
        _filterTextBtn = CreateFilterPill("Text", FilterMode.Text);
        _filterImagesBtn = CreateFilterPill("Images", FilterMode.Images);

        _countText.Margin = new Thickness(8, 0, 0, 0);
        var filterRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _filterAllBtn, _filterTextBtn, _filterImagesBtn, _countText }
        };
        UpdateFilterVisuals();

        // ── Transcription list ───────────────────────────────────────
        _listPanel = new StackPanel { Spacing = 4 };

        var listScroll = new ScrollViewer
        {
            Content = _listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = AppTheme.TransparentBrush
        };
        _listScroll = listScroll;

        var listCard = AppTheme.CreateCard(listScroll, 12);

        // ── Detail panel ─────────────────────────────────────────────
        _detailMeta = new TextBlock
        {
            FontSize = 12,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _detailText = new TextBlock
        {
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            LineHeight = 22
        };

        // Original text label (shown only when enhancement differs from original)
        _originalLabel = new TextBlock
        {
            Text = "Original",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            Margin = new Thickness(0, 12, 0, 4),
            Visibility = Visibility.Collapsed
        };

        _originalText = new TextBlock
        {
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            LineHeight = 20,
            Visibility = Visibility.Collapsed
        };

        // "Enhanced" badge shown above the enhanced text when enhancement is present
        _enhancedBadge = new Border
        {
            Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)), // AccentGreen at 20%
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Enhanced",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Brush(AppTheme.AccentGreen)
            }
        };

        // Placeholder shown when no transcription is selected
        _detailPlaceholder = new TextBlock
        {
            Text = "Select a transcription to view its full text.",
            FontSize = 14,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(32, 0, 32, 0)
        };

        // Image preview for generated images (hidden for text items)
        _detailImage = new Microsoft.UI.Xaml.Controls.Image
        {
            MaxHeight = 400,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 8),
            Visibility = Visibility.Collapsed
        };
        _detailImage.DoubleTapped += (_, _) =>
        {
            var item = _viewModel.SelectedTranscription;
            if (TryResolveHistoryImagePath(item?.ImageFilePath, out var safePath))
            {
                try
                {
                    global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
                    {
                        FileName = safePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to open image file");
                }
            }
        };

        _copyImageBtn = AppTheme.CreateSecondaryButton("Copy Image to Clipboard", async (_, _) =>
        {
            var item = _viewModel.SelectedTranscription;
            if (TryResolveHistoryImagePath(item?.ImageFilePath, out var safePath))
            {
                try
                {
                    var imageBytes = await Task.Run(() => File.ReadAllBytes(safePath));
                    // BRANCH on the result. This used to log success unconditionally, which was
                    // nearly harmless while every payload was a decodable PNG — but the decoder now
                    // has a pixel bound and `.img` files can be undecodable, so an unchecked "copied"
                    // is a log that lies and a user who thinks the clipboard holds their image.
                    if (await _clipboard.SetClipboardImageAsync(imageBytes))
                    {
                        Logger.Information("Image copied to clipboard from history");
                    }
                    else
                    {
                        Logger.Warning("Image copy from history failed — clipboard not set");
                        await ShowCopyFailedAsync();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to copy image from history");
                    await ShowCopyFailedAsync();
                }
            }
        });
        _copyImageBtn.Margin = new Thickness(0, 0, 0, 8);
        _copyImageBtn.Visibility = Visibility.Collapsed;

        // Copy is a deliberate user action, so a silent failure is the wrong answer — the user would
        // paste stale clipboard content believing it was their image. Uses the page's existing
        // ContentDialog idiom rather than introducing a second messaging surface.
        async Task ShowCopyFailedAsync()
        {
            try
            {
                await new ContentDialog
                {
                    Title = "Couldn't copy image",
                    Content = "This image couldn't be placed on the clipboard. "
                        + "You can still open it from the Images folder.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme,
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                // A dialog that cannot open (no XamlRoot, one already showing) must not turn a copy
                // failure into a crash — the Warning above is already recorded.
                Logger.Warning(ex, "Could not show the copy-failed dialog");
            }
        }

        // HIS-1: explains an EMPTY preview slot — a successful image row whose file
        // was deleted from the Images folder previously looked identical to a text row
        // apart from the badge (owner report 2026-07-15).
        _detailImageNote = new TextBlock
        {
            Foreground = AppTheme.Brush(AppTheme.WarningText),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
            Visibility = Visibility.Collapsed
        };

        var detailContentStack = new StackPanel
        {
            Children = { _detailMeta, _enhancedBadge, _detailImage, _detailImageNote, _copyImageBtn, _detailText, _originalLabel, _originalText }
        };

        var detailGrid = new Grid();
        detailGrid.Children.Add(detailContentStack);
        detailGrid.Children.Add(_detailPlaceholder);

        var detailScroll = new ScrollViewer
        {
            Content = detailGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var detailCard = AppTheme.CreateCard(detailScroll);

        // ── Assemble page (Grid with draggable splitter between list and detail) ──
        var headerPanel = new StackPanel
        {
            Children = { header, enableToggle, toolBar, filterRow }
        };

        // ── Splitter grip ─────────────────────────────────────────────
        var gripLine = new Border
        {
            Width = 40,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = AppTheme.Brush(AppTheme.CardBorderColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var splitter = new Border
        {
            Height = 16,
            Background = AppTheme.TransparentBrush,
            Child = gripLine
        };
        var listRow = new RowDefinition { Height = new GridLength(200), MinHeight = 100 };
        var detailRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 };

        var layout = new Grid { Margin = new Thickness(AppTheme.PagePadding) };

        // Drag logic
        bool dragging = false;
        double dragStartY = 0;
        double listStartHeight = 0;
        var defaultCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        var resizeCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);

        splitter.PointerEntered += (_, _) =>
        {
            gripLine.Background = AppTheme.Brush(AppTheme.AccentBlue);
            if (!dragging) ProtectedCursor = resizeCursor;
        };
        splitter.PointerExited += (_, _) =>
        {
            gripLine.Background = AppTheme.Brush(AppTheme.CardBorderColor);
            if (!dragging) ProtectedCursor = defaultCursor;
        };
        splitter.PointerPressed += (s, e) =>
        {
            dragging = true;
            dragStartY = e.GetCurrentPoint(null).Position.Y;
            listStartHeight = listCard.ActualHeight;
            splitter.CapturePointer(e.Pointer);
            ProtectedCursor = resizeCursor;
            e.Handled = true;
        };
        splitter.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var currentY = e.GetCurrentPoint(null).Position.Y;
            var delta = currentY - dragStartY;
            var newHeight = listStartHeight + delta;
            // Clamp: respect list min and reserve space for splitter + detail min
            var maxListHeight = layout.ActualHeight - headerPanel.ActualHeight - splitter.Height - detailRow.MinHeight;
            newHeight = Math.Clamp(newHeight, listRow.MinHeight, Math.Max(listRow.MinHeight, maxListHeight));
            listRow.Height = new GridLength(newHeight);
            e.Handled = true;
        };
        splitter.PointerReleased += (s, e) =>
        {
            if (!dragging) return;
            dragging = false;
            splitter.ReleasePointerCapture(e.Pointer);
            ProtectedCursor = defaultCursor;
            e.Handled = true;
        };
        splitter.PointerCaptureLost += (_, _) =>
        {
            dragging = false;
            ProtectedCursor = defaultCursor;
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row 0: header
        layout.RowDefinitions.Add(listRow);                                         // row 1: list
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row 2: splitter
        layout.RowDefinitions.Add(detailRow);                                       // row 3: detail

        Grid.SetRow(headerPanel, 0);
        Grid.SetRow(listCard, 1);
        Grid.SetRow(splitter, 2);
        Grid.SetRow(detailCard, 3);
        listCard.Margin = new Thickness(0, 0, 0, 0);
        detailCard.Margin = new Thickness(0);

        layout.Children.Add(headerPanel);
        layout.Children.Add(listCard);
        layout.Children.Add(splitter);
        layout.Children.Add(detailCard);

        Content = layout;
    }

    private const int PageSize = 20;

    /// <summary>How many additional rows a single "Load more" click renders.</summary>
    private const int LoadMoreBatchSize = 100;

    /// <summary>HIS-7: the most rows an EXTERNAL refresh will re-render to preserve the viewport.
    /// <para>Bounds a regression this fix would otherwise introduce. HistoryChanged fires once PER
    /// SAVED ROW — an image batch commits incrementally, one event per version — and each event now
    /// rebuilds the rendered list synchronously on the UI thread. Before this change that was always
    /// <see cref="PageSize"/> rows; unbounded it becomes however many the user has materialised. A
    /// user who accepted the "Load all" warning on a few thousand rows and then dictates would pay a
    /// full multi-thousand-row teardown-and-rebuild per version — the one-time cost that dialog
    /// gates on, recurring per row with no dialog.</para>
    /// <para>Set to <see cref="LoadAllConfirmThreshold"/>'s value deliberately: that is already this
    /// page's stated boundary between "fine" and "warn the user first". Above it the list falls back
    /// to one page, i.e. the pre-HIS-7 behaviour, which is a real residual for very heavy users and
    /// is carded as HIS-9 rather than hidden.</para></summary>
    private const int PreserveRenderCeiling = LoadAllConfirmThreshold;

    /// <summary>Above this many remaining rows, "Load all" confirms before materializing them.</summary>
    private const int LoadAllConfirmThreshold = 500;

    private void UpdateCountText()
    {
        if (_countText == null) return;
        var label = _filterMode switch
        {
            FilterMode.Text => "text transcriptions",
            FilterMode.Images => "images",
            _ => IsSearchActive ? "results" : "transcriptions"
        };
        _countText.Text = $"{_viewModel.TotalCount} {label}";
    }

    /// <summary>Rebuild the rendered rows from <c>_viewModel.Transcriptions</c>.
    /// <para>HIS-7: <paramref name="preserveViewport"/> is for an EXTERNAL refresh — a new
    /// transcription arriving while the user is reading. Without it, one new row collapsed the
    /// list back to a single page and threw the user to the top: the ViewModel's
    /// <c>RefreshAsync</c> correctly reloads every page that was loaded (it passes
    /// <c>loadedPages * PageSize</c>), but this method then rendered only the first
    /// <c>PageSize</c> of them and reset <c>_renderedCount</c>, so the DB paging survived and
    /// the RENDER paging did not. Clearing the panel is also what dropped the scroll offset.
    /// A search, a filter change or a fresh page load pass false: those genuinely produce a
    /// different result set, where starting at the top is correct.</para></summary>
    private void RefreshList(bool preserveViewport = false)
    {
        if (_listPanel == null) return;

        // `previousFirstId` is what lets the scroll offset survive rows inserted ABOVE the user's
        // position: holding the raw offset would slide the view down by exactly the height of the
        // new rows, which is the "loses their place" complaint in a second form. It comes from the
        // FIELD, never from _viewModel.Transcriptions -- by the time this runs the collection has
        // already been replaced in place by RefreshAsync (see the field's comment).
        var previousRenderedCount = preserveViewport ? _renderedCount : 0;
        var previousOffset = preserveViewport ? _listScroll?.VerticalOffset ?? 0 : 0;
        var previousFirstId = preserveViewport ? _firstRenderedId : null;

        _renderGeneration++; // invalidate any in-flight RenderMoreAsync loop
        _listPanel.Children.Clear();
        _rowBorders.Clear();
        _renderedCount = 0;

        var items = _viewModel.Transcriptions;
        var renderTarget = HistoryViewportRestore.RenderTarget(
            PageSize, previousRenderedCount, items.Count, PreserveRenderCeiling);
        var batch = items.Take(renderTarget).ToList();
        foreach (var item in batch)
            AddRow(item);

        _renderedCount = batch.Count;
        _firstRenderedId = batch.Count > 0 ? batch[0].Id : null;
        _selectedId = _viewModel.SelectedTranscription?.Id;

        UpdateLoadMoreButton(_viewModel.TotalCount);

        if (preserveViewport)
            RestoreViewport(previousOffset, previousFirstId);
    }

    /// <summary>HIS-7: put the user back where they were after an external refresh. Rows added
    /// above the previous top row shift everything down, so the offset has to grow by exactly
    /// their height — otherwise "restoring" the offset still moves the content under the user.
    /// <para>Fail-soft by design: if the previously-top row is gone (it was deleted, or fell off
    /// the end of the reloaded window) there is no meaningful anchor, so the raw offset is
    /// restored and the ScrollViewer clamps it. A wrong-by-a-few-pixels restore is strictly
    /// better than the jump to top this replaces.</para></summary>
    private void RestoreViewport(double previousOffset, int? previousFirstId)
    {
        if (_listScroll == null || _listPanel == null) return;
        if (previousOffset <= 0 && previousFirstId == null) return;

        var insertedAbove = HistoryViewportRestore.InsertedAbove(
            _viewModel.Transcriptions.Select(t => t.Id).ToList(), previousFirstId, _renderedCount);

        // Force layout BEFORE measuring and before ChangeView. Two separate reasons, and the
        // second one bites even when insertedAbove is 0: ActualHeight is meaningless until the
        // rows just added have been laid out, AND ChangeView silently clamps to the ScrollViewer's
        // ScrollableHeight, which is stale until it re-measures its content. Layout is forced on
        // the SCROLLVIEWER (not just the panel) for that second reason — the same pattern
        // LogViewerPage already uses when scrolling to newly appended content. ChangeView's return
        // value is deliberately not consulted: there is no better fallback than the clamp.
        _listScroll.UpdateLayout();

        double delta = 0;
        for (var i = 0; i < insertedAbove && i < _listPanel.Children.Count; i++)
        {
            // Rows are variable height (wrapped text of arbitrary length), so a constant would be
            // wrong per row.
            if (_listPanel.Children[i] is FrameworkElement fe)
                delta += fe.ActualHeight + _listPanel.Spacing;
        }

        _listScroll.ChangeView(null, previousOffset + delta, null, disableAnimation: true);
    }

    private async void LoadMoreRows() => await RenderMoreAsync(LoadMoreBatchSize);

    private async void LoadAllRows()
    {
        // Materializing a very large history at once (non-virtualized list, full-text rows)
        // can briefly freeze the window — confirm first past a threshold.
        var remaining = _viewModel.TotalCount - _renderedCount;
        if (remaining > LoadAllConfirmThreshold)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Load all history?",
                    Content = $"This will display all {remaining:N0} remaining entries at once. "
                        + "On a large history that may make the window briefly unresponsive.",
                    PrimaryButtonText = "Load all",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = AppTheme.ElementTheme
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            catch (Exception ex)
            {
                // Couldn't confirm — fail safe by not materializing everything unprompted.
                Logger.Error(ex, "Load-all confirmation dialog failed");
                return;
            }
        }

        await RenderMoreAsync(int.MaxValue);
    }

    /// <summary>
    /// Render up to <paramref name="count"/> additional rows, fetching further DB pages
    /// as needed. Pass <see cref="int.MaxValue"/> to render everything ("Load all").
    /// </summary>
    private async Task RenderMoreAsync(int count)
    {
        if (_listPanel == null || _isLoadingMore) return;
        _isLoadingMore = true;
        try
        {
            // Snapshot the list generation. If a RefreshList() (search / filter / external
            // refresh) rebuilds the list while we're awaiting a DB page, this loop's state is
            // stale — bail rather than render old rows into the new list.
            var generation = _renderGeneration;

            // Saturating target so int.MaxValue ("Load all") can't overflow.
            var target = count >= int.MaxValue - _renderedCount
                ? int.MaxValue
                : _renderedCount + count;

            while (_renderedCount < target)
            {
                var items = _viewModel.Transcriptions;

                // All loaded items already rendered — fetch the next page from DB.
                if (_renderedCount >= items.Count)
                {
                    if (!_viewModel.HasMore) break;
                    _suppressCollectionRefreshDepth++;
                    try
                    {
                        await _viewModel.LoadMoreAsync();
                    }
                    finally
                    {
                        if (_suppressCollectionRefreshDepth > 0) _suppressCollectionRefreshDepth--;
                    }
                    if (generation != _renderGeneration) return; // list rebuilt mid-load — abort
                    items = _viewModel.Transcriptions; // re-read after async load
                    if (_renderedCount >= items.Count) break; // no progress — avoid spinning
                }

                var take = Math.Min(target - _renderedCount, items.Count - _renderedCount);
                var batch = items.Skip(_renderedCount).Take(take).ToList();
                foreach (var item in batch)
                    AddRow(item);
                _renderedCount += batch.Count;
            }

            UpdateLoadMoreButton(_viewModel.TotalCount);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "RenderMoreAsync failed");
            // Footer remains visible so user can retry
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private void UpdateLoadMoreButton(int totalCount)
    {
        if (_listPanel == null) return;

        // Remove existing footer if present
        if (_loadMoreFooter != null && _listPanel.Children.Contains(_loadMoreFooter))
            _listPanel.Children.Remove(_loadMoreFooter);

        if (_renderedCount < totalCount)
        {
            var remaining = totalCount - _renderedCount;

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            footer.Children.Add(AppTheme.CreateSecondaryButton(
                $"Load {Math.Min(LoadMoreBatchSize, remaining)} more ({remaining} remaining)",
                (_, _) => LoadMoreRows()));
            footer.Children.Add(AppTheme.CreateSecondaryButton(
                $"Load all ({remaining})",
                (_, _) => LoadAllRows()));

            _loadMoreFooter = footer;
            _listPanel.Children.Add(footer);
        }
        else
        {
            _loadMoreFooter = null;
        }
    }

    private void AddRow(TranscriptionRecord item)
    {
        if (_listPanel == null) return;

        var normalBg = AppTheme.Brush(AppTheme.CardBg);
        var selectedBg = AppTheme.Brush(ColorHelper.FromArgb(25, 0, 122, 255));
        var hoverBg = AppTheme.Brush(AppTheme.HoverBg);
        var isSelected = _viewModel.SelectedTranscription?.Id == item.Id;

        var isImageItem = IsImageHistoryItem(item);
        var isFailedImage = item.EnhancedText == TranscriptionRecord.ImageFailedMarker;
        var isFailedEnhancement = !isImageItem && item.EnhancedText == "[Enhancement failed]";
        var isFailed = isFailedImage || isFailedEnhancement;
        var hasEnhancement = !isImageItem && !isFailedEnhancement
            && item.WasEnhanced && !string.IsNullOrEmpty(item.EnhancedText);
        var displayText = isImageItem || isFailedEnhancement
            ? (item.Text ?? "")
            : (hasEnhancement ? item.EnhancedText! : (item.Text ?? ""));
        var preview = displayText;
        if (isFailed)
            preview = "\u26A0 " + preview;

        // Visual indicator: image (blue), enhanced (green), failed (red), plain (subtle)
        var typeIcon = new FontIcon
        {
            Glyph = isImageItem ? "\uE8B9" : "\uE8D2", // Photo : Page
            FontSize = 12,
            Foreground = AppTheme.Brush(isFailed ? AppTheme.AccentRed
                : isImageItem ? AppTheme.AccentBlue
                : hasEnhancement ? AppTheme.AccentGreen
                : AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 8, 0)
        };

        var previewBlock = new TextBlock
        {
            Text = preview,
            FontSize = 13,
            Foreground = AppTheme.Brush(AppTheme.TextPrimary),
            TextWrapping = TextWrapping.Wrap
        };

        var time = FormatRelativeTimestamp(item.Timestamp);
        var timeBlock = new TextBlock
        {
            Text = time,
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var capturedItem = item;

        // ── Copy button ────────────────────────────────────────────
        var copyIcon = new FontIcon
        {
            Glyph = "\uE8C8", // Copy
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        var copyBtn = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Background = AppTheme.TransparentBrush,
            Child = new Viewbox { Width = 12, Height = 12, Child = copyIcon }
        };
        var copyFeedbackGeneration = 0;
        var copyFeedbackActive = false; // hover handlers must not overwrite the feedback tint
        copyBtn.PointerEntered += (_, _) =>
        {
            copyBtn.Background = AppTheme.Brush(ColorHelper.FromArgb(30, 142, 142, 147));
            if (!copyFeedbackActive) copyIcon.Foreground = AppTheme.Brush(AppTheme.TextPrimary);
        };
        copyBtn.PointerExited += (_, _) =>
        {
            copyBtn.Background = AppTheme.TransparentBrush;
            if (!copyFeedbackActive) copyIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        };
        copyBtn.PointerPressed += (_, pe) => pe.Handled = true;
        copyBtn.Tapped += async (_, e) =>
        {
            e.Handled = true;
            // For image/failed items, copy the original text, not the marker
            var isImageItem = IsImageHistoryItem(capturedItem);
            var textToCopy = isImageItem || capturedItem.EnhancedText == "[Enhancement failed]"
                ? capturedItem.Text
                : (capturedItem.WasEnhanced && !string.IsNullOrEmpty(capturedItem.EnhancedText)
                    ? capturedItem.EnhancedText
                    : capturedItem.Text);
            var copied = await _clipboard.SetClipboardAsync(textToCopy ?? "");

            // HIS-1: say WHAT was copied — this button is a TEXT affordance (the image
            // copy lives on the detail pane), and for image rows it yields the PROMPT;
            // with a missing image that silence read as a wrong copy (owner report).
            // The feedback branches on SetClipboard's RESULT (a checkmark on a failed
            // copy would be the same silent lie this fixes — Codex diff review), the
            // ToolTip is opened EXPLICITLY (SetToolTip alone only configures hover
            // content), and a generation guard keeps rapid re-taps from leaving a
            // stuck glyph or an orphaned tip.
            var generation = ++copyFeedbackGeneration;
            copyFeedbackActive = true;
            copyIcon.Glyph = copied ? "\uE73E" : "\uE711"; // CheckMark / Cancel
            copyIcon.Foreground = AppTheme.Brush(copied ? AppTheme.AccentGreen : AppTheme.AccentRed);
            var tip = new ToolTip
            {
                Content = copied
                    ? HistoryImagePresentation.CopyFeedbackLabel(isImageItem)
                    : HistoryImagePresentation.CopyFailedLabel
            };
            ToolTipService.SetToolTip(copyBtn, tip);
            tip.IsOpen = true;
            await Task.Delay(1200);
            // ALWAYS close this invocation's own tooltip — replacing the attachment
            // only implicitly hides it; IsOpen is the documented visibility control
            // (Codex HIS-1 r2). Only the SHARED glyph/attachment cleanup below is
            // generation-guarded.
            tip.IsOpen = false;
            if (generation != copyFeedbackGeneration) return;
            copyFeedbackActive = false;
            copyIcon.Glyph = "\uE8C8"; // Copy
            copyIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
            ToolTipService.SetToolTip(copyBtn, null);
        };

        // ── Redo enhancement button ────────────────────────────────
        var redoIcon = new FontIcon
        {
            Glyph = "\uE72C", // Refresh/Redo
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        var redoBtn = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Background = AppTheme.TransparentBrush,
        };
        redoBtn.Child = new Viewbox { Width = 12, Height = 12, Child = redoIcon };

        bool isRedoing = false;
        redoBtn.PointerEntered += (_, _) =>
        {
            if (isRedoing) return;
            redoBtn.Background = AppTheme.Brush(ColorHelper.FromArgb(30, 48, 209, 88));
            redoIcon.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
        };
        redoBtn.PointerExited += (_, _) =>
        {
            if (isRedoing) return;
            redoBtn.Background = AppTheme.TransparentBrush;
            redoIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        };
        redoBtn.PointerPressed += (_, pe) => pe.Handled = true;
        redoBtn.Tapped += (_, e) =>
        {
            e.Handled = true;

            // Construct a RedoContext from the history item and use the shared model picker
            // (same dialog as MiniRecorder redo button and redo hotkey).
            // Parse "Provider/Model" from EnhancementModelName to pre-select in dialog.
            var prompt = ResolvePromptForItem(capturedItem) ?? _enhancement.GetActivePrompt();
            bool isImage = IsImageHistoryItem(capturedItem);
            var (prevProvider, prevModel) = ProviderModelLabel.Parse(capturedItem.EnhancementModelName);
            var ctx = new MainViewModel.RedoContext(
                RawText: capturedItem.Text ?? "",
                EnhancedText: capturedItem.EnhancedText,
                Prompt: prompt,
                WasImageGeneration: isImage,
                TargetWindow: IntPtr.Zero,
                WasPushToTalk: false,
                TranscriptionModelName: capturedItem.ModelName ?? "unknown",
                // The row's OWN language, not the live setting. A History redo re-runs enhancement
                // on a record that may be weeks old; re-reading Settings would stamp it with
                // whatever the user has selected today.
                TranscriptionLanguage: capturedItem.Language,
                PreviousProvider: prevProvider,
                PreviousModel: prevModel,
                PreviousImageAspect: capturedItem.ImageAspect,
                PreviousImageSizeTier: capturedItem.ImageSizeTier,
                PreviousImageQuality: capturedItem.ImageQuality,
                // Redo repeats THIS request, so it restores the version count too
                // (owner, 2026-07-29). Null on rows written before the column existed →
                // SeedCount maps it to 1, exactly the previous behaviour.
                PreviousImageCount: capturedItem.ImageVersionCount,
                // ENH-6b/6f: seed the row's persisted reference copies RAW — the encoded
                // column value decodes to one AppReferences selection per path; the
                // dialog is authoritative and re-validates per origin, per item (a
                // swept/missing copy degrades with a note there, never a dead path here).
                References: DecodeRowReferences(capturedItem.ReferenceImagePath)
            );

            _mainViewModel.RequestModelPickerWithContext(ctx);
        };

        // ── Iterate button (ENH-6, successful image rows only) ─────
        // Opens the text-first image dialog pre-seeded with THIS row's image as the
        // reference — "make another one like this". Presence-gated on the success
        // marker + a stored path; deep validation (containment, existence) happens in
        // RequestNewImageGenerationWithReference, which degrades to a no-reference
        // dialog when the file is gone.
        //
        // 2026-08-03: also gated on the extension being a type we can actually SEND. Generated
        // files are now named for what they are, so a GIF/BMP/HEIF/.img row would otherwise show a
        // button that always fails at the read gate. The check is the EXTENSION half only — pure,
        // no per-row file I/O — because this runs synchronously for every rendered row and the deep
        // validation deliberately lives downstream (see the comment above).
        //
        // Scope, honestly: this hides the button for extension-VISIBLE kinds, which is all a name
        // can express. A row written by an older build that holds GIF bytes in a `.png` name still
        // shows Iterate and still fails — but now with the typed, rewritten message instead of a
        // provider 400. Accepted residual.
        Border? iterateBtn = null;
        if (capturedItem.EnhancedText == TranscriptionRecord.ImageGeneratedMarker
            && !string.IsNullOrEmpty(capturedItem.ImageFilePath)
            && ReferenceImagePolicy.MimeFromExtension(capturedItem.ImageFilePath) != null)
        {
            var iterateIcon = new FontIcon
            {
                Glyph = "\uE8B9", // Picture
                FontSize = 11,
                Foreground = AppTheme.Brush(AppTheme.SubtleText)
            };
            var iterateBorder = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                Background = AppTheme.TransparentBrush,
                Child = new Viewbox { Width = 12, Height = 12, Child = iterateIcon }
            };
            ToolTipService.SetToolTip(iterateBorder, "New image from this one");
            iterateBorder.PointerEntered += (_, _) =>
            {
                iterateBorder.Background = AppTheme.Brush(ColorHelper.FromArgb(30, 0, 122, 255));
                iterateIcon.Foreground = AppTheme.Brush(AppTheme.AccentBlue);
            };
            iterateBorder.PointerExited += (_, _) =>
            {
                iterateBorder.Background = AppTheme.TransparentBrush;
                iterateIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
            };
            iterateBorder.PointerPressed += (_, pe) => pe.Handled = true;
            iterateBorder.Tapped += (_, e) =>
            {
                e.Handled = true;
                // ENH-6d: hand over the EXACT settings that generated this image —
                // "another one like this" starts from this row's provider/model/
                // aspect/size/quality, not the current defaults.
                // DELIBERATELY NOT the version count (owner, 2026-07-29): unlike Redo,
                // which repeats that request, Iterate branches a NEW image off this one
                // — you clicked one picture, so one comes back. A hidden 4 here would
                // quadruple provider spend from a button labelled "New image from this
                // one". The dialog's Versions picker is right there if you want more.
                var (iterProvider, iterModel) = ProviderModelLabel.Parse(capturedItem.EnhancementModelName);
                _mainViewModel.RequestNewImageGenerationWithReference(
                    capturedItem.ImageFilePath!,
                    iterProvider,
                    iterModel,
                    capturedItem.ImageAspect,
                    capturedItem.ImageSizeTier,
                    capturedItem.ImageQuality);
            };
            iterateBtn = iterateBorder;
        }

        // ── Delete button ──────────────────────────────────────────
        var deleteIcon = new FontIcon
        {
            Glyph = "\uE74D",
            FontSize = 11,
            Foreground = AppTheme.Brush(AppTheme.SubtleText)
        };
        var deleteBtn = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Background = AppTheme.TransparentBrush,
            Child = new Viewbox { Width = 12, Height = 12, Child = deleteIcon }
        };
        deleteBtn.PointerEntered += (_, _) =>
        {
            deleteBtn.Background = AppTheme.Brush(ColorHelper.FromArgb(30, 255, 69, 58));
            deleteIcon.Foreground = AppTheme.Brush(AppTheme.AccentRed);
        };
        deleteBtn.PointerExited += (_, _) =>
        {
            deleteBtn.Background = AppTheme.TransparentBrush;
            deleteIcon.Foreground = AppTheme.Brush(AppTheme.SubtleText);
        };
        deleteBtn.PointerPressed += (_, pe) => pe.Handled = true;
        deleteBtn.Tapped += async (_, e) =>
        {
            e.Handled = true;
            await _viewModel.DeleteCommand.ExecuteAsync(capturedItem);
        };

        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { timeBlock, copyBtn, redoBtn, deleteBtn }
        };
        // ENH-6: Iterate sits between redo and delete on successful image rows.
        if (iterateBtn != null)
            rightStack.Children.Insert(3, iterateBtn);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(typeIcon, 0);
        Grid.SetColumn(previewBlock, 1);
        Grid.SetColumn(rightStack, 2);
        rowGrid.Children.Add(typeIcon);
        rowGrid.Children.Add(previewBlock);
        rowGrid.Children.Add(rightStack);

        var border = new Border
        {
            Background = isSelected ? selectedBg : normalBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = isSelected
                ? new Thickness(3, 0, 0, 0)
                : new Thickness(0),
            BorderBrush = isSelected
                ? AppTheme.Brush(AppTheme.AccentBlue)
                : null,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Child = rowGrid
        };

        border.PointerEntered += (_, _) =>
        {
            if (_selectedId != capturedItem.Id)
                border.Background = hoverBg;
        };
        border.PointerExited += (_, _) =>
        {
            if (_selectedId != capturedItem.Id)
                border.Background = normalBg;
        };

        // Focus styling (keyboard navigation)
        border.GotFocus += (_, _) =>
        {
            if (_selectedId != capturedItem.Id)
                border.Background = hoverBg;
        };
        border.LostFocus += (_, _) =>
        {
            if (_selectedId != capturedItem.Id)
                border.Background = normalBg;
        };

        // Keyboard navigation: Enter to select, Delete to remove
        border.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _viewModel.SelectedTranscription = capturedItem;
                UpdateSelection(capturedItem.Id);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Delete)
            {
                await _viewModel.DeleteCommand.ExecuteAsync(capturedItem);
                e.Handled = true;
            }
        };

        border.PointerPressed += (_, _) =>
        {
            _viewModel.SelectedTranscription = capturedItem;
            UpdateSelection(capturedItem.Id);
            // Focus MUST follow the click. The Enter/Delete handlers above are per-row KeyDown
            // subscriptions, so they act on the row holding KEYBOARD FOCUS — and clicking used to
            // move SELECTION only. Focus stayed wherever WinUI first put it (the first row, being
            // the first tab stop), so a user who clicked row N and pressed Delete destroyed row 1
            // while row N sat highlighted (owner report 2026-08-03). Selection and focus are two
            // different things here; this is what keeps them from disagreeing.
            //
            // FocusState.Pointer, not Programmatic: the row is already marked by the selection
            // background + accent bar, and Programmatic would additionally draw the system focus
            // rectangle on an ordinary mouse click. Routing is identical either way — KeyDown goes
            // to the focused element regardless of how it was focused.
            //
            // The return value is CHECKED rather than discarded (Kimi diff review): a failed focus
            // silently restores the exact bug this line exists to fix — selection on row N, focus
            // still on row 1, Delete destroying row 1 — and nothing else in the UI would reveal it.
            // A row that stops being focusable (IsTabStop cleared, an ancestor made hit-test
            // invisible) is the realistic cause, so the log names the row. Failure is not otherwise
            // actionable here, and Focus() does not throw, so this stays a warning rather than a
            // guard; in practice it never fires.
            if (!border.Focus(FocusState.Pointer))
            {
                Logger.Warning(
                    "History row {RowId} could not take focus; Delete may act on another row",
                    capturedItem.Id);
            }
        };

        _rowBorders[item.Id] = border;

        // Insert before the load-more footer if it exists
        if (_loadMoreFooter != null && _listPanel.Children.Contains(_loadMoreFooter))
            _listPanel.Children.Insert(_listPanel.Children.Count - 1, border);
        else
            _listPanel.Children.Add(border);
    }

    private void UpdateSelection(int newSelectedId)
    {
        var normalBg = AppTheme.Brush(AppTheme.CardBg);
        var selectedBg = AppTheme.Brush(ColorHelper.FromArgb(25, 0, 122, 255));

        // Deselect previous
        if (_selectedId.HasValue && _rowBorders.TryGetValue(_selectedId.Value, out var prevBorder))
        {
            prevBorder.Background = normalBg;
            prevBorder.BorderThickness = new Thickness(0);
            prevBorder.BorderBrush = null;
        }

        // Select new
        if (_rowBorders.TryGetValue(newSelectedId, out var newBorder))
        {
            newBorder.Background = selectedBg;
            newBorder.BorderThickness = new Thickness(3, 0, 0, 0);
            newBorder.BorderBrush = AppTheme.Brush(AppTheme.AccentBlue);
        }

        _selectedId = newSelectedId;
    }

    private void RefreshDetail()
    {
        if (_detailText == null || _detailMeta == null) return;

        // HIS-1: retire any in-flight preview decode from the previous selection —
        // its ImageOpened/ImageFailed callbacks check this before touching the pane.
        _detailImageLoadGeneration++;

        var t = _viewModel.SelectedTranscription;
        if (t == null)
        {
            _detailMeta!.Text = "";
            _detailText!.Text = "";
            _enhancedBadge!.Visibility = Visibility.Collapsed;
            _originalLabel!.Visibility = Visibility.Collapsed;
            _originalText!.Visibility = Visibility.Collapsed;
            _detailImage!.Visibility = Visibility.Collapsed;
            _detailImage.Source = null;
            _copyImageBtn!.Visibility = Visibility.Collapsed;
            _detailImageNote!.Visibility = Visibility.Collapsed;
            _detailPlaceholder!.Visibility = Visibility.Visible;
            return;
        }

        _detailPlaceholder!.Visibility = Visibility.Collapsed;
        _detailImage!.Visibility = Visibility.Collapsed;
        _detailImage.Source = null;
        _copyImageBtn!.Visibility = Visibility.Collapsed;
        _detailImageNote!.Visibility = Visibility.Collapsed;
        var modelInfo = ModelDisplayName.ResolveForHistory(t.ModelName);
        if (!string.IsNullOrEmpty(t.EnhancementModelName))
            modelInfo += $"  \u2192  {t.EnhancementModelName}";
        // Image generation items: always show all three knobs with explicit labels so the user
        // can see the full configuration that produced the image, with "Auto" for any unset slot.
        // IsNullOrWhiteSpace (not IsNullOrEmpty) so a corrupt all-whitespace persisted value
        // doesn't slip through. The quality label comes from the combo's own row table
        // (IMG-14): the persisted "maximum" tag renders as "High" there, and a bare title-case of
        // the tag would have shown "Xhigh" for the new tier.
        var imageDetails = "";
        if (IsImageHistoryItem(t))
        {
            var aspect  = string.IsNullOrWhiteSpace(t.ImageAspect) ? "Auto" : t.ImageAspect!;
            var size    = string.IsNullOrWhiteSpace(t.ImageSizeTier) ? "Auto" : t.ImageSizeTier!;
            var quality = ImageOptions.QualityLabelFor(t.ImageQuality);
            imageDetails = $"  \u2022  Aspect: {aspect}  \u2022  Size: {size}  \u2022  Quality: {quality}{DescribeImageFileSize(t.ImageFilePath)}";
        }
        _detailMeta!.Text = $"{t.Timestamp.ToLocalTime():MMM dd yyyy, HH:mm:ss}  \u2022  {modelInfo}{imageDetails}";

        var isImage = IsImageHistoryItem(t);
        var isFailedEnhancement = !isImage && t.WasEnhanced && t.EnhancedText == "[Enhancement failed]";
        var hasEnhancement = !isImage && t.WasEnhanced && !string.IsNullOrEmpty(t.EnhancedText) && !isFailedEnhancement;
        if (isImage)
        {
            // Image generation: show status badge + description (not the marker)
            var isFailed = t.EnhancedText == TranscriptionRecord.ImageFailedMarker;
            _enhancedBadge!.Visibility = Visibility.Visible;
            _enhancedBadge.Background = AppTheme.Brush(isFailed
                ? ColorHelper.FromArgb(51, 255, 69, 58)   // AccentRed at 20%
                : ColorHelper.FromArgb(51, 0, 122, 255)); // AccentBlue at 20%
            if (_enhancedBadge.Child is TextBlock imgBadgeText)
            {
                imgBadgeText.Text = isFailed ? "\u26A0 Image Generation Failed" : "\U0001F5BC Image Generated";
                imgBadgeText.Foreground = AppTheme.Brush(isFailed ? AppTheme.AccentRed : AppTheme.AccentBlue);
            }
            _detailText!.Text = t.Text; // the image description/prompt
            _originalLabel!.Visibility = Visibility.Collapsed;
            _originalText!.Visibility = Visibility.Collapsed;

            // Show the image preview if the file exists and is contained in the Images
            // dir; otherwise say WHY the slot is empty (HIS-1 — a successful row's
            // deleted file previously showed nothing at all). BitmapImage DECODES
            // ASYNCHRONOUSLY — a sync try/catch only sees URI errors, so corrupt
            // files previously left an empty preview too (Codex diff review): the
            // preview and Copy-Image button stay HIDDEN until ImageOpened, and
            // ImageFailed re-decides through the pinned helper with a FRESH
            // resolution probe (deleted-during-load → missing note; undecodable
            // content → undisplayable note). The generation fence keeps a stale
            // selection's callbacks out of the current detail view.
            var fileResolvable = TryResolveHistoryImagePath(t.ImageFilePath, out var previewPath);
            void ShowDetailImageNote()
            {
                _detailImage!.Visibility = Visibility.Collapsed;
                _copyImageBtn!.Visibility = Visibility.Collapsed;
                var stillResolvable = TryResolveHistoryImagePath(t.ImageFilePath, out _);
                _detailImageNote!.Text = HistoryImagePresentation.DecidePreview(
                        isImageItem: true, isFailedGeneration: false, stillResolvable, decodeFailed: true)
                    == HistoryImagePreview.MissingNote
                        ? HistoryImagePresentation.MissingImageNote
                        : HistoryImagePresentation.UndisplayableImageNote;
                _detailImageNote.Visibility = Visibility.Visible;
            }
            switch (HistoryImagePresentation.DecidePreview(
                isImageItem: true, isFailed, fileResolvable, decodeFailed: false))
            {
                case HistoryImagePreview.Preview:
                    var loadGeneration = _detailImageLoadGeneration;
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    bitmap.ImageOpened += (_, _) =>
                    {
                        if (loadGeneration != _detailImageLoadGeneration) return;
                        _detailImage!.Visibility = Visibility.Visible;
                        _copyImageBtn!.Visibility = Visibility.Visible;
                    };
                    bitmap.ImageFailed += (_, _) =>
                    {
                        if (loadGeneration != _detailImageLoadGeneration) return;
                        ShowDetailImageNote();
                    };
                    try
                    {
                        bitmap.UriSource = new Uri(previewPath);
                        _detailImage!.Source = bitmap;
                    }
                    catch
                    {
                        ShowDetailImageNote();
                    }
                    break;
                case HistoryImagePreview.MissingNote:
                    _detailImageNote!.Text = HistoryImagePresentation.MissingImageNote;
                    _detailImageNote.Visibility = Visibility.Visible;
                    break;
            }
        }
        else if (isFailedEnhancement)
        {
            // Show failed enhancement badge (same visual as failed image generation)
            _enhancedBadge!.Visibility = Visibility.Visible;
            _enhancedBadge.Background = AppTheme.Brush(ColorHelper.FromArgb(51, 255, 69, 58)); // AccentRed at 20%
            if (_enhancedBadge.Child is TextBlock failBadgeText)
            {
                var promptTitle = ResolvePromptTitle(t.PromptUsed);
                failBadgeText.Text = promptTitle != null
                    ? $"\u26A0 Enhancement Failed \u2022 {promptTitle}"
                    : "\u26A0 Enhancement Failed";
                failBadgeText.Foreground = AppTheme.Brush(AppTheme.AccentRed);
            }
            _detailText!.Text = t.Text;
            _originalLabel!.Visibility = Visibility.Collapsed;
            _originalText!.Visibility = Visibility.Collapsed;
        }
        else if (hasEnhancement)
        {
            // Show "Enhanced" badge + enhanced text, then "Original" label + raw text below
            _enhancedBadge!.Visibility = Visibility.Visible;
            _enhancedBadge.Background = AppTheme.Brush(ColorHelper.FromArgb(51, 48, 209, 88)); // AccentGreen at 20%
            if (_enhancedBadge.Child is TextBlock enhBadgeText)
            {
                // Show which enhancement was used (resolve prompt ID → title)
                var promptTitle = ResolvePromptTitle(t.PromptUsed);
                enhBadgeText.Text = promptTitle != null ? $"Enhanced \u2022 {promptTitle}" : "Enhanced";
                enhBadgeText.Foreground = AppTheme.Brush(AppTheme.AccentGreen);
            }
            _detailText!.Text = t.EnhancedText!;
            _originalLabel!.Visibility = Visibility.Visible;
            _originalText!.Text = t.Text;
            _originalText.Visibility = Visibility.Visible;
        }
        else
        {
            _enhancedBadge!.Visibility = Visibility.Collapsed;
            _detailText!.Text = t.Text;
            _originalLabel!.Visibility = Visibility.Collapsed;
            _originalText!.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Formats a UTC timestamp as a relative date: "Today HH:mm", "Yesterday HH:mm",
    /// or "MMM dd, HH:mm" for older dates.
    /// </summary>
    private static string FormatRelativeTimestamp(DateTime utcTimestamp)
    {
        var local = utcTimestamp.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
            return $"Today {local:HH:mm}";
        if (local.Date == today.AddDays(-1))
            return $"Yesterday {local:HH:mm}";
        return local.ToString("MMM dd, HH:mm");
    }

    private async Task DebounceSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            _viewModel.SearchQuery = query;
            _suppressCollectionRefreshDepth++;
            try
            {
                if (string.IsNullOrEmpty(query))
                    await _viewModel.LoadAsync();
                else
                    await _viewModel.SearchAsync();
            }
            finally
            {
                if (_suppressCollectionRefreshDepth > 0) _suppressCollectionRefreshDepth--;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshList();
                UpdateCountText();
            });
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Resolve a prompt ID/title to its display title, or null if not found.</summary>
    private string? ResolvePromptTitle(string? promptUsed)
    {
        if (string.IsNullOrWhiteSpace(promptUsed)) return null;
        var prompts = _enhancement.GetPrompts();
        var match = prompts.FirstOrDefault(p => p.Id == promptUsed)
                    ?? prompts.FirstOrDefault(p => p.Title == promptUsed);
        return match?.Title;
    }

    /// <summary>
    /// Look up the CustomPrompt that was used for a history item.
    /// PromptUsed stores either the prompt Id or Title.
    /// </summary>
    private CustomPrompt? ResolvePromptForItem(TranscriptionRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.PromptUsed))
            return null;

        var prompts = _enhancement.GetPrompts();
        // Try by Id first (MainViewModel stores Id), then by Title (older records)
        return prompts.FirstOrDefault(p => p.Id == item.PromptUsed)
            ?? prompts.FirstOrDefault(p => p.Title == item.PromptUsed);
    }

    /// <summary>
    /// Detect whether a history item represents an image generation (successful or failed).
    /// </summary>
    private static bool IsImageHistoryItem(TranscriptionRecord item)
        => item.WasEnhanced && (item.EnhancedText == TranscriptionRecord.ImageGeneratedMarker || item.EnhancedText == TranscriptionRecord.ImageFailedMarker);

    /// <summary>
    /// Human file size. KB below 1 MB, MB above.
    /// <para>Everything used to render as MB with one decimal, so riverflow's 17–61 KB WebP images
    /// all displayed as "0.0 MB" — which reads as data loss when the file is perfectly intact.
    /// Pure so the boundaries are pinned by tests.</para>
    /// </summary>
    internal static string FormatFileSize(long bytes)
    {
        const long OneKb = 1024;
        const long OneMb = 1024 * 1024;
        if (bytes < OneKb) return $"{bytes} bytes";
        if (bytes < OneMb) return $"{bytes / (double)OneKb:0.#} KB";
        return $"{bytes / (double)OneMb:0.0} MB";
    }

    /// <summary>
    /// "  •  2.6 MB" for the generated image file, empty when the path is absent or the
    /// file is missing/unreadable — the size is cosmetic metadata and must never fail
    /// the detail pane (failed generations have no image file at all). Stats only through
    /// the F25 containment gate: the persisted path is a free-form DB string, and a
    /// corrupt/imported row must not make the detail pane touch an arbitrary local or UNC
    /// path (UI stall / outbound SMB auth); the gate rejects non-contained paths lexically
    /// before any file I/O.
    /// </summary>
    private static string DescribeImageFileSize(string? imageFilePath)
    {
        try
        {
            // Size read from the SAME verified handle the gate opened — a separate
            // FileInfo lookup on the returned path would re-resolve it (small TOCTOU).
            using var stream = ReferenceImagePolicy.TryOpenVerifiedAppImage(imageFilePath, AppPaths.ImagesDir);
            if (stream == null || stream.Length <= 0) return string.Empty;
            return "  •  " + FormatFileSize(stream.Length);
        }
        catch
        {
            return string.Empty;
        }
    }
}
