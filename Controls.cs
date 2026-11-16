using System.Drawing;
using System.Drawing.Drawing2D;

namespace BtNotifier;

// Small set of custom-drawn controls that follow AppTheme (light / dark, accent colour, DPI).
// Everything reads the palette while painting, so a theme switch only needs an Invalidate.

abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected int S(int px) => AppTheme.S(this, px);
    protected static Palette P => AppTheme.P;

    /// Colour of whatever this control sits on: a card, or the window.
    protected Color Surface
    {
        get
        {
            for (var p = Parent; p != null; p = p.Parent)
                if (p is Card) return P.Card;
            return P.Window;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Surface);
}

/// Rounded surface that groups related rows.
sealed class Card : Panel
{
    readonly List<(int Y, int Inset)> _dividers = new();

    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void AddDivider(int y, int inset) => _dividers.Add((y, inset));
    public void ClearDividers() => _dividers.Clear();

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = AppTheme.P;
        g.Clear(p.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = AppTheme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), AppTheme.S(this, 8));
        using var fill = new SolidBrush(p.Card);
        using var pen = new Pen(p.Border);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);

        g.SmoothingMode = SmoothingMode.None;
        foreach (var (y, inset) in _dividers)
            g.DrawLine(pen, AppTheme.S(this, inset), AppTheme.S(this, y), Width - AppTheme.S(this, inset), AppTheme.S(this, y));
    }
}

/// Label that picks its colour from the current theme.
sealed class ThemedLabel : Label
{
    public Tone Kind { get; set; } = Tone.Normal;

    public ThemedLabel()
    {
        AutoSize = false;
        BackColor = Color.Transparent;
        UseMnemonic = false;
    }

    public override Color ForeColor
    {
        get => AppTheme.ToneColor(Kind);
        set { }
    }
}

/// One line in a card: title + description on the left, a control (toggle, stepper...) on the right.
sealed class SettingRow : ThemedControl
{
    Control? _trailing;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    public void SetTrailing(Control c)
    {
        _trailing = c;
        Controls.Add(c);
        PositionTrailing();
    }

    void PositionTrailing()
    {
        if (_trailing == null) return;
        _trailing.Location = new Point(Width - S(16) - _trailing.Width, (Height - _trailing.Height) / 2);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PositionTrailing(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var titleFont = AppTheme.Face(10f);
        var descFont = AppTheme.Face(8.5f);
        int pad = S(16);
        int textW = Width - pad * 2 - (_trailing?.Width ?? 0) - S(12);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top;

        int th = titleFont.Height;
        int dh = string.IsNullOrEmpty(Description) ? 0 : descFont.Height + S(2);
        int y = (Height - (th + dh)) / 2;

        TextRenderer.DrawText(g, Title, titleFont, new Rectangle(pad, y, textW, th), P.Text, flags);
        if (dh > 0)
            TextRenderer.DrawText(g, Description, descFont, new Rectangle(pad, y + th + S(2), textW, descFont.Height), P.TextDim, flags);
    }
}

/// Windows 11 style on/off switch with a short slide animation.
sealed class ToggleSwitch : ThemedControl
{
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    bool _checked, _hover;
    float _t;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(S(48), S(26));
        _anim.Tick += (_, _) =>
        {
            float target = _checked ? 1f : 0f;
            _t += Math.Sign(target - _t) * 0.2f;
            if (Math.Abs(target - _t) < 0.01f) { _t = target; _anim.Stop(); }
            Invalidate();
        };
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (IsHandleCreated) _anim.Start(); else _t = value ? 1f : 0f;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// Set the state without animating (used while loading).
    public void SetInitial(bool value) { _checked = value; _t = value ? 1f : 0f; Invalidate(); }

    public void Toggle() => Checked = !Checked;

