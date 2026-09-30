using System.Globalization;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>Which recognised llama-server log shape a line matched.</summary>
public enum LlamaServerLogLineKind
{
    /// <summary><c>llama_prepare_model_devices: using device Vulkan0 (NAME) (PCI) - N MiB free</c> —
    /// the device llama.cpp put the model on. Printed at <c>-lv 4</c> only; absent on a CPU-only
    /// run (<c>--device none</c>), which is why its ABSENCE never counts as GPU evidence.</summary>
    DeviceSelected,
    /// <summary><c>load_tensors: offloaded X/Y layers to GPU</c>.</summary>
    LayersOffloaded,
    /// <summary><c>init: chat template, thinking = 0|1</c> — the server's own statement of whether
    /// the template will think. LAI-2 runs with thinking OFF on the command line; a 1 here is a
    /// contract failure the manager refuses.</summary>
    Thinking,
}

/// <summary>
/// LAI-2: the ONE parser over llama-server's captured stdout/stderr (b11147, <c>-lv 4</c>). The
/// <see cref="Helpers.GgmlVulkanDeviceLine"/> precedent, for the other engine: only parsed fields
/// ever reach a log line — the server's output carries the model PATH (<c>loading model '…'</c>),
/// the whole chat template, and at higher verbosities request text — so anything unrecognised is
/// dropped, and the two free-text fields (the device token and the device name) are bounded and
/// refused when they look like a path. Line shapes measured on the b11147 binaries (desktop,
/// RTX 3080, 2026-09-29): each recognised line carries the log prefix <c>0.00.502.290 I </c> first,
/// so matching is by the INNER marker, never by a line start.
/// </summary>
public readonly record struct LlamaServerLogLine(
    LlamaServerLogLineKind Kind,
    string DeviceToken,
    string DeviceName,
    int LayersOnGpu,
    int LayersTotal,
    bool ThinkingOn)
{
    /// <summary>A device token longer than this is refused — a device id is a short word
    /// (<c>Vulkan0</c>), a path is not.</summary>
    public const int MaxDeviceTokenLength = 32;

    /// <summary>A device name longer than this, or carrying a path separator, is refused (the
    /// <see cref="Helpers.GgmlVulkanDeviceLine.MaxNameLength"/> rule: one byte-mode pipe carries the
    /// model banner too, so a spliced line must not be able to assemble a loggable path).</summary>
    public const int MaxNameLength = 128;

    private const string UsingDevice = "llama_prepare_model_devices: using device ";
    private const string Offloaded = "load_tensors: offloaded ";
    private const string OffloadedSuffix = " layers to GPU";
    private const string Thinking = "init: chat template, thinking = ";

    public static bool TryParse(string? line, out LlamaServerLogLine parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var at = line.IndexOf(UsingDevice, StringComparison.Ordinal);
        if (at >= 0)
        {
            // "Vulkan0 (NVIDIA GeForce RTX 3080) (0000:01:00.0) - 9235 MiB free": the token is the
            // first word; the name is the FIRST parenthesised group after it, taken up to the
            // matching close so a name carrying its own parentheses (Intel(R) Arc(TM)) survives.
            var rest = line[(at + UsingDevice.Length)..];
            var space = rest.IndexOf(' ');
            var token = space < 0 ? rest : rest[..space];
            if (!IsDeviceToken(token))
            {
                return false;
            }
            var name = space < 0 ? "" : ReadBalancedGroup(rest[(space + 1)..]);
            if (name is null || !IsSafeName(name))
            {
                return false;
            }
            parsed = new LlamaServerLogLine(LlamaServerLogLineKind.DeviceSelected, token, name, 0, 0, false);
            return true;
        }

        at = line.IndexOf(Offloaded, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = line[(at + Offloaded.Length)..];
            var end = rest.IndexOf(OffloadedSuffix, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }
            var pair = rest[..end].Split('/');
            if (pair.Length != 2
                || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var onGpu)
                || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                || total <= 0 || onGpu > total)
            {
                return false;
            }
            parsed = new LlamaServerLogLine(LlamaServerLogLineKind.LayersOffloaded, "", "", onGpu, total, false);
            return true;
        }

        at = line.IndexOf(Thinking, StringComparison.Ordinal);
        if (at >= 0)
        {
            var value = line[(at + Thinking.Length)..].Trim();
            if (value is not ("0" or "1"))
            {
                return false;
            }
            parsed = new LlamaServerLogLine(LlamaServerLogLineKind.Thinking, "", "", 0, 0, value == "1");
            return true;
        }

        return false;
    }

    /// <summary>Does this device token name a GPU? llama.cpp prints the <c>using device</c> line
    /// for the devices it offloads to, named by their backend — <c>Vulkan0</c> on the x64 payload,
    /// <c>GPUOpenCL</c> on the ARM64 one — so every token is GPU evidence EXCEPT a <c>CPU</c> one.
    /// A rule by exclusion on purpose (LAI-10): a list of known backend names would silently
    /// read a new backend as "no GPU" and skip the first-use check that exists to catch a GPU
    /// producing wrong text. The wrong direction here only tests a CPU child, which passes.</summary>
    internal static bool IsGpuDevice(string? token)
        => !string.IsNullOrEmpty(token) && !token.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>The token must be a short run of letters and digits — <c>Vulkan0</c>, <c>CPU</c>.
    /// Anything else (a path, a quoted string, an empty token) is refused.</summary>
    private static bool IsDeviceToken(string token)
    {
        if (token.Length == 0 || token.Length > MaxDeviceTokenLength)
        {
            return false;
        }
        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeName(string name)
        => name.Length > 0 && name.Length <= MaxNameLength
           && name.IndexOfAny(['\\', '/', '\'', '"']) < 0
           && !name.Any(char.IsControl);

    /// <summary>The text inside the parenthesised group <paramref name="s"/> starts with, honouring
    /// nested parentheses; null when it does not start with one or never closes.</summary>
    private static string? ReadBalancedGroup(string s)
    {
        if (s.Length == 0 || s[0] != '(')
        {
            return null;
        }
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return s[1..i];
                }
            }
        }
        return null;
    }
}
