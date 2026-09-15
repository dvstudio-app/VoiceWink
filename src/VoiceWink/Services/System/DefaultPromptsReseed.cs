using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Models;
using JsonNode = global::System.Text.Json.Nodes.JsonNode;
using JsonObject = global::System.Text.Json.Nodes.JsonObject;
using JsonArray = global::System.Text.Json.Nodes.JsonArray;
using JsonValue = global::System.Text.Json.Nodes.JsonValue;

namespace VoiceWink.Services.System;

/// <summary>
/// UPD-4 — re-seed the shipped DEFAULT enhancement prompts + default App-Mode configs into
/// settings when the SOURCE defaults change (owner: "overwrite all defaults, but only if they
/// actually changed"). Runs once at startup, after the App-Mode migration and
/// before any service reads prompts/configs. Pure over <see cref="SettingsService"/>; the live
/// caches don't exist yet at this point.
///
/// SAFETY (Codex plan r1/r2): reads RAW settings and mutates via JsonNode SURGERY so unknown /
/// user records are structurally preserved during the reseed; a malformed present blob is left
/// untouched; the whole run is FAIL-SOFT (a throw can never abort OnLaunched); and the three keys
/// (prompts, App-Mode, manifest) are written in ONE atomic batch so a partial state is never
/// observable. Matching is by immutable <see cref="CustomPrompt.SeedKey"/>; pre-UPD-4 records are
/// adopted once by UNIQUE Title/Name (first occurrence). See <see cref="DefaultsManifest"/> for
/// the per-domain hash manifest and the reconcile decision.
///
/// Downgrade note: the content hash has no direction — an older build re-applies its (older)
/// defaults and re-stamps; switching builds re-clobbers default edits each switch. Accepted under
/// "overwrite when changed".
///
/// THE LINK IS THE USER'S WIRING (launch defaults audit, 2026-09-13): a default App-Mode config's
/// <c>LinkedEnhancementId</c> is deliberately not hashed content (<see cref="DefaultsManifest.AppModeContent"/>),
/// and on the steady-state <c>None</c> arm a stored link that still RESOLVES to a prompt — the
/// default, another prompt, or none — is kept whatever it points at. Only a DANGLING link (an Id
/// no prompt carries any more: the target was deleted, or re-imported under a new Id) is repaired,
/// to the template's current default target. Until this audit the arm re-pointed every default's
/// link to the template's target at EVERY start, so a user's own choice was replaced silently. The
/// <c>Overwrite</c> arm still applies the default link along with the content, once, at the release
/// that changed the template — the owner's "overwrite when changed" rule, kept distinct on purpose.
/// </summary>
public static class DefaultPromptsReseed
{
    private static ILogger Logger => Log.ForContext(typeof(DefaultPromptsReseed));

    public static void Run(SettingsService settings)
    {
        try
        {
            RunCore(settings);
        }
        catch (global::System.Exception ex)
        {
            // Fail-soft: diagnostics must never abort startup, and nothing is mutated before the
            // single atomic write, so a throw here leaves settings exactly as they were. No
            // contents logged.
            Logger.Warning(ex, "Default re-seed skipped (fail-soft)");
        }
    }

