using System.Security.Cryptography;
using System.Text;
using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// UPD-4 — pure content-hashing + reconcile-decision logic for re-seeding the shipped default
/// prompts / App-Mode configs when the SOURCE defaults change. No IO, no settings access.
///
/// The CANONICAL CONTENT records below are the SINGLE source of truth for BOTH the hash and the
/// fields the runner overwrites — identical by construction, so the "hash and applied fields
/// disagree" class of bug can't happen (Codex plan r1). Hashing is a pure function of the SHIPPED
/// template content (never the persisted record), so the manifest records "the hash of the default
/// we last applied for this key." A per-DOMAIN manifest (prompts vs App-Mode) keeps blast radius
/// contained: changing one prompt never touches App-Mode (Codex plan r1/r2).
/// </summary>
public static class DefaultsManifest
{
    // ── Canonical content (hashed AND overwritten — the same fields) ────────

    /// <summary>The prompt fields the re-seed manages. Title IS included (a shipped rename
    /// updates the matched record in place). Id/IsActive/SeedKey are user/runtime state and are
    /// deliberately absent — never hashed, never overwritten.</summary>
    public sealed record PromptContent(
        string Title, string PromptText, string Icon, string? Description,
        IReadOnlyList<string> TriggerWords, bool IsImageGeneration, bool AskImageSize);

    /// <summary>The App-Mode fields the re-seed manages. LinkedEnhancementId is NOT here — it is
    /// a DERIVED applied field (resolved from <see cref="DefaultEnhancementKey"/> to a runtime
    /// prompt Id), so it is not hashed; but the KEY is, so re-pointing a default's link in source
    /// re-fires the reconcile.</summary>
    public sealed record AppModeContent(
        string Name, IReadOnlyList<string> ProcessPatterns, string? DefaultEnhancementKey);

    public static PromptContent ContentOf(TemplatePrompt t) => new(
        t.Title, t.PromptText, t.Icon, t.Description,
        new List<string>(t.DefaultTriggerWords), t.IsImageGeneration, t.AskImageSize);

    public static AppModeContent ContentOf(AppModeTemplate t) => new(
        t.Name, (string[])t.ProcessPatterns.Clone(), t.DefaultEnhancementKey);

    // -- Hashing - deterministic, machine-independent, COLLISION-FREE --------

    // Length-prefixed encoding: every field is written as "<charLength>:<value>" and arrays as
    // "<count>:" then each element length-prefixed. Because the length is unambiguous, NO field
    // value (even one containing separators/colons/control chars) can be mistaken for a boundary,
    // so two different content tuples can never collide (Codex diff r1). Null encodes as "-1:"
    // (distinct from any real length, including 0 for "").
    private static void Field(StringBuilder sb, string? value)
    {
        if (value == null) { sb.Append("-1:"); return; }
        sb.Append(value.Length).Append(':').Append(value);
    }

    private static void Field(StringBuilder sb, bool value) => Field(sb, value ? "1" : "0");

    private static void Field(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.Append(items.Count).Append(':');
        foreach (var s in items) Field(sb, s);
    }

    public static string Hash(PromptContent c)
    {
        var sb = new StringBuilder();
        Field(sb, c.Title);
        Field(sb, c.PromptText);
        Field(sb, c.Icon);
        Field(sb, c.Description);
        Field(sb, c.TriggerWords);
        Field(sb, c.IsImageGeneration);
        Field(sb, c.AskImageSize);
        return Sha256Hex(sb.ToString());
    }

