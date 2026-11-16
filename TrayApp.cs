using System.Runtime.InteropServices;
using System.Text;
using static BtNotifier.Native;

namespace BtNotifier;

/// The always-running part. Deliberately tiny: a hidden window, a tray icon, a native popup menu and the
/// Bluetooth trackers. No WinForms / System.Drawing is loaded here; windows with UI (settings, diagnostics)
/// run as short-lived separate processes (see UiHost), so their memory is returned to Windows when closed.
sealed class TrayApp
{
    const uint TIMER_TICK = 1;
    const uint CMD_SETTINGS = 1, CMD_TEST = 2, CMD_DIAG = 3, CMD_LOG = 4, CMD_EXIT = 5;

    readonly WndProc _proc;            // kept in a field so the delegate is never garbage collected
    readonly object _lock = new();
    List<DeviceTracker> _trackers = new();
    Config _cfg = new();
    IntPtr _hwnd, _icon;
    uint _taskbarCreated;
    string _tip = "Bluetooth Notifier";
    int _ticking, _ticks;
    long _tickStartedTicks;

    TrayApp() { _proc = OnMessage; }

    public static int Run()
    {
        using var mutex = new Mutex(true, "BtNotifier.Singleton", out bool created);
        if (!created) return 0; // already running

        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // crisp menu on high-DPI screens

        var app = new TrayApp();
        return app.Init() ? app.Loop() : 1;
    }

    bool Init()
    {
        _cfg = Config.Load();
        Autostart.Refresh();
        SetMenuTheme(_cfg.Appearance);
        Toast.Init(_cfg.ResolveLogo());

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = TrayClassName,
        };
        if (RegisterClassExW(ref wc) == 0) return false;

        // Hidden top-level window (not message-only, so it also receives power and taskbar broadcasts)
        _hwnd = CreateWindowExW(0, TrayClassName, "Bluetooth Notifier", 0, 0, 0, 0, 0,
                                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;

        Flyout.Init(_hwnd);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadTrayIcon();
        AddIcon();
        SetTimer(_hwnd, (UIntPtr)TIMER_TICK, 10_000, IntPtr.Zero);

        ApplyConfig(_cfg);
        if (_cfg.Devices.Count == 0) OpenSettings(); // first launch: let the user pick a device
        Log.Info("Started");
        return true;
    }

