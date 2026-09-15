namespace VoiceWink.Helpers;

/// <summary>Diagnostics returned by <see cref="CaptureRingBuffer.AttachSink"/>.</summary>
/// <param name="DrainedBytes">Converted bytes replayed from the ring into the sink.</param>
/// <param name="DrainedChunks">Chunks the replay touched (including a trimmed boundary chunk).</param>
/// <param name="TrimmedByCapacity">True when the cut predates the oldest retained chunk — audio
/// between the cut and the ring start was already evicted. Should never happen at the configured
/// capacity; the caller logs it so a capacity misjudgment is visible in support bundles.</param>
public readonly record struct SinkAttachResult(long DrainedBytes, int DrainedChunks, bool TrimmedByCapacity);

/// <summary>
/// AUD-6: bounded RAM-only ring of converted capture chunks (16 kHz mono 16-bit PCM), the bridge
/// between the standing warm capture and a recording's WAV writer. Holds the last
/// <see cref="CapacitySeconds"/> seconds of audio so the drain can start at the HOTKEY instant
/// even though the writer attaches a few (or, under an AV stall, a few hundred) milliseconds later.
///
/// <para><b>Append and attach share ONE lock — that is the exactly-once mechanism.</b>
/// <see cref="AttachSink"/> replays the ring from the cut point and flips live delivery inside the
/// same lock the capture thread appends under, so a chunk lands in the replay XOR the live stream,
/// never both, never neither (Kimi plan review B3). The stated trade: the capture thread's
/// <see cref="Append"/> blocks while a replay or a slow sink write is in flight, and the stop-side
/// <see cref="DetachSink"/> can wait on one in-flight chunk write — the same
/// write-on-capture-thread blast radius the cold path has always had, bounded by ring capacity.</para>
///
/// <para><b>Cut-point math — arrival-based, honestly bounded.</b> Chunks are stamped with their
/// arrival timestamp (the same <c>Stopwatch.GetTimestamp</c> clock the hotkey dispatch stamps);
/// a chunk's span is derived from its byte length
/// (<see cref="CaptureFormatConverter.TargetBytesPerMillisecond"/>) ending at the stamp, and the
/// boundary chunk is trimmed to the cut at sample alignment. Delivery latency skews arrival
/// stamps late, so persisted audio TYPICALLY begins up to ~one driver delivery period
/// (10–30 ms) before the cut. The bound is NOT absolute (Codex diff r1): a delivery burst after
/// a scheduler stall stamps old audio "now", so audio queued across such a stall lands after
/// the cut with its true age invisible to this model — the hard ceiling is the ring capacity,
/// and the legal/product wording claims only the typical bound (addendum A11).</para>
///
/// <para>Privacy: RAM-only, continuously overwritten, never persisted except through an attached
/// sink, and <see cref="Clear"/>ed whenever the standing capture stops.</para>
/// </summary>
public sealed class CaptureRingBuffer
{
    public const int CapacitySeconds = 10;

    private readonly object _lock = new();
    private readonly long _ticksPerSecond;
    private readonly int _capacityBytes;
    private readonly Queue<Chunk> _chunks = new();
    private long _totalBytes;
    private long _nextSequence;
    private Action<byte[]>? _sink;

    private readonly record struct Chunk(long Sequence, long EndTimestamp, byte[] Data);

    /// <param name="ticksPerSecond">Timestamp frequency — <c>Stopwatch.Frequency</c> in
    /// production; injected so the cut math is testable with a fake clock.</param>
    /// <param name="capacityBytes">Override for tests; defaults to <see cref="CapacitySeconds"/>
    /// of target-format audio.</param>
    public CaptureRingBuffer(long ticksPerSecond, int? capacityBytes = null)
    {
        _ticksPerSecond = ticksPerSecond;
        _capacityBytes = capacityBytes
            ?? CapacitySeconds * CaptureFormatConverter.TargetBytesPerMillisecond * 1000;
    }

    /// <summary>Monotonic count of appended chunks — diagnostics only.</summary>
    public long AppendedChunkCount { get { lock (_lock) return _nextSequence; } }

