using System.Runtime.InteropServices;

namespace BtNotifier;

/// Minimal Win32 surface for the tray process (no WinForms, no System.Drawing).
static class Native
{
    public const string TrayClassName = "BtNotifier.TrayWindow";

    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_TRAY = WM_APP + 1;     // tray icon callback
    public const uint WM_APP_RELOAD = WM_APP + 2;   // posted by the settings process after saving
    public const uint WM_APP_FLYOUT = WM_APP + 3;   // posted by worker threads to show the flyout on the UI thread

    public const long PBT_APMRESUMESUSPEND = 0x0007;
    public const long PBT_APMRESUMEAUTOMATIC = 0x0012;

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    public const int SW_SHOWNORMAL = 1;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // ---- user32 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpmParams);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? className, string? windowName);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    // ---- shell32 / kernel32 ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr ShellExecuteW(IntPtr hWnd, string? operation, string file, string? parameters, string? directory, int show);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr process, out uint count);
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);

    // ---- undocumented uxtheme ordinals: dark mode for native popup menus (Win10 1903+) ----
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    /// Native context menu follows Windows dark/light mode, or the user's explicit choice.
    public static void SetMenuTheme(string? appearance)
    {
        int mode = (appearance ?? "System").ToLowerInvariant() switch
        {
            "dark" => 2,   // ForceDark
            "light" => 3,  // ForceLight
            _ => 1,        // AllowDark (follow system)
        };
        try { SetPreferredAppMode(mode); FlushMenuThemes(); }
        catch { /* older Windows: menu stays light */ }
    }

    /// Full compacting GC, then hand every unused page back to Windows.
    /// This is what makes the idle tray process show only a few MB in Task Manager.
    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        }
        catch { /* cosmetic */ }
    }

    /// One-line resource snapshot, used for leak checks: if handles / GDI / USER objects / threads
    /// keep climbing over hours, something leaks. Stable numbers mean no leak.
    public static string ResourceStats()
    {
        try
        {
            var self = new IntPtr(-1); // pseudo-handle for the current process
            GetProcessHandleCount(self, out uint handles);
            uint gdi = GetGuiResources(self, 0), user = GetGuiResources(self, 1);
            return $"working set {Environment.WorkingSet / (1024 * 1024)} MB, managed heap {GC.GetTotalMemory(false) / 1024} KB, " +
                   $"handles {handles}, GDI {gdi}, USER {user}, pool threads {ThreadPool.ThreadCount}";
        }
        catch { return "n/a"; }
    }
}
