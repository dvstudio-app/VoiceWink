using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>Which compute class a local engine is running its decodes on in THIS process. Two
/// values on purpose: the speed star table (TRN-52) keeps one measured set per class, and a
/// finer distinction (which GPU, which driver) is not something a star can carry. Public only so
/// xUnit theory rows can take it as a parameter (a public test method cannot expose an internal
/// type); nothing outside this assembly consumes it.</summary>
public enum LocalCompute
{
    Cpu,
    Gpu,
}

/// <summary>
/// TRN-52: the per-engine compute class the Models page rates SPEED for — the fact the owner's
/// 2026-09-01 decision turns on ("display the set matching the user's actual setup, one correct
/// column"). One value per local engine, because the two resolve independently: Whisper's backend
/// is decided by <see cref="WhisperBackendLog"/>'s probe-gated pin and frozen at the first native
/// load, while the Parakeet child's launch mode starts from the same pre-boot decision and can
/// latch Auto→Cpu once, mid-session, when a GPU child died before health. A page-wide switch would
/// show GPU stars on a Parakeet row whose child is serving CPU.
///
/// <para><b>What it keys on — the RESOLVED backend, never the hardware.</b> The card is explicit:
/// the predicate must follow the same outcome the <c>Whisper native backend:</c> line reports.
/// So the Whisper half reads Whisper.net's own load state (<see cref="WhisperBackendLog.ResolvedCompute"/>)
/// and the Parakeet half reads the LIVE child's own parsed device token
/// (<c>ParakeetServerProcess.HasLiveGpuEvidence</c>) — its launch mode until TRN-64 PR 2, which
/// made this snapshot carry an affirmative "running on your graphics card" sentence that intent
/// cannot support: Auto is a REQUEST, and a loader-present machine with no usable adapter serves
/// CPU under it (Codex diff r1). No live child now reads Cpu, one render behind the first spawn;
/// a machine with a discrete GPU whose driver failed the probe is <see cref="LocalCompute.Cpu"/>
/// here, and correctly so — that is the speed its user gets.</para>
///
/// <para><b>Re-read on every render, never cached.</b> <see cref="Current"/> calls the configured
/// provider each time because two of its inputs move during a session: the Whisper backend becomes
/// KNOWN at the first native load (before that it is the pinned order's first entry), and the
/// Parakeet latch can flip. A Models page already open keeps the stars it rendered until it is
/// re-navigated — accepted; the next render is right.</para>
///
/// <para><b>Fails toward CPU, always.</b> The default provider (no composition root: tests,
/// harnesses that never configure one) and a provider that throws both yield
/// <see cref="AllCpu"/>. A star must never take the Models page down, and the CPU set is the one
/// every machine can honour.</para>
///
/// <para><b>Known residual, stated once — and HALVED by TRN-64 PR 2:</b> a machine with a Vulkan
/// loader but no working device used to resolve Gpu on BOTH halves. The Parakeet half no longer
/// does: it consumes the child's own device line, so such a machine reads Cpu there. The WHISPER
/// half still resolves Gpu — Whisper.net records the Vulkan library as loaded while ggml finds
/// zero devices and computes on CPU — so that engine still shows the GPU set at CPU speed. The
/// loader ships with GPU drivers, so this is a driver-installed-then-GPU-removed shape, not a
/// population. Since TRN-60 the fact reaches the LOG (<c>Whisper Vulkan devices found: 0</c> is
/// this shape's signal) and <c>GpuWarmup.WhisperGateOutcome</c> already gates on the same count;
/// wiring it in here is the follow-up card, and until it exists the Whisper half keeps reading
/// the RESOLVED backend and never the hardware.</para>
/// </summary>
/// <para><b>UI-14 / TRN-68: the snapshot also carries each engine's LIVE adapter name</b> —
/// <paramref name="WhisperGpuName"/> is the row every Whisper factory in this process builds on
/// (<see cref="WhisperBackendLog.ObservedGpuName"/>), <paramref name="ParakeetGpuName"/> the resident
/// child's own resolved row (<c>ParakeetServerProcess.LiveGpuName</c>), null while nothing live is on
/// a GPU. The Pass tick names these, never a persisted name alone: the warm-up child and the resident
/// child pick their adapters independently, so after a refused pin the marker can carry the dedicated
/// adapter while the resident decodes on the integrated one (self-review Blocker).</para>
internal readonly record struct LocalComputeSnapshot(
    LocalCompute Whisper, LocalCompute Parakeet, string? WhisperGpuName = null, string? ParakeetGpuName = null)
{
    /// <summary>UI-14: the live adapter name for one engine, null when unknown or not on a GPU.</summary>
    public string? GpuNameFor(LocalRuntimeKind runtime) => runtime switch
    {
        LocalRuntimeKind.Whisper => Whisper == LocalCompute.Gpu ? WhisperGpuName : null,
        LocalRuntimeKind.Parakeet => Parakeet == LocalCompute.Gpu ? ParakeetGpuName : null,
        _ => null,
    };

    /// <summary>Every engine on CPU — the default, the toggle-OFF machine, and the GPU-less one.</summary>
    public static readonly LocalComputeSnapshot AllCpu = new(LocalCompute.Cpu, LocalCompute.Cpu);

    /// <summary>Every engine on its GPU path — the RTX-class machine the GPU set was measured on.</summary>
    public static readonly LocalComputeSnapshot AllGpu = new(LocalCompute.Gpu, LocalCompute.Gpu);

    /// <summary>The compute class for one engine. An engine this snapshot does not model reads
    /// <see cref="LocalCompute.Cpu"/> — a runtime added later never claims a GPU by default.</summary>
    public LocalCompute For(LocalRuntimeKind runtime) => runtime switch
    {
        LocalRuntimeKind.Whisper => Whisper,
        LocalRuntimeKind.Parakeet => Parakeet,
        _ => LocalCompute.Cpu,
    };

    private static Func<LocalComputeSnapshot> s_provider = static () => AllCpu;

    /// <summary>The live snapshot for this process — what the Models page renders against.
    /// Provider exceptions read as <see cref="AllCpu"/> (see the class doc).</summary>
    public static LocalComputeSnapshot Current
    {
        get
        {
            try
            {
                return s_provider();
            }
            catch
            {
                return AllCpu;
            }
        }
    }

    /// <summary>Install the process-wide provider. Called once, at the composition root, after the
    /// container exists; the provider is invoked on every render, so it must be cheap and must
    /// read live state rather than capture a value.</summary>
    public static void Configure(Func<LocalComputeSnapshot> provider)
        => s_provider = provider ?? throw new ArgumentNullException(nameof(provider));

    /// <summary>Test seam: back to the CPU-only default. The suite runs single-threaded, so a test
    /// that configures a provider restores it in a finally.</summary>
    internal static void ResetForTests() => s_provider = static () => AllCpu;
}
