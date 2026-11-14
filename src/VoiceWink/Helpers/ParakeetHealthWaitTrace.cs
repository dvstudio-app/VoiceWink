using System.Globalization;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>TRN-95: a parakeet-server child's output timing — line counts, line kinds and ages only, never the text.</summary>
public readonly record struct ParakeetChildOutputTrace(
    int Lines,
    TimeSpan? SinceLastLine,
    GgmlVulkanDeviceLineKind? LastDeviceLine,
    TimeSpan? SinceLastDeviceLine)
{
    /// <summary>Builds the trace from raw counters; a tick newer than <paramref name="now"/> reads as zero age, never negative.</summary>
    public static ParakeetChildOutputTrace From(int lines, long lastLineTick, int deviceKind, long lastDeviceTick, long now)
        => new(
            lines,
            lines == 0 ? null : Age(now, lastLineTick),
            deviceKind < 0 ? null : (GgmlVulkanDeviceLineKind)deviceKind,
            deviceKind < 0 ? null : Age(now, lastDeviceTick));

    private static TimeSpan Age(long now, long tick) => TimeSpan.FromMilliseconds(Math.Max(0, now - tick));
}

/// <summary>TRN-95: how the most recent throwaway child (warm-up, CPU re-decode) ended, and how long before this spawn.</summary>
public readonly record struct ParakeetThrowawayExit(string Label, bool Confirmed, TimeSpan BeforeSpawn);

/// <summary>TRN-95: what a resident child that never became healthy was doing when its health wait ended.</summary>
public sealed record ParakeetHealthWaitTrace(
    int SpawnNumber,
    TimeSpan? PortSeenAfter,
    int ProbesSent,
    bool ProbeInFlight,
    ParakeetChildOutputTrace? Output,
    ParakeetThrowawayExit? PriorThrowawayExit)
{
    /// <summary><c>spawn #2, port after 1.2s, probes 87 (one in flight), output 14 lines (last 27.9s ago), last device line BackendSelection 27.9s ago, prior throwaway: parakeet GPU warm-up exit confirmed 3.4s before spawn</c>.</summary>
    public string Render()
    {
        var b = new StringBuilder();
        b.Append("spawn #").Append(SpawnNumber.ToString(CultureInfo.InvariantCulture));
        b.Append(PortSeenAfter is { } port ? ", port after " + Seconds(port) : ", port never seen");
        b.Append(", probes ").Append(ProbesSent.ToString(CultureInfo.InvariantCulture));
        if (ProbeInFlight) b.Append(" (one in flight)");
        if (Output is { } o)
        {
            b.Append(", output ").Append(o.Lines.ToString(CultureInfo.InvariantCulture)).Append(" lines");
            if (o.SinceLastLine is { } line) b.Append(" (last ").Append(Seconds(line)).Append(" ago)");
            b.Append(o.LastDeviceLine is { } kind && o.SinceLastDeviceLine is { } since
                ? $", last device line {kind} {Seconds(since)} ago"
                : ", no device line");
        }
        else
        {
            b.Append(", output not observed");
        }
        b.Append(PriorThrowawayExit is { } prior
            ? $", prior throwaway: {LogValueSanitizer.SingleLine(prior.Label)} exit {(prior.Confirmed ? "confirmed" : "unconfirmed")} {Seconds(prior.BeforeSpawn)} before spawn"
            : ", no prior throwaway child");
        return b.ToString();
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
}
