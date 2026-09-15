namespace VoiceWink.Helpers;

/// <summary>What the zero-run scan found in one recording. Only <see cref="SpeechCutRuns"/> and
/// <see cref="EdgeRuns"/> are worth alarm; <see cref="PauseRuns"/> exists so tests can pin that
/// ordinary pausing is classified AWAY rather than merely not counted.</summary>
internal readonly record struct ZeroRunCounts(int SpeechCutRuns, int EdgeRuns, int PauseRuns);

/// <summary>
/// AUD-10 (2026-08-19), the card's own "cheap proxy first": count runs of EXACT digital zeros in a
/// recording, classified by what SURROUNDS them — because the raw percentage is a bad instrument,
/// and this card nearly shipped it. The 2026-08-08 re-scan is the calibration story: a clean 52 s
/// recording scored 24.6% zeros with not one run touching speech (thinking pauses on a gating
/// endpoint), while the same corpus held exactly 2 runs that cut speech on both sides. **A metric
/// that fires on normal pausing is worse than no metric, because it will be believed** — so this
/// reports SPEECH-CUT and EDGE runs, never a percentage.
///
/// <para><b>Classification rule (the hand-scan's, made executable):</b> a run of ≥
/// <see cref="MinRunMs"/> consecutive exact-zero samples, judged by the RMS of the
/// <see cref="SideWindowMs"/> on each side — both sides active ⇒ the gate closed ON SPEECH
/// (speech-cut); exactly one ⇒ it closed against a phrase boundary (edge — the residual worth
/// caring about: a gate clipping word endings); neither ⇒ a pause (benign, the common case).
/// "Active" is the fixed −55 dBFS floor — TailRescue's pinned convention, deliberately not
/// adaptive for the same reason recorded there. A run at the file's very start or end has one
/// side missing; the missing side counts as INACTIVE (a leading/trailing gate close can only ever
/// be an edge, never a speech cut — there is no speech on the far side to have cut).</para>
///
/// <para><b>What the counts can and cannot claim.</b> Two mechanisms produce byte-identical zero
/// runs (the card's own analysis): an endpoint setting <c>AUDCLNT_BUFFERFLAGS_SILENT</c> (NAudio
/// zero-fills those packets) and an APO/driver gate below ≈ −96 dBFS quantizing to exact 0. This
/// scan CANNOT attribute the cause — the flag read that could is the card's deliberately-deferred
/// M–L half — it makes the SYMPTOM visible in every support bundle, which is the whole measured
/// gap (answering one incident took two WAV scans and most of a session). Pure; no I/O — the
/// callers own reading the file.</para>
///
/// <para><b>Input domain: PRE-gain audio, always</b> — both emit sites scan before AUD-2's gain
/// rewrite, the same convention as the no-speech gate ("the gate always judges pre-gain audio").
/// Judged post-gain, a +12 dB boost lifts −62 dBFS gate skirts over the −55 floor and converts
/// ordinary pauses into speech-cuts on exactly the quiet recordings users report mic problems
/// about (both 2026-08-19 self-review lenses, independently). The zero runs themselves are
/// gain-invariant (0 × g = 0); only the side classification moves.</para>
///
/// <para><b>Two honest bounds on "≥ 40 ms".</b> (1) AUD-8's anti-alias filter (48 → 16 kHz path)
/// convolves ~8.3 ms of kernel support across every boundary, so a device-side silent run of
/// L ms lands in the file as ~(L − 8.3) ms of exact zeros — the effective device-side threshold
/// on a 48 kHz endpoint is ~48 ms, while a native-16 kHz endpoint (bit-exact passthrough) is
/// judged at 40 ms. (2) The −55 floor shares TailRescue's VALUE but not its METHOD — TailRescue
/// takes the max over 20 ms frame RMS, this takes mean power over one 200 ms window, so 20 ms of
/// speech inside 200 ms of silence reads active there and inactive here. Both differences bias
/// toward Pause/Edge — under-counting, the safe direction for a metric that gets believed.</para>
/// </summary>
internal static class ZeroRunClassifier
{
    /// <summary>The hand-scan's run floor: shorter zero runs are ordinary inter-sample zero
    /// crossings and dithered silence, not gating (the clean pre-incident device measured ZERO
    /// runs ≥ 40 ms while sitting at 0.1–0.5% raw zeros).</summary>
    internal const int MinRunMs = 40;

    /// <summary>The hand-scan's context window: RMS of this much audio on each side decides what
    /// the run interrupted.</summary>
    internal const int SideWindowMs = 200;

    /// <summary>TailRescue's pinned activity floor, reused deliberately — one calibrated
    /// convention for "is this audio active", not a second constant to drift.</summary>
    internal const double ActivityFloorDbfs = -55;

    internal static ZeroRunCounts Classify(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) return new ZeroRunCounts(0, 0, 0);

        var minRun = (int)(sampleRate * (MinRunMs / 1000.0));
        var side = (int)(sampleRate * (SideWindowMs / 1000.0));
        if (minRun <= 0) return new ZeroRunCounts(0, 0, 0);

        int cut = 0, edge = 0, pause = 0;
        var i = 0;
        while (i < samples.Length)
        {
            if (samples[i] != 0f) { i++; continue; }

            var runStart = i;
            while (i < samples.Length && samples[i] == 0f) i++;
            var runEnd = i;   // exclusive
            if (runEnd - runStart < minRun) continue;

            var leftActive = IsActive(samples, global::System.Math.Max(0, runStart - side), runStart);
            var rightActive = IsActive(samples, runEnd, global::System.Math.Min(samples.Length, runEnd + side));

            if (leftActive && rightActive) cut++;
            else if (leftActive || rightActive) edge++;
            else pause++;
        }

        return new ZeroRunCounts(cut, edge, pause);
    }

    private static bool IsActive(float[] samples, int start, int end)
    {
        // An absent or degenerate side window is INACTIVE by definition — see the summary: a
        // leading/trailing run has no speech on its missing side to have cut.
        if (end - start <= 0) return false;

        double sum = 0;
        for (var j = start; j < end; j++)
        {
            double v = samples[j];
            sum += v * v;
        }

        var rms = global::System.Math.Sqrt(sum / (end - start));
        if (rms <= 0) return false;
        return 20 * global::System.Math.Log10(rms) >= ActivityFloorDbfs;
    }
}
