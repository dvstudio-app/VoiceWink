using NAudio.Wave;
using Serilog;

namespace VoiceWink.Services.Audio;

/// <summary>
/// Converts audio files to 16kHz mono WAV for Whisper processing.
/// </summary>
public sealed class AudioFileProcessor
{
    private static ILogger Logger => Log.ForContext<AudioFileProcessor>();

    private readonly Func<string, Helpers.WavLevels?> _measure;
    private readonly Func<string, double, global::System.Threading.CancellationToken, bool> _applyGain;
    private readonly Func<string> _outputDir;

    public AudioFileProcessor()
        // The lambda (not a method group) because ApplyGain's REL-23 beforeReplace hook made it
        // 4-parameter; the file path deliberately keeps the 3-arg seam — no raw sibling here,
        // the user's own source file IS this path's raw end (REL-23 card).
        : this(Helpers.WavLevelAnalyzer.MeasureFile,
               (path, gainDb, ct) => Helpers.WavGainApplier.ApplyGain(path, gainDb, ct),
               Helpers.AppPaths.EnsureRecordings) { }

    /// <summary>
    /// Test seam — mirrors <c>RecordingAudioPreparation</c>'s, plus an OUTPUT-DIRECTORY seam that
    /// class does not need.
    ///
    /// <para><b>The output seam is not convenience, it is a correctness rule.</b> Conversion writes
    /// <c>converted_&lt;timestamp&gt;.wav</c> into <c>AppPaths.EnsureRecordings()</c> — the REAL
    /// <c>%LOCALAPPDATA%/VoiceWink/Recordings</c>. Before AUD-9 no test completed a conversion (the
    /// existing ones only drive throw paths), so nothing noticed; the first tests that do would have
    /// written into the owner's live recordings folder on every <c>dotnet test</c> run, where
    /// <c>SupportBundle.EnumerateRecordingCandidates</c> globs <c>*.wav</c> and would offer them as
    /// selectable attachments in Report-a-problem until the 7-day sweep. Both diff reviewers flagged
    /// it as blocking, independently, and they were right — the suite's standing rule is injectable
    /// temp roots, never the real user-data root.</para>
    /// </summary>
    internal AudioFileProcessor(
        Func<string, Helpers.WavLevels?> measure,
        Func<string, double, global::System.Threading.CancellationToken, bool> applyGain,
        Func<string> outputDir,
        Func<string, Helpers.ZeroRunCounts?>? scanZeroRuns = null)
    {
        _measure = measure;
        _applyGain = applyGain;
        _outputDir = outputDir;
        _scanZeroRuns = scanZeroRuns ?? Helpers.ZeroRunScan.TryScan;
    }

    // AUD-10: same seam and same shared adapter (ZeroRunScan) as the recording side — "the
    // counter belongs in both or file conversions stay blind" (the card's own AUD-9 note). The
    // call site owns seam totality, same as the recording side.
    private readonly Func<string, Helpers.ZeroRunCounts?> _scanZeroRuns;

