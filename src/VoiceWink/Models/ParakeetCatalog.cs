namespace VoiceWink.Models;

using VoiceWink.Models.Enums;

/// <summary>
/// TRN-29 G6: the ONE home for both Parakeet bundle descriptors and for which of them is THIS
/// build's catalog row.
///
/// <para><b>Why a dedicated type.</b> The rollout flip made the Parakeet catalog row
/// CONFIG-CONDITIONAL (<c>PCPP_ENABLED</c> ⇒ the GGUF bundle served by parakeet.cpp; otherwise the
/// sherpa-onnx bundle — the kill-switch rebuild's catalog must be the pre-flip one, or a rebuilt
/// OFF binary meets selections its registry throws on). Before this type, the sherpa row lived
/// inline in <see cref="PredefinedModels"/> and the GGUF descriptor inside
/// <c>ParakeetBackendCoordinator</c>, and the coordinator DEFAULTED its sherpa descriptor to
/// "the first catalog row whose runtime is Parakeet" — which, the moment the catalog row became
/// the GGUF bundle, silently re-targeted every sherpa-keyed operation (installed derivation,
/// tombstone probes, delete) at the WRONG artifact. The plan round named that line the flip's most
/// dangerous. Both descriptors are now EXPLICIT constants here, referenced by the catalog and the
/// coordinator alike; nothing derives either by position or by runtime kind.</para>
///
/// <para><b>Aliasing: either name resolves, in either config.</b> Persisted selections, App Mode
/// overrides and immutable History rows can carry EITHER spelling (an upgrader's settings say
/// <see cref="SherpaName"/> forever unless re-selected; a kill-switch rebuild meets
/// <see cref="GgufName"/> ones). <see cref="CanonicalName"/> maps both onto the ACTIVE row's name
/// and is consumed by every exact-name catalog surface — the runtime's ownership check, display
/// names, the default-model resolver, language support, ratings, the retry picker's same-model
/// row. There is deliberately NO persisted-selection rewrite: a rewrite could never reach History
/// (immutable SQLite), so lookup-time aliasing is the only design that actually covers the data
/// that exists.</para>
///
/// <para><b>One deliberate non-consumer:</b> <c>InstalledCatalogModels</c> projects DISK names
/// onto catalog rows and must NOT canonicalize — mapping the sherpa DIRECTORY onto the GGUF row
/// would present a model as installed off bytes that are not it. The Models page's Parakeet row
/// presentation is the coordinator's <c>RowInstalled()</c>, not the disk projection.</para>
/// </summary>
internal static class ParakeetCatalog
{
    /// <summary>sherpa-onnx's published int8 export of Parakeet TDT 0.6B v3 (TRN-1 step 3),
    /// served from DV Studio's own model mirror (TRN-33, 2026-08-27 — Cloudflare R2 behind
    /// <c>models.voicewink.app</c>; see <c>PredefinedModels.MirrorBase</c> for the full design
    /// comment). The write-once path names the upstream repo and the first 8 hex of the immutable
    /// revision the bytes were mirrored from
    /// (csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 @
    /// 2bda32ec70b097a55adaa07d9a7173915b43cc78, the TRN-33 interim pin at which the three
    /// LFS-backed files' sha256 values were verified equal to the pins below; tokens.txt is
    /// non-LFS and was verified separately). The Sha256Hash pins stay the integrity
    /// gate.</summary>
    private const string SherpaBase =
        "https://models.voicewink.app/parakeet/csukuangfj-sherpa-int8/2bda32ec";

    /// <summary>The pinned immutable upstream the mirror was built from — the PR #626 interim
    /// pin, kept as the FALLBACK source (owner decision, 2026-08-27): tried only when the mirror
    /// fails or serves bytes that miss the sha pin, logged loudly so a broken mirror stays
    /// visible. The same Sha256Hash pins gate both sources.</summary>
    private const string SherpaUpstreamBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78";

    /// <summary>The sherpa-era bundle (directory) name — the only Parakeet name that existed
    /// before the TRN-29 flip, so the spelling every pre-flip selection and History row carries.</summary>
    internal const string SherpaName = "parakeet-tdt-0.6b-v3";

    /// <summary>The GGUF payload file name — also the leaf the resident server is pointed at.</summary>
    internal const string GgufFileName = "tdt-0.6b-v3-q8_0.gguf";

    /// <summary>The GGUF bundle (directory) name under the Models root.</summary>
    internal const string GgufName = "parakeet-tdt-0.6b-v3-gguf";

