using System.Runtime.InteropServices;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>PST-22: one step of a clipboard write — did it succeed, and the Win32 error when it did not.</summary>
internal readonly record struct ClipboardWriteStep(bool Ok, int Win32Error)
{
    public static ClipboardWriteStep Succeeded { get; } = new(true, 0);

    /// <summary>A failure; pass 0 when the cause is not a clipboard refusal (an allocation failure), so it is never retried.</summary>
    public static ClipboardWriteStep Failed(int win32Error) => new(false, win32Error);
}

/// <summary>PST-22: the open, empty and close calls of a clipboard write, so the write sequence can be tested without a clipboard.</summary>
internal interface IClipboardWriteNative
{
    bool Open();

    bool Empty(out int win32Error);

    void Close();
}

/// <summary>PST-22: how VoiceWink opens the clipboard and runs a write — moved out of <see cref="ClipboardService"/>, the hub.</summary>
internal static class ClipboardAccess
{
    private static ILogger Logger => Log.ForContext(typeof(ClipboardAccess));

    internal const int ErrorClipboardNotOpen = 1418;
    internal const int ErrorInvalidWindowHandle = 1400;

    // Every open names the same owner window, and a second thread opening with it takes the clipboard over, so opens never overlap.
    private static readonly object OpenGate = new();
    private static readonly TimeSpan OpenGateWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    internal static IClipboardWriteNative Native { get; } = new Win32ClipboardWriteNative();

    /// <summary>
    /// Opens the clipboard with retries and backoff, owned by <see cref="ClipboardOwnerWindow"/>. OpenClipboard
    /// fails when another process holds it (clipboard managers, RDP). Pair every true with <see cref="Close"/>, on this thread.
    /// </summary>
    internal static bool TryOpen(int maxRetries = 3)
    {
        if (!Monitor.TryEnter(OpenGate, OpenGateWait))
        {
            Logger.Warning("Clipboard open waited {Seconds} s for another VoiceWink clipboard call; not opened", OpenGateWait.TotalSeconds);
            return false;
        }

        var opened = false;
        try
        {
            opened = OpenWithRetries(maxRetries);
            return opened;
        }
        finally
        {
            if (!opened) Monitor.Exit(OpenGate);
        }
    }

    /// <summary>Closes a clipboard <see cref="TryOpen"/> opened on this thread.</summary>
    internal static void Close()
    {
        try
        {
            NativeInterop.CloseClipboard();
        }
        finally
        {
            if (Monitor.IsEntered(OpenGate)) Monitor.Exit(OpenGate);
            else if (Interlocked.Exchange(ref _unpairedCloseReported, 1) == 0)
                // An open closed on another thread leaves the lock held, and every later open then waits 2 s and fails.
                Logger.Error("Clipboard closed on a thread that did not open it; later clipboard opens will time out");
        }
    }

    private static int _unpairedCloseReported;

