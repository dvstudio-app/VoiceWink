using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.TextProcessing;

namespace VoiceWink.Services.Data;

/// <summary>The Dictionary as it travels in a backup file.</summary>
public sealed record DictionarySnapshot(IReadOnlyList<string> Words, IReadOnlyList<DictionaryRule> Rules);

/// <summary>What a Dictionary import did. <c>AlreadyPresent</c> counts entries this PC already had
/// (a rule counts when every spelling it names is already corrected here).</summary>
public sealed record DictionaryMergeResult(int WordsAdded, int RulesAdded, int AlreadyPresent);

/// <summary>
/// Reads the Dictionary for a backup and merges a backup's Dictionary in — the database half of
/// <see cref="System.ImportExportService"/>, which owns the file. A merge only ADDS: it never
/// deletes or changes a row this PC has (<see cref="DictionaryMerge"/> decides what is new).
/// Logs counts only — Dictionary entries are user-authored, often personal names (SEC-3).
/// </summary>
public sealed class DictionaryTransfer
{
    private static ILogger Logger => Log.ForContext<DictionaryTransfer>();

    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly WordReplacementService _wordReplacementService;

    public DictionaryTransfer(IDbContextFactory<VoiceWinkDbContext> dbFactory, WordReplacementService wordReplacementService)
    {
        _dbFactory = dbFactory;
        _wordReplacementService = wordReplacementService;
    }

    /// <summary>The Dictionary for a backup, minus the shipped defaults (<see cref="WithoutShippedDefaults"/>).</summary>
    public async Task<DictionarySnapshot> ReadAsync()
    {
        using var db = _dbFactory.CreateDbContext();
        var words = await db.VocabularyWords.AsNoTracking().OrderBy(w => w.Id).Select(w => w.Word).ToListAsync().ConfigureAwait(false);
        var rules = await db.WordReplacements.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new DictionaryRule(r.OriginalText, r.ReplacementText, r.IsEnabled))
            .ToListAsync().ConfigureAwait(false);
        return WithoutShippedDefaults(new DictionarySnapshot(words, rules));
    }

    /// <summary>
    /// Owner decision 2026-10-25: a backup never brings back a shipped default the user deleted on
    /// this PC. Every install seeds the defaults itself, so they are left out of an export AND
    /// out of an import (a hand-edited file included): a default word exactly as shipped — a
    /// word the user cased their own way still travels — and a rule exactly as the seed writes it.
    /// A user's own rule identical to a shipped one is indistinguishable and is left out too; the
    /// other PC's seed carries the same correction unless the user deleted it there.
    /// </summary>
    internal static DictionarySnapshot WithoutShippedDefaults(DictionarySnapshot snapshot)
        => new(
            snapshot.Words.Where(w => !DefaultDictionary.VocabularyWords.Contains(w.Trim())).ToList(),
            snapshot.Rules.Where(r => !DefaultDictionary.IsUnchangedDefaultRule(r.OriginalText, r.ReplacementText, r.IsEnabled)).ToList());

    /// <summary>Adds what is new in ONE <c>SaveChanges</c>, so a failure adds nothing.</summary>
    public async Task<DictionaryMergeResult> MergeAsync(DictionarySnapshot incoming)
    {
        using var db = _dbFactory.CreateDbContext();
        var plan = DictionaryMerge.Decide(
            await db.VocabularyWords.Select(w => w.Word).ToListAsync().ConfigureAwait(false),
            await db.WordReplacements.Select(r => r.OriginalText).ToListAsync().ConfigureAwait(false),
            incoming.Words,
            incoming.Rules);
        // Filter what is about to be WRITTEN (trimmed, minus covered spellings), not what came
        // in: " Deepgram " or an extended rule this PC partly covers would otherwise reduce to a
        // shipped default after the merge and bring it back (Codex + Kimi + Gemini diff r1).
        var toAdd = WithoutShippedDefaults(new DictionarySnapshot(plan.WordsToAdd, plan.RulesToAdd));

        foreach (var word in toAdd.Words)
            db.VocabularyWords.Add(new VocabularyWord { Word = word });
        foreach (var rule in toAdd.Rules)
            db.WordReplacements.Add(new WordReplacement
            {
                OriginalText = rule.OriginalText,
                ReplacementText = rule.ReplacementText,
                IsEnabled = rule.IsEnabled,
            });

        if (toAdd.Words.Count + toAdd.Rules.Count > 0)
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            _wordReplacementService.InvalidateCache();
        }

        Logger.Information("Dictionary imported: {WordsAdded} words and {RulesAdded} rules added, {AlreadyPresent} already present",
            toAdd.Words.Count, toAdd.Rules.Count, plan.AlreadyPresent);
        return new DictionaryMergeResult(toAdd.Words.Count, toAdd.Rules.Count, plan.AlreadyPresent);
    }
}
