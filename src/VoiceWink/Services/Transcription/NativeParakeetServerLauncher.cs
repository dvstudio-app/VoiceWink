using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The real Win32 half of the launcher seam (TRN-29 slice 2). The child is created ALREADY
/// INSIDE a kill-on-close job — `CreateProcessW` + `STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_JOB_LIST`
/// — so there is no assign-after-start window in which an app crash orphans a resident server
/// (plan round, Blocker 1). ANY setup failure closes everything opened and returns null: no
/// child exists, and the manager serves sherpa this preparation.
///
/// <para><b>Child output IS captured since TRN-60 (2026-09-02), but never logged raw.</b> v1
/// deliberately did not capture it — the server's banner carries the model PATH, and not
/// capturing was the stronger privacy default. The default survives in spirit: stdout and stderr
/// ride ONE anonymous pipe into a background drain (<see cref="ParakeetServerOutputPump"/>) that
/// hands only lines <see cref="GgmlVulkanDeviceLine.TryParse"/> recognises to the log — the
/// Vulkan device count, the device rows, the server's own <c>using device:</c> selection — and
/// discards everything else. That is what turns the healthy line's <c>launch mode "Auto"</c>
/// (a REQUEST) into a device the support bundle can name.</para>
///
/// <para><b>Four launch rules the pipe added, each from the plan review (Grok r1):</b>
/// <c>STARTF_USESTDHANDLES</c> disables the CRT's automatic NUL for a missing console, so stdin is
/// an inheritable <c>\\.\NUL</c> we open ourselves — a NULL stdin cannot ride the handle list;
/// <c>bInheritHandles</c> is TRUE but RESTRICTED by <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> to
/// exactly {nul, pipe write end}, so no other inheritable app handle is duplicated into a
/// session-long child; the parent closes its copies of both after <c>CreateProcessW</c>, or EOF
/// never arrives; and the job handle is handed to the child object ONLY once the drain is running,
/// so a failure between spawn and drain still kills the child through the finally's
/// <c>KILL_ON_JOB_CLOSE</c> rather than leaving a child with no reader.</para>
///
/// <para><b>The coupling this introduces, named so nobody treats the drain as optional:</b> before
/// TRN-60 the child wrote into a windowless console and could never block on stdout. It can now —
/// the child's output is back-pressured by OUR drain thread, and a drain that stops is a child that
/// wedges on its next write past the pipe's buffer. Which is also why <c>Dispose</c> must always
/// close the job: closing the pipe's read end does NOT end a pending native read (the SafeHandle
/// refcount held by the P/Invoke defers the close), the job close does — it kills the child, whose
/// exit closes the write end and turns the pending read into EOF.</para>
/// </summary>
public sealed class NativeParakeetServerLauncher : IParakeetServerLauncher
{
    private static ILogger Logger => Log.ForContext<NativeParakeetServerLauncher>();

    private readonly int _threads;

    /// <summary>Null means the shipped policy value — a nullable default rather than the constant
    /// itself because <c>ServerDecodeThreads</c> is a computed property since TRN-47, and a
    /// default parameter value must be a compile-time constant.</summary>
    public NativeParakeetServerLauncher(int? threads = null)
        => _threads = threads ?? Helpers.ParakeetServerPolicy.ServerDecodeThreads;

    /// <summary>TRN-60 TEST SEAM — null in production. Returns the FULL command line, argv[0]
    /// included, for a given (exePath, modelPath); when set, <c>lpApplicationName</c> is null so
    /// the command line's own argv[0] resolves. The end-to-end pipe test launches
    /// <c>cmd.exe /c echo …</c> through it, which needs no parakeet-server and no model. Never set
    /// outside a test: it decouples the launched image from the one <c>ParakeetSpawnGate</c>
    /// verified (the gate checks <paramref name="exePath"/>; the override decides what runs).</summary>
    internal Func<string, string, string>? CommandLineOverride { get; set; }

    /// <summary>TRN-60 TEST SEAM — null in production. Sees every parsed device line from every
    /// child this launcher spawned, in addition to the log. Parsed fields only, never raw text.
    /// Set it BEFORE <see cref="TryLaunch"/> — the drain thread reads it after <c>Thread.Start</c>'s
    /// barrier, and a value swapped in later has no publication guarantee.</summary>
    internal Action<uint, GgmlVulkanDeviceLine>? DeviceLineObserver { get; set; }

