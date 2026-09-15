namespace VoiceWink.Helpers;

/// <summary>A half-open sample range <c>[Start, Start + Count)</c> to decode as one unit.</summary>
/// <remarks>Named <c>DecodeChunk</c> rather than <c>AudioChunk</c> to stay clear of
/// <c>AudioRecorderService.OnAudioChunk</c>, which is a capture-buffer event — a different concept one
/// word away.</remarks>
public readonly record struct DecodeChunk(int Start, int Count);

/// <summary>
/// TRN-10 (2026-08-05): where to cut a long recording so a single-pass decoder never sees more audio
/// than it can handle. Built for sherpa-onnx/Parakeet, which — unlike whisper.cpp — does no internal
/// windowing.
///
/// <para><b>The defect this exists to fix is SILENT.</b> Fed a 108 s recording, Parakeet TDT returned
/// 566 chars covering the first ~61 s and reported success; the pipeline pasted half a dictation with
/// no error anywhere. Measured on the same file and model: the 60–108 s tail decodes perfectly in
/// isolation, and a 10–108 s slice skipped ~40 s out of the MIDDLE while still emitting the final
/// sentence — so frames are being skipped, not cut off, and the failure is content-dependent rather
/// than a fixed cap (88 s and 103 s slices decoded end to end while 64 s did not). Punctuation and
/// capitalisation degrade progressively with length well before any text is lost. Full method and
/// numbers in <c>tools/parakeet-long-audio/README.md</c>.</para>
///
/// <para><b>Do NOT attach a speed claim to this change, in either direction.</b> Four successive attempts
/// to measure one each produced a different answer, because on this machine the same operation ranges
/// 10.7–33.5 s (single pass) and 9.3–35.3 s (chunked). The within-path spread exceeds the between-path
/// gap, so the harness cannot rank them; details in <c>tools/parakeet-long-audio/README.md</c>.
/// Correctness is the entire justification, and the O(n²)-attention argument that motivated the first
/// claim is left as theory.</para>
///
/// <para><b>No stub chunks, by construction.</b> A cut happens only when more than
/// <see cref="MaxChunkSeconds"/> + <see cref="SearchBandSeconds"/> remains, and lands within the band,
/// so the leftover is always greater than <see cref="SearchBandSeconds"/>. Expressing it as a
/// PRECONDITION rather than merging small remainders afterwards is deliberate — it makes "never hand the
/// recognizer a stub" a property of the algorithm instead of something a test has to catch.</para>
///
/// <para><b>And that is MEASURED, not defensive.</b> The harness was briefly able to request a
/// zero-length slice, and sherpa did not return empty or throw — it killed the process with
/// <c>0xC0000409</c> (STATUS_STACK_BUFFER_OVERRUN), no managed exception to catch. So a planner that can
/// emit a stub chunk does not produce a slightly worse transcript; it crashes the app mid-dictation.</para>
///
/// <para><b>Everything at or below <see cref="MaxChunkSeconds"/> + <see cref="SearchBandSeconds"/>
/// yields ONE chunk</b>, so short recordings — the common case — take a byte-identical path to the
/// pre-TRN-10 single decode. (Since TRN-10b the decode INPUT may additionally be gain-scaled when
/// the recording is quiet — that is <c>DecodeInputGain</c>'s layer, downstream of this planner and
/// invisible to it: uniform gain does not move the quietest-window argmin.)</para>
///
/// <para><b>Empty input yields one EMPTY chunk</b>, keeping <see cref="Plan"/> total over its input
/// rather than special-casing zero. The reason is the coverage invariant, NOT byte-identity with the old
/// decode: an earlier draft justified it as "an empty WAV made one decode call before this change too",
/// which stopped being true the moment `ChunkedDecode` began skipping zero-length chunks — it now makes
/// zero. Stopping an empty waveform from reaching the recognizer is deliberately NOT this type's job;
/// `ChunkedDecode` is the single place that does it, because an empty buffer does not merely decode
/// badly inside sherpa — it kills the process (0xC0000409, no managed exception). `WavPcm` deliberately
/// still READS a zero-frame file as empty rather than rejecting it, so "you recorded nothing" does not
/// become "your file is broken"; see the comment there for the two-reviewer resolution.</para>
/// </summary>
internal static class LongAudioChunker
{
    /// <summary>Nominal cut point. 30 s measured complete with punctuation intact, ~2× under the
    /// observed ~61 s failure point, and degradation was already visible at 68 s — so this is not
    /// tuned to the edge of what works.</summary>
    internal const double MaxChunkSeconds = 30.0;

    /// <summary>How far BACK from the nominal cut to hunt for a pause. Wider finds real silence more
    /// often (fewer split words); narrower keeps chunks even. Also the stub floor — see the type
    /// remarks.</summary>
    internal const double SearchBandSeconds = 5.0;

    /// <summary>Energy probe width. Short enough to sit inside a between-word gap.</summary>
    internal const double ProbeWindowSeconds = 0.05;

