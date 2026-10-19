namespace VoiceWink.Helpers;

/// <summary>
/// The processor a prebuilt native engine needs (TRN-79). One row per binary family, each naming
/// where its requirement comes from, so a package bump re-checks it here.
///
/// <para>Why a table: a native engine built for a newer instruction set does not fail with an
/// exception on an older processor — it executes an instruction the CPU lacks and the process dies
/// (<c>0xC000001D</c>). VOICEWINK-J/K/M (2026-10-15) was <c>parakeet-server</c> dying that way while
/// the app checked only SSE2 for it, and local Whisper had no check at all.</para>
/// </summary>
internal enum EngineCpuRequirement
{
    /// <summary>Whisper.net 1.9.1's CPU and Vulkan runtimes (each ships one
    /// <c>ggml-cpu-whisper.dll</c>, no runtime dispatch): AVX, AVX2, FMA, F16C — the package
    /// readme's list. Its own loader checks AVX/AVX2/FMA for the CPU runtime only, not F16C and
    /// not Vulkan. The Silero ggml VAD's requirement is
    /// <see cref="EngineCpuRequirements.WhisperRuntimeRunnable"/>, not this row.</summary>
    GgmlAvx2,

    /// <summary><c>parakeet-server</c> (parakeet.cpp v0.6.1, the default Parakeet engine): built with
    /// <c>GGML_NATIVE=OFF</c>, static, no CPU variants. ggml at its pinned commit then enables SSE4.2,
    /// AVX, AVX2 and BMI2, and MSVC's <c>/arch:AVX2</c> brings FMA and F16C — inferred from the
    /// build configuration, consistent with the field crash.</summary>
    ParakeetServer,

    /// <summary><c>Whisper.net.Runtime.NoAvx</c> 1.9.1 — ggml with AVX/AVX2/FMA/F16C off. SSE4.2 is a
    /// conservative floor: whether that build emits SSE4.x is not established, and Windows 11 24H2
    /// itself requires SSE4.2, so the floor costs no supported machine.</summary>
    WhisperNoAvx,

    /// <summary>sherpa-onnx (the kill-switch Parakeet build): onnxruntime's documented SSE2 floor,
    /// with faster paths chosen at runtime by its own dispatch.</summary>
    X64Baseline,
}

/// <summary>Reads a <see cref="CpuInstructionSets"/> against an <see cref="EngineCpuRequirement"/>.</summary>
internal static class EngineCpuRequirements
{
    /// <summary>The single token a non-x86 process (a native arm64 build — none ships) reports.</summary>
    internal const string NotX86 = "x86-64";

    /// <summary>The extension tokens <paramref name="requirement"/> needs that <paramref name="cpu"/>
    /// lacks, in the order the reason line ranks them; empty means the engine can run.</summary>
    internal static IReadOnlyList<string> Missing(EngineCpuRequirement requirement, CpuInstructionSets cpu)
    {
        if (!cpu.X86) return [NotX86];

        var missing = new List<string>();
        switch (requirement)
        {
            case EngineCpuRequirement.GgmlAvx2:
                AddAvx2Class(missing, cpu);
                break;
            case EngineCpuRequirement.ParakeetServer:
                AddAvx2Class(missing, cpu);
                if (!cpu.Bmi2) missing.Add("bmi2");
                break;
            case EngineCpuRequirement.WhisperNoAvx:
                if (!cpu.Sse42) missing.Add("sse4.2");
                break;
            case EngineCpuRequirement.X64Baseline:
                // SSE2 is architectural on x86-64; X86 above is the whole test.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(requirement), requirement, null);
        }
        return missing;
    }

    internal static bool IsMet(EngineCpuRequirement requirement, CpuInstructionSets cpu)
        => Missing(requirement, cpu).Count == 0;

    /// <summary>Whether any Whisper.net CPU build this app ships can run here — the AVX2-class build,
    /// or the no-AVX one <c>WhisperBackendLog</c> pins without it. The ONE answer for every consumer
    /// of that runtime: the transcription engine and the Silero VAD gate run on the same native load,
    /// so they must never disagree about whether it exists.</summary>
    internal static bool WhisperRuntimeRunnable(CpuInstructionSets cpu)
        => IsMet(EngineCpuRequirement.GgmlAvx2, cpu) || IsMet(EngineCpuRequirement.WhisperNoAvx, cpu);

    /// <summary>The tokens an engine needs, for a log line that states the requirement beside what
    /// is missing.</summary>
    internal static string Describe(EngineCpuRequirement requirement) => requirement switch
    {
        EngineCpuRequirement.GgmlAvx2 => "avx avx2 fma f16c",
        EngineCpuRequirement.ParakeetServer => "avx avx2 fma f16c bmi2",
        EngineCpuRequirement.WhisperNoAvx => "sse4.2",
        EngineCpuRequirement.X64Baseline => "sse2",
        _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement, null),
    };

    /// <summary>The one line a model row shows when this processor cannot run its engine — the first
    /// missing token, which <see cref="Missing"/> orders oldest extension first: "Needs a processor
    /// with AVX2." Empty when nothing is missing.</summary>
    internal static string ReasonLine(IReadOnlyList<string> missing)
    {
        if (missing.Count == 0) return "";
        return missing[0] == NotX86
            ? "Needs an x86-64 processor."
            : $"Needs a processor with {missing[0].ToUpperInvariant()}.";
    }

    private static void AddAvx2Class(List<string> missing, CpuInstructionSets cpu)
    {
        if (!cpu.Avx) missing.Add("avx");
        if (!cpu.Avx2) missing.Add("avx2");
        if (!cpu.Fma) missing.Add("fma");
        if (!cpu.F16c) missing.Add("f16c");
    }
}
