using System.Globalization;
using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>
/// LAI-15: the PC's memory state when a local AI reply stalled or ran out of time — the evidence the
/// card's follow-ups wait for (an automatic restart only where memory was NOT short; a "Low memory" pill
/// only once these lines show a level that separates stalls). Numbers only; every field is null when
/// it could not be read.
/// </summary>
internal sealed record MemoryStateSnapshot(
    long? AvailableMb, long? TotalMb, long? CommitMb, long? CommitLimitMb, double? PagesPerSec,
    long? ChildDedicatedMb, long? ChildSharedMb)
{
    private const long Mb = 1024 * 1024;

    /// <summary>The one line, counts only. <paramref name="what"/> is fixed app text
    /// (<see cref="DescribeStall"/> / "total deadline").</summary>
    internal static string Render(string provider, string what, long elapsedMs, int textChunks, MemoryStateSnapshot s)
        => string.Create(CultureInfo.InvariantCulture,
            $"Local AI {what}: {provider} after {elapsedMs} ms, {textChunks} text chunks; memory {N(s.AvailableMb)} MB available of {N(s.TotalMb)} MB, commit {N(s.CommitMb)}/{N(s.CommitLimitMb)} MB, {Rate(s.PagesPerSec)} pages/s; AI process graphics memory {N(s.ChildDedicatedMb)} MB dedicated, {N(s.ChildSharedMb)} MB shared");

    internal static string DescribeStall(LocalReplyPhase phase, bool watchOnly = false)
        => (watchOnly ? "would have stalled " : "stalled ")
           + (phase == LocalReplyPhase.FirstText ? "before its first text" : "between chunks")
           + (watchOnly ? " (watch only)" : "");

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    private static string Rate(double? value) => value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Bytes from a 64-bit counter to whole MB; null stays null.</summary>
    internal static long? ToMb(ulong? bytes) => bytes is { } b ? (long)(b / Mb) : null;
}

/// <summary>Reads a <see cref="MemoryStateSnapshot"/>; tests fake it.</summary>
internal interface IMemoryStateReader
{
    MemoryStateSnapshot Read(uint? childPid);
}

/// <summary>
/// The production reader: RAM and commit from <c>GlobalMemoryStatusEx</c> (commit used = limit minus
/// available), pages/sec from PDH's <c>\Memory\Pages/sec</c> over a one-second sample, the child's
/// graphics memory from <see cref="GpuMemoryCounters.ProcessUsageOnAllAdapters"/>. Blocks about a
/// second — call it off the paste path. Never throws.
/// </summary>
internal sealed class SystemMemoryStateReader : IMemoryStateReader
{
    internal static readonly SystemMemoryStateReader Instance = new();

    /// <summary>The rate counter's sample: two collections this far apart.</summary>
    internal static readonly TimeSpan PagesSample = TimeSpan.FromSeconds(1);

    public MemoryStateSnapshot Read(uint? childPid)
    {
        long? available = null, total = null, commit = null, limit = null;
        try
        {
            var status = new NativeInterop.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeInterop.MEMORYSTATUSEX>() };
            if (NativeInterop.GlobalMemoryStatusEx(ref status))
            {
                available = MemoryStateSnapshot.ToMb(status.ullAvailPhys);
                total = MemoryStateSnapshot.ToMb(status.ullTotalPhys);
                limit = MemoryStateSnapshot.ToMb(status.ullTotalPageFile);
                commit = MemoryStateSnapshot.ToMb(status.ullTotalPageFile >= status.ullAvailPageFile
                    ? (ulong?)(status.ullTotalPageFile - status.ullAvailPageFile)
                    : null);
            }
        }
        catch
        {
            // n/a
        }

        long? dedicated = null, shared = null;
        if (childPid is { } pid)
        {
            dedicated = MemoryStateSnapshot.ToMb((ulong?)GpuMemoryCounters.ProcessUsageOnAllAdapters(pid, shared: false));
            shared = MemoryStateSnapshot.ToMb((ulong?)GpuMemoryCounters.ProcessUsageOnAllAdapters(pid, shared: true));
        }

        return new MemoryStateSnapshot(available, total, commit, limit, ReadPagesPerSec(), dedicated, shared);
    }

    private static double? ReadPagesPerSec()
    {
        var query = IntPtr.Zero;
        try
        {
            if (NativeInterop.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return null;
            if (NativeInterop.PdhAddEnglishCounterW(query, @"\Memory\Pages/sec", IntPtr.Zero, out var counter) != 0) return null;
            if (NativeInterop.PdhCollectQueryData(query) != 0) return null;
            Thread.Sleep(PagesSample);
            if (NativeInterop.PdhCollectQueryData(query) != 0) return null;
            if (NativeInterop.PdhGetFormattedCounterValue(counter, NativeInterop.PDH_FMT_DOUBLE, IntPtr.Zero, out var value) != 0)
                return null;
            return value.CStatus is NativeInterop.PDH_CSTATUS_VALID_DATA or NativeInterop.PDH_CSTATUS_NEW_DATA
                ? Math.Max(0, value.DoubleValue)
                : null;
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
