using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Hidden top-level Win32 window whose only job is to receive the
/// <c>WM_DISPLAYCHANGE</c> broadcast on a thread we KNOW pumps messages (the
/// main UI thread) and forward it to the MiniRecorder display-change recreate
/// path.
///
/// <para><b>Why this exists:</b> the app's original signal —
/// <c>Microsoft.Win32.SystemEvents.DisplaySettingsChanged</c> — delivers
/// broadcasts via a hidden window on SystemEvents' own dedicated thread, and
/// on this hardware/app class that infrastructure demonstrably never fires:
/// live UAT on 2026-07-06 unplugged and replugged an external monitor twice
/// with ZERO receipts logged, leaving the MiniRecorder recreate machinery
/// blind (stale-composition artifact on replug, position jump on unplug).
/// The OS broadcast itself is reliable; receiving it on our own pumping
/// thread removes the flaky middleman. The SystemEvents subscription stays
/// as a second source — the caller's debounce coalesces double delivery.</para>
///
/// <para><b>Deliberately a top-level window, NOT <c>HWND_MESSAGE</c>:</b>
/// message-only windows do not receive broadcast messages. And deliberately
/// NOT a subclass of the WinUI MainWindow's WndProc — a separate listener
/// cannot interfere with WinUI's internal message handling (see the hard-won
/// WinUI constraints in CLAUDE.md).</para>
///
/// <para><b>Threading:</b> create on the UI thread (its dispatcher pumps this
/// window's messages). <see cref="Dispose"/> destroys the window only on the
/// creating thread — <c>DestroyWindow</c> is thread-affine; a wrong-thread
/// dispose logs and skips native teardown (the OS reclaims the window at
/// process exit) rather than failing.</para>
/// </summary>
internal sealed class DisplayChangeListener : global::System.IDisposable
{
    private static ILogger Logger => Log.ForContext<DisplayChangeListener>();

    private static int _instanceCounter;

    private readonly string _className;
    private readonly uint _ownerThreadId;
    // Keeps the native class's function pointer alive — the runtime must never
    // collect this delegate while the window class is registered.
    private readonly NativeInterop.WndProc _wndProc;
    private readonly global::System.Action _onDisplayChange;

    private IntPtr _hwnd;
    private bool _disposed;
    private int _receivedCount;

    /// <summary>Test-only: the listener window handle.</summary>
    internal IntPtr Hwnd => _hwnd;

    /// <summary>Test-only: number of WM_DISPLAYCHANGE messages routed so far.</summary>
    internal int ReceivedCount => global::System.Threading.Volatile.Read(ref _receivedCount);

    /// <summary>
    /// Create the listener on the CURRENT thread (production: the UI thread).
    /// Fail-soft: returns null after logging when class registration or window
    /// creation fails — callers continue with the SystemEvents source alone.
    /// A registration that succeeded before a failed window creation is
    /// unregistered before returning so nothing leaks.
    /// </summary>
    internal static DisplayChangeListener? TryCreate(global::System.Action onDisplayChange)
        => TryCreate(onDisplayChange, out _);

    /// <summary>
    /// Test seam: surfaces the Win32 error of a failed creation so tests can
    /// distinguish a recognized no-window-station environment (narrow CI skip)
    /// from a genuine defect (bad P/Invoke signature etc. — must FAIL, not skip).
    /// </summary>
    internal static DisplayChangeListener? TryCreate(global::System.Action onDisplayChange, out int win32Error)
    {
        win32Error = 0;
        try
        {
            return new DisplayChangeListener(onDisplayChange);
        }
        catch (global::System.ComponentModel.Win32Exception ex)
        {
            win32Error = ex.NativeErrorCode;
            Logger.Warning(ex, "Display-change listener unavailable — continuing with SystemEvents only");
            return null;
        }
        catch (global::System.Exception ex)
        {
            win32Error = -1;
            Logger.Warning(ex, "Display-change listener unavailable — continuing with SystemEvents only");
            return null;
        }
    }

    private DisplayChangeListener(global::System.Action onDisplayChange)
    {
        _onDisplayChange = onDisplayChange;
        _ownerThreadId = NativeInterop.GetCurrentThreadId();
        _wndProc = WndProcImpl;

        var instance = NativeInterop.GetModuleHandle(null);
        var processId = global::System.Environment.ProcessId;
        var instanceNumber = global::System.Threading.Interlocked.Increment(ref _instanceCounter);
        _className = $"VoiceWink.DisplayChangeListener.{processId}.{instanceNumber}";

        var wc = new NativeInterop.WNDCLASSEX
        {
            cbSize = (uint)global::System.Runtime.InteropServices.Marshal.SizeOf<NativeInterop.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = _className,
        };

        if (NativeInterop.RegisterClassEx(ref wc) == 0)
        {
            throw new global::System.ComponentModel.Win32Exception(
                global::System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"RegisterClassEx failed for {_className}");
        }

        // Invisible TOP-LEVEL window (no WS_VISIBLE, no parent). Top-level is
        // load-bearing: WM_DISPLAYCHANGE is broadcast to top-level windows only.
        _hwnd = NativeInterop.CreateWindowEx(
            0, _className, null, 0 /* WS_OVERLAPPED, never shown */,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var error = global::System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            NativeInterop.UnregisterClass(_className, instance); // no partial-failure leak
            throw new global::System.ComponentModel.Win32Exception(
                error, $"CreateWindowEx failed for {_className}");
        }

        Logger.Information("Display-change listener window created (class {ClassName})", _className);
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeInterop.WM_DISPLAYCHANGE)
        {
            global::System.Threading.Interlocked.Increment(ref _receivedCount);
            try
            {
                _onDisplayChange();
            }
            catch (global::System.Exception ex)
            {
                // Never let a callback fault propagate into the message loop.
                Logger.Warning(ex, "Display-change callback threw");
            }
        }

        return NativeInterop.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (NativeInterop.GetCurrentThreadId() != _ownerThreadId)
        {
            // DestroyWindow is thread-affine. Production disposal runs on the UI
            // thread (ShutdownMiniRecorder); anything else is a shutdown-order bug
            // we log rather than crash on — the OS reclaims the window at exit.
            // Deliberately do NOT mark disposed here: a later owner-thread Dispose
            // must still be able to perform the real native teardown.
            Logger.Warning("Display-change listener disposed off its owner thread — skipping native teardown");
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            if (!NativeInterop.DestroyWindow(_hwnd))
                Logger.Debug("Display-change listener DestroyWindow failed (Win32 {Err})",
                    global::System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            _hwnd = IntPtr.Zero;
        }

        if (!NativeInterop.UnregisterClass(_className, NativeInterop.GetModuleHandle(null)))
            Logger.Debug("Display-change listener UnregisterClass failed (Win32 {Err})",
                global::System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }
}
