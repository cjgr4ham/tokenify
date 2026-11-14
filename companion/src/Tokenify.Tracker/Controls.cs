using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Tokenify.Tracker;

internal enum ButtonKind { Primary, Secondary, Ghost, Danger }

/// <summary>A rounded button in TOKENIFY's colours. Still a real button: tab stop, Space/Enter, access keys.</summary>
internal sealed class TkButton : Button
{
    private bool _hover, _down;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    public TkButton(string text = "", ButtonKind kind = ButtonKind.Secondary, EventHandler? onClick = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        Kind = kind;
        Font = Theme.Bold(9.5f);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        Cursor = Cursors.Hand;
        Margin = Theme.Pad(0, 0, 8, 0);
        if (onClick is not null) Click += onClick;
    }

    private float S => Theme.Scale;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(text.Width + (int)(30 * S), Math.Max(text.Height + (int)(14 * S), (int)(32 * S)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var shape = Theme.Rounded(r, 8 * S);
        Color fill, text, border;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = _down ? Theme.GoldDeep : _hover ? Theme.GoldLight : Theme.Gold;
                text = Theme.GoldInk;
                border = fill;
                break;
            case ButtonKind.Danger:
                fill = _down ? Theme.Mix(Theme.Bg2, Theme.Coral, 0.35f) : _hover ? Theme.Mix(Theme.Bg2, Theme.Coral, 0.2f) : Theme.Bg2;
                text = Theme.Coral;
                border = Theme.Mix(Theme.Line, Theme.Coral, 0.6f);
                break;
            case ButtonKind.Ghost:
                fill = _down ? Theme.Bg4 : _hover ? Theme.Bg3 : Theme.Backdrop(this);
                text = _hover ? Theme.GoldLight : Theme.TextDim;
                border = fill;
                break;
            default:
                fill = _down ? Theme.Bg4 : _hover ? Theme.Bg4 : Theme.Bg3;
                text = _hover ? Theme.GoldLight : Theme.Text;
                border = _hover ? Theme.Mix(Theme.LineStrong, Theme.Gold, 0.45f) : Theme.LineStrong;
                break;
        }
        if (!Enabled)
        {
            fill = Theme.Bg2;
            text = Theme.TextFaint;
            border = Theme.Line;
        }
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
        using (var pen = new Pen(border, 1f)) g.DrawPath(pen, shape);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Kind == ButtonKind.Primary ? Theme.Text : Theme.Gold, 1.6f) { DashStyle = DashStyle.Dot };
            using var inner = Theme.Rounded(RectangleF.Inflate(r, -3 * S, -3 * S), 6 * S);
            g.DrawPath(ring, inner);
        }
    }
}

/// <summary>An on/off switch with its label.</summary>
internal sealed class TkToggle : CheckBox
{
    private bool _hover;

    public TkToggle(string text = "", bool on = false, Action<bool>? changed = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        Checked = on;
        Font = Theme.Body();
        AutoSize = true;
        Cursor = Cursors.Hand;
        Margin = Theme.Pad(0, 4, 0, 4);
        if (changed is not null) CheckedChanged += (_, _) => changed(Checked);
    }