    private static void RunCore(SettingsService settings)
    {
        var promptsPresent = settings.Contains(AppDefaults.CustomPrompts);
        var appModesPresent = settings.Contains(AppDefaults.AppModeConfigs);

        // Guard against a developer introducing a duplicate/empty template key (round r2).
        if (!KeysAreUniqueAndNonEmpty())
        {
            Logger.Error("Default templates have duplicate or empty keys — re-seed skipped");
            return;
        }

        // Virgin install — neither key exists. Materialize both sets + the full manifest atomically
        // (removes the old ephemeral-defaults gap where prompts weren't persisted until first use).
        if (!promptsPresent && !appModesPresent)
        {
            MaterializeVirgin(settings);
            return;
        }

        // Parse raw blobs strictly. A present-but-not-an-array blob is malformed → leave EVERYTHING
        // untouched (never let a save destroy an unreadable-but-recoverable user blob).
        if (!TryParseArray(settings, AppDefaults.CustomPrompts, promptsPresent, out var promptArr))
        {
            Logger.Warning("Custom prompts JSON is malformed — default re-seed skipped, blob left intact");
            return;
        }
        if (!TryParseArray(settings, AppDefaults.AppModeConfigs, appModesPresent, out var appModeArr))
        {
            Logger.Warning("App-Mode configs JSON is malformed — default re-seed skipped, blob left intact");
            return;
        }

        var manifest = DefaultsManifest.Parse(settings.GetString(AppDefaults.DefaultsManifest, ""));

        // ── Prompts domain ──────────────────────────────────────────────────
        // Seed the new manifest maps FROM the stored maps so RETIRED-key tombstones survive: a
        // template removed from source keeps its manifest entry, so deleting its remaining record
        // and later reintroducing the key does NOT resurrect a deletion (Codex diff r1).
        var newPromptMap = new Dictionary<string, string>(manifest.Prompts, global::System.StringComparer.Ordinal);
        bool promptsChanged;
        if (!promptsPresent)
        {
            // One key absent (non-virgin): materialize prompts fresh + record shipped hashes.
            promptArr = MaterializePromptArray();
            foreach (var t in PromptTemplates.All)
                newPromptMap[t.Key] = DefaultsManifest.Hash(DefaultsManifest.ContentOf(t));
            promptsChanged = true;
        }
        else
        {
            promptsChanged = ReconcilePrompts(promptArr!, manifest.Prompts, manifest.PromptsPresent, newPromptMap);
        }

        // SeedKey → prompt Id index (unique only) for App-Mode link resolution, and the set of EVERY
        // prompt Id for the dangling-link test (a user's own prompt has no SeedKey; a duplicated
        // default has no index entry — both are valid link targets).
        var promptIdByKey = BuildPromptIdIndex(promptArr!);
        var promptIds = BuildPromptIdSet(promptArr!);

        // ── App-Mode domain ─────────────────────────────────────────────────
        var newAppModeMap = new Dictionary<string, string>(manifest.AppModes, global::System.StringComparer.Ordinal);
        bool appModesChanged;
        if (!appModesPresent)
        {
            appModeArr = MaterializeAppModeArray(promptIdByKey);
            foreach (var t in AppModeTemplates.Defaults)
                newAppModeMap[t.Key] = DefaultsManifest.Hash(DefaultsManifest.ContentOf(t));
            appModesChanged = true;
        }
        else
        {
            appModesChanged = ReconcileAppModes(appModeArr!, manifest.AppModes, manifest.AppModesPresent, promptIdByKey, promptIds, newAppModeMap);
        }

        var newManifestJson = DefaultsManifest.Serialize(newPromptMap, newAppModeMap);
        var manifestChanged = newManifestJson != settings.GetString(AppDefaults.DefaultsManifest, "");

        if (!promptsChanged && !appModesChanged && !manifestChanged)
            return; // steady state — the common path

        // Atomic batch: all three keys advance together (or none, on IO failure).
        var batch = new Dictionary<string, string?>(global::System.StringComparer.Ordinal)
        {
            [AppDefaults.DefaultsManifest] = newManifestJson,
        };
        if (promptsChanged) batch[AppDefaults.CustomPrompts] = promptArr!.ToJsonString();
        if (appModesChanged) batch[AppDefaults.AppModeConfigs] = appModeArr!.ToJsonString();
        settings.SetManyAndFlushOrThrow(batch);

        Logger.Information("Default re-seed applied: prompts={PromptsChanged}, appModes={AppModesChanged}",
            promptsChanged, appModesChanged);
    }

    // ── Prompts reconcile ───────────────────────────────────────────────────

