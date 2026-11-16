using System.Runtime.CompilerServices;
using static BtNotifier.Native;

namespace BtNotifier;

/// Hosts the WinForms windows. Runs only in the short-lived "--settings" / "--text" processes,
/// so WinForms, System.Drawing and the theme code never load into the always-running tray process.
static class UiHost
{
    const string SettingsTitle = "Bluetooth Notifier - Settings"; // must match SettingsForm.Text

    static void InitWinForms()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int RunSettings()
    {
        using var mutex = new Mutex(true, "BtNotifier.Settings", out bool created);
        if (!created)
        {
            // already open: just bring that window to the front
            var existing = FindWindowW(null, SettingsTitle);
            if (existing != IntPtr.Zero) { ShowWindow(existing, 9 /* SW_RESTORE */); SetForegroundWindow(existing); }
            return 0;
        }

        InitWinForms();

        var cfg = Config.Load();
        AppTheme.Init();
        AppTheme.SetMode(AppTheme.Parse(cfg.Appearance));

        bool firstRun = !cfg.FirstRunDone;
        using var form = new SettingsForm(cfg, Autostart.IsEnabled() || firstRun);
        if (form.ShowDialog() != DialogResult.OK) return 0;

        var result = form.Result;
        result.FirstRunDone = true;
        result.Save();
        Autostart.Set(result.StartWithWindows);

        // Tell the tray process to pick up the new settings (start it if it is not running)
        var tray = FindWindowW(TrayClassName, null);
        if (tray != IntPtr.Zero) PostMessageW(tray, WM_APP_RELOAD, IntPtr.Zero, IntPtr.Zero);
        else ShellExecuteW(IntPtr.Zero, "open", Environment.ProcessPath!, null, null, SW_SHOWNORMAL);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int RunText(string path)
    {
        InitWinForms();

        var cfg = Config.Load();
        AppTheme.Init();
        AppTheme.SetMode(AppTheme.Parse(cfg.Appearance));

        string body;
        try { body = File.ReadAllText(path); }
        catch (Exception e) { body = "Could not read " + path + "\n" + e.Message; }

        InfoDialog.ShowText("Bluetooth Notifier - Diagnostics", body);
        return 0;
    }
}
