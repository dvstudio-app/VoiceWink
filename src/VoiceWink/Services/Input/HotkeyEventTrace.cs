using System.Runtime.InteropServices;
using System.Threading.Channels;
using Serilog;
using Serilog.Core;
using SharpHook.Native;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Input;

/// <summary>HKY-9: an observe-only keyboard hook logging the raw hotkey events at Debug while verbose logging is on.</summary>
public sealed class HotkeyEventTrace : IDisposable
{
    private static ILogger Logger => Log.ForContext<HotkeyEventTrace>();

    private const uint WmReinstall = NativeInterop.WM_APP + 1;
    private const int ChannelCapacity = 256;

    // LL hooks report side-specific VKs; the generic VK_CONTROL / VK_MENU are listed in case a driver sends them.
    private static readonly uint[] AltGrPairVks = [0xA2, 0xA3, 0xA4, 0xA5, 0x11, 0x12];

    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfLowerIlInjected = 0x02;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfAltDown = 0x20;
    private const uint LlkhfUp = 0x80;

    private readonly HotkeyService _hotkeys;
    private readonly object _lifecycleLock = new();
    private volatile TraceSession? _active;
    private bool _started;
    private bool _disposed;

    public HotkeyEventTrace(HotkeyService hotkeys) => _hotkeys = hotkeys;

