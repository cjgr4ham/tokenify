using System.Drawing.Drawing2D;
using Tokenify.Core;
using Tokenify.Overlay;

namespace Tokenify.Tracker;

/// <summary>The overlay as it will look, on a stand-in for a bright or a dark game scene (click to switch).</summary>
internal sealed class PreviewBox : Control
{
    private TrackerSnapshot? _snapshot;
    private bool _bright = true;

    public PreviewBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = Theme.Px(TkCard.MinWidth, 310);
        Margin = Theme.Pad(0, 0, 0, 14);
        Cursor = Cursors.Hand;
        Click += (_, _) => { _bright = !_bright; Invalidate(); };
    }

    public void Show(TrackerSnapshot snapshot)
    {
        _snapshot = snapshot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Backdrop(this));
        if (_snapshot is not { } s) return;
        var o = s.Settings.Overlay;
        var dpi = Theme.Scale;
        var content = Display.Build(s, o.Metrics());
        // Draw at the real size when it fits; otherwise smaller, so the whole overlay is always visible here.
        using var probe = OverlayRenderer.Render(content, o, dpi);
        var fit = Math.Min(1f, Math.Min((Width - 40f * dpi) / probe.Bitmap.Width, (Height - 30f * dpi) / probe.Bitmap.Height));
        using var scene = OverlayRenderer.Preview(content, o, dpi * fit, canvas: new Size(Width, Height), bright: _bright);
        using var clip = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 12 * dpi);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.SetClip(clip);
        g.DrawImageUnscaled(scene, 0, 0);
        g.ResetClip();
        using var edge = new Pen(Theme.LineStrong);
        g.DrawPath(edge, clip);
        var note = (fit < 0.999f ? $"Shown at {fit:P0} to fit  ·  " : "") + (_bright ? "Bright scene (click for dark)" : "Dark scene (click for bright)");
        TextRenderer.DrawText(g, note, Theme.Small, new Rectangle(Theme.Px(10), Height - Theme.Px(24), Width - Theme.Px(20), Theme.Px(18)), _bright ? Color.FromArgb(20, 30, 50) : Theme.TextDim,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }
}

internal sealed class AppearancePage : Page
{
    private static readonly string[] Styles = ["frosted", "solid", "hud", "gold"];
    private static readonly string[] Accents = ["#F6C24A", "#FFE58F", "#6FD3FF", "#7BE0A1", "#FF8FB1", "#F4ECD9"];

    private readonly PreviewBox _preview = new();
    private readonly TkSegment _style;
    private readonly TkSlider _background, _overall, _scale, _text, _radius;
    private readonly TkToggle _shadow;
    private readonly FlowLayoutPanel _swatches;

    public AppearancePage(TrackerApp app) : base(app, "Appearance", "How the overlay looks. Changes show at once, here and on the overlay itself.")
    {
        _style = new TkSegment(["Frosted Dark", "Solid Dark", "HUD Only", "Gold Accent"], 0, i => Change(s => s.Overlay.Style = Styles[i])) { Width = Theme.Px(520) };
        _background = new TkSlider(0, 90, 70, 1, v => v + "%", v => Change(s => s.Overlay.BackgroundOpacity = v)) { Width = Theme.Px(520) };
        _overall = new TkSlider(40, 100, 100, 1, v => v + "%", v => Change(s => s.Overlay.OverallOpacity = v)) { Width = Theme.Px(520) };
        _scale = new TkSlider(50, 200, 100, 5, v => v + "%", v => Change(s => s.Overlay.Scale = v)) { Width = Theme.Px(520) };
        _text = new TkSlider(80, 130, 100, 5, v => v + "%", v => Change(s => s.Overlay.FontScale = v)) { Width = Theme.Px(520) };
        _radius = new TkSlider(0, 24, 12, 1, v => v.ToString(), v => Change(s => s.Overlay.CornerRadius = v)) { Width = Theme.Px(520) };
        _shadow = new TkToggle("Soft shadow around the panel", true, on => Change(s => s.Overlay.Shadow = on));

        _swatches = Ui.Row();
        foreach (var hex in Accents) _swatches.Controls.Add(new Swatch(hex, c => Change(s => s.Overlay.Accent = c)));
        _swatches.Controls.Add(new TkButton("Other…", ButtonKind.Ghost, (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = OverlayRenderer.Accent(Snapshot!.Settings.Overlay) };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) Change(s => s.Overlay.Accent = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }));