    public static string Hash(AppModeContent c)
    {
        var sb = new StringBuilder();
        Field(sb, c.Name);
        Field(sb, c.ProcessPatterns);
        Field(sb, c.DefaultEnhancementKey);
        return Sha256Hex(sb.ToString());
    }

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return global::System.Convert.ToHexString(bytes);
    }

    // ── Reconcile decision (pure) ───────────────────────────────────────────

    // Stand-in recorded for a malformed/null manifest hash so the KEY stays known (a deletion is
    // not resurrected). Lowercase + non-hex → can never equal a real SHA-256 hex hash, so Decide
    // treats it as "known but changed", never "new".
    internal const string MalformedHashSentinel = "malformed";

    public enum ReseedAction
    {
        /// <summary>Shipped hash equals the last-applied hash — nothing to do.</summary>
        None,
        /// <summary>A present default record must have its content overwritten.</summary>
        Overwrite,
        /// <summary>No present record and the key was never recorded — a genuinely new shipped
        /// template — so add it.</summary>
        Add,
        /// <summary>No present record, but record the current hash WITHOUT adding: either the user
        /// deleted a known default (don't resurrect) or this is the first-run baseline (a pre-UPD-4
        /// deletion we must preserve).</summary>
        AcknowledgeDeleted,
    }

    /// <summary>
    /// Decide what to do for ONE template key. <paramref name="present"/> = a persisted record
    /// with this SeedKey exists (after legacy adoption). <paramref name="storedHash"/> = the
    /// manifest's last-applied hash for this key (null = never recorded).
    /// <paramref name="isFirstRunBaseline"/> = this DOMAIN's manifest map was absent entirely
    /// (first UPD-4 run) — in which case an ABSENT record is treated as an intentional pre-UPD-4
    /// deletion (acknowledged, not resurrected), never as a new template.
    /// </summary>
    public static ReseedAction Decide(bool present, string? storedHash, string currentHash, bool isFirstRunBaseline)
    {
        if (storedHash == currentHash)
            return ReseedAction.None;               // unchanged (present or already-acknowledged)
        if (present)
            return ReseedAction.Overwrite;          // shipped content changed -> update in place
        if (storedHash != null)
            return ReseedAction.AcknowledgeDeleted; // known key, user deleted it -> don't resurrect
        return isFirstRunBaseline
            ? ReseedAction.AcknowledgeDeleted       // first run: preserve pre-UPD-4 deletion
            : ReseedAction.Add;                     // genuinely new shipped template
    }

    // ── Manifest (de)serialization — per-domain, fail-contained ─────────────

    /// <summary>Parsed manifest: two independent domain maps + whether each was PRESENT in the
    /// stored JSON (absent =&gt; first-run baseline for that domain). A malformed manifest parses
    /// as both-absent (fail-contained — treated as a fresh baseline, never throws).</summary>
    public sealed class Parsed
    {
        public Dictionary<string, string> Prompts { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AppModes { get; init; } = new(StringComparer.Ordinal);
        public bool PromptsPresent { get; init; }
        public bool AppModesPresent { get; init; }
    }

    public static Parsed Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Parsed();
        try
        {
            var node = global::System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is not global::System.Text.Json.Nodes.JsonObject obj)
                return new Parsed();
            return new Parsed
            {
                Prompts = ReadMap(obj, "prompts", out var pPresent),
                PromptsPresent = pPresent,
                AppModes = ReadMap(obj, "appModes", out var aPresent),
                AppModesPresent = aPresent,
            };
        }
        catch
        {
            return new Parsed(); // fail-contained: treat a malformed manifest as absent
        }
    }

    private static Dictionary<string, string> ReadMap(
        global::System.Text.Json.Nodes.JsonObject root, string name, out bool present)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        present = false;
        if (root[name] is global::System.Text.Json.Nodes.JsonObject sub)
        {
            present = true;
            foreach (var kv in sub)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                // Per-entry tolerant: a non-string/null hash value is never thrown (a single bad
                // entry must not collapse BOTH domains). But it must PRESERVE KEY MEMBERSHIP via a
                // sentinel that can never equal a real (uppercase-hex) hash — dropping the key
                // would turn a "known-but-malformed" default into a "genuinely new" one and
                // RESURRECT a deletion (Codex diff r2).
                string? v;
                try { v = kv.Value?.GetValue<string>(); }
                catch { v = null; }
                map[kv.Key] = v ?? MalformedHashSentinel;
            }
        }
        return map;
    }

    public static string Serialize(IReadOnlyDictionary<string, string> prompts, IReadOnlyDictionary<string, string> appModes)
    {
        // Sorted keys -> stable serialization (no spurious diffs / debug noise).
        var obj = new global::System.Text.Json.Nodes.JsonObject
        {
            ["prompts"] = ToSortedObject(prompts),
            ["appModes"] = ToSortedObject(appModes),
        };
        return obj.ToJsonString();
    }

    /// <summary>
    /// Return the manifest JSON with the named domain(s) INVALIDATED — the domain's map key is
    /// OMITTED entirely, so the next startup sees it as absent (first-run baseline) and reconciles
    /// the freshly-imported data. Returns null when nothing survives (caller REMOVES the manifest
    /// key). Used by settings-import: invalidating ONLY the imported domain avoids the cross-domain
    /// blast Codex flagged (importing prompts must not force an App-Mode overwrite).
    /// </summary>
    public static string? WithDomainsInvalidated(string? currentJson, bool invalidatePrompts, bool invalidateAppModes)
    {
        var p = Parse(currentJson);
        var keepPrompts = p.PromptsPresent && !invalidatePrompts;
        var keepAppModes = p.AppModesPresent && !invalidateAppModes;
        if (!keepPrompts && !keepAppModes)
            return null;
        var obj = new global::System.Text.Json.Nodes.JsonObject();
        if (keepPrompts) obj["prompts"] = ToSortedObject(p.Prompts);
        if (keepAppModes) obj["appModes"] = ToSortedObject(p.AppModes);
        return obj.ToJsonString();
    }

    private static global::System.Text.Json.Nodes.JsonObject ToSortedObject(IReadOnlyDictionary<string, string> map)
    {
        var o = new global::System.Text.Json.Nodes.JsonObject();
        foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            o[key] = map[key];
        return o;
    }
}
