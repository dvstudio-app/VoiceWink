using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceWink.Models;

/// <summary>
/// The completion marker written into a bundle directory as the LAST step of an install (TRN-1
/// step 2). Its presence is necessary for "installed" — never sufficient: see
/// <c>ModelDownloadManager.TryGetInstalledLocation</c>, which also requires the manifest to match the
/// catalog's expected plan and every payload file to be present at its expected size.
///
/// <para><b>Manifest contents are UNTRUSTED as filesystem authority.</b> The catalog decides which
/// files a bundle has and where they live; this records what was installed so a mismatch is
/// detectable. A path read from here is never composed into a filesystem path — that would let a
/// hand-edited marker point the reader at arbitrary files.</para>
/// </summary>
internal sealed class ModelInstallManifest
{
    /// <summary>
    /// Bumped when the shape changes. An UNKNOWN version reads as NOT INSTALLED (fail-closed):
    /// a future version may record invariants this build cannot check, and treating it as installed
    /// would hand the runtime a bundle nobody verified.
    /// </summary>
    internal const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("modelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestEntry> Files { get; set; } = [];

    internal sealed class ManifestEntry
    {
        [JsonPropertyName("relativePath")]
        public string? RelativePath { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    internal static ModelInstallManifest For(string modelName, IReadOnlyList<ModelFile> files) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        ModelName = modelName,
        Files = files.Select(f => new ManifestEntry
        {
            RelativePath = f.RelativePath,
            SizeBytes = f.FileSizeBytes,
            Sha256 = f.Sha256Hash,
        }).ToList(),
    };

    internal string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Why a manifest read did not produce a usable manifest — and, crucially, whether the caller is
    /// allowed to DELETE what it found (TRN-1 step 3, PR A).
    ///
    /// <para><b>The distinction is a data-loss boundary, not bookkeeping.</b> Step 2 collapsed every
    /// failure into one null, and <c>DownloadBundleAsync</c> then treats a not-installed destination
    /// as debris and deletes it recursively. A transient sharing or ACL failure on the manifest of a
    /// COMPLETE 670 MB install therefore reads exactly like corruption — and the install is
    /// destroyed. Step 2 recorded this as mandatory before the first production bundle; the Parakeet
    /// row is that bundle.</para>
    /// </summary>
    internal enum ManifestReadStatus
    {
        /// <summary>Read, parsed, known schema, structurally sound.</summary>
        Valid,
        /// <summary>Nothing there. Safe to install over.</summary>
        Absent,
        /// <summary>Present and definitely wrong: malformed JSON, or a structurally impossible
        /// shape. Cleanable — we know what it is and it is not a valid install.</summary>
        Invalid,
        /// <summary>
        /// Present but we could not determine what it is: an I/O or access failure. **Never
        /// cleanable.** Absence of proof is not proof of corruption.
        /// </summary>
        Unreadable,
        /// <summary>
        /// A schema this build does not know — necessarily a NEWER one, since versions only go up.
        /// **Never cleanable**, and deliberately its own state rather than folded into
        /// <see cref="Invalid"/>: install on build N+1 (schema v2), Velopack-downgrade to build N,
        /// and a v1 reader would classify a perfectly good install as corrupt and delete it. That is
        /// the same data loss this enum exists to prevent, arriving one version later.
        /// </summary>
        UnknownSchemaVersion,
    }

    /// <summary>The manifest, plus why it is missing when it is.</summary>
    internal readonly record struct ManifestReadResult(ManifestReadStatus Status, ModelInstallManifest? Manifest)
    {
        /// <summary>True only for <see cref="ManifestReadStatus.Invalid"/> and
        /// <see cref="ManifestReadStatus.Absent"/>. Every indeterminate state answers NO — the
        /// single question every destructive caller must ask.</summary>
        internal bool MayReplace => Status is ManifestReadStatus.Absent or ManifestReadStatus.Invalid;
    }

    /// <summary>
    /// Read a manifest and say what was found. Callers that only need "is it installed" can test
    /// <c>Status == Valid</c>; callers about to DELETE must consult
    /// <see cref="ManifestReadResult.MayReplace"/>.
    /// </summary>
    internal static ManifestReadResult Read(string manifestPath)
    {
        string json;
        try
        {
            // Read first and let absence surface as an exception rather than pre-checking with
            // File.Exists — that returns false for an ACCESS failure too, which is precisely the
            // conflation this method exists to end.
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ManifestReadStatus.Absent, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ManifestReadStatus.Unreadable, null);
        }

        ModelInstallManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModelInstallManifest>(json);
        }
        catch (JsonException)
        {
            return new(ManifestReadStatus.Invalid, null);
        }

        if (manifest is null) return new(ManifestReadStatus.Invalid, null);

        // Checked BEFORE the structural rules below: a NEWER schema may legitimately carry shapes
        // this build would call malformed, and misreading "newer" as "corrupt" is what deletes it.
        //
        // Strictly GREATER, not merely different. `!=` also caught the DEFAULT 0 that any JSON
        // without a schemaVersion field deserializes to — so `{}` was protected as "from the
        // future" and could never self-heal, when it is simply malformed. An older version is
        // likewise knowable and cleanable; only a version this build cannot reason about is
        // untouchable.
        if (manifest.SchemaVersion > CurrentSchemaVersion)
            return new(ManifestReadStatus.UnknownSchemaVersion, null);
        if (manifest.SchemaVersion < CurrentSchemaVersion)
            return new(ManifestReadStatus.Invalid, null);

        // Structural hardening, load-bearing rather than defensive habit: JSON carrying
        // "files": null or a null array ENTRY deserializes perfectly happily, and the
        // NullReferenceException would then surface later inside MatchesPlan — outside this
        // method's catch, on the Models page's reconciliation path.
        if (manifest.Files is null) return new(ManifestReadStatus.Invalid, null);
        if (manifest.Files.Any(e => e is null || string.IsNullOrEmpty(e.RelativePath)))
            return new(ManifestReadStatus.Invalid, null);

        return new(ManifestReadStatus.Valid, manifest);
    }

    /// <summary>
    /// The manifest, or null for any reason at all. Retained for read-only callers that genuinely
    /// only need "is this installed" — <b>never</b> for a caller deciding whether to delete, which
    /// must use <see cref="Read"/> and honour <see cref="ManifestReadResult.MayReplace"/>.
    /// </summary>
    internal static ModelInstallManifest? TryRead(string manifestPath) => Read(manifestPath).Manifest;

    /// <summary>
    /// Whether this manifest describes exactly the plan the catalog declares — same file set (by
    /// normalized relative path, case-insensitive as Windows is), same sizes, same hashes.
    /// A mismatch means the directory holds some OTHER install, so it is not this model.
    /// </summary>
    internal bool MatchesPlan(string modelName, IReadOnlyList<ModelFile> expected)
    {
        if (!string.Equals(ModelName, modelName, StringComparison.OrdinalIgnoreCase)) return false;
        if (Files.Count != expected.Count) return false;

        var recorded = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Files)
        {
            if (string.IsNullOrEmpty(entry.RelativePath)) return false;
            if (!recorded.TryAdd(entry.RelativePath, entry)) return false;
        }

        foreach (var file in expected)
        {
            if (!recorded.TryGetValue(file.RelativePath, out var entry)) return false;
            if (entry.SizeBytes != file.FileSizeBytes) return false;
            if (!string.Equals(entry.Sha256, file.Sha256Hash, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