    /// <summary>
    /// TRN-51/53: the child's device rides a CHILD-SPECIFIC environment block. Returns null for
    /// "inherit the parent environment unchanged" — the common case (Auto with no stray
    /// <c>PARAKEET_DEVICE</c> in our own environment), which keeps the launch byte-identical to
    /// pre-TRN-51 behaviour. Otherwise a Unicode block (name=value pairs, each NUL-terminated,
    /// double-NUL final; name-sorted ordinal-ignore-case per the CreateProcess contract) built
    /// from a COPY of the parent environment with <c>PARAKEET_DEVICE</c> removed (Auto) or set to
    /// <c>cpu</c> (Cpu). Codex plan-round Blocker 2: a process-global variable cannot express the
    /// per-child CPU retry and leaks into every other child the app spawns.
    ///
    /// <para>TRN-68: <paramref name="deviceToken"/> — a ggml registry name from
    /// <see cref="GpuAdapterPreference.DeviceToken"/> (<c>Vulkan1</c>), the ONLY producer — pins an
    /// <c>Auto</c> child to that adapter (<c>PARAKEET_DEVICE=Vulkan1</c>; parakeet.cpp matches it
    /// case-insensitively against the registry device name). <c>Cpu</c> always wins over a token:
    /// the toggle-OFF / pinned-CPU contract is untouched.</para>
    /// </summary>
    internal static string? BuildEnvironmentBlock(ParakeetLaunchMode mode, string? deviceToken = null)
    {
        var add = new List<(string Name, string Value)>(1);
        if (mode == ParakeetLaunchMode.Cpu)
        {
            add.Add(("PARAKEET_DEVICE", "cpu"));
        }
        else if (deviceToken is not null)
        {
            add.Add(("PARAKEET_DEVICE", deviceToken)); // TRN-68: the pinned adapter, Auto only
        }
        // Case-INSENSITIVE match, deliberately: Windows treats environment names case-insensitively
        // but .NET's snapshot keys them verbatim, so a `setx parakeet_device cpu` would slip past a
        // literal compare, inherit into the Auto child, and silently flip every decode to CPU with
        // no log line — falsifying the strip invariant documented on ParakeetServerProcess
        // (self-review, regression lens). The copy/sort/format rules live on the shared builder.
        return NativeChildLauncher.BuildEnvironmentBlock(
            global::System.Environment.GetEnvironmentVariables(),
            static name => string.Equals(name, "PARAKEET_DEVICE", StringComparison.OrdinalIgnoreCase),
            add);
    }

    public IParakeetServerChild? TryLaunch(string exePath, string modelPath, ParakeetLaunchMode mode, string? deviceToken = null)
    {
        // --port 0: the child binds an ephemeral port at birth; the kernel's TCP table is the
        // trustworthy readback (Blocker 2). Quote both paths. The override (a test seam) replaces
        // the whole command line and leaves lpApplicationName null so its argv[0] resolves.
        var overrideLine = CommandLineOverride?.Invoke(exePath, modelPath);
        var commandLine = overrideLine
            ?? $"\"{exePath}\" --model \"{modelPath}\" --host 127.0.0.1 --port 0 --threads {_threads}";

        // The device observation exists BEFORE the child does: the drain thread starts inside the
        // launch and may see the first device line before TryLaunch returns.
        var observation = new DeviceObservation();
        var handle = NativeChildLauncher.TryLaunch(
            "parakeet-server",
            overrideLine is null ? exePath : null,
            commandLine,
            BuildEnvironmentBlock(mode, deviceToken),
            Path.GetDirectoryName(exePath)!,
            (reader, pid) => ParakeetServerOutputPump.Pump(reader, line =>
            {
                observation.Observe(line);
                OnDeviceLine(pid, line);
            }));
        return handle is null ? null : new NativeChild(handle, observation);
    }

