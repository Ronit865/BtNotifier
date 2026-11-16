using System.Runtime.InteropServices;
using Microsoft.Win32;
using static BtNotifier.Native;

namespace BtNotifier;

enum FlyoutTone { Normal, Warn, Dim }

/// What the flyout shows. Dark is resolved by the caller (system theme or the user's choice).
sealed record FlyoutData(string Title, string Subtitle, int? Battery, bool Audio, FlyoutTone Tone, bool Dark, int Seconds);

/// A small on-screen indicator near the bottom of the screen, in the style of the Windows 11 Num Lock / volume flyout.
/// Drawn with plain GDI inside the tray process (no WinForms, no extra libraries), so it costs well under 1 MB.
/// Requests may come from any thread; the window itself is created and painted on the tray's UI thread.
static class Flyout
{
    const string ClassName = "BtNotifier.Flyout";
    const int TimerId = 1;

    static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;
    static readonly WndProc Proc = FlyoutProc; // static field: the delegate must never be garbage collected

    static IntPtr _owner, _hwnd;
    static FlyoutData? _pending, _data;
    static int _dpi = 96;

    // ───────────── public API ─────────────

    public static void Init(IntPtr trayWindow) => _owner = trayWindow;

    /// Thread-safe. The request is posted to the tray window, which calls ShowPending on the UI thread.
    public static void Show(FlyoutData data)
    {
        if (_owner == IntPtr.Zero) return;
        Volatile.Write(ref _pending, data);
        PostMessageW(_owner, WM_APP_FLYOUT, IntPtr.Zero, IntPtr.Zero);
    }

    /// UI thread only.
    public static void ShowPending()
    {
        var data = Interlocked.Exchange(ref _pending, null);
        if (data == null) return;
        try { ShowNow(data); }
        catch (Exception e) { Log.Info("Flyout failed: " + e.Message); }
    }