    /// <summary>
    /// Contiguous, non-overlapping chunks covering <c>[0, samples.Length)</c> EXACTLY.
    ///
    /// <para>That total-coverage property is the whole contract: a planner that drops samples
    /// reintroduces silent truncation somewhere new, which is why it is test-pinned rather than
    /// asserted here.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sampleRate"/> is not positive. Thrown rather than defaulted because at rate 0
    /// the boundary never advances and the loop would never terminate — and because a silent
    /// single-chunk answer would hide the caller's bug instead of surfacing it. <c>WavPcm</c> rejects
    /// non-positive rates before this is reached, so production cannot get here; this guards future
    /// callers.
    /// </exception>
    internal static List<DecodeChunk> Plan(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        var total = samples.Length;
        var chunks = new List<DecodeChunk>();

        // Thresholds in LONG. At a declared rate near 62 MHz, `maxChunk + band` in int arithmetic
        // overflows NEGATIVE, which makes the loop condition below permanently true: an unbounded loop
        // holding the service's semaphore, uncancellable because `Plan` takes no token.
        //
        // DEFENCE IN DEPTH, not a live user path — and the correction matters, because the first version
        // of this comment claimed the opposite. `WavPcm` does accept every positive int32 rate (it
        // rejects only non-positive ones), but Audio Transcribe never hands it an arbitrary file:
        // `AudioFileProcessor.ConvertToWavAsync` resamples everything to canonical 16 kHz mono 16-bit
        // first, so production callers supply 16 kHz. What remains unbounded is a DIRECT caller —
        // `tools/parakeet-long-audio` today, any future one tomorrow — and sherpa forwards a mismatched
        // rate into its own resampler with no upper-rate guard. Cheap to make safe, so it is.
        // (Both the bug and the false reachability claim were found by Codex on successive diff rounds.)
        var maxChunk = (long)(MaxChunkSeconds * sampleRate);
        var band = (long)(SearchBandSeconds * sampleRate);

        // The cut threshold, NOT maxChunk — see the type remarks on why this is a precondition.
        var cutAbove = maxChunk + band;

        // The whole recording fits: one chunk, byte-identical to the pre-TRN-10 single decode. This
        // is ALSO the only exit an absurd rate can take, and it is what makes the narrowing below
        // safe: past here `cutAbove < total <= int.MaxValue`, so `maxChunk` and `band` each fit in
        // int, and the loop condition (`pos < total - cutAbove`) keeps `pos + maxChunk < total - band`
        // — so no expression inside the loop can overflow either.
        if (total <= cutAbove)
        {
            chunks.Add(new DecodeChunk(0, total));
            return chunks;
        }

        var maxChunkSamples = (int)maxChunk;
        var bandSamples = (int)band;
        var probe = Math.Max(1, (int)(ProbeWindowSeconds * sampleRate));
        var pos = 0;

        while (total - pos > cutAbove)
        {
            var cut = FindQuietestPoint(
                samples, pos + maxChunkSamples - bandSamples, pos + maxChunkSamples, probe);
            chunks.Add(new DecodeChunk(pos, cut - pos));
            pos = cut;
        }

        // The remainder. Guaranteed > SearchBandSeconds because the cut never lands past the nominal
        // boundary (FindQuietestPoint clamps to it) and the loop only ran while more than
        // maxChunk + band remained.
        chunks.Add(new DecodeChunk(pos, total - pos));
        return chunks;
    }

    /// <summary>
    /// Start index of the lowest-energy probe window in <c>[from, to)</c>, returned as its CENTRE so
    /// the cut sits in the middle of the quiet patch rather than at its edge.
    ///
    /// <para>Returns the argmin unconditionally — there is no silence threshold and no widen-the-search
    /// fallback. When the speaker never pauses, the cut splits a word; that costs one word, against
    /// losing tens of seconds of speech, and both extra mechanisms would buy something nobody has
    /// measured.</para>
    ///
    /// <para><b><c>internal</c> rather than <c>private</c> since TRN-22</b>, which added a second
    /// caller: <see cref="VadDecodePlan"/> uses the same argmin to cap an over-long VAD segment. The
    /// probe constants are re-used as-is — a cut is a cut, and a second set of them would be two
    /// unmeasured numbers instead of one measured one.</para>
    /// </summary>
    internal static int FindQuietestPoint(ReadOnlySpan<float> samples, int from, int to, int probe)
    {
        // The nominal boundary, captured BEFORE `to` is narrowed for the scan. Every return below
        // clamps to it, and that is what makes the remainder floor literally true rather than
        // approximately true: the scan steps by probe/2, which does not divide the band evenly at
        // every rate, so `bestAt + probe/2` could otherwise land PAST the boundary — 1002 samples
        // past it at 44.1 kHz, the most common user-file rate, eating ~23 ms off the promised
        // remainder. Behaviourally harmless (a 4.977 s chunk is no stub) but the claim in the type
        // remarks would have been false, and both diff reviewers found it independently.
        var ceiling = Math.Min(to, samples.Length);

        // Clamped so a short or oddly-sized span can never read out of range. `from` can be negative
        // when the band is wider than the audio ahead of the boundary.
        from = Math.Max(0, from);
        to = Math.Min(samples.Length - probe, to);

        // No room to scan — cut at the nominal boundary. Unreachable from Plan's loop (which only
        // runs while more than maxChunk + band remains); defensive for any future caller.
        if (to <= from) return Math.Max(0, ceiling);

        var step = Math.Max(1, probe / 2);
        var bestAt = from;
        var bestEnergy = double.MaxValue;

        for (var i = from; i < to; i += step)
        {
            double energy = 0;
            for (var j = i; j < i + probe; j++) energy += samples[j] * samples[j];

            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestAt = i;
            }
        }

        return Math.Min(bestAt + probe / 2, ceiling);
    }
}
