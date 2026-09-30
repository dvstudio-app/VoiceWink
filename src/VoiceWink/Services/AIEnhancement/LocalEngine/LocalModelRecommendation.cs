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
/// <item><b>Best</b> needs a dedicated GPU with ≥ 12 GiB: weights + KV cache + Vulkan buffers
/// ≈ 7.8 GB plus resident Parakeet do not fit 8 GB (plan R3-B7), and on an integrated GPU it
/// measured 12 s per dictation — too slow.</item>
/// <item><b>Standard</b> needs ≥ 16 GiB RAM with a dedicated GPU of ≥ 6 GiB, or ≥ 32 GiB RAM
/// without one: on the laptop the 4B beside Parakeet peaked at +9.8 GB commit and left 4.3 GB
/// free on 32 GB with a working day's apps open; on the desktop, with every layer on the GPU,
/// the server still committed ~4 GiB of RAM, so a GPU does not remove the RAM need.</item>
/// <item><b>Light</b> needs ≥ 16 GiB RAM (the 2B beside Parakeet peaked at +3.6 GB).</item>
/// </list>
/// Unknown RAM recommends nothing; unknown VRAM counts as no dedicated GPU.
/// </summary>
internal static class LocalModelRecommendation
{
    private const long GiB = 1L << 30;

    internal const long BestMinVram = 12 * GiB;
    internal const long StandardMinVram = 6 * GiB;
    internal const long GpuTierMinRam = 16 * GiB;
    internal const long StandardCpuMinRam = 32 * GiB;
    internal const long LightMinRam = 16 * GiB;

    /// <summary>The recommended tier, or null when this PC is below every floor (or its memory
    /// cannot be read).</summary>
    internal static LocalModelTier? Recommend(LocalHardwareProfile profile)
    {
        foreach (var tier in new[] { LocalModelTier.Best, LocalModelTier.Standard, LocalModelTier.Light })
        {
            if (Fits(tier, profile))
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
            LocalModelTier.Best => "Needs 16 GB of memory and a graphics card with 12 GB.",
            LocalModelTier.Standard => "Needs 32 GB of memory, or 16 GB and a graphics card with 6 GB.",
            _ => "Needs 16 GB of memory.",
        };
    }
}
