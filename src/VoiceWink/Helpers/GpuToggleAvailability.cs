using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>What the Models page's "GPU acceleration" row shows: whether the switch is live, which
/// state it displays, the description under the title, and an optional amber advisory line
/// beneath the row. <see cref="IsOn"/> is what the switch DISPLAYS — it equals the persisted
/// value on every live row and is <c>false</c> on the disabled one, where the persisted value is
/// deliberately not shown and never written (see <see cref="GpuToggleAvailability"/>).</summary>
internal readonly record struct GpuTogglePresentation(bool Enabled, bool IsOn, string Description, string? Advisory, string? Status = null);

/// <summary>UI-12: where an engine's GPU check stands when it has no verdict. <see cref="None"/>
/// says nothing (a verdict exists, or the engine warmed without one); <see cref="Awaiting"/> is a
/// check the marker still owes — it runs at a start where the toggle lets it; <see cref="Running"/>
/// is a run in flight right now. Public only so xUnit theory rows can take it as a parameter
/// (the <c>GpuSelfTestOutcome</c> rule); nothing outside this assembly consumes it.</summary>
public enum GpuSelfTestPhase
{
    None,
    Awaiting,
    Running,
}

/// <summary>TRN-50: both engines' persisted self-test records, as the Models page row reads them from
/// the LIVE marker. <c>default</c> (both Unknown) is "no verdict" and adds nothing to the row.
/// <paramref name="WhisperSelectedModelOutcome"/> (TRN-64 PR 2, self-review) is the SELECTED Whisper
/// model's own verdict as the gate would read it — Whisper verdicts are per model, so the engine
/// record's Pass says nothing about a model nobody judged; the positive tick keys on this.</summary>
internal readonly record struct GpuSelfTestSummary(
    GpuSelfTestRecord Whisper, GpuSelfTestRecord Parakeet,
    GpuSelfTestPhase WhisperPhase = GpuSelfTestPhase.None, GpuSelfTestPhase ParakeetPhase = GpuSelfTestPhase.None,
    GpuSelfTestOutcome WhisperSelectedModelOutcome = GpuSelfTestOutcome.Unknown)
{
    internal bool AnyFailed => Whisper.Failed || Parakeet.Failed;

    /// <summary>TRN-64: any verdict that puts an engine on the CPU — Fail, Inconclusive or (PR 2) Slower.</summary>
    internal bool AnyPinsCpu => Whisper.PinsCpu || Parakeet.PinsCpu;

    /// <summary>UI-12: the phase of the SELECTED engine's check, for the neutral status line. Null
    /// for a cloud/unrecognised selection — a progress line about "local models" in general would
    /// be noise under a cloud model.</summary>
    internal GpuSelfTestPhase? PhaseFor(LocalRuntimeKind? activeRuntime) => activeRuntime switch
    {
        LocalRuntimeKind.Whisper => WhisperPhase,
        LocalRuntimeKind.Parakeet => ParakeetPhase,
        _ => null,
    };

    /// <summary>UI-12: does a recorded failure concern the model the user has SELECTED? The two
    /// engines self-test independently and one can pass where the other fails (the owner's Adreno
    /// X1-85 is exactly that shape), so a Parakeet failure under a selected Whisper model is a
    /// warning about something the user is not using. A cloud (or unrecognised) selection has no
    /// local engine to scope to and keeps the union — the local rows are on the same page, and
    /// the failure applies to whichever the user picks next. An enum member this switch does not
    /// name lands there too: never hide a real failure by default.</summary>
    internal bool ConcernsActiveModel(LocalRuntimeKind? activeRuntime) => activeRuntime switch
    {
        LocalRuntimeKind.Whisper => Whisper.PinsCpu,
        LocalRuntimeKind.Parakeet => Parakeet.PinsCpu,
        _ => AnyPinsCpu,
    };

    /// <summary>The adapter to name: the Parakeet child's observed device when that engine failed
    /// (its line is per child, the more specific fact), else Whisper's; null when neither
    /// failure carried a name.</summary>
    internal string? GpuName
        => Parakeet.PinsCpu && Parakeet.GpuName is not null ? Parakeet.GpuName
         : Whisper.PinsCpu ? Whisper.GpuName
         : null;
}

