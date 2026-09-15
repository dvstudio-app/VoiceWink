namespace VoiceWink.Helpers;

/// <summary>What a recording's all-zero fact means, once its LENGTH is taken into account.</summary>
internal enum DigitalSilenceOutcome
{
    /// <summary>Not every sample was zero (or the level was not measurable): the ordinary path.</summary>
    NotSilent,
    /// <summary>Every sample was zero, but this recording — together with the consecutive all-zero
    /// recordings before it — is shorter than the evidence bound: a gated microphone can deliver
    /// exact zeros over a short press. Treated as no speech.</summary>
    TooShortToJudge,
    /// <summary>Every sample was zero, and the consecutive all-zero audio reaches the evidence
    /// bound: the capture chain delivered nothing — AUD-21's "No audio captured" and a
    /// standing-capture rebuild.</summary>
    Established,
}

/// <summary>
/// AUD-34 (2026-09-03): the recording-side digital-silence verdict keys on DURATION, the same way
/// the standing stream's own rule does. AUD-21 protected the STREAM rule against a known
/// population — an APO noise gate emits byte-identical exact-zero runs, so a gating endpoint sits
/// at zeros between dictations — with its "ever delivered a non-zero sample, given at least
/// <see cref="StandingCapturePolicy.MinSilenceEvidenceMs"/> to prove it" rule, and gave the
/// RECORDING rule no equivalent guard: any all-zero recording, however short, said "No audio
/// captured" and rebuilt the stream.
///
/// <para><b>Measured on the owner's ARM64 laptop (built-in array, AEC+NS+AGC on):</b> eight
/// all-zero recordings in five days, every one a push-to-talk hold of 630–2182 ms or a brief-tap
/// recording of ~1.2–1.5 s, with normal dictations minutes before AND after on the same stream —
/// the gate closing over a press that ended before it opened, not a latched stream. The owner
/// listened and heard nothing; correct. Under this rule seven of those eight route to the short
/// path; the 2182 ms one sits above the bound and keeps AUD-21's verdict, which is stated rather
/// than tuned away — the bound is the stream rule's own evidence constant, not a number fitted to
/// one day's data, and a recording that long with every sample at zero IS the incident's shape.</para>
///
/// <para><b>Consecutive all-zero recordings ACCUMULATE toward the same bound</b> (self-review,
/// correctness lens). The recording verdict was AUD-21's ONLY catch for a stream that latched
/// silent AFTER carrying audio — the stream's from-birth flag is monotonic, so its own rule cannot
/// see that flavour — and a duration-only bound would have left a user whose presses stay under
/// the bound on a dead stream indefinitely. So the caller passes the zero seconds of the
/// consecutive all-zero recordings before this one: three 0.7 s silent presses are 2.1 s of zero
/// audio, the stream rule's own evidence gathered across presses. A non-silent recording resets
/// the count; an Established verdict resets it too (the rebuild it posts is the fresh start). The
/// cost on a gated microphone is one rebuild after three silent short presses in a row — the
/// outcome AUD-21 gave every one of them.</para>
///
/// <para><b>The bound is shared, not copied.</b> <see cref="StandingCapturePolicy.MinSilenceEvidenceMs"/>
/// already answers "how much zero audio condemns a capture" for the stream; a second constant
/// here would be the same question with a second answer. Pure and total; pinned by
/// <c>DigitalSilenceVerdictTests</c>.</para>
///
/// <para><b>Scope of the zeros proof.</b> Only the FILE measurement's <see cref="WavLevels.IsDigitallySilent"/>
/// is a proof that every sample was zero; the float overload of <see cref="WavLevelAnalyzer"/>
/// clamps and documents that it is not. Every production caller measures the recording file.</para>
/// </summary>
internal static class DigitalSilenceVerdict
{
    /// <summary>The evidence bound, in milliseconds — the stream rule's constant.</summary>
    internal static double MinEvidenceMs => StandingCapturePolicy.MinSilenceEvidenceMs;

    /// <summary>Judge a measured recording. <c>null</c> (unmeasurable) is <see cref="DigitalSilenceOutcome.NotSilent"/>:
    /// not established, the direction that never asserts a capture fault it cannot prove.</summary>
    /// <param name="priorZeroSeconds">The zero seconds accumulated by the CONSECUTIVE all-zero
    /// recordings immediately before this one (0 when the previous recording carried audio).
    /// Never condemns a recording that is not itself all-zero.</param>
    internal static DigitalSilenceOutcome Judge(WavLevels? levels, double priorZeroSeconds = 0.0)
    {
        if (levels is not { IsDigitallySilent: true } l)
        {
            return DigitalSilenceOutcome.NotSilent;
        }
        return (priorZeroSeconds + l.DurationSeconds) * 1000.0 >= MinEvidenceMs
            ? DigitalSilenceOutcome.Established
            : DigitalSilenceOutcome.TooShortToJudge;
    }
}
