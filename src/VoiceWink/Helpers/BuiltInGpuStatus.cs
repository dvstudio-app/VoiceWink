using VoiceWink.Services.AIEnhancement.LocalEngine;

namespace VoiceWink.Helpers;

/// <summary>What the bundled AI engine knows about where one built-in model runs: its first-use
/// check's stored verdict and route, whether that check is running now, whether the whole
/// session runs on the processor, and the live child when it serves this model.</summary>
internal readonly record struct BuiltInComputeFacts(
    LlamaSelfTestVerdict? Verdict,
    LlamaRoute? Route,
    bool Checking,
    bool RunsOnProcessor,
    bool? LiveOnGpu,
    string? LiveDeviceName);

/// <summary>
/// The status line under the AI Enhancement page's GPU switch (owner, 2026-10-04: the same tick the
/// Models page shows for the speech engines, about the built-in AI model in use). "Is running on your
/// …" is a claim about THIS process, so the tick needs the live child serving this model with GPU
/// evidence; a stored GPU pass alone says nothing (the Models page's UI-14 rule). A running check
/// outranks the tick (its GPU child is live but has not passed yet), and a session latched to the
/// processor outranks it too (that child is retired at the next use). Pure.
/// </summary>
internal static class BuiltInGpuStatus
{
    internal static string FasterOnProcessor(string model) => $"{model} runs on the processor; it was at least as fast as the graphics card here.";
    internal static string CheckFailed(string model) => $"{model} runs on the processor; the graphics card failed its check.";
    internal static string ProcessorThisSession(string model) => $"{model} runs on the processor until VoiceWink restarts.";

    /// <summary>The line, or null for none (GPU acceleration off, no model in use, nothing known yet).</summary>
    internal static string? Decide(string? model, bool gpuAccelerationOn, BuiltInComputeFacts facts)
    {
        if (model is null || !gpuAccelerationOn)
            return null;
        if (facts.Verdict == LlamaSelfTestVerdict.Fail)
            return CheckFailed(model);
        if (facts is { Verdict: LlamaSelfTestVerdict.Pass, Route: LlamaRoute.Cpu })
            return FasterOnProcessor(model);
        if (facts.Checking)
            return GpuToggleAvailability.CheckingStatus(model);
        if (facts.RunsOnProcessor)
            return ProcessorThisSession(model);
        if (facts is { LiveOnGpu: true, Verdict: LlamaSelfTestVerdict.Pass, Route: LlamaRoute.Gpu })
            return GpuToggleAvailability.PassStatus(model, facts.LiveDeviceName);
        return null;
    }
}
