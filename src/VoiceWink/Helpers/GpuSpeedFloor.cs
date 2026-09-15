namespace VoiceWink.Helpers;

/// <summary>TRN-64 PR 2: what the two timed clip decodes concluded. <see cref="NotMeasured"/> is
/// "no verdict" (no samples, a sample decode failed, or the processor was gone) and changes
/// nothing; <see cref="Cleared"/> lets a correctness Pass be recorded; <see cref="Slower"/>
/// pins the engine to the CPU like a Fail. Public only so xUnit theory rows can take it as a
/// parameter (the GpuSelfTestPhase rule); nothing outside this assembly consumes it.</summary>
public enum GpuSpeedFloorVerdict
{
    NotMeasured,
    Cleared,
    Slower,
}

/// <summary>
/// TRN-64 PR 2 (2026-09-04): the speed FLOOR a warmed GPU decode must clear — the pure half of
/// "GPU acceleration is a verified claim". After the compile decode has been judged for
/// correctness, the clip is decoded twice more and each decode is timed; the engine is
/// <see cref="GpuSelfTestOutcome.Slower"/> when the FASTER of the two still took longer than
/// <see cref="Multiplier"/> × the audio the engine ACTUALLY DECODES — Parakeet's 4 s shape → 8 s;
/// Whisper's 30 s encoder window → 60 s, because whisper.cpp pads every input to that window
/// (<see cref="WhisperWindowSeconds"/>), so the 2.85 s clip is not the work done. The first cut
/// took the clip's own length (a 5.7 s floor) and would have demanded the whole window in 5.7 s:
/// a common Intel iGPU running Large V3 Turbo, still several times faster than its own CPU, would
/// have been pinned to the CPU — the design's stated non-goal (self-review, fail-open lens).
///
/// <para><b>Why two samples, and the minimum.</b> One sample can be a scheduler hiccup or a page
/// fault; the minimum of two says "even the better run could not keep up". Healthy GPUs decode the
/// clip in 0.15–1.0 s (the Arc 140V and an RTX 3080, measured); the one slow class ever measured,
/// the Adreno X1-85, ran at ~0.08–0.125× real time (23–36 s on the clip). Parakeet's 8 s floor
/// sits ~50× from the healthy population and well inside the Adreno's pace; Whisper's 60 s floor
/// sits ABOVE the Adreno's clip time — that card fails on the WORDS first — so the Whisper floor
/// catches only the pathological class (a CPU-emulated Vulkan device, a driver that returns
/// correct text at a crawl). It deliberately does NOT catch a GPU that is merely moderately slower
/// than its CPU: no such machine has been observed, every sample is logged with its rate so the
/// field can show one, and the dropped GPU-vs-CPU A/B returns if it does (the re-open trigger on
/// the TRN-64 card; `docs/plans/2026-09-04-trn64-gpu-self-verifying/50-decision.md`).</para>
///
/// <para><b>Each sample is CAPPED at the floor</b> (Kimi r2 C1): a decode that reaches the floor is
/// cut and counts as over it — the <see cref="Capped"/> sentinel here — so a slow machine's verdict costs at most
/// 2 × floor (16 s for Parakeet; 120 s for Whisper, the pathological class only — a healthy Whisper
/// sample is under 2 s) instead of two full slow decodes, and Parakeet's lands inside the 120 s
/// startup grace with room to spare, before the 35 s shape is compiled.</para>
///
/// <para>Parakeet's floor is judged at the 4 s shape while the transport's 0.3 s edge pads make the
/// decoded audio 4.6 s — ~15 % inside the 2× margin, noted rather than corrected (self-review).</para>
///
/// <para>Pure: no clock, no I/O. The callers own the decodes, the stopwatch and the cap.</para>
/// </summary>
internal static class GpuSpeedFloor
{
    /// <summary>The floor is this many times the audio's own duration.</summary>
    internal const double Multiplier = 2.0;

    /// <summary>How many timed samples the verdict takes.</summary>
    internal const int SampleCount = 2;

