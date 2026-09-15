using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VoiceWink.Models.Entities;
using VoiceWink.Services.Data;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for transcription history.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<HistoryViewModel>();

    private readonly TranscriptionHistoryService _history;
    private readonly CsvExportService _csvExport;

    // (Removed 2026-07-29: the TranscriptionsDeleted event had no subscribers and its
    // semantics were inconsistent — raised for some single-row deletes, always for
    // Delete-all here, only on nonzero type deletions, and never for Settings' direct
    // service call. Home stays in sync via the service's HistoryChanged; bulk deletions
    // now notify through TranscriptionHistoryService.BulkDeleteCommitted, which every
    // surface passes through.)

    [ObservableProperty]
    private ObservableCollection<TranscriptionRecord> _transcriptions = new();

    [ObservableProperty]
    private TranscriptionRecord? _selectedTranscription;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private int _currentPage;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private int _totalCount;

    /// <summary>True when the current view is a search result (not the full list).</summary>
    [ObservableProperty]
    private bool _isSearchActive;

    private int _searchPage;

    /// <summary>null = all, true = images only, false = text only.</summary>
    private bool? _typeFilter;

    private const int PageSize = 50;

    // IMG-4b freshness fence: incremental batch commits raise HistoryChanged once PER
    // ROW in quick succession, and each event starts an independent async refresh whose
    // DB awaits interleave on the dispatcher — without a fence, an OLDER snapshot can
    // apply after a newer one and hide just-committed versions. Every replace flow bumps
    // and captures the generation at entry and abandons its apply when superseded;
    // deletes bump it BEFORE their service mutation so a held stale snapshot can never
    // resurrect a deleted row; LoadMore neither bumps nor runs unless the collection on
    // screen IS the newest generation (see below). UI-thread-affine (dispatcher handlers
    // + sequential tests) — no synchronization.
    private int _loadGeneration;

    // The generation whose data is actually ON SCREEN — advanced ONLY by a completed
    // apply (a replacement that superseded, threw, or abandoned leaves it behind).
    // LoadMore requires _loadGeneration == _appliedGeneration, i.e. no newer replacement
    // is pending: appending onto a snapshot that is about to be replaced would let the
    // replacement clear the appended page while the page counter keeps pointing past it,
    // skipping that page until the next full reload (Codex diff r3 — the earlier
    // "settled" marker advanced on failure too, which reopened exactly that hole).
    private int _appliedGeneration;

    // Test seam (IMG-4b fence rows): awaited after a load/search's ITEMS query so a test
    // can hold a stale snapshot while a newer refresh completes. Assigned by tests only —
    // always null in production (the explicit initializer is what marks that intent).
    internal Func<Task>? _afterItemsQueryHook = null;

    public HistoryViewModel(TranscriptionHistoryService history, CsvExportService csvExport)
    {
        _history = history;
        _csvExport = csvExport;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        _typeFilter = null; // Reset filter on fresh page load (page UI defaults to "All")
        await LoadAsync(null);
    }

    public async Task RefreshAsync()
    {
        var selectedId = SelectedTranscription?.Id;

        if (IsSearchActive && !string.IsNullOrWhiteSpace(SearchQuery))
        {
            var loadedPages = Math.Max(1, _searchPage + 1);
            await SearchAsync(selectedId, loadedPages * PageSize, loadedPages - 1);
        }
        else
        {
            var loadedPages = Math.Max(1, CurrentPage + 1);
            await LoadAsync(selectedId, loadedPages * PageSize, loadedPages - 1);
        }
    }

    private async Task LoadAsync(int? selectedIdToRestore)
    {
        await LoadAsync(selectedIdToRestore, PageSize, 0);
    }

    private async Task LoadAsync(int? selectedIdToRestore, int pageSize, int currentPageToRestore)
    {
        var generation = ++_loadGeneration;
        IsSearchActive = false;
        CurrentPage = currentPageToRestore;
        var items = await _history.GetPageAsync(0, pageSize, _typeFilter);
        if (_afterItemsQueryHook != null)
            await _afterItemsQueryHook();
        var totalCount = await _history.GetCountAsync(_typeFilter);
        if (generation != _loadGeneration)
            return; // superseded by a newer load/search — a stale snapshot must not apply

        TotalCount = totalCount;
        Transcriptions.Clear();
        foreach (var item in items)
            Transcriptions.Add(item);

        HasMore = pageSize == PageSize
            ? items.Count == PageSize
            : TotalCount > items.Count;
        SelectedTranscription = selectedIdToRestore.HasValue
            ? Transcriptions.FirstOrDefault(t => t.Id == selectedIdToRestore.Value)
            : null;
        MarkApplied(generation); // ONLY a completed apply — a throw above leaves it behind
    }

    /// <summary>Publish "the collection now shows this generation" (monotonic — a held
    /// older flow can never drag the marker backwards).</summary>
    private void MarkApplied(int generation)
    {
        if (generation > _appliedGeneration)
            _appliedGeneration = generation;
    }

    /// <summary>Set the type filter and reload from DB.</summary>
    /// <param name="imagesOnly">null = all, true = images only, false = text only</param>
    public async Task SetTypeFilterAsync(bool? imagesOnly)
    {
        _typeFilter = imagesOnly;
        await LoadAsync(null);
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        // An append composes only with the APPLIED snapshot: while a newer replacement is
        // pending — in flight, or one that threw and never applied — refuse outright.
        // Its apply would clear the appended page while the page counter keeps pointing
        // past it, permanently skipping that page (the user just scrolls again once the
        // list has settled; Codex diff r2 + r3).
        if (_loadGeneration != _appliedGeneration)
            return;
        // Captured WITHOUT bumping: a replacement that enters AFTER this point read the
        // incremented page counter and loads these rows itself — appending would then
        // duplicate them, so abandon. The counter stays as-is: it correctly describes
        // what that replacement loaded.
        var generation = _loadGeneration;
        if (IsSearchActive)
        {
            _searchPage++;
            var items = await _history.SearchAsync(SearchQuery, _searchPage, PageSize);
            if (generation != _loadGeneration)
                return;

            foreach (var item in items)
                Transcriptions.Add(item);

            HasMore = items.Count == PageSize;
        }
        else
        {
            CurrentPage++;
            var items = await _history.GetPageAsync(CurrentPage, PageSize, _typeFilter);
            if (generation != _loadGeneration)
                return;

            foreach (var item in items)
                Transcriptions.Add(item);

            HasMore = items.Count == PageSize;
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        await SearchAsync(null);
    }

    private async Task SearchAsync(int? selectedIdToRestore)
    {
        await SearchAsync(selectedIdToRestore, PageSize, 0);
    }

    private async Task SearchAsync(int? selectedIdToRestore, int pageSize, int searchPageToRestore)
    {
        var generation = ++_loadGeneration;
        IsSearchActive = true;
        _searchPage = searchPageToRestore;
        var items = await _history.SearchAsync(SearchQuery, 0, pageSize);
        if (_afterItemsQueryHook != null)
            await _afterItemsQueryHook();
        var totalCount = await _history.SearchCountAsync(SearchQuery);
        if (generation != _loadGeneration)
            return; // superseded — see LoadAsync's fence

        TotalCount = totalCount;
        Transcriptions.Clear();
        foreach (var item in items)
            Transcriptions.Add(item);

        HasMore = pageSize == PageSize
            ? items.Count == PageSize
            : TotalCount > items.Count;
        SelectedTranscription = selectedIdToRestore.HasValue
            ? Transcriptions.FirstOrDefault(t => t.Id == selectedIdToRestore.Value)
            : null;
        MarkApplied(generation);
    }

    [RelayCommand]
    public async Task DeleteAsync(TranscriptionRecord transcription)
    {
        // Join the freshness protocol (Codex diff r2/r3): invalidate any held stale
        // snapshot BEFORE the mutation — it must not resurrect the deleted row — and
        // publish the applied marker only AFTER the local delta lands, so an append can
        // never compose with a half-applied delete. A throwing delete leaves the marker
        // behind: the collection state is unknown, so LoadMore stays refused until a
        // replacement re-establishes it. The local delta applies only when this exact
        // instance is still displayed (a completed refresh holds NEW entity instances;
        // the event-driven refresh that follows every delete re-derives the
        // authoritative list and count either way).
        var generation = ++_loadGeneration;
        await _history.DeleteAsync(transcription.Id);
        if (Transcriptions.Remove(transcription))
            TotalCount--;
        if (SelectedTranscription == transcription)
            SelectedTranscription = null;
        MarkApplied(generation);
        // (Home's "Last transcription" is kept in sync by the service's HistoryChanged →
        // SyncLastTranscriptionWithHistoryAsync wiring in App; the old TranscriptionsDeleted
        // event and its newest-row probe were removed as dead weight — nothing subscribed.)
    }

    [RelayCommand]
    public async Task DeleteAllAsync()
    {
        // Same protocol join as DeleteAsync: a held stale snapshot must not repopulate
        // the list after the wipe, and the applied marker publishes only once it has.
        var generation = ++_loadGeneration;
        await _history.DeleteAllAsync();
        Transcriptions.Clear();
        SelectedTranscription = null;
        TotalCount = 0;
        MarkApplied(generation);
        // Deleting history no longer deletes the application log (launch defaults audit,
        // 2026-09-13). The log holds no dictated text — that is the REL-17 prompt trace's job,
        // and it is a separate opt-in with its own sweep — so the only thing this coupling did
        // was destroy the evidence for the support case a user is most likely to raise right
        // after clearing history. The Log Viewer's Clear button is the deliberate way to wipe
        // the log; GDPR erasure still covers it.
    }

    [RelayCommand]
    public async Task ExportCsvAsync(string filePath)
    {
        var all = await _history.GetPageAsync(0, int.MaxValue);
        await _csvExport.ExportToFileAsync(all, filePath);
        Logger.Information("Exported {Count} transcriptions to CSV", all.Count);
    }

    /// <summary>Returns CSV content as a string (for writing via StorageFile API).</summary>
    public async Task<string> ExportCsvContentAsync()
    {
        var all = await _history.GetPageAsync(0, int.MaxValue);
        Logger.Information("Exported {Count} transcriptions to CSV", all.Count);
        return _csvExport.ExportToCsv(all);
    }

    /// <summary>Returns CSV content for all items matching a type filter (queries full DB).</summary>
    public async Task<string> ExportCsvFilteredAsync(bool imagesOnly)
    {
        var all = await _history.GetPageAsync(0, int.MaxValue, imagesOnly);
        Logger.Information("Exported {Count} filtered transcriptions to CSV ({Type})", all.Count, imagesOnly ? "images" : "text");
        return _csvExport.ExportToCsv(all);
    }

    /// <summary>Delete all image or all text records from the database.</summary>
    public async Task DeleteByTypeAsync(bool imagesOnly)
    {
        // Leave SEARCH mode (and page 0) BEFORE the delete (F39 + Codex R1/R2 ordering): the
        // service raises HistoryChanged from inside the delete call, and the page's queued
        // handler runs RefreshAsync — which must already observe the post-search, page-0
        // state, or its stale load races the type-filtered reload below. Restored if the
        // delete fails — the view genuinely still shows the search results then.
        var wasSearchActive = IsSearchActive;
        var priorSearchPage = _searchPage;
        var priorCurrentPage = CurrentPage;
        IsSearchActive = false;
        _searchPage = 0;
        CurrentPage = 0;
        int count;
        try
        {
            count = imagesOnly
                ? await _history.DeleteAllImagesAsync()
                : await _history.DeleteAllTextAsync();
        }
        catch
        {
            IsSearchActive = wasSearchActive;
            _searchPage = priorSearchPage;
            CurrentPage = priorCurrentPage;
            throw;
        }
        // Reload through the SHARED non-search load (keeps _typeFilter): it queries first and
        // replaces the collection only after both awaits, atomically on the UI thread — the
        // old manual clear-await-append block here could interleave with the event-triggered
        // RefreshAsync (clear → event refresh replaces → append = duplicate rows, Codex R2).
        // Now both flows are whole-collection replacements; any completion order converges.
        await LoadAsync(null);
    }

}
