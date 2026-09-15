namespace VoiceWink.Helpers;

/// <summary>
/// Orchestrates the bundled <c>WindowsAppRuntimeInstall-x64.exe</c> child
/// process so VoiceWink can run on a clean Windows install (no
/// <c>Microsoft.WindowsAppRuntime.1.6</c> pre-installed). Called from three
/// sites: Velopack's <c>OnAfterInstallFastCallback</c> (install path),
/// Velopack's <c>OnAfterUpdateFastCallback</c> (update path — re-armed by the
/// TCH-3 servicing bump so updated machines meet the raised runtime minimum),
/// and <c>Program.Main</c> stage 3 (normal-launch guard if Bootstrap detection
/// fails for any reason).
///
/// Production invariants (validated by the <c>spike/velopack-day1</c> spike
/// against a clean Hyper-V VM, 7/7 acceptance green):
/// <list type="bullet">
/// <item>Cross-process serialization via the named mutex
///   <c>Global\VoiceWink-WinAppRuntime-Install</c> so two install paths
///   running on the same machine cannot both spawn the 61 MB installer.</item>
/// <item>PID sentinel at <c>%LOCALAPPDATA%\VoiceWinkApp\install.pid</c> for
///   child-lifetime tracking across processes that bypass the mutex (e.g.
///   a stage-3 caller after the mutex-holder timed out).</item>
/// <item>3-layer PID-reuse defense in <see cref="TryReadLiveSentinel"/>:
///   ProcessName prefix, StartTime epoch (within
///   <see cref="StartTimeToleranceSeconds"/>), and canonical
///   MainModule.FileName.</item>
/// <item><see cref="WriteSentinel"/> takes the process directly so
///   <c>StartTime</c> acquisition (which can throw on permission denial or
///   rapid child exit) happens INSIDE the method's try/catch — preserving
///   the fail-closed contract.</item>
/// <item>Sentinel-write failure ALWAYS routes through synchronous wait
///   before any other return path, closing the double-spawn race window.</item>
/// <item>Pre-spawn budget check refuses to spawn when caller budget is
///   already exhausted (prevents tracked-but-unwaitable child leaks).</item>
/// <item>Installer args are <c>--quiet --license --msix</c> (the bare
///   <c>--license</c> form, NOT a made-up <c>--license-accept</c> — VM
///   matrix caught this typo on first attempt).</item>
/// </list>
///
/// See <c>docs/plans/2026-05-05-2103-installer-signing-autoupdate-plan/50-decision.md</c>
/// override items 6–9 for the failure modes each invariant guards.
///
/// <para><b>Test seams (UPD-1 Phase 4):</b> the sentinel read / validate /
/// write helpers (<see cref="TryReadLiveSentinel"/>, <see cref="WriteSentinel"/>,
/// <see cref="ClearSentinel"/>) are <c>internal</c> and take an
/// <see cref="IFileSystem"/> + <see cref="IProcessRunner"/> + explicit paths so
/// the <c>VoiceWink.Tests</c> project can pin the subtle 3-layer PID-reuse
/// defense with in-memory fakes. <c>RunInstaller</c>'s mutex + live spawn/wait
/// orchestration is unchanged and always passes the production
/// <see cref="ProductionFileSystem"/> / <see cref="ProductionProcessRunner"/>
/// singletons, so runtime behavior is identical to before the seam. The
/// <c>Func&lt;bool&gt; recheck</c> parameter is itself the runtime-detector
/// seam (same shape as <c>UpdateScheduler</c>'s applying-probe).</para>
/// </summary>
internal static class PrereqInstaller
{
    private const string InstallMutexName = "Global\\VoiceWink-WinAppRuntime-Install";
    private const string ProcessName      = "WindowsAppRuntimeInstall";
    private const string ExeFileName      = "WindowsAppRuntimeInstall-x64.exe";

    // Tolerance for matching the process StartTime against recorded epoch.
    // Accounts for clock skew + WriteSentinel-after-Start latency.
    private const int StartTimeToleranceSeconds = 5;