    private static bool OpenWithRetries(int maxRetries)
    {
        int lastError = 0;
        var everyFailureWasInvalidWindow = true;
        for (int i = 0; i < maxRetries; i++)
        {
            // Read per attempt: the owner window's thread may have ended since the last one.
            if (NativeInterop.OpenClipboard(ClipboardOwnerWindow.Handle)) return true;
            // Read before the sleep: any later Win32 call may overwrite it.
            lastError = Marshal.GetLastWin32Error();
            everyFailureWasInvalidWindow &= lastError == ErrorInvalidWindowHandle;
            Thread.Sleep(50 * (i + 1));
        }

        // A broken owner window must never make the clipboard unreachable.
        if (maxRetries > 0 && everyFailureWasInvalidWindow)
        {
            Logger.Warning("OpenClipboard refused the owner window (win32 error {Win32Error}); opening without an owner", lastError);
            if (NativeInterop.OpenClipboard(IntPtr.Zero)) return true;
            lastError = Marshal.GetLastWin32Error();
        }

        // Name the contender so clipboard contention is attributable (RDP bridges,
        // clipboard managers, Office apps) instead of reading as an API flake.
        try
        {
            var owner = NativeInterop.GetClipboardOwner();
            NativeInterop.GetWindowThreadProcessId(owner, out var ownerPid);
            string ownerName = "";
            if (ownerPid != 0)
            {
                try
                {
                    using var p = global::System.Diagnostics.Process.GetProcessById((int)ownerPid);
                    ownerName = p.ProcessName;
                }
                catch { /* process exited / access denied */ }
            }
            // SEC-3: this is the clipboard OWNER's process name — the exact value {OwnerProcess}
            // and the `ownerProc="…"` rendered token already exist for, on the allowlist since
            // REL-16. It was logged under the generic {Name} with a `process="…"` token, so BOTH
            // redaction layers missed it. Reuse the established contract rather than adding a
            // third spelling; a quoted shape is safe here because a Windows process name cannot
            // contain a quote (unlike device names, which is why those use an end-of-line token).
            Logger.Warning(
                "OpenClipboard failed after {Retries} retries (win32 error {Win32Error}); clipboard owner=0x{Owner:X} pid={Pid} ownerProc=\"{OwnerProcess}\"",
                maxRetries, lastError, owner, ownerPid, LogValueSanitizer.SingleLine(ownerName));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Clipboard-owner diagnostic failed");
        }
        return false;
    }

    /// <summary>SetClipboardData; on failure frees <paramref name="handle"/>, logs, and returns the Win32 error.</summary>
    internal static bool SetHandle(uint format, IntPtr handle, string description, out int win32Error)
    {
        var result = NativeInterop.SetClipboardData(format, handle);
        if (result != IntPtr.Zero)
        {
            // After SetClipboardData succeeds, the system owns the handle.
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        NativeInterop.GlobalFree(handle);
        Logger.Warning("SetClipboardData failed for {Description}, Win32={Error}", description, win32Error);
        return false;
    }

    /// <summary>
    /// One clipboard write: open, empty, <paramref name="writeUnderOpenClipboard"/>, close — the whole sequence run once
    /// more when it fails with 1418 (Windows says the clipboard is not open: another opener took it over). A clipboard
    /// that could not be emptied is not written. <paramref name="emptied"/> is true once any attempt emptied it.
    /// </summary>
    internal static bool RunWrite(
        IClipboardWriteNative native,
        string description,
        Action onEmptied,
        Func<ClipboardWriteStep> writeUnderOpenClipboard,
        out bool emptied,
        TimeSpan? retryDelay = null)
    {
        emptied = false;
        for (var attempt = 1; ; attempt++)
        {
            if (!native.Open())
            {
                Logger.Warning("Failed to open clipboard for {Description} after retries", description);
                return false;
            }

            ClipboardWriteStep step;
            try
            {
                if (!native.Empty(out var emptyError))
                {
                    Logger.Warning("EmptyClipboard failed for {Description} (win32 error {Win32Error})", description, emptyError);
                    step = ClipboardWriteStep.Failed(emptyError);
                }
                else
                {
                    emptied = true;
                    onEmptied();
                    step = writeUnderOpenClipboard();
                }
            }
            finally
            {
                native.Close();
            }

            if (step.Ok) return true;
            if (attempt > 1 || step.Win32Error != ErrorClipboardNotOpen) return false;

            Logger.Information("Clipboard write refused for {Description} (win32 error {Win32Error}); retrying the whole write once",
                description, step.Win32Error);
            Thread.Sleep(retryDelay ?? RetryDelay);
        }
    }

    private sealed class Win32ClipboardWriteNative : IClipboardWriteNative
    {
        public bool Open() => TryOpen();

        public bool Empty(out int win32Error)
        {
            var ok = NativeInterop.EmptyClipboard();
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        public void Close() => ClipboardAccess.Close();
    }
}