    /// <summary>Measured from the exact binary every TRN-29 G-gate ran
    /// (docs/plans/2026-08-23-trn29-parakeetcpp-swap/evidence/artifact-pins.md) — pinning the
    /// VALIDATED configuration, not merely a version number.</summary>
    internal const long GgufFileSizeBytes = 940_663_680;
    internal const string GgufSha256 = "4d69a4a6683f4f2d952bad794c1357ca6eb628027695b4699c5a9ad4cd07d757";

    /// <summary>
    /// NVIDIA Parakeet TDT 0.6B v3, int8 ONNX, served by sherpa-onnx — four files installed
    /// transactionally (TRN-1 step 2), not one .bin.
    ///
    /// <para>Files are mirrored per file from the upstream Hugging Face repo rather than from the
    /// sherpa-onnx GitHub release, which ships the same model as ONE .tar.bz2 — an archive the
    /// installer cannot consume, since it downloads and hashes per file and has no extraction
    /// step. The mirror keeps that per-file layout, and R2 answers <c>accept-ranges: bytes</c>,
    /// so the Range-resume machinery works.</para>
    ///
    /// <para>Sizes and hashes were VERIFIED, not read off a page (2026-08-03): the joiner was
    /// downloaded and hashed locally and matched HF's LFS oid exactly, which is what makes the two
    /// large hashes safe to take from LFS metadata; tokens.txt is not LFS and was hashed directly.
    /// Total 670,478,772 bytes.</para>
    /// </summary>
    internal static readonly TranscriptionModelInfo SherpaDescriptor = new()
    {
        Name = SherpaName,
        DisplayName = "Parakeet",
        Provider = ModelProvider.Local,
        Runtime = LocalRuntimeKind.Parakeet,
        // Aggregate of the four files below — the Models page renders this, and the free-space
        // precheck uses it. Asserted equal to the sum by the catalog-integrity test.
        FileSizeBytes = 670_478_772,
        Files =
        [
            new ModelFile
            {
                RelativePath = "encoder.int8.onnx",
                Url = $"{SherpaBase}/encoder.int8.onnx",
                FallbackUrl = $"{SherpaUpstreamBase}/encoder.int8.onnx",
                FileSizeBytes = 652_184_281,
                Sha256Hash = "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247",
            },
            new ModelFile
            {
                RelativePath = "decoder.int8.onnx",
                Url = $"{SherpaBase}/decoder.int8.onnx",
                FallbackUrl = $"{SherpaUpstreamBase}/decoder.int8.onnx",
                FileSizeBytes = 11_845_275,
                Sha256Hash = "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e",
            },
            new ModelFile
            {
                RelativePath = "joiner.int8.onnx",
                Url = $"{SherpaBase}/joiner.int8.onnx",
                FallbackUrl = $"{SherpaUpstreamBase}/joiner.int8.onnx",
                FileSizeBytes = 6_355_277,
                Sha256Hash = "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3",
            },
            new ModelFile
            {
                RelativePath = "tokens.txt",
                Url = $"{SherpaBase}/tokens.txt",
                FallbackUrl = $"{SherpaUpstreamBase}/tokens.txt",
                FileSizeBytes = 93_939,
                Sha256Hash = "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d",
            },
        ],
    };

    /// <summary>
    /// The same model as a parakeet.cpp GGUF, served by the resident <c>parakeet-server</c>
    /// (TRN-29). DisplayName is deliberately IDENTICAL to the sherpa row's — the flip is an
    /// engine swap behind one user-visible model, and display continuity is the point of the
    /// one-row design.
    /// </summary>
    internal static readonly TranscriptionModelInfo GgufDescriptor = new()
    {
        Name = GgufName,
        DisplayName = "Parakeet",
        Provider = ModelProvider.Local,
        Runtime = LocalRuntimeKind.Parakeet,
        FileSizeBytes = GgufFileSizeBytes,
        Files =
        [
            new ModelFile
            {
                RelativePath = GgufFileName,
                // DV Studio's mirror (TRN-33) — same write-once scheme as SherpaBase above. The
                // revision segment names the upstream source: mudler/parakeet-cpp-gguf @
                // bf0af9f425fa01809cadec671b3cb672709d13e9 (the TRN-33 interim pin, whose LFS
                // sha256 was verified equal to GgufSha256 and HEAD-probed at 940,663,680 bytes).
                Url = "https://models.voicewink.app/parakeet/mudler-parakeet-cpp-gguf/bf0af9f4/" + GgufFileName,
                // Fallback: the pinned upstream the mirror was built from (same rule as
                // SherpaUpstreamBase above).
                FallbackUrl = "https://huggingface.co/mudler/parakeet-cpp-gguf/resolve/bf0af9f425fa01809cadec671b3cb672709d13e9/" + GgufFileName,
                FileSizeBytes = GgufFileSizeBytes,
                Sha256Hash = GgufSha256,
            },
        ],
    };

#if PCPP_ENABLED
    /// <summary>This build's Parakeet catalog row (the parakeet.cpp era).</summary>
    internal static TranscriptionModelInfo ActiveRow => GgufDescriptor;

