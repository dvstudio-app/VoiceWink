using System.Runtime.InteropServices;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// Consolidated Win32 P/Invoke declarations used across the application.
/// Replaces CsWin32 generation for full control over marshalling.
/// </summary>
internal static class NativeInterop
{
    // --- Final-path resolution (ENH-6 reference-image trust boundary) ---
    // GetFinalPathNameByHandle resolves every reparse point (junction/symlink) in the
    // OPENED handle's path, so containment can be verified against what the kernel
    // actually opened — a lexical Path.GetFullPath check cannot see reparse points.

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(
        global::Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    // CreateFileW is needed only to obtain a DIRECTORY handle (FileStream cannot open
    // directories); FILE_FLAG_BACKUP_SEMANTICS is the documented requirement for that.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern global::Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // --- DPI Awareness ---

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // --- Window display affinity (screen-capture exclusion) ---

    /// <summary>Normal window — included in captures.</summary>
    public const uint WDA_NONE = 0x00000000;
    /// <summary>Excluded from capture by supported public capture mechanisms and DWM composition —
    /// screenshots / recordings / screen shares omit the window while it stays visible on screen.
    /// Requires Windows 10 2004 (build 19041), which is already this app's
    /// <c>TargetPlatformMinVersion</c>. Not a guarantee against every possible capture stack.</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    // --- Clipboard ---

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int CountClipboardFormats();

    // Open-free, per-window-station clipboard mutation counter (REL-16 paste diagnostics).
    // Returns 0 when the caller has no clipboard access to its window station. Deliberately
    // NO SetLastError — the API sets no error, and the flag would clear the cached last-error
    // slot the paste path snapshots right after SendInput.
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport("ole32.dll")]
    public static extern int OleGetClipboard(
        [MarshalAs(UnmanagedType.Interface)]
        out global::System.Runtime.InteropServices.ComTypes.IDataObject? dataObject);

    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard(
        [MarshalAs(UnmanagedType.Interface)]
        global::System.Runtime.InteropServices.ComTypes.IDataObject? dataObject);

