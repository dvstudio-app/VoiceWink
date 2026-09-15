using Serilog;
using Whisper.net.LibraryLoader;

namespace VoiceWink.Helpers;

/// <summary>
/// TRN-27 stage 1: the ONE place that decides which native whisper.cpp backend this process may
/// load, and the one log line that says which backend it actually did load.
///
/// <para><b>The order is DECIDED by a managed pre-flight, not left to native fall-through, and
/// that is a measured requirement — not caution.</b> Whisper.net 1.9.1's loader opens each
/// runtime directory's dependencies ONE BY ONE and, when one fails, closes the handles it
/// collected and moves on (decompiled from the shipped assembly, 2026-09-01). That fall-through
/// path is exactly where this machine measured a NATIVE ABORT: with the Vulkan directory present
/// but its backend DLL missing, the failed Vulkan attempt left ggml's process-wide state
/// (a <c>std::set_terminate</c> registration) behind, and the CPU directory's own
/// <c>ggml-base-whisper.dll</c> then died in
/// <c>GGML_ASSERT(prev != ggml_uncaught_exception)</c> — the whole process gone, at what would
/// be the first recording's VAD init. And the GPU-less machine walks the SAME shape by
/// construction: <c>ggml-vulkan-whisper.dll</c> statically imports <c>vulkan-1.dll</c> (PE
/// import table, read 2026-09-01), and the Vulkan build's <c>ggml-whisper.dll</c> statically
/// imports <c>ggml-vulkan-whisper.dll</c>, so on a machine with no Vulkan loader every DLL in
/// that chain fails to open and the loader falls through with residue. So: probe FIRST, in
/// managed code, and pin [Vulkan, Cpu] only when the Vulkan attempt is going to succeed —
/// otherwise pin [Cpu], so Whisper.net never walks the Vulkan directory. What the PROBE itself
/// mapped before declining stays resident by design (see the dependency-order comment below) —
/// that resident ggml-base is what makes the declined path abort-free, and the CPU decode/gate
/// behaviour under it is measured, not assumed.</para>
///
/// <para><b>Why not the library default order.</b> The default is
/// [Cuda, Cuda12, Vulkan, CoreML, OpenVino, Cpu, CpuNoAvx]: five probes that can never find our
/// payload (we ship exactly two runtimes), CUDA confusion TRN-45 measured, and <c>CpuNoAvx</c>
/// maps to a package this app does not reference — a probe that cannot succeed. Pre-AVX
/// machines cannot run local Whisper either way, and the VAD gate already fail-opens to its RMS
/// fallback on its own CPU-feature probe.</para>
///
/// <para><b>What the log line may carry:</b> the <see cref="RuntimeLibrary"/> enum name, and for
/// a declined Vulkan the probe outcome plus our own DLL basenames — never paths, models, or
/// device strings. One backend line per process (Interlocked latch), from whichever factory
/// succeeds first: the no-speech gate's <c>WhisperVadFactory</c> is the FIRST factory on the
/// shipped default path (it runs on every fresh recording, whatever engine is selected), so it
/// is what freezes the backend for almost every user — the reason <see cref="TryLogOnce"/> is
/// called from BOTH factory sites, not just the Whisper transcription path.</para>
/// </summary>
public static class WhisperBackendLog
{
    private static ILogger Logger => Log.ForContext(typeof(WhisperBackendLog));

    /// <summary>The five DLLs a loadable Vulkan runtime directory must hold — the whisper.cpp
    /// chain the loader opens (VC++ siblings are verified by the payload gate, not here: their
    /// absence fails the vulkan-1/backend probe loads below rather than needing its own rule).</summary>
    internal static readonly string[] VulkanRuntimeFiles =
    [
        "ggml-base-whisper.dll",
        "ggml-cpu-whisper.dll",
        "ggml-vulkan-whisper.dll",
        "ggml-whisper.dll",
        "whisper.dll",
    ];

    /// <summary>0 = the backend line has not been written yet; 1 = it is out. Interlocked latch
    /// because the two factory sites (VAD init on a background task, model load on another) can
    /// race — at most one line per process.</summary>
    private static int _logged;

