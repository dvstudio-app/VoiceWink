namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-4: is "On this PC" offered at all? The ONE predicate behind every provider list and the
/// models card.
///
/// <para><b>Every architecture, ARM64 PCs included, and no architecture or hardware-name rule
/// for the GPU either.</b> On an ARM64 PC whose processor can run it the engine is the NATIVE
/// ARM64 build (<see cref="LlamaServerPayload.Current"/>, LAI-10; an older ARM64 processor keeps
/// the emulated x64 one - <see cref="LlamaArm64Cpu"/>), started as a child of this x64 app; it
/// carries a CPU backend and an OpenCL GPU backend, and which one a PC uses is decided per PC by
/// the first-use GPU check (<see cref="OnThisPcEngine"/>), exactly as on x64: a GPU that returns
/// wrong text fails it once and that PC runs on the CPU, until a driver update re-arms the check.
/// Measured 2026-09-30 on ONE Snapdragon X Elite laptop (Adreno X1-85), the 4B model,
/// <c>tools/llama-server-probe</c>: every check held in both modes - the GPU check passed 4 of 4
/// and a short cleanup took about 2.2 s on the GPU and 1.6-2.3 s on the CPU. The first version of
/// LAI-4 hid the provider on ARM64 on an unmeasured "too slow" assumption, and a second pinned
/// every ARM64 PC to the CPU; the owner rejected both as wider than the evidence.</para>
/// </summary>
internal static class OnThisPcAvailability
{
    internal static bool IsOffered => LocalEngineFeature.IsEnabled;
}
