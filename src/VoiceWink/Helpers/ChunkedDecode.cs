namespace VoiceWink.Helpers;

/// <summary>
/// TRN-10 chunk-decode ORCHESTRATION — the aggregation around a decoder, split from the native decode
/// it drives so the parts that can go wrong WITHOUT a recognizer are testable: which samples each chunk
/// gets, the between-chunk cancellation check, empty-result handling, joining, and exception
/// propagation.
///
/// <para><b>Why it is a helper rather than a method on the service.</b> Two reasons, both from diff
/// review. A real decode needs the native library and the 670 MB model, so the first version left the
/// whole loop unpinned and leaned on the pure planner plus a manual harness — which lives outside the
/// solution and therefore protects nothing in CI, on a P1 path. Then, once the seam existed as an
/// `internal` on the service, the HARNESS still could not reach it (it links individual files, and
/// dragging the service in would pull Serilog and the sherpa wrapper with it), so the harness was
/// re-implementing this loop while its README claimed to measure the shipped path. Living here, one
/// copy serves production, CI and the harness.</para>
///
/// <para><b>Failures propagate — no partial-join salvage.</b> REL-12's retained WAV + amber Retry
/// already covers a failed transcription, and returning a half transcript as though it had succeeded is
/// the exact defect TRN-10 exists to fix.</para>
/// </summary>
internal static class ChunkedDecode
{
    /// <summary>
    /// Decode every chunk and join the non-empty results with a single space.
    /// </summary>
    /// <param name="decodeOne">Decodes one chunk's samples. Called once per chunk, sequentially.</param>
    internal static string Run(
        IReadOnlyList<DecodeChunk> plan,
        float[] samples,
        Func<float[], string> decodeOne,
        CancellationToken ct)
    {
        var parts = new List<string>(plan.Count);

        foreach (var chunk in plan)
        {
            // Between chunks, INSIDE the loop. `Task.Run`'s token is only a pre-start check, so before
            // TRN-10 a long recording was ONE uninterruptible native call; now a cancel lands within
            // one chunk.
            ct.ThrowIfCancellationRequested();

            // An empty chunk NEVER reaches the decoder, and this line IS the guard rather than a backstop
            // behind one. The consequence is not a bad transcript: an empty waveform kills the process
            // inside sherpa with 0xC0000409, no managed exception to catch — measured, and reachable in
            // production from a structurally valid WAV with an empty `data` chunk (an instantly-stopped
            // recording, or a source file that converts to no frames). `WavPcm` deliberately still reads
            // such a file as empty — rejecting it would turn "you recorded nothing" into "your file is
            // broken", see the comment there — so the crash has to close at this boundary.
            //
            // The skip is deliberately SILENT here, and the observability lives one layer up. A reviewer
            // rightly asked for a mid-plan empty chunk to be visible — it would mean the planner had
            // broken total coverage, and skipping it would swallow the audio it stood for. Two attempts
            // at putting that in this method were wrong: a `Debug.Assert(chunk.Count > 0 ||
            // plan.Count == 1)` contradicts this class's own defensive test
            // (`DecodeChunks_EmptyChunkAmongRealOnes_IsSkippedButOthersDecode` builds exactly that plan on
            // purpose) and hung the suite when it fired; and logging from here would need either a Serilog
            // dependency in a file `tools/parakeet-long-audio` links or a callback in the shipped
            // signature. The anomaly is a property of the PLAN, so
            // `ParakeetTranscriptionService.TranscribeAsync` warns on it where a logger already exists,
            // and `Plan` is test-pinned against producing one at all.
            if (chunk.Count == 0) continue;

            var part = decodeOne(SliceFor(chunk, samples));
            if (!string.IsNullOrEmpty(part)) parts.Add(part);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The chunk's samples — the ORIGINAL array when the chunk is the whole recording.
    ///
    /// <para>That special case is not a micro-optimisation, it is what keeps the short-recording path
    /// operationally unchanged. Copying unconditionally added a full-length allocation to EVERY
    /// recording — 2.24 MB for 35 s at 16 kHz, i.e. past the 85 KB large-object threshold on ordinary
    /// dictation, where the pre-TRN-10 code handed `samples` straight to `AcceptWaveform`. A diff
    /// reviewer caught the claim of an unchanged path not matching the allocation behaviour.</para>
    ///
    /// <para>Multi-chunk slices ARE copied, one at a time and never materialised up front: sherpa's
    /// <c>AcceptWaveform</c> takes a <c>float[]</c>, and at a high user-file rate one slice is tens of
    /// MB — fine sequentially, not fine as a list.</para>
    ///
    /// <para>The hand-over is also why TRN-10b's <c>DecodeInputGain.Apply</c> copies when it gains
    /// and never scales in place: on the single-chunk path the "slice" IS the caller's recording
    /// array, and mutating it would corrupt whatever else still reads it (the harness reuses one
    /// array across modes).</para>
    /// </summary>
    private static float[] SliceFor(DecodeChunk chunk, float[] samples)
        => chunk.Start == 0 && chunk.Count == samples.Length
            ? samples
            : samples.AsSpan(chunk.Start, chunk.Count).ToArray();
}