    protected override void OnClick(EventArgs e) { base.OnClick(e); Toggle(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Toggle(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int inset = S(2);
        var track = new RectangleF(inset + 0.5f, inset + 0.5f, Width - inset * 2 - 1, Height - inset * 2 - 1);

        using (var path = AppTheme.RoundRect(track, track.Height / 2))
        {
            using var fill = new SolidBrush(AppTheme.Mix(Surface, AppTheme.Accent, _t));
            using var pen = new Pen(AppTheme.Mix(P.Track, AppTheme.Accent, _t), 1f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        float d = S(12) + (_hover || Capture ? S(2) : 0);
        float cy = track.Y + track.Height / 2;
        float x0 = track.X + track.Height / 2, x1 = track.Right - track.Height / 2;
        float cx = x0 + (x1 - x0) * _t;
        using (var knob = new SolidBrush(AppTheme.Mix(P.Track, AppTheme.AccentText, _t)))
            g.FillEllipse(knob, cx - d / 2, cy - d / 2, d, d);

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(P.Text, 1.5f);
            using var rp = AppTheme.RoundRect(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), Height / 2f);
            g.DrawPath(ring, rp);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

enum ButtonKind { Primary, Secondary, Subtle }

/// Flat rounded button; works with Form.AcceptButton / CancelButton.
sealed class ModernButton : ThemedControl, IButtonControl
{
    bool _hover, _down;

    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public string Glyph { get; set; } = "";
    public DialogResult DialogResult { get; set; }

    public ModernButton()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(S(96), S(32));
    }

    public override string Text
    {
        get => base.Text;
        set { base.Text = value; Invalidate(); }
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is { } f) f.DialogResult = DialogResult;
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Focus(); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _down = false; Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _down = true; Invalidate(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _down) { _down = false; Invalidate(); OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color bg, fg, border;
        switch (Kind)
        {
            case ButtonKind.Primary:
                bg = !Enabled ? AppTheme.Mix(P.Window, P.TextDim, 0.25f)
                   : _down ? AppTheme.AccentPressed : _hover ? AppTheme.AccentHover : AppTheme.Accent;
                fg = Enabled ? AppTheme.AccentText : P.TextDim;
                border = bg;
                break;
            case ButtonKind.Subtle:
                bg = _down ? P.ControlPressed : _hover ? P.ControlHover : Surface;
                fg = Enabled ? P.Text : P.TextDim;
                border = bg;
                break;
            default:
                bg = _down ? P.ControlPressed : _hover ? P.ControlHover : P.Control;
                fg = Enabled ? P.Text : P.TextDim;
                border = P.ControlBorder;
                break;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = AppTheme.RoundRect(rect, S(5)))
        using (var fill = new SolidBrush(bg))
        using (var pen = new Pen(border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        // content: optional icon glyph + label, centred together
        var textFont = AppTheme.Face(9.5f);
        var iconFont = AppTheme.IconFace(10f);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;
        var big = new Size(int.MaxValue, int.MaxValue);

        int gw = Glyph.Length > 0 ? TextRenderer.MeasureText(Glyph, iconFont, big, flags).Width : 0;
        int tw = TextRenderer.MeasureText(Text, textFont, big, flags).Width;
        int gap = gw > 0 && tw > 0 ? S(8) : 0;
        int x = (Width - (gw + gap + tw)) / 2;

        if (gw > 0) TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(x, 0, gw, Height), fg, flags);
        if (tw > 0) TextRenderer.DrawText(g, Text, textFont, new Rectangle(x + gw + gap, 0, tw, Height), fg, flags);

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Kind == ButtonKind.Primary ? P.Text : AppTheme.Accent, 1.5f);
            using var rp = AppTheme.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5, Height - 5), S(3));
            g.DrawPath(ring, rp);
        }
    }
}

/// "−  20%  +" number picker with press-and-hold repeat.
sealed class Stepper : ThemedControl
{
    readonly System.Windows.Forms.Timer _repeat = new() { Interval = 400 };
    int _value = 20, _hotZone, _dir;

    public int Minimum { get; set; } = 5;
    public int Maximum { get; set; } = 50;
    public string Suffix { get; set; } = "%";
    public event EventHandler? ValueChanged;

    public Stepper()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(S(128), S(32));
        _repeat.Tick += (_, _) => { _repeat.Interval = 70; Step(_dir); };
    }

    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, Minimum, Maximum);
            if (value == _value) return;
            _value = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void Step(int d) => Value += d;