/// <summary>
/// TRN-61 (2026-09-03): the ONE decision on how the "GPU acceleration" row presents
/// itself on THIS machine — a tester's suggestion that a PC with no usable GPU should show the
/// switch OFF and greyed out with the reason, instead of a live switch that changes nothing.
///
/// <para><b>Keys on the pre-boot Vulkan probe verdict, the same fact the launch-mode decision
/// keys on.</b> <c>App.ConfigureServices</c> runs <c>WhisperBackendLog.PinLoadOrder</c> once and
/// <see cref="Record"/>s its <see cref="VulkanSupport"/> verdict here; <c>null</c> means no probe
/// ran (the toggle was OFF at boot, or the pin arrived too late) and makes NO claim about the
/// system — the row then behaves exactly as it did before this type existed. Two bounds follow:
/// the disabled state is reachable only when the toggle was ON at boot (OFF skips the probe by
/// design, so a no-loader PC whose user already turned it OFF keeps the live row until they turn
/// it ON and restart), and a greyed row leaves the persisted value at whatever it was — <c>true</c>
/// for good on such a machine, which a support bundle then shows beside a disabled switch.</para>
///
/// <para><b>Disabled ONLY where the toggle is provably inert for BOTH engines.</b> That is
/// <see cref="VulkanSupport.LoaderMissing"/> alone: no Vulkan loader on the system pins Whisper to
/// <c>[Cpu]</c> AND forces the Parakeet child to <c>Cpu</c> whatever the toggle says
/// (<c>App.xaml.cs</c>'s launch-mode decision). The two Whisper-only declines —
/// <see cref="VulkanSupport.PayloadIncomplete"/> (our Vulkan payload is damaged, the AV-quarantine
/// shape) and <see cref="VulkanSupport.BackendLoadFailed"/> — leave the Parakeet child on
/// <c>Auto</c> with its own bundled loader, so the toggle still decides that engine's device;
/// greying the row out there would tell a Parakeet user "no GPU" while their child runs on one,
/// and would take away the only control that turns it off. Those two keep the row live and add an
/// advisory that names Whisper and says why it is on the CPU — an actual issue on this machine,
/// not a standing explanation of how the control works.</para>
///
/// <para><b>TRN-50 (2026-09-03): a failed GPU SELF-TEST is a second advisory on the live row.</b>
/// It names the adapter the child (or whisper.cpp) reported, says which engine now runs on the
/// CPU, and gives the ONE action that re-tests: turn the switch off and back on
/// (<c>GpuAccelerationPreference.Write</c> re-arms the marker on any actual change). It never disables the row: the switch is the re-arm
/// control, and taking it away would strand the user on CPU for the app version. Read from the
/// LIVE marker on every page build, so a re-arm is visible without a restart. Two rules from the
/// diff round: it shows only while the switch is ON — a user who turned the GPU off chose the CPU
/// and has nothing to re-test, and "turn this switch off and back on" under a switch that is off
/// is wrong (Kimi diff r1) — and the Whisper half says "from the next start of VoiceWink" only
/// until that start has happened: the loaded Vulkan runtime cannot be switched in-process, so a
/// FAIL recorded in THIS process applies at the next start, while a process that BOOTED on the
/// verdict (<see cref="RecordWhisperSelfTestApplied"/>, set beside the pin) already runs Whisper on
/// the CPU and must say so (Grok diff r1).</para>
///
/// <para><b>UI-12 (2026-09-04): that sentence is scoped to the SELECTED model's engine.</b> The
/// two engines self-test independently — the owner's Adreno X1-85 failed Parakeet while Whisper
/// carried no verdict at all — so the row was telling a Whisper user that Parakeet runs on the
/// CPU, true and about something they are not using. <see cref="Decide"/> now takes the active
/// <see cref="LocalRuntimeKind"/> and shows the sentence only when the failure concerns it; a
/// cloud or unrecognised selection keeps the union. <b>Only the gate is scoped, never the
/// wording</b>: with both engines failed the line stays "local models run on the CPU" whichever
/// is selected, which is what the owner asked for and is the more useful fact — one selected
/// engine is not the only one the user can reach from this page.</para>
///
/// <para><b>The persisted setting is never written by this presentation.</b> A probe verdict is
/// about THIS machine at THIS boot; writing OFF because a driver was missing would leave the user
/// on the CPU after they install one, silently. The disabled row shows OFF while
/// <c>gpuAccelerationEnabled</c> keeps whatever the user last chose — the page passes
/// <see cref="GpuTogglePresentation.IsOn"/> INTO the toggle factory, so no <c>Toggled</c> event
/// ever fires for the displayed state.</para>
///
/// <para><b>Fails toward the live row.</b> An unrecorded verdict (tests, harnesses) and any enum
/// member this switch does not name both read as today's behaviour: a wrong default must never be
/// a disabled control.</para>
///
/// <para><b>Not consumed here, deliberately:</b> TRN-60's runtime device count (<c>Whisper Vulkan
/// devices found: 0</c>, the loader-present-but-no-device shape). It arrives at the first
/// transcription-model load, not pre-boot, and <see cref="LocalComputeSnapshot"/> already records
/// that consumer as a follow-up; a driver-installed-then-GPU-removed machine keeps the live row.</para>
/// </summary>
internal static class GpuToggleAvailability
{
    /// <summary>The row's copy on a machine where the toggle works. <b>It no longer states the
    /// restart requirement</b> (owner, 2026-09-04, UI-12): TRN-53 put that sentence here because
    /// nothing else told the user, and TRN-59's modal now says it at the moment of the flip and
    /// offers the restart. The fact is unchanged — the native load order is process-wide and
    /// frozen at the first decode, so a flip cannot apply mid-session by construction — only the
    /// place it is said. Two paths write the setting without raising that dialog and therefore say
    /// nothing about the restart: a failed <c>ShowAsync</c> (fail-soft) and "Reset all settings",
    /// which is the accepted cost of the removal.</summary>
    public const string StandardDescription =
        "Use your graphics card to speed up local transcription models when a compatible GPU is " +
        "available.";

