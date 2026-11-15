using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.Data;
using VoiceWink.Services.System;
using VoiceWink.Services.TextProcessing;

namespace VoiceWink.ViewModels;

/// <summary>
/// ViewModel for dictionary page — manages vocabulary + word replacements.
/// </summary>
public partial class DictionaryViewModel : ObservableObject
{
    private static ILogger Logger => Log.ForContext<DictionaryViewModel>();

    private readonly CustomVocabularyService _vocabulary;
    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly WordReplacementService _wordReplacementService;

    [ObservableProperty] private ObservableCollection<VocabularyWord> _vocabularyWords = new();
    [ObservableProperty] private ObservableCollection<WordReplacement> _wordReplacements = new();
    [ObservableProperty] private string _newVocabularyWord = "";
    [ObservableProperty] private string _newReplacementOriginal = "";
    [ObservableProperty] private string _newReplacementText = "";

    /// <summary>DCT-5: ids of the rules that form a loop with other rules, refreshed on load and delete; listed, never deleted.</summary>
    public IReadOnlySet<int> LoopingReplacementIds { get; private set; } = new HashSet<int>();

    private readonly ImportExportService? _backup;

    public DictionaryViewModel(
        CustomVocabularyService vocabulary,
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        WordReplacementService wordReplacementService,
        ImportExportService? backup = null)
    {
        _vocabulary = vocabulary;
        _dbFactory = dbFactory;
        _wordReplacementService = wordReplacementService;
        _backup = backup;
    }

    private ImportExportService Backup
        => _backup ?? throw new InvalidOperationException("Dictionary backup isn't available in this session.");

    /// <summary>Writes the Dictionary alone to a backup file.</summary>
    public Task ExportAsync(string filePath) => Backup.ExportAsync(filePath, BackupParts.Dictionary);

    /// <summary>Merges the Dictionary of any backup file in, then reloads the lists.</summary>
    public async Task<ImportExportService.ImportResult> ImportAsync(string filePath)
    {
        var result = await Backup.ImportAsync(filePath, BackupParts.Dictionary);
        // The rows are committed; a failed refresh must not report the import as failed.
        try { await LoadAsync(); }
        catch (Exception ex) { Logger.Warning(ex, "Dictionary lists could not be reloaded after an import"); }
        return result;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var words = await _vocabulary.GetAllAsync();
        words.Sort((a, b) => string.Compare(a.Word, b.Word, StringComparison.OrdinalIgnoreCase));
        VocabularyWords.Clear();
        foreach (var w in words) VocabularyWords.Add(w);

        using var db = _dbFactory.CreateDbContext();
        var replacements = await db.WordReplacements.AsNoTracking().ToListAsync();
        replacements.Sort((a, b) => string.Compare(a.OriginalText, b.OriginalText, StringComparison.OrdinalIgnoreCase));
        // Set before the collection changes, so the page's rebuild reads the set for these rows.
        LoopingReplacementIds = ReplacementLoops.LoopingRuleIds(replacements.Select(ToRule).ToList());
        WordReplacements.Clear();
        foreach (var r in replacements) WordReplacements.Add(r);
    }

    /// <summary>Command form over <see cref="NewVocabularyWord"/>; clears it only on a save (DCT-10).</summary>
    [RelayCommand]
    public async Task AddVocabularyWordAsync()
    {
        if (await AddVocabularyWordAsync(NewVocabularyWord))
            NewVocabularyWord = "";
    }

    /// <summary>Adds the given word; false when it is blank or already in the list (DCT-10, argument form as DCT-2's replacements).</summary>
    public async Task<bool> AddVocabularyWordAsync(string word)
    {
        if (!await _vocabulary.AddAsync(word)) return false;
        await LoadAsync();
        return true;
    }

    [RelayCommand]
    public async Task DeleteVocabularyWordAsync(VocabularyWord word)
    {
        await _vocabulary.DeleteAsync(word.Id);
        VocabularyWords.Remove(word);
    }

    /// <summary>Command form over the bound properties — kept for the binding-style callers and
    /// the existing tests; the page calls <see cref="AddReplacementAsync(string, string)"/> with
    /// its boxes' text directly (DCT-2, see there).</summary>
    [RelayCommand]
    public async Task AddReplacementAsync()
    {
        if ((await AddReplacementAsync(NewReplacementOriginal, NewReplacementText)).Saved)
        {
            NewReplacementOriginal = "";
            NewReplacementText = "";
        }
    }

