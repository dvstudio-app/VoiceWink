using System.Runtime.InteropServices;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Input;

/// <summary>HKY-2: counts raw keyboard input on its own thread, so the hook watchdog restarts only on a key the hook provably missed.</summary>
internal sealed class RawKeyboardSink : IDisposable
{
    private static ILogger Logger => Log.ForContext<RawKeyboardSink>();

    /// <summary>Fallback = use the legacy decision; the rest replace it.</summary>
    internal enum Verdict { Fallback, Healthy, NearMiss, RestartRaw, RestartBackstop }

    internal enum StartOutcome { NotStarted, Started, QueryFailed, ForeignRegistration, WindowFailed, RegisterFailed, TimedOut, Stopped }

    internal enum Registration { Ours, QueryFailed, Lost }

    /// <summary>A silent hook this long with a quiet raw clock still restarts when legacy says so (HKY-2 backstop).</summary>
    internal const int BackstopHookSilentSeconds = 600;

    private const uint PingMessage = NativeInterop.WM_APP + 1;
    private static readonly TimeSpan SuspectLogThrottle = TimeSpan.FromMinutes(10);
    private static int _instanceCounter;

    private readonly object _lifecycleLock = new();
    private readonly object _threadIdLock = new();
    private readonly NativeInterop.WndProc _wndProc; // held so the GC never collects the class's callback
    private Thread? _thread;
    private volatile uint _threadId;
    private bool _stopRequested; // guarded by _threadIdLock
    private volatile IntPtr _hwnd;
    private volatile StartOutcome _outcome;
    private volatile bool _disposed;
    private int _lastWin32Error;

    private long _rawKeyCount;
    private int _lastRawTime; // MSG.time of the last keyboard WM_INPUT (GetTickCount units)
    private long _posted;
    private long _acked;

    // Evaluation state: watchdog (UI) thread only.
    private bool _lost;
    private bool _suspect;
    private bool _firstKeyLogged;
    private long _hookBaseline;
    private long _rawBaseline;
    private DateTime _lastSuspectStartLogUtc = DateTime.MinValue;
    private DateTime _lastSuspectEndLogUtc = DateTime.MinValue;

