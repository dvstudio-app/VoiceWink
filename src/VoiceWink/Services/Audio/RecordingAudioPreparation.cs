using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Audio;

/// <summary>
/// What <see cref="RecordingAudioPreparation.PrepareAsync"/> established about a recording.
///
/// <para><see cref="DigitallySilent"/> is AUD-21's signal: the capture produced no audio at all, as
/// distinct from "quiet". <b>False also means NOT ESTABLISHED</b> — measurement failed, this was
/// a retry (which skips normalization entirely and so measures nothing), or the recording was
/// all-zero but too short to judge (AUD-34). That conflation is
/// deliberate and safe in exactly one direction: every consumer must treat false as "say the
/// ordinary thing", never as positive evidence that audio was present.</para>
/// </summary>
public readonly record struct PreparedRecording(bool Blocked, bool DigitallySilent);

/// <summary>
/// AUD-2 (2026-07-31): the single owner of post-recording audio preparation — the no-speech gate
/// verdict and the bounded soft-speech gain, in the ONE order that keeps the gates honest:
///
/// <para><b>raw gate → normalize → caller acts on the verdict.</b></para>
///
/// The gate delegate (VAD + the −60 dB RMS fallback) always evaluates the ORIGINAL bytes —
/// normalizing first would boost stationary noise past the RMS fallback (Codex plan review R1's
/// blocking find). Normalization runs on fresh recordings only; a blocked-and-retained WAV is
/// normalized BEFORE retention, so the user's "I really spoke" retry replays byte-identical
/// boosted audio, and the provider/debug-retention/ledger all see one canonical file whose path
/// never changes.
///
/// <para><b>Exception contract (Codex R2, verbatim):</b> the gate delegate runs OUTSIDE the
/// fail-soft scope — a throwing gate propagates exactly as it did before this class existed.
/// EVERY <see cref="global::System.OperationCanceledException"/> propagates (no token filtering —
/// the caller's catch distinguishes caller-cancel from timeout by token state). Only
/// analyzer/applier NON-cancellation failures are fail-soft: original file untouched, one
/// Warning, and the already-obtained verdict is still returned. DSP + IO run off the caller's
/// (UI) thread via Task.Run.</para>
///
/// Lives outside MainViewModel per the launch-freeze hub rule; the hub keeps one call.
/// </summary>
public sealed class RecordingAudioPreparation
{
    private static ILogger Logger => Log.ForContext<RecordingAudioPreparation>();

    private readonly global::System.Func<string, WavLevels?> _measure;
    private readonly global::System.Func<string, double, global::System.Threading.CancellationToken, global::System.Action<string>?, bool> _applyGain;
    private readonly global::System.Func<bool> _keepRawForDebug;
    private readonly global::System.Func<string?> _resolveDebugDir;
    private readonly global::System.Action<string, string> _copyFile;

    /// <summary>Real analyzer + applier with raw-sibling retention OFF — the pre-REL-23 shape.
    /// Tests use it for the AUD-2 contract rows; DI never picks it: MS.DI selects the longest
    /// resolvable constructor PROVIDED it dominates every other resolvable one — the empty
    /// parameter set is a subset of any set, so (SettingsService) dominates and no ambiguity
    /// exists. Pinned by the DI construction test, which also asserts the toggle is WIRED (a
    /// silent fallback to this ctor would disable retention with no exception and no log).</summary>
    public RecordingAudioPreparation() : this(WavLevelAnalyzer.MeasureFile, WavGainApplier.ApplyGain)
    {
    }

    public RecordingAudioPreparation(System.SettingsService settings)
        : this(
            WavLevelAnalyzer.MeasureFile,
            WavGainApplier.ApplyGain,
            // Read LIVE per recording — the Settings toggle applies immediately, like every
            // other AppDefaults preference (the WindowMinimizePolicy pattern).
            () => settings.GetBoolDefaulted(AppDefaults.KeepRecordingsForDebug),
            ResolveDebugDirGuarded,
            (from, to) => File.Copy(from, to, overwrite: true))
    {
    }