    /// <summary>The disabled row's copy — the reason IS the description. No restart sentence:
    /// nothing the user can do on this PC changes the outcome.</summary>
    public const string NoDriverDescription =
        "No GPU driver with Vulkan support was found on this PC, so local models run on the CPU.";

    /// <summary>Advisory for <see cref="VulkanSupport.PayloadIncomplete"/>: files are missing from
    /// VoiceWink's own Vulkan runtime directory (the AV-quarantine shape). A reinstall lays the
    /// files down again — that much is mechanical and true; whether they STAY is the antivirus's
    /// call, which is why the line names the files and promises no more than putting them back.</summary>
    public const string PayloadAdvisory =
        "Whisper models are running on the CPU because files VoiceWink needs for GPU support are " +
        "missing on this PC. Reinstalling VoiceWink puts them back.";

    /// <summary>Advisory for <see cref="VulkanSupport.BackendLoadFailed"/>: the loader is present
    /// but the whisper.cpp Vulkan backend would not load. On a machine that HAS a loader that is
    /// most often a driver problem, so the line offers the one action a user can take, hedged —
    /// the probe cannot tell a driver fault from a runtime one, and the copy claims no cause.</summary>
    public const string BackendAdvisory =
        "Whisper models are running on the CPU because VoiceWink could not start GPU support on " +
        "this PC. Updating your graphics driver may fix it.";

    /// <summary>The name used when no adapter name was observed.</summary>
    public const string UnnamedGpu = "graphics card";

    /// <summary>UI-12: the neutral status line while the selected engine's check is in flight.</summary>
    internal static string CheckingStatus(string engine) => $"Checking your graphics card for {engine}\u2026";

    /// <summary>UI-12: the neutral status line when the selected engine's check is still owed. The
    /// promise holds for the SELECTED engine WHEN ITS MODEL IS INSTALLED: a Whisper selection is
    /// preloaded at start (which queues its self-test) and a Parakeet selection queues its warm-up
    /// at start. It is false when the model is not on disk — the preload no-ops with nothing to
    /// load and the Parakeet warm-up finds no GGUF — so <see cref="Decide"/> withholds it there
    /// (Codex diff r2). The case where it fails for an installed model, one selected after a
    /// recording, is TRN-64's defect.</summary>
    internal static string AwaitingStatus(string engine) => $"Your graphics card will be checked for {engine} when VoiceWink restarts.";

