using System.Runtime.InteropServices;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>PST-22: the message-only window every clipboard open names as owner, so EmptyClipboard gives the clipboard a real owner.</summary>
internal static class ClipboardOwnerWindow
{
    private static ILogger Logger => Log.ForContext(typeof(ClipboardOwnerWindow));

    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(1);
    private static readonly object StartGate = new();
    private static bool _startAttempted;
    private static IntPtr _hwnd;

    // Held so the GC never collects the class's callback (the native side keeps only the pointer).
    private static readonly NativeInterop.WndProc WndProc = (hWnd, msg, wParam, lParam)
        => NativeInterop.DefWindowProc(hWnd, msg, wParam, lParam);

    /// <summary>The owner window, or <see cref="IntPtr.Zero"/> when it could not be created or its thread ended.</summary>
    internal static IntPtr Handle
    {
        get
        {
            if (!Volatile.Read(ref _startAttempted)) Start();
            return Volatile.Read(ref _hwnd);
        }
    }

    private static void Start()
    {
        lock (StartGate)
        {
            if (_startAttempted) return;

            var ready = new ManualResetEventSlim(false);
            var error = 0;
            // Set first: a thread that cannot start must not make every later open throw.
            Volatile.Write(ref _startAttempted, true);
            try
            {
                new Thread(() => Run(ready, e => error = e)) { IsBackground = true, Name = "ClipboardOwnerWindow" }.Start();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Clipboard owner window thread could not start; the clipboard is opened without an owner");
                return;
            }

            // Late success still publishes the handle; until then opens run without an owner, as before PST-22.
            if (!ready.Wait(ReadyWait))
                Logger.Warning("Clipboard owner window not ready after {Seconds} s; the clipboard is opened without an owner until it is", ReadyWait.TotalSeconds);
            else if (Volatile.Read(ref _hwnd) == IntPtr.Zero)
                Logger.Warning("Clipboard owner window unavailable (win32 error {Win32Error}); the clipboard is opened without an owner", Volatile.Read(ref error));
        }
    }

    private static void Run(ManualResetEventSlim ready, Action<int> reportError)
    {
        var className = $"VoiceWink.ClipboardOwner.{Environment.ProcessId}";
        var instance = NativeInterop.GetModuleHandle(null);
        var classRegistered = false;
        var hwnd = IntPtr.Zero;
        try
        {
            var wc = new NativeInterop.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeInterop.WNDCLASSEX>(),
                lpfnWndProc = WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (NativeInterop.RegisterClassEx(ref wc) == 0)
            {
                reportError(Marshal.GetLastWin32Error());
                return;
            }
            classRegistered = true;

            hwnd = NativeInterop.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0,
                NativeInterop.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                reportError(Marshal.GetLastWin32Error());
                return;
            }

            Volatile.Write(ref _hwnd, hwnd);
            ready.Set();

            // The owner receives WM_DESTROYCLIPBOARD when another app empties the clipboard, so this thread must pump.
            int result;
            while ((result = NativeInterop.GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (result == -1)
                {
                    Logger.Warning("Clipboard owner window message loop failed (win32 error {Win32Error})", Marshal.GetLastWin32Error());
                    break;
                }
                NativeInterop.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Clipboard owner window thread failed");
        }
        finally
        {
            // A destroyed handle would make every open fail with 1400, so it is withdrawn first.
            Volatile.Write(ref _hwnd, IntPtr.Zero);
            if (hwnd != IntPtr.Zero) NativeInterop.DestroyWindow(hwnd);
            if (classRegistered) NativeInterop.UnregisterClass(className, instance);
            ready.Set();
        }
    }
}
