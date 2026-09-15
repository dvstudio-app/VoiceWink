using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models.Entities;
using VoiceWink.Services.Data;
using VoiceWink.Services.System;

namespace VoiceWink.Services.TextProcessing;

/// <summary>
/// Applies user-defined word replacement rules to transcribed text.
/// Pipeline step 4: TranscriptionTextFormatter → WordReplacementService.
/// Caches both the DB rows AND the compiled regex per non-CJK original so a transcription
/// pass costs no regex parsing — only matching.
/// Master-gated by the Dictionary-page "Enable Dictionary" toggle: while it is off,
/// <see cref="ApplyReplacements"/> passes text through unchanged (read per call —
/// stage-by-stage semantics, same idiom as the per-provider transport Funcs).
/// </summary>
public sealed class WordReplacementService
{
    private static ILogger Logger => Log.ForContext<WordReplacementService>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<VoiceWinkDbContext> _dbFactory;
    private readonly SettingsService _settings;
    private readonly object _cacheLock = new();
    private List<CompiledReplacement>? _cachedReplacements;
    // DCT-2 (Codex diff r1): the compile runs OUTSIDE the lock, so a cache miss that started
    // before an InvalidateCache could store its already-stale list AFTER it â€” and a rule edited
    // on the Dictionary page stayed invisible to dictation until the next mutation or restart.
    // A miss snapshots the generation it read under and stores only if no invalidation landed
    // in between; a superseded miss returns its (correct-for-that-call) list without caching.
    private long _cacheGeneration;
    // Test seam: runs after the rows are read and before the compiled list is stored â€” the
    // window the generation guard exists for. Null in production.
    internal Action? AfterRowsReadForTest { get; set; }

    public WordReplacementService(IDbContextFactory<VoiceWinkDbContext> dbFactory, SettingsService settings)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    /// <summary>
    /// Invalidate the cached replacements so the next call to ApplyReplacements re-reads from DB.
    /// Call this when word replacements are added, edited, or deleted.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedReplacements = null;
            _cacheGeneration++;
        }
    }

    public string ApplyReplacements(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (!_settings.GetBool(AppDefaults.DictionaryEnabled, true))
            return text;

        var replacements = GetReplacements();

        if (replacements.Count == 0)
            return text;

        var result = text;
        var anyChanged = false;

        foreach (var compiled in replacements)
        {
            if (compiled.IsCjk)
            {
                var next = result.Replace(compiled.Original, compiled.Replacement, StringComparison.OrdinalIgnoreCase);
                if (!ReferenceEquals(next, result)) { result = next; anyChanged = true; }
            }
            else
            {
                try
                {
                    var next = compiled.Pattern!.Replace(result, compiled.Replacement);
                    if (!ReferenceEquals(next, result)) { result = next; anyChanged = true; }
                }
                catch (Exception ex)
                {
                    // SEC-3: both values are user-authored dictionary text. Kept adjacent and
                    // LAST so the single `dictTerm=` end-of-line scrub covers the pair — correct
                    // rather than lossy, since neither may leave the device.
                    Logger.Warning(ex, "Failed to apply replacement: dictTerm={Original} → {Replacement}",
                        Helpers.LogValueSanitizer.SingleLine(compiled.Original),
                        Helpers.LogValueSanitizer.SingleLine(compiled.Replacement));
                }
            }
        }

        if (anyChanged)
        {
            Logger.Debug("Word replacements applied: {Count} compiled rules", replacements.Count);
        }

        return result;
    }

    private List<CompiledReplacement> GetReplacements()
    {
        long generation;
        lock (_cacheLock)
        {
            if (_cachedReplacements != null)
                return _cachedReplacements;
            generation = _cacheGeneration;
        }

        using var db = _dbFactory.CreateDbContext();
        var rows = db.WordReplacements
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .ToList();
        AfterRowsReadForTest?.Invoke();

        var compiled = new List<CompiledReplacement>(rows.Count * 2);
        foreach (var row in rows)
        {
            var originals = row.OriginalText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var original in originals)
            {
                if (string.IsNullOrWhiteSpace(original)) continue;
                if (IsCjk(original))
                {
                    compiled.Add(new CompiledReplacement(original, row.ReplacementText, IsCjk: true, Pattern: null));
                }
                else
                {
                    var pattern = new Regex($@"\b{Regex.Escape(original)}\b",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
                    compiled.Add(new CompiledReplacement(original, row.ReplacementText, IsCjk: false, Pattern: pattern));
                }
            }
        }

        lock (_cacheLock)
        {
            // An invalidation landed while this miss was reading/compiling: its list may predate
            // the mutation, so it is used for THIS call only and the next call reads afresh.
            if (_cacheGeneration == generation)
                _cachedReplacements = compiled;
        }

        return compiled;
    }

    /// <summary>
    /// Cache entry for a single (original, replacement) pair. CJK strings use a literal
    /// <c>string.Replace</c> path (no word boundaries make sense in CJK); everything else
    /// carries a pre-compiled <see cref="Regex"/> so transcription doesn't pay the parse cost.
    /// </summary>
    private sealed record CompiledReplacement(string Original, string Replacement, bool IsCjk, Regex? Pattern);

    private static bool IsCjk(string text)
    {
        return text.Any(c =>
            (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK Unified Ideographs
            (c >= 0x3400 && c <= 0x4DBF) ||   // CJK Extension A
            (c >= 0x3040 && c <= 0x309F) ||   // Hiragana
            (c >= 0x30A0 && c <= 0x30FF) ||   // Katakana
            (c >= 0xAC00 && c <= 0xD7AF));    // Hangul
    }
}