    /// <summary>TRN-64 PR 2 (owner copy decision, 2026-09-04): a PASSING check is said, positively —
    /// the feature is promoted, never merely defended. Never a per-PC number: nothing measured on
    /// this PC supports one, and an invented figure is what would undermine the claim when checked.
    /// <para>UI-14 (2026-09-05): the line NAMES the adapter \u2014 "\u2713 Parakeet is running on your NVIDIA
    /// GeForce GTX 1050 Ti with Max-Q Design." \u2014 because on a two-GPU PC "your graphics card" is
    /// exactly the question (the tester who prompted TRN-68 spent an afternoon in Task Manager
    /// answering it). The name is the LIVE adapter when known (the caller resolves it), otherwise
    /// the selected engine's persisted self-test record; <see cref="UnnamedGpu"/> keeps the old
    /// sentence when neither is known. A name, not a number: the owner's copy rule stands.</para>
    /// </summary>
    internal static string PassStatus(string engine, string? gpuName = null)
        => $"\u2713 {engine} is running on your {gpuName ?? UnnamedGpu}.";

    /// <summary>The verdict recorded at boot. Written once, before the container exists, and read
    /// on the UI thread much later — a plain field is enough; no live state moves it.</summary>
    private static VulkanSupport? s_probe;

    /// <summary>TRN-50: whether THIS process booted on a Whisper self-test FAIL, i.e. the CPU-only
    /// load order is already in force. Same lifecycle as <see cref="s_probe"/>.</summary>
    private static bool s_whisperSelfTestApplied;

    /// <summary>The pre-boot probe verdict for this process, or <c>null</c> when none was recorded
    /// (no probe ran, or nothing recorded one — tests and harnesses).</summary>
    public static VulkanSupport? Current => s_probe;

    /// <summary>TRN-50: true once this process booted on a Whisper self-test FAIL (the probe-free
    /// <c>[Cpu]</c> pin), so the advisory can say "runs on the CPU" rather than "from the next start".</summary>
    internal static bool WhisperSelfTestApplied => s_whisperSelfTestApplied;

    /// <summary>Record the verdict <c>WhisperBackendLog.PinLoadOrder</c> returned. Called once, at
    /// the composition root, beside the launch-mode decision that reads the same value.</summary>
    public static void Record(VulkanSupport? probe) => s_probe = probe;

    /// <summary>Record whether the pin was taken because of a persisted Whisper self-test FAIL —
    /// called once at the composition root, beside <see cref="Record"/>.</summary>
    public static void RecordWhisperSelfTestApplied(bool applied) => s_whisperSelfTestApplied = applied;

    /// <summary>Test seam: back to "no verdict". The suite runs single-threaded, so a test that
    /// records a verdict restores this in a finally.</summary>
    internal static void ResetForTests()
    {
        s_probe = null;
        s_whisperSelfTestApplied = false;
    }

