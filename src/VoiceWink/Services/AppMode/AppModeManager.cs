using System.Text.Json;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.System;

namespace VoiceWink.Services.AppMode;

/// <summary>
/// CRUD + matching for App Mode configs.
/// Persists as JSON in settings. Caches deserialized configs in memory, invalidated on save.
/// </summary>
public sealed class AppModeManager
{
    private static ILogger Logger => Log.ForContext<AppModeManager>();

    private readonly SettingsService _settings;
    private readonly IActiveWindowService _activeWindow;
    private readonly AIEnhancementService _enhancement;

    /// <summary>
    /// In-memory cache. Null means stale — next GetConfigs() will deserialize from JSON.
    /// Invalidated in SaveConfigs(). Singleton accessed from UI thread, no lock needed.
    /// Note: returns a mutable reference — callers must not cache it across mutation boundaries.
    /// </summary>
    private List<AppModeConfig>? _cache;

    public AppModeManager(SettingsService settings, IActiveWindowService activeWindow, AIEnhancementService enhancement)
    {
        _settings = settings;
        _activeWindow = activeWindow;
        _enhancement = enhancement;
    }

    public List<AppModeConfig> GetConfigs()
    {
        if (_cache != null)
            return _cache;

        // Defensive re-run of the one-way legacy-key migration (App.OnLaunched runs it eagerly;
        // this covers managers constructed outside app startup, e.g. in tests). No-op once the
        // legacy key is gone.
        AppModeSettingsMigration.Run(_settings);

        var json = _settings.GetString(AppDefaults.AppModeConfigs, "");
        if (string.IsNullOrEmpty(json))
        {
            var defaults = CreateDefaults();
            SaveConfigs(defaults); // persist so IDs are stable across sessions
            _cache = defaults;    // re-set after SaveConfigs nulls it
            return _cache;
        }

        try
        {
            _cache = JsonSerializer.Deserialize<List<AppModeConfig>>(json) ?? new List<AppModeConfig>();
            return _cache;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to deserialize App Mode configs ({JsonLength} chars)", json.Length);
            _cache = new List<AppModeConfig>();
            return _cache;
        }
    }

    private List<AppModeConfig> CreateDefaults()
    {
        // GetPrompts() may return ephemeral defaults with fresh GUIDs.
        // Persist them so LinkedEnhancementId references remain stable.
        var prompts = _enhancement.GetPrompts();
        _enhancement.SavePrompts(prompts);

        var defaults = new List<AppModeConfig>();
        foreach (var template in AppModeTemplates.Defaults)
        {
            // Resolve the default link by the prompt's stable SeedKey, not its Title (UPD-4,
            // Codex r2 — a title matches duplicates/renames, a key doesn't). Unique match only;
            // a missing/ambiguous target yields a null link, never an arbitrary duplicate.
            string? enhancementId = null;
            if (!string.IsNullOrEmpty(template.DefaultEnhancementKey))
            {
                var matches = prompts.Where(p => p.SeedKey == template.DefaultEnhancementKey).ToList();
                if (matches.Count == 1)
                    enhancementId = matches[0].Id;
            }
            defaults.Add(template.ToAppModeConfig(enhancementId));
        }
        return defaults;
    }

    public void SaveConfigs(List<AppModeConfig> configs)
    {
        var json = JsonSerializer.Serialize(configs);
        try
        {
            _settings.SetString(AppDefaults.AppModeConfigs, json);
        }
        finally
        {
            _cache = null; // Invalidate cache whether write succeeded or threw
        }
    }

    public void AddConfig(AppModeConfig config)
    {
        var configs = new List<AppModeConfig>(GetConfigs());
        configs.Add(config);
        SaveConfigs(configs);
    }

    public void UpdateConfig(AppModeConfig config)
    {
        var configs = new List<AppModeConfig>(GetConfigs());
        var idx = configs.FindIndex(c => c.Id == config.Id);
        if (idx < 0)
        {
            Logger.Warning("UpdateConfig: id '{Id}' not found — no change written", config.Id);
            return;
        }
        configs[idx] = config;
        SaveConfigs(configs);
    }

    public void DeleteConfig(string id)
    {
        var configs = new List<AppModeConfig>(GetConfigs());
        var removed = configs.RemoveAll(c => c.Id == id);
        if (removed == 0)
        {
            Logger.Warning("DeleteConfig: id '{Id}' not found — no change written", id);
            return;
        }
        SaveConfigs(configs);
    }

