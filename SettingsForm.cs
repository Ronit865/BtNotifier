using System.Drawing;

namespace BtNotifier;

/// Settings window: pick paired Bluetooth devices, sound, autostart, low-battery level, theme.
/// Modern card layout that follows the Windows light/dark setting (or an explicit choice).
sealed class SettingsForm : Form
{
    const string DefaultStatus = "Only devices already paired with this PC are listed.";

    readonly Config _orig;
    readonly ThemeChoice _origMode;

    readonly ThemedLabel _devHead = new() { Text = "Devices" };
    readonly ThemedLabel _devSub = new() { Text = "Notify me when these connect or disconnect", Kind = Tone.Dim };
    readonly ThemedLabel _prefHead = new() { Text = "Preferences" };
    readonly Card _deviceCard = new();
    readonly Card _prefCard = new();
    readonly DeviceListBox _list = new();
    readonly ThemedLabel _empty = new() { Kind = Tone.Dim, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    readonly ThemedLabel _status = new() { Kind = Tone.Dim, TextAlign = ContentAlignment.MiddleLeft };
    readonly ModernButton _refresh = new() { Kind = ButtonKind.Subtle, Glyph = "", Text = "Refresh" };
    readonly ToggleSwitch _sound = new();
    readonly ToggleSwitch _autostart = new();
    readonly Stepper _low = new();
    readonly SegmentedControl _style = new("Toast", "Flyout", "Both");
    readonly SegmentedControl _theme = new("System", "Light", "Dark");
    readonly ModernButton _save = new() { Kind = ButtonKind.Primary, Text = "Save" };
    readonly ModernButton _cancel = new() { Kind = ButtonKind.Secondary, Text = "Cancel", DialogResult = DialogResult.Cancel };

    bool _loadedOnce;
    int _rows;

    public Config Result { get; private set; }

    int S(int px) => AppTheme.S(this, px);

    public SettingsForm(Config cfg, bool autostartOn)
    {
        _orig = cfg;
        _origMode = AppTheme.Parse(cfg.Appearance);
        Result = cfg;

        Text = "Bluetooth Notifier - Settings";
        Font = AppTheme.Face(9.5f);
        AutoScaleMode = AutoScaleMode.None; // all sizes below are scaled by hand (see S)
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ClientSize = new Size(S(480), S(582));
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // The title bar already carries the app name and icon, so the window body starts
        // straight with content: a heading per card instead of a second "Bluetooth Notifier".
        _devHead.Font = AppTheme.Face(11f, semibold: true);
        _devSub.Font = AppTheme.Face(9f);
        _prefHead.Font = AppTheme.Face(11f, semibold: true);

        // ── devices card ──
        _list.BackColor = AppTheme.P.Card;
        _list.CheckedChanged += (_, _) => { if (_status.Kind == Tone.Danger) SetStatus(DefaultStatus, Tone.Dim); };

        _empty.Font = AppTheme.Face(9f);
        _empty.Text = "No paired Bluetooth devices found.\nPair your device in Windows Settings, then click Refresh.";

        _status.Font = AppTheme.Face(8.5f);
        _refresh.Click += async (_, _) => await LoadDevicesAsync();

        _deviceCard.Controls.Add(_list);
        _deviceCard.Controls.Add(_empty);
        _deviceCard.Controls.Add(_status);
        _deviceCard.Controls.Add(_refresh);
        _empty.BringToFront();

        // ── preferences card ──
        for (int i = 1; i < PrefRows; i++) _prefCard.AddDivider(5 + 52 * i, 16);

        _sound.SetInitial(cfg.ToastSound);
        _autostart.SetInitial(autostartOn);
        _low.Minimum = 5;
        _low.Maximum = 50;
        _low.Value = Math.Clamp(cfg.LowBatteryPercent, 5, 50);
        _theme.SelectedIndex = (int)_origMode;
        _style.SelectedIndex = StyleIndex(cfg);

        MakeRow(0, "Notification style", "Windows toast, on-screen flyout, or both", _style);
        var soundRow = MakeRow(1, "Notification sound", "Play a sound with each toast", _sound);
        var autoRow = MakeRow(2, "Start with Windows", "Launch automatically when you sign in", _autostart);
        MakeRow(3, "Low battery warning", "Warn me when battery drops to", _low);
        MakeRow(4, "Appearance", "Follow Windows, or pick a theme", _theme);
        soundRow.Cursor = autoRow.Cursor = Cursors.Hand;
        soundRow.Click += (_, _) => _sound.Toggle();
        autoRow.Click += (_, _) => _autostart.Toggle();

        _theme.SelectedIndexChanged += (_, _) => AppTheme.SetMode((ThemeChoice)_theme.SelectedIndex); // live preview

        // ── footer buttons ──
        _save.Click += (_, _) => Save();
        AcceptButton = _save;
        CancelButton = _cancel;

        Controls.Add(_devHead);
        Controls.Add(_devSub);
        Controls.Add(_prefHead);
        Controls.Add(_deviceCard);
        Controls.Add(_prefCard);
        Controls.Add(_save);
        Controls.Add(_cancel);

        // keyboard order: devices, refresh, options, buttons
        int tab = 0;
        _deviceCard.TabIndex = tab++;
        _list.TabIndex = 0;
        _refresh.TabIndex = 1;
        _prefCard.TabIndex = tab++;
        _save.TabIndex = tab++;
        _cancel.TabIndex = tab++;

        // start with roughly the right height (saved devices) so the window doesn't jump after the scan
        DoLayout(RowsFor(_orig.Devices.Count));

        ApplyTheme();
        AppTheme.Changed += ApplyTheme;

        Shown += async (_, _) =>
        {
            TopMost = true; TopMost = false; Activate(); // bring to front when opened from the tray
            await LoadDevicesAsync();
        };
    }

    const int PrefRows = 5;

    /// Toast = 0, Flyout = 1, Both = 2
    static int StyleIndex(Config c) => c.ShowToast ? (c.ShowFlyout ? 2 : 0) : (c.ShowFlyout ? 1 : 0);

    /// Device card grows with the number of devices (1-3 rows; an empty list shows a 2-row message).
    static int RowsFor(int deviceCount) => deviceCount == 0 ? 2 : Math.Min(deviceCount, 3);

    void DoLayout(int rows)
    {
        _rows = rows;
        const int M = 20;      // outer margin
        int cardW = 480 - M * 2;
        int listH = 52 * rows;
        int y = M;

        _devHead.SetBounds(S(M), S(y), S(cardW), S(22)); y += 22;
        _devSub.SetBounds(S(M), S(y), S(cardW), S(18)); y += 18 + 8;

        int devCardH = 5 + listH + 1 + 46 + 1;
        _deviceCard.SetBounds(S(M), S(y), S(cardW), S(devCardH));
        _deviceCard.ClearDividers();
        _deviceCard.AddDivider(5 + listH, 1);
        _list.SetBounds(S(1), S(5), S(cardW - 2), S(listH));
        _empty.SetBounds(S(1), S(5), S(cardW - 2), S(listH));
        int stripY = 5 + listH + 1;
        _status.SetBounds(S(16), S(stripY + 7), S(cardW - 16 - 96 - 16 - 12), S(32));
        _refresh.SetBounds(S(cardW - 16 - 96), S(stripY + 7), S(96), S(32));
        y += devCardH + 18;

        _prefHead.SetBounds(S(M), S(y), S(cardW), S(22)); y += 22 + 6;
        int prefH = 5 + 52 * PrefRows + 5;
        _prefCard.SetBounds(S(M), S(y), S(cardW), S(prefH));
        y += prefH + 16;

        _save.SetBounds(S(480 - M - 96), S(y), S(96), S(32));
        _cancel.SetBounds(S(480 - M - 96 - 8 - 96), S(y), S(96), S(32));
        y += 32 + M;

        ClientSize = new Size(S(480), S(y));
        Invalidate(true);
    }

    SettingRow MakeRow(int index, string title, string description, Control trailing)
    {
        var row = new SettingRow { Title = title, Description = description };
        row.SetBounds(S(1), S(5 + 52 * index), S(480 - 40 - 2), S(52));
        row.SetTrailing(trailing);
        row.TabStop = false;
        _prefCard.Controls.Add(row);
        trailing.TabIndex = index;
        return row;
    }

    void ApplyTheme()
    {
        BackColor = AppTheme.P.Window;
        ForeColor = AppTheme.P.Text;
        _list.BackColor = AppTheme.P.Card;
        AppTheme.StyleWindow(this);
        AppTheme.StyleScroll(_list);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppTheme.StyleWindow(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppTheme.Changed -= ApplyTheme;
        // Cancelled: undo the live theme preview
        if (DialogResult != DialogResult.OK) AppTheme.SetMode(_origMode);
        base.OnFormClosed(e);
    }

    void SetStatus(string text, Tone tone)
    {
        _status.Text = text;
        _status.Kind = tone;
        _status.Invalidate();
    }

    async Task LoadDevicesAsync()
    {
        SetStatus("Searching for paired devices...", Tone.Dim);
        _refresh.Enabled = false;

        // What should be ticked: what is ticked now, or (first load) what was saved
        var keepAddresses = new HashSet<ulong>(
            _loadedOnce
                ? _list.CheckedItems2.Select(i => i.Device.Address)
                : _orig.Devices.Select(d => d.Address));
        // Older versions saved names only: pre-tick by name on first load
        var legacyNames = new HashSet<string>(
            _loadedOnce || _orig.Devices.Count > 0 ? Array.Empty<string>() : _orig.TargetNames,
            StringComparer.OrdinalIgnoreCase);
        _loadedOnce = true;

        List<FoundDevice> found;
        try { found = await Discovery.ListPairedAsync(); }
        catch (Exception ex) { Log.Info("Device list failed: " + ex.Message); found = new(); }

        if (IsDisposed) return;

        // address -> (device, connected, visible right now)
        var all = new Dictionary<ulong, (TrackedDevice Dev, bool Connected, bool Present)>();
        foreach (var f in found)
            all[f.Address] = (new TrackedDevice { Name = f.Name, Address = f.Address, LowEnergy = f.LowEnergy }, f.Connected, true);
        foreach (var d in _orig.Devices) // saved devices that are not visible right now
            all.TryAdd(d.Address, (d, false, false));

        var duplicateNames = all.Values.GroupBy(x => x.Dev.Name, StringComparer.OrdinalIgnoreCase)
                                       .Where(g => g.Count() > 1).Select(g => g.Key)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var v in all.Values.OrderBy(x => x.Dev.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new List<string>
            {
                !v.Present ? "Not found" : v.Connected ? "Connected" : "Paired"
            };
            if (v.Dev.LowEnergy) parts.Add("Low Energy");
            if (duplicateNames.Contains(v.Dev.Name)) parts.Add("#" + v.Dev.Address.ToString("X12")[^4..]);

            _list.Items.Add(new DeviceItem
            {
                Device = v.Dev,
                Name = v.Dev.Name,
                Detail = string.Join(" · ", parts),
                Connected = v.Connected,
                Present = v.Present,
                Checked = keepAddresses.Contains(v.Dev.Address) || legacyNames.Contains(v.Dev.Name),
            });
        }
        _list.EndUpdate();

        _empty.Visible = _list.Items.Count == 0;
        int rows = RowsFor(_list.Items.Count);
        if (rows != _rows) DoLayout(rows);
        _refresh.Enabled = true;
        SetStatus(_list.Items.Count == 0 ? "Nothing to show yet." : DefaultStatus, Tone.Dim);
    }

    void Save()
    {
        var chosen = _list.CheckedItems2.Select(i => i.Device).ToList();
        if (chosen.Count == 0)
        {
            SetStatus("Select at least one device to continue.", Tone.Danger);
            _list.Focus();
            return;
        }

        var r = _orig.Clone();
        r.Devices = chosen;
        r.TargetNames = Array.Empty<string>(); // legacy name list is replaced by address-based devices
        r.ShowToast = _style.SelectedIndex != 1;   // Toast or Both
        r.ShowFlyout = _style.SelectedIndex != 0;  // Flyout or Both
        r.ToastSound = _sound.Checked;
        r.StartWithWindows = _autostart.Checked;
        r.LowBatteryPercent = _low.Value;
        r.Appearance = ((ThemeChoice)_theme.SelectedIndex).ToString();
        Result = r;

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// Themed replacement for MessageBox when showing a block of text (used by Diagnostics).
sealed class InfoDialog : Form
{
    readonly Card _card = new();
    readonly TextBox _box = new();
    readonly ModernButton _copy = new() { Kind = ButtonKind.Secondary, Text = "Copy" };
    readonly ModernButton _close = new() { Kind = ButtonKind.Primary, Text = "Close", DialogResult = DialogResult.OK };

    int S(int px) => AppTheme.S(this, px);

    public static void ShowText(string title, string body)
    {
        using var d = new InfoDialog(title, body);
        d.ShowDialog();
    }

    InfoDialog(string title, string body)
    {
        Text = title;
        Font = AppTheme.Face(9.5f);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(600), S(420));
        MinimumSize = Size;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _card.SetBounds(S(20), S(20), S(560), S(336));

        _box.Multiline = true;
        _box.ReadOnly = true;
        _box.BorderStyle = BorderStyle.None;
        _box.ScrollBars = ScrollBars.Both;
        _box.WordWrap = false;
        _box.Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        _box.Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _box.SetBounds(S(14), S(12), S(560 - 28), S(336 - 24));
        _card.Controls.Add(_box);

        _close.Anchor = _copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _close.SetBounds(S(600 - 20 - 96), S(372), S(96), S(32));
        _copy.SetBounds(S(600 - 20 - 96 - 8 - 96), S(372), S(96), S(32));
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_box.Text); _copy.Text = "Copied"; } catch { }
        };

        Controls.Add(_card);
        Controls.Add(_copy);
        Controls.Add(_close);
        AcceptButton = _close;
        CancelButton = _close;

        ApplyTheme();
        AppTheme.Changed += ApplyTheme;
        Shown += (_, _) => { ActiveControl = _close; _box.SelectionStart = 0; _box.SelectionLength = 0; };
    }

    void ApplyTheme()
    {
        BackColor = AppTheme.P.Window;
        ForeColor = AppTheme.P.Text;
        _box.BackColor = AppTheme.P.Card;
        _box.ForeColor = AppTheme.P.Text;
        AppTheme.StyleWindow(this);
        AppTheme.StyleScroll(_box);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppTheme.StyleWindow(this);
        AppTheme.StyleScroll(_box);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppTheme.Changed -= ApplyTheme;
        base.OnFormClosed(e);
    }
}
