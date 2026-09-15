using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-14 (2026-08-19): the post-stop capture grace — keep the microphone hot for up to
/// <see cref="MaxGraceMs"/> after the user's stop gesture, then trim the recording back to the
/// last speech. The measured gap it closes: a stop gesture lands while the final word is still
/// leaving the microphone (or still crossing a Bluetooth link's buffering), and today's stop cuts
/// the WAV at gesture time — the tail TRN-15 then has to rescue TEXT for is often simply not in
/// the file. This buys the audio itself, upstream of every decode-side repair.
///
/// <para><b>The no-regression floor is a PER-RECORDING SNAPSHOT, not a constant</b> (Kimi plan
/// round, B1): the trim can never cut BELOW the writer's bytes-written at grace entry — that IS
/// today's cut point on both capture paths, whatever the endpoint's delivery cadence, so "never
/// shorter than today" holds per recording by construction. A silent tail trims back to exactly
/// the snapshot, and the recorder's grace gate makes the snapshot exact — a concurrent chunk is
/// either counted in it or fed to the policy, never lost between the two.</para>
///
/// <para><b>Early exit is a SILENCE RUN, capped by a TIMER the caller owns</b> (Kimi plan round,
/// B2 shape): the grace ends at the first of <see cref="SilenceRunMs"/> of continuous sub-floor
/// audio after grace entry, or the caller's <see cref="MaxGraceMs"/> timer — never chunk-counting,
/// because a mid-grace capture death delivers no chunks and a chunk-counted grace would hang the
/// stop path. <see cref="Feed"/> is allocation-free and runs on the capture thread; the frame
/// convention (20 ms, fixed −55 dBFS floor) is TailRescue's, compared in the energy domain so no
/// log runs per frame.</para>
///
/// <para><b>The trim rewrites nothing</b> — <see cref="TrimFileFailSoft"/> parses the closed WAV's
/// own chunk layout (never assumes a 44-byte header), truncates the stream, and patches the two
/// size fields. Fail-soft: a trim failure keeps the full-length file, which is the safe direction
/// (the grace tail is real audio; the transcription pipeline handles trailing silence today).</para>
/// </summary>
internal sealed class PostStopGrace
{
    private static ILogger Logger => Log.ForContext<PostStopGrace>();

    /// <summary>Continuous sub-floor audio that ends the grace early — a clean stop pays this,
    /// not the full cap. Corpus-validation of the value is the card's dry-run item.</summary>
    internal const int SilenceRunMs = 300;

    /// <summary>The dwell cap, enforced by the CALLER's timer (never chunk arrival — a dead
    /// capture delivers no chunks). The stop path holds its lock this much longer, worst case.</summary>
    internal const int MaxGraceMs = 1000;

    /// <summary>Kept beyond the last active frame: covers the delivery blur around the grace-entry
    /// snapshot and a soft word ending's sub-floor decay.</summary>
    internal const int PadMs = 150;

    /// <summary>TailRescue's frame convention — one calibrated "is this audio active" grid.</summary>
    internal const int FrameMs = 20;

    /// <summary>TailRescue's pinned activity floor, reused deliberately (the ZeroRunClassifier
    /// precedent): one calibrated floor, not a second constant to drift.</summary>
    internal const double ActivityFloorDbfs = TailRescue.ActivityFloorDbfs;

    // The converted stream this feeds on is the recorder's canonical 16 kHz mono 16-bit PCM.
    private const int SampleRate = 16000;
    private const int FrameSamples = SampleRate * FrameMs / 1000;   // 320
    internal const int BytesPerMs = SampleRate * 2 / 1000;          // 32

    // Frame "active" test in the ENERGY domain: mean square over the frame (int16 domain) vs the
    // floor's mean square. rmsDbfs = 10·log10(meanSquare/32768²) ⇒ threshold = 10^(floor/10)·32768².
    private static readonly double ActivityFloorMeanSquare =
        global::System.Math.Pow(10, ActivityFloorDbfs / 10.0) * 32768.0 * 32768.0;

    // Capture-thread state: exactly one writer (the capture callback). The recorder serializes
    // write+feed against snapshot+publish under its grace gate (Codex verification round), so a
    // chunk is atomically either IN the snapshot or FED here — the former snapshot-to-publish
    // blur is structurally gone. The trim-side read happens only after teardown quiesces
    // delivery; on the NOT-quiesced exits (stop error / timeout) the recorder skips the trim
    // entirely, so a straggler Feed can never race a read that matters.
    private long _frameSumSquares;
    private int _framePartialSamples;
    private int _fedMs;
    private int _silentRunMs;
    private int _lastActiveEndMs;

    /// <summary>Milliseconds of whole frames consumed so far.</summary>
    internal int FedMs => _fedMs;

