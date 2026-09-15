using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
        var parent = global::System.Environment.GetEnvironmentVariables();
        // Case-INSENSITIVE detection, deliberately not Hashtable.Contains: Windows treats
        // environment names case-insensitively but .NET's snapshot keys them verbatim, so a
        // `setx parakeet_device cpu` would slip past a literal Contains, inherit into the Auto
        // child, and silently flip every decode to CPU with no log line — falsifying the
        // strip invariant documented on ParakeetServerProcess (self-review, regression lens).
        var hasDevice = false;
        foreach (var key in parent.Keys)
        {
            if (key is string name && string.Equals(name, "PARAKEET_DEVICE", StringComparison.OrdinalIgnoreCase))
            {
                hasDevice = true;
                break;
            }
        }
        if (mode == ParakeetLaunchMode.Auto && !hasDevice && deviceToken is null)
        {
            return null;
        }

        var entries = new List<(string Name, string Value)>(parent.Count + 1);
        foreach (global::System.Collections.DictionaryEntry entry in parent)
        {
            var name = (string)entry.Key;
            if (string.Equals(name, "PARAKEET_DEVICE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            entries.Add((name, $"{entry.Value}"));
        }
        if (mode == ParakeetLaunchMode.Cpu)
        {
            entries.Add(("PARAKEET_DEVICE", "cpu"));
        }
        else if (deviceToken is not null)
        {
            entries.Add(("PARAKEET_DEVICE", deviceToken)); // TRN-68: the pinned adapter, Auto only
        }
        // Sorted by NAME, case-insensitively — the CreateProcess contract. Deliberately not a
        // sort of the joined "NAME=value" strings: '=' compares differently from characters
        // that legally appear in names, so a whole-entry sort misorders prefix-related names
        // ("COMMONPROGRAMFILES" vs "CommonProgramFiles(x86)" — the real pair that caught this).
        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var block = new StringBuilder();
        foreach (var (name, value) in entries)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        block.Append('\0');
        return block.ToString();
    }

    public IParakeetServerChild? TryLaunch(string exePath, string modelPath, ParakeetLaunchMode mode, string? deviceToken = null)
    {
        var job = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        var jobHandleSlot = IntPtr.Zero;
        var handleListSlot = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var securityAttributes = IntPtr.Zero;
        var readEnd = IntPtr.Zero;
        var writeEnd = IntPtr.Zero;
        SafeFileHandle? nul = null;
        var nulAddRef = false;
        try
        {
            job = NativeInterop.CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                Logger.Warning("parakeet-server launch: CreateJobObject failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            var limits = new NativeInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = NativeInterop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!NativeInterop.SetInformationJobObject(
                    job,
                    NativeInterop.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<NativeInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                Logger.Warning("parakeet-server launch: SetInformationJobObject failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            // TRN-60: ONE anonymous pipe carries both stdout and stderr. Both ends are born
            // inheritable (the child needs the write end); the read end is then made
            // NON-inheritable so the child cannot hold its own output open.
            var inheritable = new NativeInterop.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<NativeInterop.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = IntPtr.Zero,
                bInheritHandle = true,
            };
            if (!NativeInterop.CreatePipe(out readEnd, out writeEnd, ref inheritable, 0))
            {
                Logger.Warning("parakeet-server launch: CreatePipe failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }
            if (!NativeInterop.SetHandleInformation(readEnd, NativeInterop.HANDLE_FLAG_INHERIT, 0))
            {
                Logger.Warning("parakeet-server launch: SetHandleInformation failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            // stdin: an inheritable NUL device. STARTF_USESTDHANDLES switches off the CRT's own
            // NUL-for-a-missing-console, and a NULL hStdInput cannot be placed on the handle
            // list, so the child would start with a dead stdin (Grok plan r1, B1). CreateFileW
            // returns a SafeFileHandle: DangerousAddRef keeps the raw handle valid through
            // CreateProcessW; the finally releases and disposes it.
            securityAttributes = Marshal.AllocHGlobal(Marshal.SizeOf<NativeInterop.SECURITY_ATTRIBUTES>());
            Marshal.StructureToPtr(inheritable, securityAttributes, false);
            nul = NativeInterop.CreateFileW(
                @"\\.\NUL",
                NativeInterop.GENERIC_READ,
                NativeInterop.FILE_SHARE_READ | NativeInterop.FILE_SHARE_WRITE,
                securityAttributes,
                NativeInterop.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (nul.IsInvalid)
            {
                Logger.Warning("parakeet-server launch: opening NUL for stdin failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }
            nul.DangerousAddRef(ref nulAddRef);
            var nulHandle = nul.DangerousGetHandle();

            // Attribute list with exactly TWO attributes: the job the child is born into, and the
            // handles it may inherit. Count 2 on BOTH the size probe and the initialise call.
            var size = IntPtr.Zero;
            NativeInterop.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!NativeInterop.InitializeProcThreadAttributeList(attributeList, 2, 0, ref size))
            {
                Logger.Warning("parakeet-server launch: InitializeProcThreadAttributeList failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }
            // DeleteProcThreadAttributeList walks the list's internal state; on a buffer the
            // initialise call refused it is undefined, so the finally deletes only an initialised
            // list (self-review, launch lens) and frees the memory either way.
            attributeListInitialized = true;

            // The attribute's value is a LIST of job handles; ours has one entry. The memory
            // holding the handle must stay valid until CreateProcessW returns.
            jobHandleSlot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobHandleSlot, job);
            if (!NativeInterop.UpdateProcThreadAttribute(
                    attributeList, 0, NativeInterop.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    jobHandleSlot, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                Logger.Warning("parakeet-server launch: UpdateProcThreadAttribute (job) failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            // The inheritable set, restricted: {nul, writeEnd} — the write end once, though it
            // serves both stdout and stderr. The job handle is NEVER in this list (it is not
            // inheritable and rides the job attribute above). Same lifetime rule as the job slot.
            handleListSlot = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(handleListSlot, 0, nulHandle);
            Marshal.WriteIntPtr(handleListSlot, IntPtr.Size, writeEnd);
            if (!NativeInterop.UpdateProcThreadAttribute(
                    attributeList, 0, NativeInterop.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    handleListSlot, 2 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                Logger.Warning("parakeet-server launch: UpdateProcThreadAttribute (handles) failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            var startup = new NativeInterop.STARTUPINFOEXW
            {
                StartupInfo = new NativeInterop.STARTUPINFOW
                {
                    cb = (uint)Marshal.SizeOf<NativeInterop.STARTUPINFOEXW>(),
                    dwFlags = NativeInterop.STARTF_USESTDHANDLES,
                    hStdInput = nulHandle,
                    hStdOutput = writeEnd,
                    hStdError = writeEnd,
                },
                lpAttributeList = attributeList,
            };

            // --port 0: the child binds an ephemeral port at birth; the kernel's TCP table is
            // the trustworthy readback (Blocker 2). Quote both paths.
            var overrideLine = CommandLineOverride?.Invoke(exePath, modelPath);
            var commandLine = new StringBuilder(
                overrideLine ?? $"\"{exePath}\" --model \"{modelPath}\" --host 127.0.0.1 --port 0 --threads {_threads}");

            // TRN-51/53: device selection travels in the CHILD's environment, never ours. A null
            // block means "inherit unchanged" (Auto, nothing to strip) and passes IntPtr.Zero —
            // byte-identical to the pre-TRN-51 launch. The flag is required whenever a block is
            // passed: without CREATE_UNICODE_ENVIRONMENT the kernel reads the UTF-16 block as
            // ANSI and the child gets garbage variables.
            var creationFlags = NativeInterop.EXTENDED_STARTUPINFO_PRESENT | NativeInterop.CREATE_NO_WINDOW;
            if (BuildEnvironmentBlock(mode, deviceToken) is { } block)
            {
                environmentBlock = Marshal.StringToHGlobalUni(block);
                creationFlags |= NativeInterop.CREATE_UNICODE_ENVIRONMENT;
            }

            if (!NativeInterop.CreateProcessW(
                    overrideLine is null ? exePath : null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    creationFlags,
                    environmentBlock,
                    Path.GetDirectoryName(exePath),
                    ref startup,
                    out var processInfo))
            {
                Logger.Warning("parakeet-server launch: CreateProcessW failed ({Err})", Marshal.GetLastWin32Error());
                return null;
            }

            // The child holds its own copies now. Close ours at once: the write end so the pipe
            // reaches EOF when the child exits (the finally would close it too, but only after
            // the pump below is already running), the NUL because it has no further use.
            NativeInterop.CloseHandle(writeEnd);
            writeEnd = IntPtr.Zero;
            nul.DangerousRelease();
            nulAddRef = false;
            nul.Dispose();
            nul = null;

            // From here the read end belongs to the child object. Build it — which opens the pipe
            // stream and STARTS the drain thread — while `job` is still ours: a throw here (the
            // FileStream over the pipe, the thread start) reaches the finally with `job` set,
            // KILL_ON_JOB_CLOSE kills the child, and nothing is left running with no reader (Grok
            // final check, advisory 2; the stream is opened on THIS thread for exactly that reason).
            var output = new SafeFileHandle(readEnd, ownsHandle: true);
            readEnd = IntPtr.Zero;
            NativeChild child;
            try
            {
                child = new NativeChild(processInfo, job, output, OnDeviceLine);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "parakeet-server launch: output drain could not start - killing the child");
                output.Dispose();
                NativeInterop.CloseHandle(processInfo.hThread);
                NativeInterop.CloseHandle(processInfo.hProcess);
                return null;
            }

            // Success: the child owns the job's lifetime coupling from birth. The child object
            // now owns the process/thread/job handles and the pipe; the finally must not close them.
            job = IntPtr.Zero;
            return child;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            if (handleListSlot != IntPtr.Zero) Marshal.FreeHGlobal(handleListSlot);
            if (jobHandleSlot != IntPtr.Zero) Marshal.FreeHGlobal(jobHandleSlot);
            if (attributeList != IntPtr.Zero)
            {
                if (attributeListInitialized) NativeInterop.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (securityAttributes != IntPtr.Zero) Marshal.FreeHGlobal(securityAttributes);
            if (nul is not null)
            {
                if (nulAddRef) nul.DangerousRelease();
                nul.Dispose();
            }
            if (writeEnd != IntPtr.Zero) NativeInterop.CloseHandle(writeEnd);
            if (readEnd != IntPtr.Zero) NativeInterop.CloseHandle(readEnd);
            if (job != IntPtr.Zero) NativeInterop.CloseHandle(job);
        }
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

    private sealed class NativeChild : IParakeetServerChild, IChildOutputStats
    {
        private readonly NativeInterop.PROCESS_INFORMATION _process;
        private readonly IntPtr _job;
        private readonly SafeFileHandle _output;
        // Interlocked, not a bool: the callers serialise Dispose under their own locks today
        // (ParakeetServerProcess under _gate, GpuWarmup under _parakeetLock), but a double close
        // of the process/job handles is the kind of fault that survives a refactor of those
        // locks, so the child guards itself (self-review, launch lens).
        private int _disposed;
        private volatile int _linesPumped;
        private volatile bool _outputDrained;

        // TRN-50: this child's OWN compute observation, from the same parsed lines the log gets —
        // the backend-selection token and the device rows, resolved to the selected adapter's
        // name. Written by the drain thread, read by whoever holds the child; volatile because a
        // reader on another thread must see the selection once it was written. Parsed fields
        // only, bounded by the parser; never a raw line.
        private volatile string? _observedBackend;
        private volatile string? _observedGpuName;
        // TRN-68: the count line, what "every row has arrived" is measured against; −1 until it fired.
        private volatile int _observedDeviceCount = -1;
        private readonly object _observeLock = new();
        private readonly List<GgmlVulkanDeviceLine> _devices = [];

        public int LinesPumped => _linesPumped;
        public bool OutputDrained => _outputDrained;
        public string? ObservedBackend => _observedBackend;
        public string? ObservedGpuName => _observedGpuName;
        public int ObservedDeviceCount => _observedDeviceCount;

        /// <summary>TRN-68: a snapshot of the device rows this child printed, in pipe order —
        /// parsed fields only, bounded by the parser. The adapter decision reads these together
        /// with <see cref="ObservedDeviceCount"/> and <see cref="ObservedBackend"/>.</summary>
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

        /// <summary>Record one parsed line against this child. The selection may arrive before
        /// or after the device rows on a byte-mode pipe, so the name is (re)resolved on both.</summary>
        private void Observe(GgmlVulkanDeviceLine line)
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

        internal NativeChild(
            NativeInterop.PROCESS_INFORMATION process,
            IntPtr job,
            SafeFileHandle output,
            Action<uint, GgmlVulkanDeviceLine> onMatch)
        {
            _process = process;
            _job = job;
            _output = output;
            var pid = process.dwProcessId;

            // Opened HERE, on the launching thread, so a failure to open the pipe stream is a
            // constructor throw the launcher's catch turns into a killed child — inside the drain
            // thread it would have been swallowed, leaving a healthy-looking child with no reader.
            // The FileStream shares the SafeFileHandle with this object (both Dispose paths reach
            // the same idempotent handle close).
            var stream = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
            var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);

            // Drains for the child's whole lifetime — an unread pipe blocks the child's next write
            // and would hang a transcription — and ends on EOF: the child exited, or Dispose closed
            // the job and KILL_ON_JOB_CLOSE ended it (closing the read end alone does NOT unblock a
            // pending native read; see the class doc). Background, so an app exit never waits on it.
            var pump = new Thread(() =>
            {
                try
                {
                    using (reader)
                    {
                        _linesPumped = ParakeetServerOutputPump.Pump(reader, line =>
                        {
                            Observe(line);
                            onMatch(pid, line);
                        });
                    }
                }
                catch
                {
                    // Nothing to propagate from a background drain; the pump itself is quiet too.
                }
                finally
                {
                    _outputDrained = true;
                }
            })
            {
                IsBackground = true,
                Name = "parakeet-server output",
            };
            try
            {
                pump.Start();
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public uint Pid => _process.dwProcessId;

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool HasExited
        {
            get
            {
                if (IsDisposed) return true;
                return !NativeInterop.GetExitCodeProcess(_process.hProcess, out var code)
                       || code != NativeInterop.STILL_ACTIVE;
            }
        }

        public int? TryReadListeningPort()
        {
            if (IsDisposed) return null;
            var size = 0;
            _ = NativeInterop.GetExtendedTcpTable(IntPtr.Zero, ref size, false,
                NativeInterop.AF_INET, NativeInterop.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeInterop.GetExtendedTcpTable(buffer, ref size, false,
                        NativeInterop.AF_INET, NativeInterop.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + sizeof(int);
                var rowSize = Marshal.SizeOf<NativeInterop.MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeInterop.MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                    // Loopback (127.0.0.1 network order = 0x0100007F) owned by OUR child.
                    if (row.owningPid == _process.dwProcessId && row.localAddr == 0x0100007F)
                    {
                        // dwLocalPort: port in the high-order 16 bits of the low word, network order.
                        var port = (ushort)(((row.localPort & 0xFF) << 8) | ((row.localPort >> 8) & 0xFF));
                        return port;
                    }
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Kill()
        {
            if (IsDisposed) return;
            _ = NativeInterop.TerminateProcess(_process.hProcess, 1);
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (IsDisposed) return true;
            var ms = (uint)Math.Clamp((long)timeout.TotalMilliseconds, 0, int.MaxValue);
            return NativeInterop.WaitForSingleObject(_process.hProcess, ms) == NativeInterop.WAIT_OBJECT_0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // The pipe's read end first, in a try/finally so a throw there can never skip the
            // handle closes. Note what does and does not end the drain: disposing the SafeFileHandle
            // under a pending ReadFile only marks it closed (the P/Invoke holds a ref), so the
            // drain thread is actually unblocked by CloseHandle(_job) below — KILL_ON_JOB_CLOSE
            // kills the child, its write end closes, the read returns EOF. The job close must
            // therefore never be removed from, or reordered out of, this method. Handle closes are
            // idempotent cleanup, never a code path a test needs to distinguish.
            try
            {
                _output.Dispose();
            }
            finally
            {
                NativeInterop.CloseHandle(_process.hThread);
                NativeInterop.CloseHandle(_process.hProcess);
                NativeInterop.CloseHandle(_job);
            }
        }
    }
}
