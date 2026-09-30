using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// The Win32 core of every resident local-engine child: <c>parakeet-server</c> (TRN-29) and, since
/// LAI-2, llama.cpp's <c>llama-server</c>. MOVED here from <see cref="NativeParakeetServerLauncher"/>
/// (whose surface and behaviour are unchanged) rather than copied, so the two engines share one
/// set of launch properties instead of two drifting ones. Everything engine-specific — the command
/// line, the environment edits, what the output drain recognises, what gets logged — arrives as
/// arguments.
///
/// <para><b>The launch rules, each from a review round of the Parakeet launcher:</b> the child is
/// created ALREADY INSIDE a kill-on-close job (<c>CreateProcessW</c> +
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> — no assign-after-start window in which an app crash
/// orphans it); stdout and stderr ride ONE anonymous pipe into a background drain (an undrained
/// pipe blocks the child's next write); stdin is an inheritable <c>\\.\NUL</c> we open ourselves
/// (<c>STARTF_USESTDHANDLES</c> disables the CRT's automatic NUL, and a NULL stdin cannot ride the
/// handle list); <c>bInheritHandles</c> is TRUE but RESTRICTED by
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> to exactly {nul, pipe write end}; the parent closes its
/// copies of both after <c>CreateProcessW</c>; the working directory is the exe's own directory;
/// and the job handle is handed to the child object only once the drain is running, so a failure
/// between spawn and drain still kills the child through <c>KILL_ON_JOB_CLOSE</c>. ANY setup
/// failure closes everything opened and returns null: no child exists.</para>
///
/// <para><b>The working directory is load-bearing for llama-server</b> (LAI-2): ggml's backend
/// loader scans the exe directory AND the current directory for <c>ggml-&lt;name&gt;*.dll</c>, so a
/// child started in some other directory could map a stray DLL from there. It is the exe directory
/// for both engines.</para>
/// </summary>
internal static class NativeChildLauncher
{
    private static ILogger Logger => Log.ForContext(typeof(NativeChildLauncher));