    private static bool ReconcilePrompts(
        JsonArray arr, Dictionary<string, string> storedHashes, bool domainPresent,
        Dictionary<string, string> newMap)
    {
        // Adoption mutates the array (assigns SeedKeys); count it as a change so the adopted
        // provenance is PERSISTED even when every hash is unchanged (Codex diff r1).
        //
        // Legacy TITLE aliases: a genuine pre-UPD-4 record (no SeedKey) is matched to its key by
        // its title. When a default is RENAMED, the current-title map alone can no longer adopt a
        // record still carrying the OLD shipped title — a pre-UPD-4 install upgrading DIRECTLY to
        // the renamed build (auto-update skips intermediate versions) would leave it orphaned and
        // unrenamed, and null out any App-Mode link derived from its key. Map each renamed
        // default's old title to its immutable key here. Keys never change (see PromptTemplates).
        var titleToKey = PromptTemplates.All.Select(t => (t.Key, t.Title))
            .Append(("improve-accuracy", "Improve Accuracy")); // renamed -> "Improve Transcription" (2026-07-22)
        var changed = AdoptLegacyBySingleField(arr, "Title", titleToKey);

        var present = IndexObjectsBySeedKey(arr);
        var firstRun = !domainPresent;

        foreach (var t in PromptTemplates.All)
        {
            var content = DefaultsManifest.ContentOf(t);
            var currentHash = DefaultsManifest.Hash(content);
            storedHashes.TryGetValue(t.Key, out var stored);
            var action = DefaultsManifest.Decide(present.ContainsKey(t.Key), stored, currentHash, firstRun);

            switch (action)
            {
                case DefaultsManifest.ReseedAction.Overwrite:
                    ApplyPromptContent(present[t.Key], content);
                    changed = true;
                    break;
                case DefaultsManifest.ReseedAction.Add:
                    arr.Add(NodeOf(t.ToCustomPrompt(isActive: false)));
                    changed = true;
                    break;
                case DefaultsManifest.ReseedAction.AcknowledgeDeleted:
                case DefaultsManifest.ReseedAction.None:
                    break;
            }
            newMap[t.Key] = currentHash; // always record the current shipped hash for known keys
        }
        return changed;
    }

    private static void ApplyPromptContent(JsonObject obj, DefaultsManifest.PromptContent c)
    {
        obj["Title"] = c.Title;
        obj["PromptText"] = c.PromptText;
        obj["Icon"] = c.Icon;
        obj["Description"] = c.Description;
        obj["TriggerWords"] = NewStringArray(c.TriggerWords);
        obj["IsImageGeneration"] = c.IsImageGeneration;
        obj["AskImageSize"] = c.AskImageSize;
        // A pre-2026-08-01 record may still carry "UseSystemInstructions". It is deliberately NOT
        // stripped here: the flag was never read, System.Text.Json ignores it on load, and the
        // first typed AIEnhancementService.SavePrompts rewrites the array from CustomPrompt and
        // drops it from EVERY record. Stripping it here would reach only overwritten DEFAULTS,
        // leaving user-created prompts inconsistent — a half-migration for no behavioural gain.
        // Id, IsActive, SeedKey, IsPredefined, and any unknown properties are deliberately
        // untouched — IsPredefined is provenance (set at adoption/Add), NOT hashed content, so
        // keeping it out of the overwrite preserves the hash==apply-fields equivalence (Codex r1).
    }

    // ── App-Mode reconcile ──────────────────────────────────────────────────

    private static bool ReconcileAppModes(
        JsonArray arr, Dictionary<string, string> storedHashes, bool domainPresent,
        IReadOnlyDictionary<string, string> promptIdByKey, IReadOnlySet<string> promptIds, Dictionary<string, string> newMap)
    {
        var changed = AdoptLegacyBySingleField(arr, "Name", AppModeTemplates.Defaults.Select(t => (t.Key, t.Name)));

        var present = IndexObjectsBySeedKey(arr);
        var firstRun = !domainPresent;

        foreach (var t in AppModeTemplates.Defaults)
        {
            var content = DefaultsManifest.ContentOf(t);
            var currentHash = DefaultsManifest.Hash(content);
            storedHashes.TryGetValue(t.Key, out var stored);
            var action = DefaultsManifest.Decide(present.ContainsKey(t.Key), stored, currentHash, firstRun);
            var linkId = ResolveLink(t.DefaultEnhancementKey, promptIdByKey);

            switch (action)
            {
                case DefaultsManifest.ReseedAction.Overwrite:
                    ApplyAppModeContent(present[t.Key], content, linkId);
                    changed = true;
                    break;
                case DefaultsManifest.ReseedAction.Add:
                    arr.Add(NodeOf(t.ToAppModeConfig(linkId)));
                    changed = true;
                    break;
                case DefaultsManifest.ReseedAction.AcknowledgeDeleted:
                    break;
                case DefaultsManifest.ReseedAction.None:
                    // Hash unchanged. The link is the USER's wiring, not shipped content (it is
                    // deliberately not hashed — DefaultsManifest.AppModeContent): a stored link
                    // that still resolves to a prompt — the default, another prompt, or none — is
                    // a choice and is kept. Only a DANGLING link (an Id no prompt carries any more:
                    // the target was deleted, or re-imported under a new Id — the drift the UPD-4
                    // Codex diff r1 High named) is repaired, to the template's current default
                    // target (null when that is gone too). Until the 2026-09-13 audit this arm
                    // re-pointed the link to the default whenever it DIFFERED, at every start.
                    if (present.TryGetValue(t.Key, out var cfg) && RepairDanglingLink(cfg, linkId, promptIds))
                        changed = true;
                    break;
            }
            newMap[t.Key] = currentHash;
        }
        return changed;
    }

