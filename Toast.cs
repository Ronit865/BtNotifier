using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BtNotifier;

/// Shows Windows toasts straight through WinRT (no helper library).
/// The app identity (name + icon in the toast header) comes from the Start-menu shortcut the
/// installer creates; the registry entry below covers runs without the installer.
static class Toast
{
    public const string Aumid = "BtNotifier.App";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Init(string? logoPath)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);

            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid);
            k.SetValue("DisplayName", "Bluetooth Notifier");
            if (!string.IsNullOrEmpty(logoPath)) k.SetValue("IconUri", logoPath);
        }
        catch (Exception e) { Log.Info("Toast init failed: " + e.Message); }
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                                    .Replace("\"", "&quot;").Replace("'", "&apos;");

    /// title / subtitle are the two top lines; battery adds a progress bar; extra is one more body line.
    public static void Show(string tag, string title, string subtitle, int? battery = null,
                            string? batteryStatus = null, string? extra = null,
                            bool sound = true, string? logo = null)
    {
        try
        {
            var sb = new StringBuilder("<toast><visual><binding template=\"ToastGeneric\">");

            if (!string.IsNullOrEmpty(logo))
                sb.Append("<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"")
                  .Append(Esc(new Uri(logo).AbsoluteUri)).Append("\"/>");

            sb.Append("<text>").Append(Esc(title)).Append("</text>");
            sb.Append("<text>").Append(Esc(subtitle)).Append("</text>");

            if (battery is int p)
                sb.Append("<progress title=\"Battery\" value=\"")
                  .Append((p / 100.0).ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" valueStringOverride=\"").Append(p).Append("%\" status=\"")
                  .Append(Esc(batteryStatus ?? "")).Append("\"/>");

            if (!string.IsNullOrEmpty(extra))
                sb.Append("<text>").Append(Esc(extra)).Append("</text>");

            sb.Append("</binding></visual>");
            if (!sound) sb.Append("<audio silent=\"true\"/>");
            sb.Append("</toast>");

            var doc = new XmlDocument();
            doc.LoadXml(sb.ToString());

            // same tag = replaces the previous toast for this device instead of stacking
            var toast = new ToastNotification(doc) { Tag = tag, Group = "bt" };
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
        }
        catch (Exception e) { Log.Info("Toast failed: " + e.Message); }
    }
}
