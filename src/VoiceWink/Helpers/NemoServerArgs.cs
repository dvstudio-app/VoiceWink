namespace VoiceWink.Helpers;

/// <summary>Where a NeMo-Speech.cpp <c>serve</c> child computes: NeMo's own first device, the
/// CPU, or one Vulkan adapter by index.</summary>
public enum NemoServerDevice { Auto, Cpu, Vulkan }

/// <summary>
/// TRN-81 S1: the ONE argv for <c>nemo-speech serve</c> (plan §3.3,
/// <c>docs/plans/2026-10-18-trn81-nemo-speech-switch/10-plan.md</c>). Linked into
/// <c>tools/parakeet-long-audio</c> so the ship gate (G2) spawns exactly what the app would — the
/// <c>ParakeetServerPolicy.ServerDecodeThreads</c> precedent. Nothing in the app calls it yet.
///
/// <para>The device moves into argv (<c>--device</c>) where pcpp read <c>PARAKEET_DEVICE</c>.
/// The port is the caller's: v0.2.0 refuses <c>--port 0</c> (plan F1). The model is always a
/// PATH, so <c>serve</c> never pulls from the network. <paramref name="batching"/> false adds
/// <c>--asr.batching.enabled=false</c>, the one ship-config key G2/G5 decide between; warm-up
/// stays NeMo's default (on).</para>
/// </summary>
public static class NemoServerArgs
{
    public static string Build(string ggufPath, int port, NemoServerDevice device, int vulkanIndex, bool batching)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || ggufPath.Contains('"'))
            throw new ArgumentException("model path must be non-empty and contain no quote", nameof(ggufPath));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "nemo-speech refuses ports outside 1-65535");
        if (device == NemoServerDevice.Vulkan && vulkanIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(vulkanIndex), vulkanIndex, "Vulkan index must be >= 0");

        var deviceArg = device switch
        {
            NemoServerDevice.Auto => "auto",
            NemoServerDevice.Cpu => "cpu",
            NemoServerDevice.Vulkan => $"vulkan:{vulkanIndex}",
            _ => throw new ArgumentOutOfRangeException(nameof(device), device, null),
        };

        var args = $"serve --asr-model \"{ggufPath}\" --host 127.0.0.1 --port {port} --no-ui --device {deviceArg}";
        return batching ? args : args + " --asr.batching.enabled=false";
    }
}
