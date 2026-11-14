using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Tokenify.Core;

namespace Tokenify.Overlay;

/// <param name="Editing">Unlocked: show the outline, the hint and the resize grip.</param>
/// <param name="ToastAlpha">0-1: how visible the "+42 CHESTS" line is right now.</param>
/// <param name="HeroOverride">The big number while it is counting up to its new value.</param>
public sealed record RenderState(bool Editing = false, float ToastAlpha = 0f, string? HeroOverride = null);

/// <summary>One drawn overlay: the picture (with transparency), and where the panel and the resize grip are in it.</summary>
public sealed class OverlayFrame(Bitmap bitmap, Rectangle panel, Rectangle grip) : IDisposable
{
    public Bitmap Bitmap { get; } = bitmap;
    public Rectangle Panel { get; } = panel;
    public Rectangle Grip { get; } = grip;
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Draws the overlay into a bitmap with per-pixel transparency. Pure: the same content and settings always give
/// the same picture, which is how it is tested (the test tool renders every preset and style to PNG files).
/// The panel's opacity and the text's are separate: the background can be nearly invisible while the numbers
/// stay crisp, kept readable over a bright game by a thin dark outline.
/// </summary>
public static class OverlayRenderer
{
    private static readonly Color White = Color.FromArgb(0xF4, 0xF1, 0xE6);
    private static readonly Color GreyOnPanel = Color.FromArgb(0xA4, 0xAF, 0xC6);
    /// <summary>Labels drawn straight over the game need to be lighter than on a dark panel.</summary>
    private static readonly Color GreyOnGame = Color.FromArgb(0xE4, 0xE9, 0xF3);
    private static readonly Color Green = Color.FromArgb(0x57, 0xD9, 0x8A);
    private static readonly Color Coral = Color.FromArgb(0xE9, 0x78, 0x5F);

    private static readonly FontFamily Display = Family("Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Impact", "Arial Narrow", "Segoe UI Semibold", "Segoe UI");
    private static readonly FontFamily Label = Family("Bahnschrift SemiBold SemiConden", "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI Semibold", "Segoe UI");
    private static readonly StringFormat Typo = new(StringFormat.GenericTypographic) { FormatFlags = StringFormat.GenericTypographic.FormatFlags | StringFormatFlags.MeasureTrailingSpaces };

