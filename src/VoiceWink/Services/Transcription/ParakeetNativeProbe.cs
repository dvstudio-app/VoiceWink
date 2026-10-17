using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Can this machine run the Parakeet engine this build ships (TRN-1 step 3; TRN-79)?
///
/// <para><b>TRN-79 (2026-10-17): the default engine is parakeet-server, and it needs the AVX2
/// class.</b> This probe was written for sherpa-onnx and checked SSE2 only; after the TRN-29 flip
/// made parakeet.cpp the default, a PC without AVX2 passed it, downloaded the model and had the
/// child die with <c>0xC000001D</c> in GPU mode and again in the CPU retry (VOICEWINK-J/K/M). The
/// requirement per binary now lives in <see cref="Helpers.EngineCpuRequirement"/>; the paragraphs
/// below describe the sherpa floor, which still governs a <c>-p:PcppEnabled=false</c> build.</para>
///
/// <para><b>Why a probe rather than a try/catch around the first use.</b> A missing CPU feature in
/// native code is not an exception — it is an illegal instruction, which terminates the process.
/// REL-15 is this project's own instance of that class (a vcomp140/OpenMP access violation on user
/// machines that no dev machine reproduced), and the response was to check BEFORE loading.</para>
///
/// <para><b>The feature set is deliberately NOT copied from the VAD's probe.</b> That one checks
/// AVX/AVX2/FMA/F16C because Whisper.net documents those for its prebuilt binaries. onnxruntime is a
/// different binary from a different vendor: Microsoft's published minimum for the default x64 CPU
/// build is SSE2, with AVX paths selected at RUNTIME through its own dispatch. Asserting AVX2 here
/// would refuse machines that work; asserting nothing would risk the crash the probe exists to stop.
/// So this checks the documented floor and lets onnxruntime's own dispatcher answer "how fast".</para>
///
/// <para><b>What this does NOT do: confirm the library loads.</b> An earlier version of this comment
/// claimed it did, and a diff reviewer caught that nothing here touches the DLL. A quarantined or
/// missing <c>onnxruntime.dll</c> (antivirus false positives on it are a known class) passes every
/// check below. That case is handled where it actually surfaces — <c>ParakeetLocalRuntime</c> catches
/// <see cref="DllNotFoundException"/>/<see cref="BadImageFormatException"/> from the load and reports
/// <c>PrepareOutcome.Unavailable</c>, the same answer this probe would give. It is not folded in here
/// because a load attempt is not a cheap cached property, and because a managed exception is
/// catchable while the illegal instruction this probe guards against is not.</para>
///
/// <para><b>The CPU check is not redundant with the bitness check.</b> SSE2 is architecturally
/// mandatory on x86-64, so on an x64 CPU it is indeed always true — but <c>Is64BitProcess</c> is also
/// true for a NATIVE arm64 process on Windows, where <c>X86Base.IsSupported</c> is false. That is the
/// case it catches.</para>
///
/// <para><b>That case is unreachable in every build that ships, and saying so here is the point:</b>
/// paraphrases of this paragraph elsewhere shortened "a native arm64 process" to "ARM64" and were
/// then read as "an ARM64 machine", which is the opposite of the truth. This project is win-x64 only
/// (<c>&lt;Platforms&gt;x64&lt;/Platforms&gt;</c>; a native arm64 target is backlog TCH-2, unbuilt),
/// so on Windows-on-ARM the x64 build runs under Prism emulation, which supplies x86-64 semantics
/// including SSE2 — this probe returns true and Parakeet runs. Owner-verified 2026-08-20 on a
/// Snapdragon ARM64 PC. Nothing here tests the architecture; it tests the instruction set the
/// process actually has.</para>
///
/// <para>Evaluated once and cached: it cannot change within a process.</para>
/// </summary>
internal static class ParakeetNativeProbe
{
    private static ILogger Logger => Log.ForContext(typeof(ParakeetNativeProbe));

    private static readonly Lazy<IReadOnlyList<string>> MissingLazy = new(EvaluateCurrent, isThreadSafe: true);

    /// <summary>True when the engine can be loaded on this machine.</summary>
    internal static bool IsSupported => MissingLazy.Value.Count == 0;

    /// <summary>What this processor lacks for the engine this build ships — empty when supported. The
    /// Models page's reason line reads it.</summary>
    internal static IReadOnlyList<string> MissingExtensions => MissingLazy.Value;

    private static IReadOnlyList<string> EvaluateCurrent()
        => Evaluate(CpuInstructionSets.Current, Environment.Is64BitProcess, PcppFeature.IsEnabled);

    /// <summary>
    /// The pure decision. <paramref name="pcppEnabled"/> picks the binary: the parakeet.cpp server
    /// in every ordinary build (TRN-79: AVX2-class + BMI2 — this probe checked only SSE2 for it until
    /// 2026-10-17, which is how a PC without AVX2 was offered Parakeet and its child died with an
    /// illegal instruction), sherpa-onnx's SSE2 floor in a kill-switch build.
    /// </summary>
    internal static IReadOnlyList<string> Evaluate(CpuInstructionSets cpu, bool is64BitProcess, bool pcppEnabled)
    {
        // The runtime package ships win-x64 natives only. A 32-bit or ARM process cannot load them,
        // and finding that out via a BadImageFormatException mid-recording is worse than knowing now.
        if (!is64BitProcess)
        {
            Logger.Information("Parakeet unavailable: not a 64-bit process");
            return [EngineCpuRequirements.NotX86];
        }

        var requirement = pcppEnabled ? EngineCpuRequirement.ParakeetServer : EngineCpuRequirement.X64Baseline;
        var missing = EngineCpuRequirements.Missing(requirement, cpu);
        if (missing.Count > 0)
        {
            // Below this the binary is not merely slow, it is unrunnable.
            Logger.Information(
                "Parakeet unavailable: processor lacks {Missing} (the engine needs {Required})",
                string.Join(' ', missing), EngineCpuRequirements.Describe(requirement));
        }
        return missing;
    }
}
