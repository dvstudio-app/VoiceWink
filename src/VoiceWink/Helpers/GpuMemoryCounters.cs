using System.Globalization;
using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>One graphics adapter as the DirectX registry records it: its LUID and how much
/// dedicated memory it has.</summary>
internal readonly record struct GpuAdapterMemory(long Luid, long DedicatedBytes);

/// <summary>What the built-in AI engine reads to tell whether another app is holding the graphics
/// card. Production is <see cref="GpuMemoryCounters"/>; tests inject a fake. Every member returns
/// null when it cannot answer, and null always means "behave as before" — never "busy".</summary>
internal interface IGpuMemoryProbe
{
    /// <summary>The adapter whose DirectX description equals <paramref name="deviceName"/> (the name
    /// llama-server printed for its device); null unless exactly one adapter matches.</summary>
    GpuAdapterMemory? FindAdapter(string deviceName);

    /// <summary>Dedicated memory in use on the adapter by every process.</summary>
    long? AdapterDedicatedUsage(long luid);

    /// <summary>Dedicated memory one process holds on the adapter.</summary>
    long? ProcessDedicatedUsage(uint pid, long luid);
}

/// <summary>
/// Is the graphics card busy enough that a timing taken on it would be wrong? The rules, pure.
/// <para>Built from one measurement (owner's desktop, RTX 3080 10 GB, ComfyUI generating a video,
/// 2026-10-05): llama.cpp's own free-memory figure said 9,467 MiB free while the card had 2,989;
/// it loaded all 49 layers of a model projected at 7,128 MiB; Windows' counters then showed the
/// child holding 4,333 MiB of dedicated memory (0.61 of its projection) with the rest spilled to
/// shared memory, and a short cleanup took 10.3 s instead of under 1 s. Windows' counters were
/// right, so both rules read them.</para>
/// <para>Neither rule applies to an adapter whose dedicated memory is smaller than the projection:
/// an integrated adapter uses shared memory by design, so "it spilled" would always be true there.</para>
/// </summary>
internal static class GpuMemoryVerdict
{
    private const long MiB = 1024 * 1024;

    /// <summary>A child holding less than this share of its projection in dedicated memory has
    /// spilled. Provisional: the one spilled child measured held 0.61; re-derive from logged pairs.</summary>
    internal const double SpillShare = 0.9;

    /// <summary>Before the weights load: busy when the memory the other processes leave free is
    /// under half of what the child needs. Deliberately far out — the OS can move idle allocations
    /// of other apps out of the way, so "not quite enough free" is not yet "will spill"; the check
    /// after the load (<see cref="Spilled"/>) is the one that decides the marginal case. This one
    /// only stops a load that clearly cannot fit (the measured case had 2,989 MiB free against
    /// 7,128 needed), before it pushes the other app's work out of graphics memory.</summary>
    internal static bool ClearlyTooFull(long adapterDedicatedBytes, long adapterUsedBytes, long childUsedBytes, int projectedMiB)
    {
        if (!Applies(adapterDedicatedBytes, projectedMiB) || adapterUsedBytes < 0 || childUsedBytes < 0)
            return false;
        var others = Math.Max(0, adapterUsedBytes - childUsedBytes);
        var free = adapterDedicatedBytes - others;
        return free * 2 < projectedMiB * MiB;
    }

    /// <summary>After the load: did the child get its memory? Spilled when it holds less than
    /// <see cref="SpillShare"/> of its projection in dedicated memory.</summary>
    internal static bool Spilled(long adapterDedicatedBytes, long childDedicatedBytes, int projectedMiB)
        => Applies(adapterDedicatedBytes, projectedMiB) && childDedicatedBytes >= 0
           && childDedicatedBytes < SpillShare * projectedMiB * MiB;

    private static bool Applies(long adapterDedicatedBytes, int projectedMiB)
        => projectedMiB > 0 && adapterDedicatedBytes >= projectedMiB * MiB;
}

/// <summary>
/// The production <see cref="IGpuMemoryProbe"/>: the adapter from <c>HKLM\SOFTWARE\Microsoft\DirectX</c>
/// (the key <see cref="LocalHardwareProfile"/> reads; its <c>AdapterLuid</c> is the LUID Windows'
/// counters name their instances by), usage from the <c>GPU Adapter Memory</c> and
/// <c>GPU Process Memory</c> counters through PDH. Every failure reads as null.
/// </summary>
internal sealed class GpuMemoryCounters : IGpuMemoryProbe
{
    private const string DirectXKey = @"SOFTWARE\Microsoft\DirectX";
    private const int SoftwareDeviceFlag = 0x4;

    /// <summary>A counter read that takes longer than this answers null (behave as before). Measured
    /// on the desktop: ~0.5 s for the first PDH use in a process, 0–2 ms after it.</summary>
    internal static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(2);

    internal static readonly GpuMemoryCounters Instance = new();

