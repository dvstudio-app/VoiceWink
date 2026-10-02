using VoiceWink.Helpers;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-3: which catalog tier this PC should use, from its <see cref="LocalHardwareProfile"/>. Pure.
/// <para>RAM is the INSTALLED figure (<see cref="LocalHardwareProfile.ReadTotalRam"/>), so a
/// "16 GB" PC meets the 16 GiB floor. <b>The floors are PROVISIONAL</b> — set from one laptop (Core Ultra 7 258V, Arc 140V
/// integrated, 32 GB) and one desktop (RTX 3080), with Parakeet loaded beside the model
/// (<c>docs/plans/2026-09-23-local-ai-enhancement/bench/README.md</c>, "Laptop on the bundled
/// engine"). LAI-0's open rows (4 GB and 8 GB cards, AMD, a non-AVX-512 CPU) re-check them:</para>
/// <list type="bullet">
/// <item><b>Best</b> (Gemma 4 12B) needs a dedicated GPU with ≥ 10 GiB: weights + KV cache +
/// Vulkan buffers ≈ 7.8 GB plus resident Parakeet do not fit 8 GB (plan R3-B7), it runs fast on
/// the owner's 10 GB RTX 3080 with Parakeet beside it (2026-10-01; the floor was 12 GiB until
/// then, never measured), and on an integrated GPU it measured 12 s per dictation — too slow.</item>
/// <item><b>Large</b> (Qwen3.5 9B) needs a dedicated GPU with ≥ 8 GiB: a 5.7 GB file, about a
/// gigabyte less than Gemma's; not yet measured on an 8 GB card (an LAI-0 row) — where it does not
/// fit, the server may keep fewer layers on the GPU (slower), or a load that dies before health is
/// retried on the CPU; neither is measured. On the integrated-GPU laptop it
/// was about twice the 4B's time, so it is never the pick without a dedicated GPU.</item>
/// <item><b>Standard</b> needs ≥ 16 GiB RAM with a dedicated GPU of ≥ 6 GiB, or ≥ 32 GiB RAM
/// without one: on the laptop the 4B beside Parakeet peaked at +9.8 GB commit and left 4.3 GB
/// free on 32 GB with a working day's apps open; on the desktop, with every layer on the GPU,
/// the server still committed ~4 GiB of RAM, so a GPU does not remove the RAM need.</item>
/// <item><b>Light</b> needs ≥ 16 GiB RAM (the 2B beside Parakeet peaked at +3.6 GB).</item>
/// </list>
/// Unknown RAM recommends nothing; unknown VRAM counts as no dedicated GPU.
/// <para><b>A measurement overrides the floors, both ways</b> (owner, 2026-10-01): once a downloaded
/// model's first-use check has timed it on this PC, that time decides — over
/// <see cref="TooSlowMs"/> and the model is never the pick, at or under it and the model is
/// eligible even above its floor. The floors only guess for models not yet measured. The measured
/// case: the floors picked Gemma on the Arc 140V laptop, whose check took 3.7 s and whose
/// dictations took 11–38 s.</para>
/// </summary>
internal static class LocalModelRecommendation
{
    private const long GiB = 1L << 30;

    internal const long BestMinVram = 10 * GiB;
    internal const long LargeMinVram = 8 * GiB;
    internal const long StandardMinVram = 6 * GiB;
    internal const long GpuTierMinRam = 16 * GiB;
    internal const long StandardCpuMinRam = 32 * GiB;
    internal const long LightMinRam = 16 * GiB;

    /// <summary>The first-use check's timed run above which a model is too slow for dictation on
    /// this PC. PROVISIONAL (owner's estimate, 2026-10-01): a real dictation took 3–10 times the
    /// check on the Arc 140V laptop; the per-machine test sweep sets the final value.</summary>
    internal const int TooSlowMs = 1500;

    /// <summary>Speed stars from the check's measured time, banded on <see cref="TooSlowMs"/> so a
    /// model too slow to recommend never shows more than two stars.</summary>
    internal static int MeasuredSpeedStars(int ms) => ms switch
    {
        <= TooSlowMs / 4 => 5,
        <= TooSlowMs / 2 => 4,
        <= TooSlowMs => 3,
        <= TooSlowMs * 2 => 2,
        _ => 1,
    };

    internal static bool IsTooSlow(int? measuredMs) => measuredMs > TooSlowMs;

    /// <summary>The recommended tier, or null when this PC is below every floor (or its memory
    /// cannot be read).</summary>
    internal static LocalModelTier? Recommend(LocalHardwareProfile profile) => Recommend(profile, _ => null);

    /// <summary>The largest tier that is fast enough where measured, or fits the floors where not;
    /// null when none is. <paramref name="measuredMs"/> gives a tier's measured check time, or null.</summary>
    internal static LocalModelTier? Recommend(LocalHardwareProfile profile, Func<LocalModelTier, int?> measuredMs)
    {
        foreach (var tier in new[] { LocalModelTier.Best, LocalModelTier.Large, LocalModelTier.Standard, LocalModelTier.Light })
        {
            if (measuredMs(tier) is { } ms ? !IsTooSlow(ms) : Fits(tier, profile))
            {
                return tier;
            }
        }
        return null;
    }

    /// <summary>Does <paramref name="profile"/> meet <paramref name="tier"/>'s floor?</summary>
    internal static bool Fits(LocalModelTier tier, LocalHardwareProfile profile)
    {
        if (profile.TotalRamBytes is not { } ram)
        {
            return false;
        }
        var vram = profile.LargestDedicatedVramBytes ?? 0;
        return tier switch
        {
            LocalModelTier.Best => vram >= BestMinVram && ram >= GpuTierMinRam,
            LocalModelTier.Large => vram >= LargeMinVram && ram >= GpuTierMinRam,
            LocalModelTier.Standard => (vram >= StandardMinVram && ram >= GpuTierMinRam) || ram >= StandardCpuMinRam,
            LocalModelTier.Light => ram >= LightMinRam,
            _ => false,
        };
    }

    /// <summary>The one-line reason a row sits below this PC's floor, or null when it fits.
    /// Brief by the UI-text rule: what is missing, nothing else.</summary>
    internal static string? FloorNote(LocalModelTier tier, LocalHardwareProfile profile)
    {
        if (Fits(tier, profile))
        {
            return null;
        }
        return tier switch
        {
            LocalModelTier.Best => "Needs 16 GB of memory and a graphics card with 10 GB.",
            LocalModelTier.Large => "Needs 16 GB of memory and a graphics card with 8 GB.",
            LocalModelTier.Standard => "Needs 32 GB of memory, or 16 GB and a graphics card with 6 GB.",
            _ => "Needs 16 GB of memory.",
        };
    }

    /// <summary>The order the model rows are shown in: the pick for this PC first, then the rest
    /// largest to smallest (the Models page's Whisper order). Display only —
    /// <see cref="LocalModelCatalog.All"/> keeps the default first, which the "first installed
    /// model" fallback relies on.</summary>
    internal static IReadOnlyList<LocalModelEntry> DisplayOrder(LocalModelTier? recommended)
        => LocalModelCatalog.All
            .OrderBy(e => e.Tier == recommended ? 0 : 1)
            .ThenByDescending(e => e.Tier)
            .ToList();
}
