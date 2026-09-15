namespace VoiceWink.Helpers;

/// <summary>
/// TRN-15 tail rescue — recovers a final word Parakeet decoded past. The engine occasionally emits
/// its last token well before the speech ends and reports success; the transcript still reads as a
/// finished sentence because the model punctuates whatever it produces, so the loss is invisible.
/// Measured 2026-08-14: capture complete, 0.37 s of trailing silence, gpt-transcribe hears the
/// dropped word on the same WAV, and the shipped decode reproducibly loses it.
///
/// <para><b>Why a TAIL-SCOPED second pass and not a decoder knob.</b> Every global lever was
/// measured and refuted on the owner's 41-file day corpus before this design was chosen: input
/// level has OPPOSITE survival bands on the two confirmed repro files; appended trailing silence
/// is non-monotonic (0.1–0.8 s made one repro WORSE) and recovered nothing across 29 files;
/// modified_beam_search changed neither repro; BlankPenalty 1.5–2.0 fixed both repros and
/// regressed the corpus 11.46% → 15.18% aggregate word-edit with real mid-text corruption. Any
/// global perturbation flips OTHER marginal decisions somewhere. A rescue that re-decodes only the
/// tail and may only APPEND cannot touch the rest of the transcript by construction.</para>
///
/// <para><b>Trigger.</b> sherpa's offline result carries per-token timestamps. When the last
/// active 20 ms frame of the RAW final-chunk audio ends more than <see cref="TriggerThresholdSeconds"/>
/// past the last token's START time, the decode provably stopped before the speech did. Calibrated
/// on 41 recordings under EXACTLY the conventions pinned here (raw buffer, 20 ms frames, fixed
/// −55 dBFS floor, token start): healthy files p50 0.12 / p90 0.26 / max 1.20; the confirmed drop
/// 0.74; ~8% of healthy files fire and their rescues no-op at the anchor. The second confirmed
/// repro sits at 0.12 — INSIDE the healthy band: a final short syllable absorbed into the last
/// token's span is undetectable here and unstable under every rescue window tried. That class is
/// an accepted residual, recorded on the card — fixing it means fixing the model. A second, rarer
/// residual (diff review): on a multi-chunk recording whose final chunk contributed only ONE word,
/// the transcript's final bigram crosses the chunk boundary, cannot occur in the rescue audio, and
/// the merge deterministically no-ops — safety-preserving, never wrong text.</para>
///
/// <para><b>Merge is anchor-gated, append-only, capped.</b> The rescue decode of a short window can
/// legitimately DISAGREE with the main decode — measured, not hypothetical: on the second repro a
/// shorter window decoded a different inflection of the main text's final word. So: find the main
/// text's final bigram in the rescue text (normalized
/// comparison, searched from the end); no anchor → no change; anchor → append at most
/// <see cref="AppendCapWords"/> whitespace-delimited SURFACE words that follow it. Nothing is ever
/// replaced or reordered. Corpus evaluation of the full mechanism: exactly one append in 41 files —
/// the correct one — and zero junk.</para>
///
/// <para><b>Why the ORCHESTRATION lives here and not in the service</b> — the ChunkedDecode
/// precedent, re-raised by the plan review: an orchestration written into the service gets
/// re-implemented by the harness, and the two drift on exactly the calibration conventions pinned
/// above. One copy serves production, CI (via a fake decode closure) and
/// tools/parakeet-long-audio (which links this file).</para>
/// </summary>
internal static class TailRescue
{
    /// <summary>Activity-scan frame length. Part of the pinned calibration surface — the corpus
    /// numbers above are measured under these exact values; change one and they must be re-run.</summary>
    internal const double FrameSeconds = 0.02;

    /// <summary>Fixed frame-RMS activity floor. Deliberately NOT adaptive: a final-chunk slice is
    /// too short to estimate a noise percentile from, and the calibration used this fixed value.</summary>
    internal const double ActivityFloorDbfs = -55;

    /// <summary>Gap (last active frame end − last token start) above which the rescue fires.
    /// Margin: 0.19 s above the healthy p90, 0.29 s below the confirmed drop.</summary>
    internal const double TriggerThresholdSeconds = 0.45;