    private static void ApplyAppModeContent(JsonObject obj, DefaultsManifest.AppModeContent c, string? linkId)
    {
        obj["Name"] = c.Name;
        obj["ProcessPatterns"] = NewStringArray(c.ProcessPatterns);
        obj["LinkedEnhancementId"] = linkId; // derived-applied: null when the target default is gone
        // Id, IsEnabled, SeedKey, DateCreated, and any unknown properties are untouched.
    }

    // Repair ONLY a DANGLING link on a present config. Returns true iff it was dangling and got
    // repaired. Null / absent = "no enhancement", a choice, never repaired (and never written, so
    // the steady state stays write-free); a link that resolves is kept whatever it points at.
    private static bool RepairDanglingLink(JsonObject cfg, string? defaultLinkId, IReadOnlySet<string> promptIds)
    {
        var current = GetString(cfg, "LinkedEnhancementId");
        if (string.IsNullOrEmpty(current) || promptIds.Contains(current))
            return false;
        cfg["LinkedEnhancementId"] = defaultLinkId;
        return true;
    }

    private static string? ResolveLink(string? enhancementKey, IReadOnlyDictionary<string, string> promptIdByKey)
        => enhancementKey != null && promptIdByKey.TryGetValue(enhancementKey, out var id) ? id : null;

    // ── Materialization (virgin / one-key-absent) ───────────────────────────

    private static void MaterializeVirgin(SettingsService settings)
    {
        var promptArr = MaterializePromptArray();
        var promptIdByKey = BuildPromptIdIndex(promptArr);
        var appModeArr = MaterializeAppModeArray(promptIdByKey);

        var promptMap = new Dictionary<string, string>(global::System.StringComparer.Ordinal);
        foreach (var t in PromptTemplates.All)
            promptMap[t.Key] = DefaultsManifest.Hash(DefaultsManifest.ContentOf(t));
        var appModeMap = new Dictionary<string, string>(global::System.StringComparer.Ordinal);
        foreach (var t in AppModeTemplates.Defaults)
            appModeMap[t.Key] = DefaultsManifest.Hash(DefaultsManifest.ContentOf(t));

        settings.SetManyAndFlushOrThrow(new Dictionary<string, string?>(global::System.StringComparer.Ordinal)
        {
            [AppDefaults.CustomPrompts] = promptArr.ToJsonString(),
            [AppDefaults.AppModeConfigs] = appModeArr.ToJsonString(),
            [AppDefaults.DefaultsManifest] = DefaultsManifest.Serialize(promptMap, appModeMap),
        });
        Logger.Information("Default re-seed: materialized fresh defaults (virgin install)");
    }

    private static JsonArray MaterializePromptArray()
    {
        var arr = new JsonArray();
        var all = PromptTemplates.All;
        for (var i = 0; i < all.Length; i++)
            arr.Add(NodeOf(all[i].ToCustomPrompt(isActive: i == 0))); // Improve Transcription active
        return arr;
    }

