using System.Diagnostics;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Measures hotkey-press → audio-actually-flowing latency and logs ONE Information line
/// per hotkey-started recording:
/// <c>Hotkey→capture live: {Total}ms (preflight={P}ms, device={D}ms, startReturned={S}ms)</c>.
///
/// <para><b>Why this exists alongside <see cref="MiniRecorderShowLatencyProbe"/>.</b> That probe
/// measures hotkey → pill window shown, which is already fast (4–144 ms measured 2026-08-02).
/// The user-visible complaint ("the recorder takes seconds to appear") is the SECOND half —
/// the pill sits in Starting while capture spins up, measured at 162–4002 ms across the same
/// five recordings. Nothing measured that half, so this probe was added to aim the follow-up
/// work at whichever phase actually dominates.</para>
///
/// <para><b>Every milestone is ABSOLUTE from the hotkey press, never a phase duration.</b> The
/// pre-flight overlaps device resolution now (the hoisted resolve), so additive per-phase
/// numbers would not sum to the total and would read as though time went missing.</para>
///
/// <para><b>No expiry drop.</b> <see cref="MiniRecorderShowLatencyProbe"/> returns null and logs
/// NOTHING past its 5 s expiry — which silently deletes exactly the slow starts this probe
/// exists to catch. Here a slow start is the signal, so measurements log at any duration up to
/// <see cref="SanityBound"/> (a runaway guard, not a filter).</para>
///
/// <para><b>Attempt-scoped by token, not a bare singleton</b> (both plan reviewers, 2026-08-02).
/// <c>MainViewModel.StartRecordingAsync</c> has five post-arm supersession returns plus the
/// post-start supersede/cancel paths: start A can arm, start B can replace it, and A's
/// superseded return would then disarm or mark B's live measurement. <see cref="Arm"/> hands
/// back a token and every subsequent call is identity-checked against the armed one, so a
/// superseded attempt's late marks are inert.</para>
///
/// <para><b>Threading.</b> Arm/MarkPreflightDone/MarkDeviceResolved run on the UI thread;
/// MarkStartReturned runs on whatever thread the recorder returned on; MarkCaptureLive runs on
/// NAudio's CAPTURE thread. The lock covers all of them, and the terminal log is dispatched off
/// the capture thread — Serilog's file sink is synchronous, and blocking the capture thread on
/// file I/O would drop audio buffers (Codex final check).</para>
/// </summary>
internal sealed class RecordingStartLatencyProbe
{
    private static ILogger Logger => Log.ForContext<RecordingStartLatencyProbe>();

    /// <summary>Runaway guard. A measurement older than this is dropped — a start that took
    /// minutes is a wedged machine, not a latency data point.</summary>
    internal static readonly TimeSpan SanityBound = TimeSpan.FromSeconds(120);

    /// <summary>A hotkey dispatch timestamp older than this is stale (a start that never
    /// happened — blocked, ignored, or enqueue-dropped) and must not arm. Mirrors
    /// <see cref="MiniRecorderShowLatencyProbe.ArmFreshness"/>.</summary>
    internal static readonly TimeSpan ArmFreshness = TimeSpan.FromSeconds(2);

    /// <summary>Token value meaning "not armed" — returned by <see cref="Arm"/> when the
    /// timestamp is stale. Every Mark/Disarm no-ops on it.</summary>
    internal const long NoToken = 0;

    public static readonly RecordingStartLatencyProbe Instance =
        new(Stopwatch.GetTimestamp, Stopwatch.Frequency);

    private readonly Func<long> _timestampSource;
    private readonly long _frequency;
    private readonly Action<string> _sink;
    private readonly object _lock = new();

    private long _nextToken;
    private long _armedToken = NoToken;
    private long _hotkeyTimestamp;
    private long _preflightDone;
    private long _deviceResolved;
    private long _startReturned;
    private long _recordingLive;

    internal RecordingStartLatencyProbe(Func<long> timestampSource, long frequency, Action<string>? sink = null)
    {
        _timestampSource = timestampSource;
        _frequency = frequency;
        // Default sink hops off the calling thread: MarkCaptureLive runs on the capture thread.
        _sink = sink ?? (message => Task.Run(() => Logger.Information("{HotkeyToCaptureLive}", message)));
    }

    /// <summary>True when <paramref name="hotkeyTimestamp"/> is recent enough to arm.</summary>
    public bool IsFresh(long hotkeyTimestamp)
    {
        var elapsed = TicksToMs(_timestampSource() - hotkeyTimestamp);
        return elapsed >= 0 && elapsed <= ArmFreshness.TotalMilliseconds;
    }

    /// <summary>
    /// Arm a new measurement, replacing any armed state. Returns the token that every later
    /// call must present, or <see cref="NoToken"/> when the timestamp is too stale to arm
    /// (the caller then holds a token that makes all its own Mark/Disarm calls inert).
    /// </summary>
    public long Arm(long hotkeyTimestamp)
    {
        lock (_lock)
        {
            if (!IsFresh(hotkeyTimestamp))
                return NoToken;

            _armedToken = ++_nextToken;
            _hotkeyTimestamp = hotkeyTimestamp;
            _preflightDone = 0;
            _deviceResolved = 0;
            _startReturned = 0;
            _recordingLive = 0;
            return _armedToken;
        }
    }

