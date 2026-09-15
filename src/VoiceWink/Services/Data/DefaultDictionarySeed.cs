using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Data;

/// <summary>
/// DCT-1 — writes <see cref="DefaultDictionary"/> into the Dictionary tables ONCE per default,
/// at launch, after the database is initialised and before anything reads the Dictionary.
///
/// <para><b>The rule is "offer each default once", not "keep the defaults present".</b> A
/// settings-stored record (<see cref="AppDefaults.DictionaryDefaultsOffered"/>) lists every
/// default this install has ever been offered; a run adds only the defaults NOT in that record,
/// then records them. So a default the user deletes stays deleted — it is in the record, and the
/// record is never consulted for presence — while a later release that adds a word offers exactly
/// that word. This is the same "acknowledge, never resurrect" stance <c>DefaultPromptsReseed</c>
/// takes for prompts, without its content hashing: a vocabulary word has one field, so the word
/// IS its own key, and a rule's key is its target spelling (<see cref="RuleKey"/>) so that editing
/// a rule's variant list in a later release never re-offers it.</para>
///
/// <para><b>Existing installs get the seed once on upgrade</b>, deliberately: no default existed
/// before this, so there is no earlier deletion an absent row could stand for (the reason the
/// prompt re-seed treats its first run as a baseline does not apply here). Dedupe is against the
/// rows actually present, case-insensitively — <c>CustomVocabularyService.AddAsync</c> compares
/// exact strings, and an install carrying "Openrouter" must not be doubled by "OpenRouter". A
/// shipped rule arrives WITHOUT the variants a rule of the user's own already matches: the user
/// has an opinion about those spellings (the owner's "voice wing → VoiceWink", someone's
/// "deepgram → DeepGram"), and a shipped rule added later in row order would silently win over
/// it on every transcript (self-review lens A) — while the corrections they lack still arrive
/// (the owner's rule names "voice wing", not "voice wink"). Every variant covered ⇒ nothing
/// added, and the rule is recorded as offered either way.</para>
///
/// <para><b>The record and the rows live in different stores, so the order of writes is the
/// contract.</b> (1) A launch whose settings cannot persist (protect mode, CR-1) seeds NOTHING —
/// rows without their record would be re-offered next launch and resurrect whatever the user
/// deleted in between. (2) The record goes FIRST, durably (<see cref="SettingsService.SetManyAndFlushOrThrow"/>,
/// not the debounced save), and the rows follow in one <c>SaveChanges</c>: a row is only ever
/// added after its key is on record, so a deleted default's key is always there and a missing
/// key proves the row was never added — resurrection is impossible by construction, not merely
/// unlikely (Codex diff r1: with rows-first, a failed record write followed by a delete and a
/// recovered settings file re-added the deleted word). A failed record write adds nothing and
/// retries next launch; a failed row write leaves the record stamped — this install would lack
/// the defaults, the harmless direction — and a best-effort rollback of the stamp makes the next
/// launch retry. (3) A database that was CREATED at this launch (fresh install, a deleted file,
/// or the schema-repair recreate in <c>App.InitializeDatabaseCore</c>) is offered everything
/// again — the caller passes that fact and the record is cleared first, or an intact record
/// beside an empty database would leave the Dictionary empty while the page promises the
/// defaults. Fail-soft throughout: a throw is logged and never aborts launch.</para>
///
/// <para>The record is app-managed state, NOT in <see cref="AppDefaults.Defaults"/>: it survives
/// Reset All Settings (which resets preferences, not Dictionary data — resetting it would
/// resurrect deleted defaults) and stays out of settings export/import (which default the target
/// install seeds for itself).</para>
/// </summary>
public static class DefaultDictionarySeed
{
    private static ILogger Logger => Log.ForContext(typeof(DefaultDictionarySeed));

    /// <summary>Record key for a replacement rule — the TARGET spelling, namespaced so a rule can
    /// never collide with a vocabulary word in the one shared record.</summary>
    internal static string RuleKey(DefaultDictionary.ReplacementRule rule) => "replace:" + rule.ReplacementText;

    /// <param name="databaseCreatedThisLaunch">True when the database file was created (or
    /// recreated) by this launch's initialisation — the record is cleared so every default is
    /// offered to the fresh database.</param>
    public static void Run(
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        SettingsService settings,
        bool databaseCreatedThisLaunch = false)
    {
        try
        {
            RunCore(dbFactory, settings, databaseCreatedThisLaunch);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Default dictionary seed skipped (fail-soft)");
        }
    }

