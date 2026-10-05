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
/// <item><b>Standard</b> needs ≥ 16 GiB RAM, with or without a GPU (owner, 2026-10-04; it was
/// ≥ 32 GiB without one): on the desktop it measured 3.6 GB working set on the processor and
/// 4.3 GB commit with every layer on the GPU (bench README, "Desktop memory per local model"),
/// so a GPU does not remove the RAM need and 16 GiB holds it beside Parakeet. The laptop's
/// +9.8 GB peak commit, which set the old figure, is unexplained. On the processor it is slow
/// (13–19 s a paragraph on the desktop), which the speed estimate and the first-use check
/// decide, not the floor.</item>
/// <item><b>Light</b> needs ≥ 16 GiB RAM (the 2B beside Parakeet peaked at +3.6 GB).</item>
/// </list>
/// Unknown RAM recommends nothing; unknown VRAM counts as no dedicated GPU.
/// <para><b>A measurement overrides the floors, both ways</b> (owner, 2026-10-01): once a downloaded
/// model's first-use check has timed it on this PC, that time decides — over
/// <see cref="TooSlowMs"/> and the model is never the pick, at or under it and the model is
/// eligible even above its floor. The floors only guess for models not yet measured. The measured
/// case: the floors picked Gemma on the Arc 140V laptop, whose dictations took 11–38 s.</para>
/// </summary>
internal static class LocalModelRecommendation
{
    private const long GiB = 1L << 30;

    internal const long BestMinVram = 10 * GiB;
    internal const long LargeMinVram = 8 * GiB;
    internal const long StandardMinVram = 6 * GiB;
    internal const long GpuTierMinRam = 16 * GiB;
    internal const long StandardMinRam = 16 * GiB;
    internal const long LightMinRam = 16 * GiB;

    /// <summary>The first-use check's timed run — a paragraph-sized cleanup — above which a model is
    /// too slow for dictation on this PC. From the owner's sweep (2026-10-03, three PCs, one
    /// ~80-word paragraph): every model judged too slow took 10.5 s or more, every model judged
    /// fine or borderline 7.2 s or less (Qwen3.5 4B on the ARM64 laptop, "borderline"), so the line
    /// sits just above the borderline case. The 1.5 s line before it was set on a ~25-word timed
    /// run, which could not tell those models apart (Qwen3.5 4B and Gemma 4 measured the same on
    /// the Arc laptop while their paragraphs took 5.0 s and 15.4 s).</summary>
    internal const int TooSlowMs = 8000;

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

    /// <summary>2026-10-03 (owner rule: speed stars must not jump around without the user knowing
    /// why): each model's expected time for the check's paragraph on a PC like this one, from the
    /// owner's three-PC sweep the same day (2B / 4B / 9B / 12B). A model that fits a dedicated
    /// graphics card (<see cref="DesktopMinVram"/>) takes the desktop's time (RTX 3080); otherwise an
    /// ARM64 PC takes the ARM64 laptop's (Snapdragon, on the processor) and any other PC the
    /// integrated-graphics laptop's (Arc 140V). PROVISIONAL like the floors: three PCs, one paragraph.
    /// A measured model replaces its own estimate and calibrates the rest (<see cref="Speeds"/>).</summary>
    internal static int EstimateMs(LocalModelTier tier, LocalHardwareProfile profile, bool arm64)
    {
        if ((profile.LargestDedicatedVramBytes ?? 0) >= DesktopMinVram(tier))
        {
            return tier switch { LocalModelTier.Light => 500, LocalModelTier.Standard => 900, LocalModelTier.Large => 1300, _ => 3400 };
        }
        return arm64
            ? tier switch { LocalModelTier.Light => 2500, LocalModelTier.Standard => 7200, LocalModelTier.Large => 12100, _ => 14800 }
            : tier switch { LocalModelTier.Light => 2200, LocalModelTier.Standard => 5000, LocalModelTier.Large => 10500, _ => 15400 };
    }