    public GpuAdapterMemory? FindAdapter(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;
        try
        {
            using var root = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DirectXKey);
            if (root is null) return null;
            // An adapter removed from the PC keeps its subkey, with a LUID from an earlier boot that
            // can name a different live adapter now; the live ones carry this boot's LastSeen.
            var lastSeen = root.GetValue("LastSeen") as long?;
            var rows = new List<(string? Description, object? Luid, object? Dedicated, object? AdapterType)>();
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var adapter = root.OpenSubKey(name);
                    if (adapter is null) continue;
                    if (lastSeen is { } boot && adapter.GetValue("LastSeen") is long seen && seen != boot) continue;
                    rows.Add((adapter.GetValue("Description") as string, adapter.GetValue("AdapterLuid"),
                        adapter.GetValue("DedicatedVideoMemory"), adapter.GetValue("AdapterType")));
                }
                catch
                {
                    // An unreadable adapter: skip it, keep the rest.
                }
            }
            return MatchAdapter(rows, deviceName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The one hardware adapter whose description equals <paramref name="deviceName"/>
    /// (ordinal, case-insensitive), or null when none or several do. Pure.</summary>
    internal static GpuAdapterMemory? MatchAdapter(
        IEnumerable<(string? Description, object? Luid, object? Dedicated, object? AdapterType)> rows, string deviceName)
    {
        GpuAdapterMemory? found = null;
        foreach (var (description, luid, dedicated, adapterType) in rows)
        {
            if (!string.Equals(description?.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (adapterType is int flags && (flags & SoftwareDeviceFlag) != 0) continue;
            if (luid is not long l || dedicated is not long bytes || bytes <= 0) continue;
            if (found is not null) return null; // ambiguous: two adapters of one model
            found = new GpuAdapterMemory(l, bytes);
        }
        return found;
    }

    public long? AdapterDedicatedUsage(long luid)
        => Bounded(() => SumInstances(@"\GPU Adapter Memory(*)\Dedicated Usage", InstancePrefix(null, luid)));

    public long? ProcessDedicatedUsage(uint pid, long luid)
        => Bounded(() => SumInstances(@"\GPU Process Memory(*)\Dedicated Usage", InstancePrefix(pid, luid)));

    /// <summary>The read on the pool, waited for at most <see cref="ReadBudget"/>: the callers hold
    /// the engine's slot or its process gate. A read that overruns keeps running and frees its own
    /// handles when it ends; its answer is dropped.</summary>
    private static long? Bounded(Func<long?> read)
    {
        var task = Task.Run(read);
        try
        {
            return task.Wait(ReadBudget) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>The counter instance name's start for an adapter (<c>luid_0x00000000_0x0000f2ab_phys_</c>)
    /// or a process on it (<c>pid_6304_luid_…_phys_</c>), the shape Windows names them in. Pure.</summary>
    internal static string InstancePrefix(uint? pid, long luid)
    {
        var high = (uint)((ulong)luid >> 32);
        var low = (uint)((ulong)luid & 0xFFFFFFFF);
        var adapter = string.Create(CultureInfo.InvariantCulture, $"luid_0x{high:x8}_0x{low:x8}_phys_");
        return pid is { } p ? string.Create(CultureInfo.InvariantCulture, $"pid_{p}_{adapter}") : adapter;
    }

    /// <summary>The sum of every instance of <paramref name="counterPath"/> whose name starts with
    /// <paramref name="prefix"/>; null when PDH fails or no instance matches. One collection: these
    /// are instantaneous byte counts, not rates.</summary>
    private static long? SumInstances(string counterPath, string prefix)
    {
        var query = IntPtr.Zero;
        try
        {
            if (NativeInterop.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return null;
            if (NativeInterop.PdhAddEnglishCounterW(query, counterPath, IntPtr.Zero, out var counter) != 0) return null;
            if (NativeInterop.PdhCollectQueryData(query) != 0) return null;

            uint size = 0;
            var status = NativeInterop.PdhGetFormattedCounterArrayW(counter, NativeInterop.PDH_FMT_LARGE, ref size, out _, IntPtr.Zero);
            if (status != NativeInterop.PDH_MORE_DATA || size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = NativeInterop.PdhGetFormattedCounterArrayW(counter, NativeInterop.PDH_FMT_LARGE, ref size, out var count, buffer);
                if (status != 0) return null;
                var itemSize = Marshal.SizeOf<NativeInterop.PDH_FMT_COUNTERVALUE_ITEM_LARGE>();
                long? sum = null;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NativeInterop.PDH_FMT_COUNTERVALUE_ITEM_LARGE>(buffer + i * itemSize);
                    var name = Marshal.PtrToStringUni(item.szName);
                    if (name is null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.FmtValue.CStatus is not (NativeInterop.PDH_CSTATUS_VALID_DATA or NativeInterop.PDH_CSTATUS_NEW_DATA)) continue;
                    sum = (sum ?? 0) + Math.Max(0, item.FmtValue.LargeValue);
                }
                return sum;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (query != IntPtr.Zero)
                _ = NativeInterop.PdhCloseQuery(query);
        }
    }
}
