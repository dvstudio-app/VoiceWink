namespace VoiceWink.Helpers;

/// <summary>
/// The ONE place that says how the local Whisper decoder is configured, and the shipped values.
///
/// <para><b>Why this exists.</b> The settings used to be three inline calls in
/// <c>WhisperTranscriptionService.BuildProcessor</c>, which made two things impossible: seeing at a
/// glance what we send whisper.cpp versus what it defaults to, and MEASURING an alternative without
/// editing shipped code. On 2026-08-31 the owner asked why Turbo scored worse than Medium; answering
/// it needed exactly that comparison, and the only way to run one was a throwaway processor built
/// beside the app's — which measures something the app does not do, the failure the harness rule
/// exists to prevent.</para>
///
/// <para><b>Unset means whisper.cpp's own default, with ONE exception, and both halves are
/// load-bearing.</b> whisper.net calls <c>whisper_full_default_params</c> and then SKIPS any option
/// we leave null, so a null here is not "off" — it is upstream's value; by that code fact (verified
/// against whisper.net 1.9.1's nullable options, not a runtime measurement) <c>temperature_inc</c>
/// 0.2, <c>entropy_thold</c> 2.4 and <c>logprob_thold</c> -1.0 are already active because we never
/// touch them. An earlier note in this project claimed the anti-loop machinery was absent; it was
/// not, and three of four knobs tried against it were no-ops. The exception is <c>Threads</c>:
/// <c>BuildProcessor</c> always calls <c>WithThreads(ResolveThreads())</c>, so a null there means
/// "apply the formula below" — whisper.net's own thread default is unreachable from the app.</para>
///
/// <para><b>Provenance of each shipped value — all three are VoiceInk emulation, each with its own
/// basis</b> (the per-member docs carry the detail). <c>NoContext = true</c> follows upstream
/// VoiceInk (<c>LibWhisper.swift</c>, present since its initial open-sourcing, no rationale recorded
/// there); note TRN-44 measured that it does NOT alone fix the B101 repetition — it was adopted as
/// upstream emulation, cheap for dictation where most recordings are one 30-second window.
/// <c>Temperature = 0.2</c> is the change that killed the measured repetition loop, adopted by
/// owner decision over this session's determinism objection — the member doc records both.
/// <c>Threads</c> null resolves to VoiceInk's <c>min(8, ProcessorCount − 2)</c>, adopted on
/// TRN-46's measured sweep; the old <c>ProcessorCount / 2</c> is retired. On big CPUs the two
/// formulas cross: for 18+ logical processors the new formula gives FEWER threads than the old one
/// (a 32-thread part: 8 vs 16) — the cap is VoiceInk's, unmeasured here, and the fleet question
/// stays open on TRN-46.</para>
/// </summary>
internal sealed record WhisperDecodeSettings
{
    /// <summary>Do not use the previous window's transcription as the decoder's prompt.</summary>
    public bool NoContext { get; init; } = true;

    /// <summary>
    /// Initial decoding temperature. <b>0.2, following upstream VoiceInk</b> — whisper.cpp's own
    /// default is 0.0 (fully greedy), and 0.0 is what makes a repetition loop a fixed point: once the
    /// most likely continuation is "say that sentence again", a deterministic decoder takes it with
    /// certainty and stays there. A small temperature gives it a way out.
    ///
    /// <para><b>Measured before adopting, on the clip that prompted it (B101, TRN-43):</b> at 0.0
    /// the decode invents a lead-in and emits it twice; at 0.2 both the invention and the
    /// duplication are gone, and four consecutive re-decodes of the clip produced byte-identical
    /// text. Those per-clip observations come from the harness's console-only <c>transcribe</c>
    /// replays (transcripts never enter the repo, so they are not recomputable from committed
    /// files); what IS committed: B101's error count against the owner's reference falls 49 → 18,
    /// level with Medium (<c>results/2026-08-31-ggml-large-v3-turbo-q8_0~label-temp02a.json</c> vs
    /// the temp-0 run), and the whole 56-clip corpus improves 19.3% → 18.2% WER. Scope of the
    /// determinism evidence: each harness run is a FRESH process; the app caches one processor per
    /// session, whisper.cpp's sampler state lives with it, and per-session repeat-determinism was
    /// not measured — do not quote "byte-identical" as an app-level guarantee.</para>
    ///
    /// <para><b>The objection this overrode, recorded because it was mine and it was wrong.</b> I held
    /// this back on the grounds that a non-zero temperature is formally non-deterministic and so makes
    /// this project's own WER measurements irreproducible — the instrument that sets every accuracy
    /// star. The owner overruled it (2026-08-31) and the priority was the right way round: <i>"what is
    /// more important, better results in practice or a perfect measuring tool?"</i> A configuration
    /// that demonstrably produces better text for a user wins over one that is tidier to measure. The
    /// measurement serves the product.</para>
    /// </summary>
    public float? Temperature { get; init; } = 0.2f;

    /// <summary>
    /// Decode threads. NULL resolves to <b>VoiceInk's formula, <c>min(8, ProcessorCount − 2)</c></b> —
    /// adopted 2026-08-31 on a MEASUREMENT, not on precedent alone, and the measurement refuted the
    /// hypothesis that held it back: on the owner's 4 P-core + 4 E-core machine (no HT), 6 threads
    /// decodes <b>12% faster than 4</b> (13.53× → 15.20× real-time on the TRN-46 sweep, thermal
    /// control passing at 0.7%), flat from 6 to 8, with identical per-clip char counts at every
    /// count (speed mode stores no text, so char counts are the committed evidence). The
    /// E-core-drag argument for capping at 4 was wrong here. The old <c>ProcessorCount / 2</c> gave
    /// 4 on this machine and 2 on a 4-core laptop — under the engine's own default on the machines
    /// least able to spare it. Small-machine end (hw&lt;6) is still unmeasured; the fleet question
    /// stays open on TRN-46 pending the owner's gaming PC as a second point.
    /// </summary>
    public int? Threads { get; init; }

    /// <summary>What the app ships. A test pins these, so a change here is a change on purpose.</summary>
    public static WhisperDecodeSettings Shipped { get; } = new();

    /// <summary>
    /// The measurement seam, and the whole reason this type is not a set of constants.
    /// <b>Only <c>VoiceWink.AsrBench</c> ever assigns it</b> — the app reads it and never writes it,
    /// so a harness can vary the decode without a parallel processor that measures the wrong thing.
    /// Defaults to <see cref="Shipped"/>, so an app run and an unmodified harness run are identical.
    /// Latent trap for a future settings-driven knob: <c>BuildProcessor</c> reads this at
    /// processor-BUILD time and the service caches processors per model/language/prompt, so a
    /// mid-session change takes effect only at the next rebuild — fine today (nothing writes it
    /// mid-session anywhere), wrong the day a knob becomes a live setting.
    /// </summary>
    internal static WhisperDecodeSettings Active { get; set; } = Shipped;

    public int ResolveThreads() => Threads ?? ResolveThreads(System.Environment.ProcessorCount);

    /// <summary>The formula alone, seam-tested: CI runners have few cores, so a test that restates
    /// the instance expression cannot catch a silent revert (both sides agree at 2 and 4 logical
    /// processors — exactly what hosted runners have). The case table pins it across the range.</summary>
    internal static int ResolveThreads(int processorCount) =>
        System.Math.Max(1, System.Math.Min(8, processorCount - 2));
}