    private float S => Theme.Scale;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var max = proposedSize.Width > 60 ? proposedSize.Width - (int)(50 * S) : int.MaxValue;
        var text = TextRenderer.MeasureText(Text, Font, new Size(max, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        return new Size(text.Width + (int)(52 * S), Math.Max(text.Height + (int)(6 * S), (int)(24 * S)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = 38 * S, h = 21 * S;
        var track = new RectangleF(1, (Height - h) / 2f, w, h);
        using (var shape = Theme.Rounded(track, h / 2))
        using (var fill = new SolidBrush(!Enabled ? Theme.Bg3 : Checked ? Theme.Gold : _hover ? Theme.Bg4 : Theme.Bg3))
        using (var edge = new Pen(Checked && Enabled ? Theme.Gold : Theme.LineStrong, 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        var d = h - 6 * S;
        var x = Checked ? track.Right - d - 3 * S : track.Left + 3 * S;
        using (var thumb = new SolidBrush(!Enabled ? Theme.TextFaint : Checked ? Theme.GoldInk : Theme.TextDim))
        {
            g.FillEllipse(thumb, x, track.Top + 3 * S, d, d);
        }
        var textRect = new Rectangle((int)(w + 12 * S), 0, Width - (int)(w + 12 * S), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? Theme.Text : Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.GoldLight, 1.4f) { DashStyle = DashStyle.Dot };
            using var outer = Theme.Rounded(RectangleF.Inflate(track, 2.5f * S, 2.5f * S), h / 2 + 2.5f * S);
            g.DrawPath(ring, outer);
        }
    }
}

/// <summary>A horizontal slider with its value written beside it.</summary>
internal sealed class TkSlider : Control
{
    private int _min, _max = 100, _value, _step = 1;
    private bool _drag, _hover;

    public event Action<int>? ValueChanged;
    /// <summary>Raised when the player lets go (for work that should not run on every pixel of a drag).</summary>
    public event Action<int>? Committed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, string> Format { get; set; } = v => v.ToString();

    public TkSlider(int min, int max, int value, int step = 1, Func<int, string>? format = null, Action<int>? changed = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _min = min;
        _max = Math.Max(min + 1, max);
        _step = Math.Max(1, step);
        _value = Math.Clamp(value, _min, _max);
        if (format is not null) Format = format;
        if (changed is not null) ValueChanged += changed;
        TabStop = true;
        Size = Theme.Px(300, 30);
        Font = Theme.Bold(9.5f);
        Cursor = Cursors.Hand;
        Margin = Theme.Pad(0, 2, 0, 6);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp((int)Math.Round((double)(value - _min) / _step) * _step + _min, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(v);
        }
    }

    /// <summary>Sets the value without telling anyone (when settings change elsewhere).</summary>
    public void SetQuietly(int value)
    {
        _value = Math.Clamp(value, _min, _max);
        Invalidate();
    }

    private float S => Theme.Scale;
    private float LabelWidth => 62 * S;
    private float TrackLeft => 9 * S;
    private float TrackRight => Width - LabelWidth - 9 * S;

    private void FromMouse(int x) => Value = _min + (int)Math.Round((x - TrackLeft) / Math.Max(1f, TrackRight - TrackLeft) * (_max - _min));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drag = true;
        FromMouse(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) FromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag) return;
        _drag = false;
        Committed?.Invoke(_value);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var before = _value;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= _step; break;
            case Keys.Right or Keys.Up: Value += _step; break;
            case Keys.PageDown: Value -= _step * 5; break;
            case Keys.PageUp: Value += _step * 5; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
            default: return;
        }
        e.Handled = true;
        if (before != _value) Committed?.Invoke(_value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var mid = Height / 2f;
        var t = (float)(_value - _min) / (_max - _min);
        var x = TrackLeft + (TrackRight - TrackLeft) * t;
        using (var track = new Pen(Theme.Bg4, 4.5f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(track, TrackLeft, mid, TrackRight, mid);
        }
        using (var fill = new Pen(Enabled ? Theme.Gold : Theme.TextFaint, 4.5f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (x > TrackLeft + 0.5f) g.DrawLine(fill, TrackLeft, mid, x, mid);
        }
        var r = (_drag || _hover || Focused ? 8.5f : 7.5f) * S;
        using (var ring = new SolidBrush(Theme.GoldInk)) g.FillEllipse(ring, x - r - 1.5f * S, mid - r - 1.5f * S, (r + 1.5f * S) * 2, (r + 1.5f * S) * 2);
        using (var thumb = new SolidBrush(Enabled ? Theme.GoldLight : Theme.TextFaint)) g.FillEllipse(thumb, x - r, mid - r, r * 2, r * 2);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Text, 1.3f) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(focus, x - r - 4 * S, mid - r - 4 * S, (r + 4 * S) * 2, (r + 4 * S) * 2);
        }
        var label = new Rectangle((int)(Width - LabelWidth), 0, (int)LabelWidth, Height);
        TextRenderer.DrawText(g, Format(_value), Font, label, Enabled ? Theme.Text : Theme.TextFaint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>A row of choices where exactly one is picked (presets, styles).</summary>
internal sealed class TkSegment : Control
{
    private readonly string[] _items;
    private int _selected, _hover = -1;

    public event Action<int>? SelectedChanged;

    public TkSegment(string[] items, int selected = 0, Action<int>? changed = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _items = items;
        _selected = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        if (changed is not null) SelectedChanged += changed;
        TabStop = true;
        Font = Theme.Bold(9.25f);
        Size = Theme.Px(Math.Max(220, items.Length * 110), 34);
        Cursor = Cursors.Hand;
        Margin = Theme.Pad(0, 2, 0, 8);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, _items.Length - 1);
            if (v == _selected) return;
            _selected = v;
            Invalidate();
            SelectedChanged?.Invoke(v);
        }
    }

    public void SetQuietly(int index)
    {
        _selected = Math.Clamp(index, 0, _items.Length - 1);
        Invalidate();
    }

    private float S => Theme.Scale;
    private RectangleF Cell(int i) => new(2 * S + i * ((Width - 4 * S) / _items.Length), 2 * S, (Width - 4 * S) / _items.Length, Height - 4 * S);
    private int At(int x) => Math.Clamp((int)((x - 2 * S) / ((Width - 4 * S) / _items.Length)), 0, _items.Length - 1);

    protected override void OnMouseMove(MouseEventArgs e) { var h = At(e.X); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); SelectedIndex = At(e.X); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex--; e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SelectedIndex++; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var outer = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var shape = Theme.Rounded(outer, 9 * S))
        using (var fill = new SolidBrush(Theme.Bg1))
        using (var edge = new Pen(Focused && ShowFocusCues ? Theme.Gold : Theme.LineStrong, 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        for (var i = 0; i < _items.Length; i++)
        {
            var cell = Cell(i);
            var on = i == _selected;
            if (on || i == _hover)
            {
                using var shape = Theme.Rounded(RectangleF.Inflate(cell, -1.5f * S, -1.5f * S), 7 * S);
                using var fill = new SolidBrush(on ? (Enabled ? Theme.Gold : Theme.TextFaint) : Theme.Bg3);
                g.FillPath(fill, shape);
            }
            TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(cell), on ? Theme.GoldInk : Enabled ? Theme.TextDim : Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>A rounded panel that groups related settings, with an optional heading.</summary>
internal sealed class TkCard : Panel
{
    private string? _title;

    public TkCard(string? title = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg2;
        ForeColor = Theme.Text;
        _title = title;
        Margin = Theme.Pad(0, 0, 0, 14);
        Size = Theme.Px(MinWidth, 60);
    }

    /// <summary>Every card on a page is at least this wide, so their edges line up.</summary>
    public const int MinWidth = 760;
    private bool _arranging;

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is not null) e.Control.SizeChanged += (_, _) => Arrange();
        Arrange();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Arrange();
    }

    /// <summary>Places the content inside the rounded edge (below the heading) and fits the card around it.</summary>
    private void Arrange()
    {
        if (_arranging || Controls.Count == 0) return;
        _arranging = true;
        try
        {
            var s = Theme.Scale;
            var content = Controls[0];
            var left = (int)(20 * s);
            var top = (int)((_title is null ? 16 : 44) * s);
            if (content.Location != new Point(left, top)) content.Location = new Point(left, top);
            var size = new Size(Math.Max((int)(MinWidth * s), content.Width + left * 2), content.Height + top + (int)(14 * s));
            if (Size != size) Size = size;
        }
        finally
        {
            _arranging = false;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Title { get => _title; set { _title = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Theme.Scale;
        using (var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 12 * s))
        using (var fill = new SolidBrush(Theme.Bg2))
        using (var edge = new Pen(Theme.Line, 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        if (_title is null) return;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var brush = new SolidBrush(Theme.Gold);
        g.DrawString(_title.ToUpperInvariant(), Theme.Head(11f), brush, 17 * s, 13 * s);
    }
}

/// <summary>The sections down the left side of the main window.</summary>
internal sealed class TkNav : Control
{
    public sealed record Item(string Key, string Text, bool Header = false);

    private readonly List<Item> _items;
    private string _selected;
    private int _hover = -1;
    private readonly Dictionary<string, bool> _badges = new();

    public event Action<string>? Selected;

    public TkNav(IEnumerable<Item> items, string selected)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _items = items.ToList();
        _selected = selected;
        BackColor = Theme.Bg1;
        TabStop = true;
        Width = Theme.Px(196);
        Dock = DockStyle.Left;
        Font = Theme.Bold(10f);
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedKey
    {
        get => _selected;
        set
        {
            if (_selected == value || _items.All(i => i.Key != value)) return;
            _selected = value;
            Invalidate();
            Selected?.Invoke(value);
        }
    }

    /// <summary>A gold dot next to a section that wants attention.</summary>
    public void SetBadge(string key, bool on)
    {
        if (_badges.GetValueOrDefault(key) == on) return;
        _badges[key] = on;
        Invalidate();
    }

    private float S => Theme.Scale;
    private float Top0 => 92 * S;
    private float Height0(Item i) => (i.Header ? 34 : 38) * S;

    private int At(int y)
    {
        var top = Top0;
        for (var i = 0; i < _items.Count; i++)
        {
            var h = Height0(_items[i]);
            if (y >= top && y < top + h) return _items[i].Header ? -1 : i;
            top += h;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { var h = At(e.Y); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); var i = At(e.Y); if (i >= 0) SelectedKey = _items[i].Key; base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var selectable = _items.Where(i => !i.Header).ToList();
        var index = selectable.FindIndex(i => i.Key == _selected);
        if (e.KeyCode == Keys.Down && index < selectable.Count - 1) { SelectedKey = selectable[index + 1].Key; e.Handled = true; }
        else if (e.KeyCode == Keys.Up && index > 0) { SelectedKey = selectable[index - 1].Key; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // brand block
        Overlay.Compass.Draw(g, 34 * S, 42 * S, 36 * S);
        using (var gold = new SolidBrush(Theme.Gold)) g.DrawString("TOKENIFY", Theme.Head(15f), gold, 58 * S, 22 * S);
        using (var faint = new SolidBrush(Theme.TextFaint)) g.DrawString("CHEST TRACKER", Theme.Head(8.5f), faint, 60 * S, 48 * S);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, 16 * S, 78 * S, Width - 16 * S, 78 * S);

        var top = Top0;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var h = Height0(item);
            if (item.Header)
            {
                using var faint = new SolidBrush(Theme.TextFaint);
                g.DrawString(item.Text.ToUpperInvariant(), Theme.Head(8.5f), faint, 18 * S, top + 14 * S);
            }
            else
            {
                var on = item.Key == _selected;
                var row = new RectangleF(10 * S, top + 2 * S, Width - 20 * S, h - 4 * S);
                if (on || i == _hover)
                {
                    using var shape = Theme.Rounded(row, 8 * S);
                    using var fill = new SolidBrush(on ? Theme.Bg3 : Theme.Bg2);
                    g.FillPath(fill, shape);
                }
                if (on)
                {
                    using var bar = new SolidBrush(Theme.Gold);
                    using var barShape = Theme.Rounded(new RectangleF(10 * S, top + 9 * S, 3.5f * S, h - 18 * S), 2 * S);
                    g.FillPath(bar, barShape);
                }
                TextRenderer.DrawText(g, item.Text, Font, new Rectangle((int)(26 * S), (int)top, Width - (int)(46 * S), (int)h),
                    on ? Theme.GoldLight : i == _hover ? Theme.Text : Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (_badges.GetValueOrDefault(item.Key))
                {
                    using var dot = new SolidBrush(Theme.Gold);
                    g.FillEllipse(dot, Width - 30 * S, top + h / 2 - 4 * S, 8 * S, 8 * S);
                }
                if (on && Focused && ShowFocusCues)
                {
                    using var ring = new Pen(Theme.Gold, 1.2f) { DashStyle = DashStyle.Dot };
                    using var shape = Theme.Rounded(RectangleF.Inflate(row, -2 * S, -2 * S), 6 * S);
                    g.DrawPath(ring, shape);
                }
            }
            top += h;
        }
    }
}

/// <summary>A dark text box with a rounded edge.</summary>
internal sealed class TkTextBox : Panel
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Bg1, ForeColor = Theme.Text, Font = Theme.Body(10f) };

    public TkTextBox(int width = 220, string text = "", string? placeholder = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg1;
        Size = Theme.Px(width, 34);
        Padding = Theme.Pad(10, 8, 10, 6);
        Margin = Theme.Pad(0, 2, 8, 6);
        Box.Dock = DockStyle.Fill;
        Box.Text = text;
        if (placeholder is not null) Box.PlaceholderText = placeholder;
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Click += (_, _) => Box.Focus();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get => Box.Text; set => Box.Text = value; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Theme.Scale;
        using var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8 * s);
        using var fill = new SolidBrush(Theme.Bg1);
        using var edge = new Pen(Box.Focused ? Theme.Gold : Theme.LineStrong, 1f);
        g.FillPath(fill, shape);
        g.DrawPath(edge, shape);
    }
}

/// <summary>A date shown as text. A click opens a small month calendar in the tracker's colours.</summary>
internal sealed class TkDateBox : Control
{
    private DateTime _value = DateTime.Today;
    private ToolStripDropDown? _popup;

    public event Action<DateTime>? ValueChanged;

    public TkDateBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = Theme.Px(176, 34);
        Margin = Theme.Pad(0, 4, 0, 6);
        Font = Theme.Bold(9.75f);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ComboBox;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime Value
    {
        get => _value;
        set
        {
            var v = value.Date;
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(v);
        }
    }

    /// <summary>Sets the date without telling anyone (when settings change elsewhere).</summary>
    public void SetQuietly(DateTime value)
    {
        _value = value.Date;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Enabled && e.Button == MouseButtons.Left) Open();
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Value = _value.AddDays(-1); break;
            case Keys.Right: Value = _value.AddDays(1); break;
            case Keys.Up: Value = _value.AddDays(-7); break;
            case Keys.Down: Value = _value.AddDays(7); break;
            case Keys.Space or Keys.Enter: Open(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Open()
    {
        _popup?.Close();
        var calendar = new TkCalendar(_value);
        var popup = new ToolStripDropDown { Padding = Padding.Empty, Margin = Padding.Empty, BackColor = Theme.Bg2, AutoSize = true, DropShadowEnabled = true };
        popup.Items.Add(new ToolStripControlHost(calendar) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = calendar.Size });
        calendar.Picked += day =>
        {
            Value = day;
            popup.Close();
        };
        popup.Closed += (_, _) =>
        {
            if (_popup == popup) _popup = null;
            if (IsHandleCreated) BeginInvoke(popup.Dispose);
        };
        _popup = popup;
        popup.Show(this, new Point(0, Height + Theme.Px(4)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = Theme.Scale;
        g.Clear(Theme.Backdrop(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 8 * s))
        using (var fill = new SolidBrush(Theme.Bg1))
        using (var edge = new Pen(Focused && Enabled ? Theme.Gold : Theme.LineStrong, 1f))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        TextRenderer.DrawText(g, _value.ToString("MMM d, yyyy"), Font, new Rectangle((int)(12 * s), 0, Width - (int)(40 * s), Height),
            Enabled ? Theme.Text : Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using var chevron = new Pen(Enabled ? Theme.TextDim : Theme.TextFaint, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var cx = Width - 18 * s;
        var cy = Height / 2f;
        g.DrawLines(chevron, new[] { new PointF(cx - 4.5f * s, cy - 2.5f * s), new PointF(cx, cy + 2.5f * s), new PointF(cx + 4.5f * s, cy - 2.5f * s) });
    }
}

/// <summary>One month of days to pick from (the drop-down of <see cref="TkDateBox"/>).</summary>
internal sealed class TkCalendar : Control
{
    private readonly DateTime _selected;
    private DateTime _month;
    private Point _mouse = new(-1, -1);

    public event Action<DateTime>? Picked;

    public TkCalendar(DateTime selected)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _selected = selected.Date;
        _month = new DateTime(selected.Year, selected.Month, 1);
        BackColor = Theme.Bg2;
        Size = new Size((int)(Cell * 7 + Pad * 2), (int)(HeaderHeight + WeekdayHeight + Cell * 6 + Pad));
        Cursor = Cursors.Hand;
    }

    private static float S => Theme.Scale;
    private static float Cell => 34 * S;
    private static float Pad => 10 * S;
    private static float HeaderHeight => 42 * S;
    private static float WeekdayHeight => 22 * S;

    private static DayOfWeek FirstDay => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>The day in the first cell: the start of the week holding the 1st.</summary>
    private DateTime GridStart => _month.AddDays(-(((int)_month.DayOfWeek - (int)FirstDay + 7) % 7));

    private RectangleF CellAt(int index) => new(Pad + index % 7 * Cell, HeaderHeight + WeekdayHeight + index / 7 * Cell, Cell, Cell);
    private RectangleF Previous => new(Pad, 6 * S, 30 * S, 30 * S);
    private RectangleF Next => new(Width - Pad - 30 * S, 6 * S, 30 * S, 30 * S);

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; Invalidate(); base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _mouse = new Point(-1, -1); Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Previous.Contains(e.Location)) { _month = _month.AddMonths(-1); Invalidate(); return; }
        if (Next.Contains(e.Location)) { _month = _month.AddMonths(1); Invalidate(); return; }
        for (var i = 0; i < 42; i++)
        {
            var day = GridStart.AddDays(i);
            if (day.Month == _month.Month && CellAt(i).Contains(e.Location))
            {
                Picked?.Invoke(day);
                return;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var edge = new Pen(Theme.LineStrong)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, _month.ToString("MMMM yyyy"), Theme.Head(11.5f), new Rectangle(0, (int)(6 * S), Width, (int)(30 * S)), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        foreach (var (rect, forward) in new[] { (Previous, false), (Next, true) })
        {
            var hot = rect.Contains(_mouse);
            if (hot)
            {
                using var shape = Theme.Rounded(rect, 7 * S);
                using var fill = new SolidBrush(Theme.Bg4);
                g.FillPath(fill, shape);
            }
            using var arrow = new Pen(hot ? Theme.GoldLight : Theme.TextDim, 1.8f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float cx = rect.Left + rect.Width / 2, cy = rect.Top + rect.Height / 2, d = forward ? 3 * S : -3 * S;
            g.DrawLines(arrow, new[] { new PointF(cx - d, cy - 5 * S), new PointF(cx + d, cy), new PointF(cx - d, cy + 5 * S) });
        }

        var names = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (var i = 0; i < 7; i++)
        {
            var name = names[((int)FirstDay + i) % 7];
            TextRenderer.DrawText(g, name.Length > 2 ? name[..2] : name, Theme.Head(8.5f), Rectangle.Round(new RectangleF(Pad + i * Cell, HeaderHeight, Cell, WeekdayHeight)),
                Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var start = GridStart;
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            if (day.Month != _month.Month) continue;
            var cell = RectangleF.Inflate(CellAt(i), -2.5f * S, -2.5f * S);
            var chosen = day == _selected;
            var hot = CellAt(i).Contains(_mouse);
            if (chosen || hot)
            {
                using var shape = Theme.Rounded(cell, 7 * S);
                using var fill = new SolidBrush(chosen ? Theme.Gold : Theme.Bg4);
                g.FillPath(fill, shape);
            }
            else if (day == DateTime.Today)
            {
                using var shape = Theme.Rounded(cell, 7 * S);
                using var ring = new Pen(Theme.Mix(Theme.LineStrong, Theme.Gold, 0.6f));
                g.DrawPath(ring, shape);
            }
            TextRenderer.DrawText(g, day.Day.ToString(), chosen ? Theme.Bold(9.5f) : Theme.Body(9.5f), Rectangle.Round(CellAt(i)),
                chosen ? Theme.GoldInk : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>
/// A window of the tracker. It is laid out once, in pixels, for one display scale (<see cref="Theme.Scale"/>);
/// Windows' own automatic rescaling is switched off so the two can never disagree. If the window ends up on a
/// display with a different scale, it asks to be built again for that scale.
/// </summary>
internal abstract class TkForm : Form
{
    private readonly float _builtScale = Theme.Scale;
    private float _pendingScale;
    private bool _moving;

    /// <summary>Self-test: windows are shown without taking the keyboard from whatever the player is doing.</summary>
    internal static bool Quiet { get; set; }

    protected TkForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg0;
        ForeColor = Theme.Text;
        Font = Theme.Body();
        Icon = Theme.AppIcon();
    }

    protected override bool ShowWithoutActivation => Quiet;

    /// <summary>Build the window again for another display scale. The default does nothing (short-lived dialogs).</summary>
    protected virtual void RebuildForScale(float scale) { }

    /// <summary>
    /// Sizes the window (given for 100% scaling) and puts it in the middle of a display: the one around
    /// <paramref name="centre"/>, or the one with the mouse pointer. It is never made larger than that display.
    /// </summary>
    protected void Place(int width, int height, Point? centre = null, Size? minimum = null)
    {
        var area = (centre is { } c ? Screen.FromPoint(c) : Screen.FromPoint(Cursor.Position)).WorkingArea;
        var room = new Size(Math.Max(Theme.Px(320), area.Width - Theme.Px(32)), Math.Max(Theme.Px(240), area.Height - Theme.Px(56)));
        if (minimum is { } min) MinimumSize = new Size(Math.Min(Theme.Px(min.Width), room.Width), Math.Min(Theme.Px(min.Height), room.Height));
        ClientSize = new Size(Math.Min(Theme.Px(width), room.Width), Math.Min(Theme.Px(height), room.Height));
        StartPosition = FormStartPosition.Manual;
        var middle = centre ?? new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        Location = new Point(
            Math.Clamp(middle.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(middle.Y - Height / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Opened on a display with another scale than the one it was built for.
        if (!Theme.ScaleFixed && Math.Abs(DeviceDpi / 96f - _builtScale) > 0.01f)
        {
            _pendingScale = DeviceDpi / 96f;
            BeginInvoke(Rebuild);
        }
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        _moving = true;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        _moving = false;
        if (_pendingScale > 0) Rebuild();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;   // no automatic rescaling: the window is built again instead
        base.OnDpiChanged(e);
        if (Theme.ScaleFixed) return;
        _pendingScale = e.DeviceDpiNew / 96f;
        // While it is being dragged, wait until it is dropped.
        if (!_moving && IsHandleCreated) BeginInvoke(Rebuild);
    }

    private void Rebuild()
    {
        var scale = _pendingScale;
        _pendingScale = 0;
        if (scale <= 0 || IsDisposed || Math.Abs(scale - _builtScale) < 0.01f) return;
        RebuildForScale(scale);
    }
}

/// <summary>Layout and text helpers shared by every page.</summary>
internal static class Ui
{
    public static Label Text(string text, int maxWidth = 620, Color? color = null, Font? font = null) => new()
    {
        Text = text,
        Font = font ?? Theme.Body(),
        ForeColor = color ?? Theme.Text,
        BackColor = Color.Transparent,
        AutoSize = true,
        MaximumSize = new Size(Theme.Px(maxWidth), 0),
        Margin = Theme.Pad(0, 0, 0, 6),
        UseMnemonic = false,
    };

    public static Label Dim(string text, int maxWidth = 620) => Text(text, maxWidth, Theme.TextDim, Theme.Small);
    public static Label Faint(string text, int maxWidth = 620) => Text(text, maxWidth, Theme.TextFaint, Theme.Small);
    public static Label Label(string text) => Text(text.ToUpperInvariant(), 620, Theme.TextDim, Theme.Head(9.5f));
    public static Label Heading(string text) => Text(text, 760, Theme.Text, Theme.Head(19f));

    /// <summary>A vertical stack that grows with its content.</summary>
    public static FlowLayoutPanel Stack(params Control[] children)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0), BackColor = Color.Transparent,
        };
        p.Controls.AddRange(children);
        return p;
    }

    public static FlowLayoutPanel Row(params Control[] children)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            // A row never grows wider than a card's inside: its buttons fold onto a second line instead.
            MaximumSize = new Size(Theme.Px(TkCard.MinWidth - 40), 0),
            Margin = Theme.Pad(0, 0, 0, 6), BackColor = Color.Transparent,
        };
        p.Controls.AddRange(children);
        return p;
    }

    public static Control Gap(int height = 8) => new Panel { Height = Theme.Px(height), Width = 1, Margin = new Padding(0), BackColor = Color.Transparent };

    public static TkCard Card(string? title, params Control[] children)
    {
        var card = new TkCard(title);
        var stack = Stack(children);
        stack.BackColor = Theme.Bg2;
        card.Controls.Add(stack);
        foreach (Control c in stack.Controls) Tint(c, Theme.Bg2);
        return card;
    }

    /// <summary>Custom-drawn controls paint their parent's colour behind themselves; plain panels just take it.</summary>
    private static void Tint(Control c, Color back)
    {
        if (c is FlowLayoutPanel or System.Windows.Forms.Label || (c is Panel && c is not TkCard && c is not TkTextBox)) c.BackColor = back;
        foreach (Control child in c.Controls) Tint(child, back);
    }

    public static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    public static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        if (t.TotalMinutes < 1) return "just now";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min ago";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours} h ago";
        return $"{(int)t.TotalDays} d ago";
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Could not open " + url + ": " + ex.Message);
        }
    }

    public static bool Confirm(IWin32Window owner, string title, string text, string yes = "Yes") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
}