    /// <summary>Context carried into the rescue decode, measured back from the last active frame.
    /// 6 s of left context decoded the confirmed drop's tail correctly at every window tried
    /// (2–8 s), while a SHORTER window was measured to decode a different inflection of the main
    /// text's final word — the disagreeing-rescue case the anchor guard exists for. The slice runs
    /// from (lastActive − 6 s) to the END of the final chunk, so its true length is 6 s plus
    /// whatever trailing silence follows the speech, bounded by the chunk itself (≤ ~35 s) — the
    /// exact configuration the corpus evaluation validated, kept rather than trimmed.</summary>
    internal const double RescueWindowSeconds = 6;

    /// <summary>A continuation longer than this is misalignment, not a missed tail — refuse it.</summary>
    internal const int AppendCapWords = 4;

    /// <summary>
    /// The whole trigger + rescue + merge, orchestrated in one place. Pure except for
    /// <paramref name="decode"/>, which the caller supplies as a FRESH-stream decode of the gained
    /// rescue slice (never a shared stream — a sherpa stream accumulates its waveform; and never
    /// the service's DecodeOne, whose chunk bookkeeping a rescue must not pollute).
    /// </summary>
    /// <param name="rawFinalChunk">The final chunk's samples BEFORE decode gain — the pinned scan
    /// buffer. The gained slice is recomputed here (scale-then-clip is per-sample, so
    /// slice-then-gain ≡ gain-then-slice).</param>
    /// <param name="gainDb">The recording's whole-file decode gain (TRN-10b), re-applied to the
    /// rescue slice so the second pass sees the same level the first did.</param>
    /// <param name="mainText">The full joined transcript of the main decode.</param>
    /// <param name="lastTokenTimestamps">The FINAL chunk's per-token start times, seconds relative
    /// to that chunk — never borrowed from an earlier chunk. Null/empty ⇒ the final chunk decoded
    /// empty or the model returned no timing ⇒ no rescue (the trigger cannot prove anything).</param>
    internal static TailRescueOutcome TryAppend(
        float[] rawFinalChunk,
        int sampleRate,
        double gainDb,
        string mainText,
        IReadOnlyList<float>? lastTokenTimestamps,
        Func<float[], string> decode,
        CancellationToken ct)
    {
        // No timestamps ⇒ no trigger. Covers: empty final-chunk decode (earlier chunks may have
        // produced the whole mainText), a model/package that stops reporting timing, and the
        // "(durations mismatch)" shape the investigation observed — timing we cannot trust is
        // timing we do not act on.
        if (lastTokenTimestamps is null || lastTokenTimestamps.Count == 0 ||
            rawFinalChunk.Length == 0 || sampleRate <= 0 || string.IsNullOrWhiteSpace(mainText))
        {
            return TailRescueOutcome.NotFired(mainText);
        }

        // A last token STARTING at exactly 0 is the signature of a timing regression (a package
        // that zeroes Timestamps the way this model already zeroes Durations), not of dictation —
        // and acting on it would fire a wasted rescue on every short recording. Refusing costs at
        // most one missed rescue on a legitimately-zero token; the asymmetry decides it. Written
        // as !(x > 0) so NaN refuses too — NaN is the one untrusted value for which every later
        // comparison would fail OPEN (gap=NaN passes both threshold checks); +∞ already fails safe.
        if (!(lastTokenTimestamps[^1] > 0)) return TailRescueOutcome.NotFired(mainText);

        return TryAppendCore(rawFinalChunk, sampleRate, gainDb, mainText, lastTokenTimestamps[^1], decode, ct);
    }