    int Zone(Point p) => p.X < S(36) ? -1 : p.X > Width - S(36) ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int z = Zone(e.Location); if (z != _hotZone) { _hotZone = z; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hotZone = 0; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dir = Zone(e.Location);
        if (_dir == 0) return;
        Step(_dir);
        _repeat.Interval = 400;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _repeat.Stop(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Step(e.Delta > 0 ? 1 : -1);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Right) { Step(1); e.Handled = true; }
        else if (e.KeyCode is Keys.Down or Keys.Left) { Step(-1); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = AppTheme.RoundRect(rect, S(5)))
        using (var fill = new SolidBrush(P.Control))
        using (var pen = new Pen(Focused && ShowFocusCues ? AppTheme.Accent : P.ControlBorder, Focused && ShowFocusCues ? 1.5f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        int zw = S(36);
        DrawSign(g, new Rectangle(0, 0, zw, Height), plus: false, enabled: _value > Minimum, hot: _hotZone == -1);
        DrawSign(g, new Rectangle(Width - zw, 0, zw, Height), plus: true, enabled: _value < Maximum, hot: _hotZone == 1);

        TextRenderer.DrawText(g, _value + Suffix, AppTheme.Face(10f), new Rectangle(zw, 0, Width - zw * 2, Height), P.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    void DrawSign(Graphics g, Rectangle zone, bool plus, bool enabled, bool hot)
    {
        if (hot && enabled)
        {
            using var hb = new SolidBrush(P.ControlHover);
            using var hp = AppTheme.RoundRect(new RectangleF(zone.X + 2, zone.Y + 2, zone.Width - 4, zone.Height - 4), S(4));
            g.FillPath(hb, hp);
        }
        using var pen = new Pen(enabled ? P.Text : AppTheme.Mix(P.TextDim, P.Control, 0.5f), 1.4f);
        float cx = zone.X + zone.Width / 2f, cy = zone.Y + zone.Height / 2f, h = S(5);
        g.DrawLine(pen, cx - h, cy, cx + h, cy);
        if (plus) g.DrawLine(pen, cx, cy - h, cx, cy + h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}

/// Pill-shaped choice between a few options (System / Light / Dark).
sealed class SegmentedControl : ThemedControl
{
    readonly string[] _items;
    int _selected, _hot = -1;

    public event EventHandler? SelectedIndexChanged;

    public SegmentedControl(params string[] items)
    {
        _items = items;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(S(60) * items.Length + S(6), S(32));
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, _items.Length - 1);
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    Rectangle SegmentRect(int i)
    {
        int inset = S(3);
        int w = (Width - inset * 2) / _items.Length;
        return new Rectangle(inset + w * i, inset, w, Height - inset * 2);
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < _items.Length; i++)
            if (SegmentRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitTest(e.Location); if (h != _hot) { _hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int h = HitTest(e.Location);
        if (h >= 0) SelectedIndex = h;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex--; e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SelectedIndex++; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = AppTheme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(6)))
        using (var fill = new SolidBrush(P.Control))
        using (var pen = new Pen(Focused && ShowFocusCues ? AppTheme.Accent : P.ControlBorder, Focused && ShowFocusCues ? 1.5f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        for (int i = 0; i < _items.Length; i++)
        {
            var r = SegmentRect(i);
            bool sel = i == _selected;
            if (sel || i == _hot)
            {
                using var sp = AppTheme.RoundRect(r, S(4));
                using var sb = new SolidBrush(sel ? AppTheme.Accent : P.ControlHover);
                g.FillPath(sb, sp);
            }
            TextRenderer.DrawText(g, _items[i], AppTheme.Face(9f), r, sel ? AppTheme.AccentText : P.Text, flags);
        }
    }
}

/// One paired device in the list.
sealed class DeviceItem
{
    public TrackedDevice Device { get; init; } = new();
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Connected { get; init; }
    public bool Present { get; init; }
    public bool Checked { get; set; }
    public override string ToString() => Name;
}

/// Owner-drawn device list: tick box, Bluetooth badge, name and status per row.
sealed class DeviceListBox : ListBox
{
    int _hot = -1;

    public event EventHandler? CheckedChanged;

    public DeviceListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        Cursor = Cursors.Hand;
        ItemHeight = AppTheme.S(this, 52);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public IEnumerable<DeviceItem> AllItems => Items.Cast<DeviceItem>();
    public IEnumerable<DeviceItem> CheckedItems2 => AllItems.Where(i => i.Checked);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppTheme.StyleScroll(this);
    }

    void Toggle(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        var it = (DeviceItem)Items[index];
        it.Checked = !it.Checked;
        Invalidate(GetItemRectangle(index));
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = IndexFromPoint(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int i = IndexFromPoint(e.Location);
        if (i >= 0) { SelectedIndex = i; Toggle(i); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Toggle(SelectedIndex); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var it = (DeviceItem)Items[e.Index];
        var g = e.Graphics;
        var p = AppTheme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int S(int px) => AppTheme.S(this, px);

        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, e.Bounds);

        var rc = Rectangle.Inflate(e.Bounds, -S(6), -S(2));
        bool focusedRow = (e.State & DrawItemState.Selected) != 0 && Focused;
        if (e.Index == _hot || focusedRow)
        {
            using var hp = AppTheme.RoundRect(rc, S(6));
            using var hb = new SolidBrush(p.CardHover);
            g.FillPath(hb, hp);
        }

        // tick box
        int box = S(20);
        var br = new RectangleF(rc.X + S(10) + 0.5f, rc.Y + (rc.Height - box) / 2f + 0.5f, box - 1, box - 1);
        using (var bp = AppTheme.RoundRect(br, S(5)))
        {
            if (it.Checked)
            {
                using var fill = new SolidBrush(AppTheme.Accent);
                g.FillPath(fill, bp);
                using var tick = new Pen(AppTheme.AccentText, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(br.X + br.Width * 0.27f, br.Y + br.Height * 0.52f),
                    new PointF(br.X + br.Width * 0.43f, br.Y + br.Height * 0.68f),
                    new PointF(br.X + br.Width * 0.74f, br.Y + br.Height * 0.34f),
                });
            }
            else
            {
                using var pen = new Pen(p.Track, 1.3f);
                g.DrawPath(pen, bp);
            }
        }

        // Bluetooth badge
        int badge = S(34);
        var badgeRect = new Rectangle((int)br.Right + S(14), rc.Y + (rc.Height - badge) / 2, badge, badge);
        using (var bb = new SolidBrush(AppTheme.Mix(p.Card, AppTheme.Accent, it.Present ? 0.14f : 0.05f)))
            g.FillEllipse(bb, badgeRect);
        TextRenderer.DrawText(g, "", AppTheme.IconFace(12f), badgeRect,
            it.Present ? AppTheme.Accent : p.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        // name + status
        var nameFont = AppTheme.Face(10f);
        var detailFont = AppTheme.Face(8.5f);
        int tx = badgeRect.Right + S(12);
        int tw = rc.Right - tx - S(10);
        int th = nameFont.Height, dh = detailFont.Height;
        int ty = rc.Y + (rc.Height - (th + dh + S(1))) / 2;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine;

        TextRenderer.DrawText(g, it.Name, nameFont, new Rectangle(tx, ty, tw, th), it.Present ? p.Text : p.TextDim, flags);
        TextRenderer.DrawText(g, it.Detail, detailFont, new Rectangle(tx, ty + th + S(1), tw, dh),
            it.Connected ? p.Success : p.TextDim, flags);
    }
}

/// Tray / context menu painter that follows the theme (dark menu, rounded hover, thin border).
sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => AppTheme.P.Card;
        public override Color MenuBorder => AppTheme.P.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => AppTheme.P.CardHover;
        public override Color ImageMarginGradientBegin => AppTheme.P.Card;
        public override Color ImageMarginGradientMiddle => AppTheme.P.Card;
        public override Color ImageMarginGradientEnd => AppTheme.P.Card;
        public override Color SeparatorDark => AppTheme.P.Border;
        public override Color SeparatorLight => AppTheme.P.Border;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(AppTheme.P.Card);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        using var pen = new Pen(AppTheme.P.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, e.Item.Width - 1, e.Item.Height - 1);
        using var path = AppTheme.RoundRect(r, e.Item.Owner == null ? 4 : (int)(4 * e.Item.Owner.DeviceDpi / 96.0));
        using var b = new SolidBrush(AppTheme.P.CardHover);
        g.FillPath(b, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(AppTheme.P.Border);
        e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? AppTheme.P.Text : AppTheme.P.TextDim;
        base.OnRenderItemText(e);
    }
}