    /// <summary>Subscribe to the verbose switch and the hotkey hook, then apply the current level. Call once.</summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_started || _disposed) return;
            _started = true;
        }
        LogLevelControl.Switch.MinimumLevelChanged += OnLevelChanged;
        _hotkeys.HookEnabled += OnHotkeyHookEnabled;
        ReconcileInBackground();
    }

    private void OnLevelChanged(object? sender, LoggingLevelSwitchChangedEventArgs e) => ReconcileInBackground();

    // Off the UI thread: a start waits for the hook thread and a stop joins it.
    private void ReconcileInBackground() => _ = Task.Run(Reconcile);

    // Raised on SharpHook's hook thread: post only, never wait on the lifecycle lock; a dead session is replaced off-thread.
    private void OnHotkeyHookEnabled()
    {
        if (_active is { } session && !session.RequestReinstall()) ReconcileInBackground();
    }

    private void Reconcile()
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                var wanted = LogLevelControl.IsVerbose;
                var current = _active;
                if (current is not null && (!wanted || current.HasExited))
                {
                    _active = null;
                    current.Stop();
                    current = null;
                }
                if (wanted && current is null)
                {
                    // Published before the thread starts, so a HookEnabled during startup still re-installs it.
                    var session = new TraceSession(this);
                    _active = session;
                    if (!session.Start()) _active = null;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hotkey event trace start/stop failed");
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        LogLevelControl.Switch.MinimumLevelChanged -= OnLevelChanged;
        _hotkeys.HookEnabled -= OnHotkeyHookEnabled;
        var session = _active;
        _active = null;
        session?.Stop();
    }

    /// <summary>Whether a key event is traced: the AltGr pair always, the recording trigger unless it is a typing key.</summary>
    internal static bool ShouldTrace(uint vk, KeyCode recordingTrigger)
    {
        if (Array.IndexOf(AltGrPairVks, vk) >= 0) return true;
        var triggerVk = HotkeyService.MapKeyCodeToVk(recordingTrigger);
        if (triggerVk == 0 || vk != (uint)triggerVk) return false;
        return !HotkeyBinding.IsTypingKey(recordingTrigger);
    }

    /// <summary>The set LLKHF_* flags by name, <c>none</c> when clear; unknown bits as hex.</summary>
    internal static string DescribeFlags(uint flags)
    {
        var names = new List<string>(5);
        if ((flags & LlkhfUp) != 0) names.Add("UP");
        if ((flags & LlkhfExtended) != 0) names.Add("EXTENDED");
        if ((flags & LlkhfAltDown) != 0) names.Add("ALTDOWN");
        if ((flags & LlkhfInjected) != 0) names.Add("INJECTED");
        if ((flags & LlkhfLowerIlInjected) != 0) names.Add("LOWER_IL_INJECTED");
        var unknown = flags & ~(LlkhfUp | LlkhfExtended | LlkhfAltDown | LlkhfInjected | LlkhfLowerIlInjected);
        if (unknown != 0) names.Add($"0x{unknown:X}");
        return names.Count == 0 ? "none" : string.Join('|', names);
    }

    internal static string MessageName(uint message) => message switch
    {
        0x0100 => "KEYDOWN",
        0x0101 => "KEYUP",
        0x0104 => "SYSKEYDOWN",
        0x0105 => "SYSKEYUP",
        _ => $"0x{message:X4}",
    };

    /// <summary>One copied hook event. Kind Installed is the marker a (re)install writes.</summary>
    internal readonly record struct TraceEvent(
        long Sequence, int Generation, bool Installed, uint Message, uint Vk, uint Scan, uint Flags, uint Tick, ulong ExtraInfo);

    /// <summary>The Debug line for one event; pure so the format is test-pinned.</summary>
    internal static string Render(TraceEvent e) => e.Installed
        ? $"Hook trace #{e.Sequence} g{e.Generation}: installed"
        : $"Hook trace #{e.Sequence} g{e.Generation}: {MessageName(e.Message)} vk=0x{e.Vk:X2} scan=0x{e.Scan:X2} flags={DescribeFlags(e.Flags)} tick={e.Tick} extra=0x{e.ExtraInfo:X}";

    /// <summary>One "on" period: a dedicated thread with its own message loop and hook.</summary>
    private sealed class TraceSession
    {
        private readonly HotkeyEventTrace _owner;
        private readonly Channel<TraceEvent> _channel;
        private readonly NativeInterop.LowLevelKeyboardProc _proc; // held so the GC never collects the callback
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private IntPtr _hook;
        private volatile uint _threadId;
        private volatile bool _exited;
        private readonly object _threadIdLock = new();
        private bool _stopRequested; // guarded by _threadIdLock
        private long _sequence;
        private int _generation;
        private long _dropped;
        private long _callbackFaults;

        internal TraceSession(HotkeyEventTrace owner)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<TraceEvent>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                },
                _ => Interlocked.Increment(ref _dropped));
            _proc = Callback;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "HotkeyEventTrace",
                // A starved low-level hook thread delays every keystroke on the machine.
                Priority = ThreadPriority.AboveNormal,
            };
        }

        /// <summary>False when the hook could not be installed, so the next verbose toggle retries.</summary>
        internal bool Start()
        {
            _ = Task.Run(DrainAsync);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warning("Hotkey event trace thread did not report ready within 2 s");
                Stop(); // a late thread must still see the stop, or its hook outlives the session
                return false;
            }
            if (_threadId == 0) return false;
            Logger.Information("Hotkey event trace on (verbose logging)");
            return true;
        }

        /// <summary>True once the hook thread has ended, e.g. after a failed re-install.</summary>
        internal bool HasExited => _exited;

        /// <summary>False when the thread has ended or not started, so the caller can replace the session.</summary>
        internal bool RequestReinstall()
        {
            var id = _threadId;
            if (id == 0) return false;
            return NativeInterop.PostThreadMessage(id, WmReinstall, UIntPtr.Zero, IntPtr.Zero);
        }

        internal void Stop()
        {
            uint id;
            lock (_threadIdLock)
            {
                _stopRequested = true;
                id = _threadId;
            }
            if (id != 0) NativeInterop.PostThreadMessage(id, NativeInterop.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(TimeSpan.FromSeconds(1)))
                Logger.Warning("Hotkey event trace thread did not exit within 1 s");
        }

        private void Run()
        {
            try
            {
                // The queue exists before the id is shared, so no post to this thread can be lost.
                NativeInterop.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeInterop.PM_NOREMOVE);
                lock (_threadIdLock)
                {
                    if (_stopRequested) return;
                    _threadId = NativeInterop.GetCurrentThreadId();
                }
                if (!Install()) return;
                _ready.Set();
                while (NativeInterop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message != WmReinstall) continue;
                    Uninstall();
                    if (!Install()) return;
                }
            }
            catch (Exception ex)
            {
                Uninstall();
                Logger.Error(ex, "Hotkey event trace thread failed");
            }
            finally
            {
                Uninstall();
                _threadId = 0;
                _exited = true;
                _ready.Set();
                _channel.Writer.TryComplete();
            }
        }

        private bool Install()
        {
            _hook = NativeInterop.SetWindowsHookEx(
                NativeInterop.WH_KEYBOARD_LL, _proc, NativeInterop.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                // No hook is installed at this point, so a synchronous log write delays nothing.
                Logger.Warning("Hotkey event trace could not install its hook (Win32 error {Error})", Marshal.GetLastWin32Error());
                return false;
            }
            var generation = ++_generation;
            _channel.Writer.TryWrite(new TraceEvent(Interlocked.Increment(ref _sequence), generation, true, 0, 0, 0, 0, NativeInterop.GetTickCount(), 0));
            return true;
        }

        private void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            NativeInterop.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    // vkCode is the struct's first field; read it alone so untraced keys copy nothing.
                    var vk = (uint)Marshal.ReadInt32(lParam);
                    if (ShouldTrace(vk, _owner._hotkeys.Configuration.Recording.Trigger))
                    {
                        var data = Marshal.PtrToStructure<NativeInterop.KBDLLHOOKSTRUCT>(lParam);
                        _channel.Writer.TryWrite(new TraceEvent(
                            Interlocked.Increment(ref _sequence), _generation, false, (uint)wParam,
                            data.vkCode, data.scanCode, data.flags, data.time, data.dwExtraInfo.ToUInt64()));
                    }
                }
                catch
                {
                    // An exception must never cross into the native hook chain.
                    Interlocked.Increment(ref _callbackFaults);
                }
            }
            return NativeInterop.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var e in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                    Logger.Debug("{HookTraceLine:l}", Render(e));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Hotkey event trace drain failed");
            }
            Logger.Information(
                "Hotkey event trace off ({Lines} lines, {Dropped} dropped, {Faults} callback faults)",
                Interlocked.Read(ref _sequence) - Interlocked.Read(ref _dropped), Interlocked.Read(ref _dropped), Interlocked.Read(ref _callbackFaults));
        }
    }
}
