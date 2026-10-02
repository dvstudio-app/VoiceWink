using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-3: the two facts the local-model recommendation needs — total physical memory, and the
/// largest DEDICATED graphics memory of any hardware display adapter. Null means unknown, never a
/// guess: the recommendation treats unknown VRAM as "no dedicated GPU" and unknown RAM as "cannot
/// size", the directions that never recommend a model the PC cannot hold.
/// <para>VRAM comes first from the adapters Windows' graphics stack records under
/// <c>HKLM\SOFTWARE\Microsoft\DirectX</c>: <c>DedicatedVideoMemory</c> is the figure Task Manager
/// shows as "Dedicated GPU memory", and <c>AdapterType</c> flags software and integrated adapters,
/// which are skipped. The value is a little under the card's label (a 10 GB RTX 3080 records 9.82 GiB),
/// so it is rounded to the nearest GiB before the floors compare it. Only when that key yields
/// nothing does the display-class slot <see cref="DisplayDriverSignature"/> walks answer
/// (<c>HardwareInformation.qwMemorySize</c>, else <c>MemorySize</c>) — the driver's own figure,
/// which an integrated GPU can fill with SHARED memory: the Arc 140V laptop read as a GPU with
/// 10 GB or more there, and was offered Gemma (2026-10-01). No WMI, no DXGI.</para>
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

    internal static long? ReadLargestDedicatedVram() => ReadDirectXDedicatedVram() ?? ReadDisplayClassVram();

    private const string DirectXKey = @"SOFTWARE\Microsoft\DirectX";

    // D3DKMT_ADAPTERTYPE bits (d3dkmthk.h): a software rasterizer, or the integrated half of a hybrid pair.
    private const int SoftwareDeviceFlag = 0x4;
    private const int HybridIntegratedFlag = 0x20;

    private static long? ReadDirectXDedicatedVram()
    {
        try
        {
            using var root = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DirectXKey);
            if (root is null) return null;
            var adapters = new List<(object? AdapterType, object? Dedicated)>();
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var adapter = root.OpenSubKey(name);
                    if (adapter?.GetValue("DedicatedVideoMemory") is not { } dedicated) continue;
                    adapters.Add((adapter.GetValue("AdapterType"), dedicated));
                }
                catch
                {
                    // An unreadable adapter: skip it, keep the rest.
                }
            }
            return LargestDiscreteVram(adapters);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The largest dedicated memory among hardware, non-integrated adapters, rounded to the
    /// nearest GiB; 0 when every adapter is software or integrated or holds no dedicated memory;
    /// null when no adapter carries a readable value (the caller then asks the display class). Pure.</summary>
    internal static long? LargestDiscreteVram(IEnumerable<(object? AdapterType, object? Dedicated)> adapters)
    {
        const long GiB = 1L << 30;
        long? largest = null;
        foreach (var (adapterType, dedicated) in adapters)
        {
            if (dedicated is not long bytes) continue;
            largest ??= 0;
            if (adapterType is int flags && (flags & (SoftwareDeviceFlag | HybridIntegratedFlag)) != 0) continue;
            var rounded = (long)Math.Round(bytes / (double)GiB, MidpointRounding.AwayFromZero) * GiB;
            if (rounded > largest) largest = rounded;
        }
        return largest;
    }

    private static long? ReadDisplayClassVram()
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