    /// <summary>
    /// Add a replacement from the given text. Writes nothing when a field is blank or the rule
    /// would loop (DCT-5). Takes its inputs as ARGUMENTS on purpose (DCT-2 self-review, lens A): this view model
    /// is a DI singleton while the page is created per navigation, so text mirrored into
    /// <see cref="NewReplacementOriginal"/> / <see cref="NewReplacementText"/> outlived the page
    /// that typed it — navigate away with the boxes filled (or an edit loaded), come back to empty
    /// boxes, press Add, and a row with the stale text appeared from a visibly empty input. The
    /// page no longer mirrors its boxes into the properties at all.
    /// </summary>
    public async Task<ReplacementSaveResult> AddReplacementAsync(string original, string replacementText)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacementText))
            return ReplacementSaveResult.NotSaved;

        using var db = _dbFactory.CreateDbContext();
        var loop = await FindLoopAsync(db, original, replacementText, excludeId: null);
        if (loop.Count > 0)
            return new ReplacementSaveResult(false, loop);

        db.WordReplacements.Add(new WordReplacement
        {
            OriginalText = original.Trim(),
            ReplacementText = replacementText.Trim()
        });
        await db.SaveChangesAsync();
        _wordReplacementService.InvalidateCache();

        await LoadAsync();
        return ReplacementSaveResult.Done;
    }

    /// <summary>
    /// DCT-2 (2026-09-13): edit an existing replacement in place — the row used to offer only a
    /// toggle and a delete, so changing one variant of a six-variant rule meant retyping the row.
    /// Same validation as <see cref="AddReplacementAsync"/> (both fields non-blank, trimmed), same
    /// cache invalidation, same reload (the list is sorted by original, so an edit may move the
    /// row). Changes nothing when a field is blank, the edit would loop (DCT-5) or the row no longer exists
    /// (deleted elsewhere between the edit starting and Save) — the page keeps the boxes filled
    /// so the user's typing is not lost. A plain method rather than a command: the page needs
    /// the verdict to decide whether to leave edit mode. <c>IsEnabled</c> is untouched — editing
    /// a switched-off rule leaves it off.
    /// </summary>
    public async Task<ReplacementSaveResult> UpdateReplacementAsync(WordReplacement replacement, string original, string replacementText)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacementText))
            return ReplacementSaveResult.NotSaved;

        using var db = _dbFactory.CreateDbContext();
        var entity = await db.WordReplacements.FindAsync(replacement.Id);
        if (entity == null)
            return ReplacementSaveResult.NotSaved;

        var loop = await FindLoopAsync(db, original, replacementText, excludeId: entity.Id);
        if (loop.Count > 0)
            return new ReplacementSaveResult(false, loop);

        entity.OriginalText = original.Trim();
        entity.ReplacementText = replacementText.Trim();
        await db.SaveChangesAsync();
        _wordReplacementService.InvalidateCache();

        await LoadAsync();
        return ReplacementSaveResult.Done;
    }

    /// <summary>DCT-5: the stored rules a new or edited rule would loop through; switched-off rules count, so switching one on needs no check.</summary>
    private static async Task<IReadOnlyList<WordReplacement>> FindLoopAsync(
        VoiceWinkDbContext db, string original, string replacementText, int? excludeId)
    {
        var rows = await db.WordReplacements.AsNoTracking().ToListAsync();
        var byId = rows.ToDictionary(r => r.Id);
        return ReplacementLoops.FindLoop(rows.Select(ToRule), original, replacementText, excludeId)
            .Select(rule => byId[rule.Id])
            .Distinct()
            .ToList();
    }

    private static ReplacementLoops.Rule ToRule(WordReplacement r) => new(r.Id, r.OriginalText, r.ReplacementText);

    [RelayCommand]
    public async Task DeleteReplacementAsync(WordReplacement replacement)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = await db.WordReplacements.FindAsync(replacement.Id);
        if (entity != null)
        {
            db.WordReplacements.Remove(entity);
            await db.SaveChangesAsync();
            _wordReplacementService.InvalidateCache();
        }
        // A deleted rule may have been the one closing a loop; set before the collection changes.
        LoopingReplacementIds = ReplacementLoops.LoopingRuleIds(
            WordReplacements.Where(r => r.Id != replacement.Id).Select(ToRule).ToList());
        WordReplacements.Remove(replacement);
    }

    [RelayCommand]
    public async Task ToggleReplacementAsync(WordReplacement replacement)
    {
        replacement.IsEnabled = !replacement.IsEnabled;
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = await db.WordReplacements.FindAsync(replacement.Id);
            if (entity != null)
            {
                entity.IsEnabled = replacement.IsEnabled;
                await db.SaveChangesAsync();
                _wordReplacementService.InvalidateCache();
            }
        }
        catch (Exception ex)
        {
            // Revert in-memory state so it matches the database
            replacement.IsEnabled = !replacement.IsEnabled;
            // SEC-3: dictionary entries are user-authored (often personal names) — the same class
            // as {TriggerWord}, already on the allowlist. This one is Logger.ERROR, so it becomes
            // a direct Sentry EVENT, not merely a breadcrumb. Token last, EOL-scrubbed.
            Logger.Error(ex, "Failed to save toggle state for replacement: dictTerm={Original}",
                Helpers.LogValueSanitizer.SingleLine(replacement.OriginalText));
            throw; // Re-throw so the UI layer can show feedback
        }
    }
}

/// <summary>DCT-5: a save's outcome; <see cref="LoopsWith"/> names the rules a refused rule would loop through.</summary>
public sealed record ReplacementSaveResult(bool Saved, IReadOnlyList<WordReplacement> LoopsWith)
{
    public static ReplacementSaveResult Done { get; } = new(true, []);
    public static ReplacementSaveResult NotSaved { get; } = new(false, []);
}
