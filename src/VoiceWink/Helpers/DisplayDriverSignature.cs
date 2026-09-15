namespace VoiceWink.Helpers;

/// <summary>
/// TRN-63: the display driver's identity, as one string, so <see cref="GpuWarmupMarker"/> can key
/// the GPU self-test verdicts on it beside the app version. A driver update is the one event that
/// can turn a FAIL into a PASS without an app update (the TRN-62 Adreno case is a driver bug that
/// only Qualcomm can fix), and until this existed a fixed driver stayed on the CPU pin until the
/// next release or a toggle re-arm.
///
/// <para><b>Source: the registry, not WMI.</b> Every display adapter has a slot under the display
/// class (<see cref="DisplayClassKey"/>, four-digit subkeys <c>0000</c>, <c>0001</c>, …) whose
/// <c>DriverDesc</c> and <c>DriverVersion</c> values are what Device Manager shows. Reading them
/// is a millisecond of registry access with no service, no P/Invoke and no COM — the TRN-49 marker
/// doc had recorded "reading it takes WMI", which was the reason the version did not ride the key;
/// the TRN-62 experiment scripts read exactly these values with <c>reg query</c>.</para>
///
/// <para><b>Shape: fail-soft, bounded, order-free.</b> <see cref="Read"/> returns null on any
/// failure or when no slot carries a version, and null means "nothing known" to the marker — it
/// never discards a persisted verdict. <see cref="Build"/> is the pure part: one
/// <c>description=version</c> entry per adapter that has a version, sorted ordinally BEFORE the
/// entry cap so slot numbering and enumeration order cannot make the same machine read as a
/// change, every value flattened to one loggable line (the separators this format uses, path
/// separators, and control characters are replaced — the same bounds the subsystem's other
/// native-sourced names get, so the one log line that names the signature needs no further
/// sanitising), the entry count bounded, and a joined string over <see cref="MaxSignatureLength"/>
/// replaced by its SHA-256 (bounded AND collision-free — a truncation could make two different
/// driver sets compare equal, i.e. a missed driver change, which is the direction that keeps a
/// stale CPU pin). Registry values are read with environment-variable expansion OFF, so a
/// <c>REG_EXPAND_SZ</c> description can never pull a user-profile path into the signature.</para>
///
/// <para><b>Accepted residual:</b> a launch that lands inside a driver install can see a slot
/// with a description and no version yet; that adapter contributes nothing for that launch, so
/// one driver update can read as two changes (absent, then present with the new version) and
/// cost two re-warms instead of one. Self-limiting, and rarer than the update itself.</para>
/// </summary>
internal static class DisplayDriverSignature
{
    /// <summary>The display adapter class GUID's key under HKLM — the same slots
    /// <c>reg query</c> reads and Device Manager's "Driver" tab shows.</summary>
    internal const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>More adapters than any real machine has; the bound is on the marker file, not the machine.</summary>
    internal const int MaxAdapters = 16;

    /// <summary>Per-field bound (a description or a version); a longer value is truncated, not dropped.</summary>
    internal const int MaxFieldLength = 128;

    /// <summary>Bound on the whole signature: a longer joined string is replaced by its SHA-256
    /// hex (64 chars), never truncated (see the type remarks).</summary>
    internal const int MaxSignatureLength = 1024;

