namespace BtNotifier;

/// One place that decides how an event is shown: Windows toast, on-screen flyout, or both (Settings > Notification style).
static class Notifier
{
    public static void Show(Config cfg, string tag, string name, string toastSubtitle, string flyoutSubtitle,
                            int? battery, string? batteryStatus, string? extra, bool audio, FlyoutTone tone)
    {
        if (cfg.ShowToast)
            Toast.Show(tag, name, toastSubtitle, battery, batteryStatus, extra, cfg.ToastSound, cfg.ResolveLogo());

        if (cfg.ShowFlyout)
            Flyout.Show(new FlyoutData(name, flyoutSubtitle, battery, audio, tone, Flyout.IsDark(cfg.Appearance), cfg.FlyoutSeconds));
    }

    /// A plain message (no device), e.g. the welcome note after the first setup.
    public static void Info(Config cfg, string tag, string title, string text)
    {
        if (cfg.ShowToast)
            Toast.Show(tag, title, text, sound: cfg.ToastSound, logo: cfg.ResolveLogo());

        if (cfg.ShowFlyout)
            Flyout.Show(new FlyoutData(title, text, null, false, FlyoutTone.Normal, Flyout.IsDark(cfg.Appearance), cfg.FlyoutSeconds));
    }
}