    internal RecordingAudioPreparation(
        global::System.Func<string, WavLevels?> measure,
        global::System.Func<string, double, global::System.Threading.CancellationToken, global::System.Action<string>?, bool> applyGain,
        global::System.Func<bool>? keepRawForDebug = null,
        global::System.Func<string?>? resolveDebugDir = null,
        global::System.Action<string, string>? copyFile = null,
        global::System.Func<string, ZeroRunCounts?>? scanZeroRuns = null)
    {
        _measure = measure;
        _applyGain = applyGain;
        _keepRawForDebug = keepRawForDebug ?? (static () => false);
        _resolveDebugDir = resolveDebugDir ?? (static () => null);
        _copyFile = copyFile ?? (static (_, _) => { });
        _scanZeroRuns = scanZeroRuns ?? ZeroRunScan.TryScan;
    }

    /// <summary>AUD-34: the zero seconds accumulated by the CONSECUTIVE all-zero recordings so far —
    /// the evidence a run of short silent presses gathers toward <see cref="DigitalSilenceVerdict.MinEvidenceMs"/>
    /// (why: the class doc of <see cref="DigitalSilenceVerdict"/>). Reset by a recording that carried
    /// audio and by an Established verdict; untouched by a retry or an unmeasurable file, which
    /// learn nothing either way. Per instance because this class is the DI singleton the pipeline
    /// calls once per recording, and recordings never overlap (the stop path is single-flight), so
    /// a plain field is the right shape.</summary>
    private double _consecutiveZeroSeconds;

    // AUD-10: the zero-run scan seam (shared adapter: ZeroRunScan). A SECOND read of the WAV,
    // deliberately — extending WavLevelAnalyzer would touch a parity-pinned instrument for a
    // diagnostic. Null means the Audio level line simply omits the two tokens (a diagnostic must
    // never cost a transcription or the instrument line itself). The call site owns totality —
    // an injected seam that throws must not abort the gain flow.
    private readonly global::System.Func<string, ZeroRunCounts?> _scanZeroRuns;

