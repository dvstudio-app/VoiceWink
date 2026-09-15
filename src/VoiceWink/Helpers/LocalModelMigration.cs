using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using VoiceWink.Models;
using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// Maps retired local model names onto their successors in the shipped catalogue. Two owner
/// decisions feed it, both 2026-08-03: TRN-6 made the catalogue q8-only, and the four English-only
/// (<c>.en</c>) builds were then dropped because each rendered identical accuracy stars, speed stars
/// and download size to its multilingual twin — a choice the Models page could not justify.
///
/// <para><b>Why automatic migration rather than keeping the old rows resolvable.</b> The safe design
/// — keep every legacy row in the catalogue, hide it from discovery — was planned and REJECTED by
/// the owner on the grounds that the entire user base is four people in direct contact, so the
/// breakage this insures against is a conversation rather than an incident. That trade is only
/// valid at this size; a public build would want the other design back. The simplification it buys
/// is large: no lifecycle flag on the catalogue record, no discovery projection, no
/// selected-or-installed escape set, and no chance of a legacy row stranding a user in a surface
/// nobody thought to check.</para>
///
/// <para><b>Most mappings are a DOWNGRADE IN FILE SIZE and an upgrade in nothing measurable</b> —
/// the same weights at 8-bit instead of 16-bit or 5-bit. Two groups differ, both deliberately:</para>
/// <list type="bullet">
/// <item>the four <c>large-v3</c> rows land on <c>large-v3-turbo-q8_0</c>, a DIFFERENT model —
/// upstream publishes no q8 build of v3 at all, and rates turbo within 0.01 accuracy of it while
/// our own 2026-08-02 benchmark measured v3 at a tenth of real time;</item>
/// <item>the eight <c>.en</c> rows land on their multilingual twin at the same size — the same
/// Whisper family, more languages, and (per OpenAI's own documentation) slightly weaker on English
/// at tiny/base. That trade is the point of the removal, not a side effect of it.</item>
/// </list>
///
/// <para><b>Scope: names only — but the map is also the allowlist for the on-disk sweep.</b> This
/// type rewrites persisted SELECTIONS (the global model and every App Mode override) and touches no
/// files itself. Deleting the retired bytes is a separate step, deliberately: <c>RunOnSettings</c>
/// is also called by <c>ImportExportService</c>, and restoring a settings backup must never destroy
/// downloaded models. So <c>App.OnLaunched</c> — and only <c>App.OnLaunched</c> — hands
/// <see cref="SupersededNames"/> to <c>ModelDownloadManager.DeleteRetiredModelFiles</c> after this
/// migration has run.</para>
///
/// <para>An earlier owner decision (2026-08-03, same day) left those files in place: four users and
/// one instruction to clear the folder, against multi-gigabyte automatic deletion needing its own
/// containment guards. The owner REVERSED it later the same day — the guards already existed
/// (kernel-verified containment in the manager), and the files were not merely unreferenced but
/// invisible, since <c>InstalledCatalogModels</c> drops on-disk names the catalogue does not know.
/// Unreachable bytes with no surface that can delete them is a worse resting state than a sweep.</para>
///
/// <para><b>Consequence worth knowing when editing this map:</b> adding an entry now deletes files.
/// A name here must be one no shipped build can use — which is what
/// <c>LocalModelMigrationTests.NoLegacyNameSurvivesInTheCatalog</c> pins.</para>
///
/// <para>Unknown names pass through UNCHANGED rather than falling back to a default. A name this map
/// does not recognise is either already a q8 row or something the user hand-placed, and silently
/// rewriting it would be the wrong-engine substitution the runtime seam exists to prevent.</para>
/// </summary>
internal static class LocalModelMigration
{
    /// <summary>Legacy name → q8 successor. Case-insensitive: a persisted value may carry any
    /// casing (<c>ModelDiskReconciliation.NameComparer</c> resolves that way, and an imported
    /// settings file can hold anything).</summary>
    private static readonly Dictionary<string, string> Successors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Full-precision → same weights at 8-bit.
            ["ggml-tiny"] = "ggml-tiny-q8_0",
            ["ggml-base"] = "ggml-base-q8_0",
            ["ggml-small"] = "ggml-small-q8_0",
            ["ggml-medium"] = "ggml-medium-q8_0",
            ["ggml-large-v3-turbo"] = "ggml-large-v3-turbo-q8_0",

            // English-only → its multilingual twin (2026-08-03). Two generations map here, and
            // BOTH are required: the pre-q8 `.en` names above USED to target `.en-q8_0`, which no
            // longer exists — leaving them pointed there would strand every pre-q8 English-only
            // user on a name TranscriptionServiceRegistry now THROWS on, which is the exact
            // failure this map exists to prevent.
            ["ggml-tiny.en"] = "ggml-tiny-q8_0",
            ["ggml-base.en"] = "ggml-base-q8_0",
            ["ggml-small.en"] = "ggml-small-q8_0",
            ["ggml-medium.en"] = "ggml-medium-q8_0",
            ["ggml-tiny.en-q8_0"] = "ggml-tiny-q8_0",
            ["ggml-base.en-q8_0"] = "ggml-base-q8_0",
            ["ggml-small.en-q8_0"] = "ggml-small-q8_0",
            ["ggml-medium.en-q8_0"] = "ggml-medium-q8_0",

