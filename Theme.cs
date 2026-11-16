using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BtNotifier;

enum ThemeChoice { System, Light, Dark }

enum Tone { Normal, Dim, Danger, Success }

/// Colour set for one theme (light or dark).
sealed class Palette
{
    public Color Window, Card, CardHover, Border, Text, TextDim;
    public Color Control, ControlHover, ControlPressed, ControlBorder;
    public Color Track, Danger, Success;
}

/// Central theme: light / dark palette, Windows accent colour, fonts and a few Win32 helpers.
/// Follows the Windows app-mode setting unless the user picked Light or Dark.
static class AppTheme
{
    static Color Rgb(int rgb) => Color.FromArgb(0xFF, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    static readonly Palette LightPalette = new()
    {
        Window = Rgb(0xF3F3F3), Card = Rgb(0xFFFFFF), CardHover = Rgb(0xF5F5F5), Border = Rgb(0xE5E5E5),
        Text = Rgb(0x1B1B1B), TextDim = Rgb(0x5F5F5F),
        Control = Rgb(0xFFFFFF), ControlHover = Rgb(0xF6F6F6), ControlPressed = Rgb(0xEDEDED), ControlBorder = Rgb(0xD6D6D6),
        Track = Rgb(0x6B6B6B), Danger = Rgb(0xC42B1C), Success = Rgb(0x0F7B0F),
    };

    static readonly Palette DarkPalette = new()
    {
        Window = Rgb(0x202020), Card = Rgb(0x2B2B2B), CardHover = Rgb(0x353535), Border = Rgb(0x3A3A3A),
        Text = Rgb(0xFFFFFF), TextDim = Rgb(0xA6A6A6),
        Control = Rgb(0x333333), ControlHover = Rgb(0x3B3B3B), ControlPressed = Rgb(0x2D2D2D), ControlBorder = Rgb(0x454545),
        Track = Rgb(0xA0A0A0), Danger = Rgb(0xFF99A4), Success = Rgb(0x6CCB5F),
    };

    public static ThemeChoice Mode { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }
    public static Palette P { get; private set; } = LightPalette;
    public static Color Accent { get; private set; } = Rgb(0x0067C0);
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    public static Color AccentText { get; private set; } = Color.White;

    /// Raised on the UI thread when colours change (user switched mode, or Windows did).
    public static event Action? Changed;

    static Control? _ui;
    static bool _ready;

    public static ThemeChoice Parse(string? s) =>
        Enum.TryParse<ThemeChoice>(s, true, out var m) ? m : ThemeChoice.System;

    /// Call once on the UI thread, before any window is created.
    public static void Init()
    {
        if (_ui != null) return;
        _ui = new Control();
        _ = _ui.Handle; // create the handle now so we can marshal system events onto the UI thread

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
                return;
            try { _ui.BeginInvoke(new Action(Recompute)); } catch { }
        };
        Recompute();
    }

    public static void SetMode(ThemeChoice mode)
    {
        Mode = mode;
        Recompute();
    }

    static void Recompute()
    {
        bool dark = Mode == ThemeChoice.Dark || (Mode == ThemeChoice.System && SystemUsesDark());
        var accent = ReadAccent(dark);
        bool changed = !_ready || dark != IsDark || accent != Accent;

        IsDark = dark;
        P = dark ? DarkPalette : LightPalette;
        Accent = accent;
        AccentHover = Mix(accent, P.Window, 0.15f);
        AccentPressed = Mix(accent, P.Window, 0.30f);
        AccentText = Luma(accent) > 0.6f ? Rgb(0x1B1B1B) : Color.White;
        _ready = true;

        if (changed) Changed?.Invoke();
    }

    static bool SystemUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// Windows accent colour, nudged so it stays readable on the current background.
    static Color ReadAccent(bool dark)
    {
        var accent = dark ? Rgb(0x60CDFF) : Rgb(0x0067C0);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentColorMenu") is int abgr)
            {
                var c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                if (dark && Luma(c) < 0.45f) c = Mix(c, Color.White, 0.35f);
                else if (!dark && Luma(c) > 0.55f) c = Mix(c, Color.Black, 0.30f);
                accent = c;
            }
        }
        catch { }
        return accent;
    }

    // ── colour helpers ──

    public static float Luma(Color c) => (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        255,
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));

    public static Color ToneColor(Tone t) => t switch
    {
        Tone.Dim => P.TextDim,
        Tone.Danger => P.Danger,
        Tone.Success => P.Success,
        _ => P.Text,
    };

    // ── fonts ──

    static readonly Dictionary<(float, FontStyle, bool), Font> Fonts = new();
    static string? _text, _semibold, _icons;

    static bool Has(string family)
    {
        try { using var f = new FontFamily(family); return true; }
        catch { return false; }
    }

    /// Segoe UI Variable on Windows 11, Segoe UI elsewhere. semibold = the lighter "bold" used for headings.
    public static Font Face(float pt, FontStyle style = FontStyle.Regular, bool semibold = false)
    {
        lock (Fonts)
        {
            if (Fonts.TryGetValue((pt, style, semibold), out var f)) return f;
            _text ??= Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            _semibold ??= Has("Segoe UI Semibold") ? "Segoe UI Semibold" : _text;
            f = new Font(semibold ? _semibold : _text, pt, style, GraphicsUnit.Point);
            Fonts[(pt, style, semibold)] = f;
            return f;
        }
    }

    /// Segoe Fluent Icons (Win11) or Segoe MDL2 Assets (Win10) for glyphs like the Bluetooth symbol.
    public static Font IconFace(float pt)
    {
        lock (Fonts)
        {
            if (Fonts.TryGetValue((-pt, FontStyle.Regular, true), out var f)) return f;
            _icons ??= Has("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            f = new Font(_icons, pt, FontStyle.Regular, GraphicsUnit.Point);
            Fonts[(-pt, FontStyle.Regular, true)] = f;
            return f;
        }
    }

    // ── geometry ──

    public static int S(Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ── Win32 ──

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    static void Dwm(IntPtr h, int attr, int value) { try { DwmSetWindowAttribute(h, attr, ref value, sizeof(int)); } catch { } }

    /// Dark title bar, and on Windows 11 a title bar that blends into the window background.
    public static void StyleWindow(Form f)
    {
        if (!f.IsHandleCreated) return;
        var h = f.Handle;
        Dwm(h, 20, IsDark ? 1 : 0); // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            Dwm(h, 35, ColorRef(P.Window)); // DWMWA_CAPTION_COLOR
            Dwm(h, 36, ColorRef(P.Text));   // DWMWA_TEXT_COLOR
            Dwm(h, 34, ColorRef(P.Border)); // DWMWA_BORDER_COLOR
        }
    }

    /// Rounded window corners (Windows 11; harmless elsewhere).
    public static void RoundCorners(IntPtr handle) => Dwm(handle, 33, 2); // DWMWCP_ROUND

    /// Dark / light themed scroll bars for list boxes and text boxes.
    public static void StyleScroll(Control c)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }
}