    /// <summary>
    /// TRN-31: the same trigger + rescue + merge, anchored on the final chunk's LAST WORD END
    /// TIME — the parakeet.cpp transport's timing (verbose_json word end times are REAL there,
    /// where sherpa's TDT durations are all-zero and force the sherpa entry onto token STARTS).
    /// The END anchor is the G4-prescribed form: on the 781-file corpus the start-based gap
    /// over-triggers 337/781 by construction (a word's own duration absorbs the gap), while the
    /// end-based gap at the same <see cref="TriggerThresholdSeconds"/> marks 82/781 — the rows
    /// whose truth-set deletions run 3.2× the non-trigger rows'. Same refusal shapes as the
    /// sherpa entry: null timing (the transport did not return words), a non-positive or NaN
    /// end (a timing regression, refused for the same fail-open reason), empty buffer, blank
    /// text.
    ///
    /// <para><b>MEASURED AND REFUSED as the shipped pcpp default — NO app caller</b> (the
    /// <c>ParakeetHotwords</c> precedent; sole caller is <c>tools/parakeet-long-audio</c>'s
    /// `pcpp-rescue` arm). The registered 801-file ON/OFF gate (2026-08-28): 87 triggers fired
    /// exactly as G4 predicted, 86 no-opped at the anchor, 1 append, truth aggregates
    /// byte-identical — a same-model re-decode of the tail reproduces the same ending, so the
    /// trigger rows' excess deletions are not recoverable this way on this engine. Verdict +
    /// reopen condition: the TRN-31 card; evidence
    /// `docs/plans/2026-08-27-trn31-tailrescue-pcpp/`.</para>
    /// </summary>
    /// <param name="lastWordEndSeconds">The final chunk's last word END time, seconds relative
    /// to that chunk — never borrowed from an earlier chunk. Null ⇒ the transport returned no
    /// word timing ⇒ no rescue (the trigger cannot prove anything).</param>
    internal static TailRescueOutcome TryAppendEndAnchored(
        float[] rawFinalChunk,
        int sampleRate,
        double gainDb,
        string mainText,
        double? lastWordEndSeconds,
        Func<float[], string> decode,
        CancellationToken ct)
    {
        if (lastWordEndSeconds is not { } lastEnd ||
            rawFinalChunk.Length == 0 || sampleRate <= 0 || string.IsNullOrWhiteSpace(mainText))
        {
            return TailRescueOutcome.NotFired(mainText);
        }

        // Same !(x > 0) shape as the sherpa entry: NaN and non-positive ends are untrusted
        // timing, and NaN would fail OPEN through every later comparison.
        if (!(lastEnd > 0)) return TailRescueOutcome.NotFired(mainText);

        return TryAppendCore(rawFinalChunk, sampleRate, gainDb, mainText, lastEnd, decode, ct);
    }

    /// <summary>
    /// The shared trigger arithmetic + rescue + merge, over one validated ANCHOR time. The two
    /// entries differ only in what the anchor IS (sherpa: last token START, the only timing that
    /// engine reports; pcpp: last word END) and in how they validate it — the core never
    /// re-validates, so callers must have refused null/NaN/non-positive anchors already.
    /// </summary>
    private static TailRescueOutcome TryAppendCore(
        float[] rawFinalChunk,
        int sampleRate,
        double gainDb,
        string mainText,
        double anchorSeconds,
        Func<float[], string> decode,
        CancellationToken ct)
    {
        var lastActive = LastActiveTime(rawFinalChunk, sampleRate);
        if (lastActive < 0) return TailRescueOutcome.NotFired(mainText);   // no frame above the floor

        var gap = lastActive - anchorSeconds;
        if (gap <= TriggerThresholdSeconds) return TailRescueOutcome.NotFired(mainText);

        // A gap wider than the rescue window means the anchor (the main text's ending) cannot be
        // inside the slice — the decode would be a guaranteed no-op, so skip paying for it. NOT
        // silent: this is the LARGEST loss the trigger can see, and the outcome carries the gap so
        // the caller's instrument reports it instead of filing it under "healthy".
        if (gap > RescueWindowSeconds) return TailRescueOutcome.SkippedBeyondWindow(mainText, gap);

        ct.ThrowIfCancellationRequested();

        // The slice below is non-empty, but not for the reason a quick read suggests: the operative
        // chain is the empty-buffer early return in each entry, LastActiveTime's bound (a
        // non-negative lastActive is at most the buffer's frame-quantised duration), and the
        // Max(0, …) clamp — lastActive ≤ 6 clamps to 0 and takes the WHOLE buffer. That matters:
        // an empty waveform into sherpa is the measured 0xC0000409 process kill, and this
        // arithmetic — not a downstream length check — is what makes it unreachable at every
        // shipped rate.
        var startSample = Math.Max(0, (int)((lastActive - RescueWindowSeconds) * sampleRate));
        var slice = rawFinalChunk.AsSpan(startSample).ToArray();

        var gained = DecodeInputGain.Apply(slice, gainDb, out _);
        var rescueText = decode(gained) ?? string.Empty;

        var merged = Merge(mainText, rescueText);
        return merged.appended == 0
            ? TailRescueOutcome.FiredNoAppend(mainText, gap)
            : new TailRescueOutcome(true, gap, merged.appended, merged.text);
    }

