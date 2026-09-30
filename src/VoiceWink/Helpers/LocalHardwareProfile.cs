using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-3: the two facts the local-model recommendation needs — total physical memory, and the
/// largest DEDICATED graphics memory of any hardware display adapter. Null means unknown, never a
/// guess: the recommendation treats unknown VRAM as "no dedicated GPU" and unknown RAM as "cannot
/// size", the directions that never recommend a model the PC cannot hold.
/// <para>VRAM comes from the display-class registry slots <see cref="DisplayDriverSignature"/>
/// already walks (same slot filter, software devices skipped): the driver-written
/// <c>HardwareInformation.qwMemorySize</c> (QWORD), else <c>HardwareInformation.MemorySize</c>
/// (DWORD, or 4/8 bytes of binary on older drivers). An integrated GPU reports its small
/// dedicated carve-out there (≤ 2 GB), below every GPU threshold by design. No WMI, no DXGI.</para>
/// </summary>
internal sealed record LocalHardwareProfile(long? TotalRamBytes, long? LargestDedicatedVramBytes)
{
    internal static LocalHardwareProfile Read() => new(ReadTotalRam(), ReadLargestDedicatedVram());

    /// <summary>Installed RAM — the firmware's round figure, so a "16 GB" PC meets a 16 GiB floor
    /// (the usable amount Windows reports is a few hundred MB lower, and a floor on it would exclude
    /// every 16 GB PC). Falls back to the usable amount when the firmware table cannot be read.</summary>
    internal static long? ReadTotalRam()
    {
        try
        {
            if (NativeInterop.GetPhysicallyInstalledSystemMemory(out var kib) && kib > 0)
            {
                return (long)Math.Min(kib, long.MaxValue / 1024) * 1024;
            }
        }
        catch
        {
            // fall through to the usable amount
        }
        try
        {
            var status = new NativeInterop.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>() };
            return NativeInterop.GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0
                ? (long)Math.Min(status.ullTotalPhys, long.MaxValue)
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static long? ReadLargestDedicatedVram()
    {
        try
        {
            using var cls = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayDriverSignature.DisplayClassKey);
            if (cls is null) return null;
            var values = new List<object?>();
            foreach (var name in cls.GetSubKeyNames())
            {
                if (!DisplayDriverSignature.IsAdapterSlot(name)) continue;
                try
                {
                    using var slot = cls.OpenSubKey(name);
                    if (slot is null) continue;
                    if (DisplayDriverSignature.IsSoftwareDevice(slot.GetValue("MatchingDeviceId") as string)) continue;
                    values.Add(slot.GetValue("HardwareInformation.qwMemorySize") ?? slot.GetValue("HardwareInformation.MemorySize"));
                }
                catch
                {
                    // An access-denied or vanished slot: skip it, keep the rest.
                }
            }
            return LargestMemorySize(values);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The largest positive memory size among the raw registry values, or null when none
    /// reads as one. Pure; the case table lives in <c>LocalHardwareProfileTests</c>.</summary>
    internal static long? LargestMemorySize(IEnumerable<object?> rawValues)
    {
        long? largest = null;
        foreach (var raw in rawValues)
        {
            if (ParseMemorySize(raw) is { } bytes && (largest is null || bytes > largest))
            {
                largest = bytes;
            }
        }
        return largest;
    }

    /// <summary>A registry memory-size value as bytes: QWORD (long), DWORD (int, read unsigned),
    /// or 4/8-byte little-endian binary. Anything else, zero or negative is null.</summary>
    internal static long? ParseMemorySize(object? raw)
    {
        long value = raw switch
        {
            long q => q,
            int d => (uint)d,
            byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
            byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
            _ => 0,
        };
        return value > 0 ? value : null;
    }
}