    /// <summary>The tail to KEEP past the grace-entry snapshot: nothing when the whole tail was
    /// sub-floor, otherwise up to the last active frame's end plus <see cref="PadMs"/>, never more
    /// than was actually fed.</summary>
    internal int KeepTailMs
        => _lastActiveEndMs == 0 ? 0 : global::System.Math.Min(_lastActiveEndMs + PadMs, _fedMs);

    /// <summary>
    /// Consume one converted chunk (16 kHz mono 16-bit PCM) on the capture thread. Returns true
    /// when the continuous-silence early exit is reached — the caller signals its cut; feeds may
    /// legitimately CONTINUE until the caller unpublishes this instance after teardown quiesces
    /// delivery, and the monotone state makes that safe (post-cut speech can only lengthen the
    /// keep — real audio that reached the file). Allocation-free; partial frames carry across
    /// chunk boundaries.
    /// </summary>
    internal bool Feed(byte[] pcm16, int count)
    {
        var cut = false;
        for (var i = 0; i + 1 < count; i += 2)
        {
            long sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            _frameSumSquares += sample * sample;
            if (++_framePartialSamples < FrameSamples) continue;

            var active = _frameSumSquares > ActivityFloorMeanSquare * FrameSamples;
            _frameSumSquares = 0;
            _framePartialSamples = 0;
            _fedMs += FrameMs;

            if (active)
            {
                _silentRunMs = 0;
                _lastActiveEndMs = _fedMs;
            }
            else
            {
                _silentRunMs += FrameMs;
                if (_silentRunMs >= SilenceRunMs) cut = true;
            }
        }
        return cut;
    }

    /// <summary>
    /// Truncate <paramref name="wavPath"/>'s data chunk to
    /// <paramref name="snapshotBytes"/> + <paramref name="keepTailMs"/> of audio, patching the
    /// RIFF and data size fields in place. Never throws; never extends; never cuts below the
    /// snapshot. Returns the bytes removed (0 = nothing trimmed).
    /// </summary>
    internal static long TrimFileFailSoft(string wavPath, long snapshotBytes, int keepTailMs)
    {
        try
        {
            using var fs = new global::System.IO.FileStream(
                wavPath, global::System.IO.FileMode.Open,
                global::System.IO.FileAccess.ReadWrite, global::System.IO.FileShare.None);
            using var reader = new global::System.IO.BinaryReader(fs);

            // Parse the actual chunk layout — a fixed 44-byte assumption breaks the moment the
            // writer emits any extra chunk. RIFF<size>WAVE, then chunks to "data".
            if (fs.Length < 12) return 0;
            if (reader.ReadUInt32() != 0x46464952) return 0;             // "RIFF"
            fs.Seek(4, global::System.IO.SeekOrigin.Current);            // riff size — rewritten below
            if (reader.ReadUInt32() != 0x45564157) return 0;             // "WAVE"

            long dataOffset = -1, dataSize = -1, dataSizeFieldPos = -1;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                if (id == 0x61746164)                                    // "data"
                {
                    dataSizeFieldPos = fs.Position - 4;
                    dataOffset = fs.Position;
                    dataSize = size;
                    break;
                }
                fs.Seek(size + (size & 1), global::System.IO.SeekOrigin.Current);  // chunks are word-aligned
            }
            if (dataOffset < 0) return 0;
            // The writer may have recorded the true data length even when the header field says
            // otherwise mid-flush; trust the smaller of field vs physical remainder.
            dataSize = global::System.Math.Min(dataSize, fs.Length - dataOffset);

            var keep = snapshotBytes + (long)keepTailMs * BytesPerMs;
            keep -= keep & 1;                                            // 16-bit sample alignment
            if (keep < 0) return 0;
            if (keep >= dataSize) return 0;                              // never extend, nothing to cut

            var removed = dataSize - keep;
            // Size fields FIRST, truncate LAST (Kimi verification round): an I/O fault landing
            // mid-sequence then leaves a header-short/file-long WAV — benign, readers clamp —
            // instead of a truncated file whose header still claims the old larger sizes.
            fs.Position = dataSizeFieldPos;
            new global::System.IO.BinaryWriter(fs).Write((uint)keep);
            fs.Position = 4;
            new global::System.IO.BinaryWriter(fs).Write((uint)(dataOffset + keep - 8));
            fs.SetLength(dataOffset + keep);

            Logger.Information(
                "Post-stop grace trimmed {RemovedMs} ms of tail ({KeptTailMs} ms kept past the stop gesture)",
                removed / BytesPerMs, keepTailMs);
            return removed;
        }
        catch (global::System.Exception ex)
        {
            // Fail-soft: the untrimmed file is real audio and safe; a trim failure must never
            // cost the dictation. Warning, not Error — the recording itself succeeded.
            try { Logger.Warning(ex, "Post-stop grace trim failed — recording kept at full length"); } catch { }
            return 0;
        }
    }
}