    /// <summary>The decision. Pure and total: every <see cref="VulkanSupport"/> member and
    /// <c>null</c> map to a presentation, and anything unnamed reads as the live row. A failed
    /// self-test (TRN-50) adds its advisory to a LIVE row whose switch is ON only — the disabled
    /// row has no GPU to re-test, and an OFF switch is the user's own choice — after any probe
    /// advisory, as a second sentence.</summary>
    /// <param name="activeRuntime">UI-12: the local engine behind the SELECTED transcription
    /// model, or <c>null</c> for a cloud/unrecognised selection. It gates WHETHER the self-test
    /// sentence shows (<see cref="GpuSelfTestSummary.ConcernsActiveModel"/>), never how it reads:
    /// the wording still describes every engine that failed, so a machine where both failed says
    /// "local models run on the CPU" whichever one is selected. Defaulting to <c>null</c> keeps
    /// every existing caller — and the union behaviour — unchanged.</param>
    /// <param name="selectedModelInstalled">UI-12: whether the selected model is on disk. Gates
    /// the "will be checked when VoiceWink restarts" line only — a check that cannot be scheduled
    /// (nothing to preload, no GGUF to warm) must not be promised. A run already in flight is a
    /// fact and is reported regardless. Defaults to <c>true</c> so every existing caller and
    /// test keeps its behaviour.</param>
    /// <param name="selectedEngineOnGpu">TRN-64 PR 2 (self-review): whether the selected engine is
    /// resolved on the GPU in THIS process (<c>LocalComputeSnapshot.Current.For(engine)</c>). The
    /// positive tick is a present-tense claim, and a persisted Pass alone cannot support it — the
    /// Parakeet child may have latched CPU this session, Whisper may have pinned. Defaults to
    /// <c>false</c>, the direction that WITHHOLDS the claim.</param>
    /// <param name="liveGpuName">UI-14: the adapter the selected engine is computing on in THIS
    /// process (<c>LocalComputeSnapshot.GpuNameFor</c>), which the Pass tick names ahead of the
    /// engine's persisted record — the warm-up child and the resident child pick their adapters
    /// independently since TRN-68, so the record alone can name an adapter the live child is not on.</param>
    public static GpuTogglePresentation Decide(
        VulkanSupport? probe, bool persistedOn, GpuSelfTestSummary selfTest = default,
        LocalRuntimeKind? activeRuntime = null, bool selectedModelInstalled = true, bool selectedEngineOnGpu = false,
        string? liveGpuName = null)
    {
        var presentation = probe switch
        {
            VulkanSupport.LoaderMissing => new GpuTogglePresentation(Enabled: false, IsOn: false, NoDriverDescription, Advisory: null),
            VulkanSupport.PayloadIncomplete => new GpuTogglePresentation(Enabled: true, IsOn: persistedOn, StandardDescription, PayloadAdvisory),
            VulkanSupport.BackendLoadFailed => new GpuTogglePresentation(Enabled: true, IsOn: persistedOn, StandardDescription, BackendAdvisory),
            _ => new GpuTogglePresentation(Enabled: true, IsOn: persistedOn, StandardDescription, Advisory: null),
        };
        if (!presentation.Enabled || !presentation.IsOn)
        {
            return presentation;
        }
        if (!selfTest.ConcernsActiveModel(activeRuntime))
        {
            // UI-12: nothing has failed for this selection — say where its check stands, if
            // anywhere. Never beside a failure line: a failed engine is not "being checked".
            return presentation with { Status = SelfTestStatus(probe, selfTest, activeRuntime, selectedModelInstalled, selectedEngineOnGpu, liveGpuName) };
        }
        var advisory = SelfTestAdvisory(selfTest, s_whisperSelfTestApplied);
        return presentation with
        {
            Advisory = presentation.Advisory is null ? advisory : presentation.Advisory + " " + advisory,
        };
    }

    /// <summary>UI-12: the neutral status line for the selected engine, or null. Whisper's is
    /// suppressed on the two Whisper-only probe declines — its Vulkan chain will not load, so no
    /// check runs there and the probe advisory already says why; Parakeet's child carries its own
    /// loader and is unaffected. A null probe (no probe ran — the toggle was OFF at boot) keeps
    /// the promise: the next start probes. TRN-64 PR 2: a check in flight or owed OUTRANKS the
    /// positive tick (those lines are about now; a Pass is history), and the tick itself is earned
    /// three ways — the engine on the GPU in this process, the SELECTED model's own Pass (Whisper's
    /// per-model verdict; Parakeet's engine record IS its one model's), and no check pending.</summary>
    internal static string? SelfTestStatus(VulkanSupport? probe, GpuSelfTestSummary selfTest, LocalRuntimeKind? activeRuntime, bool selectedModelInstalled = true, bool selectedEngineOnGpu = false, string? liveGpuName = null)
    {
        if (activeRuntime == LocalRuntimeKind.Whisper
            && probe is VulkanSupport.PayloadIncomplete or VulkanSupport.BackendLoadFailed)
        {
            return null;
        }
        var engine = activeRuntime switch
        {
            LocalRuntimeKind.Whisper => "Whisper",
            LocalRuntimeKind.Parakeet => "Parakeet",
            _ => null,
        };
        if (engine is null) return null;
        var pending = selfTest.PhaseFor(activeRuntime) switch
        {
            GpuSelfTestPhase.Running => CheckingStatus(engine),
            GpuSelfTestPhase.Awaiting when selectedModelInstalled => AwaitingStatus(engine),
            _ => null,
        };
        if (pending is not null)
        {
            return pending; // a check in flight or owed is the fact about NOW
        }
        if (!selectedEngineOnGpu)
        {
            return null; // the engine is on the CPU in this process, whatever the marker remembers
        }
        var passed = activeRuntime == LocalRuntimeKind.Whisper
            ? selfTest.WhisperSelectedModelOutcome == GpuSelfTestOutcome.Pass
            : selfTest.Parakeet.Outcome == GpuSelfTestOutcome.Pass;
        // UI-14: the LIVE adapter first (the one the engine computes on in this process), then the
        // selected ENGINE's own record (the adapter its self-test ran on) — never the summary's
        // combined GpuName, which is defined for the failure sentence. The live name leads because
        // the warm-up child and the resident child pick independently since TRN-68: after a refused
        // pin the record can carry the dedicated adapter while the resident sits on the integrated one.
        var gpuName = liveGpuName ?? (activeRuntime == LocalRuntimeKind.Whisper ? selfTest.Whisper.GpuName : selfTest.Parakeet.GpuName);
        return passed ? PassStatus(engine, gpuName) : null; // TRN-64 PR 2: the selected MODEL's own Pass, said positively
    }