    /// <summary>The other era's bundle — the coordinator's auxiliary artifact (serve-until-migrated,
    /// then the auto-cleanup's delete target).</summary>
    internal static TranscriptionModelInfo LegacyRow => SherpaDescriptor;
#else
    /// <summary>This build's Parakeet catalog row (the sherpa era / kill-switch rebuild).</summary>
    internal static TranscriptionModelInfo ActiveRow => SherpaDescriptor;

    /// <summary>The other era's bundle. In an OFF rebuild an installed GGUF is an ACCEPTED
    /// 940 MB orphan (no coordinator exists to serve or delete it): the kill switch is an
    /// incident lever, and having it delete user disk would be worse. The ON build's cleanup
    /// surfaces reclaim it when the flag returns. The orphan's stem still ENUMERATES (the
    /// manager's auxiliary-bundle list is this property), which is deliberate — it never lights
    /// the Parakeet row (<see cref="HoldsServableArtifact"/> refuses it in OFF, test-pinned),
    /// and any unknown-stems surface that lists it is incidentally a reclaim path.</summary>
    internal static TranscriptionModelInfo LegacyRow => GgufDescriptor;
#endif

    /// <summary>True when <paramref name="modelName"/> is either Parakeet bundle name.</summary>
    internal static bool IsParakeetName(string? modelName) =>
        !string.IsNullOrWhiteSpace(modelName)
        && (string.Equals(modelName, SherpaName, global::System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelName, GgufName, global::System.StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The name every exact-name catalog surface should look up: either Parakeet spelling maps to
    /// the ACTIVE row's name; anything else passes through unchanged (null and blank included, so
    /// "never chosen" stays distinguishable from "chose something" — the LocalModelMigration rule).
    /// </summary>
    internal static string? CanonicalName(string? modelName) =>
        IsParakeetName(modelName) ? ActiveRow.Name : modelName;

    /// <summary>
    /// Do these on-disk bundle stems hold a Parakeet artifact THIS build's engine can serve?
    ///
    /// <para>The "which models can the user run right now" surfaces (the TRN-17 retry picker, the
    /// App Mode local dropdown) project disk stems onto catalog rows by exact name — correct for
    /// every row except this one during the migration window: the disk holds the SHERPA bundle,
    /// the ON build's catalog row is the GGUF, and the engine IS serving (the coordinator's
    /// sherpa fall-through), so the row must count as held. Deliberately ONE direction and
    /// config-aware: in an ON build the legacy sherpa artifact serves (count it); in a
    /// kill-switch rebuild an orphaned GGUF cannot serve anything (do NOT count it — offering a
    /// row whose first use immediately reports not-downloaded is the picker lying).</para>
    ///
    /// <para><b>Stated bound (Kimi diff r1):</b> a STEM is name evidence, not a verified
    /// install, so phase-2-delete DEBRIS (manifest gone, payload lingering) also counts here —
    /// and in the one edge where nothing serves it (debris + the pcpp fuse latched) the picker
    /// over-lists a row whose first use reports not-downloaded and self-heals by downloading
    /// the active catalog row. Accepted: the alternative (consulting the manifest-backed
    /// install) would drop the row for REAL installs in callers that only hold stems, and the
    /// over-list edge is cosmetic, transient, and self-repairing.</para>
    /// </summary>
    internal static bool HoldsServableArtifact(global::System.Collections.Generic.IEnumerable<string> stems)
    {
        foreach (var stem in stems)
        {
            if (string.Equals(stem, ActiveRow.Name, global::System.StringComparison.OrdinalIgnoreCase))
                return true;
#if PCPP_ENABLED
            if (string.Equals(stem, SherpaName, global::System.StringComparison.OrdinalIgnoreCase))
                return true; // the migration-window fallback: the coordinator serves sherpa
#endif
        }
        return false;
    }
}