    private static JsonArray MaterializeAppModeArray(IReadOnlyDictionary<string, string> promptIdByKey)
    {
        var arr = new JsonArray();
        foreach (var t in AppModeTemplates.Defaults)
            arr.Add(NodeOf(t.ToAppModeConfig(ResolveLink(t.DefaultEnhancementKey, promptIdByKey))));
        return arr;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Assign a SeedKey to a pre-UPD-4 record (no SeedKey) by matching a single field (Title/Name)
    // to a template — FIRST occurrence only, and only when the template key is not already claimed
    // by another record. Ambiguous duplicates stay unmarked (treated as user records). This is the
    // documented, owner-accepted legacy path; its one loss corner (a user-created record titled
    // exactly like a shipped default) is unavoidable without historical content signatures.
    private static bool AdoptLegacyBySingleField(JsonArray arr, string field, IEnumerable<(string Key, string Value)> templates)
    {
        var keyByValue = new Dictionary<string, string>(global::System.StringComparer.Ordinal);
        foreach (var (key, value) in templates)
            keyByValue.TryAdd(value, key); // if two templates share a value (shouldn't), first wins

        var claimed = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var node in arr)
        {
            if (node is JsonObject o && GetString(o, "SeedKey") is { Length: > 0 } sk)
                claimed.Add(sk);
        }

        var adopted = false;
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            if (GetString(o, "SeedKey") is { Length: > 0 }) continue; // already has provenance
            var value = GetString(o, field);
            if (value != null && keyByValue.TryGetValue(value, out var key) && claimed.Add(key))
            {
                o["SeedKey"] = key;
                o["IsPredefined"] = true; // provenance — set once at adoption, never in the overwrite
                adopted = true;
            }
        }
        return adopted;
    }

    // Map SeedKey -> the record's JsonObject, EXCLUDING keys that appear on more than one record
    // (ambiguous → dropped, so a default is never matched to an arbitrary duplicate — Codex r2).
    private static Dictionary<string, JsonObject> IndexObjectsBySeedKey(JsonArray arr)
    {
        var index = new Dictionary<string, JsonObject>(global::System.StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var sk = GetString(o, "SeedKey");
            if (string.IsNullOrEmpty(sk)) continue;
            if (!index.TryAdd(sk, o)) ambiguous.Add(sk);
        }
        foreach (var a in ambiguous) index.Remove(a);
        return index;
    }

    private static Dictionary<string, string> BuildPromptIdIndex(JsonArray promptArr)
    {
        var byKey = IndexObjectsBySeedKey(promptArr);
        var index = new Dictionary<string, string>(global::System.StringComparer.Ordinal);
        foreach (var (key, obj) in byKey)
        {
            var id = GetString(obj, "Id");
            if (!string.IsNullOrEmpty(id)) index[key] = id;
        }
        return index;
    }

    // Every non-empty string Id in the prompt array, read through the same tolerant GetString the
    // index uses, so one malformed record is skipped rather than turning the whole fail-soft reseed
    // into a no-op (Codex plan round).
    private static HashSet<string> BuildPromptIdSet(JsonArray promptArr)
    {
        var ids = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var node in promptArr)
        {
            if (node is not JsonObject obj) continue;
            var id = GetString(obj, "Id");
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    private static bool KeysAreUniqueAndNonEmpty()
    {
        var pk = PromptTemplates.All.Select(t => t.Key).ToList();
        var ak = AppModeTemplates.Defaults.Select(t => t.Key).ToList();
        return pk.All(k => !string.IsNullOrEmpty(k)) && pk.Distinct(global::System.StringComparer.Ordinal).Count() == pk.Count
            && ak.All(k => !string.IsNullOrEmpty(k)) && ak.Distinct(global::System.StringComparer.Ordinal).Count() == ak.Count;
    }

    private static bool TryParseArray(SettingsService settings, string key, bool present, out JsonArray? arr)
    {
        arr = null;
        if (!present) return true; // absent is fine (materialize path handles it)
        // The blob is stored as a STRING containing a JSON array. A non-string outer value is a
        // corrupted-but-maybe-recoverable state — treat as malformed and leave it untouched, never
        // coerce it to an empty array via GetString (Codex diff r1).
        if (settings.GetValueKind(key) != global::System.Text.Json.JsonValueKind.String)
            return false;
        var json = settings.GetString(key, "");
        if (string.IsNullOrWhiteSpace(json)) { arr = new JsonArray(); return true; }
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray a) { arr = a; return true; }
            return false; // present but not an array → malformed
        }
        catch
        {
            return false; // unparseable → malformed
        }
    }

    private static JsonNode NodeOf<T>(T value) =>
        JsonNode.Parse(global::System.Text.Json.JsonSerializer.Serialize(value))!;

    private static JsonArray NewStringArray(IReadOnlyList<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s); // fresh array node per replacement (Codex r2)
        return a;
    }

    private static string? GetString(JsonObject o, string prop) =>
        o.TryGetPropertyValue(prop, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
}