    /// <summary>The encoder window whisper.cpp pads every input to. Whisper's floor is over THIS,
    /// not the clip: the encoder does the same work for a 2.85 s clip as for 30 s of speech
    /// (<c>GpuSelfTestClip</c> and <c>WhisperTranscriptionService</c> both record the padding).</summary>
    internal const int WhisperWindowSeconds = 30;

    /// <summary>The Whisper floor's work in samples — <see cref="WhisperWindowSeconds"/> at the clip rate.</summary>
    internal const int WhisperWindowSamples = WhisperWindowSeconds * GpuSelfTestClip.SampleRate;

    /// <summary>The floor PHASE's own deadline — <see cref="SampleCount"/> capped samples plus 30 s
    /// for the calls around them. The Whisper run claims the verdict slot before the floor, so the
    /// watchdog can no longer record for a sample that wedges; a phase that outlives this bound is
    /// recorded <see cref="GpuSelfTestOutcome.Inconclusive"/> by the run itself (self-review,
    /// concurrency lens).</summary>
    internal static TimeSpan PhaseBudget(TimeSpan floor) => floor * SampleCount + TimeSpan.FromSeconds(30);

    /// <summary>The value a sample takes when its decode was cut at the floor. A sentinel on a plain
    /// <see cref="TimeSpan"/> rather than a <c>TimeSpan?</c>: a nullable struct inside a generic
    /// interface (<c>IReadOnlyList&lt;TimeSpan?&gt;</c>) makes CsWinRT's generic-instantiation
    /// generator emit invalid code and fails the app's XAML compile pass (measured, 2026-09-04).</summary>
    internal static readonly TimeSpan Capped = TimeSpan.MaxValue;

    /// <summary>The floor for a clip of <paramref name="audioSamples"/> samples at
    /// <paramref name="sampleRate"/> Hz; <see cref="TimeSpan.Zero"/> for a clip with no audio,
    /// which <see cref="Judge"/> reads as nothing to measure.</summary>
    internal static TimeSpan FloorFor(int audioSamples, int sampleRate = GpuSelfTestClip.SampleRate)
    {
        if (audioSamples <= 0 || sampleRate <= 0)
        {
            return TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(Multiplier * audioSamples / sampleRate);
    }

    /// <summary>The verdict over the timed samples — a capped sample is <see cref="Capped"/>.
    /// <see cref="GpuSpeedFloorVerdict.Slower"/> when NO sample cleared the floor (all capped or all
    /// over it, i.e. the minimum is over it); <see cref="GpuSpeedFloorVerdict.Cleared"/> when at
    /// least one did; <see cref="GpuSpeedFloorVerdict.NotMeasured"/> for no samples or a zero floor.</summary>
    internal static GpuSpeedFloorVerdict Judge(ReadOnlySpan<TimeSpan> samples, TimeSpan floor)
    {
        if (samples.Length == 0 || floor <= TimeSpan.Zero)
        {
            return GpuSpeedFloorVerdict.NotMeasured;
        }
        foreach (var sample in samples)
        {
            if (sample != Capped && sample <= floor)
            {
                return GpuSpeedFloorVerdict.Cleared;
            }
        }
        return GpuSpeedFloorVerdict.Slower;
    }

    /// <summary>One sample for a log line: "412 ms (9.7x real time)" or "capped at 8000 ms" (Parakeet's
    /// 4 s shape) — the rate is audio seconds decoded per wall second, over the work the engine
    /// actually did, never a per-PC claim the row would show.</summary>
    internal static string Describe(TimeSpan sample, int audioSamples, TimeSpan floor, int sampleRate = GpuSelfTestClip.SampleRate)
    {
        if (sample == Capped)
        {
            return $"capped at {(int)floor.TotalMilliseconds} ms";
        }
        var audioSeconds = sampleRate > 0 ? (double)audioSamples / sampleRate : 0;
        var rate = sample.TotalSeconds > 0 && audioSeconds > 0 ? audioSeconds / sample.TotalSeconds : 0;
        return $"{(int)sample.TotalMilliseconds} ms ({rate:F1}x real time)";
    }
}
