using System.IO;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-2: applies a positive dB gain to a canonical recording WAV IN PLACE, atomically.
/// Only the <c>data</c> chunk's samples change (per-sample clamp at full scale); every other
/// byte — headers, extra chunks, padding — is preserved verbatim. The gained copy is written to
/// <c>&lt;stem&gt;.norm.wav</c> beside the original and moved over it, so a crash leaves either
/// the intact original or the fully-written replacement, never a torn file. The temp name ends
/// in <c>.wav</c> ON PURPOSE: a crash orphan then matches the recordings root's existing
/// <c>*.wav</c> 7-day hard-retention sweep (privacy backstop, test-pinned in
/// <c>TranscriptionCleanupServiceTests</c>) — do not rename it outside that pattern.
/// Write seams (<see cref="_writeFile"/>/<see cref="_moveFile"/>) mirror SettingsService's
/// test-seam idiom so failure paths are testable without filesystem faults.
/// </summary>
internal static class WavGainApplier
{
    internal static global::System.Action<string, byte[]> _writeFile = File.WriteAllBytes;
    internal static global::System.Action<string, string> _moveFile = (from, to) => File.Move(from, to, overwrite: true);

    internal static string TempPathFor(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".norm.wav");

    /// <summary>Returns true when the file was replaced with the gained copy; false when the
    /// file is not a canonical WAV (untouched). IO failures throw — the coordinator owns the
    /// fail-soft contract. A cancellation before the replacement leaves the original current.
    ///
    /// <para><b><paramref name="beforeReplace"/> (REL-23):</b> invoked with the original path
    /// AFTER the gained temp is fully written and BEFORE the move destroys the pre-gain bytes —
    /// the one instant where retention can act without ever competing with the boost for disk
    /// (the temp write has already claimed its space, exactly as it did before REL-23 existed;
    /// two Codex diff rounds established that any retention IO placed ahead of the temp write
    /// can starve it and silently ship unboosted quiet audio). The hook OWNS its failures — it
    /// must never throw (RecordingAudioPreparation's hook is internally fail-soft); a throw
    /// here would abort a gain whose temp already succeeded, recreating the exact coupling this
    /// placement removes, so it is treated as an IO failure of the apply and propagates to the
    /// coordinator's fail-soft catch.</para></summary>
    internal static bool ApplyGain(
        string path,
        double gainDb,
        global::System.Threading.CancellationToken ct,
        global::System.Action<string>? beforeReplace = null)
    {
        if (gainDb <= 0) return false;

        // BEFORE the read, not after it. `File.ReadAllBytes` takes no token, so a check on the far
        // side of it cannot stop the read — an already-cancelled caller still paid for a full-file
        // load first (Codex diff review r8, AUD-9). Harmless on the recording path, where files are
        // dictation-sized; it matters on the file path, where the caller has just finished its own
        // full read to MEASURE and the bound permits 512 MB.
        ct.ThrowIfCancellationRequested();

        var wav = File.ReadAllBytes(path);
        if (!WavLevelAnalyzer.TryLocateCanonicalData(wav, out var dataOffset, out var dataLength))
            return false;

        var factor = global::System.Math.Pow(10.0, gainDb / 20.0);
        var sampleCount = dataLength / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested(); // every ~16k samples (~1 s of audio)
            var offset = dataOffset + 2 * i;
            var scaled = global::System.BitConverter.ToInt16(wav, offset) * factor;
            var clamped = (short)global::System.Math.Clamp(
                global::System.Math.Round(scaled), short.MinValue, short.MaxValue);
            wav[offset] = (byte)(clamped & 0xFF);
            wav[offset + 1] = (byte)((clamped >> 8) & 0xFF);
        }

        var temp = TempPathFor(path);
        try
        {
            _writeFile(temp, wav);
            ct.ThrowIfCancellationRequested();
            beforeReplace?.Invoke(path);       // REL-23: last look at the pre-gain bytes
            // Re-checked AFTER the hook — the hook is a synchronous full-file copy that does not
            // observe the token, so without this a cancel landing during it would ride into the
            // move and break the documented invariant that cancellation before the replacement
            // leaves the original current (Codex diff round 3).
            ct.ThrowIfCancellationRequested(); // last check before the point of no return
            _moveFile(temp, path);
            return true;
        }
        finally
        {
            // Success removes the temp via the move; every failure path deletes it here so
            // partial dictated audio never lingers (the *.wav sweep is the crash backstop only).
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (global::System.Exception) { /* sweep-covered */ }
        }
    }
}
