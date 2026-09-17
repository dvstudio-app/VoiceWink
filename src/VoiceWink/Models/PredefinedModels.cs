using VoiceWink.Models.Enums;

namespace VoiceWink.Models;

/// <summary>
/// Predefined LOCAL transcription models available for download — Whisper (whisper.cpp) since the
/// beginning, and Parakeet (sherpa-onnx) since TRN-1 step 3. Each row declares its engine in
/// <see cref="TranscriptionModelInfo.Runtime"/>; that value, not the file layout, is what routes it.
///
/// SHA-256 hashes were taken from the upstream repos' Git LFS pointers — the Whisper rows from
/// ggerganov/whisper.cpp (at the revision named in the mirror path below), the Parakeet bundle
/// from its own repo — and are checked by <see cref="Services.Transcription.ModelDownloadManager"/>
/// after each download to detect corruption or tampering in transit. Downloads come from
/// DV Studio's own mirror (TRN-33; see the MirrorBase comment), so the upstream hashes now also
/// prove the mirror serves byte-identical copies of the verified upstream files.
///
/// <para><b>The WHISPER rows are q8_0 ONLY (owner decision, TRN-6, 2026-08-03).</b> Every one is
/// whisper.cpp's 8-bit build — roughly half the download of the full-precision weights, and the
/// least lossy quantization upstream publishes. The full-precision and q5 rows this array used to
/// carry are GONE; <see cref="Helpers.LocalModelMigration"/> maps their names onto these
/// successors. Parakeet is unaffected: it is a different engine with its own export.</para>
///
/// <para><b>Why q8 and not q5</b> (the previous choice): 8 bits per weight is strictly closer to the
/// original f16 than 5, so accuracy can only be better. Speed favours it too, and for a structural
/// reason rather than a benchmark artifact — <c>q5_0</c> stores four bits in <c>qs</c> and the fifth
/// in a separate <c>qh</c> field, so every block costs extra unpacking, while <c>q8_0</c> is
/// byte-aligned and dequantizes trivially. The cost is ~40% more disk than q5 — still ~45% below
/// full. Honest limit: no measurement of whisper.cpp q8-vs-q5 on a modern AVX2 CPU at dictation
/// lengths exists, here or upstream; the reasoning is structural, not measured.</para>
///
/// <para><b><c>large-v3</c> is deliberately absent.</b> It is the ONE Whisper model upstream
/// publishes no q8_0 build for, and the 2026-08-02 local benchmark measured it at RTF 8.237 — a
/// tenth of real time, so a 10 s dictation takes ~82 s. <c>ggml-large-v3-turbo-q8_0</c> is the top
/// local Whisper model instead.</para>
///
/// <para><b>Array ORDER is the Models page's render order</b> — Parakeet first, then Whisper
/// LARGEST to smallest (owner decision, 2026-08-04; the Whisper block ran smallest-first until
/// then). Both engines therefore lead with what they recommend: Parakeet because it is what
/// <c>DefaultLocalModel.Resolve</c> hands a capable fresh install, and Large V3 Turbo because it is
/// the top Whisper row — the page opens on the recommendation instead of scrolling to it, the same
/// best-first convention the cloud catalogue follows per provider. Nothing derives behaviour from
/// this order: every consumer looks a row up by NAME, and the two <c>First(Whisper)</c> call sites
/// (in tests) want any representative Whisper row. The row is named plainly "Parakeet" and the
/// Whisper rows carry their engine in the name, which is what tells the two engines apart now that
/// they are interleaved at the top of the list.
/// There is one row per size since 2026-08-03: the four <c>.en</c> builds were dropped
/// because each rendered identical accuracy stars, speed stars and download size to its
/// multilingual twin, so the page could not justify the choice it was offering. Restoring one means
/// restoring its handling too — see <c>ModelsPageTests.EnglishOnlyDetection_MatchesTheCatalog_InBothDirections</c>.
/// <c>FileSizeBytes</c> is the
/// EXACT LFS byte count on every row (it feeds the free-space precheck, where an under-estimate is
/// the failure direction), and the Whisper Sha256 values are the LFS <c>oid</c>. Note the upstream
/// README's per-model hashes are git blob SHA-1, NOT sha256 — using those would fail every
/// install.</para>
/// </summary>
public static class PredefinedModels
{
    // DV Studio's own model mirror (TRN-33, 2026-08-27): Cloudflare R2 behind
    // models.voicewink.app, so a third party vanishing can no longer break new installs. The
    // path is write-once and revision-addressed — /whisper/ggerganov-whisper.cpp/5359861c/ names
    // the UPSTREAM repo and the first 8 hex of the immutable revision the bytes were mirrored
    // from (ggerganov/whisper.cpp @ 5359861c739e955e79d9a303bcbc70fb988958b1, the TRN-33 interim
    // pin whose LFS sha256 values were verified equal to the pins below) — which preserves the
    // PR #626 immutability property on our host: changed upstream bytes would need a NEW
    // revision segment, never a rewrite of an existing key. installer/upload-model-mirror.ps1
    // is the only writer and refuses to overwrite; the Sha256Hash pins below stay the
    // end-to-end integrity gate either way.
    private const string MirrorBase = "https://models.voicewink.app/whisper/ggerganov-whisper.cpp/5359861c";

