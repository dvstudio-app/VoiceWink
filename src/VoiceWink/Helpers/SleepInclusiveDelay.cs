using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceWink.Helpers;

/// <summary>UPD-16: a delay that counts time the PC sleeps — <c>Task.Delay</c>'s timer clock stops during sleep.</summary>
internal static class SleepInclusiveDelay
{
    /// <summary>Longest single timer wait; bounds how late an elapsed delay is noticed after the PC wakes.</summary>
    internal static readonly TimeSpan MaxStep = TimeSpan.FromHours(1);

    public static Task WaitAsync(TimeSpan duration, CancellationToken ct)
        => WaitAsync(duration, static () => Environment.TickCount64, static (d, c) => Task.Delay(d, c), ct);

    // GetTickCount64 counts sleep and ignores clock changes; the timer queue's unbiased time counts neither.
    internal static async Task WaitAsync(
        TimeSpan duration,
        Func<long> tickCount64,
        Func<TimeSpan, CancellationToken, Task> step,
        CancellationToken ct)
    {
        var started = tickCount64();
        while (true)
        {
            var left = duration - TimeSpan.FromMilliseconds(tickCount64() - started);
            if (left <= TimeSpan.Zero) return;
            await step(left < MaxStep ? left : MaxStep, ct).ConfigureAwait(false);
        }
    }
}