    /// <summary>
    /// Decide and pin the native load order. Call ONCE, at the composition root, before any
    /// service that could build a <c>WhisperFactory</c>/<c>WhisperVadFactory</c> is resolved —
    /// <c>RuntimeOptions</c> is process-wide and frozen at the first native load, so a pin
    /// applied after that is silently ignored (reported here as
    /// <see cref="WhisperBackendPin.AlreadyLoaded"/>, logged as Error).
    ///
    /// <para><paramref name="gpuAccelerationEnabled"/> is TRN-53's toggle (pre-boot read, default
    /// true): OFF pins [Cpu] WITHOUT probing — the same clean path a GPU-less machine takes, and
    /// deliberately probe-free so a disabled machine never maps a ggml module it will not use.
    /// The frozen-at-first-load fact is also why the toggle takes effect at next app start, which
    /// the Settings copy states.</para>
    ///
    /// <para>The returned <see cref="WhisperBackendPinResult.Probe"/> is what the Parakeet
    /// launch-mode decision reads: <see cref="VulkanSupport.LoaderMissing"/> means the SYSTEM has
    /// no Vulkan loader at all (this probe's TryLoad searches the app dir + System32 and is
    /// deliberately blind to the loader bundled beside `parakeet-server` in
    /// <c>runtimes\win-x64</c>), so Parakeet starts as <c>Cpu</c> directly — byte-identical to
    /// its measured zero-ICD auto-fallback, one fewer moving part.</para>
    /// </summary>
    public static WhisperBackendPinResult PinLoadOrder(bool gpuAccelerationEnabled = true)
    {
        var alreadyLoaded = RuntimeOptions.LoadedLibrary;
        if (alreadyLoaded is not null)
        {
            Logger.Error(
                "Whisper load-order pin arrived AFTER a native backend was already loaded ({Backend}) — the pin is a no-op for this session",
                alreadyLoaded.Value);
            return new WhisperBackendPinResult(WhisperBackendPin.AlreadyLoaded, null);
        }

        if (!gpuAccelerationEnabled)
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            // Information, not Warning: this is the user's own setting doing what it says.
            Logger.Information("GPU acceleration disabled by setting — pinning CPU-only load order");
            RegisterVulkanDeviceLoggerOnce();
            return new WhisperBackendPinResult(WhisperBackendPin.Pinned, null);
        }

        var probe = ProbeVulkanSupport();
        RuntimeOptions.RuntimeLibraryOrder = [.. DecideOrder(probe.Outcome)];
        // TRN-60: LAST step of a successful pin, on both paths, never on the AlreadyLoaded
        // return above — after the order is assigned, so nothing about registering can precede
        // the decision (Grok plan r1, B3; verified managed-only, see the method).
        RegisterVulkanDeviceLoggerOnce();
        if (probe.Outcome == VulkanSupport.Available)
        {
            return new WhisperBackendPinResult(WhisperBackendPin.Pinned, probe.Outcome);
        }