    /// <summary>
    /// REL-23: the guarded production resolver for the raw-sibling copy target. Returns null —
    /// which SKIPS the copy — when the Debug dir cannot be created or is a reparse point. The
    /// junction check is the same write-side guard every other Debug writer carries (REL-17 diff
    /// round 5): a junctioned Debug dir would route the RAW microphone signal outside the app
    /// root, where the 7-day sweep refuses the directory and GDPR erasure deletes the junction
    /// without traversing it — erasure would report success while raw dictation audio persisted
    /// outside every retention surface.
    /// </summary>
    private static string? ResolveDebugDirGuarded()
    {
        try
        {
            var dir = AppPaths.EnsureRecordingsDebug();
            return VerifiedFileAccess.IsReparsePointOrUnreadable(dir) ? null : dir;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the no-speech gate's verdict plus AUD-21's digital-silence fact. See the
    /// class doc for the ordering and exception contract, and <see cref="PreparedRecording"/> for
    /// what a false <c>DigitallySilent</c> does and does not claim.</summary>
    public async Task<PreparedRecording> PrepareAsync(
        string recordingPath,
        bool isRetry,
        global::System.Func<Task<bool>> evaluateNoSpeechGate,
        global::System.Threading.CancellationToken ct)
    {
        // 1) Verdict from the RAW file. Outside the fail-soft scope on purpose.
        var blocked = await evaluateNoSpeechGate().ConfigureAwait(false);

        // 2) Fresh recordings only — a retry replays audio normalized by its original attempt.
        //    AUD-21 rides the measurement this already performs; a retry therefore reports
        //    DigitallySilent=false meaning "not established", per PreparedRecording's contract.
        var digitallySilent = false;
        if (!isRetry)
            digitallySilent = await Task.Run(() => NormalizeFailSoft(recordingPath, ct), global::System.Threading.CancellationToken.None)
                .ConfigureAwait(false);

        // 3) The verdict was obtained before any mutation; the caller acts on it last.
        return new PreparedRecording(blocked, digitallySilent);
    }

    /// <summary>Returns AUD-21's digital-silence fact (false when it could not be established —
    /// including AUD-34's all-zero-but-too-short case). Every other responsibility is unchanged.</summary>
    private bool NormalizeFailSoft(string recordingPath, global::System.Threading.CancellationToken ct)
    {
        // Every non-cancellation path below emits EXACTLY ONE final Information line (the
        // AUD-2 remote-verification instrument) — including thrown applier failures, which
        // must surface as "ApplyFailed — original kept", never vanish into a Warning alone
        // (Codex diff review). OCE always propagates unchanged.
        WavLevels? levels;
        GainDecision decision;
        try
        {
            levels = _measure(recordingPath);
            decision = AudioGainPolicy.Decide(levels);
        }
        catch (global::System.OperationCanceledException)
        {
            throw;
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning("Audio measurement failed — proceeding with the original recording: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
            Logger.Information("Audio level: unmeasurable — normalization skipped");
            return false; // unmeasurable — NOT established, per PreparedRecording's contract
        }

        // AUD-10: scan BEFORE any gain rewrite — the classifier judges PRE-gain audio, the same
        // convention as the no-speech gate (audio-capture.md: "the gate always judges pre-gain
        // audio"). Both self-review lenses independently found the post-apply placement wrong: a
        // +12 dB boost lifts −62 dBFS gate skirts over the −55 floor and converts ordinary pauses
        // into zeroCut on exactly the quiet recordings a user reports mic problems about — the
        // card's "fires on normal pausing gets believed" failure, re-created through the gain
        // layer. Pre-gain also makes the counts comparable across outcomes (a Skipped* file and
        // an Applied file are judged in the same domain). Try/catch here, not only in the
        // adapter: the call site owns seam totality (RetainRawSiblingFailSoft's precedent), and
        // OCE propagates per this method's contract.
        ZeroRunCounts? zeroRuns;
        try
        {
            zeroRuns = _scanZeroRuns(recordingPath);
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
            // REL-23: the gain rewrite below is the moment the only copy of the pre-gain bytes
            // dies (WavGainApplier's temp + move). When the debug toggle is on, a raw sibling is
            // copied into Recordings\Debug FIRST — before the apply, never after, because a
            // post-apply copy would retain the gained file under a name that promises raw. The
            // cancellation check precedes the copy for the same reason WavGainApplier checks
            // before its read: an already-cancelled caller must not pay for full-file IO.
            try
            {
                // RETENTION CAN NEVER COST THE BOOST — by construction, not by retry (two Codex
                // diff rounds): the raw-sibling copy runs INSIDE the applier's protocol, after
                // the gained temp is fully written and before the move destroys the pre-gain
                // bytes. The temp write competes for disk exactly as it did before REL-23; a
                // failed sibling copy (space, AV hold, junctioned Debug dir) skips retention
                // inside the fail-soft hook and the gain proceeds untouched.
                applied = _applyGain(recordingPath, decision.GainDb, ct,
                    path => RetainRawSiblingFailSoft(path, ct));
            }
            catch (global::System.OperationCanceledException)
            {
                throw; // every cancellation propagates — the pipeline's handler owns classification
            }
            catch (global::System.Exception ex)
            {
                // Fail-soft: a normalization problem must never cost a dictation. The applier's
                // temp+move protocol guarantees the original file is still the current one.
                Logger.Warning("Audio normalization failed — proceeding with the original recording: {ErrorType}: {ErrorMessage}",
                    ex.GetType().Name, ex.Message);
            }
        }

        var outcome = decision.Reason switch
        {
            GainReason.Applied or GainReason.HeadroomLimited when !applied => "ApplyFailed — original kept",
            var r => r.ToString(),
        };
        if (levels is { } l)
        {
            // AUD-10 cheap proxy: SPEECH-CUT and EDGE zero runs on the same instrument line, so a
            // gating endpoint is visible from a support bundle without a WAV scan. Classified,
            // never a raw percentage — the 2026-08-08 re-scan proved a clean recording reads 24.6%
            // zeros from ordinary pausing, and a metric that fires on pausing gets believed.
            if (zeroRuns is { } z)
                Logger.Information(
                    "Audio level: peak={Peak:F1}dBFS activeRms={ActiveRms:F1}dBFS gain={Gain:F1}dB zeroCut={ZeroCut} zeroEdge={ZeroEdge} ({Outcome})",
                    l.PeakDbfs, l.ActiveRmsDbfs, decision.GainDb, z.SpeechCutRuns, z.EdgeRuns, outcome);
            else
                Logger.Information(
                    "Audio level: peak={Peak:F1}dBFS activeRms={ActiveRms:F1}dBFS gain={Gain:F1}dB ({Outcome})",
                    l.PeakDbfs, l.ActiveRmsDbfs, decision.GainDb, outcome);
        }
        else
            Logger.Information("Audio level: unmeasurable — normalization skipped");

        // AUD-21: a recording of nothing but exact digital zeros. WARNING, not Information: the
        // file sink's floor is Information, so an Information line would be one more row among
        // thousands — and this is the line whose absence turned the 2026-08-25 incident into a
        // whole session of investigation (the app recorded 20 s of silence twice, blamed the user's
        // voice, and left no greppable marker that its own capture had produced nothing). Numbers
        // only, no content. The gain policy has already logged its own line above; this one names
        // the FAULT rather than the level.
        //
        // Ordering needs no guard of its own (Kimi plan review): an all-zero file measures
        // -120/-120, which AudioGainPolicy resolves to SkippedDigitalSilence, so the applier never runs
        // on it and this fact is always decided before any mutation.
        //
        // AUD-34: the verdict keys on DURATION — the stream rule's own evidence bound. A gated
        // microphone delivers exact zeros over a press that ends before the gate opens (the
        // owner's ARM64 laptop: eight all-zero recordings in five days, every one under ~2.2 s,
        // normal dictations minutes either side on the same stream), and calling that "No audio
        // captured" plus rebuilding a healthy stream was wrong twice. Short-and-zero is now
        // "too short to judge": today's "No speech detected" path, no rebuild, one Information
        // line so a bundle still shows the shape. Consecutive all-zero recordings ACCUMULATE
        // toward the bound, so a stream that latched silent after carrying audio — the flavour
        // only this verdict can see — still heals for a user whose presses stay short.
        var priorZeroSeconds = _consecutiveZeroSeconds;
        switch (DigitalSilenceVerdict.Judge(levels, priorZeroSeconds))
        {
            case DigitalSilenceOutcome.Established:
                _consecutiveZeroSeconds = 0.0; // the rebuild this posts is the fresh start
                Logger.Warning(
                    "Capture produced no audio: every sample is digital zero (peak={Peak:F1}dBFS over {Seconds:F2}s, after {PriorSeconds:F2}s of consecutive all-zero recordings). The microphone or its capture stream delivered silence — this is not a quiet recording.",
                    levels!.Value.PeakDbfs, levels.Value.DurationSeconds, priorZeroSeconds);
                return true;
            case DigitalSilenceOutcome.TooShortToJudge:
                _consecutiveZeroSeconds = priorZeroSeconds + levels!.Value.DurationSeconds;
                Logger.Information(
                    "Capture produced only digital zeros for {Ms:F0} ms ({TotalMs:F0} ms across consecutive all-zero recordings) — shorter than the {MinMs:F0} ms needed to judge the stream (a gated microphone delivers exact zeros over a short press); treated as no speech, no rebuild (AUD-34)",
                    levels.Value.DurationSeconds * 1000.0, _consecutiveZeroSeconds * 1000.0, DigitalSilenceVerdict.MinEvidenceMs);
                return false;
            default:
                // Audio was present: the run of silent presses is over. An UNMEASURABLE file
                // (MeasureFile swallows an AV hold to null; a sub-50 ms file measures null) learned
                // nothing either way and carries the count — resetting there wiped the evidence a
                // dead stream had already gathered (Kimi diff r1 Blocker).
                if (levels is not null)
                    _consecutiveZeroSeconds = 0.0;
                return false;
        }
    }

    /// <summary>
    /// REL-23: the raw sibling's DISCARD rule — called from the pipeline's Delete disposition so
    /// "an explicit user cancel discards" (and a toggle-off delete) covers the pre-gain copy too,
    /// not just the gained WAV. Static and path-derived: the sibling's location is a pure function
    /// of the recording path, and the delete must work whether or not this instance created it.
    /// Fail-soft; a lingering sibling falls to the 7-day Debug sweep.
    /// </summary>
    internal static void DeleteRawSiblingFor(string recordingPath)
        => DeleteRawSiblingFor(recordingPath, AppPaths.RecordingsDebugDir);

    // Dir-parameterized for tests (the suite's standing rule: injectable temp roots, never the
    // real profile). Production always passes AppPaths.RecordingsDebugDir via the overload above.
    internal static void DeleteRawSiblingFor(string recordingPath, string debugDir)
    {
        try
        {
            // Destructive paths never trust lexical paths (the sweep's rule, both diff
            // reviewers): a junctioned Debug dir is refused outright, and the sibling's
            // kernel-resolved path must land under the Debug root before deletion — otherwise a
            // Delete disposition could remove an external file through the junction.
            if (VerifiedFileAccess.IsReparsePointOrUnreadable(debugDir)) return;

            var sibling = Path.Combine(
                debugDir,
                Path.GetFileNameWithoutExtension(recordingPath) + ".raw.wav");
            if (!File.Exists(sibling)) return;
            if (!VerifiedFileAccess.IsVerifiedUnder(sibling, debugDir)) return;

            File.Delete(sibling);
            Logger.Information("Raw debug sibling deleted with its recording: {FileName}", Path.GetFileName(sibling));
        }
        catch (global::System.Exception ex)
        {
            Logger.Debug(ex, "Raw debug sibling delete failed — the 7-day sweep is the backstop");
        }
    }

    /// <summary>
    /// REL-23 (2026-08-15): retain the pre-gain capture as <c>&lt;stem&gt;.raw.wav</c> in
    /// <c>Recordings\Debug</c> when the keep-recordings debug toggle is on. Runs as
    /// <see cref="WavGainApplier.ApplyGain"/>'s <c>beforeReplace</c> hook — after the gained temp
    /// is written, before the move destroys the pre-gain bytes — so retention structurally cannot
    /// compete with the boost for disk (two Codex diff rounds killed the pre-apply-copy shape:
    /// with free space between S and 2S the copy starved the temp write, and no retry can promise
    /// to free space an AV hold pins). Debug-resident from birth — never in the recordings root —
    /// so it inherits the 7-day sweep, GDPR erasure, the export media opt-in and the
    /// support-bundle candidate set with zero lifecycle coupling; the gained main WAV joins it at
    /// end-of-life under REL-17's existing move and the stem reunites the pair. OWNS its
    /// failures: a hook throw would abort a gain whose temp already succeeded, so every non-OCE
    /// exception is swallowed to a Warning; OCE propagates (the class cancellation contract —
    /// the applier treats it as cancel-before-move, original kept).
    /// </summary>
    private void RetainRawSiblingFailSoft(string recordingPath, global::System.Threading.CancellationToken ct)
    {
        try
        {
            if (!_keepRawForDebug()) return;
        }
        catch (global::System.OperationCanceledException)
        {
            throw; // contract purity: EVERY OCE propagates, whatever delegate raised it
        }
        catch (global::System.Exception ex)
        {
            // A throwing toggle read must not cost the dictation. (SettingsService is total
            // today — this guards the hook's no-throw promise, not a reachable failure.)
            Logger.Warning("Raw-capture retention toggle read failed — retention skipped: {ErrorType}", ex.GetType().Name);
            return;
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            var debugDir = _resolveDebugDir();
            if (debugDir is null)
            {
                Logger.Warning("Raw-capture retention skipped — Debug directory unavailable or a reparse point");
                return;
            }

            var target = Path.Combine(debugDir, Path.GetFileNameWithoutExtension(recordingPath) + ".raw.wav");
            _copyFile(recordingPath, target);
            Logger.Information("Raw capture retained for debugging: {FileName}", Path.GetFileName(target));
        }
        catch (global::System.OperationCanceledException)
        {
            throw;
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning("Raw-capture retention failed — proceeding: {ErrorType}: {ErrorMessage}",
                ex.GetType().Name, ex.Message);
        }
    }
}