    /// <summary>The self-test sentence: which adapter, what was found (a wrong answer, or since
    /// TRN-64 PR 2 a card too slow for the engine), which engine is on the CPU (and, for Whisper,
    /// whether already or from the next start), and the one action that re-tests. Pure; the case
    /// table pins each engine shape.</summary>
    /// <param name="whisperPinApplied">True when this process already booted on the Whisper FAIL
    /// (<see cref="WhisperSelfTestApplied"/>), so "from the next start" would be stale.</param>
    internal static string SelfTestAdvisory(GpuSelfTestSummary selfTest, bool whisperPinApplied = false)
    {
        var name = selfTest.GpuName ?? UnnamedGpu;
        if (selfTest.Whisper.Outcome == GpuSelfTestOutcome.Inconclusive && !selfTest.Parakeet.PinsCpu)
        {
            // TRN-64: the check did not FINISH — a different fact from a wrong answer, and the
            // user may well be looking at a wedged driver. Whisper-only: Parakeet never persists it.
            var when = whisperPinApplied ? "stays on the processor" : "refuses the graphics card for now and starts on the processor next time";
            return $"The GPU check for Whisper did not finish on this PC's {name}, so Whisper {when}. To check again, turn this switch off and back on.";
        }
        var whisperEffect = whisperPinApplied ? "" : " from the next start of VoiceWink";
        var effect = (selfTest.Parakeet.PinsCpu, selfTest.Whisper.PinsCpu) switch
        {
            (true, true) => whisperPinApplied ? "local models run on the CPU" : "local models run on the CPU (Whisper from the next start of VoiceWink)",
            (true, false) => "Parakeet runs on the CPU",
            _ => "Whisper runs on the CPU" + whisperEffect,
        };
        // TRN-64 PR 2: a correct-but-slow GPU is a different finding from a wrong answer, and the
        // copy says so plainly — never a per-PC number and never "slower than your processor"
        // (nothing measured the processor; the floor is an absolute one, 2x the audio the engine decodes).
        var onlySlower = (!selfTest.Parakeet.PinsCpu || selfTest.Parakeet.Outcome == GpuSelfTestOutcome.Slower)
                      && (!selfTest.Whisper.PinsCpu || selfTest.Whisper.Outcome == GpuSelfTestOutcome.Slower);
        var subject = (selfTest.Parakeet.PinsCpu, selfTest.Whisper.PinsCpu) switch
        {
            (true, true) => "local models",
            (true, false) => "Parakeet",
            _ => "Whisper",
        };
        var anyFailed = selfTest.Parakeet.Outcome == GpuSelfTestOutcome.Fail || selfTest.Whisper.Outcome == GpuSelfTestOutcome.Fail;
        var finding = anyFailed ? $"The GPU self-test failed on this PC's {name}"
            : onlySlower ? $"The GPU check found this PC's {name} too slow for {subject}"
            // Inconclusive beside Slower: nothing FAILED and not everything was slow — claim neither (self-review).
            : $"The GPU check did not clear this PC's {name} for {subject}";
        return $"{finding}, so {effect}. To test the GPU again, turn this switch off and back on.";
    }
}