    public static void Shutdown()
    {
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    /// Light or dark flyout: the user's explicit choice, otherwise the Windows system theme.
    public static bool IsDark(string? appearance)
    {
        switch ((appearance ?? "System").ToLowerInvariant())
        {
            case "dark": return true;
            case "light": return false;
        }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ───────────── showing ─────────────

    static void ShowNow(FlyoutData d)
    {
        // Don't pop up over a full-screen game / video or while presenting
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4) return;

        EnsureWindow();
        _data = d;

        var work = new RECT();
        SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA: primary monitor, minus the taskbar */, 0, ref work, 0);
        _dpi = PrimaryDpi();

        int w = Px(340), h = Px(88);
        int x = work.Left + (work.Right - work.Left - w) / 2;
        int y = work.Bottom - h - Px(24);

        var c = Colors(d.Dark);
        SetDwm(_hwnd, 20, d.Dark ? 1 : 0);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (Win11) SetDwm(_hwnd, 34, c.Border); // DWMWA_BORDER_COLOR

        SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, w, h, 0x0010 | 0x0040 /* NOACTIVATE | SHOWWINDOW */);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
        SetTimer(_hwnd, (UIntPtr)TimerId, (uint)Math.Max(1000, d.Seconds * 1000), IntPtr.Zero); // same id: restarts the countdown
    }

    static void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x00020000, // CS_DROPSHADOW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);

        // topmost, no taskbar button, never takes focus
        const uint exStyle = 0x00000008 | 0x00000080 | 0x08000000; // TOPMOST | TOOLWINDOW | NOACTIVATE
        _hwnd = CreateWindowExW(exStyle, ClassName, "Bluetooth Notifier flyout", 0x80000000 /* WS_POPUP */,
                                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        SetDwm(_hwnd, 33, 2); // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11)
    }

    static IntPtr FlyoutProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case 0x0014: return new IntPtr(1);                   // WM_ERASEBKGND: everything is painted in WM_PAINT
                case 0x000F: Paint(hwnd); return IntPtr.Zero;        // WM_PAINT
                case 0x0021: return new IntPtr(3);                   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x0201:                                         // WM_LBUTTONDOWN: a click dismisses it
                    KillTimer(hwnd, (UIntPtr)TimerId);
                    ShowWindow(hwnd, 0);
                    return IntPtr.Zero;
                case WM_TIMER:                                       // time is up
                    KillTimer(hwnd, (UIntPtr)TimerId);
                    ShowWindow(hwnd, 0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception e) { Log.Info("Flyout message failed: " + e.Message); }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ───────────── painting ─────────────

    static void Paint(IntPtr hwnd)
    {
        IntPtr hdc = BeginPaint(hwnd, out var ps);
        try { if (_data != null) Draw(hwnd, hdc, _data); }
        finally { EndPaint(hwnd, ref ps); }
    }

    static void Draw(IntPtr hwnd, IntPtr hdc, FlyoutData d)
    {
        GetClientRect(hwnd, out var rc);
        int W = rc.Right, H = rc.Bottom;
        var c = Colors(d.Dark);

        // background (+ 1 px frame on Windows 10; Windows 11 draws its own border and rounds the corners)
        Fill(hdc, rc, c.Bg);
        if (!Win11)
        {
            IntPtr fb = CreateSolidBrush(c.Border);
            FrameRect(hdc, ref rc, fb);
            DeleteObject(fb);
        }
        SetBkMode(hdc, 1 /* TRANSPARENT */);

        int pad = Px(20);
        int textLeft = pad + Px(46);

        // icon: headphones for audio devices, Bluetooth symbol otherwise
        string glyph = d.Audio ? "\uE7F6" : "\uE702";
        DrawText(hdc, glyph, new RECT { Left = pad, Top = Px(10), Right = pad + Px(34), Bottom = H - Px(28) },
                 Win11 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 20, 400, c.Text, 0x1 | 0x4 | 0x20 | 0x800); // center, vcenter, single line

        // device name + state
        string face = Win11 ? "Segoe UI Variable Text" : "Segoe UI";
        const uint left = 0x0 | 0x4 | 0x20 | 0x800 | 0x8000; // left, vcenter, single line, no prefix, end ellipsis
        DrawText(hdc, d.Title, new RECT { Left = textLeft, Top = Px(14), Right = W - pad, Bottom = Px(38) }, face, 11, 600, c.Text, left);
        DrawText(hdc, d.Subtitle, new RECT { Left = textLeft, Top = Px(38), Right = W - pad, Bottom = Px(60) }, face, 9, 400, c.Dim, left);

        // bottom line: battery gauge when we know the level, otherwise a short accent line like the Num Lock flyout
        int barH = Px(4), barY = H - Px(20);
        int lineColor = d.Tone switch { FlyoutTone.Warn => c.Warn, FlyoutTone.Dim => c.Dim, _ => c.Accent };
        if (d.Battery is int p)
        {
            int full = W - pad * 2;
            Bar(hdc, pad, barY, pad + full, barY + barH, c.Track);
            int fill = Math.Max(barH, full * Math.Clamp(p, 0, 100) / 100);
            Bar(hdc, pad, barY, pad + fill, barY + barH, p <= 20 ? c.Warn : lineColor);
        }
        else
        {
            int len = Px(56), x = (W - len) / 2;
            Bar(hdc, x, barY, x + len, barY + barH, lineColor);
        }
    }

    static void DrawText(IntPtr hdc, string text, RECT rect, string face, int points, int weight, int color, uint flags)
    {
        IntPtr font = CreateFontW(-(int)Math.Round(points * _dpi / 72.0), 0, 0, 0, weight, 0, 0, 0,
                                  1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, face);
        IntPtr old = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        DrawTextW(hdc, text, -1, ref rect, flags);
        SelectObject(hdc, old);
        DeleteObject(font);
    }

    static void Fill(IntPtr hdc, RECT rc, int color)
    {
        IntPtr brush = CreateSolidBrush(color);
        FillRect(hdc, ref rc, brush);
        DeleteObject(brush);
    }

    static void Bar(IntPtr hdc, int l, int t, int r, int b, int color)
    {
        IntPtr oldPen = SelectObject(hdc, GetStockObject(8 /* NULL_PEN */));
        IntPtr brush = CreateSolidBrush(color);
        IntPtr oldBrush = SelectObject(hdc, brush);
        RoundRect(hdc, l, t, r, b, b - t, b - t);
        SelectObject(hdc, oldBrush);
        DeleteObject(brush);
        SelectObject(hdc, oldPen);
    }

    // ───────────── colours ─────────────

    readonly record struct FlyoutColors(int Bg, int Border, int Text, int Dim, int Track, int Accent, int Warn);

    static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16); // COLORREF

    static FlyoutColors Colors(bool dark) => dark
        ? new(Rgb(0x2C, 0x2C, 0x2C), Rgb(0x42, 0x42, 0x42), Rgb(0xFF, 0xFF, 0xFF), Rgb(0xB4, 0xB4, 0xB4), Rgb(0x55, 0x55, 0x55), Accent(true), Rgb(0xFF, 0xB9, 0x00))
        : new(Rgb(0xF7, 0xF7, 0xF7), Rgb(0xD9, 0xD9, 0xD9), Rgb(0x1B, 0x1B, 0x1B), Rgb(0x60, 0x60, 0x60), Rgb(0xCF, 0xCF, 0xCF), Accent(false), Rgb(0xC4, 0x7F, 0x00));

    /// The Windows accent colour, nudged so it stays visible on the flyout background.
    static int Accent(bool dark)
    {
        int c = dark ? Rgb(0x60, 0xCD, 0xFF) : Rgb(0x00, 0x67, 0xC0);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentColorMenu") is int abgr)
            {
                int r = abgr & 0xFF, g = (abgr >> 8) & 0xFF, b = (abgr >> 16) & 0xFF;
                double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                if (dark && luma < 0.45) { r = Mix(r, 255, 0.35); g = Mix(g, 255, 0.35); b = Mix(b, 255, 0.35); }
                else if (!dark && luma > 0.55) { r = Mix(r, 0, 0.30); g = Mix(g, 0, 0.30); b = Mix(b, 0, 0.30); }
                c = Rgb(r, g, b);
            }
        }
        catch { /* keep the default */ }
        return c;
    }

    static int Mix(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    // ───────────── DPI, monitor, Win32 ─────────────

    static int Px(int logical) => (int)Math.Round(logical * _dpi / 96.0);

    static int PrimaryDpi()
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0 && dx > 0) return (int)dx;
        }
        catch { }
        return 96;
    }

    static void SetDwm(IntPtr hwnd, int attribute, int value)
    {
        try { DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch { /* cosmetic */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public uint r0, r1, r2, r3, r4, r5, r6, r7; // rgbReserved[32]
    }

    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);
    [DllImport("user32.dll")] static extern int FrameRect(IntPtr hdc, ref RECT rc, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rc, uint format);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rc, bool erase);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, ref RECT value, uint winIni);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
                                     uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