    /// <summary>The dedicated graphics memory above which a model's speed is estimated at the
    /// desktop's: the 12B's and 9B's floors, 6 GiB for the 4B and the 2B, whose floors are RAM only.</summary>
    internal static long DesktopMinVram(LocalModelTier tier) => tier switch
    {
        LocalModelTier.Best => BestMinVram,
        LocalModelTier.Large => LargeMinVram,
        _ => StandardMinVram,
    };

    /// <summary>One model's speed on this PC: measured by its first-use check, or estimated and
    /// calibrated by the models this PC has measured.</summary>
    internal readonly record struct TierSpeed(int Ms, bool Measured);

    /// <summary>Every model's speed on this PC. One measurement moves every unmeasured model by the
    /// same factor (<see cref="SpeedCalibration"/>), so a slow PC's first check lowers all the rows
    /// together rather than leaving the bigger models looking faster than they are.</summary>
    internal static IReadOnlyDictionary<LocalModelTier, TierSpeed> Speeds(LocalHardwareProfile profile, bool arm64,
        Func<LocalModelTier, int?> measuredMs)
    {
        var tiers = Enum.GetValues<LocalModelTier>();
        var factor = SpeedCalibration.Factor(tiers
            .Where(t => measuredMs(t) is not null)
            .Select(t => (measuredMs(t)!.Value, EstimateMs(t, profile, arm64))));
        return tiers.ToDictionary(t => t, t => measuredMs(t) is { } ms
            ? new TierSpeed(ms, Measured: true)
            : new TierSpeed(SpeedCalibration.Apply(EstimateMs(t, profile, arm64), factor), Measured: false));
    }

    /// <summary>The recommended tier, or null when this PC is below every floor (or its memory
    /// cannot be read).</summary>
    internal static LocalModelTier? Recommend(LocalHardwareProfile profile) => Recommend(profile, _ => null);

    /// <summary>The largest tier that is fast enough on this PC — measured where it was, estimated and
    /// calibrated where not, in which case it must also meet its floor; null when none is.
    /// <paramref name="measuredMs"/> gives a tier's measured check time, or null.</summary>
    internal static LocalModelTier? Recommend(LocalHardwareProfile profile, Func<LocalModelTier, int?> measuredMs,
        bool arm64 = false)
    {
        var speeds = Speeds(profile, arm64, measuredMs);
        foreach (var tier in new[] { LocalModelTier.Best, LocalModelTier.Large, LocalModelTier.Standard, LocalModelTier.Light })
        {
            var speed = speeds[tier];
            if (!IsTooSlow(speed.Ms) && (speed.Measured || Fits(tier, profile)))
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
            LocalModelTier.Standard => ram >= StandardMinRam,
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
            // The graphics card leads on every row that needs one.
            LocalModelTier.Best => "Needs a 10 GB graphics card and 16 GB of memory.",
            LocalModelTier.Large => "Needs an 8 GB graphics card and 16 GB of memory.",
            _ => "Needs 16 GB of memory.",
        };
    }

    /// <summary>The order the model rows are shown in: highest Accuracy first, the larger model first
    /// on a tie (owner, 2026-10-05: always the same order; "Best for this PC" marks its row and no
    /// longer moves it). Display only — <see cref="LocalModelCatalog.All"/> keeps the default first,
    /// which the "first installed model" fallback relies on.</summary>
    internal static IReadOnlyList<LocalModelEntry> DisplayOrder()
        => LocalModelCatalog.All
            .OrderByDescending(e => e.Accuracy)
            .ThenByDescending(e => e.Tier)
            .ToList();

    /// <summary>What setup's AI step offers (owner, 2026-10-05: one model, as the transcription step
    /// offers one): the pick for this PC, or the catalog default (<see cref="LocalModelCatalog.Standard"/>)
    /// when nothing can be picked, plus the model already in use when that is another one.</summary>
    internal static IReadOnlyList<LocalModelEntry> SetupOffer(LocalModelTier? recommended, Func<string, bool> isSelected)
    {
        var pick = recommended ?? LocalModelCatalog.Standard.Tier;
        return DisplayOrder().Where(e => e.Tier == pick || isSelected(e.Id)).ToList();
    }
}
