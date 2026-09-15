namespace VoiceWink.Helpers;

/// <summary>
/// HKY-2: foreground sample taken on the UI thread at the moment the hotkey-hook
/// watchdog decides to restart. <see cref="Pid"/> 0 = no foreground window / capture
/// failed; <see cref="SampleUtc"/> is the watchdog tick's clock so the asynchronously
/// emitted log line can state WHEN the sample was taken, not when it was logged.
/// </summary>
internal readonly record struct WatchdogForegroundSample(uint Pid, DateTime SampleUtc);

/// <summary>
/// HKY-2: pure capture for the watchdog's foreground-context line. Only the two
/// sub-millisecond user32 calls run here (foreground HWND → PID) — the elevation
/// probe and the slow process-name lookup live in
/// <see cref="Services.Input.WatchdogForegroundObserver"/>, OFF the UI thread, so
/// the watchdog's recovery sequencing is never delayed by observability.
/// Natives are injected for testability; any failure degrades to Pid 0.
/// </summary>
internal static class WatchdogForegroundProbe
{
    internal static WatchdogForegroundSample Capture(
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr, uint> getWindowPid,
        DateTime nowUtc)
    {
        try
        {
            var hwnd = getForegroundWindow();
            if (hwnd == IntPtr.Zero) return new WatchdogForegroundSample(0, nowUtc);
            return new WatchdogForegroundSample(getWindowPid(hwnd), nowUtc);
        }
        catch (global::System.Exception)
        {
            return new WatchdogForegroundSample(0, nowUtc);
        }
    }

    /// <summary>The log line's elevation vocabulary — a null probe result is "unknown",
    /// never a guess (an inconclusive probe must not read as "not elevated").</summary>
    internal static string DescribeElevation(bool? elevated) => elevated switch
    {
        true => "true",
        false => "false",
        null => "unknown",
    };
}