    /// <summary>
    /// Convert any supported audio file to 16kHz mono 16-bit PCM WAV.
    /// Returns path to the converted file.
    /// </summary>
    public async Task<string> ConvertToWavAsync(string inputPath, global::System.Threading.CancellationToken ct = default)
    {
        // Validate input path
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input path is required.", nameof(inputPath));

        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Audio file not found.", fullPath);

        if (!IsSupportedFormat(fullPath))
            throw new ArgumentException($"Unsupported audio format: {Path.GetExtension(fullPath)}", nameof(inputPath));

        var outputDir = _outputDir();

        var outputPath = Path.Combine(outputDir, $"converted_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        // SEC-4: the user picks this file, so its DIRECTORY names their work ("\Documents\Acme\").
        // Folded to filename+extension — enough to diagnose a conversion failure, without the tree.
        Logger.Information("Converting to WAV: filePath={UserFilePath}", Helpers.LogPathProjection.FileNameOnly(inputPath));

        // The output file is OURS from the moment WaveFileWriter opens it until this method returns
        // its path. Anything that throws in between leaves the caller's `wavPath` unassigned, so its
        // own cleanup sees null and the file rides the 7-day sweep in the REAL recordings root,
        // where SupportBundle would offer it as an attachment. Round 3 wrapped only the normalization
        // half; a mid-decode failure (corrupt source, MediaFoundation error) leaks the same way with
        // a PARTIAL file, which Kimi rightly called asymmetric. One try now covers both phases.
        try
        {
            await ConvertCoreAsync(inputPath, outputPath, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteConverted(outputPath);
            throw;
        }

        return outputPath;
    }

    private async Task ConvertCoreAsync(string inputPath, string outputPath,
                                        global::System.Threading.CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var source = CreateReader(inputPath);
            var targetFormat = new WaveFormat(16000, 16, 1);

            // WAVE_FORMAT_EXTENSIBLE (tag 0xFFFE) carries byte-identical PCM under a longer header,
            // and is ordinary Windows output — this repo already recognises it in
            // ParakeetRuntimeTests. Two things go wrong if it is left alone (Codex diff review r2):
            // it matches on rate/channels/depth so the resample test below passes it through
            // UNCONVERTED, and WavLevelAnalyzer accepts only tag 1, so measurement returns null and
            // the whole AUD-9 gain silently does not happen for that class of file.
            //
            // Reinterpreting is the fix, NOT routing it to the resampler — the first attempt did
            // that and MediaFoundationResampler rejects it outright ("Input must be PCM or IEEE
            // float"), turning a silent no-gain into a hard failure for every extensible file. The
            // sample data is unchanged; only the header describing it is replaced.
            WaveStream reader = source;
            RawSourceWaveStream? reinterpreted = null;
            var standard = StandardiseExtensible(source.WaveFormat);
            if (standard is not null)
            {
                reinterpreted = new RawSourceWaveStream(source, standard);
                reader = reinterpreted;
            }
            using var _ = reinterpreted;

            // Resample if needed. `reader` is now guaranteed to describe itself as plain PCM, so an
            // extensible file that is otherwise canonical takes the passthrough branch and is
            // written out with a tag-1 header the analyzer can read.
            if (reader.WaveFormat.SampleRate != 16000 ||
                reader.WaveFormat.Channels != 1 ||
                reader.WaveFormat.BitsPerSample != 16)
            {
                using var resampler = new MediaFoundationResampler(reader, targetFormat);
                resampler.ResamplerQuality = 60;
                WaveFileWriter.CreateWaveFile(outputPath, resampler);
            }
            else
            {
                WaveFileWriter.CreateWaveFile(outputPath, reader);
            }
        }).ConfigureAwait(false);

        Logger.Information("Audio converted to {Output}", outputPath);

        // AUD-9: the same bounded soft-speech gain AUD-2 applies to fresh recordings, on the
        // canonical conversion output. A quiet FILE previously reached a cloud provider exactly as
        // supplied, so it hit the detection floors AUD-2 exists to clear (Deepgram degrades below
        // ≈ −50 dBFS active-RMS — the July calibration, on real provider behaviour).
        //
        // It lands HERE rather than at the page for two reasons. The user's original file is never
        // touched — this is our temp, written moments ago — and this method has exactly one caller,
        // so placing it inside structurally settles the card's open question about retry semantics:
        // a retry that reconverts gets the same gain (the decision is a pure function of the input
        // bytes), and one that reuses the temp gets the already-gained bytes. Neither can disagree.
        //
        // No ordering hazard of the kind AUD-2 has: its "gate before gain" rule exists because the
        // no-speech gate must judge raw audio, and the file path runs no gate at all.
        //
        // Leak handling lives in the CALLER's single try, which covers this and the decode above.
        await Task.Run(() => NormalizeFailSoft(outputPath, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort removal of a conversion this method is about to abandon. Fail-soft by design: the
    /// caller is already unwinding, and a failed cleanup must not replace the real exception.
    /// </summary>
    private static void TryDeleteConverted(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning("Could not remove the abandoned conversion {Path}: {ErrorType}",
                path, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Converted PCM above this is not measured. <c>WavLevelAnalyzer</c> and <c>WavGainApplier</c>
    /// each <c>File.ReadAllBytes</c> the whole file — sized for a dictation, not for the multi-GB
    /// uploads Audio Transcribe's provider limits permit (Codex diff review). Two full-file
    /// allocations on a 2 GB conversion is allocation pressure that can fail the process, and the
    /// failure would land as a silently skipped gain.
    ///
    /// <para><b>512 MB does NOT exceed every provider limit, and an earlier version of this comment
    /// claimed it did</b> (both diff reviewers, r2). The app's own table in
    /// <c>AudioTranscribePage.GetProviderFileSizeLimitMB</c> allows Deepgram 2000 MB and ElevenLabs
    /// 3000 MB, and local models have no limit at all. So the honest statement of the residual is:
    /// a converted file between 512 MB (~4.7 hours) and those limits IS transcribable and DOES skip
    /// the gain — a real, narrow instance of the AUD-2 defect class this card exists to close.</para>
    ///
    /// <para>The constant stays because the memory ceiling is real: the analyzer and the applier
    /// each <c>File.ReadAllBytes</c>, so a 3 GB conversion wants ~6 GB across the two, and an
    /// allocation failure would land as a silently skipped gain — strictly worse than a logged one.
    /// <b>The skip is logged, so the exposure is visible in a support bundle rather than silent.</b>
    /// Closing it properly means streaming the analyzer and the applier; that is a real change to a
    /// calibrated instrument and belongs in its own card with its own measurement, not smuggled into
    /// this one.</para>
    /// </summary>
    private const long MaxNormalizableBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Measure and (if warranted) boost the converted WAV, emitting exactly ONE
    /// <c>Audio level:</c> line — the same remote-verification instrument AUD-2 relies on in
    /// tester bundles, so a file run and a recording read identically in a support log.
    ///
    /// <para>Fail-soft throughout: a normalization problem must never cost a transcription, and
    /// <c>WavGainApplier</c>'s temp+move protocol guarantees the converted file is still the
    /// current one on every failure path. Constants are AUD-2's shipped ones, reused rather than
    /// re-derived — new constants would need new evidence, and the file-path variant has none.</para>
    /// </summary>
    private void NormalizeFailSoft(string wavPath, global::System.Threading.CancellationToken ct)
    {
        // Exactly ONE `Audio level:` line on every path — the AUD-2 remote-verification instrument,
        // which is only useful if its absence means "no conversion happened" rather than "one of
        // several silent paths was taken". The null-levels branch below was missing in the first
        // version, which is the same gap Codex forced closed on the recording side (both reviewers).
        try
        {
            var size = new FileInfo(wavPath).Length;
            if (size > MaxNormalizableBytes)
            {
                Logger.Information(
                    "Audio level: not measured — converted file is {SizeMB:F0} MB, above the {LimitMB:F0} MB normalization bound (file)",
                    size / (1024.0 * 1024.0), MaxNormalizableBytes / (1024.0 * 1024.0));
                return;
            }
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning("Could not size the converted file — skipping normalization: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            Logger.Information("Audio level: unmeasurable — normalization skipped (file)");
            return;
        }

        // Cancellation is observed at the STEP BOUNDARIES, because neither step can be interrupted
        // from inside: `_measure` reads and scans the whole file with no token at all, and
        // `_applyGain` reads it a SECOND time (Codex diff review r8).
        //
        // This first checkpoint is DEFENCE IN DEPTH, not a live path, and the difference was
        // MEASURED rather than assumed: reverting it left every test green, because the single
        // caller is `Task.Run(() => NormalizeFailSoft(…), ct)` and `Task.Run` refuses to schedule
        // an already-cancelled token at all — the delegate never starts. It stays because it is one
        // line and it becomes the only gate the moment that token is dropped from the `Task.Run`
        // overload, which is an easy edit to make without noticing. The checkpoint that actually
        // changes behaviour today is the one AFTER measurement, below.
        //
        // Measurement itself stays token-free deliberately: threading one through
        // `WavLevelAnalyzer` would re-open AUD-2's calibrated instrument to bound something the
        // post-measurement checkpoint already caps at ONE scan. The residual is exactly that — a
        // cancel lands at the end of the in-flight scan, never mid-scan.
        ct.ThrowIfCancellationRequested();

        Helpers.WavLevels? levels;
        Helpers.GainDecision decision;
        try
        {
            levels = _measure(wavPath);
            decision = Helpers.AudioGainPolicy.Decide(levels);
        }
        catch (global::System.OperationCanceledException)
        {
            // Mirrors RecordingAudioPreparation's pinned contract, and it is REACHABLE — the page's
            // token is threaded through conversion as of this change, so a swallowed OCE would
            // surface a user cancel as "Audio normalization failed" and carry on transcribing
            // (Kimi diff review; the "unreachable today" note it replaced went stale in the same PR).
            throw;
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning("Audio measurement failed — proceeding with the converted file as-is: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            Logger.Information("Audio level: unmeasurable — normalization skipped (file)");
            return;
        }

        // The checkpoint that stops the second full read — and the only one a no-gain decision ever
        // reaches. Deliberately outside the try above: an OperationCanceledException raised here is
        // the caller's own cancel, not a measurement failure, so it must not take the fail-soft arm.
        ct.ThrowIfCancellationRequested();

        // AUD-10: scan BEFORE any gain rewrite — the classifier judges PRE-gain audio (the
        // recording side's comment carries the full rationale; the two sites must judge the same
        // domain or their lines stop being comparable). After the cancellation checkpoint, so a
        // cancelled caller never pays for the read.
        Helpers.ZeroRunCounts? zeroRuns;
        try
        {
            zeroRuns = _scanZeroRuns(wavPath);
        }
        catch (global::System.OperationCanceledException)
        {
            throw;
        }
        catch (global::System.Exception ex)
        {
            try { Logger.Debug(ex, "Zero-run scan seam threw — Audio level line omits the tokens"); } catch { }
            zeroRuns = null;
        }

        var applied = false;
        if (decision.AppliesGain)
        {
            try
            {
                applied = _applyGain(wavPath, decision.GainDb, ct);
            }
            catch (global::System.OperationCanceledException)
            {
                throw;   // same contract as above
            }
            catch (global::System.Exception ex)
            {
                Logger.Warning("Audio normalization failed — proceeding with the converted file as-is: {ErrorType}: {ErrorMessage}",
                    ex.GetType().Name, ex.Message);
            }
        }

        var outcome = decision.Reason switch
        {
            Helpers.GainReason.Applied or Helpers.GainReason.HeadroomLimited when !applied => "ApplyFailed — original kept",
            var r => r.ToString(),
        };

        if (levels is { } l)
        {
            // AUD-10: the same classified zero-run tokens as the recording side (see
            // RecordingAudioPreparation's emit for the rationale) — file conversions must not
            // stay blind, per the card's own AUD-9 note.
            if (zeroRuns is { } z)
                Logger.Information(
                    "Audio level: peak={Peak:F1}dBFS activeRms={ActiveRms:F1}dBFS gain={Gain:F1}dB zeroCut={ZeroCut} zeroEdge={ZeroEdge} ({Outcome}) (file)",
                    l.PeakDbfs, l.ActiveRmsDbfs, decision.GainDb, z.SpeechCutRuns, z.EdgeRuns, outcome);
            else
                Logger.Information(
                    "Audio level: peak={Peak:F1}dBFS activeRms={ActiveRms:F1}dBFS gain={Gain:F1}dB ({Outcome}) (file)",
                    l.PeakDbfs, l.ActiveRmsDbfs, decision.GainDb, outcome);
        }
        else
            // MeasureFile returns null WITHOUT throwing for an unreadable, non-canonical or
            // sub-50 ms file — production-reachable by dropping a short audio note, and previously
            // the one path that emitted nothing at all.
            Logger.Information("Audio level: unmeasurable — normalization skipped ({Outcome}) (file)", outcome);
    }

    /// <summary>
    /// The plain-PCM/IEEE-float equivalent of a WAVE_FORMAT_EXTENSIBLE header, or <c>null</c> when
    /// the format is already standard and nothing needs reinterpreting.
    ///
    /// <para><b>Two review rounds and two wrong versions before this one, both worth recording.</b>
    /// The first routed extensible input to <c>MediaFoundationResampler</c>, which rejects it
    /// outright ("Input must be PCM or IEEE float") — turning a silent no-gain into a hard failure.
    /// The second matched on <c>is WaveFormatExtensible</c>, which NEVER FIRES: NAudio's
    /// <c>WaveFileReader</c> hands back a <c>WaveFormatExtraData</c> for an extensible fmt chunk, so
    /// the branch was dead and the defect untouched — and the test that "proved" it worked was a
    /// false pass, because the injected measure stub cannot see a format tag.</para>
    ///
    /// <para>So the SUB-FORMAT GUID is read from the extension bytes directly (layout: 2 bytes valid
    /// bits, 4 bytes channel mask, 16 bytes GUID). Getting this wrong in the other direction matters
    /// just as much: declaring a 32-bit IEEE-float file as integer PCM would reinterpret float
    /// samples as integers and feed CORRUPTED audio to the transcriber, which is worse than the
    /// silent no-gain being fixed.</para>
    /// </summary>
    internal static WaveFormat? StandardiseExtensible(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible) return null;

        // MONO ONLY. Plain WAVEFORMATEX cannot carry dwChannelMask, and MediaFoundation uses that
        // mask to pick the channel-to-mono mixing matrix — so standardising a multichannel
        // extensible file discards its speaker layout and a non-default arrangement gets downmixed
        // with the wrong coefficients (Codex diff review r5, citing the WAVEFORMATEXTENSIBLE and
        // Audio Resampler DSP docs). Plausible-but-wrong audio is worse than the clean rejection
        // those files got before AUD-9, which declining here preserves exactly.
        //
        // A mono file has nothing to lose: one channel has no layout to get wrong. That is also the
        // only shape the conversion target (16 kHz mono) can pass through untouched, so this costs
        // the feature nothing it was actually delivering.
        if (format.Channels != 1) return null;

        // ONLY the raw-bytes shape, which is what WaveFileReader — this path's actual reader — hands
        // back for an extensible fmt chunk. A typed WaveFormatExtensible branch existed for two
        // rounds and was DEAD CODE; worse, it could not see wValidBitsPerSample, so it could never
        // make the precision check below. An unverifiable branch that only a hypothetical reader
        // reaches is not worth the risk of it being wrong — such a reader now simply gets no gain,
        // which is the pre-AUD-9 behaviour.
        // EXACTLY 22 bytes of extension, read from cbSize — NOT from ExtraData.Length.
        //
        // Microsoft permits cbSize beyond 22 for format-specific data, and this method understands
        // none of it, so accepting more meant silently DISCARDING it and relabelling the file anyway
        // (Codex diff review r7). But the obvious guard is a trap, MEASURED rather than assumed:
        // NAudio hands back a FIXED 100-byte ExtraData buffer regardless of the real cbSize, so
        // `Length >= 22` was never a length check and `Length == 22` can never match. `ExtraSize` is
        // the parsed cbSize and is the only honest source.
        if (format is WaveFormatExtraData extra && format.ExtraSize == 22 && extra.ExtraData is { Length: >= 22 })
        {
            // Extension layout: 2 bytes wValidBitsPerSample, 4 bytes dwChannelMask, 16 bytes SubFormat.
            var validBits = BitConverter.ToInt16(extra.ExtraData, 0);

            // The container may be WIDER than the signal — 20 valid bits inside a 24-bit container is
            // ordinary studio output. Plain WAVEFORMATEX cannot express that distinction at all, so
            // relabelling it claims a precision the data does not have (Codex diff review r6). The
            // FOURTH place this same rule has had to be applied: reinterpreting a header is only safe
            // where the reinterpretation provably carries the same information.
            if (validBits != format.BitsPerSample) return null;

            // Widths plain WaveFormat represents unambiguously. An exotic PCM64 is declined rather
            // than passed to a resampler that would have rejected it anyway.
            if (format.BitsPerSample is not (8 or 16 or 24 or 32)) return null;

            // The header's OWN framing must be the canonical one, because the replacement WaveFormat
            // recomputes BlockAlign and AverageBytesPerSecond from rate/channels/bits. Block align
            // defines frame boundaries, so silently substituting a computed value for a declared one
            // that disagrees would re-frame the samples (Codex diff review r7). A file whose
            // declared framing is unusual — or simply wrong — keeps its extensible header and is
            // skipped, which is the pre-AUD-9 behaviour.
            var expectedBlockAlign = format.Channels * (format.BitsPerSample / 8);
            if (format.BlockAlign != expectedBlockAlign) return null;
            if (format.AverageBytesPerSecond != format.SampleRate * expectedBlockAlign) return null;

            var guidBytes = new byte[16];
            global::System.Array.Copy(extra.ExtraData, 6, guidBytes, 0, 16);
            var subFormat = new Guid(guidBytes);

            // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT — only at 32 bits. `CreateIeeeFloatWaveFormat`
            // hardcodes 32, so a 64-bit float export (an ordinary DAW output) would be relabelled
            // with HALF its true block alignment; the header then passes MediaFoundation's PCM/float
            // guard and each 8-byte double decodes as two floats — silent corruption, where before
            // AUD-9 the same file hit NAudio's guard and produced a clean "check the file format"
            // failure. Declining preserves that clean failure byte for byte (both reviewers, r4).
            if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                return format.BitsPerSample == 32
                    ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
                    : null;

            // KSDATAFORMAT_SUBTYPE_PCM — and anything else is NOT assumed to be PCM: an unknown
            // sub-format returns null, so the file keeps its extensible header, the analyzer
            // declines it, and the gain is skipped. Declining to act beats mislabelling audio.
            if (subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"))
                return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        }

        return null;
    }


    private static WaveStream CreateReader(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => new Mp3FileReader(filePath),
            ".wav" => new WaveFileReader(filePath),
            _ => new MediaFoundationReader(filePath) // Handles M4A, FLAC, etc.
        };
    }

    public static bool IsSupportedFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".wma" or ".aac";
    }
}