        // CPU-only pin: today's shipped behaviour, stated with its reason. Warning, not Error —
        // a machine without Vulkan is a normal machine, and the loud path is reserved for the
        // pin arriving too late. {Detail} is our own DLL basename or the probe's error text.
        Logger.Warning(
            "Whisper GPU runtime declined ({Outcome}: {Detail}) — pinning CPU-only load order",
            probe.Outcome, probe.Detail);
        return new WhisperBackendPinResult(WhisperBackendPin.Pinned, probe.Outcome);
    }

    // ---- TRN-60: the resolved GPU DEVICE, not just the loaded library ----

    /// <summary>0 = the native-log handler is not registered; 1 = it is. Interlocked latch
    /// because the pin is called repeatedly by tests and handlers must never stack. Taken BEFORE
    /// the attempt and given back on failure, so a transient <c>AddLogger</c> fault does not
    /// disable device logging for the process. Deliberately NOT reset by
    /// <see cref="ResetForTests"/>: registration is per process, like the <c>LogProvider</c> it
    /// registers with.</summary>
    private static int _deviceLoggerRegistered;

    /// <summary>How many times <c>AddLogger</c> has actually been CALLED and succeeded — a
    /// counter, not the latch, so a test can prove "once" rather than "at least once" (the
    /// latch saturates at 1 and would pass a regression that stacked handlers on every pin).</summary>
    private static int _deviceLoggerRegistrations;

    /// <summary>The registration handle. Kept for the process lifetime and never disposed — the
    /// device lines arrive at the first native load and the log wants them for every session. Held
    /// rather than discarded so the subscription's lifetime is visibly ours.</summary>
    private static IDisposable? _deviceLogger;

    /// <summary>Test view: the number of successful <c>AddLogger</c> calls this process has made —
    /// exactly 1 once any successful pin has run, whatever the number of pins.</summary>
    internal static int DeviceLoggerRegistrations => Volatile.Read(ref _deviceLoggerRegistrations);

    /// <summary>
    /// Subscribe <see cref="OnNativeLog"/> to whisper.cpp's log stream, once per process.
    /// <c>LogProvider.AddLogger</c> is MANAGED-ONLY in Whisper.net 1.9.1 — it wraps the delegate
    /// and returns it as <see cref="IDisposable"/>, touching no P/Invoke; the native
    /// <c>whisper_log_set</c> is wired by the library loader at the first native load, which is
    /// also when <c>whisper.dll</c> forwards ggml's log through it (both `whisper_log_set` and
    /// `ggml_log_set` are referenced by the shipped <c>whisper.dll</c>, read 2026-09-02). So
    /// registering here loads nothing — <c>WhisperBackendLogTests</c> pins that
    /// <c>LoadedLibrary</c> is still null after a pin — and the callback starts delivering the
    /// moment the backend initialises, which is exactly when ggml enumerates Vulkan devices.
    /// </summary>
    private static void RegisterVulkanDeviceLoggerOnce()
    {
        if (global::System.Threading.Interlocked.CompareExchange(ref _deviceLoggerRegistered, 1, 0) != 0)
        {
            return;
        }
        try
        {
            _deviceLogger = Whisper.net.Logger.LogProvider.AddLogger(OnNativeLog);
            global::System.Threading.Interlocked.Increment(ref _deviceLoggerRegistrations);
        }
        catch (global::System.Exception ex)
        {
            // A logging convenience must never take the pin down; the backend line still fires,
            // and the latch is released so a later pin may try again.
            global::System.Threading.Volatile.Write(ref _deviceLoggerRegistered, 0);
            Logger.Warning(ex, "Whisper native-log registration failed — GPU device lines will be absent this session");
        }
    }

    /// <summary>
    /// The handler whisper.cpp calls with every native log line, possibly from a native thread
    /// mid-decode. It never throws (a throw into ggml is a failfast — Grok plan r1, B4) and it
    /// forwards NOTHING raw: the model-load lines carry the model path, so only the fields
    /// <see cref="GgmlVulkanDeviceLine.TryParse"/> extracts reach Serilog. Not Debug-logged either
    /// — the sink's minimum is Information today, and a future level change must not start
    /// writing paths.
    /// </summary>
    internal static void OnNativeLog(Whisper.net.Logger.WhisperLogLevel level, string? text)
    {
        try
        {
            if (GgmlVulkanDeviceLine.TryParse(text, out var line))
            {
                LogDeviceLine(line);
            }
        }
        catch
        {
            // Never into the native caller.
        }
    }

    /// <summary>One Information line per parsed device fact. The Whisper feed never carries a
    /// <see cref="GgmlVulkanDeviceLineKind.BackendSelection"/> line (that is parakeet-server's),
    /// so it is ignored here rather than logged under a Whisper heading. Property names are the
    /// DISTINCTIVE <c>GpuName</c>/<c>GpuDriver</c>, never the generic <c>{Name}</c>: a GPU model
    /// identifies hardware class, not a person, and it rides to Sentry as a breadcrumb on purpose —
    /// recorded in <c>.claude/rules/diagnostics.md</c> so the choice is auditable.</summary>
    internal static void LogDeviceLine(GgmlVulkanDeviceLine line)
    {
        switch (line.Kind)
        {
            case GgmlVulkanDeviceLineKind.DeviceCount:
                Logger.Information("Whisper Vulkan devices found: {GpuCount}", line.Count);
                global::System.Threading.Volatile.Write(ref s_observedVulkanDevices, line.Count);
                break;
            case GgmlVulkanDeviceLineKind.Device:
                Logger.Information("Whisper Vulkan device {GpuIndex}: {GpuName} ({GpuDriver}, uma={GpuUma})",
                    line.Index, LogValueSanitizer.SingleLine(line.Name), LogValueSanitizer.SingleLine(line.Driver), line.Uma);
                // TRN-68: the rows are kept, not just row 0 — which row the Whisper factory builds
                // on is decided from them (SelectedGpuDevice), and the name the self-test records
                // follows THAT row (ObservedGpuName).
                lock (s_rowsLock)
                {
                    s_observedRows.Add(line);
                }
                break;
        }
    }

    // ---- TRN-50: the Whisper half's POSITIVE compute evidence ----
    //
    // PROCESS-LEVEL facts, written when the device lines fire at the first GPU-context factory and
    // never refreshed by a later model reload (the native library enumerates once per process).
    // That is all the once-per-process self-test needs; do not read them as per-load freshness
    // (Kimi diff r1 A4).

    private static int s_observedVulkanDevices = -1;
    private static readonly object s_rowsLock = new();
    private static readonly List<GgmlVulkanDeviceLine> s_observedRows = [];

    /// <summary>How many Vulkan devices ggml enumerated for whisper.cpp in this process, from the
    /// <c>Found N Vulkan devices</c> line; −1 until it fired. The Whisper GPU self-test records a
    /// verdict only when this is ≥ 1 — a loaded Vulkan library with zero devices computes on CPU,
    /// and <see cref="ResolvedCompute"/> alone cannot tell (its documented residual).</summary>
    internal static int ObservedVulkanDevices => global::System.Threading.Volatile.Read(ref s_observedVulkanDevices);

    /// <summary>TRN-68: the device rows ggml printed for whisper.cpp in this process (a snapshot;
    /// parsed fields only), empty until the first factory made it enumerate.</summary>
    internal static IReadOnlyList<GgmlVulkanDeviceLine> ObservedDevices
    {
        get
        {
            lock (s_rowsLock)
            {
                return s_observedRows.ToArray();
            }
        }
    }

    /// <summary>
    /// TRN-68: the Vulkan device ordinal EVERY Whisper factory in this process builds on
    /// (<c>WhisperFactoryOptions.GpuDevice</c>) — whisper.cpp's <c>gpu_device</c> counts GPU-or-iGPU
    /// devices in registry order, which is the row index. 0 (whisper.cpp's own default) until the
    /// rows fired, and 0 wherever <see cref="GpuAdapterPreference"/> keeps the default; the first
    /// dedicated row's index when the default row is an integrated adapter. A process constant
    /// once the rows exist (the library enumerates once), which is what lets the first factory be
    /// rebuilt once and every later one build right first time
    /// (<see cref="BuildOnSelectedDevice{TFactory}"/>).
    /// </summary>
    internal static int SelectedGpuDevice
    {
        get
        {
            var decision = GpuAdapterPreference.Decide(ObservedVulkanDevices, ObservedDevices, 0);
            return decision.Kind == GpuAdapterDecisionKind.Pin ? decision.Index : 0;
        }
    }

    /// <summary>The SELECTED adapter's parsed name — the row <see cref="SelectedGpuDevice"/> names —
    /// or null until that row fired. Since TRN-68 this is "the adapter Whisper computes on", the
    /// one meaning the self-test verdicts, the session-pin record and the Models page Pass line
    /// all read; before it, row 0 was assumed.</summary>
    internal static string? ObservedGpuName
    {
        get
        {
            var selected = SelectedGpuDevice;
            lock (s_rowsLock)
            {
                foreach (var row in s_observedRows)
                {
                    if (row.Kind == GgmlVulkanDeviceLineKind.Device && row.Index == selected)
                    {
                        return LogValueSanitizer.SingleLine(row.Name);
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// TRN-68: build a Whisper factory on the selected device, rebuilding ONCE when the build
    /// itself revealed a better selection. The FIRST factory in a process cannot know the rows —
    /// ggml prints them inside that very call — so it is built on whatever
    /// <paramref name="selectedDevice"/> says beforehand (0), and if the answer differs afterwards
    /// the factory is disposed (BEFORE any processor exists: the only moment a
    /// <c>WhisperFactory</c> may be disposed, per the root CLAUDE.md rule about live processors'
    /// native pointers) and built again on the selected ordinal. Every later factory sees the
    /// process constant and never rebuilds. Generic over the factory type so the shipped sequence
    /// is exactly the tested one (Codex plan round, advisory 4): the service passes the real
    /// <c>WhisperFactory.FromPath</c>, the test a fake that injects rows on its first call.
    /// </summary>
    /// <param name="deviceUsed">The ordinal the returned factory was built on.</param>
    /// <param name="onRebuild">Called with (first, selected) when a rebuild happened — the caller's log line.</param>
    internal static TFactory BuildOnSelectedDevice<TFactory>(
        Func<int, TFactory> build,
        Action<TFactory> dispose,
        Func<int> selectedDevice,
        out int deviceUsed,
        Action<int, int>? onRebuild = null)
    {
        var first = selectedDevice();
        var factory = build(first);
        var now = selectedDevice();
        if (now == first)
        {
            deviceUsed = first;
            return factory;
        }
        dispose(factory);
        factory = build(now);
        onRebuild?.Invoke(first, now);
        deviceUsed = now;
        return factory;
    }

    /// <summary>The pure half of the pin: which load order each probe outcome earns. Only a
    /// probe that proved the Vulkan chain loadable puts Vulkan in the order at all — Cpu stays
    /// as the loader's own fall-through for failures the probe cannot foresee, but the probe is
    /// what makes taking that native fall-through (the measured abort surface) unlikely.</summary>
    internal static RuntimeLibrary[] DecideOrder(VulkanSupport outcome)
        => outcome == VulkanSupport.Available
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cpu];

    /// <summary>File-completeness half of the probe, separated for tests: the Vulkan runtime
    /// directory must hold every DLL of the whisper.cpp chain. Returns the missing basenames
    /// (empty = complete). An absent directory reports all five — same verdict, one rule.</summary>
    internal static IReadOnlyList<string> MissingVulkanFiles(string vulkanDir)
        => [.. VulkanRuntimeFiles.Where(f => !global::System.IO.File.Exists(global::System.IO.Path.Combine(vulkanDir, f)))];

    private static (VulkanSupport Outcome, string Detail) ProbeVulkanSupport()
    {
        try
        {
            var vulkanDir = global::System.IO.Path.Combine(
                global::System.AppContext.BaseDirectory, "runtimes", "vulkan", "win-x64");

            var missing = MissingVulkanFiles(vulkanDir);
            if (missing.Count > 0)
            {
                return (VulkanSupport.PayloadIncomplete, string.Join(", ", missing));
            }

            // The Vulkan LOADER (vulkan-1.dll ships with GPU drivers, not Windows) — its absence
            // is the whole GPU-less population, and ggml-vulkan-whisper.dll statically imports
            // it. This check runs BEFORE any ggml DLL is touched, and that ORDER is load-bearing:
            // it is what keeps the GPU-less machine from ever mapping a ggml module it would
            // then unload. The handle is discarded but the module stays resident (refcounted) —
            // deliberate, same reasoning as the keeps below.
            if (!global::System.Runtime.InteropServices.NativeLibrary.TryLoad("vulkan-1.dll", out _))
            {
                return (VulkanSupport.LoaderMissing, "vulkan-1.dll");
            }

            // Load the chain in DEPENDENCY ORDER and KEEP every handle (kimi diff round 1, B1).
            // ggml-vulkan-whisper.dll statically imports ggml-base-whisper.dll (PE import table,
            // read 2026-09-01), so probing the backend alone would let a failed TryLoad
            // unwind-unload an INITIALIZED ggml-base — and an unloaded ggml-base leaves its
            // std::set_terminate registration dangling, which is precisely the measured
            // GGML_ASSERT(prev != ggml_uncaught_exception) abort when the CPU directory's copy
            // initializes next. Loading ggml-base FIRST under our own kept handle means a later
            // failure unwinds only what IT mapped: ggml-base stays resident, and a declined
            // probe's CPU-path load then binds the already-loaded same-basename module instead
            // of re-initializing — no second init, no assert (cross-package bind measured
            // decode- and gate-clean on the declined-probe mutation, 2026-09-01). Kept on
            // success too: freeing would re-create the unload/reload window the probe exists to
            // close. An absolute-path load resolves imports from the DLL's own directory.
            // whisper.dll itself is deliberately NOT probed: with ggml-base pinned resident, a
            // present-but-unloadable whisper.dll fails Whisper.net's Vulkan pass WITHOUT the
            // residue hazard (base never unloads), and the CPU fall-through stays clean.
            if (!global::System.Runtime.InteropServices.NativeLibrary.TryLoad(
                    global::System.IO.Path.Combine(vulkanDir, "ggml-base-whisper.dll"), out _))
            {
                return (VulkanSupport.BackendLoadFailed, "ggml-base-whisper.dll");
            }

            if (!global::System.Runtime.InteropServices.NativeLibrary.TryLoad(
                    global::System.IO.Path.Combine(vulkanDir, "ggml-vulkan-whisper.dll"), out _))
            {
                return (VulkanSupport.BackendLoadFailed, "ggml-vulkan-whisper.dll");
            }

            return (VulkanSupport.Available, "");
        }
        catch (global::System.Exception ex)
        {
            // A probe must never take the app down — an undecidable probe pins CPU, which is
            // today's shipped behaviour.
            return (VulkanSupport.BackendLoadFailed, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Log which native backend loaded — once per process, enum name only. Call after a
    /// SUCCESSFUL factory build; a null <c>LoadedLibrary</c> (nothing loaded yet) does NOT
    /// consume the latch, so a later successful factory still gets to report. Returns whether
    /// THIS call wrote the line (the testable decision; callers ignore it).
    /// </summary>
    public static bool TryLogOnce()
    {
        var loaded = RuntimeOptions.LoadedLibrary;
        if (loaded is null)
        {
            return false;
        }

        if (global::System.Threading.Interlocked.Exchange(ref _logged, 1) == 1)
        {
            return false;
        }

        Logger.Information("Whisper native backend: {Backend}", loaded.Value);
        return true;
    }

    /// <summary>Test seam: re-arm the once-latch and forget the observed device facts. The suite
    /// runs single-threaded and several tests exercise the latch; without this only the first could.</summary>
    internal static void ResetForTests()
    {
        global::System.Threading.Interlocked.Exchange(ref _logged, 0);
        global::System.Threading.Volatile.Write(ref s_observedVulkanDevices, -1);
        lock (s_rowsLock)
        {
            s_observedRows.Clear();
        }
    }

    /// <summary>
    /// TRN-52: the compute class the Whisper rows are rated for in THIS process, read from the
    /// same state the backend line reports. Once a backend has loaded it is that backend
    /// (<see cref="RuntimeOptions.LoadedLibrary"/>); before the first native load it is the
    /// PINNED order's first entry — the probe's consequence, since <see cref="PinLoadOrder"/> puts
    /// Vulkan first only when the probe proved the chain loadable. Only <see cref="RuntimeLibrary.Vulkan"/>
    /// counts as GPU: the library's own default order (which this app never leaves in place) begins
    /// with Cuda, so an unpinned process — a test, a tool that skips the pin — rates the CPU set,
    /// never a GPU it has not proven. And never the hardware: a machine whose driver failed the
    /// probe is CPU here, because CPU is the speed its user gets.
    /// </summary>
    internal static LocalCompute ResolvedCompute()
    {
        var order = RuntimeOptions.RuntimeLibraryOrder;
        var effective = RuntimeOptions.LoadedLibrary ?? (order is { Count: > 0 } ? order[0] : null);
        return effective == RuntimeLibrary.Vulkan ? LocalCompute.Gpu : LocalCompute.Cpu;
    }
}

/// <summary>What <see cref="WhisperBackendLog.PinLoadOrder"/> found — <see cref="Pinned"/> is the
/// only healthy value; <see cref="AlreadyLoaded"/> means the pin was a no-op (logged as Error).</summary>
public enum WhisperBackendPin
{
    Pinned,
    AlreadyLoaded,
}

/// <summary>The pin outcome plus the probe's verdict when one ran. <c>Probe</c> is null when no
/// probe ran (the toggle-OFF pin, or the too-late no-op) — consumers must treat null as "no
/// claim about the system's Vulkan state", never as "usable".</summary>
public readonly record struct WhisperBackendPinResult(WhisperBackendPin Pin, VulkanSupport? Probe);

/// <summary>The Vulkan probe's verdict. Anything but <see cref="Available"/> pins [Cpu] — the
/// distinctions exist for the log line, because "GPU never engaged" has three different fixes
/// (reinstall / it's a GPU-less machine / driver problem) and a support bundle must tell them apart.</summary>
public enum VulkanSupport
{
    Available,
    PayloadIncomplete,
    LoaderMissing,
    BackendLoadFailed,
}