    // The pinned immutable upstream the mirror was built from — the PR #626 interim pin, kept as
    // the FALLBACK source (owner decision, 2026-08-27): tried only when the mirror fails or serves
    // bytes that miss the sha pin, logged loudly so a broken mirror stays visible. The same
    // Sha256Hash pins gate both sources.
    private const string UpstreamBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1";

    /// <summary>The local runtime that serves <paramref name="modelName"/>, or null for a cloud,
    /// unknown or blank name — the ONE name→engine lookup the pipeline's copy and gate policies
    /// share (<c>EmptyTranscriptMessage</c>, <c>NoSpeechBlockPolicy</c>; AUD-36 folded two copies
    /// into it). Either Parakeet bundle spelling resolves to the active row (TRN-29 flip — an
    /// upgrader's un-rewritten selection must keep earning engine-specific behaviour), and the
    /// match is case-insensitive because the runtime seam resolves that way.</summary>
    public static LocalRuntimeKind? RuntimeOf(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        var canonical = ParakeetCatalog.CanonicalName(modelName);
        return Models.FirstOrDefault(m => Helpers.ModelDiskReconciliation.IsSameModel(m.Name, canonical))?.Runtime;
    }

    public static readonly TranscriptionModelInfo[] Models =
    [
        // ── The default, and the first non-Whisper local model (TRN-1 step 3) ───────────────────
        // NVIDIA Parakeet TDT 0.6B v3. WHICH bundle this row is became config-conditional at the
        // TRN-29 flip — the parakeet.cpp GGUF when PCPP_ENABLED, the sherpa-onnx int8 bundle in a
        // kill-switch (-p:PcppEnabled=false) rebuild — so the descriptor lives in
        // ParakeetCatalog with its sibling, single-sourced for this catalog AND the transition
        // coordinator. Full provenance comments live on the descriptors there.
        //
        // Listed FIRST because it is what a capable machine resolves to on a fresh install
        // (DefaultLocalModel.Resolve) — the page opens on the recommendation rather than
        // scrolling past five Whisper rows to reach it.
        ParakeetCatalog.ActiveRow,

        new()
        {
            Name = "ggml-large-v3-turbo-q8_0",
            DisplayName = "Whisper Large V3 Turbo",
            Provider = ModelProvider.Local,
            Runtime = LocalRuntimeKind.Whisper,
            DownloadUrl = $"{MirrorBase}/ggml-large-v3-turbo-q8_0.bin",
            FallbackUrl = $"{UpstreamBase}/ggml-large-v3-turbo-q8_0.bin",
            FileSizeBytes = 874_188_075,
            Sha256Hash = "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"
        },
        new()
        {
            Name = "ggml-medium-q8_0",
            DisplayName = "Whisper Medium",
            Provider = ModelProvider.Local,
            Runtime = LocalRuntimeKind.Whisper,
            DownloadUrl = $"{MirrorBase}/ggml-medium-q8_0.bin",
            FallbackUrl = $"{UpstreamBase}/ggml-medium-q8_0.bin",
            FileSizeBytes = 823_369_779,
            Sha256Hash = "42a1ffcbe4167d224232443396968db4d02d4e8e87e213d3ee2e03095dea6502"
        },
        new()
        {
            Name = "ggml-small-q8_0",
            DisplayName = "Whisper Small",
            Provider = ModelProvider.Local,
            Runtime = LocalRuntimeKind.Whisper,
            DownloadUrl = $"{MirrorBase}/ggml-small-q8_0.bin",
            FallbackUrl = $"{UpstreamBase}/ggml-small-q8_0.bin",
            FileSizeBytes = 264_464_607,
            Sha256Hash = "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f"
        },
        new()
        {
            Name = "ggml-base-q8_0",
            DisplayName = "Whisper Base",
            Provider = ModelProvider.Local,
            Runtime = LocalRuntimeKind.Whisper,
            DownloadUrl = $"{MirrorBase}/ggml-base-q8_0.bin",
            FallbackUrl = $"{UpstreamBase}/ggml-base-q8_0.bin",
            FileSizeBytes = 81_768_585,
            Sha256Hash = "c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9"
        },
        new()
        {
            Name = "ggml-tiny-q8_0",
            DisplayName = "Whisper Tiny",
            Provider = ModelProvider.Local,
            Runtime = LocalRuntimeKind.Whisper,
            DownloadUrl = $"{MirrorBase}/ggml-tiny-q8_0.bin",
            FallbackUrl = $"{UpstreamBase}/ggml-tiny-q8_0.bin",
            FileSizeBytes = 43_537_433,
            Sha256Hash = "c2085835d3f50733e2ff6e4b41ae8a2b8d8110461e18821b09a15c40c42d1cca"
        },
    ];
}