    /// <summary>Fonts that ship with Windows 10 and 11 (Bahnschrift), with fallbacks: nothing is bundled.</summary>
    private static FontFamily Family(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // not installed: try the next
            }
        }
        return FontFamily.GenericSansSerif;
    }

    private static FontStyle StyleOf(FontFamily family) => family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular
        : family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Italic;

    private sealed class Run(GraphicsPath path, float width, float ascent, float size) : IDisposable
    {
        public GraphicsPath Path { get; } = path;
        public float Width { get; } = width;
        public float Ascent { get; } = ascent;
        public float Size { get; } = size;
        public void Dispose() => Path.Dispose();
    }

    private static Run Make(Graphics g, string text, FontFamily family, float size, float tracking = 0)
    {
        var style = StyleOf(family);
        var path = new GraphicsPath(FillMode.Winding);
        using var font = new Font(family, size, style, GraphicsUnit.Pixel);
        float width;
        if (tracking <= 0.01f)
        {
            path.AddString(text, family, (int)style, size, PointF.Empty, Typo);
            width = g.MeasureString(text, font, PointF.Empty, Typo).Width;
        }
        else
        {
            var x = 0f;
            foreach (var ch in text)
            {
                var s = ch.ToString();
                if (ch != ' ') path.AddString(s, family, (int)style, size, new PointF(x, 0), Typo);
                x += g.MeasureString(s, font, PointF.Empty, Typo).Width + tracking;
            }
            width = Math.Max(0, x - tracking);
        }
        return new Run(path, width, family.GetCellAscent(style) * size / family.GetEmHeight(style), size);
    }

    /// <param name="U">Pixels per layout unit.</param>
    /// <param name="Outline">Draw a dark edge around the glyphs (used when the panel behind them is faint or absent).</param>
    private sealed record Ink(float U, bool Outline, float Alpha);

    private static void Draw(Graphics g, Run run, float x, float baseline, Color color, Ink ink)
    {
        var state = g.Save();
        g.TranslateTransform(x, baseline - run.Ascent);
        if (ink.Outline)
        {
            // Straight over the game: a dark edge plus a short shadow under it. The edge is thinner for small
            // labels and heavier for the big number, never so thick that letters fill in.
            var width = Math.Clamp(run.Size * 0.13f, 1.7f * ink.U, 3.4f * ink.U);
            using var pen = new Pen(Color.FromArgb((int)(215 * ink.Alpha), 4, 7, 14), width) { LineJoin = LineJoin.Round };
            g.TranslateTransform(0, 0.9f * ink.U);
            using (var under = new Pen(Color.FromArgb((int)(90 * ink.Alpha), 0, 0, 0), width + 1.2f * ink.U) { LineJoin = LineJoin.Round })
            {
                g.DrawPath(under, run.Path);
            }
            g.TranslateTransform(0, -0.9f * ink.U);
            g.DrawPath(pen, run.Path);
        }
        else
        {
            // On a solid enough panel a soft, short shadow is all that is needed.
            var drop = Math.Clamp(run.Size * 0.035f, 0.6f * ink.U, 1.4f * ink.U);
            g.TranslateTransform(0, drop);
            using var shadow = new SolidBrush(Color.FromArgb((int)(105 * ink.Alpha), 0, 0, 0));
            g.FillPath(shadow, run.Path);
            g.TranslateTransform(0, -drop);
        }
        using var brush = new SolidBrush(Color.FromArgb((int)(color.A * ink.Alpha), color));
        g.FillPath(brush, run.Path);
        g.Restore(state);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Accent(OverlaySettings o)
    {
        try
        {
            return ColorTranslator.FromHtml(o.Accent);
        }
        catch (Exception)
        {
            return Color.FromArgb(0xF6, 0xC2, 0x4A);
        }
    }

    private static string Pill(StatusKind kind) => kind switch
    {
        StatusKind.Live => "LIVE",
        StatusKind.Busy => "READING",
        StatusKind.Good => "READY",
        StatusKind.Warn => "CHECK",
        StatusKind.Off => "PAUSED",
        _ => "WAITING",
    };

    private static Color DotColor(StatusKind kind, Color accent) => kind switch
    {
        StatusKind.Live => Green,
        StatusKind.Busy or StatusKind.Warn => accent,
        StatusKind.Good => Color.FromArgb(0x7F, 0xC8, 0xA0),
        _ => Color.FromArgb(0x6C, 0x78, 0x93),
    };

    /// <param name="dpiScale">1.0 at 100% Windows scaling (96 dpi), 1.5 at 150%...</param>
    public static OverlayFrame Render(OverlayContent content, OverlaySettings o, float dpiScale = 1f, RenderState? state = null)
    {
        state ??= new RenderState();
        var u = Math.Max(0.4f, o.Scale / 100f * dpiScale);
        var fs = o.FontScale / 100f;
        var accent = Accent(o);
        var hud = o.Style == "hud";
        var panelAlpha = hud ? 0f : o.BackgroundOpacity / 100f;
        // Over a faint or missing panel the game shows through behind the text: outline it so it stays crisp.
        var ink = new Ink(u, Outline: panelAlpha < 0.40f, Alpha: 1f);
        var Grey = ink.Outline ? GreyOnGame : GreyOnPanel;
        var minimal = o.Preset == "minimal";

        using var scratch = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var measure = Graphics.FromImage(scratch);
        measure.TextRenderingHint = TextRenderingHint.AntiAlias;

        var pad = (minimal ? Math.Max(7, o.Padding - 3) : o.Padding) * u;
        var margin = (o.Shadow && !hud && panelAlpha > 0.05f ? 11f : 4f) * u;
        var toast = state.ToastAlpha > 0.01f && content.Toast is not null ? content.Toast : null;
        var heroText = state.HeroOverride ?? content.Hero.Value;

        var runs = new List<IDisposable>();
        T Keep<T>(T run) where T : IDisposable { runs.Add(run); return run; }

        try
        {
            float width, height;
            Action<Graphics, float, float> paint;

            if (minimal)
            {
                var hero = Keep(Make(measure, heroText, Display, 30f * fs * u));
                var logo = 19f * u;
                var cols = content.Rows.Take(2).Select(r => (Row: r, Label: Keep(Make(measure, r.Label, Label, 9f * fs * u, 1.2f * u)),
                    Value: Keep(Make(measure, r.Value, Display, 16.5f * fs * u)))).ToList();
                var toastRun = toast is null ? null : Keep(Make(measure, toast, Label, 12.5f * fs * u, 1.3f * u));
                var rightWidth = toastRun?.Width ?? (cols.Count == 0 ? 0 : cols.Sum(c => Math.Max(c.Label.Width, c.Value.Width)) + (cols.Count - 1) * 12f * u);
                var lineHeight = 32f * fs * u;
                width = pad + logo + 8f * u + hero.Width + (rightWidth > 0 ? 11f * u + 1f * u + 11f * u + rightWidth : 0) + pad;
                height = pad * 2 + lineHeight;
                paint = (g, x0, y0) =>
                {
                    var mid = y0 + pad + lineHeight / 2;
                    Compass.Draw(g, x0 + pad + logo / 2, mid, logo, accent);
                    var x = x0 + pad + logo + 8f * u;
                    Draw(g, hero, x, mid + 30f * fs * u * 0.36f, White, ink);
                    x += hero.Width + 11f * u;
                    if (rightWidth <= 0) return;
                    using (var divider = new Pen(Color.FromArgb(52, 255, 255, 255), Math.Max(1f, u)))
                    {
                        g.DrawLine(divider, x, mid - 10f * u, x, mid + 10f * u);
                    }
                    x += 12f * u;
                    if (toastRun is not null)
                    {
                        Draw(g, toastRun, x, mid + 12.5f * fs * u * 0.36f, accent, ink with { Alpha = state.ToastAlpha });
                        return;
                    }
                    foreach (var c in cols)
                    {
                        var w = Math.Max(c.Label.Width, c.Value.Width);
                        Draw(g, c.Label, x, mid - 4.5f * fs * u, Grey, ink);
                        Draw(g, c.Value, x, mid + 12.5f * fs * u, c.Row.Id == Metric.Cpg ? accent : c.Row.Dim ? Grey : White, ink);
                        x += w + 12f * u;
                    }
                };
            }
            else
            {
                var brand = Keep(Make(measure, "TOKENIFY", Label, 11.5f * fs * u, 1.9f * u));
                var pill = content.Status is null ? null : Keep(Make(measure, Pill(content.StatusKind), Label, 9.5f * fs * u, 1.3f * u));
                var hero = Keep(Make(measure, heroText, Display, 47f * fs * u));
                var heroLabel = Keep(Make(measure, content.Hero.Label, Label, 10.5f * fs * u, 2.4f * u));
                var rows = content.Rows.Select(r =>
                {
                    var key = r.Id == Metric.Cpg;
                    return (Row: r, Key: key, Label: Keep(Make(measure, r.Label, Label, 10.5f * fs * u, 1.3f * u)),
                        Value: Keep(Make(measure, r.Value, Display, (key ? 21f : 17.5f) * fs * u)));
                }).ToList();
                var footerText = toast ?? (content.Status is not null && content.StatusKind is StatusKind.Live or StatusKind.Busy or StatusKind.Warn or StatusKind.Off ? content.Status : null);
                var footer = footerText is null ? null : Keep(Make(measure, footerText, Label, (toast is null ? 9.5f : 12f) * fs * u, 1.6f * u));
                var hint = !state.Editing ? null : Keep(Make(measure, "DRAG TO MOVE  ·  CORNER TO RESIZE", Label, 8.5f * fs * u, 0.9f * u));

                var logo = 16f * u;
                var headerHeight = 16f * fs * u;
                var headerWidth = logo + 6f * u + brand.Width + (pill is null ? 0 : 16f * u + 9f * u + pill.Width);
                var rowHeight = 21.5f * fs * u;
                var rowsWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Label.Width + 18f * u + r.Value.Width + (r.Row.Trend is not null and not 0 ? 11f * u : 0));
                var inner = new[] { headerWidth, hero.Width, heroLabel.Width, rowsWidth, footer?.Width ?? 0, hint?.Width ?? 0, 150f * u }.Max();
                width = inner + pad * 2;

                var heroBox = 47f * fs * u * 0.80f;
                var heroLabelBox = 13f * fs * u;
                var footerBox = 15f * fs * u;
                height = pad + headerHeight + 8f * u + heroBox + 3f * u + heroLabelBox
                    + (rows.Count > 0 ? 9f * u + 1f * u + 5f * u + rows.Count * rowHeight : 0)
                    + (footer is not null ? 7f * u + footerBox : 0)
                    + (hint is not null ? 6f * u + 10f * fs * u : 0)
                    + pad - 2f * u;

                var center = o.Align == "center";
                paint = (g, x0, y0) =>
                {
                    var left = x0 + pad;
                    var right = x0 + width - pad;
                    var y = y0 + pad;

                    // header: compass + name on the left, status on the right
                    Compass.Draw(g, left + logo / 2, y + headerHeight / 2, logo, accent);
                    Draw(g, brand, left + logo + 6f * u, y + headerHeight / 2 + 11.5f * fs * u * 0.36f, accent, ink);
                    if (pill is not null)
                    {
                        var dot = DotColor(content.StatusKind, accent);
                        var px = right - pill.Width;
                        Draw(g, pill, px, y + headerHeight / 2 + 9.5f * fs * u * 0.36f, Grey, ink);
                        var r = 3.1f * u;
                        var cx = px - 6f * u - r;
                        var cy = y + headerHeight / 2;
                        if (content.StatusKind == StatusKind.Live)
                        {
                            using var glow = new SolidBrush(Color.FromArgb(60, dot));
                            g.FillEllipse(glow, cx - r * 2, cy - r * 2, r * 4, r * 4);
                        }
                        using var ring = new SolidBrush(Color.FromArgb(160, 4, 7, 14));
                        g.FillEllipse(ring, cx - r - u, cy - r - u, (r + u) * 2, (r + u) * 2);
                        using var fill = new SolidBrush(dot);
                        g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
                    }
                    y += headerHeight + 8f * u;

                    // the big number and its label
                    var heroX = center ? x0 + (width - hero.Width) / 2 : left;
                    Draw(g, hero, heroX, y + heroBox - 1f * u, White, ink);
                    y += heroBox + 3f * u;
                    Draw(g, heroLabel, center ? x0 + (width - heroLabel.Width) / 2 : left + 1f * u, y + heroLabelBox - 3f * u, accent, ink);
                    y += heroLabelBox;

                    if (rows.Count > 0)
                    {
                        y += 9f * u;
                        using (var divider = new LinearGradientBrush(new PointF(left, 0), new PointF(right, 0), Color.FromArgb(70, accent), Color.FromArgb(18, 255, 255, 255)))
                        {
                            g.FillRectangle(divider, left, y, right - left, Math.Max(1f, u));
                        }
                        y += 1f * u + 5f * u;
                        foreach (var r in rows)
                        {
                            var baseline = y + rowHeight * 0.74f;
                            Draw(g, r.Label, left, baseline - (r.Key ? 1.5f * u : 0), Grey, ink);
                            var valueX = right - r.Value.Width;
                            Draw(g, r.Value, valueX, baseline + (r.Key ? 1f * u : 0), r.Key ? accent : r.Row.Dim ? Grey : White, ink);
                            if (r.Row.Trend is int trend && trend != 0)
                            {
                                var tx = valueX - 9f * u;
                                var ty = baseline - 6.5f * fs * u;
                                var s = 3.6f * u;
                                var up = trend > 0;
                                using var arrow = new SolidBrush(up ? Green : Coral);
                                g.FillPolygon(arrow, up
                                    ? new[] { new PointF(tx - s, ty + s), new PointF(tx + s, ty + s), new PointF(tx, ty - s) }
                                    : new[] { new PointF(tx - s, ty - s), new PointF(tx + s, ty - s), new PointF(tx, ty + s) });
                            }
                            y += rowHeight;
                        }
                    }

                    if (footer is not null)
                    {
                        y += 7f * u;
                        var isToast = toast is not null;
                        Draw(g, footer, center ? x0 + (width - footer.Width) / 2 : left, y + footerBox - 3.5f * u, isToast ? accent : Grey,
                            ink with { Alpha = isToast ? state.ToastAlpha : 1f });
                        y += footerBox;
                    }

                    if (hint is not null)
                    {
                        y += 6f * u;
                        Draw(g, hint, left, y + 9f * fs * u, Color.FromArgb(210, Grey), ink);
                    }
                };
            }

            var bmpWidth = (int)Math.Ceiling(width + margin * 2);
            var bmpHeight = (int)Math.Ceiling(height + margin * 2);
            var bitmap = new Bitmap(Math.Max(8, bmpWidth), Math.Max(8, bmpHeight), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                var panel = new RectangleF(margin, margin, width, height);
                Panel(g, panel, o, accent, panelAlpha, u, hud);
                if (state.Editing)
                {
                    // Windows only sends clicks to pixels that are not fully transparent. While unlocked, a barely
                    // visible wash makes the whole panel something to grab, even with no background at all.
                    using var grab = new SolidBrush(Color.FromArgb(panelAlpha < 0.12f ? 46 : 10, 14, 20, 36));
                    using var area = Rounded(panel, o.CornerRadius * u);
                    g.FillPath(grab, area);
                }
                paint(g, margin, margin);
                if (state.Editing) EditMarks(g, panel, accent, u, Math.Max(2f, o.CornerRadius) * u);
            }

            var grip = new Rectangle((int)(margin + width - 18f * u), (int)(margin + height - 18f * u), (int)(18f * u + margin), (int)(18f * u + margin));
            return new OverlayFrame(bitmap, Rectangle.Round(new RectangleF(margin, margin, width, height)), grip);
        }
        finally
        {
            foreach (var r in runs) r.Dispose();
        }
    }

    private static void Panel(Graphics g, RectangleF panel, OverlaySettings o, Color accent, float alpha, float u, bool hud)
    {
        if (hud) return;
        var radius = o.CornerRadius * u;
        int A(float a) => (int)Math.Clamp(255 * a, 0, 255);

        if (o.Shadow && alpha > 0.05f)
        {
            // A soft drop shadow built from a few widening outlines (no blur available, none needed at this size).
            for (var i = 6; i >= 1; i--)
            {
                var grow = i * 1.5f * u;
                using var path = Rounded(RectangleF.Inflate(panel, grow, grow) with { Y = panel.Y - grow + 2.2f * u }, radius + grow);
                using var brush = new SolidBrush(Color.FromArgb(A(0.055f * alpha * (7 - i) / 3f), 0, 0, 0));
                g.FillPath(brush, path);
            }
        }

        using var shape = Rounded(panel, radius);
        if (alpha > 0.004f)
        {
            if (o.Style == "solid")
            {
                using var fill = new SolidBrush(Color.FromArgb(A(alpha), 0x0B, 0x12, 0x21));
                g.FillPath(fill, shape);
            }
            else
            {
                // "Frosted": a soft top-lit gradient. Real backdrop blur is not available to a layered window
                // without undocumented system calls, so it is not pretended.
                // One smooth gradient at a constant opacity (varying the opacity instead shows up as bands).
                using var fill = new LinearGradientBrush(new PointF(0, panel.Top - 1), new PointF(0, panel.Bottom + 1),
                    Color.FromArgb(A(alpha), 0x1C, 0x2A, 0x48), Color.FromArgb(A(alpha), 0x07, 0x0C, 0x18));
                g.FillPath(fill, shape);
            }
        }

        if (o.Style == "gold")
        {
            var a = Math.Max(0.38f, Math.Min(1f, alpha * 1.5f));
            using var border = new Pen(Color.FromArgb(A(0.80f * a), accent), Math.Max(1f, 1.4f * u));
            using var inset = Rounded(RectangleF.Inflate(panel, -0.7f * u, -0.7f * u), Math.Max(0, radius - 0.7f * u));
            g.DrawPath(border, inset);
            // corner ticks, as on the website's cards
            using var tick = new Pen(Color.FromArgb(A(a), accent), Math.Max(1.5f, 2.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var t = 9f * u;
            var i2 = 4.5f * u;
            g.DrawLines(tick, new[] { new PointF(panel.Left + i2, panel.Top + i2 + t), new PointF(panel.Left + i2, panel.Top + i2), new PointF(panel.Left + i2 + t, panel.Top + i2) });
            g.DrawLines(tick, new[] { new PointF(panel.Right - i2 - t, panel.Bottom - i2), new PointF(panel.Right - i2, panel.Bottom - i2), new PointF(panel.Right - i2, panel.Bottom - i2 - t) });
        }
        else if (alpha > 0.004f)
        {
            using var border = new Pen(Color.FromArgb(A(0.16f * Math.Min(1f, alpha * 1.4f)), 190, 205, 240), Math.Max(1f, u));
            using var inset = Rounded(RectangleF.Inflate(panel, -0.5f * u, -0.5f * u), Math.Max(0, radius - 0.5f * u));
            g.DrawPath(border, inset);
        }
    }

    private static void EditMarks(Graphics g, RectangleF panel, Color accent, float u, float radius)
    {
        using var outline = new Pen(Color.FromArgb(225, accent), Math.Max(1.2f, 1.5f * u)) { DashStyle = DashStyle.Dash, DashPattern = new[] { 4f, 3f } };
        using var path = Rounded(RectangleF.Inflate(panel, 2.5f * u, 2.5f * u), radius + 2.5f * u);
        g.DrawPath(outline, path);
        using var grip = new Pen(Color.FromArgb(235, accent), Math.Max(1.4f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 1; i <= 3; i++)
        {
            var d = i * 4.2f * u;
            g.DrawLine(grip, panel.Right - 3.5f * u - d, panel.Bottom - 3.5f * u, panel.Right - 3.5f * u, panel.Bottom - 3.5f * u - d);
        }
    }

    /// <summary>The overlay over a stand-in for a bright, busy game scene: for previews in settings and for tests.</summary>
    public static Bitmap Preview(OverlayContent content, OverlaySettings o, float dpiScale = 1f, RenderState? state = null, Size? canvas = null, bool bright = true)
    {
        using var frame = Render(content, o, dpiScale, state);
        var size = canvas ?? new Size(frame.Bitmap.Width + 48, frame.Bitmap.Height + 48);
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var sky = new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height),
                   bright ? Color.FromArgb(0x7F, 0xC4, 0xEE) : Color.FromArgb(0x1B, 0x2A, 0x45), bright ? Color.FromArgb(0xE8, 0xD9, 0xA0) : Color.FromArgb(0x0C, 0x14, 0x24)))
        {
            g.FillRectangle(sky, 0, 0, size.Width, size.Height);
        }
        using (var hill = new SolidBrush(bright ? Color.FromArgb(0x5F, 0xB0, 0x5A) : Color.FromArgb(0x16, 0x2B, 0x2A)))
        {
            g.FillEllipse(hill, -size.Width * 0.3f, size.Height * 0.62f, size.Width * 1.1f, size.Height * 0.9f);
            g.FillEllipse(hill, size.Width * 0.45f, size.Height * 0.72f, size.Width * 0.9f, size.Height * 0.8f);
        }
        using (var sun = new SolidBrush(Color.FromArgb(bright ? 235 : 60, 255, 250, 225)))
        {
            g.FillEllipse(sun, size.Width * 0.68f, size.Height * 0.08f, size.Height * 0.28f, size.Height * 0.28f);
        }
        var opacity = o.OverallOpacity / 100f;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        var x = (size.Width - frame.Bitmap.Width) / 2;
        var y = (size.Height - frame.Bitmap.Height) / 2;
        g.DrawImage(frame.Bitmap, new Rectangle(x, y, frame.Bitmap.Width, frame.Bitmap.Height), 0, 0, frame.Bitmap.Width, frame.Bitmap.Height, GraphicsUnit.Pixel, attributes);
        return bmp;
    }
}