    // --- Memory ---

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    // --- Input Simulation ---

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // --- Cursor / DPI ---

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // --- Last input / tick count (for HotkeyService watchdog) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;  // GetTickCount()-based: ms since system boot
    }

    /// <summary>
    /// Time of last user input event (keyboard or mouse) that Windows observed, in
    /// <see cref="GetTickCount"/> units. If our keyboard hook has been silent much longer
    /// than this value reports, the hook may have been silently removed by Windows.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Milliseconds since system boot. Wraps after ~49.7 days; use unchecked subtraction.</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    /// <summary>
    /// Returns the current state of a virtual key. High bit (0x8000) is set if the key
    /// is currently physically down. Used by HotkeyService to disambiguate "stuck active
    /// key whose key-up was lost" from "modifier key genuinely held during a long
    /// push-to-talk" — modifiers don't auto-repeat, so timestamp-only heuristics can't tell
    /// the two cases apart.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // --- Keyboard layout inspection (HKY-3 AltGr detection) ---
    // KeyboardLayoutProfile asks whether any installed layout puts characters behind AltGr,
    // because AltGr arrives as a synthetic LeftControl + RightAlt pair and would otherwise
    // fire every Ctrl+X binding on a European keyboard.

    /// <summary>
    /// Fills <paramref name="lpList"/> with the input locale identifiers of every keyboard
    /// layout installed for the system. Called first with <paramref name="nBuff"/> = 0 and a
    /// null list to obtain the count.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    /// <summary>
    /// Maps a character to the virtual key and shift state that produce it on
    /// <paramref name="dwhkl"/>. Low byte = virtual key, high byte = shift state
    /// (1 Shift, 2 Ctrl, 4 Alt); returns -1 when the character is unreachable on that layout.
    /// A layout that uses the right-hand Alt as a shift key reports Ctrl+Alt, per the
    /// documented VkKeyScanEx behaviour.
    /// </summary>
    /// <remarks>
    /// Chosen over ToUnicodeEx deliberately: ToUnicodeEx mutates the layout's dead-key state
    /// as a side effect, so probing with it can corrupt the user's next keystroke. This call
    /// is a pure query.
    /// </remarks>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanExW(char ch, IntPtr dwhkl);

    // --- Window Management ---

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>Enumerate child windows of <paramref name="hWndParent"/> (reuses the
    /// <see cref="EnumWindowsProc"/> delegate signature). Used to detect an out-of-process
    /// WebView2 render host (a child window owned by a different process / msedgewebview2.exe).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>True if the handle identifies an existing window. Used by the paste
    /// target-liveness gate — a destroyed target must fail fast instead of being
    /// force-activated (the HWND value may already be recycled by another window).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>Alt-Tab-equivalent activation. Last resort of the paste foreground
    /// escalation ladder: switches to the window the way the system's own Alt-Tab
    /// does, bypassing the foreground lock that can defeat SetForegroundWindow.
    /// Deprecated-but-stable API; only called after the target re-passed the
    /// liveness/identity gate.</summary>
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>True if the window is minimized (iconic). Used to decide whether a focus-restore
    /// target needs SW_RESTORE before activation — an unconditional restore would un-maximize a
    /// maximized target.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    // --- Native Popup Menu (for system tray) ---

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    // Dark mode for native menus — uses undocumented uxtheme ordinals
    // widely used by Windows Terminal, Firefox, etc. (Windows 10 1903+)
    // Ordinal 135 = SetPreferredAppMode, Ordinal 136 = FlushMenuThemes
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>
    /// Safely calls the undocumented SetPreferredAppMode ordinal.
    /// Returns false if the entry point is not available on this Windows version.
    /// </summary>
    internal static bool TrySetPreferredAppMode(int mode)
    {
        try
        {
            SetPreferredAppMode(mode);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Safely calls the undocumented FlushMenuThemes ordinal.
    /// Returns false if the entry point is not available on this Windows version.
    /// </summary>
    internal static bool TryFlushMenuThemes()
    {
        try
        {
            FlushMenuThemes();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public const int AppMode_ForceDark = 2;
    public const int AppMode_ForceLight = 3;

    // --- App Identity / Window Icon ---
    //
    // VoiceWink is launched via "dotnet VoiceWink.dll" rather than as a stand-alone exe,
    // so the OS process is dotnet.exe. Two consequences need fixing here:
    //   1. Without an explicit AUMID, the shell groups our taskbar entry with every other
    //      .NET app hosted by the same dotnet.exe (and reuses dotnet's icon cache slot).
    //   2. AppWindow.SetIcon(string) only loads one icon size and stamps it into both
    //      ICON_SMALL and ICON_BIG slots — the taskbar wants 32px (more at hi-DPI) and
    //      ends up rendering a scaled-up 16px or a stale shell-cache placeholder.

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // MiniRecorder drag (PILL-4): system drag-start threshold in pixels — a press that
    // wobbles less than this is a click, not a drag. (The drag itself is a manual
    // captured-pointer move: the native WM_NCLBUTTONDOWN loop gets no live WM_POINTER
    // updates under WinUI 3, so the window only moved at release.)
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // No DestroyIcon here on purpose: the only HICONs this app loads are the window's
    // own icon slots (MainWindow.SetAppIcon), which must stay valid for as long as the
    // window exists — the shell re-reads them via WM_GETICON every time the taskbar
    // button is rebuilt after a hide/show. Freeing them early is what made the taskbar
    // icon go generic after reopening from the tray; the OS reclaims them at exit.

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTCOLOR = 0x0000;
    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;
    public const int SM_CXICON = 11;
    public const int SM_CYICON = 12;

    // --- Constants ---

    public const uint CF_UNICODETEXT = 13;
    public const uint CF_DIB = 8;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // Virtual key codes
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;
    public const ushort VK_RETURN = 0x0D;

    // Side-specific modifier VKs — the paste pipeline's physically-held-modifier
    // guard probes and releases exactly the held side (a generic VK_CONTROL
    // release does not reliably clear a held RIGHT-side key state).
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;

    /// <summary>Inert nudge key for the foreground escalation ladder: not in
    /// HotkeyService's assignable key lists (passes the LL hook untouched) and
    /// meaningless to real applications, yet a SendInput of it earns the calling
    /// process "received the last input event" credit for SetForegroundWindow.</summary>
    public const ushort VK_F24 = 0x87;

    // ShowWindow commands
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;

    // Window style constants
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_VISIBLE = 0x10000000;

    // SetWindowPos constants
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    // --- Structures ---

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // --- Listener window (WM_DISPLAYCHANGE receipt — see Services/System/DisplayChangeListener) ---

    public const uint WM_NULL = 0x0000;
    public const uint WM_DISPLAYCHANGE = 0x007E;

    /// <summary>Window procedure delegate for the display-change listener window.
    /// Instances MUST be kept alive (stored in a field) for the lifetime of the
    /// registered class — the native side holds only the function pointer.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // --- Monitor Info ---

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // --- DWM cloaking (CLK-1 — MiniRecorder hidden parking) ---

    /// <summary>DWMWA_CLOAK: hide a window from presentation + hit-testing while
    /// keeping WS_VISIBLE, its composition surface, AND its real on-monitor
    /// position (→ real DPI association; WM_DPICHANGED fires on cloaked moves).
    /// The property off-screen parking provably lacks (2026-07-06 evidence).</summary>
    public const int DWMWA_CLOAK = 13;

    /// <summary>DWMWA_CLOAKED read-back: bitmask of WHO cloaked the window.
    /// ANY nonzero value means not visible (e.g. SHELL = other virtual desktop);
    /// the APP bit attributes our own cloak.</summary>
    public const int DWMWA_CLOAKED = 14;
    public const uint DWM_CLOAKED_APP = 0x0000001;
    public const uint DWM_CLOAKED_SHELL = 0x0000002;
    public const uint DWM_CLOAKED_INHERITED = 0x0000004;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out uint pvAttribute, int cbAttribute);

    // --- Monitor DPI ---

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int MDT_EFFECTIVE_DPI = 0;

    // --- Helpers ---

    public static INPUT CreateKeyInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>
    /// The extended-key VKs the paste pipeline may inject. Right-side modifiers
    /// (and both Win keys) are extended keys: a synthetic event for them without
    /// KEYEVENTF_EXTENDEDKEY maps to the LEFT-side key and would fail to clear a
    /// physically-held right-side state.
    /// </summary>
    internal static bool IsExtendedKey(ushort vk)
        => vk is VK_RMENU or VK_RCONTROL or VK_LWIN or VK_RWIN;

    /// <summary>
    /// Scan-code-enriched variant of <see cref="CreateKeyInput"/>: populates
    /// <c>wScan</c> via <see cref="MapVirtualKey"/> so targets that read raw scan
    /// codes (games, terminals with raw input, RDP) observe a complete event,
    /// while the VK remains authoritative (no KEYEVENTF_SCANCODE — layout-safe),
    /// and sets KEYEVENTF_EXTENDEDKEY for the extended-key set so side-specific
    /// modifier releases actually land on the right-side key.
    /// </summary>
    public static INPUT CreateKeyInputEx(ushort vk, bool keyUp)
    {
        var flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    // --- Clipboard-owner diagnostics ---

    /// <summary>Window that currently owns the clipboard (IntPtr.Zero when none/unowned).
    /// Logged when OpenClipboard retries are exhausted so contention is attributable
    /// to a specific process instead of "clipboard API flaked".</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardOwner();

    // --- Process elevation (UIPI detection) ---

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevationClass = 20; // TOKEN_INFORMATION_CLASS.TokenElevation

    /// <summary>
    /// Best-effort elevation probe for the UIPI paste gate. Returns true/false when
    /// the token could be queried, null when it could not (access denied, process
    /// gone) — callers treat null as "unknown" and fail OPEN (proceed with the
    /// paste) because a wrong skip would be worse than today's silent UIPI drop.
    /// Handles are closed on every path.
    /// </summary>
    public static bool? TryGetProcessElevation(uint pid)
    {
        if (pid == 0) return null;

        var process = IntPtr.Zero;
        var token = IntPtr.Zero;
        try
        {
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;

            if (!OpenProcessToken(process, TOKEN_QUERY, out token) || token == IntPtr.Zero)
                return null;

            if (!GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(uint), out _))
                return null;

            return elevated != 0;
        }
        catch (global::System.Exception)
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    // --- Common File Dialog ---

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSaveFileName(ref OPENFILENAME lpofn);

    public const int OFN_OVERWRITEPROMPT = 0x00000002;
    public const int OFN_PATHMUSTEXIST = 0x00000800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    // --- Process / Thread priority + Efficiency Mode (EcoQoS) opt-out ---
    //
    // Why this exists: Windows 11 may put background processes into Efficiency Mode
    // (PROCESS_POWER_THROTTLING_EXECUTION_SPEED enabled + IDLE_PRIORITY_CLASS),
    // which starves the low-level keyboard hook of scheduling time under CPU
    // contention (e.g. Excel auto-calc). When the LL hook callback exceeds
    // LowLevelHooksTimeout (~300ms by default) Windows silently removes the hook.
    // Observed live in the 2026-05-23 session log: hook silent 60-200s while
    // Windows kept seeing input, with the dotnet host running at Idle priority.
    //
    // ProcessPriorityTuner uses these to pin priority back to Normal and opt out
    // of EcoQoS at startup. SetThreadPriority is used by HotkeyService to bump
    // the SharpHook callback thread to AboveNormal so it survives brief spikes.

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetThreadPriority(IntPtr hThread);

    public const int THREAD_PRIORITY_NORMAL = 0;
    public const int THREAD_PRIORITY_ABOVE_NORMAL = 1;
    public const int THREAD_PRIORITY_HIGHEST = 2;

    /// <summary>
    /// The only thread priority that ESCAPES its process priority class. In every
    /// dynamic (non-realtime) class this saturates to absolute priority 15, above
    /// all NORMAL_PRIORITY_CLASS work — which is exactly why REL-14's re-pin
    /// watchdog uses it: THREAD_PRIORITY_HIGHEST inside IDLE_PRIORITY_CLASS only
    /// reaches 6 and still loses to every Normal-class thread.
    ///
    /// <para><b>In REALTIME_PRIORITY_CLASS it maps to 31, not 15</b> — a hard-realtime
    /// priority that can starve kernel threads. Never set it without first checking
    /// the process is not realtime-class (see <c>PriorityRepinGate</c>).</para>
    /// </summary>
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    /// <summary>GetThreadPriority's failure sentinel — a legal-looking int, so it must be tested for.</summary>
    public const int THREAD_PRIORITY_ERROR_RETURN = 0x7FFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    // Process priority classes. NOT MONOTONIC — ordering these numerically is a bug:
    // NORMAL(0x20) < IDLE(0x40) < HIGH(0x80) < REALTIME(0x100) < BELOW_NORMAL(0x4000)
    // < ABOVE_NORMAL(0x8000). Any range test ("< NORMAL means demoted") is silently
    // wrong, which is why PriorityRepinGate switches on exact values and is pinned by
    // a truth table rather than compared.
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint REALTIME_PRIORITY_CLASS = 0x00000100;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;

    /// <summary>
    /// Interrupt time EXCLUDING time the system spent suspended, in 100 ns units.
    /// GetTickCount64/Environment.TickCount64 include sleep time, so a delta measured
    /// across a sleep/resume reports the whole suspension — VoiceWink subscribes to
    /// sleep/wake events, so that skew is live, not theoretical. Success is reported
    /// separately from the value; a false return leaves the out param meaningless.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public enum ProcessInformationClass
    {
        ProcessMemoryPriority = 0,
        ProcessMemoryExhaustionInfo = 1,
        ProcessAppMemoryInfo = 2,
        ProcessInPrivateInfo = 3,
        ProcessPowerThrottling = 4,
        ProcessTelemetryCoverageInfo = 6,
        ProcessProtectionLevelInfo = 7,
        ProcessLeapSecondInfo = 8,
    }

    /// <summary>
    /// State block for SetProcessInformation(ProcessPowerThrottling). To opt out
    /// of Efficiency Mode set <see cref="ControlMask"/> =
    /// PROCESS_POWER_THROTTLING_EXECUTION_SPEED and <see cref="StateMask"/> = 0.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        ProcessInformationClass processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize);

    /// <summary>
    /// The user's configured double-click interval in milliseconds (UI-3).
    ///
    /// <para>Used to size the toggle button's cancel-arming window, so a second click of a habitual
    /// double-click cannot abort the operation the FIRST click started. A hardcoded constant is the
    /// wrong authority here — Windows lets this be raised well past 300 ms in the Mouse control
    /// panel, and an accessibility setting is exactly the case a fixed number gets wrong.</para>
    /// </summary>
    [global::System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetDoubleClickTime();
    // --- TRN-29 slice 2: parakeet-server child-process lifecycle ---
    // The child must be inside a kill-on-close job AT CREATION (CreateProcessW +
    // STARTUPINFOEX/PROC_THREAD_ATTRIBUTE_JOB_LIST) - assigning after Process.Start leaves a
    // crash window where an orphaned server survives app death (slice-2 plan round, Blocker 1).
    // The bound ephemeral port is read back from the kernel's TCP table keyed by the child's
    // PID (GetExtendedTcpTable), because the server's own banner prints the REQUESTED port
    // (literal 0) and a same-user spoofer cannot forge kernel PID attribution (Blocker 2).

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    public enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9,
    }

    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    // 0x2000D == ProcThreadAttributeValue(13 /*JobList*/, false, true, false)
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = new(0x2000D);

    // TRN-60: the child's captured stdout/stderr. 0x20002 == ProcThreadAttributeValue(2
    // /*HandleList*/, false, true, false) — with bInheritHandles TRUE, this list is what stops
    // every other inheritable handle in the app from being duplicated into a session-long child.
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x20002);

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    public const uint HANDLE_FLAG_INHERIT = 0x00000001;
    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint GENERIC_READ = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    // lpCommandLine is a StringBuilder deliberately: CreateProcessW's Unicode variant may WRITE
    // into the command-line buffer, and marshalling a managed string there is the documented
    // corruption trap.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    public const uint STILL_ACTIVE = 259;

    // --- The PID-keyed listener readback (iphlpapi) ---

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        TCP_TABLE_CLASS tableClass,
        uint reserved);

    public enum TCP_TABLE_CLASS
    {
        TCP_TABLE_OWNER_PID_LISTENER = 3,
    }

    public const int AF_INET = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;     // network byte order
        public uint localPort;     // port in the HIGH-ORDER 16 bits (network byte order)
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }
}
