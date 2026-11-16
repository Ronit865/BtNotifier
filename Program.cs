namespace BtNotifier;

// One exe, three modes:
//   BtNotifier.exe                 -> tray process (always running, tiny, no WinForms)
//   BtNotifier.exe --settings      -> settings window (separate short-lived process)
//   BtNotifier.exe --text <file>   -> diagnostics window (separate short-lived process)
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info("UNHANDLED: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Info("UNOBSERVED: " + e.Exception); e.SetObserved(); };

        if (args.Length >= 1 && args[0] == "--settings") return UiHost.RunSettings();
        if (args.Length >= 2 && args[0] == "--text") return UiHost.RunText(args[1]);
        return TrayApp.Run();
    }
}
