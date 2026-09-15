namespace VoiceWink.Helpers;

/// <summary>
/// Minimal WAV reader for the sherpa-onnx path (TRN-1 step 3): 16-bit PCM to the normalised float
/// samples the recognizer accepts, plus the file's REAL sample rate.
///
/// <para><b>The sample rate is parsed, never assumed — and that is a scar, not caution.</b> The
/// Parakeet CPU benchmark's first run assumed 16 kHz for sherpa's own fixtures, which are actually
/// 22.05/24 kHz. Whisper.net rejects a non-16 kHz file so the mistake surfaced there; sherpa does
/// NOT, so it silently decoded time-distorted audio and divided every RTF by a wrong duration. The
/// resulting figure was quoted in a backlog card before anyone noticed. Reading the <c>fmt </c>
/// chunk is what stops that recurring.</para>
///
/// <para>VoiceWink's own recordings are canonical 16 kHz mono 16-bit, but Audio Transcribe accepts
/// user files, so neither rate nor channel count can be taken on trust.</para>
/// </summary>
internal static class WavPcm
{
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatExtensible = 0xFFFE;

    /// <summary>Samples normalised to [-1, 1], and the file's declared sample rate.</summary>
    internal static (float[] Samples, int SampleRate) ReadMono16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        return ReadMono16(reader);
    }

    /// <summary>Same parse over in-memory WAV bytes (AUD-28: the no-speech gate's second opinion
    /// reads the conditioned copy, which never touches disk). Pure code motion over a shared
    /// core, kept stream-agnostic deliberately: the one place <c>FileStream</c> and
    /// <c>MemoryStream</c> diverge is a <c>Position</c> set past <c>int.MaxValue</c> (silent
    /// overshoot vs <c>ArgumentOutOfRangeException</c>), so the unrecognised-chunk seek clamps to
    /// <c>Length</c> — the parity tests pin it, a hostile declared chunk size included.</summary>
    internal static (float[] Samples, int SampleRate) ReadMono16(byte[] wavBytes)
    {
        using var reader = new BinaryReader(new MemoryStream(wavBytes, writable: false));
        return ReadMono16(reader);
    }

    private static (float[] Samples, int SampleRate) ReadMono16(BinaryReader reader)
    {
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        reader.ReadUInt32();                                            // riff size — unused
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        short channels = 0, bitsPerSample = 0;
        var sampleRate = 0;
        // 0 is not a valid WAVE format tag, so it doubles as "fmt not seen yet".
        ushort formatTag = 0;
        ushort extensibleSubFormat = 0;
        byte[]? data = null;

        // Chunk walk rather than fixed offsets: real files carry LIST/fact/cue chunks between fmt
        // and data, and assuming a 44-byte header reads metadata as audio.
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();

            if (id == "fmt ")
            {
                // The canonical PCM fmt chunk is 16 bytes. A shorter declared size means the fields
                // read below are not actually there — and because the walk seeks to
                // `chunkStart + size`, a size under 16 seeks BACKWARD into the bytes just consumed
                // and re-walks the fmt body as though it were a chunk header. Where that ends
                // depends entirely on the garbage: at best a wrong answer, at worst a walk that
                // does not terminate (`size == 0` seeks exactly back to where it started).
                // Audio Transcribe accepts arbitrary user files, so a malformed header is reachable.
                if (size < 16)
                    throw new InvalidDataException($"WAV fmt chunk is too short ({size} bytes; 16 required).");

                var chunkStart = reader.BaseStream.Position;
                formatTag = reader.ReadUInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadUInt32();                                    // byte rate
                reader.ReadUInt16();                                    // block align
                bitsPerSample = reader.ReadInt16();

                // WAVE_FORMAT_EXTENSIBLE keeps the real encoding in a sub-format GUID whose first
                // two bytes are the tag it stands in for. Read only when the chunk is long enough to
                // carry it: 16 header bytes + 2 cbSize + 22 extension.
                // An EXTENSIBLE chunk that is too short to carry its own sub-format GUID is
                // malformed, not merely unusual — and reading past it would take bytes from the
                // next chunk and treat them as the encoding. Rejected rather than guessed: below
                // the check `extensibleSubFormat` stays 0, which is not a valid tag, so the format
                // check downstream refuses it either way. This just says why.
                if (formatTag == WaveFormatExtensible && size < 40)
                    throw new InvalidDataException($"WAV extensible fmt chunk is too short ({size} bytes; 40 required).");

                if (formatTag == WaveFormatExtensible)
                {
                    reader.BaseStream.Position = chunkStart + 18;       // past cbSize
                    reader.ReadUInt16();                                // valid bits per sample
                    reader.ReadUInt32();                                // channel mask
                    extensibleSubFormat = reader.ReadUInt16();          // first field of the GUID
                }

                reader.BaseStream.Position = chunkStart + size;         // honour the declared size
            }
            else if (id == "data")
            {
                // Bounded before the cast: an unsigned size above int.MaxValue wraps NEGATIVE, and
                // ReadBytes then throws an ArgumentOutOfRangeException that says nothing about the
                // file. Clamped to what remains rather than trusted, so a truncated or lying header
                // reads the bytes that exist instead of throwing.
                var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
                var toRead = (int)Math.Min(Math.Min(size, (uint)int.MaxValue), remaining);
                data = reader.ReadBytes(toRead);
                break;
            }
            else
            {
                // `(long)size`, NOT `size + (size % 2)` in uint arithmetic. An unrecognised chunk
                // declaring size 0xFFFFFFFF (odd) makes that expression wrap to ZERO, the position
                // never advances, and the walk re-reads the same header forever — a HANG, on a
                // 24-byte file anyone can hand to Audio Transcribe. Widening first makes the seek
                // overshoot EOF instead, and the loop's `Position + 8 <= Length` condition ends it.
                //
                // CLAMPED to Length (AUD-28): the overshoot is where the two stream types diverge —
                // FileStream permits a Position arbitrarily past EOF, MemoryStream throws
                // ArgumentOutOfRangeException past int.MaxValue — and the byte[] overload promises
                // path-identical behavior. The walk only ever needs "past the end" to mean "the
                // loop exits", which landing ON Length achieves for both, so the malformed file
                // still fails as the documented InvalidDataException, never an argument error.
                var next = reader.BaseStream.Position + (long)size + (size % 2);  // chunks are word-aligned
                reader.BaseStream.Position = Math.Min(next, reader.BaseStream.Length);
            }
        }

        if (data is null || sampleRate == 0) throw new InvalidDataException("WAV file has no fmt/data chunk.");

        // Its OWN message, and `< 0` rather than folding into the check above: the rate is read as a
        // signed int32 (see the fmt walk), so a malformed header can declare a NEGATIVE rate, which
        // sailed through the old equality test. Rejected here so a bad file fails as a FILE error
        // rather than as an ArgumentOutOfRangeException out of LongAudioChunker — but "has no fmt/data
        // chunk" would be a lie about a file that has both, and support diagnoses malformed user files
        // from exactly these strings (TRN-10 diff review, both reviewers).
        if (sampleRate < 0)
            throw new InvalidDataException($"WAV file declares an invalid sample rate ({sampleRate}).");

        // The format tag is CHECKED, not merely read past. Audio Transcribe accepts arbitrary user
        // files, and a 32-bit-float or A-law WAV can declare 16 bits per sample — passing the bits
        // check and then decoding as garbage the recognizer would faithfully transcribe as nonsense.
        // A failure that looks like a bad model rather than a bad file is the worst kind to ship.
        var effectiveFormat = formatTag == WaveFormatExtensible ? extensibleSubFormat : formatTag;
        if (effectiveFormat != WaveFormatPcm)
        {
            throw new InvalidDataException(
                $"Only uncompressed PCM WAV files are supported (format tag {formatTag}).");
        }

        if (bitsPerSample != 16) throw new InvalidDataException($"Only 16-bit PCM is supported (got {bitsPerSample}).");
        if (channels < 1) throw new InvalidDataException("WAV file declares no channels.");

        // A zero-frame file is NOT rejected here, deliberately, and the two diff reviewers disagreed
        // about it — so this records the resolution rather than the last opinion.
        //
        // Codex (TRN-10 round 3) was right that an empty waveform reaching sherpa KILLS THE PROCESS
        // (0xC0000409, STATUS_STACK_BUFFER_OVERRUN, no managed exception; measured via
        // tools/parakeet-long-audio) and that nothing on the Audio Transcribe path counted frames, so an
        // empty-but-valid WAV was a user-reachable crash. Kimi (TRN-1) had already pinned the opposite
        // contract HERE, with its own reason: a well-formed header with a zero-length data chunk is an
        // empty RECORDING, not a malformed file, and throwing turns "you recorded nothing" into "your
        // file is broken" — see WavReader_ReturnsAnEmptyResultForAValidHeaderWithNoAudio.
        //
        // Both hold, because they are about different things: the crash must close, the user-facing
        // CLASSIFICATION must not change. So the guard lives at the decode boundary (ChunkedDecode skips
        // zero-length chunks) and this reader keeps returning an empty array — unchanged as a READER
        // contract, while the decode-level outcome deliberately does change, from a process kill to an
        // empty result that lands in the existing empty-transcription handling (REL-13).
        var frames = data.Length / 2 / channels;
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            // Mono-mix by averaging: a stereo file must not be read as double-length mono, which
            // would interleave the channels into noise.
            var acc = 0;
            for (var c = 0; c < channels; c++)
                acc += BitConverter.ToInt16(data, (i * channels + c) * 2);
            samples[i] = acc / (float)channels / 32768f;
        }

        return (samples, sampleRate);
    }
}
