using System.Globalization;

namespace VoiceWink.Helpers;

/// <summary>Which of the recognised native-log shapes a line matched.</summary>
public enum GgmlVulkanDeviceLineKind
{
    /// <summary>"ggml_vulkan: Found N Vulkan devices:" or "ggml_vulkan: No devices found." — the
    /// count (0 for the latter) is the one signal the <c>LocalComputeSnapshot</c> residual lacks.</summary>
    DeviceCount,
    /// <summary>One enumerated device row.</summary>
    Device,
    /// <summary>The parakeet-server's own selection line ("… using device: Vulkan0").</summary>
    BackendSelection,
}

/// <summary>
/// TRN-60: the ONE parser over the native GPU-device lines both local engines print — whisper.cpp
/// through Whisper.net's log callback, parakeet-server through its captured stdout/stderr — so the
/// app log can name the device that was actually resolved instead of the loader that was loaded.
///
/// <para><b>Only parsed fields ever reach a log line; a raw native line never does.</b> The
/// whisper model-load lines and the parakeet banner both carry the model PATH (an absolute
/// <c>%LOCALAPPDATA%</c> path embeds the Windows username), so anything this parser does not
/// recognise is dropped by every caller, and the one field that could carry free text — the
/// <see cref="BackendSelection"/> token — is the FIRST whitespace-delimited token only, refused
/// when it looks like a path (Grok plan r1, B2: the server's banner could not be sampled without a
/// model, so the rule has to hold for a line longer than the one quoted on the TRN-51 card).</para>
///
/// <para>The three ggml shapes are the format strings read from the shipped
/// <c>ggml-vulkan-whisper.dll</c> (1.9.1, 2026-09-02):
/// <c>ggml_vulkan: Found %zu Vulkan devices:</c>,
/// <c>ggml_vulkan: %zu = %s (%s) | uma: %d | fp16: %d | …</c>, <c>ggml_vulkan: No devices found.</c>.
/// The driver is the LAST parenthesised group before <c>| uma:</c> because device names carry
/// their own parentheses (<c>Intel(R) Arc(TM) 140V GPU (Intel Corporation)</c>).</para>
/// </summary>
public readonly record struct GgmlVulkanDeviceLine(
    GgmlVulkanDeviceLineKind Kind,
    int Count,
    int Index,
    string Name,
    string Driver,
    bool Uma,
    string Device)
{
    /// <summary>A selection token longer than this is refused — a device id is a short word
    /// (<c>Vulkan0</c>, <c>CPU</c>), a path is not.</summary>
    public const int MaxDeviceTokenLength = 64;

    /// <summary>A device NAME or DRIVER longer than this is refused, and so is one carrying a
    /// path separator. The two share ONE byte-mode pipe on the Parakeet feed, so a mid-line splice
    /// of the ggml device row with the server's model banner could in principle assemble a
    /// well-formed row whose driver field ends in the model path; the same rule the selection
    /// token already has closes that class (self-review, privacy lens).</summary>
    public const int MaxNameLength = 128;

    private const string FoundPrefix = "ggml_vulkan: Found ";
    private const string FoundSuffix = " Vulkan devices:";
    private const string NoDevices = "ggml_vulkan: No devices found.";
    private const string GgmlPrefix = "ggml_vulkan: ";
    private const string UmaMarker = " | uma: ";
    private const string UsingMarker = "using device:";

    /// <summary>
    /// Recognise one native log line. Trailing CR/LF is ignored (whisper.cpp lines end in
    /// <c>\n</c>). Returns false for every line that is not one of the four shapes — including
    /// an over-long or path-shaped selection token — and never throws on any input.
    /// </summary>
    public static bool TryParse(string? line, out GgmlVulkanDeviceLine parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(line)) return false;
        var s = line.TrimEnd('\r', '\n');

        // The prefix and suffix share their space, so a 34-char "ggml_vulkan: Found Vulkan devices:"
        // satisfies both tests with NO digits between them — the length guard is what keeps the
        // slice non-negative (self-review, privacy lens: a parser throw on the Parakeet feed would
        // have ended the drain, which is the hang the pump exists to prevent).
        if (s.Length > FoundPrefix.Length + FoundSuffix.Length
            && s.StartsWith(FoundPrefix, StringComparison.Ordinal) && s.EndsWith(FoundSuffix, StringComparison.Ordinal))
        {
            var digits = s.AsSpan(FoundPrefix.Length, s.Length - FoundPrefix.Length - FoundSuffix.Length);
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                parsed = new GgmlVulkanDeviceLine(GgmlVulkanDeviceLineKind.DeviceCount, count, 0, "", "", false, "");
                return true;
            }
            return false;
        }

        if (string.Equals(s, NoDevices, StringComparison.Ordinal))
        {
            parsed = new GgmlVulkanDeviceLine(GgmlVulkanDeviceLineKind.DeviceCount, 0, 0, "", "", false, "");
            return true;
        }

        if (s.StartsWith(GgmlPrefix, StringComparison.Ordinal))
        {
            return TryParseDeviceRow(s.AsSpan(GgmlPrefix.Length), out parsed);
        }

        var at = s.IndexOf(UsingMarker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return TryParseSelection(s.AsSpan(at + UsingMarker.Length), out parsed);
        }

        return false;
    }

    /// <summary>"&lt;i&gt; = &lt;name&gt; (&lt;driver&gt;) | uma: &lt;u&gt; | …" — everything after the
    /// <c>uma</c> flag is dropped; nothing downstream can use it.</summary>
    private static bool TryParseDeviceRow(ReadOnlySpan<char> rest, out GgmlVulkanDeviceLine parsed)
    {
        parsed = default;
        var eq = rest.IndexOf(" = ", StringComparison.Ordinal);
        if (eq <= 0) return false;
        if (!int.TryParse(rest[..eq], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;

        var afterEq = rest[(eq + 3)..];
        var umaAt = afterEq.IndexOf(UmaMarker, StringComparison.Ordinal);
        if (umaAt <= 0) return false;

        var head = afterEq[..umaAt].TrimEnd();                       // "<name> (<driver>)"
        var umaTail = afterEq[(umaAt + UmaMarker.Length)..];         // "<u> | fp16: …" or "<u>"
        if (umaTail.Length == 0) return false;
        var umaChar = umaTail[0];
        if (umaChar != '0' && umaChar != '1') return false;
        if (umaTail.Length > 1 && umaTail[1] != ' ') return false;

        if (head.Length == 0 || head[^1] != ')') return false;
        // The driver is the parenthesised group that CLOSES at the end of the head — found by
        // balancing from the right, not by the last " (", so a driver string with its own
        // parentheses ("AMD open-source driver (RADV)") stays whole. Device names carry
        // parentheses too ("Intel(R) Arc(TM) 140V GPU"), which is why the group must be preceded
        // by a space: "%s (%s)" always puts one there.
        var depth = 0;
        var open = -1;
        for (var i = head.Length - 1; i >= 0; i--)
        {
            if (head[i] == ')') depth++;
            else if (head[i] == '(' && --depth == 0) { open = i; break; }
        }
        if (open <= 0 || head[open - 1] != ' ') return false;
        var name = head[..open].Trim();
        var driver = head[(open + 1)..^1].Trim();
        if (!IsLoggableField(name) || !IsLoggableField(driver)) return false;

        parsed = new GgmlVulkanDeviceLine(
            GgmlVulkanDeviceLineKind.Device, 0, index, name.ToString(), driver.ToString(), umaChar == '1', "");
        return true;
    }

    /// <summary>A name or driver is loggable when it is non-empty, bounded, and carries no path
    /// separator — the same rule as the selection token, for the same reason.</summary>
    private static bool IsLoggableField(ReadOnlySpan<char> value)
        => value.Length > 0 && value.Length <= MaxNameLength && value.IndexOfAny('\\', '/') < 0;

    /// <summary>The FIRST whitespace-delimited token after the marker, and nothing else — the
    /// remainder of the line is where a model path would be.</summary>
    private static bool TryParseSelection(ReadOnlySpan<char> afterMarker, out GgmlVulkanDeviceLine parsed)
    {
        parsed = default;
        var trimmed = afterMarker.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
        var token = trimmed[..end];
        if (token.Length == 0 || token.Length > MaxDeviceTokenLength) return false;
        if (token.IndexOfAny('\\', '/') >= 0) return false;

        parsed = new GgmlVulkanDeviceLine(GgmlVulkanDeviceLineKind.BackendSelection, 0, 0, "", "", false, token.ToString());
        return true;
    }
}