        Add(_preview,
            Ui.Card("Background", Ui.Label("Style"), _style,
                Ui.Label("Background opacity"), _background,
                Ui.Faint("Only the panel behind the numbers. At 0% there is no panel at all; the numbers stay solid and get a thin dark edge so they remain readable.", 520),
                Ui.Gap(4), Ui.Label("Corner radius"), _radius, _shadow),
            Ui.Card("Size and colour", Ui.Label("Overlay size"), _scale,
                Ui.Faint("You can also unlock the overlay and drag its bottom-right corner.", 520),
                Ui.Label("Text size"), _text, Ui.Label("Accent colour"), _swatches,
                Ui.Gap(4), Ui.Label("Whole overlay opacity"), _overall,
                Ui.Faint("Fades everything, numbers included. Leave at 100% unless you want the whole overlay see-through.", 520)));
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var o = s.Settings.Overlay;
        _preview.Show(s);
        _style.SetQuietly(Math.Max(0, Array.IndexOf(Styles, o.Style)));
        _background.SetQuietly(o.BackgroundOpacity);
        _background.Enabled = o.Style != "hud";
        _overall.SetQuietly(o.OverallOpacity);
        _scale.SetQuietly(o.Scale);
        _text.SetQuietly(o.FontScale);
        _radius.SetQuietly(o.CornerRadius);
        _radius.Enabled = o.Style != "hud";
        if (_shadow.Checked != o.Shadow) _shadow.Checked = o.Shadow;
        foreach (var sw in _swatches.Controls.OfType<Swatch>()) sw.Selected = sw.Hex.Equals(o.Accent, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Swatch : Control
    {
        private bool _selected;
        public string Hex { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

        public Swatch(string hex, Action<string> pick)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Hex = hex;
            Size = Theme.Px(34, 34);
            Margin = Theme.Pad(0, 0, 8, 0);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = "Accent colour " + hex;
            Click += (_, _) => pick(hex);
            KeyDown += (_, e) => { if (e.KeyCode is Keys.Space or Keys.Enter) pick(hex); };
            GotFocus += (_, _) => Invalidate();
            LostFocus += (_, _) => Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Backdrop(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(ColorTranslator.FromHtml(Hex));
            var inset = 5 * Theme.Scale;
            g.FillEllipse(fill, inset, inset, Width - inset * 2, Height - inset * 2);
            if (_selected || Focused)
            {
                using var ring = new Pen(_selected ? Theme.Text : Theme.TextDim, 2f * Theme.Scale);
                var edge = 1.5f * Theme.Scale;
                g.DrawEllipse(ring, edge, edge, Width - edge * 2, Height - edge * 2);
            }
        }
    }
}

/// <summary>Which metrics the Custom layout shows, and in what order.</summary>
internal sealed class MetricPicker : ListBox
{
    private List<string> _chosen = new();
    public event Action<List<string>>? Changed;

    public MetricPicker()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Bg1;
        ForeColor = Theme.Text;
        IntegralHeight = false;
        Size = Theme.Px(520, 300);
        Margin = Theme.Pad(0, 2, 0, 8);
    }

    private float S => Theme.Scale;

    public void Show(IReadOnlyList<string> chosen, bool enabled)
    {
        var selectedId = SelectedItem as string;
        _chosen = chosen.Where(id => id != Metric.Total).ToList();
        Enabled = enabled;
        ItemHeight = (int)(40 * S);
        BeginUpdate();
        Items.Clear();
        foreach (var id in _chosen) Items.Add(id);
        foreach (var m in Metric.All.Where(m => m.Id != Metric.Total && !_chosen.Contains(m.Id))) Items.Add(m.Id);
        if (selectedId is not null) SelectedItem = selectedId;
        EndUpdate();
    }

    private void Toggle(string id)
    {
        if (!_chosen.Remove(id)) _chosen.Add(id);
        Changed?.Invoke(new List<string>(_chosen));
    }

    public void MoveSelected(int direction)
    {
        if (SelectedItem is not string id) return;
        var i = _chosen.IndexOf(id);
        var j = i + direction;
        if (i < 0 || j < 0 || j >= _chosen.Count) return;
        (_chosen[i], _chosen[j]) = (_chosen[j], _chosen[i]);
        Changed?.Invoke(new List<string>(_chosen));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var i = IndexFromPoint(e.Location);
        if (i >= 0 && e.X < 44 * S && Items[i] is string id) Toggle(id);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && SelectedItem is string id)
        {
            Toggle(id);
            e.Handled = true;
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not string id) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var on = _chosen.Contains(id);
        using (var back = new SolidBrush(selected ? Theme.Bg3 : Theme.Bg1)) g.FillRectangle(back, r);
        var box = new RectangleF(r.Left + 12 * S, r.Top + (r.Height - 18 * S) / 2, 18 * S, 18 * S);
        using (var shape = Theme.Rounded(box, 4 * S))
        using (var fill = new SolidBrush(on ? (Enabled ? Theme.Gold : Theme.TextFaint) : Theme.Bg3))
        using (var edge = new Pen(on ? Theme.Gold : Theme.LineStrong))
        {
            g.FillPath(fill, shape);
            g.DrawPath(edge, shape);
        }
        if (on)
        {
            using var tick = new Pen(Theme.GoldInk, 2f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, new[] { new PointF(box.Left + 4 * S, box.Top + 9.5f * S), new PointF(box.Left + 7.5f * S, box.Top + 13 * S), new PointF(box.Left + 14 * S, box.Top + 5.5f * S) });
        }
        var info = Metric.All.First(m => m.Id == id);
        var left = r.Left + (int)(42 * S);
        TextRenderer.DrawText(g, $"{info.Label}", Theme.Bold(9.75f), new Rectangle(left, r.Top + (int)(4 * S), (int)(190 * S), (int)(18 * S)),
            on && Enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, info.Group.ToUpperInvariant(), Theme.Head(8f), new Rectangle(left, r.Top + (int)(22 * S), (int)(190 * S), (int)(14 * S)),
            Theme.TextFaint, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, info.Hint, Theme.Small, new Rectangle(left + (int)(196 * S), r.Top, r.Width - (int)(246 * S), r.Height),
            Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

internal sealed class LayoutPage : Page
{
    private static readonly string[] Presets = ["minimal", "standard", "expanded", "custom"];

    private readonly PreviewBox _preview = new();
    private readonly TkSegment _preset, _align;
    private readonly Label _presetNote = Ui.Dim("", 520);
    private readonly MetricPicker _metrics = new();
    private readonly TkSlider _padding;
    private readonly FlowLayoutPanel _order;

    public LayoutPage(TrackerApp app) : base(app, "Layout", "What the overlay shows. The total is always the big number; CPG is the next most prominent.")
    {
        _preset = new TkSegment(["Minimal", "Standard", "Expanded", "Custom"], 1, i => Change(s =>
        {
            // Starting a custom layout from the preset you were looking at keeps what you already see.
            if (Presets[i] == "custom" && s.Overlay.Preset != "custom") s.Overlay.CustomMetrics = [.. s.Overlay.Metrics()];
            s.Overlay.Preset = Presets[i];
        })) { Width = Theme.Px(520) };
        _metrics.Changed += chosen => Change(s => s.Overlay.CustomMetrics = [Metric.Total, .. chosen]);
        _order = Ui.Row(new TkButton("Move up", ButtonKind.Secondary, (_, _) => _metrics.MoveSelected(-1)), new TkButton("Move down", ButtonKind.Secondary, (_, _) => _metrics.MoveSelected(1)));
        _padding = new TkSlider(6, 24, 12, 1, v => v.ToString(), v => Change(s => s.Overlay.Padding = v)) { Width = Theme.Px(520) };
        _align = new TkSegment(["Total on the left", "Total centred"], 0, i => Change(s => s.Overlay.Align = i == 1 ? "center" : "left")) { Width = Theme.Px(520) };

        Add(_preview,
            Ui.Card("Preset", _preset, _presetNote),
            Ui.Card("Metrics", Ui.Dim("Tick what to show. Select a ticked row and use the buttons to change the order.", 520), _metrics, _order,
                Ui.Faint("Pirate Token figures are theoretical (1 in 137,500 per chest, each chest independent). They assume every counted chest can drop the token; the replay does not say which chest types can.", 520)),
            Ui.Card("Spacing", Ui.Label("Padding"), _padding, Ui.Label("Alignment"), _align));
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var o = s.Settings.Overlay;
        _preview.Show(s);
        _preset.SetQuietly(Math.Max(0, Array.IndexOf(Presets, o.Preset)));
        _presetNote.Text = o.Preset switch
        {
            "minimal" => "Minimal: the compass, your total and average CPG on one line. Almost nothing in the way.",
            "expanded" => "Expanded: total, last game, all-time and recent CPG, this session, best game and the match status.",
            "custom" => "Custom: your own choice below.",
            _ => "Standard (recommended): total, last game, average CPG, last 10 CPG and a small match status.",
        };
        var custom = o.Preset == "custom";
        _metrics.Show(o.Metrics(), custom);
        _order.Enabled = custom;
        _padding.SetQuietly(o.Padding);
        _align.SetQuietly(o.Align == "center" ? 1 : 0);
        _align.Enabled = o.Preset != "minimal";
    }
}

internal sealed class PositionPage : Page
{
    private readonly TkToggle _enabled, _locked, _snap;
    private readonly TkSegment _when;
    private readonly FlowLayoutPanel _monitors = Ui.Row();
    private readonly Label _where = Ui.Dim("", 620);
    private string _monitorSignature = "";

    public PositionPage(TrackerApp app) : base(app, "Position", "Where the overlay sits, and whether it can be moved.")
    {
        _enabled = new TkToggle("Show the overlay on top of the game", true, on => Change(s => { s.Overlay.Enabled = on; if (on) s.Overlay.Visible = true; }));
        _when = new TkSegment(["Always", "Only while Fortnite is in front"], 0, i => Change(s => s.Overlay.ShowWhen = i == 1 ? "fortnite" : "always")) { Width = Theme.Px(520) };
        _locked = new TkToggle("Locked: clicks go through it to the game", true, on => Change(s => s.Overlay.Locked = on));
        _snap = new TkToggle("Snap to screen edges and centre while dragging", true, on => Change(s => s.Overlay.SnapToEdges = on));

        var corners = Ui.Row(
            new TkButton("Top left", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("top-left")),
            new TkButton("Top right", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("top-right")),
            new TkButton("Middle left", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("left")),
            new TkButton("Middle right", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("right")),
            new TkButton("Bottom left", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("bottom-left")),
            new TkButton("Bottom right", ButtonKind.Secondary, (_, _) => App.Overlay.MoveTo("bottom-right")));

        Add(Ui.Card("Overlay", _enabled, Ui.Label("Show it"), _when,
                Ui.Faint("\"Only while Fortnite is in front\" hides it when you switch to another window. It is always shown while it is unlocked or while these settings are open.", 620)),
            Ui.Card("Move it", _locked,
                Ui.Dim("Unlock it, drag it where you want with the left mouse button, drag its bottom-right corner to resize, then lock it again. "
                       + "The shortcut does the same from inside the game.", 620),
                _where, Ui.Label("Or put it in a corner"), corners,
                Ui.Row(new TkButton("Reset position", ButtonKind.Ghost, (_, _) => Change(s => { s.Overlay.Places.Clear(); s.Overlay.Monitor = null; }))), _snap),
            Ui.Card("Display", Ui.Dim("The overlay remembers its place on each display. If its display is unplugged it moves to your main one, and goes back when the display returns.", 620), _monitors),
            Ui.Card("If you can't see it over Fortnite",
                Ui.Text("Set Fortnite to Windowed Fullscreen: Settings → Video → Window Mode → Windowed Fullscreen.", 620),
                Ui.Dim("In exclusive Fullscreen, Windows lets nothing draw on top of a game, and the tracker does not try to force it. Everything else still works: matches are counted, and the numbers are here, in the tray tooltip and on the OBS page.", 620)));
    }

    protected override void Apply(TrackerSnapshot s)
    {
        var o = s.Settings.Overlay;
        if (_enabled.Checked != o.Enabled) _enabled.Checked = o.Enabled;
        if (_locked.Checked != o.Locked) _locked.Checked = o.Locked;
        if (_snap.Checked != o.SnapToEdges) _snap.Checked = o.SnapToEdges;
        _when.SetQuietly(o.ShowWhen == "fortnite" ? 1 : 0);
        _when.Enabled = _locked.Enabled = o.Enabled;
        _where.Text = !o.Enabled ? "The overlay is off."
            : o.Locked ? $"Locked. Unlock with {s.Settings.Shortcuts.ToggleLock}, the switch above, or the tray menu."
            : $"Unlocked: it shows a dashed outline and can be dragged. Lock it with {s.Settings.Shortcuts.ToggleLock} when you are done.";

        var choices = OverlayController.MonitorChoices();
        var signature = string.Join("|", choices.Select(c => c.Device + c.Label)) + "|" + o.Monitor;
        if (signature == _monitorSignature) return;
        _monitorSignature = signature;
        _monitors.Controls.Clear();
        var current = o.Monitor is not null && choices.Any(c => c.Device == o.Monitor) ? o.Monitor : null;
        foreach (var (device, label) in choices)
        {
            var isCurrent = current == device || (current is null && label.Contains("(main)", StringComparison.Ordinal));
            _monitors.Controls.Add(new TkButton(label, isCurrent ? ButtonKind.Primary : ButtonKind.Secondary, (_, _) => App.Overlay.MoveToMonitor(device)));
        }
    }
}