    /// <summary>
    /// End time (seconds) of the last 20 ms frame whose RMS clears the fixed floor; −1 when none
    /// does. Scans the RAW buffer — the calibration's convention, pinned in the constants above.
    /// </summary>
    internal static double LastActiveTime(float[] samples, int sampleRate)
    {
        var frame = (int)(sampleRate * FrameSeconds);
        if (frame <= 0 || samples.Length < frame) return -1;

        var frames = samples.Length / frame;
        for (var i = frames - 1; i >= 0; i--)
        {
            double sum = 0;
            var offset = i * frame;
            for (var j = 0; j < frame; j++)
            {
                double v = samples[offset + j];
                sum += v * v;
            }

            var rms = Math.Sqrt(sum / frame);
            var db = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
            if (db >= ActivityFloorDbfs) return (i + 1) * FrameSeconds;
        }

        return -1;
    }

    /// <summary>
    /// Anchor-gated, append-only merge. Alignment compares NORMALIZED words (lowercase,
    /// letters/digits only); what gets appended are the rescue decode's SURFACE words — whitespace
    /// tokens, never subword units — so casing and punctuation arrive intact ("Renée.").
    /// </summary>
    internal static (string text, int appended) Merge(string mainText, string rescueText)
    {
        var mainSurface = SplitSurface(mainText);
        var rescueSurface = SplitSurface(rescueText);
        if (mainSurface.Length < 2 || rescueSurface.Length < 2) return (mainText, 0);

        var mainNorm = Normalize(mainSurface);
        var rescueNorm = Normalize(rescueSurface);
        if (mainNorm[^1].Length == 0 || mainNorm[^2].Length == 0) return (mainText, 0);

        // The main text's final bigram, searched BACKWARDS through the rescue decode: the ending
        // is what we are extending, and a late match is the one adjacent to whatever follows.
        for (var i = rescueNorm.Length - 2; i >= 0; i--)
        {
            if (rescueNorm[i] != mainNorm[^2] || rescueNorm[i + 1] != mainNorm[^1]) continue;

            var extra = rescueSurface.Length - (i + 2);
            if (extra <= 0 || extra > AppendCapWords) return (mainText, 0);

            var appended = string.Join(" ", rescueSurface[(i + 2)..]);
            return (mainText.TrimEnd() + " " + appended, extra);
        }

        return (mainText, 0);
    }

    // Null separator = all Unicode whitespace, matching the "whitespace tokens" contract above —
    // sherpa joins with plain spaces today, but a doc that promises whitespace must split on it.
    private static string[] SplitSurface(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Normalize(string[] surface)
    {
        var result = new string[surface.Length];
        for (var i = 0; i < surface.Length; i++)
        {
            var chars = new List<char>(surface[i].Length);
            foreach (var c in surface[i])
            {
                if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            }

            result[i] = new string(chars.ToArray());
        }

        return result;
    }
}

/// <summary>What a rescue attempt did — carried back for the service's counts-only log lines.
/// <see cref="GapSeconds"/> is non-zero on exactly two shapes: a fired rescue, and the
/// beyond-window skip (Fired=false, gap>window) — the largest loss the trigger can see, carried
/// out so the caller can WARN instead of filing it under "healthy".</summary>
internal readonly record struct TailRescueOutcome(bool Fired, double GapSeconds, int AppendedWordCount, string Text)
{
    internal static TailRescueOutcome NotFired(string text) => new(false, 0, 0, text);
    internal static TailRescueOutcome SkippedBeyondWindow(string text, double gap) => new(false, gap, 0, text);
    internal static TailRescueOutcome FiredNoAppend(string text, double gap) => new(true, gap, 0, text);
}
