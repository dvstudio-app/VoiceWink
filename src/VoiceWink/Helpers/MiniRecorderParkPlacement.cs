namespace VoiceWink.Helpers;

/// <summary>
/// Pure decision helper for the MiniRecorder's recreate park position: where to
/// park a recreated (hidden) window adjacent to the TARGET monitor. Extracted
/// for unit testability (no Win32 calls — probes are injected).
/// (The idle-repark decision that briefly lived here was removed 2026-07-06:
/// live logs showed off-screen parking never converges XamlRoot's DPI, so the
/// poll recreated the window in a loop.)
/// </summary>
internal static class MiniRecorderParkPlacement
{
    /// <summary>
    /// Choose an off-work-area park position adjacent to the target monitor such
    /// that <c>MonitorFromRect(parkRect, MONITOR_DEFAULTTONEAREST)</c> still
    /// resolves to the TARGET monitor. The previous fixed "above the top edge"
    /// choice resolved to the physically-above monitor on bottom-arranged
    /// externals (observed 2026-07-05: parkY=1270 vs monitorRect.Top=1440 landed
    /// on the 1.25-scale monitor above). Value of the correct side: nearest-
    /// monitor-based placement fallbacks keep pointing at the intended monitor.
    /// (It does NOT pre-initialize the window's DPI — live evidence 2026-07-06
    /// showed Windows keeps a fully off-screen window's DPI unchanged; the
    /// visible Show()'s reconciliation handles scale correction.)
    ///
    /// Candidates are probed below, above, right, then left of the monitor
    /// rect; the first whose nearest monitor is the target wins. If every side
    /// borders another monitor (target fully surrounded — not a real-world
    /// arrangement) fall back to the historical above-the-top position.
    /// </summary>
    /// <param name="monitorFromRect">Probe injected for tests; production passes
    /// a wrapper over <see cref="NativeInterop.MonitorFromRect"/> with
    /// <see cref="NativeInterop.MONITOR_DEFAULTTONEAREST"/>.</param>
    public static (int X, int Y) ChooseParkPosition(
        NativeInterop.RECT targetMonitorRect,
        int windowWidth,
        int windowHeight,
        int gap,
        Func<NativeInterop.RECT, IntPtr> monitorFromRect,
        IntPtr targetMonitor)
    {
        var monitorWidth = targetMonitorRect.Right - targetMonitorRect.Left;
        var monitorHeight = targetMonitorRect.Bottom - targetMonitorRect.Top;
        var centeredX = targetMonitorRect.Left + (monitorWidth - windowWidth) / 2;
        var centeredY = targetMonitorRect.Top + (monitorHeight - windowHeight) / 2;

        Span<(int X, int Y)> candidates =
        [
            (centeredX, targetMonitorRect.Bottom + gap),                    // below
            (centeredX, targetMonitorRect.Top - windowHeight - gap),        // above
            (targetMonitorRect.Right + gap, centeredY),                     // right
            (targetMonitorRect.Left - windowWidth - gap, centeredY),        // left
        ];

        foreach (var (x, y) in candidates)
        {
            var rect = new NativeInterop.RECT
            {
                Left = x,
                Top = y,
                Right = x + windowWidth,
                Bottom = y + windowHeight
            };
            if (monitorFromRect(rect) == targetMonitor)
                return (x, y);
        }

        // Surrounded on all four sides — keep the historical behavior.
        return (centeredX, targetMonitorRect.Top - windowHeight - gap);
    }
}