    /// <summary>Pre-flight (App Mode detect, language/model resolution, path) is done.</summary>
    public void MarkPreflightDone(long token) => StampIfArmed(token, ref _preflightDone);

    /// <summary>A capture device has been chosen — hoisted or freshly resolved.</summary>
    public void MarkDeviceResolved(long token) => StampIfArmed(token, ref _deviceResolved);

    /// <summary>
    /// The recorder's start call returned. Deliberately NOT "capture live":
    /// <c>WasapiCapture.StartRecording()</c> spawns a thread and returns; <c>audioClient.Start()</c>
    /// runs on that thread afterwards, so the return only proves the start was requested.
    /// </summary>
    public void MarkStartReturned(long token) => StampIfArmed(token, ref _startReturned);

    /// <summary>
    /// AUD-6 warm path: the state flipped to Recording — the user-experienced readiness moment.
    /// Audio has been flowing into the standing ring since before the hotkey, so this is honest;
    /// the terminal mark (<see cref="MarkWarmWriterAttached"/>) follows when the WAV drain is on.
    /// </summary>
    public void MarkRecordingLive(long token) => StampIfArmed(token, ref _recordingLive);

    /// <summary>
    /// AUD-6 warm terminal: the WAV writer attached and the ring drained — durability reached.
    /// Logs ONE line carrying both numbers the warm path has (the flip the user felt, the attach
    /// the file needed) and disarms. Returns the message (null when disarmed/stale/out-of-bounds)
    /// so tests can assert without a sink.
    /// </summary>
    public string? MarkWarmWriterAttached(long token)
    {
        string message;
        lock (_lock)
        {
            if (token == NoToken || token != _armedToken)
                return null;
            _armedToken = NoToken;

            var now = _timestampSource();
            var attachedMs = TicksToMs(now - _hotkeyTimestamp);
            if (attachedMs < 0 || attachedMs > SanityBound.TotalMilliseconds)
                return null;

            message =
                $"Hotkey→recording live: {Absolute(_recordingLive)}ms " +
                $"(writerAttached={(int)attachedMs}ms, path=warm)";
        }

        _sink(message);
        return message;
    }

    /// <summary>
    /// First audio buffer actually arrived — the earliest provable "the microphone is live"
    /// moment, and the one the user experiences. Terminal: logs and disarms on the first call
    /// after an <see cref="Arm"/>. Returns the composed message (null when disarmed, stale, or
    /// past <see cref="SanityBound"/>) so tests can assert without a log sink.
    ///
    /// <para>May legitimately arrive BEFORE <see cref="MarkStartReturned"/> — the capture thread
    /// can deliver a buffer before the recorder's own continuation runs — in which case
    /// startReturned logs as -1 rather than corrupting the measurement.</para>
    /// </summary>
    public string? MarkCaptureLive(long token)
    {
        string message;
        lock (_lock)
        {
            if (token == NoToken || token != _armedToken)
                return null;
            _armedToken = NoToken;

            var now = _timestampSource();
            var totalMs = TicksToMs(now - _hotkeyTimestamp);
            if (totalMs < 0 || totalMs > SanityBound.TotalMilliseconds)
                return null;

            message =
                $"Hotkey→capture live: {(int)totalMs}ms " +
                $"(preflight={Absolute(_preflightDone)}ms, " +
                $"device={Absolute(_deviceResolved)}ms, " +
                $"startReturned={Absolute(_startReturned)}ms, path=cold)";
        }

        _sink(message);
        return message;
    }

    /// <summary>
    /// Abandon the armed measurement (start failed, cancelled, or superseded). No-op unless
    /// <paramref name="token"/> is the armed one — a superseded attempt must never disarm the
    /// attempt that replaced it.
    /// </summary>
    public void Disarm(long token)
    {
        lock (_lock)
        {
            if (token != NoToken && token == _armedToken)
                _armedToken = NoToken;
        }
    }

    /// <summary>True when <paramref name="token"/> is the currently armed measurement.</summary>
    internal bool IsArmed(long token)
    {
        lock (_lock) return token != NoToken && token == _armedToken;
    }

    private void StampIfArmed(long token, ref long slot)
    {
        lock (_lock)
        {
            if (token == NoToken || token != _armedToken || slot != 0)
                return;
            slot = _timestampSource();
        }
    }

    /// <summary>Milestone as ms since the hotkey press; -1 when never stamped.</summary>
    private int Absolute(long stamp) =>
        stamp == 0 ? -1 : (int)TicksToMs(stamp - _hotkeyTimestamp);

    private double TicksToMs(long ticks) => ticks * 1000.0 / _frequency;
}
