using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Models.Entities;
using VoiceWink.Services.Data;
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

    public DictionaryViewModel(
        CustomVocabularyService vocabulary,
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        WordReplacementService wordReplacementService)
    {
        _vocabulary = vocabulary;
        _dbFactory = dbFactory;
        _wordReplacementService = wordReplacementService;
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
        WordReplacements.Clear();
        foreach (var r in replacements) WordReplacements.Add(r);
    }

    [RelayCommand]
    public async Task AddVocabularyWordAsync()
    {
        if (string.IsNullOrWhiteSpace(NewVocabularyWord)) return;
        await _vocabulary.AddAsync(NewVocabularyWord);
        NewVocabularyWord = "";
        await LoadAsync();
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
        if (await AddReplacementAsync(NewReplacementOriginal, NewReplacementText))
        {
            NewReplacementOriginal = "";
            NewReplacementText = "";
        }
    }

    /// <summary>
    /// Add a replacement from the given text. Returns false and writes nothing when a field is
    /// blank. Takes its inputs as ARGUMENTS on purpose (DCT-2 self-review, lens A): this view model
    /// is a DI singleton while the page is created per navigation, so text mirrored into
    /// <see cref="NewReplacementOriginal"/> / <see cref="NewReplacementText"/> outlived the page
    /// that typed it — navigate away with the boxes filled (or an edit loaded), come back to empty
    /// boxes, press Add, and a row with the stale text appeared from a visibly empty input. The
    /// page no longer mirrors its boxes into the properties at all.
    /// </summary>
    public async Task<bool> AddReplacementAsync(string original, string replacementText)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacementText))
            return false;

        using var db = _dbFactory.CreateDbContext();
        db.WordReplacements.Add(new WordReplacement
        {
            OriginalText = original.Trim(),
            ReplacementText = replacementText.Trim()
        });
        await db.SaveChangesAsync();
        _wordReplacementService.InvalidateCache();

        await LoadAsync();
        return true;
    }

    /// <summary>
    /// DCT-2 (2026-09-13): edit an existing replacement in place — the row used to offer only a
    /// toggle and a delete, so changing one variant of a six-variant rule meant retyping the row.
    /// Same validation as <see cref="AddReplacementAsync"/> (both fields non-blank, trimmed), same
    /// cache invalidation, same reload (the list is sorted by original, so an edit may move the
    /// row). Returns false and changes nothing when a field is blank or the row no longer exists
    /// (deleted elsewhere between the edit starting and Save) — the page keeps the boxes filled
    /// so the user's typing is not lost. A plain method rather than a command: the page needs
    /// the verdict to decide whether to leave edit mode. <c>IsEnabled</c> is untouched — editing
    /// a switched-off rule leaves it off.
    /// </summary>
    public async Task<bool> UpdateReplacementAsync(WordReplacement replacement, string original, string replacementText)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(replacementText))
            return false;

        using var db = _dbFactory.CreateDbContext();
        var entity = await db.WordReplacements.FindAsync(replacement.Id);
        if (entity == null)
            return false;

        entity.OriginalText = original.Trim();
        entity.ReplacementText = replacementText.Trim();
        await db.SaveChangesAsync();
        _wordReplacementService.InvalidateCache();

        await LoadAsync();
        return true;
    }

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
