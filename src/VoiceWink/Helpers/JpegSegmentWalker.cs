namespace VoiceWink.Helpers;

/// <summary>
/// The ONE walker over a JPEG's marker segments, from SOI up to the start of scan. Two readers ask
/// it different questions about the same structure: <see cref="ReferenceImagePolicy.ClassifyJpegContainer"/>
/// (ENH-6i — is this a multi-picture container) and <see cref="ImagePixelSize"/> (IMG-21 — how big
/// is the frame, and which way up is it). It was ENH-6i's private loop until IMG-21 needed it a
/// second time; it moved rather than being copied, so a bounds fix lands for both.
///
/// <para>Walker contract (Codex plan rounds 1–2 on ENH-6i, unchanged by the move):</para>
/// <list type="bullet">
/// <item>SOI required; FF fill bytes and standalone markers (01, D0–D7) tolerated; every segment
/// length bounds-checked without overflow; hard cap of <see cref="MaxSegments"/> segments.</item>
/// <item>The visitor sees a segment only once its whole payload is inside the buffer, so it may
/// index <c>[offset, offset + length)</c> without its own bounds check.</item>
/// <item>SOS ends the walk (<see cref="WalkEnd.ReachedScan"/>) once its own length header has
/// validated like every other segment — a truncated or corrupt SOS must not count (Codex diff r1).
/// EOI before SOS, EOF, a misaligned marker, a short length and the segment cap all end it as
/// <see cref="WalkEnd.NoScan"/>.</item>
/// </list>
/// Pure.
/// </summary>
internal static class JpegSegmentWalker
{
    internal const int MaxSegments = 64;

    internal enum WalkEnd
    {
        /// <summary>The visitor asked to stop.</summary>
        Stopped,
        /// <summary>A valid SOS segment was reached; everything before it was walked.</summary>
        ReachedScan,
        /// <summary>The header did not walk cleanly to SOS — callers must not vouch for it.</summary>
        NoScan,
    }

    /// <param name="bytes">The whole JPEG.</param>
    /// <param name="onSegment">
    /// Called once per length-bearing segment before SOS with (marker, payload offset, payload
    /// length) — the payload excludes the two length bytes. Return false to stop the walk.
    /// </param>
    internal static WalkEnd Walk(byte[] bytes, global::System.Func<byte, int, int, bool> onSegment)
    {
        if (bytes.Length < 4) return WalkEnd.NoScan;
        if (bytes[0] != 0xFF || bytes[1] != 0xD8) return WalkEnd.NoScan; // SOI required

        var i = 2;
        for (var segments = 0; segments < MaxSegments; segments++)
        {
            if (i >= bytes.Length - 1) return WalkEnd.NoScan; // EOF before SOS
            if (bytes[i] != 0xFF) return WalkEnd.NoScan;      // misaligned — malformed
            // Consume FF fill bytes (a marker may be preceded by any number of FFs).
            while (i < bytes.Length - 1 && bytes[i + 1] == 0xFF) i++;
            if (i >= bytes.Length - 1) return WalkEnd.NoScan;

            var marker = bytes[i + 1];
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2; // standalone marker — no length field
                continue;
            }
            if (marker == 0xD9)
                return WalkEnd.NoScan; // EOI before SOS — not a decodable frame

            if (i + 3 >= bytes.Length) return WalkEnd.NoScan;
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (length < 2) return WalkEnd.NoScan;
            var segmentEnd = (long)i + 2 + length;
            if (segmentEnd > bytes.Length) return WalkEnd.NoScan; // truncated

            if (marker == 0xDA)
                return WalkEnd.ReachedScan;

            if (!onSegment(marker, i + 4, length - 2))
                return WalkEnd.Stopped;

            i = (int)segmentEnd;
        }
        return WalkEnd.NoScan; // segment cap hit — refuse to vouch
    }
}