            // q5 → the same weights at 8-bit (less lossy, byte-aligned dequantization).
            ["ggml-small-q5_1"] = "ggml-small-q8_0",
            ["ggml-medium-q5_0"] = "ggml-medium-q8_0",
            ["ggml-large-v3-turbo-q5_0"] = "ggml-large-v3-turbo-q8_0",

            // large-v3 has NO q8 build upstream — these land on turbo. See the type doc.
            ["ggml-large-v3"] = "ggml-large-v3-turbo-q8_0",
            ["ggml-large-v3-q5_0"] = "ggml-large-v3-turbo-q8_0",
        };

    /// <summary>The successor for a persisted model name, or the name itself when nothing maps.
    /// Null/blank passes through so "never chosen" stays distinguishable from "chose something".</summary>
    internal static string? Migrate(string? persistedName)
    {
        if (string.IsNullOrWhiteSpace(persistedName)) return persistedName;
        return Successors.TryGetValue(persistedName, out var successor) ? successor : persistedName;
    }

    /// <summary>True when this name is one the migration rewrites — i.e. a retired catalogue row.
    /// Exposed for logging and tests; callers should use <see cref="Migrate"/>.</summary>
    internal static bool IsSuperseded(string? persistedName) =>
        !string.IsNullOrWhiteSpace(persistedName) && Successors.ContainsKey(persistedName);

    /// <summary>Every legacy name this map knows, for test coverage against the shipped catalogue.</summary>
    internal static IReadOnlyCollection<string> SupersededNames => Successors.Keys;

    /// <summary>Every successor this map targets, for test coverage: each MUST exist in the shipped
    /// catalogue, or migration would strand a user on an unresolvable name — the precise failure the
    /// map exists to prevent.</summary>
    internal static IReadOnlyCollection<string> SuccessorNames => Successors.Values;

    /// <summary>
    /// SHA-256 of the artifact VoiceWink actually distributed under each retired name — the identity
    /// the on-disk sweep verifies before deleting anything.
    ///
    /// <para><b>Why identity and not just the filename.</b> Codex's diff review: deleting any file
    /// called <c>ggml-small.bin</c> treats a NAME as proof of ownership, and that name is also
    /// whisper.cpp's own — someone who downloaded their own weights from Hugging Face, or
    /// fine-tuned a model, would very reasonably have one sitting there. A same-name-different-bytes
    /// file is the user's, not ours.</para>
    ///
    /// <para><b>A size check was tried first and does not work.</b> The pre-q8 catalogue rows carried
    /// ROUNDED sizes (<c>75_000_000</c>, <c>3_100_000_000</c>) sized for a free-space precheck, not
    /// real byte counts — so eight of these eighteen have no true size on record, and a size gate
    /// would silently refuse to delete every one of them.</para>
    ///
    /// <para><b>Values are lifted verbatim from the catalogue rows as they shipped</b> (git
    /// <c>93f938a~1</c> for the pre-q8 generation, <c>86b3704~1</c> for the q8 one), extracted
    /// programmatically rather than transcribed, and all eighteen were re-verified against those
    /// two revisions after the fact — the one input a reviewer cannot check from the working tree,
    /// so it was checked rather than asserted. The six full-precision digests independently match
    /// whisper.cpp's published values. <c>ModelRetiredSweepTests</c> pins that every superseded
    /// name has an entry and that each is a well-formed digest.</para>
    ///
    /// <para><b>A wrong entry fails SAFE</b> — the file is preserved and the skip is logged — which
    /// is what makes the hand-maintained table acceptable: the damage from an error is a model that
    /// stops being cleaned up, visibly, not one that gets destroyed.</para>
    /// </summary>
    private static readonly Dictionary<string, string> ArtifactHashes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ggml-tiny"] = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
            ["ggml-base"] = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            ["ggml-small"] = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            ["ggml-medium"] = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
            ["ggml-large-v3-turbo"] = "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",

            ["ggml-tiny.en"] = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
            ["ggml-base.en"] = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
            ["ggml-small.en"] = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
            ["ggml-medium.en"] = "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356",
            ["ggml-tiny.en-q8_0"] = "5bc2b3860aa151a4c6e7bb095e1fcce7cf12c7b020ca08dcec0c6d018bb7dd94",
            ["ggml-base.en-q8_0"] = "a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e",
            ["ggml-small.en-q8_0"] = "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067",
            ["ggml-medium.en-q8_0"] = "43fa2cd084de5a04399a896a9a7a786064e221365c01700cea4666005218f11c",

            ["ggml-small-q5_1"] = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
            ["ggml-medium-q5_0"] = "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
            ["ggml-large-v3-turbo-q5_0"] = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",

            ["ggml-large-v3"] = "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
            ["ggml-large-v3-q5_0"] = "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1",
        };

    /// <summary>
    /// Every retired artifact as ONE value — the name AND the digest that proves a file is it.
    ///
    /// <para>Deliberately not two collections. The sweep needs both, and handing it a name list plus
    /// a separate lookup is how the two drift into disagreement: a name with no digest would either
    /// be deleted unverified or silently never swept, depending on which side was consulted first.
    /// One sequence makes that unrepresentable. <c>ModelRetiredSweepTests</c> pins that it covers
    /// <see cref="SupersededNames"/> exactly.</para>
    /// </summary>
    internal static IEnumerable<(string Name, string Sha256)> RetiredArtifacts =>
        ArtifactHashes.Select(kv => (kv.Key, kv.Value));

    private static ILogger Logger => Log.ForContext(typeof(LocalModelMigration));

    /// <summary>
    /// Rewrite every persisted model selection in place. Called eagerly from <c>App.OnLaunched</c>
    /// before any window exists, alongside <c>AppModeSettingsMigration</c> and for the same reason:
    /// no service has cached settings yet, and no user-triggered export can snapshot a dead name.
    ///
    /// <para><b>Idempotent</b> — a migrated name is not in the map, so a second run is a dictionary
    /// miss and writes nothing. That matters because this runs on every launch, forever.</para>
    ///
    /// <para><b>Fail-soft on the App Mode half.</b> The configs are a JSON blob; if it will not
    /// parse, the global-selection migration has already been applied and is not rolled back. A
    /// corrupt blob is a pre-existing condition this migration must not escalate into a failed
    /// launch — <c>AppModeManager.GetConfigs</c> already tolerates it the same way.</para>
    /// </summary>
    internal static void RunOnSettings(SettingsService settings)
    {
        MigrateGlobalSelection(settings);
        MigrateAppModeOverrides(settings);
    }

    private static void MigrateGlobalSelection(SettingsService settings)
    {
        var current = settings.GetString(AppDefaults.SelectedModelName, "");
        if (!IsSuperseded(current)) return;

        var successor = Migrate(current)!;
        settings.SetString(AppDefaults.SelectedModelName, successor);
        Logger.Information(
            "Migrated selected model {Old} -> {New} (the catalogue no longer ships that row)",
            current, successor);
    }

    /// <summary>
    /// Rewrite ONLY the <c>ModelOverride</c> of each App Mode config, by JSON surgery.
    ///
    /// <para><b>Deliberately not deserialize-mutate-reserialize</b>, which is what this did first
    /// and what diff review rejected. A round trip through <c>List&lt;AppModeConfig&gt;</c> is
    /// lossy in two ways that both destroy user data: properties this build does not know are
    /// dropped, and properties the JSON omits come back with FRESH generated defaults — a new
    /// <c>Id</c> (breaking every reference to that config) and a new <c>DateCreated</c>. A model
    /// rename has no business rewriting either. The same reasoning is why
    /// <c>DefaultPromptsReseed</c> does JSON surgery; this now follows it.</para>
    ///
    /// <para><b>Per-entry, not all-or-nothing.</b> A null or non-object array element used to throw
    /// on property access and abandon the whole migration, so one malformed entry would strand
    /// every valid one. Each element is now skipped independently.</para>
    /// </summary>
    private static void MigrateAppModeOverrides(SettingsService settings)
    {
        var json = settings.GetString(AppDefaults.AppModeConfigs, "");
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            if (JsonNode.Parse(json) is not JsonArray configs || configs.Count == 0) return;

            var migrated = 0;
            foreach (var node in configs)
            {
                // Null elements and non-objects are legal JSON and simply have no override to
                // migrate. Skipping beats throwing: the valid entries still get fixed.
                if (node is not JsonObject config) continue;
                if (!config.TryGetPropertyValue(ModelOverrideProperty, out var overrideNode)) continue;

                // A non-string value (number, object, null) is not a model name; leave it exactly
                // as found rather than guessing at intent.
                if (overrideNode?.GetValueKind() != JsonValueKind.String) continue;

                var current = overrideNode.GetValue<string>();
                if (!IsSuperseded(current)) continue;

                config[ModelOverrideProperty] = Migrate(current);
                migrated++;
            }

            if (migrated == 0) return;

            settings.SetString(AppDefaults.AppModeConfigs, configs.ToJsonString());
            Logger.Information("Migrated {Count} App Mode model override(s) to the current catalogue", migrated);
        }
        catch (Exception ex)
        {
            // Count/type only — an App Mode config carries user-authored app names and patterns.
            // Fail-soft: the global selection above is already migrated and is not rolled back. A
            // blob that will not parse is a pre-existing condition (AppModeManager.GetConfigs
            // tolerates it the same way) and must not escalate into a failed launch.
            Logger.Warning("App Mode model-override migration skipped: {ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>The JSON property name behind <see cref="AppModeConfig.ModelOverride"/>. Spelled
    /// once: the surgery above matches on the wire name, and there is no serializer contract
    /// keeping a literal in sync with the property.</summary>
    private const string ModelOverrideProperty = nameof(AppModeConfig.ModelOverride);
}
