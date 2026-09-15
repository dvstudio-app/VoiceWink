namespace VoiceWink.Helpers;

/// <summary>
/// TRN-50: the ONE predicate that turns a parakeet-server child's own backend-selection line into
/// "this child computes on the GPU". Every GPU verdict, CPU request and safety-net decision on the
/// Parakeet side goes through here — never through the launch mode, which is a REQUEST:
/// <c>ParakeetLaunchMode.Auto</c> lets the child pick, and a loader-present machine with no
/// working device serves CPU under Auto (the documented residual on <c>ParakeetServerProcess.LaunchMode</c>).
/// Judging an Auto-served CPU decode as a GPU failure would persist a false "GPU failed" verdict
/// and pin the very device the machine was already on (Codex plan round, Blocker 2).
///
/// <para><b>Positive evidence only.</b> The token is the first word after <c>using device:</c>,
/// as <see cref="GgmlVulkanDeviceLine.TryParse"/> extracts it (<c>Vulkan0</c>, <c>Vulkan1</c>,
/// <c>CPU</c>). ggml names its Vulkan devices <c>Vulkan&lt;index&gt;</c>; anything else — a CPU
/// token, an empty or missing line, a child that never printed one — is NOT GPU, which is the
/// direction that records nothing rather than the direction that records a wrong verdict.</para>
/// </summary>
internal static class ParakeetGpuEvidence
{
    private const string VulkanPrefix = "Vulkan";

    /// <summary>True only for a token that names a Vulkan device. No trimming: the parser hands
    /// over a clean token, and a token that needs trimming is not the parser's.</summary>
    internal static bool IsGpu(string? backendToken)
        => backendToken is not null
           && backendToken.Length >= VulkanPrefix.Length
           && backendToken.StartsWith(VulkanPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The device INDEX a Vulkan token names (<c>Vulkan0</c> → 0), so the child can name
    /// the adapter it actually selected rather than the first one enumerated (Codex revised-plan
    /// advisory: a multi-adapter box must not persist the wrong GPU name). False for anything that
    /// is not a GPU token or carries no plain decimal suffix, with <paramref name="index"/> −1 —
    /// never the 0 that <c>int.TryParse</c> writes on failure, which would name adapter row 0.</summary>
    internal static bool TryDeviceIndex(string? backendToken, out int index)
    {
        index = -1;
        if (!IsGpu(backendToken)) return false;
        var suffix = backendToken!.AsSpan(VulkanPrefix.Length);
        if (suffix.Length is > 0 and <= 4
            && int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            index = parsed;
            return true;
        }
        return false;
    }
}