    /// <summary>The production match handler: one Information line per parsed device fact,
    /// tagged with the child's pid (the warm-up child and the resident both log through here).
    /// Names and drivers are OS/driver-supplied text and go through the single-line sanitizer
    /// like every other OS-supplied value the log carries.</summary>
    private void OnDeviceLine(uint pid, GgmlVulkanDeviceLine line)
    {
        switch (line.Kind)
        {
            // Distinctive GpuName/GpuDriver/GpuDevice property names, never the generic {Name} /
            // {Device}: hardware-class identifiers that ride to Sentry as breadcrumbs on purpose
            // (recorded in .claude/rules/diagnostics.md).
            case GgmlVulkanDeviceLineKind.DeviceCount:
                Logger.Information("parakeet-server (pid {Pid}): Vulkan devices found: {GpuCount}", pid, line.Count);
                break;
            case GgmlVulkanDeviceLineKind.Device:
                Logger.Information("parakeet-server (pid {Pid}): Vulkan device {GpuIndex}: {GpuName} ({GpuDriver}, uma={GpuUma})",
                    pid, line.Index, LogValueSanitizer.SingleLine(line.Name), LogValueSanitizer.SingleLine(line.Driver), line.Uma);
                break;
            case GgmlVulkanDeviceLineKind.BackendSelection:
                Logger.Information("parakeet-server (pid {Pid}): backend device {GpuDevice}",
                    pid, LogValueSanitizer.SingleLine(line.Device));
                break;
        }
        DeviceLineObserver?.Invoke(pid, line);
    }

    /// <summary>TRN-60 test view of a child's drain: how many raw lines the pipe carried and
    /// whether the pump has reached EOF. Counts only — never the lines.</summary>
    internal interface IChildOutputStats
    {
        int LinesPumped { get; }
        bool OutputDrained { get; }
    }

    /// <summary>TRN-50/68: a child's OWN compute observation, from the same parsed lines the log
    /// gets — the backend-selection token and the device rows, resolved to the selected adapter's
    /// name. Written by the drain thread, read by whoever holds the child; volatile because a reader
    /// on another thread must see the selection once it was written. Parsed fields only, bounded by
    /// the parser; never a raw line.</summary>
    private sealed class DeviceObservation
    {
        private volatile string? _observedBackend;
        private volatile string? _observedGpuName;
        // TRN-68: the count line, what "every row has arrived" is measured against; −1 until it fired.
        private volatile int _observedDeviceCount = -1;
        private readonly object _observeLock = new();
        private readonly List<GgmlVulkanDeviceLine> _devices = [];

        public string? ObservedBackend => _observedBackend;
        public string? ObservedGpuName => _observedGpuName;
        public int ObservedDeviceCount => _observedDeviceCount;

        public IReadOnlyList<GgmlVulkanDeviceLine> ObservedDevices
        {
            get
            {
                lock (_observeLock)
                {
                    return _devices.ToArray();
                }
            }
        }

        /// <summary>Record one parsed line. The selection may arrive before or after the device
        /// rows on a byte-mode pipe, so the name is (re)resolved on both.</summary>
        public void Observe(GgmlVulkanDeviceLine line)
        {
            lock (_observeLock)
            {
                switch (line.Kind)
                {
                    case GgmlVulkanDeviceLineKind.DeviceCount:
                        _observedDeviceCount = line.Count;
                        return;
                    case GgmlVulkanDeviceLineKind.Device:
                        _devices.Add(line);
                        break;
                    case GgmlVulkanDeviceLineKind.BackendSelection:
                        _observedBackend = line.Device;
                        break;
                    default:
                        return;
                }
                if (_observedGpuName is null
                    && ParakeetGpuEvidence.TryDeviceIndex(_observedBackend, out var selected))
                {
                    foreach (var row in _devices)
                    {
                        if (row.Index == selected)
                        {
                            _observedGpuName = row.Name;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>One launched parakeet-server: the shared native handle (process, job, pipe, drain,
    /// TCP readback, kill) plus this engine's device observation.</summary>
    private sealed class NativeChild(NativeChildHandle handle, DeviceObservation observation)
        : IParakeetServerChild, IChildOutputStats
    {
        public int LinesPumped => handle.LinesPumped;
        public bool OutputDrained => handle.OutputDrained;
        public string? ObservedBackend => observation.ObservedBackend;
        public string? ObservedGpuName => observation.ObservedGpuName;
        public int ObservedDeviceCount => observation.ObservedDeviceCount;

        /// <summary>TRN-68: a snapshot of the device rows this child printed, in pipe order —
        /// parsed fields only, bounded by the parser. The adapter decision reads these together
        /// with <see cref="ObservedDeviceCount"/> and <see cref="ObservedBackend"/>.</summary>
        public IReadOnlyList<GgmlVulkanDeviceLine> ObservedDevices => observation.ObservedDevices;

        public uint Pid => handle.Pid;
        public bool HasExited => handle.HasExited;
        public int? TryReadListeningPort() => handle.TryReadListeningPort();
        public void Kill() => handle.Kill();
        public bool WaitForExit(TimeSpan timeout) => handle.WaitForExit(timeout);
        public void Dispose() => handle.Dispose();
    }
}
