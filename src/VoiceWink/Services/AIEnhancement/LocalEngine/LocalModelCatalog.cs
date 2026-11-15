using VoiceWink.Models;
using VoiceWink.Models.Enums;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>The tiers of the bundled engine's catalog, one row each, ordered by file size (the
/// "larger on a tie" display rule reads this order).</summary>
internal enum LocalModelTier
{
    Light,
    Standard,
    Fast,
    Large,
    Balanced,
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

/// <summary>LAI-19: how many of a model's measured replies were bad or refused.</summary>
internal readonly record struct LocalModelBad(int Failed, int Of);

/// <summary>LAI-19: a model's bad or refused benchmark replies per shipped prompt class.</summary>
internal sealed record LocalModelBadRates(LocalModelBad Cleanup, LocalModelBad EmailChat, LocalModelBad Translate, LocalModelBad Assistant);

/// <summary>One catalog model: the GGUF, its licence files, what the tier recommendation needs, and
/// its Accuracy / Speed stars (1-5, the Models page's scale).</summary>
/// <remarks><b>Where the stars come from.</b> Accuracy and <c>BadRates</c> (LAI-19, LAI-22): every row
/// but the 2B is read on the enhancement benchmark's desktop run of 2026-11-14
/// (<c>tools/VoiceWink.AsrBench/enhancement/results/</c>, label lai22-gemma-e4b-bolt): every case, 3
/// passes, per shipped prompt class. The 2B was not in that run and keeps its rates from the runs of
/// 2026-11-11 and 2026-11-12 (labels baseline-v2 and post-w3). A refused reply counts as bad, and so
/// does an e-mail left as one block where the benchmark expects paragraphs.
/// Accuracy bands the share of good held-out replies across every shipped prompt: 95%
/// and up ★5 (where the best cloud models measured, and no built-in model; owner 2026-11-12), 80% ★4,
/// 70% ★3, 60% ★2 — Gemma 4 12B 92%, Nemotron 3.5 Bolt 84%, Gemma 4 E4B 79%, Qwen3.5 9B 78%,
/// Qwen3.5 4B 65%, Qwen3.5 2B 52%. Speed is only the fixed star the "ratings changed" notice compares
/// with; the star a user sees comes from this PC's measured or estimated time
/// (<see cref="LocalModelRecommendation.Speeds"/>).
/// A model or engine change re-runs the bench and revisits these. No language is named (owner
/// decisions 2026-09-30 and 2026-11-12).</remarks>
internal sealed record LocalModelEntry(
    string Id,
    string DisplayName,
    LocalModelTier Tier,
    int Accuracy,
    int Speed,
    LocalModelBadRates BadRates,
    string Licence,
    LocalModelFile Model,
    IReadOnlyList<LocalModelFile> LicenceFiles,
    string ValidatedOnLlamaBuild,
    // LAI-22: a retired model is listed only while installed, is never the pick, and offers no Download.
    bool Retired = false)
{
    /// <summary>What the download manager installs: one bundle (GGUF + its licence files) — staged,
    /// verified per file by SHA-256 and exact length, committed atomically. <b>No file carries a
    /// fallback URL</b>: enhancement models come only from the mirror, which is what the privacy policy (v5, and v6 §3)
    /// promises (A37). <c>Runtime</c> is null — these are not speech models.</summary>
    public TranscriptionModelInfo ToDownloadDescriptor() => new()
    {
        Name = Id,
        DisplayName = DisplayName,
        Provider = ModelProvider.Local,
        FileSizeBytes = Model.SizeBytes + LicenceFiles.Sum(f => f.SizeBytes),
        FileSizeIsExact = true,
        Files =
        [
            .. new[] { Model }.Concat(LicenceFiles).Select(f => new ModelFile
            {
                RelativePath = f.FileName, Url = f.MirrorUrl, FileSizeBytes = f.SizeBytes, Sha256Hash = f.Sha256,
            }),
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
    /// in the 2B, 4B and 9B repositories; mirrored once per model so each key names its source.</summary>
    private const long QwenLicenceBytes = 11_544;
    private const string QwenLicenceSha256 = "bbedc3fda3305820b977265f01b8619d87570a6739de3a5582c3464840f1e57a";

    // No Google repository publishes a LICENSE file for Gemma 4; Google's Gemma terms page
    // names Apache 2.0, so the canonical Apache text is mirrored, keyed by its own hash.
    private static readonly LocalModelFile ApacheLicence = new(
        "LICENSE-2.0.txt",
        MirrorBase + "apache-license-2.0/cfc7749b/LICENSE-2.0.txt",
        11_358,
        "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
        "https://www.apache.org/licenses/LICENSE-2.0.txt");

    internal static readonly LocalModelEntry Light = new(
        Id: "qwen3.5-2b-q4km",
        DisplayName: "Qwen3.5 2B",
        Tier: LocalModelTier.Light,
        Accuracy: 1,
        Speed: 5,
        BadRates: new(Cleanup: new(231, 426), EmailChat: new(114, 360), Translate: new(111, 225), Assistant: new(32, 180)),
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "Qwen3.5-2B-Q4_K_M.gguf",
            MirrorBase + "unsloth-Qwen3.5-2B-GGUF/f6d5376b/Qwen3.5-2B-Q4_K_M.gguf",
            1_280_835_840,
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf"),
        LicenceFiles: [new LocalModelFile(
            "LICENSE",
            MirrorBase + "Qwen-Qwen3.5-2B/15852e8c/LICENSE",
            QwenLicenceBytes,
            QwenLicenceSha256,
            "https://huggingface.co/Qwen/Qwen3.5-2B/resolve/15852e8c16360a2fea060d615a32b45270f8a8fc/LICENSE")],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build,
        Retired: true);

    internal static readonly LocalModelEntry Standard = new(
        Id: "qwen3.5-4b-q4km",
        DisplayName: "Qwen3.5 4B",
        Tier: LocalModelTier.Standard,
        Accuracy: 2,
        Speed: 4,
        BadRates: new(Cleanup: new(153, 570), EmailChat: new(27, 180), Translate: new(78, 225), Assistant: new(4, 90)),
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "Qwen3.5-4B-Q4_K_M.gguf",
            MirrorBase + "unsloth-Qwen3.5-4B-GGUF/e87f1764/Qwen3.5-4B-Q4_K_M.gguf",
            2_740_937_888,
            "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf"),
        LicenceFiles: [new LocalModelFile(
            "LICENSE",
            MirrorBase + "Qwen-Qwen3.5-4B/851bf6e8/LICENSE",
            QwenLicenceBytes,
            QwenLicenceSha256,
            "https://huggingface.co/Qwen/Qwen3.5-4B/resolve/851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a/LICENSE")],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build,
        Retired: true);

    /// <summary>Qwen3.5 9B (owner, 2026-10-01: in his own tests it cleaned dictations the 4B
    /// failed). Bench, RTX 3080 desktop: 37/40 against the 4B's 35/40 and Gemma's 40/40, median
    /// 0.35 s against 0.28 s and 0.83 s; laptop on Ollama: median 6.2 s, about twice the 4B.</summary>
    internal static readonly LocalModelEntry Large = new(
        Id: "qwen3.5-9b-q4km",
        DisplayName: "Qwen3.5 9B",
        Tier: LocalModelTier.Large,
        Accuracy: 3,
        Speed: 3,
        BadRates: new(Cleanup: new(98, 570), EmailChat: new(21, 180), Translate: new(52, 225), Assistant: new(0, 90)),
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "Qwen3.5-9B-Q4_K_M.gguf",
            MirrorBase + "unsloth-Qwen3.5-9B-GGUF/3885219b/Qwen3.5-9B-Q4_K_M.gguf",
            5_680_522_464,
            "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8",
            "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/3885219b6810b007914f3a7950a8d1b469d598a5/Qwen3.5-9B-Q4_K_M.gguf"),
        LicenceFiles: [new LocalModelFile(
            "LICENSE",
            MirrorBase + "Qwen-Qwen3.5-9B/c2022362/LICENSE",
            QwenLicenceBytes,
            QwenLicenceSha256,
            "https://huggingface.co/Qwen/Qwen3.5-9B/resolve/c202236235762e1c871ad0ccb60c8ee5ba337b9a/LICENSE")],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build,
        Retired: true);

    internal static readonly LocalModelEntry Best = new(
        Id: "gemma-4-12b-qat",
        DisplayName: "Gemma 4 12B",
        Tier: LocalModelTier.Best,
        Accuracy: 4,
        Speed: 2,
        BadRates: new(Cleanup: new(42, 570), EmailChat: new(5, 180), Translate: new(6, 225), Assistant: new(0, 90)),
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
            MirrorBase + "unsloth-gemma-4-12B-it-qat-GGUF/980b060c/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
            6_716_356_800,
            "90fd44e29e0d7cffeb0fd00dc73cfdab9ed0b0e95306ecf7821ea634c940c370",
            "https://huggingface.co/unsloth/gemma-4-12B-it-qat-GGUF/resolve/980b060c40a8539ac159e0501a3e0f66a6365af3/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf"),
        LicenceFiles: [ApacheLicence],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    /// <summary>LAI-22: Gemma 4 E4B, the pick where Bolt is too slow on this PC and it is not.</summary>
    internal static readonly LocalModelEntry Fast = new(
        Id: "gemma-4-e4b-qat",
        DisplayName: "Gemma 4 E4B",
        Tier: LocalModelTier.Fast,
        Accuracy: 3,
        Speed: 4,
        BadRates: new(Cleanup: new(92, 570), EmailChat: new(11, 180), Translate: new(91, 225), Assistant: new(3, 90)),
        Licence: "Apache-2.0",
        Model: new LocalModelFile(
            "gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf",
            MirrorBase + "unsloth-gemma-4-E4B-it-qat-GGUF/8c5a9e4f/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf",
            4_215_695_776,
            "df0fd4ee07072c607c29a0a1cb4f98918426cca12f45a2776bdd6ee6d09a4de3",
            "https://huggingface.co/unsloth/gemma-4-E4B-it-qat-GGUF/resolve/8c5a9e4fd5482e2be20fe0bf013b4c262a8f4265/gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf"),
        LicenceFiles: [ApacheLicence],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    /// <summary>LAI-22: NVIDIA Nemotron 3.5 Bolt E4B, the default; OpenMDW 1.1, post-trained from
    /// Gemma 4 E4B (Apache 2.0), so its bundle carries both licence texts.</summary>
    internal static readonly LocalModelEntry Balanced = new(
        Id: "nemotron-3.5-bolt-e4b-q4km",
        DisplayName: "Nemotron 3.5 Bolt",
        Tier: LocalModelTier.Balanced,
        Accuracy: 4,
        Speed: 3,
        BadRates: new(Cleanup: new(51, 570), EmailChat: new(7, 180), Translate: new(47, 225), Assistant: new(1, 90)),
        Licence: "OpenMDW-1.1",
        Model: new LocalModelFile(
            "Nemotron-3.5-Bolt-E4B-Q4_K_M.gguf",
            MirrorBase + "nvidia-NVIDIA-Nemotron-3.5-Bolt-E4B-4GB/1db54d5b/Nemotron-3.5-Bolt-E4B-Q4_K_M.gguf",
            6_180_446_208,
            "c8af2b8aea10a690a1d9491db744a9446660173cb38fc489612b556615a69612",
            "https://huggingface.co/nvidia/NVIDIA-Nemotron-3.5-Bolt-E4B-4GB/resolve/1db54d5b12533ff1bcaa8e42cf4c667988a1f1be/Nemotron-3.5-Bolt-E4B-Q4_K_M.gguf"),
        LicenceFiles:
        [
            new LocalModelFile(
                "LICENSE.OpenMDW-1.1",
                // Not a model repository, so the key is the file's own hash (the mirror script's rule).
                MirrorBase + "OpenMDW-OpenMDW/2ab44b68/LICENSE.OpenMDW-1.1",
                2_619,
                "2ab44b68365473c112f5092211a38f231cb23e50de68b75a13369adbd76a74df",
                "https://raw.githubusercontent.com/OpenMDW/OpenMDW/9067ba54b2f84bc9e15a05734f570ec88e9f445d/1.1/LICENSE.OpenMDW-1.1"),
            ApacheLicence,
        ],
        ValidatedOnLlamaBuild: LlamaServerPayload.Build);

    /// <summary>Catalog order: the default first, then the other rows by accuracy; the "first installed
    /// model" fallback reads this order. The rows are shown in
    /// <see cref="LocalModelRecommendation.DisplayOrder"/>.</summary>
    internal static readonly IReadOnlyList<LocalModelEntry> All = [Balanced, Best, Fast, Large, Standard, Light];

    internal static LocalModelEntry? Find(string id)
        => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    internal static LocalModelEntry For(LocalModelTier tier) => All.Single(e => e.Tier == tier);
}