    /// <summary>The live signature, or null when the registry cannot be read or no adapter slot
    /// carries a version. A slot that cannot be opened is skipped, never fatal — one unreadable
    /// adapter must not blind the marker to the others.</summary>
    internal static string? Read()
    {
        try
        {
            using var cls = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return null;

            var adapters = new List<(string? Description, string? Version)>();
            foreach (var name in cls.GetSubKeyNames())
            {
                if (!IsAdapterSlot(name)) continue;
                try
                {
                    using var slot = cls.OpenSubKey(name);
                    if (slot is null) continue;
                    // Expansion OFF: a REG_EXPAND_SZ value must contribute its literal text, never
                    // an expanded %USERPROFILE% (self-review, privacy lens). Non-string kinds read
                    // as null and the slot contributes nothing (or "unnamed" for the description).
                    const global::Microsoft.Win32.RegistryValueOptions noExpand =
                        global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames;
                    // Software-enumerated adapters (virtual displays, dock/RDP indirect displays)
                    // are not what Vulkan computes on, and some come and go with a dock or a
                    // session — counting them would read a dock as a driver change (self-review,
                    // concurrency lens). Hardware is NOT filtered to PCI: the ARM SoC GPU this
                    // feature exists for is ACPI-enumerated.
                    if (IsSoftwareDevice(slot.GetValue("MatchingDeviceId", null, noExpand) as string)) continue;
                    adapters.Add((
                        slot.GetValue("DriverDesc", null, noExpand) as string,
                        slot.GetValue("DriverVersion", null, noExpand) as string));
                }
                catch
                {
                    // An access-denied or vanished slot: skip it, keep the rest.
                }
            }
            return Build(adapters);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The display class also holds non-adapter subkeys (<c>Properties</c>, …); an
    /// adapter slot is exactly four ASCII digits.</summary>
    internal static bool IsAdapterSlot(string name)
        => name.Length == 4 && name.All(char.IsAsciiDigit);

    /// <summary>A slot whose matching device id is software-enumerated (<c>ROOT\…</c> virtual
    /// adapters, <c>SWD\…</c> software devices such as indirect-display drivers). Unknown or
    /// missing ids count as hardware: excluding is the exception, and a real GPU with an odd id
    /// must never be dropped from the signature.</summary>
    internal static bool IsSoftwareDevice(string? matchingDeviceId)
        => matchingDeviceId is not null
           && (matchingDeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
               || matchingDeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pure: the signature over adapter (description, version) pairs. Adapters without a
    /// version contribute nothing (a description alone cannot tell a driver update from none);
    /// a missing description reads as <c>unnamed</c>. Null when nothing contributes.</summary>
    internal static string? Build(IEnumerable<(string? Description, string? Version)> adapters)
    {
        var entries = new List<string>();
        foreach (var (description, version) in adapters)
        {
            var cleanVersion = Clean(version);
            if (cleanVersion is null) continue;
            entries.Add((Clean(description) ?? "unnamed") + "=" + cleanVersion);
        }
        if (entries.Count == 0) return null;

        // Sort BEFORE the cap: which entries survive must not depend on enumeration order.
        entries.Sort(StringComparer.Ordinal);
        if (entries.Count > MaxAdapters) entries.RemoveRange(MaxAdapters, entries.Count - MaxAdapters);

        var joined = string.Join(";", entries);
        if (joined.Length <= MaxSignatureLength) return joined;

        // Over the bound: a digest, not a prefix — two driver sets that share a long prefix must
        // still read as different (a collision here is a missed driver change).
        var digest = global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(digest);
    }

    /// <summary>Trimmed, one loggable line, bounded: the format's separators (<c>;</c>, <c>=</c>)
    /// become <c>,</c> / <c>:</c> so a value can never split or forge an entry; path separators
    /// become <c>|</c> (the bound <see cref="GpuWarmupMarker.ValidName"/> and the device-line
    /// parser enforce on every other native-sourced name); every control character becomes a
    /// space (a bare CR is a line break to a line-by-line log scrubber). Null for blank input.
    /// These are replacements, not escaping: nothing parses the signature back apart — it is
    /// only compared and logged.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var chars = new char[Math.Min(trimmed.Length, MaxFieldLength)];
        for (var i = 0; i < chars.Length; i++)
        {
            var c = trimmed[i];
            chars[i] = c switch
            {
                ';' => ',',
                '=' => ':',
                '\\' or '/' => '|',
                // Controls (C0/C1, incl. CR/LF/tab) and the Unicode line/paragraph separators.
                _ when char.IsControl(c) || c == '\u2028' || c == '\u2029' => ' ',
                _ => c,
            };
        }
        return new string(chars);
    }
}
