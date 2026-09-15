namespace VoiceWink.Helpers;

/// <summary>What <see cref="GpuAdapterPreference.Decide"/> concluded about one enumeration.</summary>
public enum GpuAdapterDecisionKind
{
    /// <summary>The evidence is not complete yet (no count line, rows still arriving, no
    /// selection) — the caller carries on exactly as it would without this type.</summary>
    Undecided,
    /// <summary>The engine's own pick stands — byte-identical to the pre-TRN-68 behaviour.</summary>
    Keep,
    /// <summary>The engine picked an integrated adapter while a dedicated one exists: pin
    /// <see cref="GpuAdapterDecision.Index"/> instead.</summary>
    Pin,
}

/// <summary>One decision: the kind, the row to pin (−1 unless <see cref="GpuAdapterDecisionKind.Pin"/>),
/// and the two adapter NAMES a log line wants — the one the engine picked and the one preferred.
/// Names are the parser's bounded, path-free fields; a caller still runs them through the
/// single-line sanitizer before logging, like every other OS-supplied value.</summary>
public readonly record struct GpuAdapterDecision(
    GpuAdapterDecisionKind Kind, int Index, string? SelectedName, string? PinnedName)
{
    public static readonly GpuAdapterDecision Undecided = new(GpuAdapterDecisionKind.Undecided, -1, null, null);
}

/// <summary>
/// TRN-68 (2026-09-05): the ONE rule for which Vulkan adapter a local engine should compute on
/// when a PC has more than one — decided from the engine's OWN enumeration, never from a
/// persisted index and never from the parent process's view.
///
/// <para><b>Why it exists.</b> Both local engines take the FIRST GPU-or-iGPU device in ggml's
/// registry, which is the Vulkan loader's enumeration order (whisper.cpp
/// <c>whisper_backend_init_gpu</c> at the shipped pin; parakeet.cpp v0.5.0 <c>Backend::Backend</c>;
/// ggml-vulkan's default device set has included integrated GPUs, in enumeration order, since
/// llama.cpp #15947). On a two-GPU laptop that order follows the machine's state at enumeration
/// time — the first field bundle from one (2026-09-05, a ThinkPad X1 Extreme) shows the SAME
/// parakeet-server binary enumerating the Intel UHD 630 first on one launch and the GTX 1050 Ti
/// first on the next, with Parakeet at 4.1–4.2× real time on the Intel and 18–22× on the NVIDIA.
/// The golden-clip self-test and the speed floor both PASS on the integrated adapter by design,
/// so nothing else in the app could tell.</para>
///
/// <para><b>The rule, and its bound.</b> Override ONLY the measured defect: the row the engine
/// selected is <c>uma=true</c> (unified memory — the integrated proxy ggml itself prints) AND at
/// least one <c>uma=false</c> row exists → pin the lowest-indexed <c>uma=false</c> row. A single
/// adapter, a dedicated adapter already selected, two integrated adapters, two dedicated adapters:
/// all <see cref="GpuAdapterDecisionKind.Keep"/>, byte-identical to today. Incomplete evidence is
/// <see cref="GpuAdapterDecisionKind.Undecided"/>, never a guess.</para>
///
/// <para><b>Why the index is trusted here and nowhere else.</b> <c>PARAKEET_DEVICE=Vulkan1</c> and
/// whisper.cpp's <c>gpu_device</c> both address a device by its position in THIS enumeration,
/// and the position is exactly what flips between launches — so a persisted index would pin the
/// wrong adapter on the next dock/undock, and the parent process's own enumeration is not
/// evidence about a child's (Windows applies its per-app GPU preference per EXECUTABLE). The only
/// index that is right by construction is one read from the same process that will use it:
/// the parakeet-server child's own rows (a respawn pinned from them costs a sub-second kill
/// before the ~1 GB model load), and the in-process rows whisper.cpp printed at its first
/// factory.</para>
///
/// <para><b>Completeness is exact (Codex plan round, advisory 1).</b> The pipe both feeds ride is
/// byte-mode and can splice or repeat a row, so "at least <c>count</c> rows" could accept a
/// malformed set and pin an index that names nothing (parakeet.cpp then falls back to CPU). The
/// rows are normalised by INDEX, first occurrence wins, and the decision requires exactly
/// <c>0..count−1</c> present; anything else is Undecided.</para>
///
/// <para>Pure: no I/O, no clock, no logging. Pinned by <c>GpuAdapterPreferenceTests</c>.</para>
/// </summary>
public static class GpuAdapterPreference
{
    /// <summary>The registry name ggml gives its Vulkan devices — <c>Vulkan0</c>, <c>Vulkan1</c> —
    /// which is what parakeet.cpp matches <c>PARAKEET_DEVICE</c> against (case-insensitively) and
    /// what its <c>using device:</c> line prints back. One producer, so the launcher needs no guard
    /// on the token's shape.</summary>
    public static string DeviceToken(int index) => $"Vulkan{index}";