    /// <summary>Returns how many rows were added (words + rules) — for tests and the log line.</summary>
    internal static int RunCore(
        IDbContextFactory<VoiceWinkDbContext> dbFactory,
        SettingsService settings,
        bool databaseCreatedThisLaunch = false)
    {
        if (settings.IsPersistenceSuppressed)
        {
            Logger.Warning("Default dictionary seed skipped: settings cannot persist this launch, so the offered record could not follow the rows");
            return 0;
        }

        var offered = databaseCreatedThisLaunch
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ReadOffered(settings);

        var pendingWords = DefaultDictionary.VocabularyWords.Where(w => !offered.Contains(w)).ToList();
        var pendingRules = DefaultDictionary.Replacements.Where(r => !offered.Contains(RuleKey(r))).ToList();
        if (pendingWords.Count == 0 && pendingRules.Count == 0)
            return 0; // the common launch: everything was offered before — no database read at all

        // Read what is present, decide what to add — nothing is written yet.
        var added = 0;
        using var db = dbFactory.CreateDbContext();
        var presentWords = new HashSet<string>(
            db.VocabularyWords.Select(w => w.Word).ToList().Select(w => w.Trim()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var word in pendingWords)
        {
            if (presentWords.Add(word))
            {
                db.VocabularyWords.Add(new VocabularyWord { Word = word });
                added++;
            }
        }

        // Every variant any present rule already matches — the same comma split the replacement
        // service applies, so "covered" here means "the user has a rule for that spelling". A
        // DISABLED rule counts too, on purpose: the service skips disabled rows, but a user who
        // switched a rule off said "do not rewrite this", and a shipped rule for the same variant
        // would override exactly that decision (Codex diff r1 advisory — kept as a tombstone).
        var coveredVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in db.WordReplacements.Select(r => r.OriginalText).ToList())
            foreach (var variant in SplitVariants(original))
                coveredVariants.Add(variant);
        foreach (var rule in pendingRules)
        {
            // Only the variants no rule of the user's own matches yet: their rule stays in
            // charge of the spellings it names (its target may differ — "DeepGram"), and the
            // shipped corrections they lack still arrive. Nothing left ⇒ nothing added.
            var remaining = SplitVariants(rule.OriginalText).Where(v => coveredVariants.Add(v)).ToArray();
            if (remaining.Length == 0)
                continue;
            db.WordReplacements.Add(new WordReplacement
            {
                OriginalText = string.Join(", ", remaining),
                ReplacementText = rule.ReplacementText,
            });
            added++;
        }

        // RECORD FIRST, rows second — the order is what makes resurrection impossible rather
        // than unlikely (Codex diff r1 Blocker). A row is only ever added AFTER its key is durably
        // on record, so a missing key on some later launch proves the row was never added, and a
        // deleted default's key is always there. Written for every pending default whether or
        // not a row follows — "already present" is offered too, or the next launch would re-offer
        // it forever and resurrect it the day the user deletes their own copy. A failed record
        // write adds nothing and retries next launch.
        foreach (var word in pendingWords) offered.Add(word);
        foreach (var rule in pendingRules) offered.Add(RuleKey(rule));
        WriteOffered(settings, offered);

        if (added > 0)
        {
            try
            {
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                // The record says offered, the rows are not there: this install would simply
                // lack the defaults — the harmless direction. Best-effort rollback so the next
                // launch retries; if that write fails too, the stamp stands and the log says so.
                Logger.Warning(ex, "Default dictionary rows could not be written — rolling the offered record back so the next launch retries");
                foreach (var word in pendingWords) offered.Remove(word);
                foreach (var rule in pendingRules) offered.Remove(RuleKey(rule));
                try
                {
                    WriteOffered(settings, offered);
                }
                catch (Exception rollbackEx)
                {
                    Logger.Warning(rollbackEx, "Default dictionary offered record could not be rolled back — this install keeps the record and will not be offered these defaults again");
                }
                return 0;
            }
        }

        Logger.Information("Default dictionary seeded: {Added} rows added, {NewlyOffered} newly offered, {Offered} on record",
            added, pendingWords.Count + pendingRules.Count, offered.Count);
        return added;
    }

    private static string[] SplitVariants(string originalText)
        => originalText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The offered record as a case-insensitive set. A missing or malformed value reads
    /// as empty (fail-contained — the worst outcome is re-offering, never a throw), and the write
    /// below replaces it with a well-formed array.</summary>
    internal static HashSet<string> ReadOffered(SettingsService settings)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = settings.GetString(AppDefaults.DictionaryDefaultsOffered, "");
        if (string.IsNullOrWhiteSpace(raw))
            return set;
        try
        {
            var items = global::System.Text.Json.JsonSerializer.Deserialize<string[]>(raw);
            if (items != null)
                foreach (var item in items)
                    if (!string.IsNullOrWhiteSpace(item))
                        set.Add(item);
        }
        catch (global::System.Text.Json.JsonException)
        {
            Logger.Warning("Dictionary defaults record is malformed — treated as empty");
        }
        return set;
    }

    private static void WriteOffered(SettingsService settings, HashSet<string> offered)
    {
        var json = global::System.Text.Json.JsonSerializer.Serialize(
            offered.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        settings.SetManyAndFlushOrThrow(new Dictionary<string, string?>
        {
            [AppDefaults.DictionaryDefaultsOffered] = json,
        });
    }
}