    int Loop()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return 0;
    }

    // ───────────── window procedure ─────────────

    IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_APP_TRAY)
            {
                uint ev = (uint)((long)lParam & 0xFFFF);
                if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu();
                else if (ev == WM_LBUTTONDBLCLK) OpenSettings();
                return IntPtr.Zero;
            }
            if (msg == WM_TIMER) { OnTimer(); return IntPtr.Zero; }
            if (msg == WM_POWERBROADCAST)
            {
                long ev = (long)wParam;
                if (ev == PBT_APMRESUMEAUTOMATIC || ev == PBT_APMRESUMESUSPEND) _ = ResumeAsync();
                return new IntPtr(1);
            }
            if (msg == WM_APP_RELOAD) { Reload(); return IntPtr.Zero; }
            if (msg == WM_APP_FLYOUT) { Flyout.ShowPending(); return IntPtr.Zero; }
            if (_taskbarCreated != 0 && msg == _taskbarCreated) { AddIcon(); return IntPtr.Zero; } // Explorer restarted
            if (msg == WM_DESTROY) { Cleanup(); PostQuitMessage(0); return IntPtr.Zero; }
        }
        catch (Exception e) { Log.Info("Window message failed: " + e.Message); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ───────────── tray icon ─────────────

    static IntPtr LoadTrayIcon()
    {
        try
        {
            var small = new IntPtr[1];
            if (ExtractIconExW(Environment.ProcessPath!, 0, null, small, 1) > 0) return small[0];
        }
        catch { }
        return IntPtr.Zero;
    }

    NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    void AddIcon()
    {
        var nid = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    void SetTip(string tip)
    {
        _tip = tip;
        var nid = NewData(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    // ───────────── menu ─────────────

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_SETTINGS, "Settings...");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_TEST, "Test notification");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_DIAG, "Diagnostics...");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_LOG, "Open log");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_EXIT, "Exit");
        SetMenuDefaultItem(menu, CMD_SETTINGS, 0); // bold

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required, otherwise the menu does not close when clicking elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch ((uint)cmd)
        {
            case CMD_SETTINGS: OpenSettings(); break;
            case CMD_TEST: _ = TestAsync(); break;
            case CMD_DIAG: _ = ShowDiagnosticsAsync(); break;
            case CMD_LOG: OpenFile(AppPaths.LogFile); break;
            case CMD_EXIT: Quit(); break;
        }
    }

    static void OpenSettings() =>
        ShellExecuteW(IntPtr.Zero, "open", Environment.ProcessPath!, "--settings", null, SW_SHOWNORMAL);

    static void OpenFile(string path)
    {
        if (File.Exists(path)) ShellExecuteW(IntPtr.Zero, "open", path, null, null, SW_SHOWNORMAL);
    }

    // ───────────── trackers ─────────────

    List<DeviceTracker> Snapshot() { lock (_lock) return _trackers; }

    void ApplyConfig(Config cfg)
    {
        var fresh = cfg.Devices.Select(d => new DeviceTracker(d, cfg)).ToList();

        List<DeviceTracker> old;
        lock (_lock) { old = _trackers; _trackers = fresh; _cfg = cfg; }
        foreach (var t in old) t.Dispose();

        SetMenuTheme(cfg.Appearance);
        SetTip(fresh.Count == 0 ? "Bluetooth Notifier - no device selected" : "Bluetooth Notifier");
        _ = Task.Run(async () => { foreach (var t in fresh) await t.StartAsync(); });
    }

    /// The settings process saved config.json and posted WM_APP_RELOAD.
    void Reload()
    {
        var before = _cfg;
        var cfg = Config.Load();
        ApplyConfig(cfg);

        if (!before.FirstRunDone && cfg.FirstRunDone)
            Notifier.Info(cfg, "hello", "Bluetooth Notifier is running", "It lives in the system tray. Right-click the icon for options.");
    }

    void OnTimer()
    {
        // Watchdog: if one tick has been stuck for minutes, let the next one run
        if (Volatile.Read(ref _ticking) == 1 &&
            DateTime.UtcNow.Ticks - Volatile.Read(ref _tickStartedTicks) > TimeSpan.FromMinutes(2).Ticks)
        {
            Log.Info("Tick stuck for more than 2 minutes - resetting");
            Interlocked.Exchange(ref _ticking, 0);
        }

        _ = TickAsync();

        int n = _ticks++;
        if (n % 6 == 0) TrimMemory();                                  // first at ~10 s, then about once a minute
        if (n % 180 == 0) Log.Info("stats: " + ResourceStats());       // every ~30 min: compare over hours to spot leaks
    }

    async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        Volatile.Write(ref _tickStartedTicks, DateTime.UtcNow.Ticks);
        try { foreach (var t in Snapshot()) await t.TickAsync(); }
        catch (Exception e) { Log.Info("Tick failed: " + e.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    async Task ResumeAsync()
    {
        await Task.Delay(3000); // give the Bluetooth stack time to come back
        foreach (var t in Snapshot()) await t.OnResumeAsync();
    }

    async Task TestAsync()
    {
        var list = Snapshot();
        if (list.Count == 0) { OpenSettings(); return; }
        foreach (var t in list) await t.TestAsync();
    }

    async Task ShowDiagnosticsAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Bluetooth Notifier {typeof(TrayApp).Assembly.GetName().Version?.ToString(3)}");
            sb.AppendLine("Process: " + ResourceStats());
            sb.AppendLine();
            foreach (var t in Snapshot()) sb.AppendLine(await t.DiagnosticsAsync());
            if (Snapshot().Count == 0) sb.AppendLine("No device selected.");

            Directory.CreateDirectory(AppPaths.Dir);
            File.WriteAllText(AppPaths.DiagFile, sb.ToString());
            ShellExecuteW(IntPtr.Zero, "open", Environment.ProcessPath!, $"--text \"{AppPaths.DiagFile}\"", null, SW_SHOWNORMAL);
        }
        catch (Exception e) { Log.Info("Diagnostics failed: " + e.Message); }
    }

    void Quit() => DestroyWindow(_hwnd); // -> WM_DESTROY -> Cleanup

    void Cleanup()
    {
        KillTimer(_hwnd, (UIntPtr)TIMER_TICK);
        foreach (var t in Snapshot()) t.Dispose();

        var nid = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        Flyout.Shutdown();

        Log.Info("Exited");
    }
}