    /// <summary>A count above this is refused as evidence (Undecided): real machines expose a
    /// handful of adapters, and the count line is parsed text — a bound is what keeps a malformed
    /// count from sizing an allocation.</summary>
    public const int MaxDevices = 16;

    /// <summary>
    /// Decide from ONE process's enumeration.
    /// </summary>
    /// <param name="observedDeviceCount">ggml's <c>Found N Vulkan devices</c> count; −1 until it fired.</param>
    /// <param name="rows">The device rows observed so far (any kinds; only
    /// <see cref="GgmlVulkanDeviceLineKind.Device"/> rows count), in pipe order.</param>
    /// <param name="selectedIndex">The row the engine selected — Parakeet's own token via
    /// <see cref="ParakeetGpuEvidence.TryDeviceIndex"/> (a CPU token is null), Whisper's the
    /// device the factory was built with. Null = not known yet.</param>
    public static GpuAdapterDecision Decide(int observedDeviceCount, IReadOnlyList<GgmlVulkanDeviceLine> rows, int? selectedIndex)
    {
        if (observedDeviceCount < 0 || selectedIndex is null)
        {
            return GpuAdapterDecision.Undecided;
        }
        if (observedDeviceCount > MaxDevices)
        {
            return GpuAdapterDecision.Undecided;
        }
        if (observedDeviceCount == 0)
        {
            // Nothing enumerated: whatever the engine says it selected, there is no adapter to prefer.
            return new GpuAdapterDecision(GpuAdapterDecisionKind.Keep, -1, null, null);
        }

        // Normalise by INDEX, first occurrence wins, and require exactly 0..count−1.
        var byIndex = new GgmlVulkanDeviceLine?[observedDeviceCount];
        var present = 0;
        foreach (var row in rows)
        {
            if (row.Kind != GgmlVulkanDeviceLineKind.Device) continue;
            if (row.Index < 0 || row.Index >= observedDeviceCount) continue;
            if (byIndex[row.Index] is not null) continue;
            byIndex[row.Index] = row;
            present++;
        }
        if (present != observedDeviceCount)
        {
            return GpuAdapterDecision.Undecided;
        }
        if (selectedIndex.Value < 0 || selectedIndex.Value >= observedDeviceCount)
        {
            return GpuAdapterDecision.Undecided;
        }

        var selected = byIndex[selectedIndex.Value]!.Value;
        if (!selected.Uma)
        {
            return new GpuAdapterDecision(GpuAdapterDecisionKind.Keep, -1, selected.Name, null);
        }
        for (var i = 0; i < observedDeviceCount; i++)
        {
            var candidate = byIndex[i]!.Value;
            if (!candidate.Uma)
            {
                return new GpuAdapterDecision(GpuAdapterDecisionKind.Pin, i, selected.Name, candidate.Name);
            }
        }
        return new GpuAdapterDecision(GpuAdapterDecisionKind.Keep, -1, selected.Name, null);
    }
}