    /// <summary>
    /// Append a converted chunk (called on the capture thread). Always enters the ring — constant
    /// memory, no mode branches — and is additionally delivered to the live sink when one is
    /// attached. A throwing sink propagates to the caller (the standing service's handler owns
    /// the catch); the chunk is already in the ring at that point.
    /// </summary>
    public void Append(long timestamp, byte[] chunk)
    {
        if (chunk.Length == 0) return;

        // A single chunk larger than the whole ring would survive eviction (the loop keeps at
        // least one entry), silently breaking the "capacity is the hard ceiling" guarantee the
        // privacy wording stands on (Codex diff r2). Cannot occur with real WASAPI buffer sizes;
        // enforced anyway — keep the NEWEST tail, sample-aligned.
        if (chunk.Length > _capacityBytes)
        {
            var keep = _capacityBytes & ~1;
            chunk = chunk[^keep..];
        }

        lock (_lock)
        {
            _chunks.Enqueue(new Chunk(_nextSequence++, timestamp, chunk));
            _totalBytes += chunk.Length;
            while (_totalBytes > _capacityBytes && _chunks.Count > 1)
            {
                var evicted = _chunks.Dequeue();
                _totalBytes -= evicted.Data.Length;
            }

            _sink?.Invoke(chunk);
        }
    }

    /// <summary>
    /// Atomically replay everything from <paramref name="cutTimestamp"/> onward into
    /// <paramref name="sink"/> and switch to live delivery. Runs entirely under the append lock —
    /// see the type doc for why that is the exactly-once guarantee and what it costs.
    /// Throws when a sink is already attached (claims are serialized upstream; a double attach is
    /// a caller bug, not a state to tolerate).
    /// </summary>
    public SinkAttachResult AttachSink(long cutTimestamp, Action<byte[]> sink)
    {
        lock (_lock)
        {
            if (_sink != null)
                throw new InvalidOperationException("A sink is already attached to the capture ring.");

            long drainedBytes = 0;
            var drainedChunks = 0;
            var trimmedByCapacity = false;
            var first = true;

            foreach (var chunk in _chunks)
            {
                if (chunk.EndTimestamp <= cutTimestamp) { first = false; continue; }

                var data = chunk.Data;
                var skipBytes = 0;
                var startTimestamp = chunk.EndTimestamp - BytesToTicks(data.Length);
                if (startTimestamp < cutTimestamp)
                {
                    // Boundary chunk: trim everything older than the cut, sample-aligned.
                    var skipMs = TicksToMs(cutTimestamp - startTimestamp);
                    skipBytes = (int)Math.Clamp(
                        (long)(skipMs * CaptureFormatConverter.TargetBytesPerMillisecond) & ~1L,
                        0, data.Length);
                }
                else if (first)
                {
                    // The oldest retained chunk starts AFTER the cut: audio between the cut and
                    // the ring start was evicted. Report it; the drain still delivers what exists.
                    trimmedByCapacity = true;
                }
                first = false;

                if (skipBytes < data.Length)
                {
                    var slice = skipBytes == 0 ? data : data[skipBytes..];
                    sink(slice);
                    drainedBytes += slice.Length;
                    drainedChunks++;
                }
            }

            _sink = sink;
            return new SinkAttachResult(drainedBytes, drainedChunks, trimmedByCapacity);
        }
    }

    /// <summary>Stop live delivery. Idempotent. Waits for an in-flight <see cref="Append"/>
    /// delivery to finish (it holds the same lock) — bounded by one chunk write.</summary>
    public void DetachSink()
    {
        lock (_lock) _sink = null;
    }

    /// <summary>Drop all retained audio (standing capture stopped/rebuilt — stale audio from a
    /// previous device must not linger in RAM, and a later cut could never legally reach it
    /// anyway). Keeps the sequence counter monotonic.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
            _totalBytes = 0;
        }
    }

    private long BytesToTicks(int bytes) =>
        (long)((double)bytes / CaptureFormatConverter.TargetBytesPerMillisecond / 1000.0 * _ticksPerSecond);

    private double TicksToMs(long ticks) => ticks * 1000.0 / _ticksPerSecond;
}