    internal RawKeyboardSink()
    {
        _wndProc = (h, m, w, l) => NativeInterop.DefWindowProc(h, m, w, l);
        PostPing = hwnd => NativeInterop.PostMessage(hwnd, PingMessage, UIntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Test seam: the ping post, so a failed post can be driven without a broken window.</summary>
    internal Func<IntPtr, bool> PostPing { get; set; }

    internal StartOutcome Outcome => _outcome;
    internal int LastWin32Error => Volatile.Read(ref _lastWin32Error);
    internal bool IsRunning => _hwnd != IntPtr.Zero;
    internal IntPtr WindowForTests => _hwnd;
    internal long RawKeyCount => Interlocked.Read(ref _rawKeyCount);
    internal long PostedPings => Interlocked.Read(ref _posted);
    internal long AckedPings => Interlocked.Read(ref _acked);
    internal bool IsSuspect => _suspect;
    internal bool IsLost => _lost;

    /// <summary>Starts the sink thread; idempotent while it runs. Blocks up to 2 s, so production calls it off the UI thread.</summary>
    internal StartOutcome Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return _outcome;
            if (_thread is { IsAlive: true }) return _outcome;

            Interlocked.Exchange(ref _rawKeyCount, 0);
            Volatile.Write(ref _lastRawTime, 0);
            Volatile.Write(ref _lastWin32Error, 0);
            (_hookBaseline, _rawBaseline, _suspect, _lost, _firstKeyLogged) = (0, 0, false, false, false);
            lock (_threadIdLock) _stopRequested = false;
            _outcome = StartOutcome.NotStarted;
            var ready = new ManualResetEventSlim(false);
            var className = $"VoiceWink.RawKeyboardSink.{Environment.ProcessId}.{Interlocked.Increment(ref _instanceCounter)}";
            _thread = new Thread(() => Run(ready, className)) { IsBackground = true, Name = "RawKeyboardSink" };
            _thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warning("Raw keyboard sink thread did not report ready within 2 s — watchdog keeps its legacy check");
                StopCore();
                _outcome = StartOutcome.TimedOut;
                return _outcome;
            }

            switch (_outcome)
            {
                case StartOutcome.Started:
                    Logger.Information("Raw keyboard sink started");
                    break;
                case StartOutcome.ForeignRegistration:
                    Logger.Information("Raw keyboard sink not started: another keyboard raw-input registration exists — watchdog keeps its legacy check");
                    break;
                default:
                    Logger.Information("Raw keyboard sink not started ({Outcome}, Win32 {Error}) — watchdog keeps its legacy check",
                        _outcome, LastWin32Error);
                    break;
            }
            return _outcome;
        }
    }

    internal void Stop()
    {
        lock (_lifecycleLock) StopCore();
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            _disposed = true;
            StopCore();
        }
    }

    private void StopCore()
    {
        var thread = _thread;
        if (thread == null) return;
        lock (_threadIdLock)
        {
            _stopRequested = true;
            // Posted under the lock the thread clears its id under, so a reused thread id never gets the WM_QUIT.
            if (_threadId != 0) NativeInterop.PostThreadMessage(_threadId, NativeInterop.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        }
        if (!thread.Join(TimeSpan.FromSeconds(1)))
            Logger.Warning("Raw keyboard sink thread did not exit within 1 s");
        _thread = null;
    }

    private void Run(ManualResetEventSlim ready, string className)
    {
        var instance = NativeInterop.GetModuleHandle(null);
        var classRegistered = false;
        var hwnd = IntPtr.Zero;
        var rawRegistered = false;
        try
        {
            // The queue exists before the id is shared, so no WM_QUIT to this thread can be lost.
            NativeInterop.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeInterop.PM_NOREMOVE);
            lock (_threadIdLock)
            {
                if (_stopRequested) { _outcome = StartOutcome.Stopped; return; }
                _threadId = NativeInterop.GetCurrentThreadId();
            }

            // One keyboard registration per process: never replace one we did not make.
            switch (QueryKeyboardRegistration(out _, out var queryError))
            {
                case RegistrationQuery.Failed:
                    Volatile.Write(ref _lastWin32Error, queryError);
                    _outcome = StartOutcome.QueryFailed;
                    return;
                case RegistrationQuery.Present:
                    _outcome = StartOutcome.ForeignRegistration;
                    return;
            }

            var wc = new NativeInterop.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeInterop.WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (NativeInterop.RegisterClassEx(ref wc) == 0)
            {
                Volatile.Write(ref _lastWin32Error, Marshal.GetLastWin32Error());
                _outcome = StartOutcome.WindowFailed;
                return;
            }
            classRegistered = true;

            hwnd = NativeInterop.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0,
                NativeInterop.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Volatile.Write(ref _lastWin32Error, Marshal.GetLastWin32Error());
                _outcome = StartOutcome.WindowFailed;
                return;
            }

            // INPUTSINK alone: no EXINPUTSINK, NOLEGACY or NOHOTKEYS, so nothing about other apps' input changes.
            var device = new[]
            {
                new NativeInterop.RAWINPUTDEVICE
                {
                    usUsagePage = NativeInterop.HID_USAGE_PAGE_GENERIC,
                    usUsage = NativeInterop.HID_USAGE_GENERIC_KEYBOARD,
                    dwFlags = NativeInterop.RIDEV_INPUTSINK,
                    hwndTarget = hwnd,
                },
            };
            if (!NativeInterop.RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<NativeInterop.RAWINPUTDEVICE>()))
            {
                Volatile.Write(ref _lastWin32Error, Marshal.GetLastWin32Error());
                _outcome = StartOutcome.RegisterFailed;
                return;
            }
            rawRegistered = true;

            lock (_threadIdLock)
            {
                if (_stopRequested) return; // Start already gave up (TimedOut): never report Started late
                Interlocked.Exchange(ref _posted, 0);
                Interlocked.Exchange(ref _acked, 0);
                _hwnd = hwnd;
                _outcome = StartOutcome.Started;
            }
            ready.Set();

            int result;
            while ((result = NativeInterop.GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
            {
                if (result == -1)
                {
                    Logger.Warning("Raw keyboard sink message loop failed (Win32 {Error})", Marshal.GetLastWin32Error());
                    break;
                }
                if (msg.message == NativeInterop.WM_INPUT) OnRawInput(msg);
                else if (msg.message == PingMessage && msg.hwnd == hwnd) Interlocked.Increment(ref _acked);
                NativeInterop.DispatchMessage(ref msg); // WndProc → DefWindowProc frees the WM_INPUT data
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Raw keyboard sink thread failed");
        }
        finally
        {
            _hwnd = IntPtr.Zero;
            // Remove only a registration that is still ours; another component may have replaced it.
            if (rawRegistered && QueryKeyboardRegistration(out var target, out _) == RegistrationQuery.Present && target == hwnd)
            {
                var remove = new[]
                {
                    new NativeInterop.RAWINPUTDEVICE
                    {
                        usUsagePage = NativeInterop.HID_USAGE_PAGE_GENERIC,
                        usUsage = NativeInterop.HID_USAGE_GENERIC_KEYBOARD,
                        dwFlags = NativeInterop.RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero,
                    },
                };
                NativeInterop.RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<NativeInterop.RAWINPUTDEVICE>());
            }
            if (hwnd != IntPtr.Zero) NativeInterop.DestroyWindow(hwnd);
            if (classRegistered) NativeInterop.UnregisterClass(className, instance);
            lock (_threadIdLock) _threadId = 0;
            ready.Set();
        }
    }

    // Reads the header only: no key, scan code or device handle is ever kept or logged.
    private void OnRawInput(NativeInterop.MSG msg)
    {
        var size = (uint)Marshal.SizeOf<NativeInterop.RAWINPUTHEADER>();
        var copied = NativeInterop.GetRawInputData(msg.lParam, NativeInterop.RID_HEADER, out var header, ref size, size);
        if (copied == uint.MaxValue || header.dwType != NativeInterop.RIM_TYPEKEYBOARD) return;
        Volatile.Write(ref _lastRawTime, unchecked((int)msg.time));
        Interlocked.Increment(ref _rawKeyCount);
    }

    private enum RegistrationQuery { Failed, None, Present }

    /// <summary>The process's keyboard raw-input registration, if any; two-call sizing with a retry for a growing list.</summary>
    private static RegistrationQuery QueryKeyboardRegistration(out IntPtr target, out int win32Error)
    {
        const int ErrorInsufficientBuffer = 122;
        target = IntPtr.Zero;
        win32Error = 0;
        var size = (uint)Marshal.SizeOf<NativeInterop.RAWINPUTDEVICE>();
        uint count = 0;
        if (NativeInterop.GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue)
        {
            win32Error = Marshal.GetLastWin32Error();
            if (win32Error != ErrorInsufficientBuffer) return RegistrationQuery.Failed;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (count == 0) return RegistrationQuery.None;
            var devices = new NativeInterop.RAWINPUTDEVICE[count];
            var written = NativeInterop.GetRegisteredRawInputDevices(devices, ref count, size);
            if (written == uint.MaxValue)
            {
                win32Error = Marshal.GetLastWin32Error();
                if (win32Error == ErrorInsufficientBuffer) continue;
                return RegistrationQuery.Failed;
            }
            for (var i = 0; i < written && i < devices.Length; i++)
            {
                if (devices[i].usUsagePage == NativeInterop.HID_USAGE_PAGE_GENERIC
                    && devices[i].usUsage == NativeInterop.HID_USAGE_GENERIC_KEYBOARD)
                {
                    target = devices[i].hwndTarget;
                    return RegistrationQuery.Present;
                }
            }
            return RegistrationQuery.None;
        }
        return RegistrationQuery.Failed;
    }

    /// <summary>Posts this tick's ping. True only when the previous ping was acknowledged and this one was posted.</summary>
    internal bool Ping()
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || _lost) return false;
        var previousAcked = Interlocked.Read(ref _acked) == Interlocked.Read(ref _posted);
        if (!PostPing(hwnd)) return false;
        Interlocked.Increment(ref _posted);
        return previousAcked;
    }

    /// <summary>The raw count and last-key tick; the watchdog reads them before the clock and the hook time, so a key mid-tick never reads as from the future (HKY-2).</summary>
    internal (long Count, uint LastTime) Sample() => (Interlocked.Read(ref _rawKeyCount), (uint)Volatile.Read(ref _lastRawTime));

    /// <summary>Milliseconds from a raw-key stamp to <paramref name="tickNow"/>; wrap-safe.</summary>
    internal static uint RawKeyAgeMs(uint tickNow, uint lastRawTime) => unchecked(tickNow - lastRawTime);

    /// <summary>The watchdog's per-tick read: native registration state plus a pre-read <see cref="Sample"/>, through <see cref="EvaluateCore"/>.</summary>
    internal Verdict Evaluate(bool pumping, long hookPassedKeyDowns, long rawKeyCount, uint rawKeyAgeMs,
        double hookSilentSeconds, double secondsSinceRestart, bool legacyWouldRestart, DateTime nowUtc)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || _lost) return Verdict.Fallback;
        var registration = QueryKeyboardRegistration(out var target, out _) switch
        {
            RegistrationQuery.Failed => Registration.QueryFailed,
            RegistrationQuery.Present when target == hwnd => Registration.Ours,
            _ => Registration.Lost,
        };
        return EvaluateCore(registration, pumping, hookPassedKeyDowns, rawKeyCount, rawKeyAgeMs,
            hookSilentSeconds, secondsSinceRestart, legacyWouldRestart, nowUtc);
    }

    /// <summary>Trust bookkeeping (proven, pumping, delivering) then <see cref="Decide"/>; no native calls, so tests drive it directly.</summary>
    internal Verdict EvaluateCore(Registration registration, bool pumping, long hookPassedKeyDowns, long rawKeyCount,
        uint rawKeyAgeMs, double hookSilentSeconds, double secondsSinceRestart, bool legacyWouldRestart, DateTime nowUtc)
    {
        if (_lost) return Verdict.Fallback;
        if (registration == Registration.Lost)
        {
            // shortcut: a lost registration is not re-registered, the watchdog keeps its legacy check for the run; upgrade if field logs show losses.
            _lost = true;
            Logger.Warning("Raw keyboard sink lost its registration to another component — watchdog keeps its legacy check for this run");
            return Verdict.Fallback;
        }

        var hookDelta = hookPassedKeyDowns - _hookBaseline;
        var rawDelta = rawKeyCount - _rawBaseline;
        _hookBaseline = hookPassedKeyDowns;
        _rawBaseline = rawKeyCount;

        if (rawKeyCount == 0) return Verdict.Fallback; // not proven: delivery has never worked
        if (!_firstKeyLogged)
        {
            _firstKeyLogged = true;
            Logger.Information("Raw keyboard sink observed its first key");
        }

        // Key-ups count on the raw side too, so a missed down can hide behind a received up within one tick; harmless at 15 s.
        // A key pressed just before a tick can read as a miss (raw input follows the hook chain); it costs one Suspect stretch, the safe direction.
        if (rawDelta > 0)
        {
            if (_suspect)
            {
                _suspect = false;
                if (nowUtc - _lastSuspectEndLogUtc >= SuspectLogThrottle)
                {
                    _lastSuspectEndLogUtc = nowUtc;
                    Logger.Information("Raw keyboard sink delivering again — raw evidence back on");
                }
            }
        }
        else if (hookDelta > 0 && !_suspect)
        {
            _suspect = true;
            if (nowUtc - _lastSuspectStartLogUtc >= SuspectLogThrottle)
            {
                _lastSuspectStartLogUtc = nowUtc;
                Logger.Information("Raw keyboard sink missed {Keys} key(s) the hook passed — watchdog uses its legacy check until raw input resumes", hookDelta);
            }
        }

        if (registration == Registration.QueryFailed || !pumping || _suspect) return Verdict.Fallback;
        return Decide(rawKeyAgeMs, hookSilentSeconds, secondsSinceRestart, legacyWouldRestart);
    }

    /// <summary>The trusted sink's decision table (HKY-2); thresholds are the legacy watchdog's.</summary>
    internal static Verdict Decide(uint rawKeyAgeMs, double hookSilentSeconds, double secondsSinceRestart, bool legacyWouldRestart)
    {
        if (rawKeyAgeMs > HotkeyService.WindowsInputFreshSeconds * 1000u)
            return hookSilentSeconds >= BackstopHookSilentSeconds && legacyWouldRestart ? Verdict.RestartBackstop : Verdict.Healthy;
        if (hookSilentSeconds < HotkeyService.NearMissHookSilentFloorSeconds) return Verdict.Healthy;
        if (hookSilentSeconds < HotkeyService.HookSilentThresholdSeconds) return Verdict.NearMiss;
        if (secondsSinceRestart < HotkeyService.MinSecondsBetweenAutoRestarts) return Verdict.NearMiss;
        return Verdict.RestartRaw;
    }
}
