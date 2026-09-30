using VoiceWink.Models;
using VoiceWink.Models.Enums;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>The three tiers of the bundled engine's catalog, most capable last.</summary>
internal enum LocalModelTier
{
    Light,
    Standard,
    Best,
}

/// <summary>One file of a catalog model, pinned: its mirror URL, exact size and SHA-256, and the
/// upstream it was mirrored from (provenance — the mirror script builds the object from it).</summary>
internal sealed record LocalModelFile(
    string FileName,
    string MirrorUrl,
    long SizeBytes,
    string Sha256,
    string UpstreamUrl);

/// <summary>One catalog model: the GGUF, its licence file, what the tier recommendation needs, and
/// its Accuracy / Speed stars (1-5, the Models page's scale).</summary>
/// <remarks><b>Where the stars come from</b> (the local AI plan's bench, `30-claude-revised-plan.md`,
/// one desktop, the owner's 40-case dictation corpus, the shipped envelope): Accuracy follows the
/// pass score — Gemma 4 12B 40/40 (★5), Qwen3.5 4B 35/40 (★4), Qwen3.5 2B 28/40 (★3). Speed follows
/// the measured cleanup times, relative to one another — the 2B about twice as fast as the 4B on the
/// same CPU (★5 / ★4), the 12B two to five times slower than the 4B on the same graphics card (★3).
/// A model or engine change re-runs the bench and revisits these. No language is named: the bench
/// measured four, and the per-language line waits for more (owner decision 2026-09-30).</remarks>
internal sealed record LocalModelEntry(
    string Id,
    string DisplayName,
    LocalModelTier Tier,
    int Accuracy,
    int Speed,
    string Licence,
    LocalModelFile Model,
    LocalModelFile LicenceFile,
    string ValidatedOnLlamaBuild)
{
    /// <summary>What the download manager installs: a two-file bundle (GGUF + licence) — staged,
    /// verified per file by SHA-256 and exact length, committed atomically. <b>No file carries a
    /// fallback URL</b>: enhancement models come only from the mirror, which is what privacy v5
    /// promises (A37). <c>Runtime</c> is null — these are not speech models.</summary>
    public TranscriptionModelInfo ToDownloadDescriptor() => new()
    {
        Name = Id,
        DisplayName = DisplayName,
        Provider = ModelProvider.Local,
        FileSizeBytes = Model.SizeBytes + LicenceFile.SizeBytes,
        FileSizeIsExact = true,
        Files =
        [
            new ModelFile { RelativePath = Model.FileName, Url = Model.MirrorUrl, FileSizeBytes = Model.SizeBytes, Sha256Hash = Model.Sha256 },
            new ModelFile { RelativePath = LicenceFile.FileName, Url = LicenceFile.MirrorUrl, FileSizeBytes = LicenceFile.SizeBytes, Sha256Hash = LicenceFile.Sha256 },
        ],
    };
}

/// <summary>
/// LAI-3: the bundled engine's model catalog — data, pinned. Every file is served from DV Studio's
/// write-once mirror (<c>models.voicewink.app/llm/…</c>, keys carrying the upstream revision) and
/// verified by SHA-256 and exact size; there is no fallback host. The rows are the exact files the
/// design plan's bench measured (<c>docs/plans/2026-09-23-local-ai-enhancement/bench/</c>): each
/// repo's last GGUF-changing revision predates that run. Picks and evidence: the LAI-3 card and
/// that plan's <c>50-decision.md</c>. Every row was validated on llama.cpp
/// <see cref="LlamaServerPayload.Build"/>; a catalog or engine bump re-runs the bench.
/// <para><c>installer/upload-model-mirror.ps1</c> builds the mirror from the same pins, and its
/// self-test checks parity with this file both ways.</para>
/// </summary>
internal static class LocalModelCatalog
{
    internal const string MirrorBase = "https://models.voicewink.app/llm/";

    /// <summary>Qwen's licence file (Apache 2.0 with Alibaba Cloud's copyright line), identical
    /// in the 2B and 4B repositories; mirrored once per model so each key names its source.</summary>
    private const long QwenLicenceBytes = 11_544;
    private const string QwenLicenceSha256 = "bbedc3fda3305820b977265f01b8619d87570a6739de3a5582c3464840f1e57a";

    internal static readonly LocalModelEntry Light = new(
        Id: "qwen3.5-2b-q4km",
        DisplayName: "Qwen3.5 2B",
        Tier: LocalModelTier.Light,
        Accuracy: 3,
        Speed: 5,
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "Qwen3.5-2B-Q4_K_M.gguf",
            MirrorBase + "unsloth-Qwen3.5-2B-GGUF/f6d5376b/Qwen3.5-2B-Q4_K_M.gguf",
            1_280_835_840,
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf"),
        LicenceFile: new LocalModelFile(
            "LICENSE",
            MirrorBase + "Qwen-Qwen3.5-2B/15852e8c/LICENSE",
            QwenLicenceBytes,
            QwenLicenceSha256,
            "https://huggingface.co/Qwen/Qwen3.5-2B/resolve/15852e8c16360a2fea060d615a32b45270f8a8fc/LICENSE"),
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    internal static readonly LocalModelEntry Standard = new(
        Id: "qwen3.5-4b-q4km",
        DisplayName: "Qwen3.5 4B",
        Tier: LocalModelTier.Standard,
        Accuracy: 4,
        Speed: 4,
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "Qwen3.5-4B-Q4_K_M.gguf",
            MirrorBase + "unsloth-Qwen3.5-4B-GGUF/e87f1764/Qwen3.5-4B-Q4_K_M.gguf",
            2_740_937_888,
            "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf"),
        LicenceFile: new LocalModelFile(
            "LICENSE",
            MirrorBase + "Qwen-Qwen3.5-4B/851bf6e8/LICENSE",
            QwenLicenceBytes,
            QwenLicenceSha256,
            "https://huggingface.co/Qwen/Qwen3.5-4B/resolve/851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a/LICENSE"),
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    internal static readonly LocalModelEntry Best = new(
        Id: "gemma-4-12b-qat",
        DisplayName: "Gemma 4 12B",
        Tier: LocalModelTier.Best,
        Accuracy: 5,
        Speed: 3,
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
            MirrorBase + "unsloth-gemma-4-12B-it-qat-GGUF/980b060c/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
            6_716_356_800,
            "90fd44e29e0d7cffeb0fd00dc73cfdab9ed0b0e95306ecf7821ea634c940c370",
            "https://huggingface.co/unsloth/gemma-4-12B-it-qat-GGUF/resolve/980b060c40a8539ac159e0501a3e0f66a6365af3/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf"),
        // No Google repository publishes a LICENSE file for Gemma 4; Google's Gemma terms page
        // names Apache 2.0, so the canonical Apache text is mirrored, keyed by its own hash.
        LicenceFile: new LocalModelFile(
            "LICENSE-2.0.txt",
            MirrorBase + "apache-license-2.0/cfc7749b/LICENSE-2.0.txt",
            11_358,
            "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
            "https://www.apache.org/licenses/LICENSE-2.0.txt"),
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    /// <summary>Display order: the default first, then the larger, then the smaller.</summary>
    internal static readonly IReadOnlyList<LocalModelEntry> All = [Standard, Best, Light];

    internal static LocalModelEntry? Find(string id)
        => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    internal static LocalModelEntry For(LocalModelTier tier) => All.Single(e => e.Tier == tier);
}