    // Production IO/process seams. RunInstaller always uses these verbatim
    // pass-through singletons; only the internal helpers below are driven with
    // fakes by the test project (InternalsVisibleTo). Runtime behavior is the
    // same as the pre-seam static body (UPD-1 Phase 4).
    private static readonly IFileSystem Fs = ProductionFileSystem.Instance;
    private static readonly IProcessRunner Procs = ProductionProcessRunner.Instance;

    private static string SentinelPath => global::System.IO.Path.Combine(
        global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.LocalApplicationData),
        "VoiceWinkApp", "install.pid");

    private static string ExpectedInstallerPath => global::System.IO.Path.Combine(
        global::System.AppContext.BaseDirectory, ExeFileName);

    /// <summary>Called by the OnAfterInstall (30s ceiling) and OnAfterUpdate
    /// (15s ceiling) fast callbacks — the caller's budget must fit its hook's
    /// kill window. Idempotent — Microsoft's installer no-ops fast when runtime
    /// is already at target version.</summary>
    public static void InstallAlwaysWithBudget(int waitMs) =>
        RunInstaller(waitMs, recheck: null);

    /// <summary>Called by stage 3. After serializing against any in-flight install,
    /// re-evaluates <paramref name="recheck"/> before spawning a new installer.</summary>
    public static void InstallWithMutexAndRecheckBudget(int waitMs, global::System.Func<bool> recheck) =>
        RunInstaller(waitMs, recheck);

    /// <summary>Public log surface for stage 3 diagnostics (Program.Main writes
    /// the Bootstrap detect-failed HRESULT through this).</summary>
    public static void LogPublic(string line) => Log(line);

    private static void RunInstaller(int waitMs, global::System.Func<bool>? recheck)
    {
        var startedUtc = global::System.DateTime.UtcNow;
        using var mutex = new global::System.Threading.Mutex(
            initiallyOwned: false, name: InstallMutexName, createdNew: out _);

        bool acquired;
        try { acquired = mutex.WaitOne(waitMs / 4); }
        catch (global::System.Threading.AbandonedMutexException)
        {
            Log("WARN: AbandonedMutexException — prior install crashed; proceeding");
            acquired = true;
        }

        if (!acquired)
        {
            Log("install mutex held by another process; skipping");
            return;
        }

        try
        {
            // Step 1: detect in-flight installer via PID sentinel.
            var inFlight = TryReadLiveSentinel(Fs, Procs, SentinelPath, ExpectedInstallerPath, Log);
            if (inFlight != null)
            {
                Log($"existing installer PID {inFlight.Id} still running — waiting for exit");
                try
                {
                    var remaining = waitMs - (int)(global::System.DateTime.UtcNow - startedUtc).TotalMilliseconds;
                    if (remaining > 0 && inFlight.WaitForExit(remaining))
                    {
                        Log($"existing installer PID {inFlight.Id} exited with code {inFlight.ExitCode}");
                        ClearSentinel(Fs, SentinelPath);
                    }
                    else
                    {
                        Log($"existing installer PID {inFlight.Id} still running after our budget; skipping");
                        return;
                    }
                }
                catch (global::System.Exception ex)
                {
                    Log($"waiting on existing PID failed: {ex.Message}");
                    return;
                }
                finally
                {
                    inFlight.Dispose();
                }
            }

            // Step 2: optional recheck after the in-flight wait (stage 3 only).
            if (recheck != null)
            {
                try
                {
                    if (recheck())
                    {
                        Log("recheck after wait succeeded — runtime now present; skipping spawn");
                        return;
                    }
                }
                catch (global::System.Exception ex)
                {
                    Log($"recheck callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Step 3: locate and spawn the installer.
            //
            // Pre-spawn budget check: after the in-flight wait + recheck, the
            // caller's budget may already be exhausted. Without this check
            // we'd spawn a child knowing the very next remaining-check would
            // fall into the WARN-and-leak path (sentinel might still fail).
            // Refuse to spawn when there's no budget left; let the caller's
            // loop spin up another iteration (stage 3) or the next launch's
            // normal-launch guard handle it.
            var preSpawnRemaining = waitMs - (int)(global::System.DateTime.UtcNow - startedUtc).TotalMilliseconds;
            if (preSpawnRemaining <= 0)
            {
                Log($"budget exhausted before spawn — refusing to spawn (caller had {waitMs}ms; spent waiting on in-flight/recheck)");
                return;
            }
            if (!Fs.FileExists(ExpectedInstallerPath))
            {
                Log($"installer not found at {ExpectedInstallerPath}");
                return;
            }

            // WindowsAppRuntimeInstall-x64.exe args. The correct CLI is:
            //   --quiet --license --msix
            // where --license and --msix are both ON BY DEFAULT but stated
            // explicitly here so the intent is obvious in code. Help text
            // confirms: "(append - to disable e.g. --license- to not install
            // licenses)".
            //
            // The VM matrix caught a prior typo where this read
            // `--license-accept` (made up — not a real arg). The installer
            // exited with -2147024736 (0x80070120 = HRESULT_FROM_WIN32(
            // ERROR_OLD_WIN_VERSION)) because the unknown arg made it print
            // help and bail. Static review didn't catch it; only empirical
            // VM testing did.
            //
            // The ProcessStartInfo (UseShellExecute=false, CreateNoWindow=true)
            // lives in ProductionProcessRunner.Start — the seam point for tests.
            global::VoiceWink.Helpers.IInstallerProcess? child = null;
            try
            {
                child = Procs.Start(ExpectedInstallerPath, "--quiet --license --msix");
                if (child == null)
                {
                    Log("Process.Start returned null");
                    return;
                }

                // Sentinel write is fail-closed: WriteSentinel takes the
                // process directly so StartTime acquisition (which can throw
                // on permission denial or rapid child exit) happens INSIDE
                // WriteSentinel's try/catch.
                var sentinelWritten = WriteSentinel(child, Fs, SentinelPath, Log);
                var remaining = waitMs - (int)(global::System.DateTime.UtcNow - startedUtc).TotalMilliseconds;

                // Sentinel-write failure ALWAYS routes through the
                // synchronous-wait path before any other return. A prior
                // shape checked `remaining <= 0` first, so a budget-exhausted
                // sentinel-failed run would return without waiting AND
                // without a tracking sentinel — re-opening the double-spawn
                // race window.
                if (!sentinelWritten)
                {
                    if (remaining > 0)
                    {
                        Log($"sentinel write FAILED for spawned PID {child.Id} — waiting synchronously to prevent double-spawn");
                        if (child.WaitForExit(remaining))
                        {
                            Log($"installer PID {child.Id} exit code: {child.ExitCode} (no sentinel)");
                        }
                        else
                        {
                            Log($"installer PID {child.Id} did not exit within {remaining}ms (no sentinel)");
                        }
                    }
                    else
                    {
                        // Budget already exhausted AND no sentinel. We can't
                        // wait and can't durably record the child for the
                        // next caller. Log loudly — this is the worst-case
                        // race window. Edge case: caller blew the entire
                        // budget on mutex acquire + in-flight wait +
                        // sentinel-write attempt; only happens when stage
                        // 3's per-iteration budget is very small AND the
                        // sentinel filesystem is slow.
                        Log($"WARN: budget exhausted AND sentinel write failed for spawned PID {child.Id} — double-spawn race window OPEN until child exits");
                    }
                    return;
                }

                // Sentinel was successfully written. Normal flow: wait what
                // budget remains; clear sentinel on clean exit; retain on
                // timeout for the next caller to find via TryReadLiveSentinel.
                if (remaining <= 0)
                {
                    Log($"budget exhausted before spawn wait; spawned PID {child.Id}, sentinel retained for next caller");
                    return;
                }

                if (child.WaitForExit(remaining))
                {
                    Log($"installer PID {child.Id} exit code: {child.ExitCode}");
                    ClearSentinel(Fs, SentinelPath);
                }
                else
                {
                    Log($"installer PID {child.Id} did not exit within {remaining}ms — sentinel retained for next caller");
                    // Intentionally LEAVE the sentinel in place so the next
                    // caller can WaitForExit on this same child.
                }
            }
            catch (global::System.Exception ex)
            {
                Log($"FAIL: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                child?.Dispose();
            }
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// 3-layer PID-reuse defense: validates the process StartTime against the
    /// recorded epoch (within tolerance) AND the canonical MainModule file name
    /// matches our expected installer path. Returns the live process on success
    /// (caller disposes), or null after clearing a stale/invalid sentinel.
    /// </summary>
    internal static global::VoiceWink.Helpers.IInstallerProcess? TryReadLiveSentinel(
        IFileSystem fs,
        IProcessRunner procs,
        string sentinelPath,
        string expectedInstallerPath,
        global::System.Action<string> log)
    {
        try
        {
            if (!fs.FileExists(sentinelPath)) return null;
            var line = fs.ReadAllText(sentinelPath).Trim();
            var parts = line.Split(' ');
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], out var pid) ||
                !long.TryParse(parts[1], out var recordedEpoch))
            {
                log("sentinel content malformed; clearing");
                ClearSentinel(fs, sentinelPath);
                return null;
            }

            // GetProcessById returns null when the PID is not found / has
            // exited (the production seam swallows ArgumentException /
            // InvalidOperationException for us). Treat null as stale.
            var p = procs.GetProcessById(pid);
            if (p == null)
            {
                ClearSentinel(fs, sentinelPath);
                return null;
            }

            try
            {
                // Defense layer 1: process name must match.
                if (!p.ProcessName.StartsWith(ProcessName, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    log($"sentinel PID {pid} alive but ProcessName='{p.ProcessName}' does not match — stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }

                // Defense layer 2: StartTime must match recorded epoch (with tolerance).
                long actualEpoch;
                try
                {
                    actualEpoch = (long)(p.StartTime.ToUniversalTime() -
                        new global::System.DateTime(1970, 1, 1, 0, 0, 0, global::System.DateTimeKind.Utc)).TotalSeconds;
                }
                catch (global::System.Exception ex)
                {
                    log($"sentinel PID {pid} StartTime unreadable ({ex.Message}); treating as stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }
                if (global::System.Math.Abs(actualEpoch - recordedEpoch) > StartTimeToleranceSeconds)
                {
                    log($"sentinel PID {pid} StartTime {actualEpoch} does not match recorded {recordedEpoch}; stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }

                // Defense layer 3: MainModule.FileName must match expected path.
                string? mainModulePath;
                try { mainModulePath = p.MainModuleFileName; }
                catch (global::System.Exception ex)
                {
                    log($"sentinel PID {pid} MainModule unreadable ({ex.Message}); treating as stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }
                // Canonicalize both sides before comparing. Velopack's
                // "current" symlink-style indirection and Windows long-path /
                // 8.3 quirks can otherwise cause a spurious mismatch on
                // otherwise-identical paths.
                string? canonicalMain = null, canonicalExpected = null;
                try
                {
                    if (mainModulePath != null)
                        canonicalMain = global::System.IO.Path.GetFullPath(mainModulePath);
                    canonicalExpected = global::System.IO.Path.GetFullPath(expectedInstallerPath);
                }
                catch (global::System.Exception ex)
                {
                    log($"sentinel PID {pid} path canonicalization threw ({ex.Message}); treating as stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }
                if (canonicalMain == null ||
                    !string.Equals(canonicalMain, canonicalExpected, global::System.StringComparison.OrdinalIgnoreCase))
                {
                    log($"sentinel PID {pid} MainModule='{canonicalMain}' does not match expected '{canonicalExpected}'; stale; clearing");
                    p.Dispose();
                    ClearSentinel(fs, sentinelPath);
                    return null;
                }

                return p; // all three defense layers passed
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }
        catch (global::System.Exception ex)
        {
            // If a sentinel was observed but validation threw inside the
            // defense-layer block, clear it so future callers don't keep
            // tripping the same bad state.
            log($"TryReadLiveSentinel failed: {ex.GetType().Name}: {ex.Message}; clearing sentinel");
            ClearSentinel(fs, sentinelPath);
            return null;
        }
    }

    /// <summary>
    /// Takes the process directly so StartTime acquisition (which can throw
    /// on PID-permission denial or rapid child exit) is inside this method's
    /// try/catch. Returns success/failure. Caller must handle failure
    /// (synchronous wait on spawned child for full budget).
    /// </summary>
    internal static bool WriteSentinel(
        global::VoiceWink.Helpers.IInstallerProcess child,
        IFileSystem fs,
        string sentinelPath,
        global::System.Action<string> log)
    {
        try
        {
            // StartTime read CAN throw — handled by the outer try.
            var childStartTime = child.StartTime;
            var pid = child.Id;

            var dir = global::System.IO.Path.GetDirectoryName(sentinelPath)!;
            fs.CreateDirectory(dir);
            var epoch = (long)(childStartTime.ToUniversalTime() -
                new global::System.DateTime(1970, 1, 1, 0, 0, 0, global::System.DateTimeKind.Utc)).TotalSeconds;
            // Write to a temp path then replace. File.Move(overwrite: true)
            // is the closest .NET 8 gets to a true atomic-replace on NTFS —
            // but it is NOT guaranteed atomic against concurrent readers.
            // The mutex around RunInstaller is the actual serializer; the
            // temp + replace pattern just avoids leaving a partially-written
            // sentinel visible to out-of-band readers.
            var tmp = sentinelPath + ".tmp";
            fs.WriteAllText(tmp, $"{pid} {epoch}");
            fs.MoveFile(tmp, sentinelPath, overwrite: true);
            return true;
        }
        catch (global::System.Exception ex)
        {
            log($"sentinel write failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static void ClearSentinel(IFileSystem fs, string sentinelPath)
    {
        try
        {
            if (fs.FileExists(sentinelPath))
                fs.DeleteFile(sentinelPath);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Win32 MessageBox via direct P/Invoke. Pure user32 — no WinUI
    /// dependency, safe to call when WinAppRuntime is missing or activation
    /// failed.
    /// </summary>
    public static void ShowFatalRuntimeMissingDialog(int hresult)
    {
        var msg = "VoiceWink requires the Microsoft Windows App Runtime 1.6, and the automatic " +
                  $"per-user install failed (HRESULT 0x{hresult:X8}). On a managed/work device this " +
                  "usually means your organisation's policy blocks app (MSIX) installs. Ask your IT " +
                  "administrator to install or enable the Windows App Runtime 1.6 (x64). If you have " +
                  "the rights yourself, you can instead run WindowsAppRuntimeInstall-x64.exe from " +
                  "https://aka.ms/windowsappsdk/1.6/latest/ — then relaunch VoiceWink.";
        try
        {
            MessageBoxW(global::System.IntPtr.Zero, msg,
                "VoiceWink — runtime missing", 0x10 /* MB_ICONERROR */);
        }
        catch { /* best-effort */ }
    }

    [global::System.Runtime.InteropServices.DllImport("user32.dll",
        CharSet = global::System.Runtime.InteropServices.CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(
        global::System.IntPtr hWnd, string text, string caption, uint type);

    private static void Log(string line)
    {
        var primary = global::System.IO.Path.Combine(
            global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.LocalApplicationData),
            "VoiceWinkApp", "install.log");
        var entry = global::System.DateTime.UtcNow.ToString("O") +
            "  " + line + global::System.Environment.NewLine;

        try
        {
            global::System.IO.Directory.CreateDirectory(
                global::System.IO.Path.GetDirectoryName(primary)!);
            global::System.IO.File.AppendAllText(primary, entry);
            return;
        }
        catch { /* fall through */ }

        try
        {
            var fallback = global::System.IO.Path.Combine(
                global::System.IO.Path.GetTempPath(), "VoiceWink-install.log");
            global::System.IO.File.AppendAllText(fallback, entry);
        }
        catch { /* nothing else we can do */ }
    }
}

/// <summary>
/// Process seam for <see cref="PrereqInstaller"/>. Abstracts the two process
/// operations the sentinel logic needs — start the installer, and look a PID
/// back up for liveness validation — so the 3-layer PID-reuse defense can be
/// unit-tested without spawning real processes. Production is
/// <see cref="ProductionProcessRunner"/>, a verbatim <c>System.Diagnostics.Process</c>
/// wrapper.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>Start the installer. Mirrors <c>Process.Start(ProcessStartInfo)</c>
    /// with <c>UseShellExecute=false, CreateNoWindow=true</c>; returns null if
    /// the OS returned no process object.</summary>
    global::VoiceWink.Helpers.IInstallerProcess? Start(string fileName, string arguments);

    /// <summary>Look up a running process by id, or null if it is not found /
    /// has exited (mirrors swallowing <c>ArgumentException</c> /
    /// <c>InvalidOperationException</c> from <c>Process.GetProcessById</c>).</summary>
    global::VoiceWink.Helpers.IInstallerProcess? GetProcessById(int pid);
}

/// <summary>
/// A handle to a running (or just-started) installer process. The
/// <see cref="StartTime"/> / <see cref="MainModuleFileName"/> members can throw
/// exactly as the underlying <c>System.Diagnostics.Process</c> members do —
/// the sentinel logic depends on that fail-closed behavior.
/// </summary>
internal interface IInstallerProcess : global::System.IDisposable
{
    int Id { get; }
    string ProcessName { get; }

    /// <summary>Mirrors <c>Process.StartTime</c> — CAN throw on permission
    /// denial or a rapidly-exited process.</summary>
    global::System.DateTime StartTime { get; }

    /// <summary>Mirrors <c>Process.MainModule?.FileName</c> — CAN throw; may
    /// be null.</summary>
    string? MainModuleFileName { get; }

    bool WaitForExit(int milliseconds);
    int ExitCode { get; }
}

/// <summary>Production <see cref="IProcessRunner"/> — wraps <c>System.Diagnostics</c>.</summary>
internal sealed class ProductionProcessRunner : IProcessRunner
{
    internal static readonly ProductionProcessRunner Instance = new();
    private ProductionProcessRunner() { }

    public global::VoiceWink.Helpers.IInstallerProcess? Start(string fileName, string arguments)
    {
        var psi = new global::System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var p = global::System.Diagnostics.Process.Start(psi);
        return p == null ? null : new ProductionInstallerProcess(p);
    }

    public global::VoiceWink.Helpers.IInstallerProcess? GetProcessById(int pid)
    {
        try { return new ProductionInstallerProcess(global::System.Diagnostics.Process.GetProcessById(pid)); }
        catch (global::System.ArgumentException) { return null; }
        catch (global::System.InvalidOperationException) { return null; }
    }

    private sealed class ProductionInstallerProcess : IInstallerProcess
    {
        private readonly global::System.Diagnostics.Process _p;
        public ProductionInstallerProcess(global::System.Diagnostics.Process p) => _p = p;

        public int Id => _p.Id;
        public string ProcessName => _p.ProcessName;
        public global::System.DateTime StartTime => _p.StartTime;
        public string? MainModuleFileName => _p.MainModule?.FileName;
        public bool WaitForExit(int milliseconds) => _p.WaitForExit(milliseconds);
        public int ExitCode => _p.ExitCode;
        public void Dispose() => _p.Dispose();
    }
}
