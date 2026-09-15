using System.Diagnostics;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Measures hotkey-press → pill-actually-on-screen latency and logs ONE
/// Information line per hotkey-started recording:
/// <c>Hotkey→pill visible: {Total}ms (preShow={PreShow}ms, path={Path})</c>.
///
/// Lifecycle: <see cref="Arm"/> is called by <c>MainViewModel.StartRecordingAsync</c>
/// AFTER the license/update gates pass, with the timestamp the hotkey hook
/// stamped before dispatching the start (consume-once — tray/UI starts never
/// arm). <see cref="MarkPreShow"/> splits out the pre-show cost (gates + focus
/// capture kick-off) right before <c>IsMiniRecorderVisible = true</c>.
/// <see cref="MarkRecreate"/> tags the measurement when a cross-DPI recreate
/// was accepted for this show, because the recreated window then reaches a
/// normal show site and would otherwise mislabel the path.
/// <see cref="MarkVisible"/> fires at each terminal SetWindowPos(SHOWWINDOW)
/// site; the FIRST call after an Arm logs and disarms. An armed measurement
/// expires after <see cref="Expiry"/> so unrelated later shows (error pill,
/// downloads) can never attribute themselves to a stale hotkey press.
///
/// Threading: Arm/MarkPreShow run on the UI thread; MarkVisible runs on the
/// MiniRecorder's UI thread (same thread today). The lock keeps the probe
/// correct regardless — calls are rare (one recording start each).
/// </summary>
internal sealed class MiniRecorderShowLatencyProbe
{
    private static ILogger Logger => Log.ForContext<MiniRecorderShowLatencyProbe>();

    /// <summary>Armed measurements older than this are dropped, not logged.</summary>
    internal static readonly TimeSpan Expiry = TimeSpan.FromSeconds(5);

    /// <summary>A hotkey dispatch timestamp older than this is stale (a start
    /// that never happened — blocked, ignored, or enqueue-dropped) and must not arm.</summary>
    internal static readonly TimeSpan ArmFreshness = TimeSpan.FromSeconds(2);

    public static readonly MiniRecorderShowLatencyProbe Instance =
        new(Stopwatch.GetTimestamp, Stopwatch.Frequency);

    private readonly Func<long> _timestampSource;
    private readonly long _frequency;
    private readonly object _lock = new();

    private bool _armed;
    private long _hotkeyTimestamp;
    private long _armedTimestamp;
    private long _preShowTimestamp;
    private bool _recreateMarked;

    internal MiniRecorderShowLatencyProbe(Func<long> timestampSource, long frequency)
    {
        _timestampSource = timestampSource;
        _frequency = frequency;
    }

    /// <summary>True when <paramref name="hotkeyTimestamp"/> is recent enough to arm.</summary>
    public bool IsFresh(long hotkeyTimestamp)
    {
        var elapsed = TicksToMs(_timestampSource() - hotkeyTimestamp);
        return elapsed >= 0 && elapsed <= ArmFreshness.TotalMilliseconds;
    }

    /// <summary>Arm a new measurement; replaces any stale armed state.</summary>
    public void Arm(long hotkeyTimestamp)
    {
        lock (_lock)
        {
            _armed = true;
            _hotkeyTimestamp = hotkeyTimestamp;
            _armedTimestamp = _timestampSource();
            _preShowTimestamp = 0;
            _recreateMarked = false;
        }
    }

    /// <summary>Record the pre-show split (immediately before the pill flips visible).</summary>
    public void MarkPreShow()
    {
        lock (_lock)
        {
            if (_armed && _preShowTimestamp == 0)
                _preShowTimestamp = _timestampSource();
        }
    }

    /// <summary>Tag the in-flight measurement as recreate-routed. No-op when disarmed.</summary>
    public void MarkRecreate()
    {
        lock (_lock)
        {
            if (_armed)
                _recreateMarked = true;
        }
    }

    /// <summary>
    /// A terminal show site put the pill on screen. Logs and disarms on the first
    /// call after an Arm; returns the logged message (null when disarmed/expired)
    /// so tests can assert without a log sink.
    /// </summary>
    public string? MarkVisible(string site)
    {
        string message;
        lock (_lock)
        {
            if (!_armed)
                return null;
            _armed = false;

            var now = _timestampSource();
            if (TicksToMs(now - _armedTimestamp) > Expiry.TotalMilliseconds)
                return null;

            var totalMs = (int)TicksToMs(now - _hotkeyTimestamp);
            var preShowMs = _preShowTimestamp == 0
                ? -1
                : (int)TicksToMs(_preShowTimestamp - _hotkeyTimestamp);
            var path = _recreateMarked ? $"recreate+{site}" : site;
            message = $"Hotkey→pill visible: {totalMs}ms (preShow={preShowMs}ms, path={path})";
        }

        Logger.Information("{HotkeyToPillVisible}", message);
        return message;
    }

    private double TicksToMs(long ticks) => ticks * 1000.0 / _frequency;
}
