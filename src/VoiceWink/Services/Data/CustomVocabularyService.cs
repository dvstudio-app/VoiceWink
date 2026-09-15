using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// CRUD for custom vocabulary words.
/// Words ride as keyterms/keywords to Deepgram, ElevenLabs and gpt-transcribe and as the
/// enhancement prompt's vocabulary block; the Whisper family and Parakeet take no hints
/// (TRN-11 — this line said "Whisper prompt" until DCT-1).
/// </summary>
public sealed class CustomVocabularyService
{
    private static ILogger Logger => Log.ForContext<CustomVocabularyService>();

    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly SettingsService _settings;

    public CustomVocabularyService(IDbContextFactory<VoiceWinkDbContext> dbFactory, SettingsService settings)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    public async Task<List<VocabularyWord>> GetAllAsync(CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.VocabularyWords.OrderBy(w => w.Word).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(string word, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        var normalizedWord = word.Trim();

        using var db = _dbFactory.CreateDbContext();
        var exists = await db.VocabularyWords.AnyAsync(w => w.Word == normalizedWord, ct).ConfigureAwait(false);
        if (exists) return;

        db.VocabularyWords.Add(new VocabularyWord { Word = normalizedWord });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // The LENGTH, never the word: Debug lines reach the local file whenever the Log Viewer's
        // verbose toggle is on (launch defaults audit, 2026-09-13 — the toggle used to change
        // nothing the sink kept, which is the only reason this line ever rendered the word
        // safely), and the file ships in the default support bundle and the GDPR export through
        // a line-by-line scrub that has no token for an untagged value. Dictionary entries are
        // user-authored and frequently personal names (the SEC-3 class); privacy-v5 §7 promises
        // they ride only the raw-trace opt-in. Pinned by LogLevelControlTests.
        Logger.Debug("Added vocabulary word ({Length} chars)", normalizedWord.Length);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var item = await db.VocabularyWords.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (item != null)
        {
            db.VocabularyWords.Remove(item);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the vocabulary terms for a transcription pass from a single DB query,
    /// in INSERTION ORDER (<c>OrderBy(Id)</c> — deterministic, so provider budget
    /// overflow always drops the same tail, PRM-3) — except that the shipped defaults
    /// (<see cref="DefaultDictionary"/>, DCT-1) come LAST, after every word the user
    /// typed, whatever their Ids: on a fresh install the seed takes the lowest Ids, and
    /// a provider cap (100 keyterms) must drop "Whisper Tiny" before the user's own
    /// names. Decided by membership, not a stored flag. The raw list feeds BOTH surfaces:
    /// <see cref="Models.TranscriptionHints.FromTerms"/> for transcription biasing
    /// and <see cref="Models.AIPrompts"/> for the enhancement vocabulary block (which
    /// owns all labeling/sanitization).
    /// This is the RUNTIME, master-gated context: it returns EMPTY while the
    /// Dictionary-page "Enable Dictionary" toggle is off (read per call, same idiom
    /// as the per-provider transport Funcs — a flip takes effect on the next stage
    /// that reads it, stage-by-stage semantics). Data consumers (the Dictionary page
    /// UI via <see cref="GetAllAsync"/>, GDPR export via its own direct DB query) are
    /// deliberately NOT gated and must not switch to this method.
    /// </summary>
    public async Task<VocabularyContext> GetVocabularyContextAsync(CancellationToken ct = default)
    {
        if (!_settings.GetBool(AppDefaults.DictionaryEnabled, true))
            return new VocabularyContext(global::System.Array.Empty<string>());

        using var db = _dbFactory.CreateDbContext();
        var words = await db.VocabularyWords
            .OrderBy(w => w.Id)
            .Select(w => w.Word)
            .ToListAsync(ct).ConfigureAwait(false);
        if (words.Count == 0)
            return new VocabularyContext(global::System.Array.Empty<string>());

        // Stable partition: the user's own words keep their insertion order, then the shipped
        // defaults keep theirs (DCT-1 — see the summary).
        var ordered = new List<string>(words.Count);
        ordered.AddRange(words.Where(w => !DefaultDictionary.IsDefaultWord(w)));
        ordered.AddRange(words.Where(DefaultDictionary.IsDefaultWord));
        return new VocabularyContext(ordered);
    }

    /// <summary>The ordered raw vocabulary word list from a single DB query.</summary>
    public sealed record VocabularyContext(IReadOnlyList<string> Terms);
}
