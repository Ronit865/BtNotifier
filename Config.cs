using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BtNotifier;

// Shared by the tray process and the settings window. Must not touch WinForms/System.Drawing,
// otherwise the always-running tray process would load them too.

static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtNotifier");
    public static string ConfigFile => Path.Combine(Dir, "config.json");
    public static string LogFile => Path.Combine(Dir, "bt-notifier.log");
    public static string DiagFile => Path.Combine(Dir, "diagnostics.txt");
}

/// A device the user chose. Identified by Bluetooth address, not by name.
sealed class TrackedDevice
{
    public string Name { get; set; } = "";
    public ulong Address { get; set; }
    public bool LowEnergy { get; set; }
}

sealed class Config
{
    public List<TrackedDevice> Devices { get; set; } = new();
    public string[] TargetNames { get; set; } = Array.Empty<string>(); // legacy (older versions); only used to pre-tick the settings window
    public string LogoPath { get; set; } = "";          // optional custom PNG; empty = built-in logo
    public bool ShowToast { get; set; } = true;          // Windows notification (toast)
    public bool ShowFlyout { get; set; } = false;        // on-screen flyout near the taskbar
    public int FlyoutSeconds { get; set; } = 3;          // how long the flyout stays
    public bool ToastSound { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public int BatteryPollMinutes { get; set; } = 5;
    public int LowBatteryPercent { get; set; } = 20;
    public int SettleSeconds { get; set; } = 4;
    public string Appearance { get; set; } = "System";   // "System" (follow Windows), "Light" or "Dark"

    public Config Clone() => (Config)MemberwiseClone();

    public static Config Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            if (File.Exists(AppPaths.ConfigFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.ConfigFile), AppJsonContext.Default.Config) ?? new Config();
        }
        catch (Exception e) { Log.Info("Config load failed: " + e.Message); }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Dir);
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, AppJsonContext.Default.Config));
        }
        catch (Exception e) { Log.Info("Config save failed: " + e.Message); }
    }

    /// Custom logo if set, otherwise the built-in one (extracted to AppData for the toast).
    public string? ResolveLogo()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(LogoPath) && File.Exists(LogoPath)) return LogoPath;

            var target = Path.Combine(AppPaths.Dir, "logo.png");
            if (!File.Exists(target))
            {
                using var s = typeof(Config).Assembly.GetManifestResourceStream("logo.png");
                if (s == null) return null;
                Directory.CreateDirectory(AppPaths.Dir);
                using var f = File.Create(target);
                s.CopyTo(f);
            }
            return target;
        }
        catch { return null; }
    }
}

/// Source-generated JSON: no reflection, so the JSON library stays small in memory.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Config))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

static class Log
{
    static readonly object L = new();

    public static void Info(string msg)
    {
        try
        {
            lock (L)
            {
                Directory.CreateDirectory(AppPaths.Dir);
                var f = AppPaths.LogFile;
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}

/// "Start with Windows" (per-user Run key).
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BtNotifier";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) != null;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return;
            if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Info("Autostart change failed: " + e.Message); }
    }

    /// Keeps the entry pointing at the exe that is running now (e.g. after reinstalling to another folder).
    public static void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(RunValue) is string current &&
                !string.Equals(current.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }
        catch { /* not critical */ }
    }
}