    /// <summary>
    /// Get templates not yet added. A template is excluded if any of its process patterns
    /// overlap (case-insensitive) with any existing config's patterns.
    /// </summary>
    public AppModeTemplate[] GetAvailableTemplates()
    {
        var existingPatterns = GetConfigs()
            .SelectMany(c => c.ProcessPatterns)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.ToUpperInvariant())
            .ToHashSet();

        var existingSeedKeys = GetConfigs()
            .Where(c => !string.IsNullOrEmpty(c.SeedKey))
            .Select(c => c.SeedKey!)
            .ToHashSet();

        return AppModeTemplates.All
            .Where(t => !t.ProcessPatterns.Any(p => existingPatterns.Contains(p.ToUpperInvariant()))
                     && !existingSeedKeys.Contains(t.Key))  // UPD-4: don't re-offer a renamed/edited seeded default
            .ToArray();
    }

    /// <summary>
    /// Add a new config from a template. Returns false if patterns overlap with existing configs.
    /// </summary>
    // SEC-3 scope note (Kimi diff r1): the three {Name} properties below carry
    // template.Name — a fixed key from the built-in catalogue ("Outlook", "Teams", …), NOT the
    // user-authored config.Name that DetectActiveAppMode handles. It is not user data, so the
    // generic property is correct here and these sites are deliberately NOT renamed. Do not
    // "finish the sweep" by changing them: {AppModeName} is on the redaction allowlist, so
    // routing a catalogue key through it would blank a useful diagnostic for no privacy gain.
    // If templates ever become user-editable, that stops being true.
    public bool AddFromTemplate(AppModeTemplate template, string? linkedEnhancementId = null)
    {
        if (PatternsOverlap(template))
        {
            Logger.Information("Template '{Name}' overlaps with existing config, skipping", template.Name);
            return false;
        }

        // UPD-4 (Codex diff r2): also skip when this template's SeedKey is already present — an
        // edited seeded config (patterns changed) would otherwise no longer overlap, letting the
        // template be re-added and creating a duplicate SeedKey (dropped from the reseed index).
        if (SeedKeyPresent(template))
        {
            Logger.Information("Template '{Name}' seed key already present, skipping", template.Name);
            return false;
        }

        AddConfig(template.ToAppModeConfig(linkedEnhancementId));
        Logger.Information("App Mode config added from template: {Name} (enhancement={Enhancement})",
            template.Name, linkedEnhancementId ?? "none");
        return true;
    }

    /// <summary>
    /// Read-only preflight: whether <see cref="AddFromTemplate"/> would ACCEPT this template (no
    /// process-pattern overlap and no existing config with its SeedKey). Lets the App-Mode add flow
    /// avoid persisting an enhancement for an app-mode add that would then be rejected.
    /// </summary>
    public bool CanAddFromTemplate(AppModeTemplate template) =>
        !PatternsOverlap(template) && !SeedKeyPresent(template);

    /// <summary>
    /// Display names of App-Mode configs (enabled OR disabled) whose enhancement links to the given
    /// prompt Id. Returns a materialized snapshot of STRINGS — no mutable config is exposed. A blank
    /// name renders as "an unnamed app mode". Used to warn before deleting a linked enhancement.
    /// </summary>
    public IReadOnlyList<string> GetAppModeNamesLinkedTo(string promptId)
    {
        if (string.IsNullOrEmpty(promptId)) return [];
        return GetConfigs()
            .Where(c => c.LinkedEnhancementId == promptId)
            .Select(c => string.IsNullOrWhiteSpace(c.Name) ? "an unnamed app mode" : c.Name)
            .ToList();
    }

    private bool PatternsOverlap(AppModeTemplate template)
    {
        var existingPatterns = GetConfigs()
            .SelectMany(c => c.ProcessPatterns)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.ToUpperInvariant())
            .ToHashSet();
        return template.ProcessPatterns.Any(p => existingPatterns.Contains(p.ToUpperInvariant()));
    }

    private bool SeedKeyPresent(AppModeTemplate template) =>
        !string.IsNullOrEmpty(template.Key) && GetConfigs().Any(c => c.SeedKey == template.Key);

    /// <summary>
    /// Detect active App Mode based on current foreground window.
    /// Returns matching config or null if no match.
    /// <para><b>Avoid on the UI thread under contention</b> —
    /// <see cref="IActiveWindowService.GetActiveProcessName"/> internally
    /// calls <c>Process.GetProcessById</c>, which blocked the UI dispatcher
    /// for 2.25 s on 2026-05-21 (Excel + OBS + Teams active). UI-thread
    /// callers should use
    /// <see cref="DetectActiveAppModeAsync(int?, TimeSpan, CancellationToken)"/>
    /// instead.</para>
    /// </summary>
    public AppModeConfig? DetectActiveAppMode()
    {
        var processName = _activeWindow.GetActiveProcessName();
        return DetectActiveAppMode(processName);
    }

    /// <summary>
    /// Detect active App Mode using a previously-captured target-window
    /// PID, with the slow <c>Process.GetProcessById</c> lookup off the UI
    /// thread via <see cref="Helpers.BoundedComCall.RunBoundedAsync{T}"/>.
    ///
    /// <para>Caller MUST capture the PID synchronously from the target
    /// window at recording-start; this method does NOT re-read
    /// <see cref="Helpers.NativeInterop.GetForegroundWindow"/> on a worker
    /// (focus may have moved by then — pill stealing focus, app switch,
    /// etc., would match the wrong process).</para>
    ///
    /// <para>Config-cache matching (<see cref="DetectActiveAppMode(string?)"/>)
    /// runs on the awaiter's continuation (UI thread under default await
    /// semantics) so the unlocked config cache is not touched off-thread.
    /// Do NOT add <c>.ConfigureAwait(false)</c> to calls of this method.</para>
    /// </summary>
    /// <param name="targetPid">PID captured from the target window. Null →
    /// returns null without invoking the worker (no App Mode match).</param>
    /// <param name="timeout">Bounded-call timeout. 250 ms recommended for the
    /// recording-start hot path to keep the gap between pill-shown and
    /// recording-started sub-perceptual. Abandonment → null → no override.</param>
    /// <param name="ct">Caller cancellation token. OCE propagates to the
    /// awaiter (matches existing Starting-state cancellation flow).</param>
    public async Task<AppModeConfig?> DetectActiveAppModeAsync(
        int? targetPid,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (targetPid is null) return null;

        var processName = await Helpers.BoundedComCall.RunBoundedAsync(
            () => _activeWindow.GetProcessNameById(targetPid.Value),
            timeout,
            "appmode-GetProcessNameById",
            dependency: null,
            ct: ct);

        return processName != null ? DetectActiveAppMode(processName) : null;
    }

    /// <summary>
    /// Detect active App Mode for a given process name.
    /// Internal for testability — allows unit tests to bypass the P/Invoke path.
    ///
    /// <para><b>Privacy contract (SEC-3).</b> Both values on these lines are PII-class and both
    /// lines are at or above Sentry's <c>Information</c> breadcrumb threshold: the process name
    /// is the foreground application on the user's machine (the <c>ownerProc</c> /
    /// <c>foregroundProc</c> class), and the App Mode label is user-authored free text that can
    /// name a client or employer — the same test the codebase already applies to
    /// <c>{Prompt}</c>. They ride <c>{AppModeName}</c> and <c>{AppModeProcess}</c>, both on
    /// <c>LogRedactionEnricher</c>'s name allowlist, and are scrubbed from rendered lines by
    /// <c>AppModePattern</c>.</para>
    ///
    /// <para>The generic <c>{Name}</c> must NOT be used here and must NOT be added to the
    /// allowlist — dialog and page titles log under it, exactly the <c>{Prompt}</c>-vs-<c>{Title}</c>
    /// split already in that file. Keep the two sensitive tokens adjacent and LAST: the rendered
    /// pattern runs from <c>mode=</c> to end-of-line, so it covers both in one match and neither
    /// value can forge its own terminator.</para>
    /// </summary>
    internal AppModeConfig? DetectActiveAppMode(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return null;

        var configs = GetConfigs().Where(c => c.IsEnabled).ToList();
        foreach (var config in configs)
        {
            try
            {
                if (ActiveWindowService.MatchesPatterns(processName, config.ProcessPatterns))
                {
                    Logger.Information("App Mode matched: appMode={AppModeName} proc={AppModeProcess}",
                        Helpers.LogValueSanitizer.SingleLine(config.Name),
                        Helpers.LogValueSanitizer.SingleLine(processName));
                    return config;
                }
            }
            catch (global::System.Text.RegularExpressions.RegexMatchTimeoutException tex)
            {
                Logger.Warning(tex, "App Mode pattern match timed out — skipping to next: appMode={AppModeName}",
                    Helpers.LogValueSanitizer.SingleLine(config.Name));
            }
        }

        return null;
    }
}