    /// <summary>
    /// Launch one child. <paramref name="applicationName"/> is passed to <c>CreateProcessW</c> as
    /// <c>lpApplicationName</c> (null = the command line's own argv[0] resolves — the Parakeet
    /// launcher's test seam); <paramref name="commandLine"/> is the FULL command line, argv[0]
    /// included; <paramref name="environmentBlock"/> is a Unicode block (see
    /// <see cref="BuildEnvironmentBlock"/>) or null to inherit ours unchanged;
    /// <paramref name="drain"/> reads the child's output to EOF on a background thread and returns
    /// the line count — it receives the child's pid. <paramref name="label"/> names the engine in
    /// the log lines. Null = no child.
    /// </summary>
    internal static NativeChildHandle? TryLaunch(
        string label,
        string? applicationName,
        string commandLine,
        string? environmentBlock,
        string workingDirectory,
        Func<StreamReader, uint, int> drain)
    {
        var job = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        var jobHandleSlot = IntPtr.Zero;
        var handleListSlot = IntPtr.Zero;
        var environmentMemory = IntPtr.Zero;
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
                Logger.Warning("{Label:l} launch: CreateJobObject failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: SetInformationJobObject failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: CreatePipe failed ({Err})", label, Marshal.GetLastWin32Error());
                return null;
            }
            if (!NativeInterop.SetHandleInformation(readEnd, NativeInterop.HANDLE_FLAG_INHERIT, 0))
            {
                Logger.Warning("{Label:l} launch: SetHandleInformation failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: opening NUL for stdin failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: InitializeProcThreadAttributeList failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: UpdateProcThreadAttribute (job) failed ({Err})", label, Marshal.GetLastWin32Error());
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
                Logger.Warning("{Label:l} launch: UpdateProcThreadAttribute (handles) failed ({Err})", label, Marshal.GetLastWin32Error());
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

            // A null block means "inherit unchanged" and passes IntPtr.Zero. The flag is required
            // whenever a block is passed: without CREATE_UNICODE_ENVIRONMENT the kernel reads the
            // UTF-16 block as ANSI and the child gets garbage variables.
            var creationFlags = NativeInterop.EXTENDED_STARTUPINFO_PRESENT | NativeInterop.CREATE_NO_WINDOW;
            if (environmentBlock is not null)
            {
                environmentMemory = Marshal.StringToHGlobalUni(environmentBlock);
                creationFlags |= NativeInterop.CREATE_UNICODE_ENVIRONMENT;
            }

            if (!NativeInterop.CreateProcessW(
                    applicationName,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    creationFlags,
                    environmentMemory,
                    workingDirectory,
                    ref startup,
                    out var processInfo))
            {
                Logger.Warning("{Label:l} launch: CreateProcessW failed ({Err})", label, Marshal.GetLastWin32Error());
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
            NativeChildHandle child;
            try
            {
                child = new NativeChildHandle(label, processInfo, job, output, drain);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "{Label:l} launch: output drain could not start - killing the child", label);
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
            if (environmentMemory != IntPtr.Zero) Marshal.FreeHGlobal(environmentMemory);
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

    /// <summary>
    /// A child-specific environment block built from a COPY of <paramref name="parent"/>: every
    /// variable whose name <paramref name="drop"/> accepts is removed, then <paramref name="add"/>
    /// is appended. Returns null — "inherit the parent environment unchanged" — when nothing was
    /// dropped and nothing is added, which keeps the common launch byte-identical to passing no
    /// block at all. <paramref name="drop"/> sees the name AS STORED; callers compare
    /// case-insensitively, because Windows treats names that way while .NET's snapshot keys them
    /// verbatim (a `setx parakeet_device cpu` would otherwise slip past a literal match).
    /// The block is sorted by NAME, case-insensitively — the CreateProcess contract. Deliberately
    /// not a sort of the joined "NAME=value" strings: '=' compares differently from characters that
    /// legally appear in names, so a whole-entry sort misorders prefix-related names
    /// ("COMMONPROGRAMFILES" vs "CommonProgramFiles(x86)" — the real pair that caught this).
    /// </summary>
    internal static string? BuildEnvironmentBlock(
        global::System.Collections.IDictionary parent,
        Func<string, bool> drop,
        IReadOnlyList<(string Name, string Value)> add)
    {
        var entries = new List<(string Name, string Value)>(parent.Count + add.Count);
        var dropped = false;
        foreach (global::System.Collections.DictionaryEntry entry in parent)
        {
            var name = (string)entry.Key;
            if (drop(name))
            {
                dropped = true;
                continue;
            }
            entries.Add((name, $"{entry.Value}"));
        }
        if (!dropped && add.Count == 0)
        {
            return null;
        }
        entries.AddRange(add);
        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var block = new StringBuilder();
        foreach (var (name, value) in entries)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        block.Append('\0');
        return block.ToString();
    }
}

/// <summary>
/// One launched child: its process, thread and job handles, and the drain over its output. Owned
/// by whoever holds it; <see cref="Dispose"/> kills it (the job handle closes with it, so disposal
/// is also what makes app-death cleanup a kernel guarantee rather than our code). Kill/dispose act
/// on the HANDLE a launch returned, never on a pid looked up later — the INS-4 lesson (a pid
/// resolves whoever owns it AT KILL TIME).
/// </summary>
internal sealed class NativeChildHandle : IDisposable
{
    private readonly NativeInterop.PROCESS_INFORMATION _process;
    private readonly IntPtr _job;
    private readonly SafeFileHandle _output;
    // Interlocked, not a bool: the callers serialise Dispose under their own locks today, but a
    // double close of the process/job handles is the kind of fault that survives a refactor of
    // those locks, so the child guards itself (self-review, launch lens).
    private int _disposed;
    private volatile int _linesPumped;
    private volatile bool _outputDrained;

    internal NativeChildHandle(
        string label,
        NativeInterop.PROCESS_INFORMATION process,
        IntPtr job,
        SafeFileHandle output,
        Func<StreamReader, uint, int> drain)
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

        // Drains for the child's whole lifetime — an unread pipe blocks the child's next write —
        // and ends on EOF: the child exited, or Dispose closed the job and KILL_ON_JOB_CLOSE ended
        // it (closing the read end alone does NOT unblock a pending native read; see Dispose).
        // Background, so an app exit never waits on it.
        var pump = new Thread(() =>
        {
            try
            {
                using (reader)
                {
                    _linesPumped = drain(reader, pid);
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
            Name = $"{label} output",
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

    /// <summary>How many raw lines the pipe carried (counts only — never the lines).</summary>
    public int LinesPumped => _linesPumped;

    /// <summary>Whether the drain has reached EOF.</summary>
    public bool OutputDrained => _outputDrained;

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

    /// <summary>The loopback port the child is LISTENING on, read from the kernel's TCP table
    /// keyed by this child's pid — PID attribution is what a same-user spoofer cannot forge (TRN-29
    /// plan round, Blocker 2). Null until the socket opens.</summary>
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
        // therefore never be removed from, or reordered out of, this method.
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
